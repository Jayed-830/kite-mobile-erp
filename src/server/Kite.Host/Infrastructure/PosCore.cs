using Kite.Accounting;
using Kite.Persistence;
using Kite.Platform;
using Kite.Sales;

namespace Kite.Host.Infrastructure;

public sealed record BusinessInfo(Guid Id, string LegalNameEn, string LegalNameAr, string? TradeNameEn, string? TradeNameAr,
    string? VatNumber, string? CrNumber, bool PricesIncludeTax, string TimeZone, string? AddressEn, string? AddressAr, string? Phone)
{
    public string SellerNameEn => TradeNameEn ?? LegalNameEn;
    public string SellerNameAr => TradeNameAr ?? LegalNameAr;

    public static Task<BusinessInfo> LoadAsync(DbTx t) =>
        t.QuerySingleOrDefaultAsync<BusinessInfo>("""
            SELECT id, legal_name_en AS LegalNameEn, legal_name_ar AS LegalNameAr, trade_name_en AS TradeNameEn, trade_name_ar AS TradeNameAr,
                   vat_number AS VatNumber, cr_number AS CrNumber, prices_include_tax AS PricesIncludeTax, time_zone AS TimeZone,
                   address_en AS AddressEn, address_ar AS AddressAr, phone
              FROM core.business LIMIT 1
            """)!;
}

/// <summary>Dates as the shop sees them: the business time zone (Asia/Muscat by default).</summary>
public static class BusinessDay
{
    public static TimeZoneInfo Zone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.CreateCustomTimeZone("Gulf+4", TimeSpan.FromHours(4), "Gulf", "Gulf"); }
    }

    public static DateOnly DateOf(DateTimeOffset at, string tz) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, Zone(tz)).DateTime);
    public static DateOnly Today(string tz, IClock clock) => DateOf(clock.UtcNow, tz);
    public static DateTime Local(DateTimeOffset at, string tz) => TimeZoneInfo.ConvertTime(at, Zone(tz)).DateTime;
}

/// <summary>The terminal of the signed-in session, with its branch and the branch's shop warehouse.</summary>
public sealed record PosTerminal(Guid Id, Guid BranchId, string Code, string Name, string Kind, string Status, bool EmergencyEnabled,
    string BranchCode, string BranchNameEn, string BranchNameAr, string? BranchAddressEn, string? BranchAddressAr, string? BranchPhone,
    Guid WarehouseId, Guid? PrinterId);

public static class PosTerminals
{
    private const string Select = """
        SELECT t.id, t.branch_id AS BranchId, t.code, t.name, t.kind, t.status, t.emergency_enabled AS EmergencyEnabled,
               b.code AS BranchCode, b.name_en AS BranchNameEn, b.name_ar AS BranchNameAr, b.address_en AS BranchAddressEn,
               b.address_ar AS BranchAddressAr, b.phone AS BranchPhone,
               (SELECT w.id FROM core.warehouse w WHERE w.branch_id = t.branch_id AND w.status = 'active'
                 ORDER BY (w.kind = 'shop') DESC, (w.code = 'SHOP') DESC, w.created_at LIMIT 1) AS WarehouseId,
               t.printer_id AS PrinterId
          FROM core.terminal t JOIN core.branch b ON b.id = t.branch_id
        """;

    /// <summary>The session's terminal; POS work needs a sign-in on an approved terminal (18.1).</summary>
    public static async Task<PosTerminal> CurrentAsync(DbTx t, HttpContext http)
    {
        var id = http.User.TerminalIdOf()
            ?? throw new KiteException(ErrorCodes.NotATerminal, "Sign in on a POS terminal to do this.", 403);
        return await ByIdAsync(t, id);
    }

    public static async Task<PosTerminal> ByIdAsync(DbTx t, Guid id)
    {
        var term = await t.QuerySingleOrDefaultAsync<PosTerminal>(Select + " WHERE t.id = @id", new { id })
            ?? throw new KiteException(ErrorCodes.NotATerminal, "This terminal is not in your branches.", 403);
        if (term.Status != "active")
            throw new KiteException(ErrorCodes.TerminalNotActive, "This terminal is not approved. Ask a manager to approve it.", 403);
        return term;
    }
}

public static class PosPermissions
{
    /// <summary>The permissions a user holds in one branch (roles for all branches count everywhere).</summary>
    public static async Task<HashSet<string>> InBranchAsync(DbTx t, Guid userId, Guid branchId) =>
        (await t.QueryAsync<string>("""
            SELECT DISTINCT rp.permission_code
              FROM iam.user_branch_role ubr
              JOIN iam.role_permission rp ON rp.role_id = ubr.role_id
              JOIN iam."user" u ON u.id = ubr.user_id
             WHERE ubr.user_id = @userId AND (ubr.branch_id IS NULL OR ubr.branch_id = @branchId) AND u.status = 'active'
            """, new { userId, branchId })).ToHashSet(StringComparer.Ordinal);

    public static async Task<DiscountLimit> DiscountLimitAsync(DbTx t, HttpContext http, Guid branchId)
    {
        if (http.User.HasIn(Permissions.PosDiscountAboveLimit, branchId)) return DiscountLimit.Any;
        var r = await t.QuerySingleOrDefaultAsync<(decimal Percent, decimal Amount)>("""
            SELECT coalesce(max(r.discount_limit_percent), 0), coalesce(max(r.discount_limit_amount), 0)
              FROM iam.user_branch_role ubr JOIN iam.role r ON r.id = ubr.role_id
             WHERE ubr.user_id = @u AND (ubr.branch_id IS NULL OR ubr.branch_id = @branchId)
            """, new { u = http.User.UserIdOf(), branchId });
        return new DiscountLimit(r.Percent, r.Amount, false);
    }
}

/// <summary>Catalog facts for pricing a cart on a date in a branch.</summary>
public static class ItemPricing
{
    public const string DescriptionEn = "p.name_en || coalesce(' ' || nullif(v.colour_en, ''), '') || coalesce(' ' || v.storage_gb::text || 'GB', '')";
    public const string DescriptionAr = "p.name_ar || coalesce(' ' || nullif(v.colour_ar, ''), '') || coalesce(' ' || v.storage_gb::text || 'GB', '')";

    public static string Select(string where) => $"""
        SELECT v.id AS VariantId, v.sku, p.product_type AS ProductType, p.tracking,
               {DescriptionEn} AS DescriptionEn, {DescriptionAr} AS DescriptionAr,
               p.tax_code_id AS TaxCodeId, tc.kind AS TaxKind,
               CASE WHEN tc.kind = 'standard' THEN coalesce((SELECT r.rate_percent FROM tax.tax_rate r WHERE r.tax_code_id = tc.id
                        AND r.effective_from <= @d AND (r.effective_to IS NULL OR r.effective_to > @d)), 0) ELSE 0 END AS TaxRate,
               coalesce(bv.override_price, pr.price) AS ListPrice,
               CASE WHEN bv.override_price IS NOT NULL THEN bv.override_min_price ELSE coalesce(bv.override_min_price, pr.min_price) END AS MinPrice,
               p.default_warranty_days AS WarrantyDays,
               (v.status = 'active' AND p.status = 'active' AND tc.status = 'active') AS Active
          FROM catalog.variant v
          JOIN catalog.product p ON p.id = v.product_id
          JOIN tax.tax_code tc ON tc.id = p.tax_code_id
          LEFT JOIN catalog.branch_variant bv ON bv.variant_id = v.id AND bv.branch_id = @branchId
          LEFT JOIN catalog.price pr ON pr.variant_id = v.id
               AND pr.price_list_id = (SELECT pl.id FROM catalog.price_list pl WHERE pl.business_id = v.business_id AND pl.is_default)
               AND pr.effective_from <= @d AND (pr.effective_to IS NULL OR pr.effective_to > @d)
         WHERE {where}
        """;

    public static async Task<Dictionary<Guid, PricedItem>> LoadAsync(DbTx t, Guid branchId, IEnumerable<Guid> variantIds, DateOnly date)
    {
        var ids = variantIds.Distinct().ToArray();
        var rows = await t.QueryAsync<PricedItem>(Select("v.id = ANY(@ids)"), new { ids, branchId, d = date.ToDateTime(TimeOnly.MinValue) });
        return rows.ToDictionary(r => r.VariantId);
    }
}

public sealed record ApprovalRow(Guid Id, Guid BranchId, string ActionCode, string Method, string Status, Guid RequesterId, Guid? ApproverId,
    string Context, string? Reason, DateTime ExpiresAt);

public static class Approvals
{
    public const string Select = """
        SELECT id, branch_id AS BranchId, action_code AS ActionCode, method, status, requester_id AS RequesterId, approver_id AS ApproverId,
               context::text AS Context, reason, expires_at AS ExpiresAt FROM iam.approval
        """;

    /// <summary>Spends an approval on a document: it must be approved, unexpired, for this action, branch and requester.</summary>
    public static async Task<ApprovalRow> UseAsync(DbTx t, Guid id, Guid requesterId, Guid branchId, string action, string docType, Guid docId)
    {
        var a = await t.QuerySingleOrDefaultAsync<ApprovalRow>(Select + " WHERE id = @id FOR UPDATE", new { id })
            ?? throw new KiteException(ErrorCodes.ApprovalInvalid, "That approval was not found.", 400, "approvalId");
        if (a.BranchId != branchId || a.RequesterId != requesterId)
            throw new KiteException(ErrorCodes.ApprovalInvalid, "That approval was given for another cashier or branch.", 400, "approvalId");
        if (a.ActionCode != action)
            throw new KiteException(ErrorCodes.ApprovalInvalid, $"That approval is for {a.ActionCode}, not {action}.", 400, "approvalId");
        if (a.Status != "approved")
            throw new KiteException(ErrorCodes.ApprovalInvalid, a.Status == "used" ? "That approval was already used." : $"That approval is {a.Status}.", 400, "approvalId");
        if (DateTime.SpecifyKind(a.ExpiresAt, DateTimeKind.Utc) <= t.Clock.UtcNow.UtcDateTime)
            throw new KiteException(ErrorCodes.ApprovalInvalid, "That approval has expired. Ask again.", 400, "approvalId");
        await t.ExecuteAsync("""
            UPDATE iam.approval SET status = 'used', used_at = now(), used_by_doc_type = @docType, used_by_doc_id = @docId WHERE id = @id
            """, new { id, docType, docId });
        return a;
    }
}

/// <summary>A sale or credit note line ready to be written.</summary>
public sealed record DraftLine(
    Guid VariantId, string ProductType, string DescriptionEn, string DescriptionAr, decimal Qty, decimal UnitPrice, decimal ListPrice,
    decimal Discount, decimal Net, Guid TaxCodeId, decimal TaxRate, decimal Tax, decimal Gross, Guid? ApprovalId, int WarrantyDays,
    Guid? ReturnedLineId = null, bool? Restock = null, decimal? RestockUnitCost = null, Guid? DeviceUnitId = null, string? Condition = null)
{
    public bool Stocked => ProductType != "service";

    /// <summary>The key the posting rules use: the product type, or "device_used" for a device that is not brand new.</summary>
    public string PostingClass => PostingRuleTemplates.ClassOf(ProductType, Condition);

    public static DraftLine From(PricedLine l, Guid? approvalId, Guid? deviceUnitId = null, string? condition = null) =>
        new(l.Item.VariantId, l.Item.ProductType, l.Item.DescriptionEn, l.Item.DescriptionAr, l.Qty, l.UnitPrice, l.ListPrice,
            l.Discount, l.Net, l.Item.TaxCodeId, l.Item.TaxRate, l.Tax, l.Gross, approvalId, l.Item.WarrantyDays, DeviceUnitId: deviceUnitId, Condition: condition);
}

public sealed record SaleDraft(
    Guid Id, Guid ShiftId, Guid CashierId, Guid? CustomerId, string DocType, string? CreditReason, Guid? OriginalSaleId,
    Guid? ExchangeGroupId, DateTimeOffset CompletedAt, IReadOnlyList<DraftLine> Lines, CheckedPayments Payments, bool Refund,
    bool IsEmergency = false, string? EmergencyNumber = null, string? Note = null, Func<string, AuditEntry>? Audit = null);

public sealed record WrittenSale(Guid Id, string Number, decimal Gross, decimal CashRounding, int StockExceptions);

/// <summary>
/// Writes a completed sale or credit note in the caller's transaction (10.2, 10.3, 16.3, 17.3): the document and its
/// lines, stock movements at weighted-average cost, payments, the revenue and cost journals and the sync event.
/// </summary>
public static class SaleWriter
{
    public static async Task<WrittenSale> WriteAsync(DbTx t, BusinessInfo biz, PosTerminal term, PosPolicy policy, SaleDraft d)
    {
        var date = BusinessDay.DateOf(d.CompletedAt, biz.TimeZone);
        var isCredit = d.DocType == "credit_note";
        var number = d.IsEmergency
            ? d.EmergencyNumber!
            : await DocNumbers.NextAsync(t, biz.Id, term.BranchId, isCredit ? "credit_note" : "sale_invoice",
                await FiscalPeriods.FiscalYearOfAsync(t, biz.Id, date), isCredit ? "CN" : "INV");
        var net = d.Lines.Sum(l => l.Net);
        var tax = d.Lines.Sum(l => l.Tax);
        var gross = d.Lines.Sum(l => l.Gross);
        var discount = d.Lines.Sum(l => l.Discount);
        var einvoice = t.NewId();
        var qr = PosMath.QrPayload(biz.SellerNameEn, biz.VatNumber, d.CompletedAt, gross, tax, einvoice);

        await t.ExecuteAsync("""
            INSERT INTO sales.sale (id, business_id, branch_id, warehouse_id, terminal_id, shift_id, cashier_id, customer_id, number, doc_type,
                                    credit_reason, original_sale_id, exchange_group_id, sale_date, completed_at, prices_include_tax,
                                    net_total, discount_total, tax_total, gross_total, cash_rounding, is_emergency, emergency_number,
                                    einvoice_uuid, qr_payload, note, origin_server_id, origin_device_id)
            VALUES (@Id, @businessId, @branchId, @warehouseId, @terminalId, @ShiftId, @CashierId, @CustomerId, @number, @DocType,
                    @CreditReason, @OriginalSaleId, @ExchangeGroupId, @date::date, @CompletedAt, @pit,
                    @net, @discount, @tax, @gross, @rounding, @IsEmergency, @EmergencyNumber,
                    @einvoice, @qr, @Note, (SELECT id FROM core.server WHERE is_self), @terminalId)
            """, new { d.Id, businessId = biz.Id, branchId = term.BranchId, warehouseId = term.WarehouseId, terminalId = term.Id, d.ShiftId,
                       d.CashierId, d.CustomerId, number, d.DocType, d.CreditReason, d.OriginalSaleId, d.ExchangeGroupId,
                       date = date.ToDateTime(TimeOnly.MinValue), d.CompletedAt, pit = biz.PricesIncludeTax, net, discount, tax, gross,
                       rounding = d.Payments.CashRounding, d.IsEmergency, d.EmergencyNumber, einvoice, qr, d.Note });

        var facts = new PostingFacts();
        var costFacts = new PostingFacts();
        var exceptions = 0;
        var lineIds = d.Lines.Select(_ => t.NewId()).ToArray();
        var costs = new decimal[d.Lines.Count];
        // Stock first, in variant (then unit) order, so two documents never wait on each other's rows in opposite order.
        // A device unit always moves: out at its own cost when sold, back in at that cost when returned or voided (16.3).
        foreach (var i in Enumerable.Range(0, d.Lines.Count).Where(i => d.Lines[i].Stocked).OrderBy(i => d.Lines[i].VariantId).ThenBy(i => d.Lines[i].DeviceUnitId))
        {
            var l = d.Lines[i];
            if (isCredit && l.Restock != true && l.DeviceUnitId is null) continue;
            var type = isCredit ? "return" : d.IsEmergency ? "emergency_sale" : "sale";
            var m = await t.QuerySingleOrDefaultAsync<(decimal Value, decimal QtyAfter)>("""
                INSERT INTO inv.stock_movement (id, business_id, branch_id, warehouse_id, variant_id, movement_type, qty, unit_cost, device_unit_id,
                                                source_doc_type, source_doc_id, source_line_id, movement_at, created_by, origin_server_id, origin_device_id)
                VALUES (@id, @businessId, @branchId, @warehouseId, @VariantId, @type, @qty, @unitCost, @DeviceUnitId, 'sale', @saleId, @lineId, @at,
                        @by, (SELECT id FROM core.server WHERE is_self), @terminalId)
                RETURNING value, qty_after
                """, new { id = t.NewId(), businessId = biz.Id, branchId = term.BranchId, warehouseId = term.WarehouseId, l.VariantId, type,
                           qty = isCredit ? l.Qty : -l.Qty, unitCost = isCredit && l.DeviceUnitId is null ? l.RestockUnitCost : null, l.DeviceUnitId, saleId = d.Id,
                           lineId = lineIds[i], at = d.CompletedAt, by = d.CashierId, terminalId = term.Id });
            costs[i] = Math.Abs(m.Value);
            if (!isCredit && m.QtyAfter < 0 && l.DeviceUnitId is null)
            {
                if (policy.NegativeStock == "block" && !d.IsEmergency)
                    throw new KiteException(ErrorCodes.InsufficientStock, $"Not enough stock of {l.DescriptionEn}: {m.QtyAfter + l.Qty:0.###} left.", 409);
                exceptions++;
                await t.ExecuteAsync("""
                    INSERT INTO inv.stock_exception (id, business_id, branch_id, warehouse_id, variant_id, kind, qty_after, source_doc_type, source_doc_id)
                    VALUES (@id, @businessId, @branchId, @warehouseId, @VariantId, 'negative_stock', @QtyAfter, 'sale', @saleId)
                    """, new { id = t.NewId(), businessId = biz.Id, branchId = term.BranchId, warehouseId = term.WarehouseId, l.VariantId, m.QtyAfter, saleId = d.Id });
            }
            costFacts.Add("cost_by_product_type", l.PostingClass, costs[i]);
        }

        for (var i = 0; i < d.Lines.Count; i++)
        {
            var l = d.Lines[i];
            await t.ExecuteAsync("""
                INSERT INTO sales.sale_line (id, sale_id, line_no, variant_id, product_type, description_en, description_ar, qty, unit_price,
                                             list_price, discount_amount, net_amount, tax_code_id, tax_rate, tax_amount, gross_amount, cost_value,
                                             approval_id, returned_line_id, restock, warranty_until, device_unit_id)
                VALUES (@id, @saleId, @no, @VariantId, @ProductType, @DescriptionEn, @DescriptionAr, @Qty, @UnitPrice, @ListPrice, @Discount,
                        @Net, @TaxCodeId, @TaxRate, @Tax, @Gross, @cost, @ApprovalId, @ReturnedLineId, @Restock, @warranty::date, @DeviceUnitId)
                """, new { id = lineIds[i], saleId = d.Id, no = (short)(i + 1), l.VariantId, l.ProductType, l.DescriptionEn, l.DescriptionAr, l.Qty,
                           l.UnitPrice, l.ListPrice, l.Discount, l.Net, l.TaxCodeId, l.TaxRate, l.Tax, l.Gross, cost = costs[i], l.ApprovalId,
                           l.ReturnedLineId, l.Restock,
                           warranty = !isCredit && l.WarrantyDays > 0 ? date.AddDays(l.WarrantyDays).ToDateTime(TimeOnly.MinValue) : (DateTime?)null, l.DeviceUnitId });
            facts.Add("net_by_product_type", l.PostingClass, l.Net);
        }

        // Each device unit moves on in its lifecycle (11.3): sold; back to inspection when returned; back in stock when voided.
        var unitIds = d.Lines.Where(l => l.DeviceUnitId is not null).Select(l => l.DeviceUnitId!.Value).OrderBy(x => x).ToArray();
        foreach (var unitId in unitIds)
        {
            var (evt, to) = !isCredit ? ("sold", "sold") : d.CreditReason == "void" ? ("sale_voided", "in_stock") : ("returned", "returned_inspection");
            await DeviceUnits.ApplyEventAsync(t, unitId, evt, to, docType: "sale", docId: d.Id, details: new { number });
        }
        if (unitIds.Length > 0 && !isCredit)
            await t.ExecuteAsync("DELETE FROM device.reservation WHERE device_unit_id = ANY(@unitIds)", new { unitIds });

        foreach (var p in d.Payments.Tenders)
        {
            await t.ExecuteAsync("""
                INSERT INTO sales.payment (id, business_id, branch_id, sale_id, shift_id, method, amount, tendered, change_given,
                                           card_terminal, approval_code, reference, created_at)
                VALUES (@id, @businessId, @branchId, @saleId, @ShiftId, @Method, @amount, @Tendered, @Change, @CardTerminal, @ApprovalCode, @Reference, @at)
                """, new { id = t.NewId(), businessId = biz.Id, branchId = term.BranchId, saleId = d.Id, d.ShiftId, p.Method,
                           amount = d.Refund ? -p.Amount : p.Amount, p.Tendered, p.Change, p.CardTerminal, p.ApprovalCode, p.Reference, at = d.CompletedAt });
            facts.Add("tenders", p.Method, p.Amount);
        }
        facts.Set("tax", tax).Set("cash_rounding", d.Payments.CashRounding);

        var description = $"{(isCredit ? "Credit note" : "Sale")} {number}";
        await Posting.PostAsync(t, biz.Id, term.BranchId, isCredit ? PostingRuleTemplates.CreditNote : PostingRuleTemplates.Sale,
            facts, date, "sale", d.Id, description, d.CustomerId);
        await Posting.PostAsync(t, biz.Id, term.BranchId, isCredit ? PostingRuleTemplates.CreditNoteCost : PostingRuleTemplates.SaleCost,
            costFacts, date, "sale", d.Id, description + " (cost)");

        // Audit before the sync event: every transaction takes the audit-chain lock before the outbox lock, so two never wait on each other.
        if (d.Audit is not null) await t.AuditAsync(d.Audit(number));
        await t.PublishAsync(isCredit ? "sales.credit_note_completed" : "sales.sale_completed", "sale", d.Id, 1,
            Json.Obj(new { number, d.DocType, d.CreditReason, d.OriginalSaleId, terminal = term.Code, gross, tax, d.IsEmergency }), term.BranchId);
        return new WrittenSale(d.Id, number, gross, d.Payments.CashRounding, exceptions);
    }
}
