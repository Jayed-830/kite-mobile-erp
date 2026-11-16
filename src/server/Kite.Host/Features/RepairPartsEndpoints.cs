using Kite.Accounting;
using Kite.Host.Infrastructure;
using Kite.Persistence;
using Kite.Platform;

namespace Kite.Host.Features;

public sealed record OutsideWorkRequest(string Vendor, decimal Amount, string? Reference = null, string? Note = null);
public sealed record OutsideWorkReverseRequest(string Reason);

public static partial class RepairEndpoints
{
    // ------------------------------------------------------------------ outside work and parts requests (13.4, 15.1)
    private static void MapParts(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/repairs/{id:guid}");

        // Outside work (a specialist's repair) at cost: into the job's work in progress, owed to the supplier until the invoice arrives.
        g.MapPost("/outside-work", async (Guid id, OutsideWorkRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await PosEndpoints.WithRetryAsync(() => db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var j = await RepairCore.LoadAsync(t, id, forUpdate: true);
                http.User.Demand(Permissions.RepairWork, j.BranchId);
                if (string.IsNullOrWhiteSpace(req.Vendor)) throw new KiteException(ErrorCodes.Validation, "Name who did the work.", 400, "vendor");
                if (req.Amount <= 0 || req.Amount != decimal.Round(req.Amount, 3)) throw new KiteException(ErrorCodes.Validation, "The amount is above zero, in baisa.", 400, "amount");
                if (j.Status is not ("under_diagnosis" or "waiting_parts" or "in_progress" or "testing"))
                    throw new KiteException(ErrorCodes.RepairState, $"Outside work is recorded while the job is diagnosed or repaired; it is {RepairCore.Words(j.Status)}.", 409);
                var biz = await BusinessInfo.LoadAsync(t);
                var lineId = t.NewId();
                var journal = await Posting.PostAsync(t, biz.Id, j.BranchId, PostingRuleTemplates.RepairOutsideWork, new PostingFacts().Set("incurred", req.Amount),
                    BusinessDay.Today(biz.TimeZone, t.Clock), "repair_outside_work", lineId, $"Repair {j.Number}: outside work by {req.Vendor.Trim()}");
                await t.ExecuteAsync("""
                    INSERT INTO repair.repair_line (id, business_id, branch_id, job_id, kind, cost, vendor, reference, technician_id, journal_id, note, created_by, origin_server_id)
                    VALUES (@lineId, @b, @BranchId, @id, 'external', @Amount, @vendor, @reference, @tech, @journal, @note, @uid, (SELECT id FROM core.server WHERE is_self))
                    """, new { lineId, b = biz.Id, j.BranchId, id, req.Amount, vendor = req.Vendor.Trim(), reference = Clean(req.Reference), tech = j.TechnicianId,
                               journal, note = Clean(req.Note), uid = Uid(http) });
                await t.AuditAsync(new AuditEntry("repair.outside_work", "repair_job", id, After: Json.Obj(new { j.Number, vendor = req.Vendor.Trim(), req.Amount, req.Reference }), BranchId: j.BranchId));
                return await DetailAsync(t, http, id);
            }, ct)))).RequirePermission(Permissions.RepairWork).RequireModule("repair");

        // Outside work recorded by mistake is reversed in full.
        g.MapPost("/outside-work/{lineId:guid}/reverse", async (Guid id, Guid lineId, OutsideWorkReverseRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await PosEndpoints.WithRetryAsync(() => db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var j = await RepairCore.LoadAsync(t, id, forUpdate: true);
                http.User.Demand(Permissions.RepairWork, j.BranchId);
                if (string.IsNullOrWhiteSpace(req.Reason)) throw new KiteException(ErrorCodes.Validation, "Say why it is reversed.", 400, "reason");
                var line = await t.QuerySingleOrDefaultAsync<(Guid Id, decimal Cost, string Vendor)>("""
                    SELECT id, cost, vendor FROM repair.repair_line WHERE id = @lineId AND job_id = @id AND kind = 'external' AND cost > 0
                    """, new { lineId, id });
                if (line.Id == Guid.Empty) throw new KiteException(ErrorCodes.NotFound, "That outside work is not on this job.", 404);
                if (await t.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM repair.repair_line WHERE return_of_id = @lineId) OR EXISTS (SELECT 1 FROM repair.wip_clearing WHERE line_id = @lineId)", new { lineId }))
                    throw new KiteException(ErrorCodes.RepairState, "That outside work was already reversed or written off.", 409);
                var biz = await BusinessInfo.LoadAsync(t);
                var revId = t.NewId();
                var journal = await Posting.PostAsync(t, biz.Id, j.BranchId, PostingRuleTemplates.RepairOutsideWork, new PostingFacts().Set("incurred", -line.Cost),
                    BusinessDay.Today(biz.TimeZone, t.Clock), "repair_outside_work", revId, $"Repair {j.Number}: outside work by {line.Vendor} reversed");
                await t.ExecuteAsync("""
                    INSERT INTO repair.repair_line (id, business_id, branch_id, job_id, kind, cost, vendor, return_of_id, journal_id, note, created_by, origin_server_id)
                    VALUES (@revId, @b, @BranchId, @id, 'external', @neg, @Vendor, @lineId, @journal, @reason, @uid, (SELECT id FROM core.server WHERE is_self))
                    """, new { revId, b = biz.Id, j.BranchId, id, neg = -line.Cost, line.Vendor, lineId, journal, reason = req.Reason.Trim(), uid = Uid(http) });
                await t.AuditAsync(new AuditEntry("repair.outside_work_reversed", "repair_job", id, After: Json.Obj(new { j.Number, line.Vendor, line.Cost }), Reason: req.Reason.Trim(), BranchId: j.BranchId));
                return await DetailAsync(t, http, id);
            }, ct)))).RequirePermission(Permissions.RepairWork).RequireModule("repair");

        // What the workshop is waiting for (13.4): open requests by part, oldest first, with the stock on hand and reserved.
        app.MapGet("/api/v1/repairs/parts-needed", async (Guid? branchId, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.RowsAsync("""
                SELECT r.job_id AS "jobId", j.number AS "jobNumber", j.status AS "jobStatus", b.code AS "branchCode", r.branch_id AS "branchId",
                       r.variant_id AS "variantId", v.sku, p.name_en AS "nameEn", p.name_ar AS "nameAr", r.qty AS outstanding, r.requested_total AS requested,
                       r.filled_total AS filled, r.open_since AS "openSince", coalesce(s.qty, 0) AS "onHand", coalesce(s.reserved, 0) AS reserved,
                       r.link_doc_type AS "linkDocType", r.link_doc_id AS "linkDocId"
                  FROM repair.part_request r JOIN repair.repair_job j ON j.id = r.job_id JOIN core.branch b ON b.id = r.branch_id
                  JOIN catalog.variant v ON v.id = r.variant_id JOIN catalog.product p ON p.id = v.product_id
                  LEFT JOIN inv.stock_balance s ON s.warehouse_id = r.warehouse_id AND s.variant_id = r.variant_id
                 WHERE r.qty > 0 AND (@branchId::uuid IS NULL OR r.branch_id = @branchId)
                 ORDER BY p.name_en, r.open_since
                """, new { branchId }), ct))).RequirePermission(Permissions.RepairView).RequireModule("repair");

        // Fills open requests from arrived stock now, instead of waiting for the background run.
        app.MapPost("/api/v1/repairs/parts-needed/fill", async (KiteDb db, CancellationToken ct) =>
            Results.Ok(new { moved = await RepairParts.FillAsync(db, ct) })).RequirePermission(Permissions.RepairWork).RequireModule("repair");
    }
}
