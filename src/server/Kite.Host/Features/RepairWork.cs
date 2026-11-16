using Kite.Accounting;
using Kite.Devices;
using Kite.Host.Infrastructure;
using Kite.Persistence;
using Kite.Platform;
using Kite.Sales;

namespace Kite.Host.Features;

public static partial class RepairEndpoints
{
    // ------------------------------------------------------------------ estimates (13.3), parts (13.4) and labour
    private static void MapWork(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/repairs/{id:guid}");

        // A new estimate version: a draft the technician fills; earlier drafts and sent versions are superseded.
        g.MapPost("/estimates", async (Guid id, EstimateRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var j = await RepairCore.LoadAsync(t, id, forUpdate: true);
                http.User.Demand(Permissions.RepairWork, j.BranchId);
                if (j.IsWarrantyClaim) throw new KiteException(ErrorCodes.RepairEstimate, "A warranty claim is at no charge; it has no estimate to write.", 409);
                var biz = await BusinessInfo.LoadAsync(t);
                var lines = await RepairCore.PriceLinesAsync(t, biz, j.BranchId, await RepairCore.SettingsAsync(t), req.Lines);
                await RepairCore.NewEstimateAsync(t, j, lines, req.Note, Uid(http));
                return await DetailAsync(t, http, id);
            }, ct))).RequirePermission(Permissions.RepairWork).RequireModule("repair");

        g.MapPut("/estimates/{estimateId:guid}", async (Guid id, Guid estimateId, EstimateRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var j = await RepairCore.LoadAsync(t, id, forUpdate: true);
                http.User.Demand(Permissions.RepairWork, j.BranchId);
                var e = await EstimateOfAsync(t, j, estimateId);
                if (e.Status != "draft") throw new KiteException(ErrorCodes.RepairEstimate, "Only a draft estimate is changed; write a new version.", 409);
                var biz = await BusinessInfo.LoadAsync(t);
                var lines = await RepairCore.PriceLinesAsync(t, biz, j.BranchId, await RepairCore.SettingsAsync(t), req.Lines);
                await RepairCore.WriteLinesAsync(t, e.Id, lines, req.Note, Uid(http));
                return await DetailAsync(t, http, id);
            }, ct))).RequirePermission(Permissions.RepairWork).RequireModule("repair");

        // Send the estimate to the customer: it is fixed from now; the job waits for approval and the WhatsApp estimate goes out
        // (with Approve and Call me) when the customer agreed to updates.
        g.MapPost("/estimates/{estimateId:guid}/send", async (Guid id, Guid estimateId, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await PosEndpoints.WithRetryAsync(() => db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var j = await RepairCore.LoadAsync(t, id, forUpdate: true);
                if (!http.User.HasIn(Permissions.RepairWork, j.BranchId)) http.User.Demand(Permissions.RepairIntake, j.BranchId);
                var e = await EstimateOfAsync(t, j, estimateId);
                if (e.Status != "draft") throw new KiteException(ErrorCodes.RepairEstimate, $"Version {e.EstimateNo} was already {e.Status}.", 409);
                var uid = Uid(http);
                await SendEstimateRowAsync(t, e, uid);
                await t.AuditAsync(new AuditEntry("repair.estimate_sent", "repair_job", id, After: Json.Obj(new { j.Number, version = e.EstimateNo, e.Total }), BranchId: j.BranchId));
                if (j.Status is "under_diagnosis" or "in_progress")
                    await RepairCore.MoveAsync(t, j, "waiting_approval", $"Estimate version {e.EstimateNo} sent: {e.Total:0.000}", uid);
                if (j.Status == "waiting_approval")
                    await RepairCore.NotifyAsync(t, j, "repair_estimate",
                        [RepairCore.FirstName(j), j.Number, RepairCore.Money(e.Total, j.Lang), e.EstimateNo.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                        "repair_estimate", e.Id, uid);
                return await DetailAsync(t, http, id);
            }, ct)))).RequirePermission(Permissions.RepairView).RequireModule("repair");

        // The customer's approval (13.3): their signature in person, or by phone with who and the number; a technician approves an
        // estimate within what the customer authorised. Optionally the work starts in the same step.
        g.MapPost("/estimates/{estimateId:guid}/approve", async (Guid id, Guid estimateId, EstimateApproveRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await PosEndpoints.WithRetryAsync(() => db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var j = await RepairCore.LoadAsync(t, id, forUpdate: true);
                var e = await EstimateOfAsync(t, j, estimateId);
                var uid = Uid(http);
                if (e.Status is not ("draft" or "sent")) throw new KiteException(ErrorCodes.RepairEstimate, $"Version {e.EstimateNo} was already {e.Status}.", 409);
                if (j.Ended) throw new KiteException(ErrorCodes.RepairState, $"Job {j.Number} has ended; its estimate cannot be approved.", 409);
                switch (req.Method)
                {
                    case "signature":
                        http.User.Demand(Permissions.RepairIntake, j.BranchId);
                        if (string.IsNullOrWhiteSpace(req.ApproverName)) throw new KiteException(ErrorCodes.Validation, "Type the name of the person approving.", 400, "approverName");
                        await RepairCore.ApproveAsync(t, j, e, "signature", req.ApproverName, null, uid, t.Clock.UtcNow);
                        await SavePhotoAsync(t, j, "approval_signature", req.Signature, $"Estimate version {e.EstimateNo} approved by {req.ApproverName!.Trim()}", e.Id, uid);
                        break;
                    case "phone":
                        http.User.Demand(Permissions.RepairIntake, j.BranchId);
                        if (string.IsNullOrWhiteSpace(req.ApproverName)) throw new KiteException(ErrorCodes.Validation, "Type the name of the person you spoke to.", 400, "approverName");
                        if (string.IsNullOrWhiteSpace(req.Phone)) throw new KiteException(ErrorCodes.Validation, "Type the number you called.", 400, "phone");
                        await RepairCore.ApproveAsync(t, j, e, "phone", req.ApproverName, req.Phone, uid, t.Clock.UtcNow);
                        break;
                    case "pre_authorised":
                        http.User.Demand(Permissions.RepairWork, j.BranchId);
                        await RepairCore.ApproveAsync(t, j, e, "pre_authorised", null, null, uid, t.Clock.UtcNow);
                        break;
                    default:
                        throw new KiteException(ErrorCodes.Validation, "Approval is by signature, by phone, or within the pre-authorised amount. WhatsApp approvals arrive by themselves.", 400, "method");
                }
                if (req.Then is { } then)
                {
                    if (then is not ("in_progress" or "waiting_parts")) throw new KiteException(ErrorCodes.Validation, "After approval the job goes to repair or waits for parts.", 400, "then");
                    j = await RepairCore.LoadAsync(t, id);
                    http.User.Demand(Permissions.RepairWork, j.BranchId);
                    await RepairCore.MoveAsync(t, j, then, $"Estimate version {e.EstimateNo} approved ({req.Method})", uid);
                }
                return await DetailAsync(t, http, id);
            }, ct)))).RequirePermission(Permissions.RepairView).RequireModule("repair");

        g.MapPost("/estimates/{estimateId:guid}/decline", async (Guid id, Guid estimateId, EstimateDeclineRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var j = await RepairCore.LoadAsync(t, id, forUpdate: true);
                http.User.Demand(Permissions.RepairIntake, j.BranchId);
                var e = await EstimateOfAsync(t, j, estimateId);
                if (e.Status is not ("draft" or "sent")) throw new KiteException(ErrorCodes.RepairEstimate, $"Version {e.EstimateNo} was already {e.Status}.", 409);
                if (string.IsNullOrWhiteSpace(req.Reason)) throw new KiteException(ErrorCodes.Validation, "Say why the customer declined.", 400, "reason");
                await t.ExecuteAsync("""
                    UPDATE repair.estimate SET status = 'declined', declined_at = now(), decline_reason = @reason, updated_by = @uid WHERE id = @eid
                    """, new { reason = req.Reason.Trim(), uid = Uid(http), eid = e.Id });
                await t.AuditAsync(new AuditEntry("repair.estimate_declined", "repair_job", id, After: Json.Obj(new { j.Number, version = e.EstimateNo, req.Reason }), BranchId: j.BranchId));
                return await DetailAsync(t, http, id);
            }, ct))).RequirePermission(Permissions.RepairIntake).RequireModule("repair");

        // A part fitted (13.4): out of spare-parts stock at its average cost into the job's work-in-progress.
        g.MapPost("/parts", async (Guid id, PartFitRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await PosEndpoints.WithRetryAsync(() => db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var j = await RepairCore.LoadAsync(t, id, forUpdate: true);
                http.User.Demand(Permissions.RepairWork, j.BranchId);
                if (j.Status is not ("in_progress" or "testing"))
                    throw new KiteException(ErrorCodes.RepairState, $"Parts are fitted while the repair is in progress or being tested; the job is {RepairCore.Words(j.Status)}.", 409);
                var part = await t.QuerySingleOrDefaultAsync<(string Type, string Tracking, string Name)>("""
                    SELECT p.product_type, p.tracking, p.name_en FROM catalog.variant v JOIN catalog.product p ON p.id = v.product_id WHERE v.id = @VariantId
                    """, new { req.VariantId });
                if (part.Type is null) throw new KiteException(ErrorCodes.Validation, "That part is not in the catalog.", 400, "variantId");
                if (part.Type is "service" or "device" || part.Tracking == "imei")
                    throw new KiteException(ErrorCodes.Validation, "A repair uses spare parts, accessories or consumables.", 400, "variantId");
                var uid = Uid(http);
                var lineId = t.NewId();
                var biz = await BusinessInfo.LoadAsync(t);
                DeviceUnitRow? unit = null;
                Guid warehouse;
                decimal qty;
                if (part.Tracking == "serial")
                {
                    // A serial-tracked part (13.4) is one device unit, scanned by its serial number.
                    if (string.IsNullOrWhiteSpace(req.Serial)) throw new KiteException(ErrorCodes.Validation, $"Scan the serial number of the {part.Name}.", 400, "serial");
                    if (req.Qty != 1) throw new KiteException(ErrorCodes.Validation, "A serial-tracked part is fitted one at a time.", 400, "qty");
                    unit = await DeviceUnits.FindAsync(t, new ScannedCode("serial", req.Serial.Trim().ToUpperInvariant()));
                    if (unit is null || unit.VariantId != req.VariantId)
                        throw new KiteException(ErrorCodes.DeviceNotFound, $"No {part.Name} with serial {req.Serial.Trim()} is in stock.", 404, "serial");
                    if (unit.BranchId != j.BranchId || unit.Status != "in_stock")
                        throw new KiteException(ErrorCodes.DeviceNotAvailable, $"That {part.Name} is {unit.Status.Replace('_', ' ')} at {unit.BranchCode}, not in stock here.", 409, "serial");
                    warehouse = unit.WarehouseId;
                    qty = 1;
                }
                else
                {
                    if (req.Qty <= 0 || req.Qty != decimal.Round(req.Qty, 3)) throw new KiteException(ErrorCodes.Validation, "The quantity is above zero (at most 3 decimals).", 400, "qty");
                    warehouse = req.WarehouseId ?? await RepairParts.WarehouseAsync(t, j.BranchId);
                    qty = req.Qty;
                }
                // The job's own reservation first, then free stock not waited for by older requests (13.4).
                var keep = await RepairParts.TakeForFittingAsync(t, j, warehouse, req.VariantId, qty, uid);
                Guid movementId;
                decimal cost, qtyAfter;
                if (unit is not null)
                {
                    var (mid, value) = await DeviceUnits.MoveAsync(t, unit, "repair_issue", -1, null, "repair_job", id, lineId, j.Number);
                    await DeviceUnits.ApplyEventAsync(t, unit.Id, "fitted", "fitted", docType: "repair_job", docId: id, details: new { job = j.Number });
                    movementId = mid;
                    cost = -value;
                    qtyAfter = await t.ExecuteScalarAsync<decimal>("SELECT qty FROM inv.stock_balance WHERE warehouse_id = @warehouse AND variant_id = @VariantId",
                        new { warehouse, req.VariantId });
                }
                else
                {
                    var m = await t.QuerySingleOrDefaultAsync<(Guid Id, decimal Value, decimal QtyAfter)>("""
                        INSERT INTO inv.stock_movement (id, business_id, branch_id, warehouse_id, variant_id, movement_type, qty, source_doc_type, source_doc_id,
                                                        source_line_id, reason, created_by, origin_server_id)
                        VALUES (@mid, @b, @BranchId, @warehouse, @VariantId, 'repair_issue', @negQty, 'repair_job', @id, @lineId, @number, @uid,
                                (SELECT id FROM core.server WHERE is_self))
                        RETURNING id, value, qty_after
                        """, new { mid = t.NewId(), b = biz.Id, j.BranchId, warehouse, req.VariantId, negQty = -qty, id, lineId, number = j.Number, uid });
                    if (m.QtyAfter < 0) throw new KiteException(ErrorCodes.InsufficientStock, $"Not enough {part.Name} in stock: {m.QtyAfter + qty:0.###} left.", 409, "qty");
                    movementId = m.Id;
                    cost = -m.Value;
                    qtyAfter = m.QtyAfter;
                }
                if (qtyAfter < keep)
                    throw new KiteException(ErrorCodes.InsufficientStock,
                        $"Only {Math.Max(0m, qtyAfter + qty - keep):0.###} {part.Name} can be used: the rest is reserved for other repairs or waited for by older jobs.", 409, "qty");
                if (cost <= 0) throw new KiteException(ErrorCodes.Validation, "That part has no cost in stock yet, so it cannot go into the repair's cost.", 400, "variantId");
                // Stock balances are all locked before the journal takes its number (ADR 54): the part being fitted is counted in.
                await RepairParts.ReconcileAsync(t, j, uid, (req.VariantId, qty));
                var journal = await Posting.PostAsync(t, biz.Id, j.BranchId, PostingRuleTemplates.RepairParts,
                    new PostingFacts().Add("parts_by_product_type", part.Type, cost), BusinessDay.Today(biz.TimeZone, t.Clock), "repair_part", lineId,
                    $"Repair {j.Number}: {part.Name} fitted");
                await t.ExecuteAsync("""
                    INSERT INTO repair.repair_line (id, business_id, branch_id, job_id, kind, variant_id, product_type, qty, cost, stock_movement_id, technician_id,
                                                    journal_id, note, device_unit_id, serial, created_by, origin_server_id)
                    VALUES (@lineId, @b, @BranchId, @id, 'part', @VariantId, @type, @qty, @cost, @movementId, @tech, @journal, @note, @unitId, @serial, @uid,
                            (SELECT id FROM core.server WHERE is_self))
                    """, new { lineId, b = biz.Id, j.BranchId, id, req.VariantId, type = part.Type, qty, cost, movementId, tech = j.TechnicianId ?? uid, journal,
                               note = Clean(req.Note), unitId = unit?.Id, serial = unit?.Serial, uid });
                await t.AuditAsync(new AuditEntry("repair.part_fitted", "repair_job", id, After: Json.Obj(new { j.Number, part = part.Name, qty, cost, serial = unit?.Serial }), BranchId: j.BranchId));
                return await DetailAsync(t, http, id);
            }, ct)))).RequirePermission(Permissions.RepairWork).RequireModule("repair");

        // A fitted part that was not used goes back to stock at what it went out at (a serial part as its own unit).
        g.MapPost("/parts/{lineId:guid}/return", async (Guid id, Guid lineId, PartReturnRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await PosEndpoints.WithRetryAsync(() => db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var j = await RepairCore.LoadAsync(t, id, forUpdate: true);
                if (!http.User.HasIn(Permissions.RepairWork, j.BranchId)) http.User.Demand(Permissions.RepairCancel, j.BranchId);
                if (j.Status == "delivered") throw new KiteException(ErrorCodes.RepairState, "The job was delivered.", 409);
                var f = await FittedAsync(t, id, lineId);
                if (f.WrittenOff) throw new KiteException(ErrorCodes.RepairState, "That part was written off.", 409);
                var cost = RepairMath.ReturnCost(f.Qty, f.Cost, f.ReturnedQty, f.ReturnedCost, req.Qty);
                var biz = await BusinessInfo.LoadAsync(t);
                var uid = Uid(http);
                var retId = t.NewId();
                Guid mid;
                if (f.DeviceUnitId is { } unitId)
                {
                    var u = await DeviceUnits.LoadAsync(t, unitId);
                    var (movement, value) = await DeviceUnits.MoveAsync(t, u, "repair_return", 1, null, "repair_job", id, retId, j.Number);
                    await DeviceUnits.ApplyEventAsync(t, unitId, "unfitted", "in_stock", docType: "repair_job", docId: id, details: new { job = j.Number });
                    if (value != cost) throw new KiteException(ErrorCodes.Validation, "The part's cost changed while it was fitted.", 409);
                    mid = movement;
                }
                else
                {
                    var warehouse = await t.ExecuteScalarAsync<Guid>("SELECT warehouse_id FROM inv.stock_movement WHERE id = @m", new { m = f.MovementId });
                    mid = await t.ExecuteScalarAsync<Guid>("""
                        INSERT INTO inv.stock_movement (id, business_id, branch_id, warehouse_id, variant_id, movement_type, qty, value, source_doc_type, source_doc_id,
                                                        source_line_id, reason, created_by, origin_server_id)
                        VALUES (@mid, @b, @BranchId, @warehouse, @VariantId, 'repair_return', @Qty, @cost, 'repair_job', @id, @retId, @number, @uid,
                                (SELECT id FROM core.server WHERE is_self))
                        RETURNING id
                        """, new { mid = t.NewId(), b = biz.Id, j.BranchId, warehouse, f.VariantId, req.Qty, cost, id, retId, number = j.Number, uid });
                }
                // Stock balances are all locked before the journal takes its number (ADR 54): the returned part is counted in.
                await RepairParts.ReconcileAsync(t, j, uid, (f.VariantId, -req.Qty));
                var journal = await Posting.PostAsync(t, biz.Id, j.BranchId, PostingRuleTemplates.RepairParts,
                    new PostingFacts().Add("parts_by_product_type", f.ProductType, -cost), BusinessDay.Today(biz.TimeZone, t.Clock), "repair_part", retId,
                    $"Repair {j.Number}: part returned to stock");
                await t.ExecuteAsync("""
                    INSERT INTO repair.repair_line (id, business_id, branch_id, job_id, kind, variant_id, product_type, qty, cost, stock_movement_id, return_of_id,
                                                    journal_id, note, device_unit_id, serial, created_by, origin_server_id)
                    VALUES (@retId, @b, @BranchId, @id, 'part', @VariantId, @ProductType, @negQty, @negCost, @mid, @lineId, @journal, @note, @DeviceUnitId, @Serial, @uid,
                            (SELECT id FROM core.server WHERE is_self))
                    """, new { retId, b = biz.Id, j.BranchId, id, f.VariantId, f.ProductType, negQty = -req.Qty, negCost = -cost, mid, lineId, journal,
                               note = Clean(req.Note), f.DeviceUnitId, f.Serial, uid });
                await t.AuditAsync(new AuditEntry("repair.part_returned", "repair_job", id, After: Json.Obj(new { j.Number, req.Qty, cost }), BranchId: j.BranchId));
                return await DetailAsync(t, http, id);
            }, ct)))).RequirePermission(Permissions.RepairView).RequireModule("repair");

        // A fitted part, or outside work, that cannot be used or charged: written off with a reason (13.5, 13.7).
        g.MapPost("/parts/{lineId:guid}/write-off", async (Guid id, Guid lineId, PartWriteOffRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await PosEndpoints.WithRetryAsync(() => db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var j = await RepairCore.LoadAsync(t, id, forUpdate: true);
                http.User.Demand(Permissions.RepairCancel, j.BranchId);
                if (j.Status == "delivered") throw new KiteException(ErrorCodes.RepairState, "The job was delivered.", 409);
                if (string.IsNullOrWhiteSpace(req.Reason)) throw new KiteException(ErrorCodes.Validation, "Say why it is written off.", 400, "reason");
                var external = await t.QuerySingleOrDefaultAsync<(Guid Id, decimal Cost, bool Reversed)>("""
                    SELECT l.id, l.cost, EXISTS (SELECT 1 FROM repair.repair_line r WHERE r.return_of_id = l.id) FROM repair.repair_line l
                     WHERE l.id = @lineId AND l.job_id = @id AND l.kind = 'external' AND l.cost > 0
                    """, new { lineId, id });
                decimal amount;
                Guid? unitToWriteOff = null;
                if (external.Id != Guid.Empty)
                {
                    if (external.Reversed) throw new KiteException(ErrorCodes.RepairState, "That outside work was reversed.", 409);
                    if (await t.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM repair.wip_clearing WHERE line_id = @lineId)", new { lineId }))
                        throw new KiteException(ErrorCodes.RepairState, "That outside work was already written off.", 409);
                    amount = external.Cost;
                }
                else
                {
                    var f = await FittedAsync(t, id, lineId);
                    if (f.WrittenOff) throw new KiteException(ErrorCodes.RepairState, "That part was already written off.", 409);
                    amount = f.Cost - f.ReturnedCost;
                    if (f.DeviceUnitId is { } u && f.ReturnedQty == 0) unitToWriteOff = u;
                }
                if (amount <= 0) throw new KiteException(ErrorCodes.RepairState, "Nothing of that is left on the job.", 409);
                if (unitToWriteOff is { } unitId)
                    await DeviceUnits.ApplyEventAsync(t, unitId, "written_off", "written_off", docType: "repair_job", docId: id, details: new { job = j.Number, reason = req.Reason.Trim() });
                var biz = await BusinessInfo.LoadAsync(t);
                var cid = t.NewId();
                var journal = await Posting.PostAsync(t, biz.Id, j.BranchId, PostingRuleTemplates.RepairCost, new PostingFacts().Set("written_off", amount),
                    BusinessDay.Today(biz.TimeZone, t.Clock), "repair_wip", cid, $"Repair {j.Number}: {(external.Id != Guid.Empty ? "outside work" : "part")} written off");
                await t.ExecuteAsync("""
                    INSERT INTO repair.wip_clearing (id, business_id, branch_id, job_id, kind, amount, line_id, reason, journal_id, created_by)
                    VALUES (@cid, @b, @BranchId, @id, 'written_off', @amount, @lineId, @reason, @journal, @uid)
                    """, new { cid, b = biz.Id, j.BranchId, id, amount, lineId, reason = req.Reason.Trim(), journal, uid = Uid(http) });
                await t.AuditAsync(new AuditEntry("repair.part_written_off", "repair_job", id, After: Json.Obj(new { j.Number, amount, req.Reason }), BranchId: j.BranchId));
                return await DetailAsync(t, http, id);
            }, ct)))).RequirePermission(Permissions.RepairCancel).RequireModule("repair");

        g.MapPost("/labour", async (Guid id, LabourRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var j = await RepairCore.LoadAsync(t, id, forUpdate: true);
                http.User.Demand(Permissions.RepairWork, j.BranchId);
                if (req.Minutes is < 1 or > 6000) throw new KiteException(ErrorCodes.Validation, "Labour is 1 to 6000 minutes.", 400, "minutes");
                var tech = req.TechnicianId ?? j.TechnicianId ?? Uid(http);
                if (req.TechnicianId is { } tid) await CheckTechnicianAsync(t, tid, j.BranchId);
                await t.ExecuteAsync("""
                    INSERT INTO repair.repair_line (id, business_id, branch_id, job_id, kind, minutes, technician_id, note, created_by, origin_server_id)
                    VALUES (@lid, @b, @BranchId, @id, 'labour', @Minutes, @tech, @note, @uid, (SELECT id FROM core.server WHERE is_self))
                    """, new { lid = t.NewId(), b = j.BusinessId, j.BranchId, id, req.Minutes, tech, note = Clean(req.Note), uid = Uid(http) });
                return await DetailAsync(t, http, id);
            }, ct))).RequirePermission(Permissions.RepairWork).RequireModule("repair");
    }

    private static async Task<RepairEstimate> EstimateOfAsync(DbTx t, RepairJob j, Guid estimateId)
    {
        var e = await t.QuerySingleOrDefaultAsync<RepairEstimate>("""
            SELECT id, job_id AS JobId, estimate_no AS EstimateNo, status, net_total AS NetTotal, tax_total AS TaxTotal, total, approval_method AS ApprovalMethod, version
              FROM repair.estimate WHERE id = @estimateId AND job_id = @jobId FOR UPDATE
            """, new { estimateId, jobId = j.Id }) ?? throw new KiteException(ErrorCodes.NotFound, "That estimate is not on this job.", 404);
        var top = await t.ExecuteScalarAsync<int>("SELECT max(estimate_no) FROM repair.estimate WHERE job_id = @jobId", new { jobId = j.Id });
        if (e.EstimateNo != top && e.Status is "draft" or "sent")
            throw new KiteException(ErrorCodes.RepairEstimate, $"Version {e.EstimateNo} is not the current estimate.", 409);
        return e;
    }

    private sealed class FittedPart
    {
        public Guid? DeviceUnitId { get; set; }
        public string? Serial { get; set; }
        public Guid Id { get; set; }
        public Guid VariantId { get; set; }
        public string ProductType { get; set; } = "";
        public decimal Qty { get; set; }
        public decimal Cost { get; set; }
        public Guid MovementId { get; set; }
        public decimal ReturnedQty { get; set; }
        public decimal ReturnedCost { get; set; }
        public bool WrittenOff { get; set; }
    }

    private static async Task<FittedPart> FittedAsync(DbTx t, Guid jobId, Guid lineId) =>
        await t.QuerySingleOrDefaultAsync<FittedPart>("""
            SELECT l.id, l.variant_id AS VariantId, l.product_type AS ProductType, l.qty, l.cost, l.stock_movement_id AS MovementId,
                   l.device_unit_id AS DeviceUnitId, l.serial,
                   coalesce((SELECT -sum(r.qty) FROM repair.repair_line r WHERE r.return_of_id = l.id), 0) AS ReturnedQty,
                   coalesce((SELECT -sum(r.cost) FROM repair.repair_line r WHERE r.return_of_id = l.id), 0) AS ReturnedCost,
                   EXISTS (SELECT 1 FROM repair.wip_clearing w WHERE w.line_id = l.id) AS WrittenOff
              FROM repair.repair_line l WHERE l.id = @lineId AND l.job_id = @jobId AND l.kind = 'part' AND l.qty > 0
            """, new { lineId, jobId }) ?? throw new KiteException(ErrorCodes.NotFound, "That part was not fitted on this job.", 404);
}
