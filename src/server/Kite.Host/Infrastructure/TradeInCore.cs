using System.Text;
using Kite.Accounting;
using Kite.Devices;
using Kite.Persistence;
using Kite.Platform;
using Kite.Sales;
using Kite.Host.Features;

namespace Kite.Host.Infrastructure;

/// <summary>One trade-in with its customer, model, branch and grade (a class: Dapper maps it by name).</summary>
public sealed class TradeInRow
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }
    public Guid BranchId { get; set; }
    public string BranchCode { get; set; } = "";
    public string Number { get; set; } = "";
    public string Status { get; set; } = "";
    public Guid CustomerId { get; set; }
    public string CustomerName { get; set; } = "";
    public string? CustomerNameAr { get; set; }
    public string? CustomerPhone { get; set; }
    public string? CustomerVat { get; set; }
    public byte[]? CivilIdEnc { get; set; }
    public string? CivilIdLast4 { get; set; }
    public string? SellerVatNumber { get; set; }
    public Guid VariantId { get; set; }
    public string Sku { get; set; } = "";
    public string DescriptionEn { get; set; } = "";
    public string DescriptionAr { get; set; } = "";
    public string ProductType { get; set; } = "";
    public string Tracking { get; set; } = "";
    public Guid? CategoryId { get; set; }
    public Guid BrandKey { get; set; }
    public string? BrandName { get; set; }
    public string? Imei1 { get; set; }
    public string? Imei2 { get; set; }
    public string? Serial { get; set; }
    public string Entry { get; set; } = "scan";
    public string? Storage { get; set; }
    public string? Colour { get; set; }
    public Guid? GradeId { get; set; }
    public string? GradeCode { get; set; }
    public string? GradeNameEn { get; set; }
    public string? GradeNameAr { get; set; }
    public string Condition { get; set; } = "used";
    public string Inspection { get; set; } = "{}";
    public short? BatteryHealth { get; set; }
    public bool CloudLockRemoved { get; set; }
    public decimal? GuideValue { get; set; }
    public decimal DeductionsTotal { get; set; }
    public decimal? OfferedValue { get; set; }
    public decimal? AgreedValue { get; set; }
    public Guid? ApprovalId { get; set; }
    public string? ConsentMethod { get; set; }
    public string? ConsentName { get; set; }
    public string? ConsentReference { get; set; }
    public string? ConsentText { get; set; }
    public DateTime? ConsentAt { get; set; }
    public Guid? SaleId { get; set; }
    public Guid? TerminalId { get; set; }
    public Guid? DeviceUnitId { get; set; }
    public bool Reconnected { get; set; }
    public bool MarginScheme { get; set; }
    public string? SelfInvoiceNumber { get; set; }
    public string? Note { get; set; }
    public string? RejectReason { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public string? AcceptedByName { get; set; }
    public DateTime? SettledAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid CreatedBy { get; set; }
    public string CreatedByName { get; set; } = "";
    public int Version { get; set; }

    public string? CivilIdMasked => CivilIdLast4 is null ? null : "•••• " + CivilIdLast4;
    public string Identifier => Imei1 ?? Serial ?? Number;
}

public sealed record TradeInSettingsRow(decimal StaffLimit, decimal OverGuidePercent, bool RequireCivilId, string DeclarationEn, string DeclarationAr, int Version);
public sealed record MarginSchemeRow(bool Approved, string? Reference, DateTime? ApprovedOn, string? Note, int Version);

/// <summary>What the business already knows about the identifiers of a device offered as a trade-in.</summary>
public sealed record TradeInIdentity(DeviceUnitRow? Reconnect, List<string> Notes);

/// <summary>Trade-ins (blueprint 12.4, 12.5): loading, valuing, the identity checks, accepting the device and settling it.</summary>
public static class TradeInCore
{
    public const string Select = $"""
        SELECT t.id, t.business_id AS BusinessId, t.branch_id AS BranchId, b.code AS BranchCode, t.number, t.status, t.customer_id AS CustomerId,
               c.name_en AS CustomerName, c.name_ar AS CustomerNameAr, c.phone_e164 AS CustomerPhone, c.vat_number AS CustomerVat,
               t.civil_id_enc AS CivilIdEnc, t.civil_id_last4 AS CivilIdLast4, t.seller_vat_number AS SellerVatNumber,
               t.variant_id AS VariantId, v.sku, {ItemPricing.DescriptionEn} AS DescriptionEn, {ItemPricing.DescriptionAr} AS DescriptionAr,
               p.product_type AS ProductType, p.tracking, p.category_id AS CategoryId, coalesce(p.brand_id, p.id) AS BrandKey, br.name_en AS BrandName,
               t.imei1, t.imei2, t.serial, t.entry, t.storage, t.colour, t.grade_id AS GradeId, g.code AS GradeCode, g.name_en AS GradeNameEn,
               g.name_ar AS GradeNameAr, t.condition, t.inspection::text AS Inspection, t.battery_health AS BatteryHealth,
               t.cloud_lock_removed AS CloudLockRemoved, t.guide_value AS GuideValue, t.deductions_total AS DeductionsTotal, t.offered_value AS OfferedValue,
               t.agreed_value AS AgreedValue, t.approval_id AS ApprovalId, t.consent_method AS ConsentMethod, t.consent_name AS ConsentName,
               t.consent_reference AS ConsentReference, t.consent_text AS ConsentText, t.consent_at AS ConsentAt, t.sale_id AS SaleId,
               t.terminal_id AS TerminalId, t.device_unit_id AS DeviceUnitId, t.reconnected, t.margin_scheme AS MarginScheme,
               t.self_invoice_number AS SelfInvoiceNumber, t.note, t.reject_reason AS RejectReason, t.accepted_at AS AcceptedAt,
               ua.display_name_en AS AcceptedByName, t.settled_at AS SettledAt, t.closed_at AS ClosedAt, t.created_at AS CreatedAt,
               t.created_by AS CreatedBy, uc.display_name_en AS CreatedByName, t.version
          FROM tradein.tradein t
          JOIN core.branch b ON b.id = t.branch_id JOIN crm.customer c ON c.id = t.customer_id
          JOIN catalog.variant v ON v.id = t.variant_id JOIN catalog.product p ON p.id = v.product_id LEFT JOIN catalog.brand br ON br.id = p.brand_id
          LEFT JOIN device.grade g ON g.id = t.grade_id JOIN iam."user" uc ON uc.id = t.created_by LEFT JOIN iam."user" ua ON ua.id = t.accepted_by
        """;

    public static async Task<TradeInRow> LoadAsync(DbTx t, Guid id, bool forUpdate = false)
    {
        if (forUpdate) await t.ExecuteAsync("SELECT 1 FROM tradein.tradein WHERE id = @id FOR UPDATE", new { id });
        return await t.QuerySingleOrDefaultAsync<TradeInRow>(Select + " WHERE t.id = @id", new { id })
            ?? throw new KiteException(ErrorCodes.NotFound, "That trade-in is not in your branches.", 404);
    }

    public static async Task<TradeInSettingsRow> SettingsAsync(DbTx t)
    {
        var s = await t.QuerySingleOrDefaultAsync<TradeInSettingsRow>("""
            SELECT staff_limit AS StaffLimit, over_guide_percent AS OverGuidePercent, require_civil_id AS RequireCivilId,
                   declaration_en AS DeclarationEn, declaration_ar AS DeclarationAr, version FROM tradein.settings LIMIT 1
            """);
        return s ?? throw new KiteException(ErrorCodes.Internal, "Trade-in settings are missing.", 500);
    }

    public static async Task<MarginSchemeRow> MarginAsync(DbTx t) =>
        await t.QuerySingleOrDefaultAsync<MarginSchemeRow>("""
            SELECT approved, reference, approved_on AS ApprovedOn, note, version FROM tax.margin_scheme LIMIT 1
            """) ?? new MarginSchemeRow(false, null, null, null, 0);

    /// <summary>Whether a device bought or sold on this date falls under the profit margin mechanism (approval recorded and in force).</summary>
    public static bool MarginOn(MarginSchemeRow m, DateOnly date) => m.Approved && m.ApprovedOn is { } on && DateOnly.FromDateTime(on) <= date;

    /// <summary>The business's VAT-MARGIN code and its rate on a date.</summary>
    public static async Task<(Guid Id, decimal Rate)> MarginCodeAsync(DbTx t, DateOnly date)
    {
        var c = await t.QuerySingleOrDefaultAsync<(Guid Id, decimal? Rate)>("""
            SELECT c.id, tax.rate_on(c.id, @d::date) FROM tax.tax_code c WHERE c.kind = 'margin' AND c.status = 'active' ORDER BY c.code LIMIT 1
            """, new { d = date.ToDateTime(TimeOnly.MinValue) });
        if (c.Id == Guid.Empty || c.Rate is null)
            throw new KiteException(ErrorCodes.Validation, "The VAT-MARGIN tax code is not active or has no rate on this date. Record the margin scheme approval in Tax settings.", 409);
        return (c.Id, c.Rate.Value);
    }

    public static async Task<decimal?> GuideAsync(DbTx t, Guid variantId, Guid? gradeId) =>
        gradeId is null ? null : await t.ExecuteScalarAsync<decimal?>("""
            SELECT base_value FROM tradein.price_guide WHERE variant_id = @variantId AND grade_id = @gradeId AND status = 'active'
            """, new { variantId, gradeId });

    public static async Task<decimal> OutstandingAsync(DbTx t, Guid id) =>
        await t.ExecuteScalarAsync<decimal?>("SELECT tradein.outstanding(@id)", new { id }) ?? 0m;

    /// <summary>The civil ID as stored: letters and digits only, upper case (6 to 20 characters).</summary>
    public static string NormCivilId(string raw)
    {
        var clean = new string(raw.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (clean.Length is < 6 or > 20) throw new KiteException(ErrorCodes.TradeInCivilId, "A civil ID is 6 to 20 letters and digits.", 400, "civilId");
        return clean;
    }

    public static byte[] Protect(Kite.Identity.ISecretProtector secrets, string civilId) => secrets.Protect(Encoding.UTF8.GetBytes(civilId));

    /// <summary>
    /// What the business already knows about a device offered as a trade-in (11.5): an identifier on the blocked list stops it;
    /// a device the business sold reconnects to its unit (11.1); a device still the business's anywhere, or registered at head
    /// office to a device that was not sold, is refused.
    /// </summary>
    public static async Task<TradeInIdentity> CheckIdentityAsync(DbTx t, IReadOnlyList<PushIdentifier> ids, Guid brandKey)
    {
        DeviceUnitRow? reconnect = null;
        var notes = new List<string>();
        foreach (var i in ids)
        {
            var label = i.Kind == "serial" ? "Serial number" : "IMEI";
            if (await CentralRegistry.BlockedReasonAsync(t, i.Kind, i.Value) is { } why)
                throw new KiteException(ErrorCodes.ImeiBlocked, $"{label} {i.Value} is on the blocked list: {why}. Do not buy this device.", 409, "device");
            var unit = await t.WithAllBranchesAsync(() => t.QuerySingleOrDefaultAsync<DeviceUnitRow>(DeviceUnits.Select + """
                 WHERE EXISTS (SELECT 1 FROM device.imei_registry r WHERE r.device_unit_id = u.id AND r.value = @Value AND r.status = 'active'
                                 AND (CASE WHEN @Kind = 'serial' THEN r.kind = 'serial' AND r.brand_key = @brandKey ELSE r.kind IN ('imei','eid') END))
                 LIMIT 1
                """, new { i.Value, i.Kind, brandKey }));
            if (unit is null) continue;
            if (unit.Status != "sold")
                throw new KiteException(ErrorCodes.TradeInDevice,
                    $"{label} {i.Value} belongs to a device that is {unit.Status.Replace('_', ' ')} at {unit.BranchCode} ({unit.DescriptionEn}): it is already the business's.", 409, "device");
            if (reconnect is not null && reconnect.Id != unit.Id)
                throw new KiteException(ErrorCodes.TradeInDevice, "These identifiers belong to two different devices the business sold. Check the device.", 409, "device");
            reconnect = unit;
        }
        if (reconnect is not null) notes.Add($"The business sold this device before (at {reconnect.BranchCode}); it continues the same device record.");
        if (t.Server.IsHeadOffice)
            foreach (var c in await CentralRegistry.CheckAsync(t, ids, reconnect?.Id))
                if (c.Holder is { } h && h.UnitStatus != "sold")
                    throw new KiteException(ErrorCodes.TradeInDevice,
                        $"{(c.Kind == "serial" ? "Serial number" : "IMEI")} {c.Value} is registered: {CentralRegistry.Describe(h)}. It is already the business's.", 409, "device");
                else if (c.Holder is { } sold && reconnect is null)
                    notes.Add($"Sold by the business at {sold.BranchCode}; that record will be replaced by this trade-in.");
        return new TradeInIdentity(reconnect, notes);
    }

    /// <summary>A branch server's answer from head office before the trade-in (null: head office cannot be reached).</summary>
    public static void RefuseCentral(IReadOnlyList<CentralCheckResult> answer)
    {
        foreach (var c in answer)
        {
            var label = c.Kind == "serial" ? "Serial number" : "IMEI";
            if (c.State == "blocked")
                throw new KiteException(ErrorCodes.ImeiBlocked, $"{label} {c.Value} is on the blocked list: {c.BlockedReason}. Do not buy this device.", 409, "device");
            if (c.Holder is { } h && h.UnitStatus != "sold")
                throw new KiteException(ErrorCodes.TradeInDevice, $"{label} {c.Value} is registered: {CentralRegistry.Describe(h)}. It is already the business's.", 409, "device");
        }
    }

    public static List<PushIdentifier> IdsOf(string? imei1, string? imei2, string? serial, string? brandName, Guid productId)
    {
        var ids = new List<PushIdentifier>();
        if (imei1 is not null) ids.Add(new PushIdentifier("imei", 1, imei1));
        if (imei2 is not null) ids.Add(new PushIdentifier("imei", 2, imei2));
        if (serial is not null) ids.Add(new PushIdentifier("serial", 1, serial, brandName is { } b ? CentralRegistry.NormBrand(b) : "product:" + productId));
        return ids;
    }

    /// <summary>
    /// Settles part or all of what is left on an accepted trade-in as store credit or cash from this terminal's drawer (12.5),
    /// with the permission or a manager's approval. Posts its journal. Returns the settlement's id.
    /// </summary>
    /// <summary>
    /// Locks in the one order every writer of trade-in credit and store credit uses (shift, then trade-ins by id, then the
    /// customer's store credit, then document numbers and journals), so concurrent tills wait for each other instead of deadlocking.
    /// </summary>
    public static async Task LockForPaymentAsync(DbTx t, IEnumerable<Guid> tradeIns, Guid? creditCustomer)
    {
        var ids = tradeIns.Distinct().Order().ToArray();
        if (ids.Length > 0) await t.ExecuteAsync("SELECT id FROM tradein.tradein WHERE id = ANY(@ids) ORDER BY id FOR UPDATE", new { ids });
        if (creditCustomer is { } c) await LockCreditAsync(t, c);
    }

    /// <summary>The same lock the store-credit ledger's trigger takes, taken earlier.</summary>
    public static Task LockCreditAsync(DbTx t, Guid customerId) =>
        t.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended('kite:store_credit:' || @c::text, 0))", new { c = customerId });

    public static async Task<Guid> SettleAsync(DbTx t, HttpContext http, TradeInRow ti, string kind, decimal? amount, Guid? approvalId, PosTerminal? term,
        Func<DbTx, PosTerminal, Task<(ShiftRow Shift, decimal ExpectedCash)>>? drawer)
    {
        if (ti.Status != "accepted") throw new KiteException(ErrorCodes.TradeInState, $"Trade-in {ti.Number} is {ti.Status}: nothing is left to settle.", 409);
        var left = await OutstandingAsync(t, ti.Id);
        var value = amount ?? left;
        if (value <= 0 || value != Currency.OMR.Round(value)) throw new KiteException(ErrorCodes.Validation, "The amount is above zero, in baisa.", 400, "amount");
        if (value > left) throw new KiteException(ErrorCodes.TradeInSettlement, $"Only {left:0.000} is left to settle on trade-in {ti.Number}.", 409, "amount");
        var uid = http.User.UserIdOf();
        var biz = await BusinessInfo.LoadAsync(t);
        var today = BusinessDay.Today(biz.TimeZone, t.Clock);
        var id = t.NewId();
        switch (kind)
        {
            case "store_credit":
            {
                if (!http.User.HasIn(Permissions.TradeInStoreCredit, ti.BranchId))
                {
                    if (approvalId is null) throw new KiteException(ErrorCodes.Forbidden, "Leaving a trade-in as store credit needs a manager's approval.", 403, "approvalId");
                    await Approvals.UseAsync(t, approvalId.Value, uid, ti.BranchId, Permissions.TradeInStoreCredit, "tradein", ti.Id);
                }
                var entry = t.NewId();
                await t.ExecuteAsync("""
                    INSERT INTO crm.credit_ledger (id, business_id, branch_id, customer_id, balance_type, amount, source_doc_type, source_doc_id, note, created_by, origin_server_id)
                    VALUES (@entry, @b, @branch, @customer, 'store_credit', @value, 'tradein', @tid, @note, @uid, (SELECT id FROM core.server WHERE is_self))
                    """, new { entry, b = biz.Id, branch = ti.BranchId, customer = ti.CustomerId, value, tid = ti.Id, note = $"Trade-in {ti.Number}", uid });
                await t.ExecuteAsync("""
                    INSERT INTO tradein.settlement (id, business_id, branch_id, tradein_id, kind, amount, credit_entry_id, created_by)
                    VALUES (@id, @b, @branch, @tid, 'store_credit', @value, @entry, @uid)
                    """, new { id, b = biz.Id, branch = ti.BranchId, tid = ti.Id, value, entry, uid });
                await Posting.PostAsync(t, biz.Id, ti.BranchId, PostingRuleTemplates.TradeInStoreCredit, new PostingFacts().Set("amount", value), today,
                    "tradein_settlement", id, $"Trade-in {ti.Number} left as store credit for {ti.CustomerName}", ti.CustomerId);
                break;
            }
            case "cash":
            {
                if (term is null || drawer is null) throw new KiteException(ErrorCodes.NotATerminal, "Pay a trade-in out in cash at a till with an open shift.", 403);
                if (term.BranchId != ti.BranchId) throw new KiteException(ErrorCodes.TradeInSettlement, $"Trade-in {ti.Number} is paid out at {ti.BranchCode}, where the device was bought.", 409);
                if (!http.User.HasIn(Permissions.TradeInPayout, ti.BranchId))
                {
                    if (approvalId is null) throw new KiteException(ErrorCodes.Forbidden, "Paying a trade-in out in cash needs a manager's approval.", 403, "approvalId");
                    await Approvals.UseAsync(t, approvalId.Value, uid, ti.BranchId, Permissions.TradeInPayout, "tradein", ti.Id);
                }
                var (shift, expected) = await drawer(t, term);
                if (value > expected) throw new KiteException(ErrorCodes.Validation, $"The drawer holds {expected:0.000}; it cannot pay out {value:0.000}.", 400, "amount");
                var cashEvent = t.NewId();
                await t.ExecuteAsync("""
                    INSERT INTO sales.cash_event (id, business_id, branch_id, shift_id, kind, amount, reason, created_by, tradein_id)
                    VALUES (@cashEvent, @b, @branch, @shift, 'tradein_payout', @value, @reason, @uid, @tid)
                    """, new { cashEvent, b = biz.Id, branch = ti.BranchId, shift = shift.Id, value, reason = $"Trade-in {ti.Number} paid out", uid, tid = ti.Id });
                await t.ExecuteAsync("""
                    INSERT INTO tradein.settlement (id, business_id, branch_id, tradein_id, kind, amount, shift_id, cash_event_id, created_by)
                    VALUES (@id, @b, @branch, @tid, 'cash', @value, @shift, @cashEvent, @uid)
                    """, new { id, b = biz.Id, branch = ti.BranchId, tid = ti.Id, value, shift = shift.Id, cashEvent, uid });
                await Posting.PostAsync(t, biz.Id, ti.BranchId, PostingRuleTemplates.TradeInPayout, new PostingFacts().Set("amount", value), today,
                    "tradein_settlement", id, $"Trade-in {ti.Number} paid out in cash, shift {shift.Number}", ti.CustomerId);
                break;
            }
            default:
                throw new KiteException(ErrorCodes.Validation, "Settle as store_credit or cash; trade-in credit for a sale is a payment at the till.", 400, "kind");
        }
        await t.AuditAsync(new AuditEntry("tradein.settled", "tradein", ti.Id, After: Json.Obj(new { ti.Number, kind, amount = value, approvalId }), BranchId: ti.BranchId));
        await t.PublishAsync("tradein.settled", "tradein", ti.Id, 1, Json.Obj(new { ti.Number, kind, amount = value }), ti.BranchId);
        return id;
    }

    /// <summary>
    /// Brings the device into stock under inspection at the agreed value (12.4): a device the business sold reconnects to its unit,
    /// any other becomes a new unit with the scanned identifiers. Returns the unit.
    /// </summary>
    public static async Task<Guid> AcquireDeviceAsync(DbTx t, TradeInRow ti, PosTerminal term, decimal agreed, DeviceUnitRow? reconnect, string source)
    {
        var uid = t.Actor.UserId;
        Guid unitId;
        if (reconnect is not null)
        {
            unitId = reconnect.Id;
            await t.ExecuteAsync("""
                SELECT device.reacquire(@unit, @branch, @warehouse, @agreed, @condition, 'tradein', @tid, @details::jsonb, @uid)
                """, new { unit = unitId, branch = term.BranchId, warehouse = term.WarehouseId, agreed, condition = ti.Condition, tid = ti.Id,
                           details = Json.Obj(new { ti.Number, agreed }).ToJsonString(), uid });
            // The unit may come from another branch of this server (sold there): its check row moves with it.
            await t.WithAllBranchesAsync(() => t.ExecuteAsync("""
                INSERT INTO device.central_check (device_unit_id, business_id, branch_id, state) VALUES (@unitId, @b, @branch, 'pending')
                ON CONFLICT (device_unit_id) DO UPDATE SET state = 'pending', branch_id = excluded.branch_id, attempts = 0, last_error = NULL, updated_at = now()
                """, new { unitId, b = ti.BusinessId, branch = term.BranchId }));
        }
        else
        {
            unitId = t.NewId();
            await t.ExecuteAsync("""
                INSERT INTO device.device_unit (id, business_id, branch_id, warehouse_id, variant_id, status, condition, acquisition_cost, accumulated_cost,
                                                source, source_doc_type, source_doc_id, created_by, origin_server_id)
                VALUES (@unitId, @b, @branch, @warehouse, @variant, 'under_inspection', @condition, @agreed, @agreed, @source, 'tradein', @tid, @uid,
                        (SELECT id FROM core.server WHERE is_self))
                """, new { unitId, b = ti.BusinessId, branch = term.BranchId, warehouse = term.WarehouseId, variant = ti.VariantId, condition = ti.Condition, agreed,
                           source, tid = ti.Id, uid });
            foreach (var (kind, slot, value) in new[] { ("imei", (short)1, ti.Imei1), ("imei", (short)2, ti.Imei2), ("serial", (short)1, ti.Serial) })
            {
                if (value is null) continue;
                await t.ExecuteAsync("""
                    INSERT INTO device.imei_registry (id, business_id, device_unit_id, kind, slot, value, brand_key, entry, created_by)
                    VALUES (@rid, @b, @unitId, @kind, @slot, @value, @brandKey, @entry, @uid)
                    """, new { rid = t.NewId(), b = ti.BusinessId, unitId, kind, slot, value, brandKey = ti.BrandKey, entry = ti.Entry, uid });
            }
            await t.ExecuteAsync("""
                INSERT INTO device.central_check (device_unit_id, business_id, branch_id, state) VALUES (@unitId, @b, @branch, 'pending')
                """, new { unitId, b = ti.BusinessId, branch = term.BranchId });
        }
        var moved = new DeviceUnitRow { Id = unitId, BusinessId = ti.BusinessId, BranchId = term.BranchId, WarehouseId = term.WarehouseId, VariantId = ti.VariantId };
        await DeviceUnits.MoveAsync(t, moved, "tradein_acquisition", 1, null, "tradein", ti.Id, null, $"Trade-in {ti.Number}", null, term.Id);
        if (ti.Entry == "manual")
            await t.AuditAsync(new AuditEntry("device.identifier_typed", "device_unit", unitId, After: Json.Obj(new { ti.Imei1, ti.Imei2, ti.Serial, tradein = ti.Number }), BranchId: term.BranchId));
        if (t.Server.IsHeadOffice) await CentralRegistry.SyncOwnUnitAsync(t, unitId);
        return unitId;
    }

    public static string DescribeSettlement(string kind) => kind switch { "sale" => "applied to a sale", "store_credit" => "store credit", _ => "cash" };
}
