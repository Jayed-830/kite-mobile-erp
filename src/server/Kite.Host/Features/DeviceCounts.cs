using System.Text.Json;
using Kite.Devices;
using Kite.Host.Infrastructure;
using Kite.Persistence;
using Kite.Platform;

namespace Kite.Host.Features;

public sealed record DeviceCountRequest(Guid BranchId, Guid? VariantId = null, string? Note = null);
public sealed record DeviceCountScanRequest(string Code, string? Entry = null);
public sealed record DeviceCountPostRequest(bool WriteOffMissing = false, string? Reason = null);

/// <summary>
/// Counting devices by IMEI: every device in the branch (or of one model) is scanned; the count shows the devices found, the
/// ones missing and the extra ones scanned (another model, at another branch, already sold, unknown). Posting keeps the result
/// and, if asked, writes off the missing devices that can be (in stock or set aside for parts) at their cost.
/// </summary>
public static class DeviceCounts
{
    private const string ExpectedSql = $"""
        SELECT u.id, {ItemPricing.DescriptionEn} AS "descriptionEn", {ItemPricing.DescriptionAr} AS "descriptionAr", u.status, u.held_from_status AS "heldFromStatus",
               u.condition, u.accumulated_cost AS "cost",
               (SELECT r.value FROM device.imei_registry r WHERE r.device_unit_id = u.id AND r.status = 'active' ORDER BY r.kind, r.slot LIMIT 1) AS identifier
          FROM device.device_unit u JOIN catalog.variant v ON v.id = u.variant_id JOIN catalog.product p ON p.id = v.product_id
         WHERE u.branch_id = @BranchId AND (@VariantId::uuid IS NULL OR u.variant_id = @VariantId)
           AND device.in_stock_status(u.status, u.held_from_status)
         ORDER BY 2, u.received_at
        """;

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/devices/counts");

        g.MapPost("", async (DeviceCountRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            http.User.Demand(Permissions.DeviceCount, req.BranchId);
            var result = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var biz = await BusinessInfo.LoadAsync(t);
                var date = BusinessDay.Today(biz.TimeZone, t.Clock);
                var number = await DocNumbers.NextAsync(t, biz.Id, req.BranchId, "device_count", await FiscalPeriods.FiscalYearOfAsync(t, biz.Id, date), "DC");
                var id = t.NewId();
                await t.ExecuteAsync("""
                    INSERT INTO device.unit_count (id, business_id, branch_id, number, variant_id, note, started_by, origin_server_id)
                    VALUES (@id, @b, @BranchId, @number, @VariantId, @Note, @uid, (SELECT id FROM core.server WHERE is_self))
                    """, new { id, b = biz.Id, req.BranchId, number, req.VariantId, req.Note, uid = http.User.UserIdOf() });
                await t.AuditAsync(new AuditEntry("device.count_started", "device_count", id, After: Json.Obj(new { number, req.VariantId }), BranchId: req.BranchId));
                return new { id, number };
            }, ct);
            return Results.Created($"/api/v1/devices/counts/{result.id}", result);
        }).RequirePermission(Permissions.DeviceCount).RequireModule("device");

        g.MapGet("", async (Guid? branchId, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.RowsAsync($"""
                SELECT c.id, c.number, c.status, b.code AS "branchCode", c.branch_id AS "branchId", c.variant_id AS "variantId",
                       CASE WHEN c.variant_id IS NULL THEN NULL ELSE (SELECT {ItemPricing.DescriptionEn} FROM catalog.variant v JOIN catalog.product p ON p.id = v.product_id WHERE v.id = c.variant_id) END AS "variantDescription",
                       c.started_at AS "startedAt", us.display_name_en AS "startedBy", c.posted_at AS "postedAt",
                       (SELECT count(*) FROM device.unit_count_scan s WHERE s.count_id = c.id) AS "scans", c.result::text AS result
                  FROM device.unit_count c JOIN core.branch b ON b.id = c.branch_id JOIN iam."user" us ON us.id = c.started_by
                 WHERE (@branchId::uuid IS NULL OR c.branch_id = @branchId) ORDER BY c.status = 'open' DESC, c.started_at DESC LIMIT 200
                """, new { branchId }), ct)))
            .RequirePermission(Permissions.DeviceCount).RequireModule("device");

        g.MapGet("/{id:guid}", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => DetailAsync(t, id), ct)))
            .RequirePermission(Permissions.DeviceCount).RequireModule("device");

        g.MapPost("/{id:guid}/scans", async (Guid id, DeviceCountScanRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            var entry = req.Entry ?? "scan";
            if (entry is not ("scan" or "manual")) throw new KiteException(ErrorCodes.Validation, "Entry is scan or manual.", 400, "entry");
            var code = DeviceUnits.Classify(req.Code);
            var scan = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var c = await OpenCountAsync(t, id);
                http.User.Demand(Permissions.DeviceCount, c.BranchId);
                if (entry == "manual") http.User.Demand(Permissions.DeviceImeiManualEntry, c.BranchId);
                var unit = await t.WithAllBranchesAsync(() => DeviceUnits.FindAsync(t, code));
                var (result, message) = unit is null ? ("unknown", "Not a device of this business: receive it as found stock if it is yours.")
                    : unit.BranchId != c.BranchId && DeviceUnits.InStockStatuses.Contains(unit.HeldFromStatus ?? unit.Status) ? ("elsewhere", $"Belongs to {unit.BranchCode} ({unit.DescriptionEn}).")
                    : !DeviceUnits.InStockStatuses.Contains(unit.HeldFromStatus ?? unit.Status) || unit.BranchId != c.BranchId ? ("not_in_stock", $"{unit.DescriptionEn} is {unit.Status.Replace('_', ' ')} in the books.")
                    : c.VariantId is { } vid && unit.VariantId != vid ? ("other_model", $"A {unit.DescriptionEn}, not the model being counted.")
                    : ("here", unit.DescriptionEn);
                var scanId = t.NewId();
                await t.ExecuteAsync("""
                    INSERT INTO device.unit_count_scan (id, count_id, business_id, branch_id, kind, value, entry, device_unit_id, result, scanned_by)
                    VALUES (@scanId, @id, @BusinessId, @BranchId, @kind, @Value, @entry, @unitId, @result, @uid)
                    """, new { scanId, id, c.BusinessId, c.BranchId, kind = code.Kind, code.Value, entry, unitId = unit?.Id, result, uid = http.User.UserIdOf() });
                if (entry == "manual")
                    await t.AuditAsync(new AuditEntry("device.identifier_typed", "device_count", id, After: Json.Obj(new { code.Value }), BranchId: c.BranchId));
                return new { id = scanId, kind = code.Kind, value = code.Value, result, message, unitId = unit?.Id };
            }, ct);
            return Results.Ok(scan);
        }).RequirePermission(Permissions.DeviceCount).RequireModule("device");

        g.MapDelete("/{id:guid}/scans/{scanId:guid}", async (Guid id, Guid scanId, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var c = await OpenCountAsync(t, id);
                http.User.Demand(Permissions.DeviceCount, c.BranchId);
                await t.ExecuteAsync("DELETE FROM device.unit_count_scan WHERE id = @scanId AND count_id = @id", new { scanId, id });
            }, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.DeviceCount).RequireModule("device");

        g.MapPost("/{id:guid}/post", async (Guid id, DeviceCountPostRequest req, HttpContext http, KiteDb db, LicenceService licence, CancellationToken ct) =>
        {
            if (req.WriteOffMissing)
            {
                if (string.IsNullOrWhiteSpace(req.Reason)) throw new KiteException(ErrorCodes.Validation, "Give the reason for writing off the missing devices.", 400, "reason");
                await licence.RequireTransactAsync(ct);
            }
            var result = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var c = await OpenCountAsync(t, id);
                http.User.Demand(Permissions.DeviceCount, c.BranchId);
                if (req.WriteOffMissing) http.User.Demand(Permissions.DeviceWriteOff, c.BranchId);
                await t.ExecuteAsync("SELECT 1 FROM device.unit_count WHERE id = @id FOR UPDATE", new { id });
                var detail = await DetailAsync(t, id);
                var writtenOff = new List<object>();
                var notWrittenOff = new List<object>();
                if (req.WriteOffMissing)
                    foreach (var m in detail.Missing)
                    {
                        var unitId = (Guid)m["id"]!;
                        var u = await DeviceUnits.LoadAsync(t, unitId);
                        if (u.Status is "in_stock" or "for_parts")
                        {
                            var w = await DeviceEndpoints.WriteOffAsync(t, u, $"Missing at count {c.Number}: {req.Reason!.Trim()}", "lost", http.User.UserIdOf());
                            writtenOff.Add(new { unitId, u.Identifier, w.Number, w.Cost });
                        }
                        else notWrittenOff.Add(new { unitId, u.Identifier, u.Status });
                    }
                var summary = new
                {
                    expected = detail.Expected.Count, found = detail.Found, missing = detail.Missing, extra = detail.Extra, writtenOff, notWrittenOff,
                };
                await t.ExecuteAsync("""
                    UPDATE device.unit_count SET status = 'posted', posted_at = now(), posted_by = @uid, result = @result::jsonb WHERE id = @id
                    """, new { id, uid = http.User.UserIdOf(), result = JsonSerializer.Serialize(summary, Json.Web) });
                await t.AuditAsync(new AuditEntry("device.count_posted", "device_count", id,
                    After: Json.Obj(new { c.Number, summary.expected, summary.found, missing = detail.Missing.Count, extra = detail.Extra.Count, writtenOff = writtenOff.Count }), BranchId: c.BranchId));
                return summary;
            }, ct);
            return Results.Ok(result);
        }).RequirePermission(Permissions.DeviceCount).RequireModule("device");

        g.MapPost("/{id:guid}/cancel", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var c = await OpenCountAsync(t, id);
                http.User.Demand(Permissions.DeviceCount, c.BranchId);
                await t.ExecuteAsync("UPDATE device.unit_count SET status = 'cancelled' WHERE id = @id", new { id });
                await t.AuditAsync(new AuditEntry("device.count_cancelled", "device_count", id, BranchId: c.BranchId));
            }, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.DeviceCount).RequireModule("device");
    }

    private sealed record CountRow(Guid Id, Guid BusinessId, Guid BranchId, string Number, Guid? VariantId, string Status);

    private static async Task<CountRow> OpenCountAsync(DbTx t, Guid id)
    {
        var c = await t.QuerySingleOrDefaultAsync<CountRow>("""
            SELECT id, business_id AS BusinessId, branch_id AS BranchId, number, variant_id AS VariantId, status FROM device.unit_count WHERE id = @id
            """, new { id }) ?? throw new KiteException(ErrorCodes.NotFound, "That count is not in your branches.", 404);
        if (c.Status != "open") throw new KiteException(ErrorCodes.DeviceCountState, $"Count {c.Number} is {c.Status}.", 409);
        return c;
    }

    public sealed record CountDetail(object Count, IReadOnlyList<Dictionary<string, object?>> Expected, IReadOnlyList<Dictionary<string, object?>> Scans,
        int Found, IReadOnlyList<Dictionary<string, object?>> Missing, IReadOnlyList<Dictionary<string, object?>> Extra);

    private static async Task<CountDetail> DetailAsync(DbTx t, Guid id)
    {
        var c = await t.QuerySingleOrDefaultAsync<(Guid Id, string Number, string Status, Guid BranchId, string BranchCode, Guid? VariantId, DateTime StartedAt, DateTime? PostedAt, string? Result, string? Note)>("""
            SELECT c.id, c.number, c.status, c.branch_id, b.code, c.variant_id, c.started_at, c.posted_at, c.result::text, c.note
              FROM device.unit_count c JOIN core.branch b ON b.id = c.branch_id WHERE c.id = @id
            """, new { id });
        if (c.Number is null) throw new KiteException(ErrorCodes.NotFound, "That count is not in your branches.", 404);
        var expected = await t.RowsAsync(ExpectedSql, new { c.BranchId, c.VariantId });
        var scans = await t.RowsAsync($"""
            SELECT s.id, s.kind, s.value, s.entry, s.result, s.device_unit_id AS "unitId", s.scanned_at AS "scannedAt", us.display_name_en AS "scannedBy",
                   (SELECT {ItemPricing.DescriptionEn} FROM device.device_unit u JOIN catalog.variant v ON v.id = u.variant_id JOIN catalog.product p ON p.id = v.product_id WHERE u.id = s.device_unit_id) AS "descriptionEn"
              FROM device.unit_count_scan s JOIN iam."user" us ON us.id = s.scanned_by WHERE s.count_id = @id ORDER BY s.scanned_at DESC
            """, new { id });
        var found = scans.Where(s => (string)s["result"]! == "here").Select(s => (Guid)s["unitId"]!).ToHashSet();
        var missing = expected.Where(e => !found.Contains((Guid)e["id"]!)).ToList();
        var extra = scans.Where(s => (string)s["result"]! != "here").ToList();
        var count = new { c.Id, c.Number, c.Status, c.BranchId, c.BranchCode, c.VariantId, c.StartedAt, c.PostedAt, c.Note,
                          result = c.Result is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(c.Result) };
        return new CountDetail(count, expected, scans, expected.Count(e => found.Contains((Guid)e["id"]!)), missing, extra);
    }
}
