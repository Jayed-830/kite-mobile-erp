using Kite.Accounting;
using Kite.Devices;
using Kite.Host.Infrastructure;
using Kite.Persistence;
using Kite.Platform;
using Kite.Sales;

namespace Kite.Host.Features;

public sealed record DevicePriceRequest(decimal? SellingPrice, int? WarrantyDays, int Version);
public sealed record DeviceReasonRequest(string Reason);
public sealed record DeviceWriteOffRequest(string Reason, string? ReasonCode = null);
public sealed record DeviceSettingsRequest(decimal LabourRatePerHour, int ReservationMinutes, int ParkedReservationMinutes, decimal SuggestedPriceStep, int Version);
public sealed record GradeRequest(string Code, string NameEn, string NameAr, decimal PricePercent, Guid? CategoryId = null, string? DescriptionEn = null,
    string? DescriptionAr = null, short? Sort = null, string? Status = null, int? Version = null);
public sealed record ChecklistItemInput(string Code, string LabelEn, string LabelAr, string Kind, bool Required = true, int? MinPercent = null, string OnFail = "refurbish");
public sealed record ChecklistRequest(string NameEn, string NameAr, List<ChecklistItemInput> Items, Guid? CategoryId = null, int? Version = null);
public sealed record ReserveRequest(Guid CartId);

/// <summary>A unit as the till sees it: the item, its own price, condition and identifiers.</summary>
public sealed record PosUnitDto(Guid UnitId, Guid VariantId, string Sku, string ProductType, string Tracking, string DescriptionEn, string DescriptionAr,
    decimal TaxRate, decimal? ListPrice, decimal? MinPrice, string Condition, string? GradeCode, string? Imei1, string? Imei2, string? Serial, int WarrantyDays,
    DateTime? ReservedUntil);

/// <summary>Device units (blueprint 11, 12): stock, history, prices, holds, write-offs, grades, checklists, settings and the till's lookups.</summary>
public static class DeviceEndpoints
{
    public static void Map(WebApplication app)
    {
        MapUnits(app);
        MapSettings(app);
        MapTill(app);
        MapReports(app);
    }

    // ------------------------------------------------------------------ units
    private static void MapUnits(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/devices");

        g.MapGet("", async (string? status, string? condition, Guid? branchId, Guid? variantId, string? q, int? limit, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            http.User.Demand(Permissions.DeviceView, branchId);
            return Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var statuses = string.IsNullOrWhiteSpace(status) ? null : status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var rows = await t.QueryAsync<DeviceUnitRow>(DeviceUnits.Select + """
                     WHERE (@statuses::text[] IS NULL OR u.status = ANY(@statuses)) AND (@condition::text IS NULL OR u.condition = @condition)
                       AND (@branchId::uuid IS NULL OR u.branch_id = @branchId) AND (@variantId::uuid IS NULL OR u.variant_id = @variantId)
                       AND (@q::text IS NULL OR upper(v.sku) LIKE '%' || upper(@q) || '%' OR p.name_en ILIKE '%' || @q || '%' OR p.name_ar ILIKE '%' || @q || '%'
                            OR EXISTS (SELECT 1 FROM device.imei_registry r WHERE r.device_unit_id = u.id AND r.value LIKE '%' || upper(@q) || '%'))
                     ORDER BY u.received_at DESC LIMIT @limit
                    """, new { statuses, condition, branchId, variantId, q = string.IsNullOrWhiteSpace(q) ? null : q.Trim(), limit = Math.Clamp(limit ?? 200, 1, 2000) });
                var today = DateTime.UtcNow.Date;
                return rows.Select(u => new
                {
                    u.Id, u.BranchCode, u.VariantId, u.Sku, u.DescriptionEn, u.DescriptionAr, u.ProductType, u.Status, u.HeldFromStatus, u.Condition,
                    u.GradeCode, u.GradeNameEn, u.GradeNameAr, u.AcquisitionCost, u.AccumulatedCost, u.SellingPrice, u.Imei1, u.Imei2, u.Serial, u.ReceivedAt,
                    ageDays = (int)(today - u.ReceivedAt.Date).TotalDays, u.Version,
                });
            }, ct));
        }).RequirePermission(Permissions.DeviceView).RequireModule("device");

        g.MapGet("/lookup/{code}", async (string code, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var unit = await DeviceUnits.FindAsync(t, DeviceUnits.Classify(code))
                    ?? throw new KiteException(ErrorCodes.DeviceNotFound, $"No device with {code.Trim()} in your branches.", 404);
                http.User.Demand(Permissions.DeviceView, unit.BranchId);
                return new { unit.Id };
            }, ct))).RequirePermission(Permissions.DeviceView).RequireModule("device");

        g.MapGet("/{id:guid}", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => DetailAsync(t, http, id), ct)))
            .RequirePermission(Permissions.DeviceView).RequireModule("device");

        // A unit's own selling price (12.6): set by a manager; empty means the grade's suggestion.
        g.MapPost("/{id:guid}/price", async (Guid id, DevicePriceRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            if (req.SellingPrice is < 0 || (req.SellingPrice is { } p && p != decimal.Round(p, 3)))
                throw new KiteException(ErrorCodes.Validation, "The price is zero or more, in baisa.", 400, "sellingPrice");
            if (req.WarrantyDays is < 0) throw new KiteException(ErrorCodes.Validation, "Warranty days cannot be negative.", 400, "warrantyDays");
            await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var u = await DeviceUnits.LoadAsync(t, id);
                http.User.Demand(Permissions.DevicePrice, u.BranchId);
                if (u.Version != req.Version) throw new KiteException(ErrorCodes.VersionMismatch, "Someone else changed this device. Reload and try again.", 412);
                await DeviceUnits.ApplyEventAsync(t, id, "price_set", null, sellingPrice: req.SellingPrice, warrantyDays: req.WarrantyDays, docType: "device_unit", docId: id);
                await t.AuditAsync(new AuditEntry("device.price_set", "device_unit", id, Before: Json.Obj(new { u.SellingPrice, u.WarrantyDays }),
                    After: Json.Obj(new { req.SellingPrice, req.WarrantyDays }), BranchId: u.BranchId));
                await t.PublishAsync("device.price_set", "device_unit", id, u.Version + 1, Json.Obj(new { req.SellingPrice }), u.BranchId);
            }, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.DevicePrice).RequireModule("device");

        // A manager's hold (11.3): any unit still in the business; release returns it to where it was.
        foreach (var (path, evt) in new[] { ("hold", "held"), ("release", "released") })
            g.MapPost($"/{{id:guid}}/{path}", async (Guid id, DeviceReasonRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(req.Reason)) throw new KiteException(ErrorCodes.Validation, "Give the reason.", 400, "reason");
                await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
                {
                    var u = await DeviceUnits.LoadAsync(t, id);
                    http.User.Demand(Permissions.DeviceHold, u.BranchId);
                    await DeviceUnits.ApplyEventAsync(t, id, evt, evt == "held" ? "quarantined" : null, docType: "device_unit", docId: id, details: new { reason = req.Reason.Trim() });
                    await t.AuditAsync(new AuditEntry($"device.{evt}", "device_unit", id, After: Json.Obj(new { reason = req.Reason.Trim(), from = u.Status }), BranchId: u.BranchId));
                    await t.PublishAsync($"device.{evt}", "device_unit", id, u.Version + 1, Json.Obj(new { reason = req.Reason.Trim() }), u.BranchId);
                }, ct);
                return Results.NoContent();
            }).RequirePermission(Permissions.DeviceHold).RequireModule("device");

        // Write-off (11.3): a stock adjustment of one unit at its cost, to stock write-offs.
        g.MapPost("/{id:guid}/write-off", async (Guid id, DeviceWriteOffRequest req, HttpContext http, KiteDb db, LicenceService licence, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Reason)) throw new KiteException(ErrorCodes.Validation, "Give the reason for the write-off.", 400, "reason");
            var code = req.ReasonCode ?? "damaged";
            if (code is not ("damaged" or "lost" or "other")) throw new KiteException(ErrorCodes.Validation, "The reason is damaged, lost or other.", 400, "reasonCode");
            await licence.RequireTransactAsync(ct);
            var result = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var u = await DeviceUnits.LoadAsync(t, id);
                http.User.Demand(Permissions.DeviceWriteOff, u.BranchId);
                if (u.Status is not ("in_stock" or "for_parts"))
                    throw new KiteException(ErrorCodes.DeviceTransition, $"A device that is {u.Status.Replace('_', ' ')} cannot be written off.", 409);
                var biz = await BusinessInfo.LoadAsync(t);
                var date = BusinessDay.Today(biz.TimeZone, t.Clock);
                var number = await DocNumbers.NextAsync(t, biz.Id, u.BranchId, "stock_adjustment", await FiscalPeriods.FiscalYearOfAsync(t, biz.Id, date), "ADJ");
                var docId = t.NewId();
                var lineId = t.NewId();
                await t.ExecuteAsync("""
                    INSERT INTO inv.adjustment (id, business_id, branch_id, warehouse_id, number, kind, adjustment_date, note, posted_by, origin_server_id)
                    VALUES (@docId, @b, @BranchId, @WarehouseId, @number, 'adjustment', @d::date, @note, @uid, (SELECT id FROM core.server WHERE is_self))
                    """, new { docId, b = biz.Id, u.BranchId, u.WarehouseId, number, d = date.ToDateTime(TimeOnly.MinValue), note = $"Write-off {u.Identifier}: {req.Reason.Trim()}", uid = http.User.UserIdOf() });
                var (_, value) = await DeviceUnits.MoveAsync(t, u, "adjustment", -1, null, "adjustment", docId, lineId, code);
                await t.ExecuteAsync("""
                    INSERT INTO inv.adjustment_line (id, adjustment_id, line_no, variant_id, qty, unit_cost, reason_code, device_unit_id)
                    VALUES (@lineId, @docId, 1, @VariantId, -1, NULL, @code, @Id)
                    """, new { lineId, docId, u.VariantId, code, u.Id });
                await DeviceUnits.ApplyEventAsync(t, id, "written_off", "written_off", docType: "adjustment", docId: docId, details: new { reason = req.Reason.Trim() });
                await Posting.PostAsync(t, biz.Id, u.BranchId, PostingRuleTemplates.StockAdjustment,
                    new PostingFacts().Add("loss_by_product_type", u.PostingClass, -value), date, "stock_adjustment", docId, $"Device write-off {number} ({u.Identifier})");
                await t.AuditAsync(new AuditEntry("device.written_off", "device_unit", id, After: Json.Obj(new { number, cost = -value, reason = req.Reason.Trim() }), BranchId: u.BranchId));
                await t.PublishAsync("device.written_off", "device_unit", id, u.Version + 1, Json.Obj(new { number, cost = -value }), u.BranchId);
                return new { id = docId, number, cost = -value };
            }, ct);
            return Results.Ok(result);
        }).RequirePermission(Permissions.DeviceWriteOff).RequireModule("device");
    }

    /// <summary>One unit's page (11.6): identity, cost build-up, price, history timeline, inspections, refurbishments and sales with profit.</summary>
    private static async Task<object> DetailAsync(DbTx t, HttpContext http, Guid id)
    {
        var u = await DeviceUnits.LoadAsync(t, id);
        http.User.Demand(Permissions.DeviceView, u.BranchId);
        var biz = await BusinessInfo.LoadAsync(t);
        var settings = await DeviceUnits.SettingsAsync(t, biz.Id);
        var newPrice = await DeviceUnits.NewPriceAsync(t, u.BranchId, u.VariantId, BusinessDay.Today(biz.TimeZone, t.Clock));
        var events = await t.RowsAsync("""
            SELECT e.seq, e.event_type AS "eventType", e.from_status AS "fromStatus", e.to_status AS "toStatus", e.from_condition AS "fromCondition",
                   e.to_condition AS "toCondition", g.code AS "gradeCode", e.cost_delta AS "costDelta", e.selling_price AS "sellingPrice",
                   e.source_doc_type AS "docType", e.source_doc_id AS "docId", coalesce(s.number, a.number, i.number, r.number) AS "docNumber",
                   e.details::text AS details, e.occurred_at AS "occurredAt", us.display_name_en AS "actorName"
              FROM device.device_event e
              LEFT JOIN device.grade g ON g.id = e.grade_id
              LEFT JOIN sales.sale s ON e.source_doc_type = 'sale' AND s.id = e.source_doc_id
              LEFT JOIN inv.adjustment a ON e.source_doc_type = 'adjustment' AND a.id = e.source_doc_id
              LEFT JOIN device.inspection i ON e.source_doc_type = 'device_inspection' AND i.id = e.source_doc_id
              LEFT JOIN refurb.refurb_order r ON e.source_doc_type = 'refurb_order' AND r.id = e.source_doc_id
              LEFT JOIN iam."user" us ON us.id = e.actor_id
             WHERE e.device_unit_id = @id ORDER BY e.seq
            """, new { id });
        var costs = await t.QuerySingleOrDefaultAsync<(decimal Parts, decimal Labour, decimal External)>("""
            SELECT coalesce(sum(amount) FILTER (WHERE kind = 'part'), 0), coalesce(sum(amount) FILTER (WHERE kind = 'labour'), 0),
                   coalesce(sum(amount) FILTER (WHERE kind = 'external'), 0)
              FROM refurb.refurb_cost WHERE device_unit_id = @id
            """, new { id });
        var inspections = await t.RowsAsync("""
            SELECT i.id, i.number, i.reason, i.outcome, i.suggested_outcome AS "suggestedOutcome", i.result_condition AS "resultCondition", g.code AS "gradeCode",
                   i.battery_health AS "batteryHealth", i.note, i.results::text AS results, i.inspected_at AS "inspectedAt", us.display_name_en AS "inspectedBy"
              FROM device.inspection i LEFT JOIN device.grade g ON g.id = i.grade_id JOIN iam."user" us ON us.id = i.inspected_by
             WHERE i.device_unit_id = @id ORDER BY i.inspected_at
            """, new { id });
        var refurbs = await t.RowsAsync("""
            SELECT r.id, r.number, r.status, r.problem, r.work_done AS "workDone", r.opened_at AS "openedAt", r.completed_at AS "completedAt",
                   tu.display_name_en AS "technicianName", (SELECT coalesce(sum(c.amount), 0) FROM refurb.refurb_cost c WHERE c.refurb_order_id = r.id) AS "total"
              FROM refurb.refurb_order r LEFT JOIN iam."user" tu ON tu.id = r.technician_id
             WHERE r.device_unit_id = @id ORDER BY r.opened_at
            """, new { id });
        var sales = await t.RowsAsync("""
            SELECT s.id AS "saleId", s.number, s.doc_type AS "docType", s.credit_reason AS "creditReason", s.completed_at AS "completedAt", l.net_amount AS "net",
                   l.tax_amount AS "tax", l.gross_amount AS "gross", l.cost_value AS "cost", CASE WHEN s.doc_type = 'credit_note' THEN NULL ELSE l.profit END AS "profit",
                   c.name_en AS "customerName"
              FROM sales.sale_line l JOIN sales.sale s ON s.id = l.sale_id LEFT JOIN crm.customer c ON c.id = s.customer_id
             WHERE l.device_unit_id = @id ORDER BY s.completed_at
            """, new { id });
        return new
        {
            unit = new
            {
                u.Id, u.BranchId, u.BranchCode, u.VariantId, u.Sku, u.DescriptionEn, u.DescriptionAr, u.ProductType, u.Tracking, u.CategoryId, u.Status,
                u.HeldFromStatus, u.Condition, u.GradeId, u.GradeCode, u.GradeNameEn, u.GradeNameAr, u.Imei1, u.Imei2, u.Serial, u.ReceivedAt, u.SellingPrice,
                u.WarrantyDays, u.DefaultWarrantyDays, u.Version,
            },
            cost = new { acquisition = u.AcquisitionCost, parts = costs.Parts, labour = costs.Labour, external = costs.External, total = u.AccumulatedCost },
            price = new
            {
                newPrice, gradePercent = u.GradePercent,
                suggested = DeviceMath.SuggestedPrice(newPrice, u.GradePercent, settings.SuggestedPriceStep),
                listPrice = DeviceMath.UnitListPrice(u.SellingPrice, u.Condition, newPrice, u.GradePercent, settings.SuggestedPriceStep),
            },
            events, inspections, refurbishments = refurbs, sales,
        };
    }

    // ------------------------------------------------------------------ settings, grades, checklists
    private static void MapSettings(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/devices");

        g.MapGet("/settings", async (HttpContext http, KiteDb db, ServerContext server, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => DeviceUnits.SettingsAsync(t, server.BusinessId!.Value), ct)))
            .RequireSignedIn();

        g.MapPut("/settings", async (DeviceSettingsRequest req, HttpContext http, KiteDb db, ServerContext server, CancellationToken ct) =>
        {
            if (req.LabourRatePerHour < 0 || req.LabourRatePerHour != decimal.Round(req.LabourRatePerHour, 3))
                throw new KiteException(ErrorCodes.Validation, "The labour rate is zero or more, in baisa.", 400, "labourRatePerHour");
            if (req.ReservationMinutes is < 1 or > 240) throw new KiteException(ErrorCodes.Validation, "A cart holds a device for 1 to 240 minutes.", 400, "reservationMinutes");
            if (req.ParkedReservationMinutes is < 1 or > 10080) throw new KiteException(ErrorCodes.Validation, "A parked sale holds a device for 1 minute to 7 days.", 400, "parkedReservationMinutes");
            if (req.SuggestedPriceStep is not (0.005m or 0.010m or 0.050m or 0.100m or 0.500m or 1.000m))
                throw new KiteException(ErrorCodes.Validation, "Round suggested prices to 0.005, 0.010, 0.050, 0.100, 0.500 or 1.000.", 400, "suggestedPriceStep");
            await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var before = await DeviceUnits.SettingsAsync(t, server.BusinessId!.Value);
                var n = await t.ExecuteAsync("""
                    UPDATE device.settings SET labour_rate_per_hour = @LabourRatePerHour, reservation_minutes = @ReservationMinutes,
                           parked_reservation_minutes = @ParkedReservationMinutes, suggested_price_step = @SuggestedPriceStep, updated_by = @uid
                     WHERE business_id = @b AND version = @Version
                    """, new { req.LabourRatePerHour, req.ReservationMinutes, req.ParkedReservationMinutes, req.SuggestedPriceStep, uid = http.User.UserIdOf(), b = server.BusinessId, req.Version });
                if (n == 0) throw new KiteException(ErrorCodes.VersionMismatch, "Someone else changed the device settings. Reload and try again.", 412);
                await t.AuditAsync(new AuditEntry("device.settings_changed", "device_settings", server.BusinessId!.Value, Before: Json.Obj(before), After: Json.Obj(req)));
            }, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.DeviceSettings).RequireModule("refurb");

        g.MapGet("/grades", async (HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.RowsAsync("""
                SELECT g.id, g.category_id AS "categoryId", c.name_en AS "categoryName", g.code, g.name_en AS "nameEn", g.name_ar AS "nameAr",
                       g.description_en AS "descriptionEn", g.description_ar AS "descriptionAr", g.price_percent AS "pricePercent", g.sort, g.status, g.version
                  FROM device.grade g LEFT JOIN catalog.category c ON c.id = g.category_id ORDER BY g.category_id NULLS FIRST, g.sort, g.code
                """), ct))).RequireSignedIn();

        g.MapPost("/grades", async (GradeRequest req, HttpContext http, KiteDb db, ServerContext server, CancellationToken ct) =>
        {
            CheckGrade(req);
            var id = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var id = t.NewId();
                await t.ExecuteAsync("""
                    INSERT INTO device.grade (id, business_id, category_id, code, name_en, name_ar, description_en, description_ar, price_percent, sort, created_by)
                    VALUES (@id, @b, @CategoryId, @code, @en, @ar, @DescriptionEn, @DescriptionAr, @PricePercent, @sort, @uid)
                    """, new { id, b = server.BusinessId, req.CategoryId, code = req.Code.Trim().ToUpperInvariant(), en = req.NameEn.Trim(), ar = req.NameAr.Trim(),
                               req.DescriptionEn, req.DescriptionAr, req.PricePercent, sort = req.Sort ?? (short)0, uid = http.User.UserIdOf() });
                await t.AuditAsync(new AuditEntry("device.grade_created", "device_grade", id, After: Json.Obj(req)));
                return id;
            }, ct);
            return Results.Created($"/api/v1/devices/grades/{id}", new { id });
        }).RequirePermission(Permissions.DeviceSettings).RequireModule("refurb");

        g.MapPut("/grades/{id:guid}", async (Guid id, GradeRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            CheckGrade(req);
            if (req.Version is null) throw new KiteException(ErrorCodes.Validation, "Version is required.", 400, "version");
            await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var status = req.Status ?? "active";
                if (status is not ("active" or "inactive")) throw new KiteException(ErrorCodes.Validation, "Status is active or inactive.", 400, "status");
                var n = await t.ExecuteAsync("""
                    UPDATE device.grade SET code = @code, name_en = @en, name_ar = @ar, description_en = @DescriptionEn, description_ar = @DescriptionAr,
                           price_percent = @PricePercent, sort = coalesce(@Sort, sort), status = @status, category_id = @CategoryId, updated_by = @uid
                     WHERE id = @id AND version = @Version
                    """, new { id, code = req.Code.Trim().ToUpperInvariant(), en = req.NameEn.Trim(), ar = req.NameAr.Trim(), req.DescriptionEn, req.DescriptionAr,
                               req.PricePercent, req.Sort, status, req.CategoryId, uid = http.User.UserIdOf(), req.Version });
                if (n == 0) throw new KiteException(ErrorCodes.VersionMismatch, "That grade was changed or removed. Reload and try again.", 412);
                await t.AuditAsync(new AuditEntry("device.grade_changed", "device_grade", id, After: Json.Obj(req)));
            }, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.DeviceSettings).RequireModule("refurb");

        g.MapGet("/checklists", async (HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var lists = await t.QueryAsync<(Guid Id, Guid? CategoryId, string? CategoryName, string NameEn, string NameAr, string Status, int Version)>("""
                    SELECT c.id, c.category_id, cat.name_en, c.name_en, c.name_ar, c.status, c.version
                      FROM device.checklist c LEFT JOIN catalog.category cat ON cat.id = c.category_id ORDER BY c.category_id NULLS FIRST, c.created_at
                    """);
                var result = new List<object>();
                foreach (var c in lists)
                    result.Add(new { c.Id, c.CategoryId, c.CategoryName, c.NameEn, c.NameAr, c.Status, c.Version, items = await ItemsAsync(t, c.Id, true) });
                return result;
            }, ct))).RequireSignedIn();

        // The checklist an inspection of this unit uses: its category's, else the default.
        g.MapGet("/{id:guid}/checklist", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var u = await DeviceUnits.LoadAsync(t, id);
                var (checklistId, items) = await ChecklistForAsync(t, u.CategoryId);
                var grades = await t.RowsAsync("""
                    SELECT id, code, name_en AS "nameEn", name_ar AS "nameAr", price_percent AS "pricePercent" FROM device.grade
                     WHERE status = 'active' AND (category_id IS NULL OR category_id = @c) ORDER BY sort, code
                    """, new { c = u.CategoryId });
                return new { checklistId, items, grades };
            }, ct))).RequirePermission(Permissions.DeviceView).RequireModule("refurb");

        g.MapPost("/checklists", async (ChecklistRequest req, HttpContext http, KiteDb db, ServerContext server, CancellationToken ct) =>
        {
            CheckChecklist(req);
            var id = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var id = t.NewId();
                await t.ExecuteAsync("""
                    INSERT INTO device.checklist (id, business_id, category_id, name_en, name_ar, created_by) VALUES (@id, @b, @CategoryId, @en, @ar, @uid)
                    """, new { id, b = server.BusinessId, req.CategoryId, en = req.NameEn.Trim(), ar = req.NameAr.Trim(), uid = http.User.UserIdOf() });
                await SaveItemsAsync(t, id, req.Items);
                await t.AuditAsync(new AuditEntry("device.checklist_created", "device_checklist", id, After: Json.Obj(req)));
                return id;
            }, ct);
            return Results.Created($"/api/v1/devices/checklists/{id}", new { id });
        }).RequirePermission(Permissions.DeviceSettings).RequireModule("refurb");

        g.MapPut("/checklists/{id:guid}", async (Guid id, ChecklistRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            CheckChecklist(req);
            if (req.Version is null) throw new KiteException(ErrorCodes.Validation, "Version is required.", 400, "version");
            await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var n = await t.ExecuteAsync("UPDATE device.checklist SET name_en = @en, name_ar = @ar, updated_by = @uid WHERE id = @id AND version = @Version",
                    new { id, en = req.NameEn.Trim(), ar = req.NameAr.Trim(), uid = http.User.UserIdOf(), req.Version });
                if (n == 0) throw new KiteException(ErrorCodes.VersionMismatch, "That checklist was changed. Reload and try again.", 412);
                await SaveItemsAsync(t, id, req.Items);
                await t.AuditAsync(new AuditEntry("device.checklist_changed", "device_checklist", id, After: Json.Obj(req)));
            }, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.DeviceSettings).RequireModule("refurb");
    }

    private static void CheckGrade(GradeRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Code) || !System.Text.RegularExpressions.Regex.IsMatch(req.Code.Trim().ToUpperInvariant(), "^[A-Z0-9][A-Z0-9+-]{0,7}$"))
            throw new KiteException(ErrorCodes.Validation, "A grade code is 1 to 8 letters, digits, + or -.", 400, "code");
        if (string.IsNullOrWhiteSpace(req.NameEn) || string.IsNullOrWhiteSpace(req.NameAr))
            throw new KiteException(ErrorCodes.Validation, "Give the grade's name in English and Arabic.", 400, "nameEn");
        if (req.PricePercent is <= 0 or > 100) throw new KiteException(ErrorCodes.Validation, "The price percentage is above 0 and at most 100.", 400, "pricePercent");
    }

    private static void CheckChecklist(ChecklistRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.NameEn) || string.IsNullOrWhiteSpace(req.NameAr))
            throw new KiteException(ErrorCodes.Validation, "Give the checklist's name in English and Arabic.", 400, "nameEn");
        if (req.Items is null || req.Items.Count == 0) throw new KiteException(ErrorCodes.Validation, "A checklist needs at least one item.", 400, "items");
        if (req.Items.Select(i => i.Code).Distinct(StringComparer.Ordinal).Count() != req.Items.Count)
            throw new KiteException(ErrorCodes.Validation, "Each item needs its own code.", 400, "items");
        foreach (var (i, n) in req.Items.Select((i, n) => (i, n)))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(i.Code ?? "", "^[a-z][a-z0-9_]{0,31}$"))
                throw new KiteException(ErrorCodes.Validation, "An item code is lower-case letters, digits and _.", 400, $"items[{n}].code");
            if (string.IsNullOrWhiteSpace(i.LabelEn) || string.IsNullOrWhiteSpace(i.LabelAr))
                throw new KiteException(ErrorCodes.Validation, "Give the item's label in English and Arabic.", 400, $"items[{n}].labelEn");
            if (i.Kind is not ("pass_fail" or "percent" or "text")) throw new KiteException(ErrorCodes.Validation, "An item is pass/fail, percent or text.", 400, $"items[{n}].kind");
            if (i.Kind == "percent" && i.MinPercent is not (>= 0 and <= 100)) throw new KiteException(ErrorCodes.Validation, "A percent item needs its minimum (0-100).", 400, $"items[{n}].minPercent");
            if (i.Kind == "text" && i.Required) throw new KiteException(ErrorCodes.Validation, "A note is never required.", 400, $"items[{n}].required");
            if (i.OnFail is not ("refurbish" or "damaged" or "for_parts")) throw new KiteException(ErrorCodes.Validation, "A failure means refurbish, damaged or for parts.", 400, $"items[{n}].onFail");
        }
    }

    private static async Task SaveItemsAsync(DbTx t, Guid checklistId, List<ChecklistItemInput> items)
    {
        await t.ExecuteAsync("UPDATE device.checklist_item SET status = 'inactive' WHERE checklist_id = @checklistId AND NOT (code = ANY(@codes))",
            new { checklistId, codes = items.Select(i => i.Code).ToArray() });
        short sort = 0;
        foreach (var i in items)
            await t.ExecuteAsync("""
                INSERT INTO device.checklist_item (id, checklist_id, sort, code, label_en, label_ar, kind, required, min_percent, on_fail)
                VALUES (@id, @checklistId, @sort, @Code, @en, @ar, @Kind, @Required, @min, @OnFail)
                ON CONFLICT (checklist_id, code) DO UPDATE SET sort = EXCLUDED.sort, label_en = EXCLUDED.label_en, label_ar = EXCLUDED.label_ar,
                    kind = EXCLUDED.kind, required = EXCLUDED.required, min_percent = EXCLUDED.min_percent, on_fail = EXCLUDED.on_fail, status = 'active'
                """, new { id = t.NewId(), checklistId, sort = sort++, i.Code, en = i.LabelEn.Trim(), ar = i.LabelAr.Trim(), i.Kind, i.Required,
                           min = i.Kind == "percent" ? (short?)i.MinPercent : null, i.OnFail });
    }

    private static async Task<List<ChecklistItem>> ItemsAsync(DbTx t, Guid checklistId, bool includeInactive = false) =>
        (await t.QueryAsync<(string Code, string LabelEn, string LabelAr, string Kind, bool Required, short? MinPercent, string OnFail)>("""
            SELECT code, label_en, label_ar, kind, required, min_percent, on_fail FROM device.checklist_item
             WHERE checklist_id = @checklistId AND (@all OR status = 'active') ORDER BY sort, code
            """, new { checklistId, all = includeInactive }))
        .Select(i => new ChecklistItem(i.Code, i.LabelEn, i.LabelAr, i.Kind, i.Required, i.MinPercent, i.OnFail)).ToList();

    /// <summary>The active checklist for a category, else the business default; its active items.</summary>
    public static async Task<(Guid? Id, List<ChecklistItem> Items)> ChecklistForAsync(DbTx t, Guid? categoryId)
    {
        var id = await t.ExecuteScalarAsync<Guid?>("""
            SELECT id FROM device.checklist WHERE status = 'active' AND (category_id = @categoryId OR category_id IS NULL)
             ORDER BY category_id NULLS LAST LIMIT 1
            """, new { categoryId });
        return id is { } c ? (c, await ItemsAsync(t, c)) : (null, []);
    }

    // ------------------------------------------------------------------ the till
    private static void MapTill(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/pos/units");

        // A scanned IMEI or serial at the till: the unit must be in stock at this branch and not in another cart.
        g.MapGet("/code/{code}", async (string code, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var term = await PosTerminals.CurrentAsync(t, http);
                http.User.Demand(Permissions.PosSell, term.BranchId);
                var scanned = DeviceUnits.Classify(code);
                var u = await DeviceUnits.FindAsync(t, scanned)
                    ?? throw new KiteException(ErrorCodes.DeviceNotFound, $"No device with {(scanned.Kind == "imei" ? "IMEI" : "serial number")} {scanned.Value} in this business.", 404);
                CheckSellable(u, term);
                return await TillUnitAsync(t, u, term);
            }, ct))).RequireSignedIn().RequireModule("device");

        // The units of one item in stock at this branch, to pick from when the cashier taps the item.
        g.MapGet("", async (Guid variantId, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var term = await PosTerminals.CurrentAsync(t, http);
                http.User.Demand(Permissions.PosSell, term.BranchId);
                var units = await t.QueryAsync<DeviceUnitRow>(DeviceUnits.Select + """
                     WHERE u.variant_id = @variantId AND u.branch_id = @branch AND u.warehouse_id = @warehouse AND u.status = 'in_stock'
                     ORDER BY u.condition = 'brand_new' DESC, u.received_at
                    """, new { variantId, branch = term.BranchId, warehouse = term.WarehouseId });
                var list = new List<PosUnitDto>();
                foreach (var u in units) list.Add(await TillUnitAsync(t, u, term));
                return list;
            }, ct))).RequireSignedIn().RequireModule("device");

        // Leases a unit to a cart (10.2): no other till can sell it until the lease ends or the cart lets it go.
        g.MapPost("/{id:guid}/reserve", async (Guid id, ReserveRequest req, HttpContext http, KiteDb db, ServerContext server, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var term = await PosTerminals.CurrentAsync(t, http);
                http.User.Demand(Permissions.PosSell, term.BranchId);
                var u = await DeviceUnits.LoadAsync(t, id);
                CheckSellable(u, term);
                var settings = await DeviceUnits.SettingsAsync(t, server.BusinessId!.Value);
                var until = await ReserveAsync(t, server.BusinessId!.Value, u, term, req.CartId, http.User.UserIdOf(), settings.ReservationMinutes);
                return new { unitId = id, reservedUntil = until };
            }, ct))).RequireSignedIn().RequireModule("device");

        g.MapDelete("/{id:guid}/reserve", async (Guid id, Guid cartId, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var term = await PosTerminals.CurrentAsync(t, http);
                await t.ExecuteAsync("DELETE FROM device.reservation WHERE device_unit_id = @id AND cart_id = @cartId AND branch_id = @branch", new { id, cartId, branch = term.BranchId });
            }, ct);
            return Results.NoContent();
        }).RequireSignedIn().RequireModule("device");
    }

    /// <summary>Takes or renews the lease of a unit for a cart; refuses when another cart holds an unexpired lease.</summary>
    public static async Task<DateTime> ReserveAsync(DbTx t, Guid businessId, DeviceUnitRow u, PosTerminal term, Guid cartId, Guid userId, int minutes)
    {
        var until = await t.ExecuteScalarAsync<DateTime?>("""
            INSERT INTO device.reservation (device_unit_id, business_id, branch_id, cart_id, terminal_id, user_id, reserved_at, expires_at)
            VALUES (@unit, @businessId, @branch, @cartId, @terminal, @userId, now(), now() + make_interval(mins => @minutes))
            ON CONFLICT (device_unit_id) DO UPDATE SET cart_id = EXCLUDED.cart_id, terminal_id = EXCLUDED.terminal_id, user_id = EXCLUDED.user_id,
                   reserved_at = EXCLUDED.reserved_at, expires_at = EXCLUDED.expires_at
             WHERE device.reservation.cart_id = EXCLUDED.cart_id OR device.reservation.expires_at <= now()
            RETURNING expires_at
            """, new { unit = u.Id, businessId, branch = term.BranchId, cartId, terminal = term.Id, userId, minutes });
        if (until is { } d) return d;
        var holder = await t.QuerySingleOrDefaultAsync<(string Code, DateTime Until)>("""
            SELECT t.code, r.expires_at FROM device.reservation r JOIN core.terminal t ON t.id = r.terminal_id WHERE r.device_unit_id = @id
            """, new { id = u.Id });
        throw new KiteException(ErrorCodes.DeviceReserved, $"{u.Identifier} is in a cart at till {holder.Code} until {holder.Until:HH:mm} UTC.", 409);
    }

    /// <summary>A unit the till may sell: in stock, in this branch's shop warehouse.</summary>
    public static void CheckSellable(DeviceUnitRow u, PosTerminal term)
    {
        if (u.BranchId != term.BranchId)
            throw new KiteException(ErrorCodes.DeviceNotAvailable, $"{u.Identifier} is at branch {u.BranchCode}, not this one.", 409);
        if (u.Status != "in_stock")
            throw new KiteException(ErrorCodes.DeviceNotAvailable, $"{u.Identifier} is {u.Status.Replace('_', ' ')}, so it cannot be sold.", 409);
        if (u.WarehouseId != term.WarehouseId)
            throw new KiteException(ErrorCodes.DeviceNotAvailable, $"{u.Identifier} is in another warehouse of this branch.", 409);
    }

    /// <summary>The till's view of a unit: the item's tax, the unit's own price (12.1) and the lease, if any.</summary>
    public static async Task<PosUnitDto> TillUnitAsync(DbTx t, DeviceUnitRow u, PosTerminal term)
    {
        var biz = await BusinessInfo.LoadAsync(t);
        var item = await PricedItemAsync(t, biz, u, term.BranchId, BusinessDay.Today(biz.TimeZone, t.Clock));
        var reserved = await t.ExecuteScalarAsync<DateTime?>("SELECT expires_at FROM device.reservation WHERE device_unit_id = @id AND expires_at > now()", new { id = u.Id });
        return new PosUnitDto(u.Id, u.VariantId, u.Sku, u.ProductType, u.Tracking, item.DescriptionEn, item.DescriptionAr, item.TaxRate, item.ListPrice, item.MinPrice,
            u.Condition, u.GradeCode, u.Imei1, u.Imei2, u.Serial, item.WarrantyDays, reserved);
    }

    /// <summary>
    /// The catalog item as one unit sells: its own list price (12.1), the item's minimum only when new, its own warranty,
    /// and a description that names the condition and grade so the receipt says exactly what was sold.
    /// </summary>
    public static async Task<PricedItem> PricedItemAsync(DbTx t, BusinessInfo biz, DeviceUnitRow u, Guid branchId, DateOnly date)
    {
        var items = await ItemPricing.LoadAsync(t, branchId, [u.VariantId], date);
        var item = items[u.VariantId];
        var settings = await DeviceUnits.SettingsAsync(t, biz.Id);
        var list = DeviceMath.UnitListPrice(u.SellingPrice, u.Condition, item.ListPrice, u.GradePercent, settings.SuggestedPriceStep);
        var suffixEn = u.Condition == "brand_new" ? "" : $" · {DeviceUnits.ConditionEn(u.Condition)}{(u.GradeCode is null ? "" : " " + u.GradeCode)}";
        var suffixAr = u.Condition == "brand_new" ? "" : $" · {DeviceUnits.ConditionAr(u.Condition)}{(u.GradeCode is null ? "" : " " + u.GradeCode)}";
        return item with
        {
            ListPrice = list, MinPrice = u.Condition == "brand_new" ? item.MinPrice : null, WarrantyDays = u.WarrantyDays ?? item.WarrantyDays,
            DescriptionEn = item.DescriptionEn + suffixEn, DescriptionAr = item.DescriptionAr + suffixAr,
        };
    }

    // ------------------------------------------------------------------ reports (12.6)
    private static void MapReports(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/devices/reports");

        // Used and refurbished stock by condition and grade, with cost and age.
        g.MapGet("/stock", async (Guid? branchId, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            http.User.Demand(Permissions.DeviceView, branchId);
            return Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.RowsAsync("""
                SELECT u.status, u.condition, g.code AS "gradeCode", count(*) AS units, sum(u.accumulated_cost) AS cost,
                       round(avg(current_date - u.received_at::date)) AS "averageAgeDays",
                       count(*) FILTER (WHERE current_date - u.received_at::date <= 30) AS "age0to30",
                       count(*) FILTER (WHERE current_date - u.received_at::date BETWEEN 31 AND 90) AS "age31to90",
                       count(*) FILTER (WHERE current_date - u.received_at::date > 90) AS "ageOver90"
                  FROM device.device_unit u LEFT JOIN device.grade g ON g.id = u.grade_id
                 WHERE device.in_stock_status(u.status, u.held_from_status) AND (@branchId::uuid IS NULL OR u.branch_id = @branchId)
                 GROUP BY u.status, u.condition, g.code ORDER BY u.status, u.condition, g.code
                """, new { branchId }), ct));
        }).RequirePermission(Permissions.DeviceView).RequireModule("device");

        // Profit per device sold (12.2): net selling price minus the unit's total cost at the moment of sale.
        g.MapGet("/profit", async (DateOnly? from, DateOnly? to, Guid? branchId, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            http.User.Demand(Permissions.DeviceView, branchId);
            return Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.RowsAsync("""
                SELECT s.number, s.sale_date AS "saleDate", l.device_unit_id AS "unitId", l.description_en AS "descriptionEn",
                       (SELECT r.value FROM device.imei_registry r WHERE r.device_unit_id = l.device_unit_id ORDER BY r.kind, r.slot LIMIT 1) AS identifier,
                       l.net_amount AS net, l.cost_value AS cost, l.profit,
                       (SELECT coalesce(sum(x.qty), 0) FROM sales.sale_line x WHERE x.returned_line_id = l.id) AS returned
                  FROM sales.sale_line l JOIN sales.sale s ON s.id = l.sale_id
                 WHERE l.device_unit_id IS NOT NULL AND s.doc_type <> 'credit_note'
                   AND (@from::date IS NULL OR s.sale_date >= @from::date) AND (@to::date IS NULL OR s.sale_date <= @to::date)
                   AND (@branchId::uuid IS NULL OR s.branch_id = @branchId)
                 ORDER BY s.completed_at DESC LIMIT 1000
                """, new { from = from?.ToDateTime(TimeOnly.MinValue), to = to?.ToDateTime(TimeOnly.MinValue), branchId }), ct));
        }).RequirePermission(Permissions.DeviceView).RequireModule("device");

        // Refurbishment cost by technician.
        g.MapGet("/refurbishment", async (DateOnly? from, DateOnly? to, Guid? branchId, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            http.User.Demand(Permissions.DeviceView, branchId);
            return Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.RowsAsync("""
                SELECT coalesce(tu.display_name_en, '-') AS technician, count(DISTINCT r.id) AS orders,
                       count(DISTINCT r.id) FILTER (WHERE r.status = 'completed') AS completed,
                       coalesce(sum(c.amount) FILTER (WHERE c.kind = 'part'), 0) AS parts, coalesce(sum(c.amount) FILTER (WHERE c.kind = 'labour'), 0) AS labour,
                       coalesce(sum(c.amount) FILTER (WHERE c.kind = 'external'), 0) AS external, coalesce(sum(c.amount), 0) AS total
                  FROM refurb.refurb_order r LEFT JOIN refurb.refurb_cost c ON c.refurb_order_id = r.id LEFT JOIN iam."user" tu ON tu.id = r.technician_id
                 WHERE (@from::date IS NULL OR r.opened_at::date >= @from::date) AND (@to::date IS NULL OR r.opened_at::date <= @to::date)
                   AND (@branchId::uuid IS NULL OR r.branch_id = @branchId)
                 GROUP BY tu.display_name_en ORDER BY total DESC
                """, new { from = from?.ToDateTime(TimeOnly.MinValue), to = to?.ToDateTime(TimeOnly.MinValue), branchId }), ct));
        }).RequirePermission(Permissions.DeviceView).RequireModule("refurb");
    }
}
