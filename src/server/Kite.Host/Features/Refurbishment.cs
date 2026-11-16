using Kite.Accounting;
using Kite.Devices;
using Kite.Host.Infrastructure;
using Kite.Persistence;
using Kite.Platform;

namespace Kite.Host.Features;

public sealed record InspectionRequest(Dictionary<string, string?> Answers, string Outcome, string Condition, Guid? GradeId = null, decimal? SellingPrice = null,
    string? Note = null, Guid? TechnicianId = null, int? Version = null);
public sealed record RefurbOrderRequest(Guid? TechnicianId, string? Problem, string? WorkDone, int Version);
public sealed record RefurbCostRequest(string Kind, Guid? VariantId = null, decimal? Qty = null, int? Minutes = null, Guid? TechnicianId = null,
    string? Vendor = null, string? Reference = null, decimal? Amount = null, string? Note = null);
public sealed record RefurbReverseRequest(string Reason);
public sealed record RefurbCompleteRequest(string Condition, Guid? GradeId, decimal? SellingPrice = null, int? WarrantyDays = null, string? WorkDone = null, int? Version = null);

/// <summary>
/// Inspections (12.4, 11.3) and refurbishment (12.2, 12.6): the checklist decides what happens to a device, and every
/// part, hour of labour and piece of outside work is added to that one device's cost, in the stock ledger and the
/// journals, the moment it is recorded.
/// </summary>
public static class RefurbishmentEndpoints
{
    private const string OrderSelect = """
        SELECT r.id, r.number, r.status, r.branch_id AS "branchId", r.device_unit_id AS "unitId", r.inspection_id AS "inspectionId", r.technician_id AS "technicianId",
               tu.display_name_en AS "technicianName", r.problem, r.work_done AS "workDone", r.opened_at AS "openedAt", r.completed_at AS "completedAt",
               r.result_condition AS "resultCondition", g.code AS "gradeCode", r.selling_price AS "sellingPrice", r.warranty_days AS "warrantyDays", r.version,
               (SELECT coalesce(sum(c.amount), 0) FROM refurb.refurb_cost c WHERE c.refurb_order_id = r.id) AS "costTotal",
               u.status AS "unitStatus", u.condition AS "unitCondition", u.accumulated_cost AS "unitCost", v.sku,
               (SELECT x.value FROM device.imei_registry x WHERE x.device_unit_id = u.id ORDER BY x.kind, x.slot LIMIT 1) AS identifier,
               p.name_en || coalesce(' ' || nullif(v.colour_en, ''), '') || coalesce(' ' || v.storage_gb::text || 'GB', '') AS "descriptionEn",
               p.name_ar || coalesce(' ' || nullif(v.colour_ar, ''), '') || coalesce(' ' || v.storage_gb::text || 'GB', '') AS "descriptionAr"
          FROM refurb.refurb_order r JOIN device.device_unit u ON u.id = r.device_unit_id
          JOIN catalog.variant v ON v.id = u.variant_id JOIN catalog.product p ON p.id = v.product_id
          LEFT JOIN iam."user" tu ON tu.id = r.technician_id LEFT JOIN device.grade g ON g.id = r.grade_id
        """;

    public static void Map(WebApplication app)
    {
        // ------------------------------------------------------------------ inspections
        app.MapGet("/api/v1/inspections/queue", async (Guid? branchId, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            http.User.Demand(Permissions.DeviceInspect, branchId);
            return Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
                (await t.QueryAsync<DeviceUnitRow>(DeviceUnits.Select + """
                     WHERE u.status IN ('under_inspection','returned_inspection') AND (@branchId::uuid IS NULL OR u.branch_id = @branchId)
                     ORDER BY u.updated_at
                    """, new { branchId }))
                .Select(u => new { u.Id, u.BranchCode, u.Sku, u.DescriptionEn, u.DescriptionAr, u.Status, u.Condition, u.Imei1, u.Serial, u.AccumulatedCost, u.ReceivedAt }), ct));
        }).RequirePermission(Permissions.DeviceInspect).RequireModule("refurb");

        app.MapPost("/api/v1/devices/{id:guid}/inspections", async (Guid id, InspectionRequest req, HttpContext http, KiteDb db, LicenceService licence, CancellationToken ct) =>
        {
            await licence.RequireTransactAsync(ct);
            var r = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => InspectAsync(t, http, id, req), ct);
            return Results.Ok(r);
        }).RequirePermission(Permissions.DeviceInspect).RequireModule("refurb");

        app.MapGet("/api/v1/inspections/{id:guid}", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var row = await t.RowsAsync("""
                    SELECT i.id, i.number, i.device_unit_id AS "unitId", i.reason, i.results::text AS results, i.battery_health AS "batteryHealth",
                           i.suggested_outcome AS "suggestedOutcome", i.outcome, i.result_condition AS "resultCondition", g.code AS "gradeCode", i.selling_price AS "sellingPrice",
                           i.note, i.inspected_at AS "inspectedAt", us.display_name_en AS "inspectedBy", i.branch_id AS "branchId"
                      FROM device.inspection i LEFT JOIN device.grade g ON g.id = i.grade_id JOIN iam."user" us ON us.id = i.inspected_by WHERE i.id = @id
                    """, new { id });
                if (row.Count == 0) throw new KiteException(ErrorCodes.NotFound, "Inspection not found in your branches.", 404);
                http.User.Demand(Permissions.DeviceView, (Guid)row[0]["branchId"]!);
                return row[0];
            }, ct))).RequirePermission(Permissions.DeviceView).RequireModule("refurb");

        // ------------------------------------------------------------------ refurbishment orders
        var g = app.MapGroup("/api/v1/refurbishments");

        g.MapGet("", async (string? status, Guid? technicianId, Guid? branchId, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            http.User.Demand(Permissions.DeviceView, branchId);
            return Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.RowsAsync(OrderSelect + """
                 WHERE (@status::text IS NULL OR r.status = @status) AND (@technicianId::uuid IS NULL OR r.technician_id = @technicianId)
                   AND (@branchId::uuid IS NULL OR r.branch_id = @branchId)
                 ORDER BY r.status = 'open' DESC, r.opened_at DESC LIMIT 500
                """, new { status, technicianId, branchId }), ct));
        }).RequirePermission(Permissions.DeviceView).RequireModule("refurb");

        g.MapGet("/{id:guid}", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => OrderAsync(t, http, id), ct)))
            .RequirePermission(Permissions.DeviceView).RequireModule("refurb");

        g.MapPut("/{id:guid}", async (Guid id, RefurbOrderRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var o = await OpenOrderAsync(t, id);
                http.User.Demand(Permissions.DeviceRefurbish, o.BranchId);
                if (req.TechnicianId is { } tech) await CheckTechnicianAsync(t, tech, o.BranchId);
                var n = await t.ExecuteAsync("""
                    UPDATE refurb.refurb_order SET technician_id = @TechnicianId, problem = @Problem, work_done = @WorkDone, updated_by = @uid
                     WHERE id = @id AND version = @Version
                    """, new { id, req.TechnicianId, req.Problem, req.WorkDone, uid = http.User.UserIdOf(), req.Version });
                if (n == 0) throw new KiteException(ErrorCodes.VersionMismatch, "Someone else changed this order. Reload and try again.", 412);
                await t.AuditAsync(new AuditEntry("refurb.order_changed", "refurb_order", id, After: Json.Obj(req), BranchId: o.BranchId));
            }, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.DeviceRefurbish).RequireModule("refurb");

        g.MapPost("/{id:guid}/costs", async (Guid id, RefurbCostRequest req, HttpContext http, KiteDb db, LicenceService licence, CancellationToken ct) =>
        {
            await licence.RequireTransactAsync(ct);
            var r = await WithRetryAsync(() => db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => AddCostAsync(t, http, id, req), ct));
            return Results.Ok(r);
        }).RequirePermission(Permissions.DeviceRefurbish).RequireModule("refurb");

        g.MapPost("/{id:guid}/costs/{costId:guid}/reverse", async (Guid id, Guid costId, RefurbReverseRequest req, HttpContext http, KiteDb db, LicenceService licence, CancellationToken ct) =>
        {
            await licence.RequireTransactAsync(ct);
            var r = await WithRetryAsync(() => db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => ReverseCostAsync(t, http, id, costId, req), ct));
            return Results.Ok(r);
        }).RequirePermission(Permissions.DeviceRefurbish).RequireModule("refurb");

        g.MapPost("/{id:guid}/complete", async (Guid id, RefurbCompleteRequest req, HttpContext http, KiteDb db, LicenceService licence, CancellationToken ct) =>
        {
            await licence.RequireTransactAsync(ct);
            var r = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => CompleteAsync(t, http, id, req), ct);
            return Results.Ok(r);
        }).RequirePermission(Permissions.DeviceRefurbish).RequireModule("refurb");
    }

    private static async Task<T> WithRetryAsync<T>(Func<Task<T>> work)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return await work(); }
            catch (Npgsql.PostgresException e) when (e.SqlState is "40P01" or "40001" && attempt < 4) { await Task.Delay(Random.Shared.Next(10, 60) * attempt); }
        }
    }

    /// <summary>
    /// Inspects a device waiting for inspection (12.4): the checklist must be complete; the inspector's decision may
    /// differ from the checklist's suggestion only with a note. The unit moves on (restock, refurbish, for parts,
    /// supplier return) in the same transaction, a brand-new device whose condition changes moves its cost to the used
    /// inventory, and a refurbish decision opens the refurbishment order.
    /// </summary>
    private static async Task<object> InspectAsync(DbTx t, HttpContext http, Guid id, InspectionRequest req)
    {
        var u = await DeviceUnits.LoadAsync(t, id);
        http.User.Demand(Permissions.DeviceInspect, u.BranchId);
        if (req.Version is { } v && v != u.Version) throw new KiteException(ErrorCodes.VersionMismatch, "Someone else changed this device. Reload and try again.", 412);
        if (u.Status is not ("under_inspection" or "returned_inspection"))
            throw new KiteException(ErrorCodes.DeviceTransition, $"{u.Identifier} is {u.Status.Replace('_', ' ')}, not waiting for inspection.", 409);
        if (!DeviceMath.Outcomes.Contains(req.Outcome)) throw new KiteException(ErrorCodes.Validation, "Choose restock, refurbish, for parts or supplier return.", 400, "outcome");
        var allowed = DeviceMath.AllowedConditions(req.Outcome, u.Condition);
        if (!allowed.Contains(req.Condition))
            throw new KiteException(ErrorCodes.Validation, $"After this decision the device is {string.Join(", ", allowed.Select(c => c.Replace('_', ' ')))}.", 400, "condition");
        if (req.SellingPrice is not null && !http.User.HasIn(Permissions.DevicePrice, u.BranchId))
            throw new KiteException(ErrorCodes.Forbidden, "Setting a device's price needs the DEVICE.PRICE permission.", 403, "sellingPrice");
        if (req.SellingPrice is < 0) throw new KiteException(ErrorCodes.Validation, "The price cannot be negative.", 400, "sellingPrice");
        if (req.GradeId is { } grade) await DeviceUnits.CheckGradeAsync(t, grade, u.CategoryId, "gradeId");
        if (req.TechnicianId is { } tech) await CheckTechnicianAsync(t, tech, u.BranchId);

        var (checklistId, items) = await DeviceEndpoints.ChecklistForAsync(t, u.CategoryId);
        var result = DeviceMath.Evaluate(items, req.Answers ?? [], u.Condition);
        if (!result.Complete)
        {
            var labels = items.Where(i => result.Missing.Contains(i.Code)).Select(i => i.LabelEn);
            throw new KiteException(ErrorCodes.InspectionIncomplete, $"Answer every checklist item first: {string.Join("; ", labels)}.", 400, "answers");
        }
        var note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();
        if (req.Outcome != result.SuggestedOutcome && note is null)
            throw new KiteException(ErrorCodes.InspectionIncomplete, $"The checklist suggests {result.SuggestedOutcome.Replace('_', ' ')}; say why you decided otherwise.", 400, "note");

        var biz = await BusinessInfo.LoadAsync(t);
        var date = BusinessDay.Today(biz.TimeZone, t.Clock);
        var fy = await FiscalPeriods.FiscalYearOfAsync(t, biz.Id, date);
        var inspectionId = t.NewId();
        var number = await DocNumbers.NextAsync(t, biz.Id, u.BranchId, "device_inspection", fy, "INS");
        var toStatus = req.Outcome switch { "restock" => "in_stock", "refurbish" => "refurbishing", "for_parts" => "for_parts", _ => "supplier_return" };
        var eventId = await DeviceUnits.ApplyEventAsync(t, id, "inspected", toStatus, req.Condition, req.GradeId,
            req.SellingPrice ?? (req.Condition == u.Condition ? u.SellingPrice : null), null, "device_inspection", inspectionId,
            new { number, outcome = req.Outcome, suggested = result.SuggestedOutcome, failed = result.Failed, battery = result.BatteryHealth });
        await t.ExecuteAsync("""
            INSERT INTO device.inspection (id, business_id, branch_id, device_unit_id, number, reason, checklist_id, results, battery_health, suggested_outcome,
                                           outcome, result_condition, grade_id, selling_price, note, event_id, inspected_by, origin_server_id)
            VALUES (@inspectionId, @b, @BranchId, @id, @number, @reason, @checklistId, @results::jsonb, @battery, @suggested, @Outcome, @Condition, @GradeId,
                    @SellingPrice, @note, @eventId, @uid, (SELECT id FROM core.server WHERE is_self))
            """, new { inspectionId, b = biz.Id, u.BranchId, id, number, reason = u.Status == "returned_inspection" ? "return" : "intake", checklistId,
                       results = System.Text.Json.JsonSerializer.Serialize(result.Answers, Json.Web), battery = (short?)result.BatteryHealth,
                       suggested = result.SuggestedOutcome, req.Outcome, req.Condition, req.GradeId, req.SellingPrice, note, eventId, uid = http.User.UserIdOf() });
        await DeviceUnits.PostReclassAsync(t, biz.Id, u.BranchId, u.ProductType, u.Condition, req.Condition, u.AccumulatedCost, date,
            "device_inspection", inspectionId, $"Inspection {number} ({u.Identifier}): {DeviceUnits.ConditionEn(u.Condition).ToLowerInvariant()} to {DeviceUnits.ConditionEn(req.Condition).ToLowerInvariant()}");

        Guid? orderId = null;
        string? orderNumber = null;
        if (req.Outcome == "refurbish")
        {
            orderId = t.NewId();
            orderNumber = await DocNumbers.NextAsync(t, biz.Id, u.BranchId, "refurb_order", fy, "RF");
            var problem = string.Join("; ", items.Where(i => result.Failed.Contains(i.Code)).Select(i => i.LabelEn));
            await t.ExecuteAsync("""
                INSERT INTO refurb.refurb_order (id, business_id, branch_id, number, device_unit_id, inspection_id, technician_id, problem, opened_by, origin_server_id)
                VALUES (@orderId, @b, @BranchId, @orderNumber, @id, @inspectionId, @TechnicianId, @problem, @uid, (SELECT id FROM core.server WHERE is_self))
                """, new { orderId, b = biz.Id, u.BranchId, orderNumber, id, inspectionId, req.TechnicianId, problem = problem.Length == 0 ? note : problem, uid = http.User.UserIdOf() });
        }
        await t.AuditAsync(new AuditEntry("device.inspected", "device_unit", id,
            After: Json.Obj(new { number, req.Outcome, suggested = result.SuggestedOutcome, req.Condition, failed = result.Failed, note, orderNumber }), BranchId: u.BranchId));
        await t.PublishAsync("device.inspected", "device_unit", id, u.Version + 1, Json.Obj(new { number, req.Outcome, req.Condition, orderNumber }), u.BranchId);
        return new { id = inspectionId, number, outcome = req.Outcome, suggestedOutcome = result.SuggestedOutcome, failed = result.Failed, refurbOrderId = orderId, refurbOrderNumber = orderNumber };
    }

    private sealed record OrderRow(Guid Id, string Number, Guid BranchId, Guid UnitId, Guid? TechnicianId, string Status, int Version);

    private static async Task<OrderRow> OpenOrderAsync(DbTx t, Guid id)
    {
        var o = await t.QuerySingleOrDefaultAsync<OrderRow>("""
            SELECT id, number, branch_id AS BranchId, device_unit_id AS UnitId, technician_id AS TechnicianId, status, version FROM refurb.refurb_order WHERE id = @id FOR UPDATE
            """, new { id }) ?? throw new KiteException(ErrorCodes.NotFound, "Refurbishment order not found in your branches.", 404);
        if (o.Status != "open") throw new KiteException(ErrorCodes.RefurbOrderState, $"Refurbishment {o.Number} is completed.", 409);
        return o;
    }

    private static async Task CheckTechnicianAsync(DbTx t, Guid userId, Guid branchId)
    {
        var ok = await t.ExecuteScalarAsync<bool>("""
            SELECT EXISTS (SELECT 1 FROM iam."user" u JOIN iam.user_branch_role ubr ON ubr.user_id = u.id
                            WHERE u.id = @userId AND u.status = 'active' AND (ubr.branch_id IS NULL OR ubr.branch_id = @branchId))
            """, new { userId, branchId });
        if (!ok) throw new KiteException(ErrorCodes.Validation, "That technician is not an active user of this branch.", 400, "technicianId");
    }

    private static async Task<object> OrderAsync(DbTx t, HttpContext http, Guid id)
    {
        var rows = await t.RowsAsync(OrderSelect + " WHERE r.id = @id", new { id });
        if (rows.Count == 0) throw new KiteException(ErrorCodes.NotFound, "Refurbishment order not found in your branches.", 404);
        http.User.Demand(Permissions.DeviceView, (Guid)rows[0]["branchId"]!);
        var costs = await t.RowsAsync($"""
            SELECT c.id, c.kind, c.amount, c.qty, c.unit_cost AS "unitCost", c.labour_minutes AS "minutes", c.labour_rate AS "rate", c.vendor, c.reference,
                   c.note, c.reversal_of_id AS "reversalOf", EXISTS (SELECT 1 FROM refurb.refurb_cost x WHERE x.reversal_of_id = c.id) AS reversed,
                   v.sku AS "partSku", {ItemPricing.DescriptionEn} AS "partDescriptionEn", {ItemPricing.DescriptionAr} AS "partDescriptionAr",
                   tu.display_name_en AS "technicianName", cu.display_name_en AS "createdBy", c.created_at AS "createdAt"
              FROM refurb.refurb_cost c
              LEFT JOIN catalog.variant v ON v.id = c.part_variant_id LEFT JOIN catalog.product p ON p.id = v.product_id
              LEFT JOIN iam."user" tu ON tu.id = c.technician_id JOIN iam."user" cu ON cu.id = c.created_by
             WHERE c.refurb_order_id = @id ORDER BY c.created_at, c.id
            """, new { id });
        return new { order = rows[0], costs };
    }

    /// <summary>
    /// Adds one cost to the device being refurbished (12.2): a part issued from stock at its average cost, labour at
    /// the business rate, or outside work at its amount. One transaction: the part's stock movement, the unit's cost
    /// movement (which raises its accumulated cost), the history event, the cost row and the journal.
    /// </summary>
    private static async Task<object> AddCostAsync(DbTx t, HttpContext http, Guid id, RefurbCostRequest req)
    {
        var o = await OpenOrderAsync(t, id);
        http.User.Demand(Permissions.DeviceRefurbish, o.BranchId);
        var u = await DeviceUnits.LoadAsync(t, o.UnitId);
        if (u.Status != "refurbishing") throw new KiteException(ErrorCodes.DeviceTransition, $"{u.Identifier} is {u.Status.Replace('_', ' ')}, not being refurbished.", 409);
        var biz = await BusinessInfo.LoadAsync(t);
        var settings = await DeviceUnits.SettingsAsync(t, biz.Id);
        var costId = t.NewId();
        var facts = new PostingFacts();
        decimal amount;
        Guid? partMovement = null;
        decimal? qty = null, unitCost = null, rate = null;
        int? minutes = null;
        Guid? technician = null;
        string? vendor = null, reference = null;
        switch (req.Kind)
        {
            case "part":
            {
                if (req.VariantId is not { } partId) throw new KiteException(ErrorCodes.Validation, "Choose the part.", 400, "variantId");
                if (req.Qty is not { } q || q <= 0 || q != decimal.Round(q, 3)) throw new KiteException(ErrorCodes.Validation, "The quantity is above zero (at most 3 decimals).", 400, "qty");
                var part = await t.QuerySingleOrDefaultAsync<(string Type, string Tracking, string Status)>("""
                    SELECT p.product_type, p.tracking, v.status FROM catalog.variant v JOIN catalog.product p ON p.id = v.product_id WHERE v.id = @partId
                    """, new { partId });
                if (part.Type is null) throw new KiteException(ErrorCodes.Validation, "That part is not in the catalog.", 400, "variantId");
                if (part.Type is "service" or "device" || part.Tracking != "none")
                    throw new KiteException(ErrorCodes.Validation, "A refurbishment uses spare parts, accessories or consumables counted by quantity.", 400, "variantId");
                var m = await t.QuerySingleOrDefaultAsync<(Guid Id, decimal Value, decimal QtyAfter)>("""
                    INSERT INTO inv.stock_movement (id, business_id, branch_id, warehouse_id, variant_id, movement_type, qty, source_doc_type, source_doc_id,
                                                    source_line_id, reason, created_by, origin_server_id)
                    VALUES (@mid, @b, @BranchId, @WarehouseId, @partId, 'refurb_issue', @negQty, 'refurb_order', @id, @costId, 'refurbishment', @uid,
                            (SELECT id FROM core.server WHERE is_self))
                    RETURNING id, value, qty_after
                    """, new { mid = t.NewId(), b = biz.Id, u.BranchId, u.WarehouseId, partId, negQty = -q, id, costId, uid = http.User.UserIdOf() });
                if (m.QtyAfter < 0) throw new KiteException(ErrorCodes.InsufficientStock, $"Not enough of that part in stock: {m.QtyAfter + q:0.###} left.", 409, "qty");
                amount = -m.Value;
                if (amount <= 0) throw new KiteException(ErrorCodes.Validation, "That part has no cost in stock yet, so it cannot be added to the device's cost.", 400, "variantId");
                partMovement = m.Id;
                qty = q;
                unitCost = decimal.Round(amount / q, 6);
                facts.Add("parts_by_product_type", part.Type, amount);
                break;
            }
            case "labour":
                if (req.Minutes is not (> 0 and <= 6000)) throw new KiteException(ErrorCodes.Validation, "Labour is 1 to 6000 minutes.", 400, "minutes");
                if (req.TechnicianId is { } tech) await CheckTechnicianAsync(t, tech, u.BranchId);
                minutes = req.Minutes;
                rate = settings.LabourRatePerHour;
                amount = DeviceMath.LabourCost(req.Minutes.Value, settings.LabourRatePerHour);
                if (amount <= 0) throw new KiteException(ErrorCodes.Validation, "The labour rate is zero, so labour adds no cost. Set the rate in device settings.", 400, "minutes");
                technician = req.TechnicianId ?? o.TechnicianId ?? http.User.UserIdOf();
                facts.Set("labour", amount);
                break;
            case "external":
                if (string.IsNullOrWhiteSpace(req.Vendor)) throw new KiteException(ErrorCodes.Validation, "Name who did the work.", 400, "vendor");
                if (req.Amount is not { } a || a <= 0 || a != decimal.Round(a, 3)) throw new KiteException(ErrorCodes.Validation, "The amount is above zero, in baisa.", 400, "amount");
                amount = a;
                vendor = req.Vendor.Trim();
                reference = string.IsNullOrWhiteSpace(req.Reference) ? null : req.Reference.Trim();
                facts.Set("external", amount);
                break;
            default:
                throw new KiteException(ErrorCodes.Validation, "A cost is a part, labour or outside work.", 400, "kind");
        }
        facts.Add("cost_by_product_type", u.PostingClass, amount);
        var (unitMovement, _) = await DeviceUnits.MoveAsync(t, u, "unit_cost", 0, amount, "refurb_order", id, costId, req.Kind);
        var eventId = await DeviceUnits.ApplyEventAsync(t, u.Id, "cost_added", null, docType: "refurb_order", docId: id,
            details: new { order = o.Number, kind = req.Kind, amount }, costDelta: amount);
        var date = BusinessDay.Today(biz.TimeZone, t.Clock);
        var journal = await Posting.PostAsync(t, biz.Id, u.BranchId, PostingRuleTemplates.RefurbCost, facts, date, "refurb_cost", costId,
            $"Refurbishment {o.Number} ({u.Identifier}): {req.Kind}");
        var note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();
        await t.ExecuteAsync("""
            INSERT INTO refurb.refurb_cost (id, business_id, branch_id, refurb_order_id, device_unit_id, kind, part_variant_id, qty, unit_cost, labour_minutes, labour_rate,
                                            technician_id, vendor, reference, amount, part_movement_id, unit_movement_id, event_id, journal_id, note, created_by, origin_server_id)
            VALUES (@costId, @b, @BranchId, @id, @unit, @Kind, @VariantId, @qty, @unitCost, @minutes, @rate, @technician, @vendor, @reference, @amount, @partMovement,
                    @unitMovement, @eventId, @journal, @note, @uid, (SELECT id FROM core.server WHERE is_self))
            """, new { costId, b = biz.Id, u.BranchId, id, unit = u.Id, req.Kind, VariantId = req.Kind == "part" ? req.VariantId : null, qty, unitCost, minutes, rate,
                       technician, vendor, reference, amount, partMovement, unitMovement, eventId, journal, note, uid = http.User.UserIdOf() });
        await t.AuditAsync(new AuditEntry("refurb.cost_added", "refurb_order", id, After: Json.Obj(new { o.Number, req.Kind, amount, unit = u.Identifier }), BranchId: u.BranchId));
        await t.PublishAsync("refurb.cost_added", "refurb_order", id, o.Version + 1, Json.Obj(new { o.Number, req.Kind, amount }), u.BranchId);
        return new { id = costId, amount, unitCost = u.AccumulatedCost + amount };
    }

    /// <summary>Takes back a cost recorded by mistake while the order is open: everything posts the other way; a part goes back to stock at what it went out at.</summary>
    private static async Task<object> ReverseCostAsync(DbTx t, HttpContext http, Guid id, Guid costId, RefurbReverseRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Reason)) throw new KiteException(ErrorCodes.Validation, "Say why the cost is taken back.", 400, "reason");
        var o = await OpenOrderAsync(t, id);
        http.User.Demand(Permissions.DeviceRefurbish, o.BranchId);
        var c = await t.QuerySingleOrDefaultAsync<(Guid Id, string Kind, Guid? PartVariantId, decimal? Qty, decimal Amount, Guid? ReversalOf, int? Minutes, decimal? Rate, Guid? Technician, string? Vendor, string? Reference)>("""
            SELECT id, kind, part_variant_id, qty, amount, reversal_of_id, labour_minutes, labour_rate, technician_id, vendor, reference
              FROM refurb.refurb_cost WHERE id = @costId AND refurb_order_id = @id
            """, new { costId, id });
        if (c.Id == Guid.Empty) throw new KiteException(ErrorCodes.NotFound, "That cost is not on this order.", 404);
        if (c.ReversalOf is not null) throw new KiteException(ErrorCodes.Validation, "A reversal cannot itself be taken back.", 400);
        if (await t.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM refurb.refurb_cost WHERE reversal_of_id = @costId)", new { costId }))
            throw new KiteException(ErrorCodes.Conflict, "That cost was already taken back.", 409);
        var u = await DeviceUnits.LoadAsync(t, o.UnitId);
        var biz = await BusinessInfo.LoadAsync(t);
        var revId = t.NewId();
        var facts = new PostingFacts().Add("cost_by_product_type", u.PostingClass, -c.Amount);
        Guid? partMovement = null;
        if (c.Kind == "part")
        {
            var type = await t.ExecuteScalarAsync<string>("SELECT p.product_type FROM catalog.variant v JOIN catalog.product p ON p.id = v.product_id WHERE v.id = @v", new { v = c.PartVariantId });
            partMovement = await t.ExecuteScalarAsync<Guid>("""
                INSERT INTO inv.stock_movement (id, business_id, branch_id, warehouse_id, variant_id, movement_type, qty, value, source_doc_type, source_doc_id,
                                                source_line_id, reason, created_by, origin_server_id)
                VALUES (@mid, @b, @BranchId, @WarehouseId, @PartVariantId, 'refurb_return', @Qty, @Amount, 'refurb_order', @id, @revId, @reason, @uid,
                        (SELECT id FROM core.server WHERE is_self))
                RETURNING id
                """, new { mid = t.NewId(), b = biz.Id, u.BranchId, u.WarehouseId, c.PartVariantId, c.Qty, c.Amount, id, revId, reason = req.Reason.Trim(), uid = http.User.UserIdOf() });
            facts.Add("parts_by_product_type", type, -c.Amount);
        }
        else facts.Set(c.Kind == "labour" ? "labour" : "external", -c.Amount);
        var (unitMovement, _) = await DeviceUnits.MoveAsync(t, u, "unit_cost", 0, -c.Amount, "refurb_order", id, revId, "reversal");
        var eventId = await DeviceUnits.ApplyEventAsync(t, u.Id, "cost_added", null, docType: "refurb_order", docId: id,
            details: new { order = o.Number, kind = c.Kind, amount = -c.Amount, reversal = true, reason = req.Reason.Trim() }, costDelta: -c.Amount);
        var journal = await Posting.PostAsync(t, biz.Id, u.BranchId, PostingRuleTemplates.RefurbCost, facts, BusinessDay.Today(biz.TimeZone, t.Clock), "refurb_cost", revId,
            $"Refurbishment {o.Number} ({u.Identifier}): {c.Kind} taken back");
        await t.ExecuteAsync("""
            INSERT INTO refurb.refurb_cost (id, business_id, branch_id, refurb_order_id, device_unit_id, kind, part_variant_id, qty, labour_minutes, labour_rate, technician_id,
                                            vendor, reference, amount, reversal_of_id, part_movement_id, unit_movement_id, event_id, journal_id, note, created_by, origin_server_id)
            VALUES (@revId, @b, @BranchId, @id, @unit, @Kind, @PartVariantId, @Qty, @Minutes, @Rate, @Technician, @Vendor, @Reference, @neg, @costId, @partMovement,
                    @unitMovement, @eventId, @journal, @reason, @uid, (SELECT id FROM core.server WHERE is_self))
            """, new { revId, b = biz.Id, u.BranchId, id, unit = u.Id, c.Kind, c.PartVariantId, c.Qty, c.Minutes, c.Rate, c.Technician, c.Vendor, c.Reference,
                       neg = -c.Amount, costId, partMovement, unitMovement, eventId, journal, reason = req.Reason.Trim(), uid = http.User.UserIdOf() });
        await t.AuditAsync(new AuditEntry("refurb.cost_reversed", "refurb_order", id, After: Json.Obj(new { o.Number, c.Kind, amount = -c.Amount, reason = req.Reason.Trim() }), BranchId: u.BranchId));
        return new { id = revId, amount = -c.Amount, unitCost = u.AccumulatedCost - c.Amount };
    }

    /// <summary>Completes the order (12.6): the device comes back refurbished (or damaged), with its grade, price and warranty, into stock.</summary>
    private static async Task<object> CompleteAsync(DbTx t, HttpContext http, Guid id, RefurbCompleteRequest req)
    {
        var o = await OpenOrderAsync(t, id);
        http.User.Demand(Permissions.DeviceRefurbish, o.BranchId);
        if (req.Version is { } v && v != o.Version) throw new KiteException(ErrorCodes.VersionMismatch, "Someone else changed this order. Reload and try again.", 412);
        if (req.Condition is not ("refurbished" or "damaged")) throw new KiteException(ErrorCodes.Validation, "A refurbished device comes back refurbished or damaged.", 400, "condition");
        if (req.SellingPrice is not null && !http.User.HasIn(Permissions.DevicePrice, o.BranchId))
            throw new KiteException(ErrorCodes.Forbidden, "Setting a device's price needs the DEVICE.PRICE permission.", 403, "sellingPrice");
        if (req.SellingPrice is < 0) throw new KiteException(ErrorCodes.Validation, "The price cannot be negative.", 400, "sellingPrice");
        if (req.WarrantyDays is < 0) throw new KiteException(ErrorCodes.Validation, "Warranty days cannot be negative.", 400, "warrantyDays");
        var u = await DeviceUnits.LoadAsync(t, o.UnitId);
        if (req.GradeId is { } grade) await DeviceUnits.CheckGradeAsync(t, grade, u.CategoryId, "gradeId");
        await DeviceUnits.ApplyEventAsync(t, u.Id, "refurb_completed", "in_stock", req.Condition, req.GradeId, req.SellingPrice, req.WarrantyDays, "refurb_order", id,
            new { order = o.Number, cost = u.AccumulatedCost });
        await t.ExecuteAsync("""
            UPDATE refurb.refurb_order SET status = 'completed', completed_at = now(), completed_by = @uid, result_condition = @Condition, grade_id = @GradeId,
                   selling_price = @SellingPrice, warranty_days = @WarrantyDays, work_done = coalesce(@WorkDone, work_done), updated_by = @uid
             WHERE id = @id
            """, new { id, uid = http.User.UserIdOf(), req.Condition, req.GradeId, req.SellingPrice, req.WarrantyDays, req.WorkDone });
        await t.AuditAsync(new AuditEntry("refurb.completed", "refurb_order", id,
            After: Json.Obj(new { o.Number, req.Condition, req.GradeId, req.SellingPrice, cost = u.AccumulatedCost, unit = u.Identifier }), BranchId: u.BranchId));
        await t.PublishAsync("refurb.completed", "refurb_order", id, o.Version + 1, Json.Obj(new { o.Number, req.Condition, cost = u.AccumulatedCost }), u.BranchId);
        return new { id, o.Number, unitId = u.Id, cost = u.AccumulatedCost };
    }
}
