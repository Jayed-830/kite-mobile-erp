using Kite.Accounting;
using Kite.Devices;
using Kite.Persistence;
using Kite.Platform;

namespace Kite.Host.Infrastructure;

/// <summary>A unit as it arrives on a receiving document (opening stock or stock found).</summary>
public sealed record UnitInput(string? Imei1 = null, string? Imei2 = null, string? Serial = null, decimal? UnitCost = null, string? Condition = null,
    string? Status = null, Guid? GradeId = null, decimal? SellingPrice = null, int? WarrantyDays = null, string? Entry = null);

public sealed record DeviceSettings(decimal LabourRatePerHour, int ReservationMinutes, int ParkedReservationMinutes, decimal SuggestedPriceStep, int Version);

/// <summary>One device unit with its item, grade and identifiers (a class: Dapper maps it by name).</summary>
public sealed class DeviceUnitRow
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }
    public Guid BranchId { get; set; }
    public string BranchCode { get; set; } = "";
    public Guid WarehouseId { get; set; }
    public Guid VariantId { get; set; }
    public string Sku { get; set; } = "";
    public Guid ProductId { get; set; }
    public string ProductType { get; set; } = "";
    public string Tracking { get; set; } = "";
    public Guid? CategoryId { get; set; }
    public Guid BrandKey { get; set; }
    public string DescriptionEn { get; set; } = "";
    public string DescriptionAr { get; set; } = "";
    public string Status { get; set; } = "";
    public string? HeldFromStatus { get; set; }
    public string Condition { get; set; } = "";
    public Guid? GradeId { get; set; }
    public string? GradeCode { get; set; }
    public string? GradeNameEn { get; set; }
    public string? GradeNameAr { get; set; }
    public decimal? GradePercent { get; set; }
    public decimal AcquisitionCost { get; set; }
    public decimal AccumulatedCost { get; set; }
    public decimal? SellingPrice { get; set; }
    public int? WarrantyDays { get; set; }
    public int DefaultWarrantyDays { get; set; }
    public string? Imei1 { get; set; }
    public string? Imei2 { get; set; }
    public string? Serial { get; set; }
    public DateTime ReceivedAt { get; set; }
    public Guid? SoldSaleId { get; set; }
    public int Version { get; set; }

    public string PostingClass => PostingRuleTemplates.ClassOf(ProductType, Condition);
    public string Identifier => Imei1 ?? Serial ?? Id.ToString();
}

public static class DeviceUnits
{
    public static readonly string[] InStockStatuses = ["in_stock", "under_inspection", "returned_inspection", "refurbishing", "for_parts", "supplier_return"];

    public const string Select = $"""
        SELECT u.id, u.business_id AS BusinessId, u.branch_id AS BranchId, b.code AS BranchCode, u.warehouse_id AS WarehouseId, u.variant_id AS VariantId,
               v.sku, p.id AS ProductId, p.product_type AS ProductType, p.tracking, p.category_id AS CategoryId, coalesce(p.brand_id, p.id) AS BrandKey,
               {ItemPricing.DescriptionEn} AS DescriptionEn, {ItemPricing.DescriptionAr} AS DescriptionAr,
               u.status, u.held_from_status AS HeldFromStatus, u.condition, u.grade_id AS GradeId, g.code AS GradeCode, g.name_en AS GradeNameEn,
               g.name_ar AS GradeNameAr, g.price_percent AS GradePercent, u.acquisition_cost AS AcquisitionCost, u.accumulated_cost AS AccumulatedCost,
               u.selling_price AS SellingPrice, u.warranty_days AS WarrantyDays, p.default_warranty_days AS DefaultWarrantyDays,
               (SELECT r.value FROM device.imei_registry r WHERE r.device_unit_id = u.id AND r.kind = 'imei' AND r.slot = 1) AS Imei1,
               (SELECT r.value FROM device.imei_registry r WHERE r.device_unit_id = u.id AND r.kind = 'imei' AND r.slot = 2) AS Imei2,
               (SELECT r.value FROM device.imei_registry r WHERE r.device_unit_id = u.id AND r.kind = 'serial') AS Serial,
               u.received_at AS ReceivedAt, u.sold_sale_id AS SoldSaleId, u.version
          FROM device.device_unit u
          JOIN catalog.variant v ON v.id = u.variant_id JOIN catalog.product p ON p.id = v.product_id
          JOIN core.branch b ON b.id = u.branch_id LEFT JOIN device.grade g ON g.id = u.grade_id
        """;

    public static async Task<DeviceUnitRow> LoadAsync(DbTx t, Guid id) =>
        await t.QuerySingleOrDefaultAsync<DeviceUnitRow>(Select + " WHERE u.id = @id", new { id })
        ?? throw new KiteException(ErrorCodes.DeviceNotFound, "That device is not in your branches.", 404);

    /// <summary>The unit an IMEI or serial number belongs to (a unit in stock first, when serials of two brands match).</summary>
    public static async Task<DeviceUnitRow?> FindAsync(DbTx t, ScannedCode code)
    {
        if (code.Kind is not ("imei" or "serial")) return null;
        return await t.QuerySingleOrDefaultAsync<DeviceUnitRow>(Select + """
             WHERE EXISTS (SELECT 1 FROM device.imei_registry r WHERE r.device_unit_id = u.id AND r.value = @v AND r.kind = ANY(@kinds))
             ORDER BY (u.status = 'in_stock') DESC, u.received_at DESC LIMIT 1
            """, new { v = code.Value, kinds = code.Kind == "imei" ? new[] { "imei", "eid" } : new[] { "serial" } });
    }

    /// <summary>Recognises a scanned code, refusing a mistyped IMEI with the reason.</summary>
    public static ScannedCode Classify(string? raw, string field = "code")
    {
        var c = DeviceMath.Classify(raw);
        return c.Kind switch
        {
            "invalid_imei" => throw new KiteException(ErrorCodes.ImeiInvalid,
                c.Value.Length == 15 ? $"{c.Value} is not a valid IMEI: the check digit is wrong. Scan it again." : $"{c.Value} is not an IMEI: an IMEI has 15 digits.", 400, field),
            "unknown" => throw new KiteException(ErrorCodes.DeviceNotFound, "Scan or type an IMEI (15 digits) or a serial number.", 400, field),
            _ => c,
        };
    }

    /// <summary>Changes a unit's status, condition, grade or price through device.apply_event; returns the event id.</summary>
    public static Task<Guid> ApplyEventAsync(DbTx t, Guid unitId, string evt, string? toStatus, string? toCondition = null, Guid? gradeId = null,
        decimal? sellingPrice = null, int? warrantyDays = null, string? docType = null, Guid? docId = null, object? details = null, decimal costDelta = 0m) =>
        t.ExecuteScalarAsync<Guid>("""
            SELECT device.apply_event(@id, @unitId, @evt, @toStatus::text, @toCondition::text, @gradeId::uuid, @sellingPrice::numeric, @warrantyDays::int,
                                      @docType::text, @docId::uuid, @details::jsonb, @actor::uuid, @costDelta::numeric)
            """, new { id = t.NewId(), unitId, evt, toStatus, toCondition, gradeId, sellingPrice, warrantyDays, docType, docId,
                       details = details is null ? null : System.Text.Json.JsonSerializer.Serialize(details, Json.Web), actor = t.Actor.UserId, costDelta });

    /// <summary>One stock movement of a unit: in (+1) or out (-1) at its cost, or a cost added (quantity 0, value given).</summary>
    public static Task<(Guid Id, decimal Value)> MoveAsync(DbTx t, DeviceUnitRow u, string type, int qty, decimal? value, string docType, Guid docId,
        Guid? lineId, string? reason, DateTimeOffset? at = null, Guid? terminalId = null) =>
        t.QuerySingleOrDefaultAsync<(Guid, decimal)>("""
            INSERT INTO inv.stock_movement (id, business_id, branch_id, warehouse_id, variant_id, movement_type, qty, value, device_unit_id, source_doc_type,
                                            source_doc_id, source_line_id, reason, movement_at, created_by, origin_server_id, origin_device_id)
            VALUES (@movementId, @BusinessId, @BranchId, @WarehouseId, @VariantId, @type, @qty, @value, @unitId, @docType, @docId, @lineId, @reason,
                    coalesce(@at, now()), @by, (SELECT id FROM core.server WHERE is_self), @terminalId)
            RETURNING id, value
            """, new { movementId = t.NewId(), u.BusinessId, u.BranchId, u.WarehouseId, u.VariantId, type, qty, value, unitId = u.Id, docType, docId, lineId, reason,
                       at, by = t.Actor.UserId, terminalId });

    /// <summary>Checks the identifiers a unit needs for its item's tracking; returns them normalised.</summary>
    public static (string? Imei1, string? Imei2, string? Serial) CheckIdentifiers(string tracking, UnitInput u, string field)
    {
        string? imei1 = null, imei2 = null, serial = null;
        if (!string.IsNullOrWhiteSpace(u.Imei1)) imei1 = ImeiOf(u.Imei1, field + ".imei1");
        if (!string.IsNullOrWhiteSpace(u.Imei2)) imei2 = ImeiOf(u.Imei2, field + ".imei2");
        if (!string.IsNullOrWhiteSpace(u.Serial))
        {
            var c = DeviceMath.Classify(u.Serial);
            if (c.Kind != "serial") throw new KiteException(ErrorCodes.Validation, "A serial number is 4 to 40 letters, digits or dashes.", 400, field + ".serial");
            serial = c.Value;
        }
        if (tracking == "imei" && imei1 is null) throw new KiteException(ErrorCodes.DeviceUnitRequired, "Scan the device's IMEI.", 400, field + ".imei1");
        if (tracking == "serial" && serial is null) throw new KiteException(ErrorCodes.DeviceUnitRequired, "Scan the device's serial number.", 400, field + ".serial");
        if (imei2 is not null && imei1 is null) throw new KiteException(ErrorCodes.Validation, "Give IMEI 1 before IMEI 2.", 400, field + ".imei1");
        if (imei2 is not null && imei2 == imei1) throw new KiteException(ErrorCodes.Validation, "IMEI 2 must differ from IMEI 1.", 400, field + ".imei2");
        return (imei1, imei2, serial);

        static string ImeiOf(string raw, string f)
        {
            var c = DeviceMath.Classify(raw);
            if (c.Kind != "imei") throw new KiteException(ErrorCodes.ImeiInvalid,
                c.Value.Length == 15 ? $"{c.Value} is not a valid IMEI: the check digit is wrong." : $"{c.Value} is not an IMEI: an IMEI has 15 digits.", 400, f);
            return c.Value;
        }
    }

    /// <summary>
    /// Creates a unit received on a document, its identifiers and its first stock movement, at its acquisition cost
    /// (11.3: received into stock, or waiting for inspection). Returns the unit and the movement value.
    /// </summary>
    public static async Task<(Guid UnitId, decimal Value, string PostingClass)> ReceiveAsync(DbTx t, Guid businessId, Guid branchId, Guid warehouseId,
        (Guid VariantId, string ProductType, string Tracking, Guid BrandKey, Guid? CategoryId) item, UnitInput u, string source, string docType, Guid docId,
        Guid lineId, string movementType, string field)
    {
        var (imei1, imei2, serial) = CheckIdentifiers(item.Tracking, u, field);
        var condition = u.Condition ?? "brand_new";
        if (condition is not ("brand_new" or "used" or "refurbished" or "damaged"))
            throw new KiteException(ErrorCodes.Validation, "A device comes in brand new, used, refurbished or damaged.", 400, field + ".condition");
        var status = u.Status ?? (condition == "brand_new" ? "in_stock" : "under_inspection");
        if (status is not ("in_stock" or "under_inspection"))
            throw new KiteException(ErrorCodes.Validation, "A device comes into stock, or waits for inspection.", 400, field + ".status");
        if (u.UnitCost is not { } cost || cost < 0 || cost != decimal.Round(cost, 3))
            throw new KiteException(ErrorCodes.Validation, "Give the device's cost (zero or more, in baisa).", 400, field + ".unitCost");
        if (u.SellingPrice is < 0) throw new KiteException(ErrorCodes.Validation, "The price cannot be negative.", 400, field + ".sellingPrice");
        if (u.GradeId is { } g) await CheckGradeAsync(t, g, item.CategoryId, field + ".gradeId");
        var entry = u.Entry ?? "scan";
        if (entry is not ("scan" or "manual")) throw new KiteException(ErrorCodes.Validation, "Entry is scan or manual.", 400, field + ".entry");

        var id = t.NewId();
        await t.ExecuteAsync("""
            INSERT INTO device.device_unit (id, business_id, branch_id, warehouse_id, variant_id, status, condition, grade_id, acquisition_cost, accumulated_cost,
                                            selling_price, warranty_days, source, source_doc_type, source_doc_id, created_by, origin_server_id)
            VALUES (@id, @businessId, @branchId, @warehouseId, @VariantId, @status, @condition, @GradeId, @cost, @cost, @SellingPrice, @WarrantyDays, @source,
                    @docType, @docId, @by, (SELECT id FROM core.server WHERE is_self))
            """, new { id, businessId, branchId, warehouseId, item.VariantId, status, condition, u.GradeId, cost, u.SellingPrice, u.WarrantyDays, source,
                       docType, docId, by = t.Actor.UserId });
        foreach (var (kind, slot, value) in new[] { ("imei", (short)1, imei1), ("imei", (short)2, imei2), ("serial", (short)1, serial) })
        {
            if (value is null) continue;
            await t.ExecuteAsync("""
                INSERT INTO device.imei_registry (id, business_id, device_unit_id, kind, slot, value, brand_key, entry, created_by)
                VALUES (@rid, @businessId, @id, @kind, @slot, @value, @BrandKey, @entry, @by)
                """, new { rid = t.NewId(), businessId, id, kind, slot, value, item.BrandKey, entry, by = t.Actor.UserId });
        }
        var unit = new DeviceUnitRow { Id = id, BusinessId = businessId, BranchId = branchId, WarehouseId = warehouseId, VariantId = item.VariantId };
        var (_, moved) = await MoveAsync(t, unit, movementType, 1, null, docType, docId, lineId, source);
        return (id, moved, PostingRuleTemplates.ClassOf(item.ProductType, condition));
    }

    public static async Task CheckGradeAsync(DbTx t, Guid gradeId, Guid? categoryId, string field)
    {
        var ok = await t.ExecuteScalarAsync<bool>("""
            SELECT EXISTS (SELECT 1 FROM device.grade WHERE id = @gradeId AND status = 'active' AND (category_id IS NULL OR category_id = @categoryId))
            """, new { gradeId, categoryId });
        if (!ok) throw new KiteException(ErrorCodes.Validation, "That grade does not exist or is not for this product's category.", 400, field);
    }

    /// <summary>A brand-new device whose condition changes moves its cost to the used-device inventory (12.5).</summary>
    public static async Task PostReclassAsync(DbTx t, Guid businessId, Guid branchId, string productType, string fromCondition, string toCondition,
        decimal amount, DateOnly date, string docType, Guid docId, string description)
    {
        var from = PostingRuleTemplates.ClassOf(productType, fromCondition);
        var to = PostingRuleTemplates.ClassOf(productType, toCondition);
        if (from == to || amount == 0m) return;
        var facts = new PostingFacts().Add("to_by_product_type", to, amount).Add("from_by_product_type", from, amount);
        await Posting.PostAsync(t, businessId, branchId, PostingRuleTemplates.DeviceReclass, facts, date, docType, docId, description);
    }

    public static async Task<DeviceSettings> SettingsAsync(DbTx t, Guid businessId) =>
        await t.QuerySingleOrDefaultAsync<DeviceSettings>("""
            SELECT labour_rate_per_hour AS LabourRatePerHour, reservation_minutes AS ReservationMinutes, parked_reservation_minutes AS ParkedReservationMinutes,
                   suggested_price_step AS SuggestedPriceStep, version FROM device.settings WHERE business_id = @businessId
            """, new { businessId })
        ?? new DeviceSettings(3.000m, 15, 240, 0.100m, 0);

    /// <summary>The variant's price when new, today, in the branch (for the grade's suggested price).</summary>
    public static async Task<decimal?> NewPriceAsync(DbTx t, Guid branchId, Guid variantId, DateOnly date) =>
        (await ItemPricing.LoadAsync(t, branchId, [variantId], date)).TryGetValue(variantId, out var item) ? item.ListPrice : null;

    public static string ConditionEn(string c) => c switch
    {
        "brand_new" => "New", "used" => "Used", "refurbished" => "Refurbished", "damaged" => "Damaged", "for_parts" => "For parts", _ => c,
    };

    public static string ConditionAr(string c) => c switch
    {
        "brand_new" => "جديد", "used" => "مستعمل", "refurbished" => "مجدد", "damaged" => "به ضرر", "for_parts" => "للقطع", _ => c,
    };
}

/// <summary>Business data Phase 5 needs: device settings, grades A, B and C, and the default inspection checklist.</summary>
public static class Phase5Seed
{
    private static readonly (string Code, string En, string Ar, decimal Percent)[] Grades =
    [
        ("A", "Like new", "كالجديد", 85m), ("B", "Good", "جيد", 70m), ("C", "Fair", "مقبول", 55m),
    ];

    public static readonly (string Code, string En, string Ar, string Kind, bool Required, int? Min, string OnFail)[] DefaultChecklist =
    [
        ("screen", "Screen: no cracks, dead pixels or burn-in", "الشاشة: بلا تشققات أو بكسلات معطلة أو احتراق", "pass_fail", true, null, "refurbish"),
        ("touch", "Touch works across the whole screen", "اللمس يعمل في كامل الشاشة", "pass_fail", true, null, "refurbish"),
        ("body", "Body and frame: no dents or cracks", "الهيكل والإطار: بلا انبعاجات أو تشققات", "pass_fail", true, null, "damaged"),
        ("buttons", "Buttons: power, volume, silent switch", "الأزرار: التشغيل والصوت ومفتاح الصامت", "pass_fail", true, null, "refurbish"),
        ("cameras", "Front and rear cameras and flash", "الكاميرات الأمامية والخلفية والفلاش", "pass_fail", true, null, "refurbish"),
        ("audio", "Speakers, earpiece and microphones", "مكبرات الصوت وسماعة المكالمات والميكروفونات", "pass_fail", true, null, "refurbish"),
        ("charging", "Charging port and charging", "منفذ الشحن والشحن", "pass_fail", true, null, "refurbish"),
        ("network", "SIM, mobile network, Wi-Fi and Bluetooth", "الشريحة والشبكة والواي فاي والبلوتوث", "pass_fail", true, null, "refurbish"),
        ("biometrics", "Face ID or fingerprint", "بصمة الوجه أو الإصبع", "pass_fail", true, null, "damaged"),
        ("water", "Water-damage indicator not triggered", "مؤشر التلف بالماء غير مُفعَّل", "pass_fail", true, null, "for_parts"),
        ("battery", "Battery health (%)", "صحة البطارية (%)", "percent", true, 80, "refurbish"),
        ("cloud_lock", "Cloud account and activation lock removed in front of staff", "حساب السحابة وقفل التفعيل مُزالان أمام الموظف", "pass_fail", true, null, "for_parts"),
        ("notes", "Cosmetic notes", "ملاحظات المظهر", "text", false, null, "refurbish"),
    ];

    public static async Task EnsureAsync(DbTx t, Guid businessId)
    {
        await t.ExecuteAsync("INSERT INTO device.settings (business_id) VALUES (@businessId) ON CONFLICT DO NOTHING", new { businessId });
        if (!await t.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM device.grade WHERE business_id = @businessId)", new { businessId }))
        {
            short sort = 0;
            foreach (var g in Grades)
                await t.ExecuteAsync("""
                    INSERT INTO device.grade (id, business_id, code, name_en, name_ar, price_percent, sort) VALUES (@id, @businessId, @Code, @En, @Ar, @Percent, @sort)
                    """, new { id = t.NewId(), businessId, g.Code, g.En, g.Ar, g.Percent, sort = sort++ });
        }
        if (!await t.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM device.checklist WHERE business_id = @businessId)", new { businessId }))
        {
            var cid = t.NewId();
            await t.ExecuteAsync("""
                INSERT INTO device.checklist (id, business_id, name_en, name_ar) VALUES (@cid, @businessId, 'Phone inspection', 'فحص الهاتف')
                """, new { cid, businessId });
            short sort = 0;
            foreach (var i in DefaultChecklist)
                await t.ExecuteAsync("""
                    INSERT INTO device.checklist_item (id, checklist_id, sort, code, label_en, label_ar, kind, required, min_percent, on_fail)
                    VALUES (@id, @cid, @sort, @Code, @En, @Ar, @Kind, @Required, @min, @OnFail)
                    """, new { id = t.NewId(), cid, sort = sort++, i.Code, i.En, i.Ar, i.Kind, i.Required, min = (short?)i.Min, i.OnFail });
        }
    }
}
