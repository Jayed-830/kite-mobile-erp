using System.Globalization;
using Kite.Accounting;
using Kite.Devices;
using Kite.Host.Infrastructure;
using Kite.Persistence;
using Kite.Platform;

namespace Kite.Host.Features;

public sealed record CodesCheckRequest(List<string> Codes, Guid? VariantId = null, Guid? ExceptUnitId = null);
public sealed record IdentifierCorrection(string? Imei1, string? Imei2, string? Serial, string Reason, string? Entry = null);
public sealed record BlockedRequest(string Value, string Reason, string? Reference = null);
public sealed record TacImportRequest(string Csv);
public sealed record SerialFormatRequest(string? Pattern);

/// <summary>
/// IMEI and serial number management (blueprint 11): checks while scanning, correcting a mistyped identifier, reversing a
/// receipt made in error, IMEI conflicts, the blocked-IMEI list, model hints from the IMEI (TAC), serial formats per brand,
/// the business-wide look-up, and where the server stands with head office.
/// </summary>
public static class DeviceIdentity
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/devices");

        // ------------------------------------------------------------------ checks while scanning (11.2, 11.5)
        g.MapPost("/identifiers/check", async (CodesCheckRequest req, HttpContext http, KiteDb db, CentralClient central, CancellationToken ct) =>
        {
            if (req.Codes is null || req.Codes.Count is 0 or > 200) throw new KiteException(ErrorCodes.Validation, "Send 1 to 200 codes.", 400, "codes");
            var (results, ids) = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var item = req.VariantId is { } vid ? await t.QuerySingleOrDefaultAsync<(string Description, string? Brand, string? Pattern, Guid ProductId, Guid BrandKey)>($"""
                    SELECT {ItemPricing.DescriptionEn}, b.name_en, b.serial_pattern, p.id, coalesce(p.brand_id, p.id)
                      FROM catalog.variant v JOIN catalog.product p ON p.id = v.product_id LEFT JOIN catalog.brand b ON b.id = p.brand_id WHERE v.id = @vid
                    """, new { vid }) : default;
                var list = new List<Dictionary<string, object?>>();
                var ids = new List<PushIdentifier>();
                foreach (var raw in req.Codes)
                {
                    var c = DeviceMath.Classify(raw);
                    var r = new Dictionary<string, object?> { ["code"] = raw, ["kind"] = c.Kind, ["value"] = c.Value, ["problem"] = null, ["hint"] = null };
                    list.Add(r);
                    if (c.Kind == "invalid_imei") { r["problem"] = Problem(ErrorCodes.ImeiInvalid, c.Value.Length == 15 ? $"{c.Value} is not a valid IMEI: the check digit is wrong." : $"{c.Value} is not an IMEI: an IMEI has 15 digits."); continue; }
                    if (c.Kind == "unknown") { r["problem"] = Problem(ErrorCodes.DeviceNotFound, "Scan an IMEI (15 digits) or a serial number."); continue; }
                    var label = c.Kind == "serial" ? "Serial number" : "IMEI";
                    if (await CentralRegistry.BlockedReasonAsync(t, c.Kind, c.Value) is { } why) { r["problem"] = Problem(ErrorCodes.ImeiBlocked, $"{label} {c.Value} is on the blocked list: {why}."); continue; }
                    if (c.Kind == "serial" && item.Pattern is { } pat && !SerialFormats.Matches(pat, c.Value))
                    { r["problem"] = Problem(ErrorCodes.SerialFormat, $"{c.Value} does not look like a {item.Brand} serial number."); continue; }
                    var local = await t.WithAllBranchesAsync(() => t.QuerySingleOrDefaultAsync<(Guid Unit, string Status, string Code, string Description)>($"""
                        SELECT u.id, u.status, b.code, {ItemPricing.DescriptionEn} FROM device.imei_registry r JOIN device.device_unit u ON u.id = r.device_unit_id
                          JOIN core.branch b ON b.id = u.branch_id JOIN catalog.variant v ON v.id = u.variant_id JOIN catalog.product p ON p.id = v.product_id
                         WHERE r.status = 'active' AND r.value = @Value AND (CASE WHEN @Kind = 'serial' THEN r.kind = 'serial' AND (@brandKey::uuid IS NULL OR r.brand_key = @brandKey) ELSE r.kind IN ('imei','eid') END)
                           AND (@except::uuid IS NULL OR u.id <> @except)
                         LIMIT 1
                        """, new { c.Value, c.Kind, brandKey = req.VariantId is null ? (Guid?)null : item.BrandKey, except = req.ExceptUnitId }));
                    if (local.Code is not null)
                    { r["problem"] = Problem(ErrorCodes.DeviceIdentifierTaken, $"{label} {c.Value} already belongs to a device {local.Status.Replace('_', ' ')} at {local.Code} ({local.Description})."); continue; }
                    if (c.Kind == "imei" && await TacAsync(t, c.Value) is { } tac)
                    {
                        var looks = $"{tac.Brand} {tac.Model}";
                        var mismatch = item.Description is not null && !item.Description.Contains(tac.Brand, StringComparison.OrdinalIgnoreCase)
                                       && !(item.Brand ?? "").Contains(tac.Brand, StringComparison.OrdinalIgnoreCase);
                        r["hint"] = mismatch ? $"This IMEI looks like a {looks}, not {item.Description}." : $"Looks like a {looks}.";
                        r["hintMismatch"] = mismatch;
                    }
                    ids.Add(new PushIdentifier(c.Kind, 1, c.Value, c.Kind == "serial" ? (item.Brand ?? (req.VariantId is null ? null : "product:" + item.ProductId)) : null));
                    if (t.Server.IsHeadOffice)
                        foreach (var hit in await CentralRegistry.CheckAsync(t, [ids[^1]], req.ExceptUnitId))
                            if (hit.Holder is { } h) r["problem"] = Problem(ErrorCodes.DeviceIdentifierTaken, $"{label} {c.Value} is already registered: {CentralRegistry.Describe(h)}.");
                    r["centralChecked"] = t.Server.IsHeadOffice;
                }
                return (list, ids);
            }, ct);
            if (central.IsBranch && ids.Count > 0)
            {
                var answer = await central.CheckAsync(ids, req.ExceptUnitId, ct);
                foreach (var r in results.Where(r => r["problem"] is null && (string)r["kind"]! is "imei" or "serial"))
                {
                    r["centralChecked"] = answer is not null;
                    var hit = answer?.FirstOrDefault(a => a.Value == (string)r["value"]!);
                    if (hit?.State == "blocked") r["problem"] = Problem(ErrorCodes.ImeiBlocked, $"{r["value"]} is on the blocked list: {hit.BlockedReason}.");
                    else if (hit?.Holder is { } h) r["problem"] = Problem(ErrorCodes.DeviceIdentifierTaken, $"{r["value"]} is already registered: {CentralRegistry.Describe(h)}.");
                }
            }
            return Results.Ok(results);
        }).RequirePermission(Permissions.DeviceView).RequireModule("device");

        // ------------------------------------------------------------------ correcting a mistyped identifier
        g.MapPost("/{id:guid}/identifiers", async (Guid id, IdentifierCorrection req, HttpContext http, KiteDb db, CentralClient central, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Reason)) throw new KiteException(ErrorCodes.Validation, "Say why the identifier is corrected.", 400, "reason");
            var entry = req.Entry ?? "scan";
            if (entry is not ("scan" or "manual")) throw new KiteException(ErrorCodes.Validation, "Entry is scan or manual.", 400, "entry");
            // Online check first on a branch server: the new identifier must not be held anywhere else.
            var pre = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var u = await DeviceUnits.LoadAsync(t, id);
                var (i1, i2, sn) = DeviceUnits.CheckIdentifiers(u.Tracking, new UnitInput(req.Imei1, req.Imei2, req.Serial), "identifiers");
                var brand = await BrandNameAsync(t, u.VariantId);
                var list = new List<PushIdentifier>();
                if (i1 is not null && i1 != u.Imei1) list.Add(new PushIdentifier("imei", 1, i1));
                if (i2 is not null && i2 != u.Imei2) list.Add(new PushIdentifier("imei", 2, i2));
                if (sn is not null && sn != u.Serial) list.Add(new PushIdentifier("serial", 1, sn, brand));
                return list;
            }, ct);
            if (central.IsBranch && pre.Count > 0 && await central.CheckAsync(pre, id, ct) is { } answer) RefuseHeld(answer);

            var result = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var u = await DeviceUnits.LoadAsync(t, id);
                http.User.Demand(Permissions.DeviceImeiCorrect, u.BranchId);
                if (entry == "manual") http.User.Demand(Permissions.DeviceImeiManualEntry, u.BranchId);
                var (i1, i2, sn) = DeviceUnits.CheckIdentifiers(u.Tracking, new UnitInput(req.Imei1, req.Imei2, req.Serial), "identifiers");
                if (i1 == u.Imei1 && i2 == u.Imei2 && sn == u.Serial) throw new KiteException(ErrorCodes.Validation, "Nothing changed: give the correct identifier.", 400, "imei1");
                var brand = await BrandNameAsync(t, u.VariantId);
                var current = (await t.QueryAsync<(Guid Id, string Kind, short Slot, string Value)>("""
                    SELECT id, kind, slot, value FROM device.imei_registry WHERE device_unit_id = @id AND status = 'active'
                    """, new { id })).ToList();
                var wanted = new[] { ("imei", (short)1, i1), ("imei", (short)2, i2), ("serial", (short)1, sn) };
                await CentralRegistry.LockAsync(t, current.Select(c => new CentralRegistry.Key(c.Kind, c.Value, c.Kind == "serial" ? brand : ""))
                    .Concat(wanted.Where(w => w.Item3 is not null).Select(w => new CentralRegistry.Key(w.Item1, w.Item3!, w.Item1 == "serial" ? brand : ""))));
                foreach (var (kind, slot, value) in wanted)
                {
                    var now = current.FirstOrDefault(c => c.Kind == kind && c.Slot == slot);
                    if (now.Value == value) continue;
                    if (value is not null)
                    {
                        var label = kind == "serial" ? "Serial number" : "IMEI";
                        if (await CentralRegistry.BlockedReasonAsync(t, kind, value) is { } why)
                            throw new KiteException(ErrorCodes.ImeiBlocked, $"{label} {value} is on the blocked list: {why}.", 409, kind);
                        var holder = await t.WithAllBranchesAsync(() => t.QuerySingleOrDefaultAsync<(string Status, string Code)>("""
                            SELECT u.status, b.code FROM device.imei_registry r JOIN device.device_unit u ON u.id = r.device_unit_id JOIN core.branch b ON b.id = u.branch_id
                             WHERE r.status = 'active' AND r.value = @value AND r.device_unit_id <> @id
                               AND (CASE WHEN @kind = 'serial' THEN r.kind = 'serial' AND r.brand_key = @BrandKey ELSE r.kind IN ('imei','eid') END)
                            """, new { value, id, kind, u.BrandKey }));
                        if (holder.Code is not null)
                            throw new KiteException(ErrorCodes.DeviceIdentifierTaken, $"{label} {value} already belongs to a device {holder.Status.Replace('_', ' ')} at {holder.Code}.", 409, kind);
                        if (t.Server.IsHeadOffice)
                            foreach (var hit in await CentralRegistry.CheckAsync(t, [new PushIdentifier(kind, slot, value, kind == "serial" ? brand : null)], id))
                                if (hit.Holder is { } h) throw new KiteException(ErrorCodes.DeviceIdentifierTaken, $"{label} {value} is already registered: {CentralRegistry.Describe(h)}.", 409, kind);
                    }
                    if (now.Id != Guid.Empty)
                        await t.ExecuteAsync("SELECT device.retire_identifier(@rid, @reason, @actor)", new { rid = now.Id, reason = "corrected: " + req.Reason.Trim(), actor = t.Actor.UserId });
                    if (value is not null)
                        await t.ExecuteAsync("""
                            INSERT INTO device.imei_registry (id, business_id, device_unit_id, kind, slot, value, brand_key, entry, created_by)
                            VALUES (@rid, @BusinessId, @id, @kind, @slot, @value, @BrandKey, @entry, @by)
                            """, new { rid = t.NewId(), u.BusinessId, id, kind, slot, value, u.BrandKey, entry, by = t.Actor.UserId });
                }
                var details = new { from = new { u.Imei1, u.Imei2, u.Serial }, to = new { imei1 = i1, imei2 = i2, serial = sn }, reason = req.Reason.Trim() };
                await DeviceUnits.ApplyEventAsync(t, id, "identifier_corrected", null, docType: "device_unit", docId: id, details: details);
                await t.AuditAsync(new AuditEntry("device.identifier_corrected", "device_unit", id, Before: Json.Obj(details.from), After: Json.Obj(new { details.to, details.reason, entry }), BranchId: u.BranchId));
                await t.PublishAsync("device.identifier_corrected", "device_unit", id, u.Version + 1, Json.Obj(details), u.BranchId);
                if (t.Server.IsHeadOffice) await CentralRegistry.SyncOwnUnitAsync(t, id);
                else await t.ExecuteAsync("UPDATE device.central_check SET state = CASE WHEN state = 'verified' THEN 'pending' ELSE state END, updated_at = now() WHERE device_unit_id = @id", new { id });
                return new { unitId = id, imei1 = i1, imei2 = i2, serial = sn };
            }, ct);
            if (central.IsBranch) await PushQuietlyAsync(central, id, http, ct);
            return Results.Ok(result);
        }).RequirePermission(Permissions.DeviceImeiCorrect).RequireModule("device");

        // ------------------------------------------------------------------ reversing a receipt made in error (8.2)
        g.MapPost("/{id:guid}/reverse-receipt", async (Guid id, DeviceReasonRequest req, HttpContext http, KiteDb db, CentralClient central, LicenceService licence, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Reason)) throw new KiteException(ErrorCodes.Validation, "Say why the receipt is reversed.", 400, "reason");
            await licence.RequireTransactAsync(ct);
            var result = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var u = await DeviceUnits.LoadAsync(t, id);
                http.User.Demand(Permissions.DeviceWriteOff, u.BranchId);
                var holds = await UnitHolds.OpenAsync(t, id);
                if (u.Status != "quarantined" || !holds.Any(h => h.Reason == "imei_conflict"))
                    throw new KiteException(ErrorCodes.DeviceOnHold, "A receipt is reversed only for a device on hold because head office found its IMEI on another device. Write it off instead.", 409);
                if (u.AccumulatedCost != u.AcquisitionCost || await t.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM device.device_event WHERE device_unit_id = @id AND event_type IN ('sold','cost_added'))", new { id }))
                    throw new KiteException(ErrorCodes.DeviceOnHold, "This device has been sold or had costs added since it was received, so its receipt cannot simply be reversed. Write it off instead.", 409);
                var source = await t.ExecuteScalarAsync<string>("SELECT source FROM device.device_unit WHERE id = @id", new { id });
                if (source is not ("opening" or "adjustment"))
                    throw new KiteException(ErrorCodes.DeviceOnHold, "Only devices received on opening or found stock can be reversed here.", 409);
                var biz = await BusinessInfo.LoadAsync(t);
                var date = BusinessDay.Today(biz.TimeZone, t.Clock);
                var number = await DocNumbers.NextAsync(t, biz.Id, u.BranchId, "stock_adjustment", await FiscalPeriods.FiscalYearOfAsync(t, biz.Id, date), "ADJ");
                var docId = t.NewId();
                var lineId = t.NewId();
                await t.ExecuteAsync("""
                    INSERT INTO inv.adjustment (id, business_id, branch_id, warehouse_id, number, kind, adjustment_date, note, posted_by, origin_server_id)
                    VALUES (@docId, @b, @BranchId, @WarehouseId, @number, 'adjustment', @d::date, @note, @uid, (SELECT id FROM core.server WHERE is_self))
                    """, new { docId, b = biz.Id, u.BranchId, u.WarehouseId, number, d = date.ToDateTime(TimeOnly.MinValue), note = $"Receipt reversed {u.Identifier}: {req.Reason.Trim()}", uid = http.User.UserIdOf() });
                var (_, value) = await DeviceUnits.MoveAsync(t, u, "adjustment", -1, null, "adjustment", docId, lineId, "correction");
                await t.ExecuteAsync("""
                    INSERT INTO inv.adjustment_line (id, adjustment_id, line_no, variant_id, qty, unit_cost, reason_code, device_unit_id)
                    VALUES (@lineId, @docId, 1, @VariantId, -1, NULL, 'correction', @Id)
                    """, new { lineId, docId, u.VariantId, u.Id });
                await t.ExecuteAsync("""
                    UPDATE device.unit_hold SET released_at = now(), released_by = @by, release_note = 'receipt reversed' WHERE device_unit_id = @id AND released_at IS NULL
                    """, new { id, by = t.Actor.UserId });
                await DeviceUnits.ApplyEventAsync(t, id, "receipt_reversed", "reversed", docType: "adjustment", docId: docId, details: new { reason = req.Reason.Trim() });
                foreach (var rid in await t.QueryAsync<Guid>("SELECT id FROM device.imei_registry WHERE device_unit_id = @id AND status = 'active'", new { id }))
                    await t.ExecuteAsync("SELECT device.retire_identifier(@rid, @reason, @actor)", new { rid, reason = "receipt reversed: " + req.Reason.Trim(), actor = t.Actor.UserId });
                await t.ExecuteAsync("UPDATE device.central_check SET state = 'pending', updated_at = now() WHERE device_unit_id = @id", new { id });
                await Posting.PostAsync(t, biz.Id, u.BranchId, PostingRuleTemplates.DeviceReceiptReversal,
                    new PostingFacts().Add(source == "opening" ? "opening_by_product_type" : "gain_by_product_type", u.PostingClass, -value),
                    date, "device_receipt_reversal", docId, $"Receipt reversed {number} ({u.Identifier})");
                await t.AuditAsync(new AuditEntry("device.receipt_reversed", "device_unit", id, After: Json.Obj(new { number, cost = -value, reason = req.Reason.Trim() }), BranchId: u.BranchId));
                await t.PublishAsync("device.receipt_reversed", "device_unit", id, u.Version + 1, Json.Obj(new { number, cost = -value }), u.BranchId);
                if (t.Server.IsHeadOffice) await CentralRegistry.SyncOwnUnitAsync(t, id);
                return new { id = docId, number, cost = -value };
            }, ct);
            if (central.IsBranch) await PushQuietlyAsync(central, id, http, ct);
            return Results.Ok(result);
        }).RequirePermission(Permissions.DeviceWriteOff).RequireModule("device");

        // ------------------------------------------------------------------ IMEI conflicts (8.2, 8.3)
        g.MapGet("/conflicts", async (string? status, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                if (!t.Server.IsHeadOffice) return await LocalConflictsAsync(t);
                var self = await t.ExecuteScalarAsync<Guid>("SELECT id FROM core.server WHERE is_self");
                var conflicts = (await t.QueryAsync<(Guid Id, string Kind, string Value, string Status, DateTime OpenedAt, DateTime? ResolvedAt, string? Resolution)>("""
                    SELECT id, kind, value, status, opened_at, resolved_at, resolution FROM device.imei_conflict
                     WHERE @status::text IS NULL OR status = @status ORDER BY status = 'open' DESC, opened_at DESC LIMIT 500
                    """, new { status = status is "open" or "resolved" ? status : null })).ToList();
                var list = new List<object>();
                foreach (var c in conflicts)
                {
                    var sides = await t.RowsAsync("""
                        SELECT s.device_unit_id AS "unitId", b.code AS "branchCode", b.name_en AS "branchNameEn", b.name_ar AS "branchNameAr",
                               s.description_en AS "descriptionEn", s.description_ar AS "descriptionAr",
                               coalesce(u.status, r.unit_status, s.unit_status) AS "unitStatus", s.received_at AS "receivedAt", s.outcome, s.outcome_at AS "outcomeAt",
                               (s.server_id = @self) AS "local", r.status AS "rowStatus"
                          FROM device.imei_conflict_side s JOIN core.branch b ON b.id = s.branch_id JOIN device.central_registry r ON r.id = s.registry_id
                          LEFT JOIN device.device_unit u ON u.id = s.device_unit_id AND s.server_id = @self
                         WHERE s.conflict_id = @Id ORDER BY s.joined_at
                        """, new { c.Id, self });
                    list.Add(new { c.Id, c.Kind, c.Value, c.Status, c.OpenedAt, c.ResolvedAt, c.Resolution, sides, note = (string?)null });
                }
                return list;
            }, ct))).RequirePermission(Permissions.DeviceConflicts).RequireModule("device");

        // ------------------------------------------------------------------ the blocked list (11.5), kept at head office
        g.MapGet("/blocked", async (string? q, string? status, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.RowsAsync("""
                SELECT b.id, b.kind, b.value, b.reason, b.reference, b.status, b.added_at AS "addedAt", coalesce(b.added_by_name, u.display_name_en) AS "addedBy",
                       b.removed_at AS "removedAt", b.removed_reason AS "removedReason",
                       (SELECT count(*) FROM device.imei_registry r WHERE r.status = 'active' AND r.value = b.value) AS "devicesHere"
                  FROM device.blocked_imei b LEFT JOIN iam."user" u ON u.id = b.added_by
                 WHERE (@status::text IS NULL OR b.status = @status) AND (@q::text IS NULL OR b.value LIKE '%' || upper(@q) || '%' OR b.reason ILIKE '%' || @q || '%')
                 ORDER BY b.status, b.added_at DESC LIMIT 1000
                """, new { q = string.IsNullOrWhiteSpace(q) ? null : q.Trim(), status = status is "active" or "removed" ? status : null }), ct)))
            .RequirePermission(Permissions.DeviceView).RequireModule("device");

        g.MapPost("/blocked", async (BlockedRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Reason)) throw new KiteException(ErrorCodes.Validation, "Give the reason (for example: reported stolen).", 400, "reason");
            var code = DeviceUnits.Classify(req.Value, "value");
            var result = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                if (!t.Server.IsHeadOffice) throw new KiteException(ErrorCodes.NotHeadOffice, "The blocked list is kept at head office; branches receive it automatically.", 409);
                var id = t.NewId();
                var name = await t.ExecuteScalarAsync<string?>("SELECT display_name_en FROM iam.\"user\" WHERE id = @uid", new { uid = http.User.UserIdOf() });
                await t.ExecuteAsync("""
                    INSERT INTO device.blocked_imei (id, business_id, kind, value, reason, reference, added_by, added_by_name, seq)
                    VALUES (@id, (SELECT id FROM core.business LIMIT 1), @kind, @Value, @reason, @reference, @uid, @name, nextval('device.blocked_seq'))
                    """, new { id, kind = code.Kind == "serial" ? "serial" : "imei", code.Value, reason = req.Reason.Trim(), reference = req.Reference?.Trim(), uid = http.User.UserIdOf(), name });
                await t.WithAllBranchesAsync(async () => { await BlockedList.ApplyToUnitsAsync(t, id, code.Kind, code.Value, true, req.Reason.Trim()); return 0; });
                await t.AuditAsync(new AuditEntry("device.blocked_added", "blocked_imei", id, After: Json.Obj(new { code.Kind, code.Value, req.Reason, req.Reference })));
                return new { id, kind = code.Kind, value = code.Value };
            }, ct);
            return Results.Created($"/api/v1/devices/blocked/{result.id}", result);
        }).RequirePermission(Permissions.DeviceBlocklist).RequireModule("device");

        g.MapPost("/blocked/{id:guid}/remove", async (Guid id, DeviceReasonRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Reason)) throw new KiteException(ErrorCodes.Validation, "Say why it comes off the list.", 400, "reason");
            await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                if (!t.Server.IsHeadOffice) throw new KiteException(ErrorCodes.NotHeadOffice, "The blocked list is kept at head office.", 409);
                var row = await t.QuerySingleOrDefaultAsync<(string Kind, string Value, string Status)>("SELECT kind, value, status FROM device.blocked_imei WHERE id = @id", new { id });
                if (row.Kind is null) throw new KiteException(ErrorCodes.NotFound, "Not on the blocked list.", 404);
                if (row.Status != "active") throw new KiteException(ErrorCodes.Conflict, "It was already taken off the list.", 409);
                await t.ExecuteAsync("""
                    UPDATE device.blocked_imei SET status = 'removed', removed_at = now(), removed_by = @uid, removed_reason = @reason, seq = nextval('device.blocked_seq')
                     WHERE id = @id
                    """, new { id, uid = http.User.UserIdOf(), reason = req.Reason.Trim() });
                await t.WithAllBranchesAsync(async () => { await BlockedList.ApplyToUnitsAsync(t, id, row.Kind, row.Value, false, req.Reason.Trim()); return 0; });
                await t.AuditAsync(new AuditEntry("device.blocked_removed", "blocked_imei", id, After: Json.Obj(new { row.Value, reason = req.Reason.Trim() })));
            }, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.DeviceBlocklist).RequireModule("device");

        // ------------------------------------------------------------------ model hints (TAC) and serial formats (11.2)
        g.MapGet("/tac", async (HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.QuerySingleOrDefaultAsync<(long Count, DateTime? LastImportedAt)>(
                "SELECT count(*), max(imported_at) FROM device.tac_model"), ct) is var s ? new { count = s.Count, lastImportedAt = s.LastImportedAt } : null))
            .RequirePermission(Permissions.DeviceView).RequireModule("device");

        g.MapGet("/tac/{code}", async (string code, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            var digits = new string(code.Where(char.IsAsciiDigit).ToArray());
            if (digits.Length < 8) throw new KiteException(ErrorCodes.Validation, "Give at least the first 8 digits of the IMEI.", 400, "code");
            var hit = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => TacAsync(t, digits), ct);
            return hit is { } h ? Results.Ok(new { tac = digits[..8], h.Brand, h.Model }) : throw new KiteException(ErrorCodes.NotFound, "That model is not in the imported model table.", 404);
        }).RequirePermission(Permissions.DeviceView).RequireModule("device");

        g.MapPost("/tac/import", async (TacImportRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Csv) || req.Csv.Length > 20_000_000) throw new KiteException(ErrorCodes.Validation, "Paste or upload the model table (tac, brand, model).", 400, "csv");
            var rows = new List<(string Tac, string Brand, string Model)>();
            var skipped = 0;
            foreach (var line in req.Csv.Split('\n'))
            {
                var cells = line.TrimEnd('\r').Split(line.Contains('\t') ? '\t' : line.Contains(';') ? ';' : ',').Select(c => c.Trim().Trim('"').Trim()).ToArray();
                if (cells.Length < 3 || cells.All(c => c.Length == 0)) { if (line.Trim().Length > 0) skipped++; continue; }
                if (cells[0].Length != 8 || !cells[0].All(char.IsAsciiDigit) || cells[1].Length == 0 || cells[2].Length == 0) { skipped++; continue; }
                rows.Add((cells[0], cells[1], string.Join(' ', cells.Skip(2).Where(c => c.Length > 0))));
            }
            if (rows.Count == 0) throw new KiteException(ErrorCodes.Validation, "No line had an 8-digit TAC, a brand and a model.", 400, "csv");
            var n = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                foreach (var chunk in rows.DistinctBy(r => r.Tac).Chunk(1000))
                    await t.ExecuteAsync("""
                        INSERT INTO device.tac_model (business_id, tac, brand, model, imported_by)
                        SELECT (SELECT id FROM core.business LIMIT 1), x.tac, x.brand, x.model, @uid FROM unnest(@tacs::text[], @brands::text[], @models::text[]) AS x(tac, brand, model)
                        ON CONFLICT (business_id, tac) DO UPDATE SET brand = EXCLUDED.brand, model = EXCLUDED.model, imported_at = now(), imported_by = EXCLUDED.imported_by
                        """, new { tacs = chunk.Select(r => r.Tac).ToArray(), brands = chunk.Select(r => r.Brand).ToArray(), models = chunk.Select(r => r.Model).ToArray(), uid = http.User.UserIdOf() });
                await t.AuditAsync(new AuditEntry("device.tac_imported", "tac_model", Guid.Empty, After: Json.Obj(new { rows = rows.Count, skipped })));
                return rows.DistinctBy(r => r.Tac).Count();
            }, ct);
            return Results.Ok(new { imported = n, skipped });
        }).RequirePermission(Permissions.DeviceSettings).RequireModule("device");

        g.MapGet("/serial-formats", async (HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.RowsAsync("""
                SELECT id, name_en AS "nameEn", name_ar AS "nameAr", serial_pattern AS "serialPattern" FROM catalog.brand WHERE status = 'active' ORDER BY name_en
                """), ct)))
            .RequirePermission(Permissions.DeviceView).RequireModule("device");

        g.MapPut("/serial-formats/{brandId:guid}", async (Guid brandId, SerialFormatRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            var pattern = string.IsNullOrWhiteSpace(req.Pattern) ? null : req.Pattern.Trim();
            if (pattern is not null && (pattern.Length > 200 || !SerialFormats.Valid(pattern)))
                throw new KiteException(ErrorCodes.Validation, "That is not a valid pattern (a regular expression such as ^[A-Z0-9]{12}$).", 400, "pattern");
            await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var n = await t.ExecuteAsync("UPDATE catalog.brand SET serial_pattern = @pattern WHERE id = @brandId", new { brandId, pattern });
                if (n == 0) throw new KiteException(ErrorCodes.NotFound, "Brand not found.", 404);
                await t.AuditAsync(new AuditEntry("catalog.brand_serial_format", "brand", brandId, After: Json.Obj(new { pattern })));
            }, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.DeviceSettings).RequireModule("device");

        // ------------------------------------------------------------------ business-wide look-up (11.5) and the link with head office
        g.MapGet("/history-lookup/{code}", async (string code, HttpContext http, KiteDb db, CentralClient central, CancellationToken ct) =>
        {
            var c = DeviceUnits.Classify(code);
            var local = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var units = await t.WithAllBranchesAsync(() => t.RowsAsync($"""
                    SELECT u.id, b.code AS "branchCode", {ItemPricing.DescriptionEn} AS "descriptionEn", {ItemPricing.DescriptionAr} AS "descriptionAr", u.status, u.condition,
                           u.received_at AS "receivedAt", s.number AS "soldOn", u.sold_at AS "soldAt", l.warranty_until AS "warrantyUntil", r.status AS "identifierStatus",
                           core.branch_visible(u.branch_id) AS "visible"
                      FROM device.imei_registry r JOIN device.device_unit u ON u.id = r.device_unit_id JOIN core.branch b ON b.id = u.branch_id
                      JOIN catalog.variant v ON v.id = u.variant_id JOIN catalog.product p ON p.id = v.product_id
                      LEFT JOIN sales.sale s ON s.id = u.sold_sale_id LEFT JOIN sales.sale_line l ON l.sale_id = s.id AND l.device_unit_id = u.id
                     WHERE r.value = @Value AND (CASE WHEN @Kind = 'serial' THEN r.kind = 'serial' ELSE r.kind IN ('imei','eid') END)
                     ORDER BY r.status = 'active' DESC, u.received_at DESC
                    """, new { c.Value, c.Kind }));
                var head = t.Server.IsHeadOffice ? await CentralSync.LookupAsync(t, c.Kind, c.Value) : null;
                var tac = c.Kind == "imei" ? await TacAsync(t, c.Value) : null;
                return (units, head, blocked: await CentralRegistry.BlockedReasonAsync(t, c.Kind, c.Value), tac);
            }, ct);
            var headOffice = local.head ?? (central.IsBranch ? await central.LookupAsync(c.Value, ct) : null);
            return Results.Ok(new
            {
                kind = c.Kind, value = c.Value, local = local.units, headOffice, headOfficeReachable = !central.IsBranch || headOffice is not null,
                blocked = headOffice?.BlockedReason ?? local.blocked, model = local.tac is { } h ? $"{h.Brand} {h.Model}" : null,
            });
        }).RequirePermission(Permissions.DeviceView).RequireModule("device");

        g.MapGet("/central-status", async (HttpContext http, KiteDb db, CentralClient central, BranchLinkService link, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var s = await t.QuerySingleOrDefaultAsync<(long Pending, long Conflicts, long Blocked)>("""
                    SELECT (SELECT count(*) FROM device.central_check c JOIN device.device_unit u ON u.id = c.device_unit_id WHERE c.state = 'pending' AND u.status NOT IN ('sold','written_off','reversed','supplier_returned')),
                           (SELECT count(DISTINCT device_unit_id) FROM device.unit_hold WHERE reason = 'imei_conflict' AND released_at IS NULL),
                           (SELECT count(*) FROM device.blocked_imei WHERE status = 'active')
                    """);
                var lk = await t.QuerySingleOrDefaultAsync<(DateTime? LastPushAt, DateTime? LastPushOkAt, string? LastPushError)>(
                    "SELECT last_push_at, last_push_ok_at, last_push_error FROM device.central_link LIMIT 1");
                var settings = await DeviceUnits.SettingsAsync(t, (await BusinessInfo.LoadAsync(t)).Id);
                var openConflicts = t.Server.IsHeadOffice ? await t.ExecuteScalarAsync<long>("SELECT count(*) FROM device.imei_conflict WHERE status = 'open'") : (long?)null;
                return new
                {
                    role = t.Server.Role, isHeadOffice = t.Server.IsHeadOffice, headOfficeReachable = !central.IsBranch || link.LastOkAt is not null && link.HeadOfficeLikelyReachable,
                    lastContactAt = central.IsBranch ? link.LastOkAt : null, pending = s.Pending, unitsInConflict = s.Conflicts, openConflicts, blocked = s.Blocked,
                    lk.LastPushAt, lk.LastPushOkAt, lk.LastPushError, strict = settings.StrictCentralCheck,
                };
            }, ct))).RequirePermission(Permissions.DeviceView).RequireModule("device");
    }

    private static Dictionary<string, object?> Problem(string code, string message) => new() { ["code"] = code, ["message"] = message };

    public static async Task<(string Brand, string Model)?> TacAsync(DbTx t, string imei)
    {
        if (imei.Length < 8) return null;
        var hit = await t.QuerySingleOrDefaultAsync<(string Brand, string Model)>("SELECT brand, model FROM device.tac_model WHERE tac = @tac", new { tac = imei[..8] });
        return hit.Brand is null ? null : hit;
    }

    public static Task<string> BrandNameAsync(DbTx t, Guid variantId) =>
        t.ExecuteScalarAsync<string>("""
            SELECT coalesce(lower(trim(b.name_en)), 'product:' || p.id::text) FROM catalog.variant v JOIN catalog.product p ON p.id = v.product_id
              LEFT JOIN catalog.brand b ON b.id = p.brand_id WHERE v.id = @variantId
            """, new { variantId });

    /// <summary>The valid identifiers on a receiving document's device lines, with the serial numbers' brand.</summary>
    public static async Task<List<PushIdentifier>> IdentifiersOfAsync(DbTx t, IEnumerable<AdjustmentLineInput> lines)
    {
        var list = new List<PushIdentifier>();
        foreach (var l in lines.Where(l => l.Units is { Count: > 0 }))
        {
            var brand = await BrandNameAsync(t, l.VariantId);
            foreach (var u in l.Units!)
            {
                foreach (var (raw, slot) in new[] { (u.Imei1, (short)1), (u.Imei2, (short)2) })
                    if (DeviceMath.Classify(raw) is { Kind: "imei" } c) list.Add(new PushIdentifier("imei", slot, c.Value));
                if (DeviceMath.Classify(u.Serial) is { Kind: "serial" } s) list.Add(new PushIdentifier("serial", 1, s.Value, brand));
            }
        }
        return list;
    }

    /// <summary>Refuses a receipt or correction when head office says an identifier is blocked or held by another unit.</summary>
    public static void RefuseHeld(IEnumerable<CentralCheckResult> answer)
    {
        foreach (var a in answer)
        {
            var label = a.Kind == "serial" ? "Serial number" : "IMEI";
            if (a.State == "blocked") throw new KiteException(ErrorCodes.ImeiBlocked, $"{label} {a.Value} is on the blocked list: {a.BlockedReason}. Do not take this device in.", 409);
            if (a.Holder is { } h) throw new KiteException(ErrorCodes.DeviceIdentifierTaken, $"{label} {a.Value} is already registered: {CentralRegistry.Describe(h)}. Scan another device.", 409);
        }
    }

    private static async Task PushQuietlyAsync(CentralClient central, Guid unitId, HttpContext http, CancellationToken ct)
    {
        try { await central.PushAsync([unitId], ct); }
        catch (Exception e) when (e is not OperationCanceledException)
        { http.RequestServices.GetRequiredService<ILogger<CentralClient>>().LogWarning("Device push failed: {Error}", e.Message); }
    }

    /// <summary>On a branch server: its own units on hold for IMEI conflicts, with head office's note.</summary>
    private static async Task<List<object>> LocalConflictsAsync(DbTx t)
    {
        var rows = await t.QueryAsync<(Guid ConflictId, Guid UnitId, string? Note, DateTime HeldAt, DateTime? ReleasedAt, string BranchCode, string NameEn, string NameAr,
            string DescriptionEn, string DescriptionAr, string Status, DateTime ReceivedAt)>($"""
            SELECT h.conflict_id, u.id, h.note, h.held_at, h.released_at, b.code, b.name_en, b.name_ar, {ItemPricing.DescriptionEn}, {ItemPricing.DescriptionAr}, u.status, u.received_at
              FROM device.unit_hold h JOIN device.device_unit u ON u.id = h.device_unit_id JOIN core.branch b ON b.id = u.branch_id
              JOIN catalog.variant v ON v.id = u.variant_id JOIN catalog.product p ON p.id = v.product_id
             WHERE h.reason = 'imei_conflict' ORDER BY h.released_at IS NULL DESC, h.held_at DESC LIMIT 500
            """);
        return rows.Select(r => (object)new
        {
            id = r.ConflictId, kind = "imei", value = (string?)null, status = r.ReleasedAt is null ? "open" : "resolved", openedAt = r.HeldAt, resolvedAt = r.ReleasedAt,
            resolution = (string?)null, note = r.Note,
            sides = new[] { new { unitId = r.UnitId, branchCode = r.BranchCode, branchNameEn = r.NameEn, branchNameAr = r.NameAr, descriptionEn = r.DescriptionEn,
                                  descriptionAr = r.DescriptionAr, unitStatus = r.Status, receivedAt = (DateTime?)r.ReceivedAt, outcome = (string?)null, outcomeAt = (DateTime?)null,
                                  local = true, rowStatus = "active" } },
        }).ToList();
    }
}
