using Kite.Accounting;
using Kite.Host.Features;
using Kite.Persistence;
using Kite.Platform;
using Kite.Sales;

namespace Kite.Host.Infrastructure;

// ---------------------------------------------------------------- requests (blueprint 16.1, 16.2)
public sealed record PurchPolicyInput(decimal PriceTolerancePct, decimal PriceToleranceAmount, decimal OverReceiptPct, string SupplierList, int Version);
public sealed record ApprovalLimitInput(Guid RoleId, Guid? BranchId, decimal? MaxAmount);
public sealed record OrderLineInput(Guid VariantId, decimal Qty, decimal UnitPrice, Guid? TaxCodeId = null, Guid? DeliverBranchId = null, Guid? RepairJobId = null);
public sealed record OrderInput(Guid BranchId, Guid SupplierId, List<OrderLineInput> Lines, DateOnly? OrderDate = null, DateOnly? ExpectedDate = null,
    string? Note = null, Guid? Id = null, int? Version = null);
public sealed record ReceiptLineInput(decimal Qty, Guid? PoLineId = null, Guid? VariantId = null, decimal? UnitPrice = null, Guid? TaxCodeId = null,
    List<UnitInput>? Units = null, Guid? RepairJobId = null);
public sealed record ChargeInput(string Kind, string Description, decimal Amount, string Basis = "value", Guid? SupplierId = null);
public sealed record ReceiptInput(Guid Id, Guid BranchId, Guid SupplierId, List<ReceiptLineInput> Lines, Guid? PoId = null, List<ChargeInput>? Charges = null,
    Guid? WarehouseId = null, DateOnly? ReceiptDate = null, string? DeliveryNote = null, string? Note = null);
public sealed record InvoiceLineInput(string Kind, decimal Qty, decimal UnitPrice, Guid? ReceiptLineId = null, Guid? ReceiptChargeId = null, string? OutsideType = null,
    Guid? OutsideId = null, Guid? AccountId = null, Guid? ExpenseBranchId = null, string? Description = null, Guid? TaxCodeId = null);
public sealed record InvoiceInput(Guid Id, Guid BranchId, Guid SupplierId, string SupplierInvoiceNo, DateOnly InvoiceDate, List<InvoiceLineInput> Lines,
    DateOnly? DueDate = null, string? Note = null, int? Version = null);
public sealed record ReturnLineInput(Guid ReceiptLineId, decimal Qty, List<Guid>? DeviceUnitIds = null);
public sealed record ReturnInput(Guid Id, Guid BranchId, Guid SupplierId, string Reason, List<ReturnLineInput> Lines, DateOnly? ReturnDate = null);
public sealed record PaymentAllocationInput(Guid EntryId, decimal Amount);
public sealed record SupplierPaymentInput(Guid Id, Guid BranchId, Guid SupplierId, string Method, decimal Amount, string? Reference = null, DateOnly? PaymentDate = null,
    string? Note = null, List<PaymentAllocationInput>? Allocations = null);

public sealed class PurchPolicy
{
    public decimal PriceTolerancePct { get; set; } = 2m;
    public decimal PriceToleranceAmount { get; set; } = 1m;
    public decimal OverReceiptPct { get; set; }
    public string SupplierList { get; set; } = "all";
    public int Version { get; set; } = 1;
    public const string Select = """
        SELECT price_tolerance_pct AS PriceTolerancePct, price_tolerance_amount AS PriceToleranceAmount, over_receipt_pct AS OverReceiptPct,
               supplier_list AS SupplierList, version FROM purch.policy
        """;
}

/// <summary>An invoice line as matched (16.2): what it clears from goods received not invoiced and where its price difference goes.</summary>
public sealed record MatchedLine(InvoiceLineInput Input, string Kind, Guid Branch, Guid? Warehouse, Guid? Variant, string? ProductType, string Description,
    decimal Net, Guid TaxCodeId, decimal TaxRate, decimal Tax, decimal Expected, decimal? OrderPrice, string Status, decimal Variance, decimal VarianceInventory,
    decimal VariancePpv, string? AccountCode, Guid? ReceiptLineId, bool Serialized, string? Problem);

/// <summary>Purchasing (blueprint 16): orders, receipts, supplier invoices matched three ways, returns and payments.</summary>
public static class PurchCore
{
    public static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static DateTime D(DateOnly d) => d.ToDateTime(TimeOnly.MinValue);

    // Lock order (ADR 54, 61, 68, 78): ... credit -> deposit -> purchase order -> goods receipts and outside work (sorted) -> supplier
    // -> document numbers -> stock -> journals.
    public static Task LockOrderAsync(DbTx t, Guid poId) =>
        t.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended('kite:po:' || @poId::text, 0))", new { poId });
    public static async Task LockSourcesAsync(DbTx t, IEnumerable<string> keys)
    {
        foreach (var k in keys.Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal))
            await t.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))", new { k });
    }
    public static string ReceiptKey(Guid receiptId) => "kite:grn:" + receiptId.ToString("D");
    public static string OutsideKey(string type, Guid id) => $"kite:grni:{type}:{id:D}";
    public static Task LockSupplierAsync(DbTx t, Guid supplierId) =>
        t.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended('kite:supplier:' || @supplierId::text, 0))", new { supplierId });

    public static async Task<PurchPolicy> PolicyAsync(DbTx t) =>
        await t.QuerySingleOrDefaultAsync<PurchPolicy>(PurchPolicy.Select + " LIMIT 1") ?? new PurchPolicy();

    public static async Task<bool> ModuleOnAsync(DbTx t) => await t.ExecuteScalarAsync<bool>("""
        SELECT coalesce((SELECT enabled FROM core.module_setting WHERE module_code = 'purch' AND branch_id IS NULL LIMIT 1), true)
        """);

    private static void Money(decimal v, string field, bool positive = false)
    {
        if (v < 0 || (positive && v == 0) || v != decimal.Round(v, 3))
            throw new KiteException(ErrorCodes.Validation, positive ? "The amount is above zero, in baisa (3 decimals)." : "The amount is zero or more, in baisa (3 decimals).", 400, field);
    }

    private static void Quantity(decimal q, string field)
    {
        if (q <= 0 || q != decimal.Round(q, 3)) throw new KiteException(ErrorCodes.Validation, "The quantity is above zero, with at most 3 decimals.", 400, field);
    }

    /// <summary>A supplier documents can be raised for: active, or provisional (added by a branch), never merged or inactive; on the branch's list when the policy says so.</summary>
    public static async Task<Supplier> SupplierForAsync(DbTx t, Guid supplierId, Guid branchId, PurchPolicy policy, bool forPayment = false)
    {
        var s = await SupplierCore.LoadAsync(t, supplierId) ?? throw new KiteException(ErrorCodes.NotFound, "That supplier was not found.", 404, "supplierId");
        if (s.Status == "merged") throw new KiteException(ErrorCodes.SupplierState, $"{s.NameEn} was merged into another supplier; use that one.", 409, "supplierId");
        if (s.Status == "inactive") throw new KiteException(ErrorCodes.SupplierState, $"{s.NameEn} is inactive.", 409, "supplierId");
        if (forPayment && s.Status == "provisional")
            throw new KiteException(ErrorCodes.SupplierState, $"{s.NameEn} was added by a branch and waits for head office to approve it; it can be paid once approved.", 409, "supplierId");
        if (policy.SupplierList == "approved" && !await t.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM purch.branch_supplier WHERE branch_id = @branchId AND supplier_id = @supplierId)", new { branchId, supplierId }))
            throw new KiteException(ErrorCodes.PurchSupplierNotAllowed, $"{s.NameEn} is not on this branch's approved supplier list.", 409, "supplierId");
        return s;
    }

    private sealed class ItemRow
    {
        public Guid Id { get; set; }
        public string ProductType { get; set; } = "";
        public string Tracking { get; set; } = "";
        public Guid TaxCodeId { get; set; }
        public Guid BrandKey { get; set; }
        public Guid? CategoryId { get; set; }
        public string Description { get; set; } = "";
        public string Status { get; set; } = "";
    }

    private static async Task<Dictionary<Guid, ItemRow>> ItemsAsync(DbTx t, IEnumerable<Guid> ids) =>
        (await t.QueryAsync<ItemRow>($"""
            SELECT v.id, p.product_type AS ProductType, p.tracking, p.tax_code_id AS TaxCodeId, coalesce(p.brand_id, p.id) AS BrandKey, p.category_id AS CategoryId,
                   {ItemPricing.DescriptionEn} AS Description, p.status
              FROM catalog.variant v JOIN catalog.product p ON p.id = v.product_id WHERE v.id = ANY(@ids)
            """, new { ids = ids.Distinct().ToArray() })).ToDictionary(x => x.Id);

    public static async Task<decimal> RateOnAsync(DbTx t, Guid taxCodeId, DateOnly on) =>
        await t.ExecuteScalarAsync<decimal?>("SELECT tax.rate_on(@taxCodeId, @on::date)", new { taxCodeId, on = D(on) })
        ?? throw new KiteException(ErrorCodes.Validation, "That tax code has no rate on that date.", 400, "taxCodeId");

    // ================================================================ orders (16.2)
    public static async Task<object> SaveOrderAsync(DbTx t, OrderInput r, Guid uid, Guid? existing)
    {
        if (r.Lines is not { Count: > 0 }) throw new KiteException(ErrorCodes.Validation, "Add at least one line.", 400, "lines");
        var biz = await BusinessInfo.LoadAsync(t);
        var policy = await PolicyAsync(t);
        await SupplierForAsync(t, r.SupplierId, r.BranchId, policy);
        var date = r.OrderDate ?? BusinessDay.Today(biz.TimeZone, t.Clock);
        if (r.ExpectedDate is { } ex && ex < date) throw new KiteException(ErrorCodes.Validation, "The expected date is on or after the order date.", 400, "expectedDate");
        var items = await ItemsAsync(t, r.Lines.Select(l => l.VariantId));
        var lines = new List<(OrderLineInput L, ItemRow Item, Guid Tax, decimal Rate, decimal Net, decimal Vat, Guid Branch)>();
        for (var i = 0; i < r.Lines.Count; i++)
        {
            var l = r.Lines[i];
            if (!items.TryGetValue(l.VariantId, out var item)) throw new KiteException(ErrorCodes.Validation, "An item is not in the catalog.", 400, $"lines[{i}].variantId");
            if (item.ProductType == "service") throw new KiteException(ErrorCodes.Validation, "Services are not ordered for stock; enter them on the supplier's invoice.", 400, $"lines[{i}].variantId");
            Quantity(l.Qty, $"lines[{i}].qty");
            if (item.Tracking != "none" && l.Qty != decimal.Truncate(l.Qty)) throw new KiteException(ErrorCodes.Validation, "Phones are ordered in whole units.", 400, $"lines[{i}].qty");
            Money(l.UnitPrice, $"lines[{i}].unitPrice");
            var branch = l.DeliverBranchId ?? r.BranchId;
            if (!await t.WithAllBranchesAsync(() => t.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM core.branch WHERE id = @branch AND status = 'active')", new { branch })))
                throw new KiteException(ErrorCodes.Validation, "Deliver to an active branch.", 400, $"lines[{i}].deliverBranchId");
            if (l.RepairJobId is { } job && !await t.WithAllBranchesAsync(() => t.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM repair.repair_job WHERE id = @job AND branch_id = @branch AND status NOT IN ('delivered','cancelled'))", new { job, branch })))
                throw new KiteException(ErrorCodes.Validation, "The repair job is not open at the branch it is delivered to.", 400, $"lines[{i}].repairJobId");
            var tax = l.TaxCodeId ?? item.TaxCodeId;
            var rate = await RateOnAsync(t, tax, date);
            var net = PurchaseMath.LineNet(l.Qty, l.UnitPrice);
            lines.Add((l, item, tax, rate, net, PurchaseMath.LineTax(net, rate), branch));
        }
        var netTotal = lines.Sum(x => x.Net);
        var taxTotal = lines.Sum(x => x.Vat);
        Guid id;
        string number;
        if (existing is { } eid)
        {
            var o = await t.QuerySingleOrDefaultAsync<(Guid Id, string Number, string Status, int Version, Guid BranchId)>(
                "SELECT id, number, status, version, branch_id FROM purch.purchase_order WHERE id = @eid FOR UPDATE", new { eid });
            if (o.Id == Guid.Empty) throw new KiteException(ErrorCodes.NotFound, "That order was not found.", 404);
            if (o.Status != "draft") throw new KiteException(ErrorCodes.PurchOrderState, $"Order {o.Number} is {o.Status.Replace('_', ' ')}; only a draft is changed.", 409);
            if (r.Version is { } v && v != o.Version) throw new KiteException(ErrorCodes.VersionMismatch, "Someone changed this order; open it again.", 412);
            if (o.BranchId != r.BranchId) throw new KiteException(ErrorCodes.Validation, "An order stays at the branch that placed it.", 400, "branchId");
            id = eid;
            number = o.Number;
            await t.ExecuteAsync("DELETE FROM purch.po_line WHERE po_id = @id", new { id });
            await t.ExecuteAsync("""
                UPDATE purch.purchase_order SET supplier_id = @SupplierId, order_date = @d::date, expected_date = @e::date, note = @note, net_total = @netTotal,
                       tax_total = @taxTotal, gross_total = @gross, updated_by = @uid WHERE id = @id
                """, new { id, r.SupplierId, d = D(date), e = r.ExpectedDate is { } x ? D(x) : (DateTime?)null, note = Clean(r.Note), netTotal, taxTotal,
                           gross = netTotal + taxTotal, uid });
        }
        else
        {
            id = r.Id ?? t.NewId();
            if (await t.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM purch.purchase_order WHERE id = @id)", new { id }))
                return await OrderAsync(t, id, uid, null);   // the same request again
            number = await DocNumbers.NextAsync(t, biz.Id, r.BranchId, "purchase_order", await FiscalPeriods.FiscalYearOfAsync(t, biz.Id, date), "PO");
            await t.ExecuteAsync("""
                INSERT INTO purch.purchase_order (id, business_id, branch_id, number, supplier_id, order_date, expected_date, net_total, tax_total, gross_total, note,
                                                  created_by, origin_server_id)
                VALUES (@id, @b, @BranchId, @number, @SupplierId, @d::date, @e::date, @netTotal, @taxTotal, @gross, @note, @uid, (SELECT id FROM core.server WHERE is_self))
                """, new { id, b = biz.Id, r.BranchId, number, r.SupplierId, d = D(date), e = r.ExpectedDate is { } x ? D(x) : (DateTime?)null, netTotal, taxTotal,
                           gross = netTotal + taxTotal, note = Clean(r.Note), uid });
        }
        short no = 0;
        foreach (var x in lines)
            await t.ExecuteAsync("""
                INSERT INTO purch.po_line (id, po_id, line_no, variant_id, description_en, deliver_branch_id, qty, unit_price, tax_code_id, tax_rate, net_amount, tax_amount, repair_job_id)
                VALUES (@lid, @id, @no, @VariantId, @desc, @Branch, @Qty, @UnitPrice, @Tax, @Rate, @Net, @Vat, @RepairJobId)
                """, new { lid = t.NewId(), id, no = ++no, x.L.VariantId, desc = x.Item.Description, x.Branch, x.L.Qty, x.L.UnitPrice, x.Tax, x.Rate, x.Net, x.Vat, x.L.RepairJobId });
        await t.AuditAsync(new AuditEntry(existing is null ? "purch.order_created" : "purch.order_changed", "purchase_order", id,
            After: Json.Obj(new { number, r.SupplierId, lines = lines.Count, gross = netTotal + taxTotal }), BranchId: r.BranchId));
        return new { id, number };
    }

    /// <summary>The largest order a user may approve at a branch (null: no limit), from the limits of every role they hold there.</summary>
    public static async Task<(bool Unlimited, decimal Max)> ApprovalLimitAsync(DbTx t, Guid userId, Guid branchId)
    {
        var limits = (await t.WithAllBranchesAsync(() => t.QueryAsync<(Guid RoleId, decimal? Max, bool Found)>("""
            SELECT ubr.role_id, coalesce(lb.max_amount, la.max_amount), (lb.id IS NOT NULL OR la.id IS NOT NULL)
              FROM iam.user_branch_role ubr
              LEFT JOIN purch.approval_limit lb ON lb.role_id = ubr.role_id AND lb.branch_id = @branchId
              LEFT JOIN purch.approval_limit la ON la.role_id = ubr.role_id AND la.branch_id IS NULL
             WHERE ubr.user_id = @userId AND (ubr.branch_id IS NULL OR ubr.branch_id = @branchId)
            """, new { userId, branchId }))).Where(l => l.Found).ToList();
        if (limits.Count == 0) return (false, 0m);
        if (limits.Any(l => l.Max is null)) return (true, 0m);
        return (false, limits.Max(l => l.Max!.Value));
    }

    public sealed class OrderHead
    {
        public Guid Id { get; set; }
        public Guid BranchId { get; set; }
        public string Number { get; set; } = "";
        public Guid SupplierId { get; set; }
        public string Status { get; set; } = "";
        public decimal GrossTotal { get; set; }
        public int Version { get; set; }
    }

    public static async Task<OrderHead> LoadOrderAsync(DbTx t, Guid id, bool forUpdate = false) =>
        await t.QuerySingleOrDefaultAsync<OrderHead>($"""
            SELECT id, branch_id AS BranchId, number, supplier_id AS SupplierId, status, gross_total AS GrossTotal, version
              FROM purch.purchase_order WHERE id = @id {(forUpdate ? "FOR UPDATE" : "")}
            """, new { id }) ?? throw new KiteException(ErrorCodes.NotFound, "That order was not found in your branches.", 404);

    public static async Task ApproveOrderAsync(DbTx t, Guid id, Guid uid, System.Security.Claims.ClaimsPrincipal user)
    {
        await LockOrderAsync(t, id);
        var o = await LoadOrderAsync(t, id, forUpdate: true);
        user.Demand(Permissions.PurchApprove, o.BranchId);
        if (o.Status != "draft") throw new KiteException(ErrorCodes.PurchOrderState, $"Order {o.Number} is already {o.Status.Replace('_', ' ')}.", 409);
        var (unlimited, max) = await ApprovalLimitAsync(t, uid, o.BranchId);
        if (!unlimited && o.GrossTotal > max)
            throw new KiteException(ErrorCodes.PurchApprovalLimit,
                $"Order {o.Number} is OMR {o.GrossTotal:0.000}; you may approve up to OMR {max:0.000}. Someone with a higher limit approves it.", 403);
        await t.ExecuteAsync("UPDATE purch.purchase_order SET status = 'approved', approved_by = @uid, approved_at = now(), updated_by = @uid WHERE id = @id", new { id, uid });
        await t.AuditAsync(new AuditEntry("purch.order_approved", "purchase_order", id, After: Json.Obj(new { o.Number, o.GrossTotal, limit = unlimited ? (decimal?)null : max }), BranchId: o.BranchId));
    }

    public static async Task<object> OrderAsync(DbTx t, Guid id, Guid uid, System.Security.Claims.ClaimsPrincipal? user) =>
        await t.WithAllBranchesAsync(async () =>
        {
            var head = (await t.RowsAsync("""
                SELECT o.id, o.number, o.status, o.order_date::text AS "orderDate", o.expected_date::text AS "expectedDate", o.net_total AS "netTotal", o.tax_total AS "taxTotal",
                       o.gross_total AS "grossTotal", o.note, o.version, o.branch_id AS "branchId", b.code AS "branchCode", b.name_en AS "branchNameEn", b.name_ar AS "branchNameAr",
                       o.supplier_id AS "supplierId", s.number AS "supplierNumber", s.name_en AS "supplierNameEn", s.name_ar AS "supplierNameAr", s.status AS "supplierStatus",
                       cu.display_name_en AS "createdBy", au.display_name_en AS "approvedBy", o.approved_at AS "approvedAt", o.sent_at AS "sentAt",
                       o.closed_at AS "closedAt", o.close_reason AS "closeReason", o.created_at AS "createdAt"
                  FROM purch.purchase_order o JOIN core.branch b ON b.id = o.branch_id JOIN purch.supplier s ON s.id = o.supplier_id
                  LEFT JOIN iam."user" cu ON cu.id = o.created_by LEFT JOIN iam."user" au ON au.id = o.approved_by
                 WHERE o.id = @id
                """, new { id })).SingleOrDefault() ?? throw new KiteException(ErrorCodes.NotFound, "That order was not found.", 404);
            var lines = await t.RowsAsync("""
                SELECT l.id, l.line_no AS "lineNo", l.variant_id AS "variantId", v.sku, l.description_en AS description, p.tracking, p.product_type AS "productType",
                       l.deliver_branch_id AS "deliverBranchId", db.code AS "deliverBranchCode", l.qty, l.unit_price AS "unitPrice", l.tax_code_id AS "taxCodeId",
                       l.tax_rate AS "taxRate", l.net_amount AS "netAmount", l.tax_amount AS "taxAmount", l.repair_job_id AS "repairJobId", j.number AS "repairJobNumber",
                       coalesce((SELECT sum(rl.qty) FROM purch.receipt_line rl WHERE rl.po_line_id = l.id), 0) AS received,
                       coalesce((SELECT sum(il.qty) FROM purch.invoice_line il JOIN purch.receipt_line rl ON rl.id = il.receipt_line_id
                                  JOIN purch.purchase_invoice i ON i.id = il.invoice_id WHERE rl.po_line_id = l.id AND i.status = 'posted'), 0) AS invoiced
                  FROM purch.po_line l JOIN catalog.variant v ON v.id = l.variant_id JOIN catalog.product p ON p.id = v.product_id
                  JOIN core.branch db ON db.id = l.deliver_branch_id LEFT JOIN repair.repair_job j ON j.id = l.repair_job_id
                 WHERE l.po_id = @id ORDER BY l.line_no
                """, new { id });
            var receipts = await t.RowsAsync("""
                SELECT r.id, r.number, r.receipt_date::text AS "receiptDate", b.code AS "branchCode", r.goods_value AS "goodsValue", r.charges_value AS "chargesValue"
                  FROM purch.goods_receipt r JOIN core.branch b ON b.id = r.branch_id WHERE r.po_id = @id ORDER BY r.created_at
                """, new { id });
            var branch = (Guid)head["branchId"]!;
            var (unlimited, max) = await ApprovalLimitAsync(t, uid, branch);
            var canApprove = user is not null && user.HasIn(Permissions.PurchApprove, branch) && (unlimited || max >= (decimal)head["grossTotal"]!);
            return (object)new { order = head, lines, receipts, approval = new { canApprove, limit = unlimited ? (decimal?)null : max } };
        });

    /// <summary>After a receipt: the order is partly received, or received in full.</summary>
    private static async Task RefreshOrderStatusAsync(DbTx t, Guid poId, Guid uid)
    {
        var x = await t.QuerySingleOrDefaultAsync<(string Status, bool Any, bool All)>("""
            SELECT o.status,
                   EXISTS (SELECT 1 FROM purch.receipt_line rl JOIN purch.po_line l ON l.id = rl.po_line_id WHERE l.po_id = o.id),
                   NOT EXISTS (SELECT 1 FROM purch.po_line l WHERE l.po_id = o.id
                                 AND l.qty > coalesce((SELECT sum(rl.qty) FROM purch.receipt_line rl WHERE rl.po_line_id = l.id), 0))
              FROM purch.purchase_order o WHERE o.id = @poId
            """, new { poId });
        var target = x.All ? "received" : x.Any ? "partly_received" : x.Status;
        if (target != x.Status)
            await t.ExecuteAsync("UPDATE purch.purchase_order SET status = @target, updated_by = @uid WHERE id = @poId", new { poId, target, uid });
    }

    // ================================================================ goods receipts (16.2, 16.3)
    private sealed class PoLineRow
    {
        public Guid Id { get; set; }
        public Guid PoId { get; set; }
        public Guid VariantId { get; set; }
        public Guid DeliverBranchId { get; set; }
        public decimal Qty { get; set; }
        public decimal UnitPrice { get; set; }
        public Guid TaxCodeId { get; set; }
        public Guid? RepairJobId { get; set; }
        public decimal Received { get; set; }
    }

    public static async Task<object?> ReceiptDoneAsync(DbTx t, Guid id) =>
        await t.QuerySingleOrDefaultAsync<Guid?>("SELECT id FROM purch.goods_receipt WHERE id = @id", new { id }) is { } done
            ? await ReceiptAsync(t, done) : null;

    public static async Task<(object Result, List<Guid> Units, bool Parts)> ReceiveAsync(DbTx t, ReceiptInput r, Guid uid, System.Security.Claims.ClaimsPrincipal user)
    {
        if (r.Lines is not { Count: > 0 }) throw new KiteException(ErrorCodes.Validation, "Add at least one line.", 400, "lines");
        user.Demand(Permissions.PurchReceive, r.BranchId);
        var biz = await BusinessInfo.LoadAsync(t);
        var policy = await PolicyAsync(t);
        await SupplierForAsync(t, r.SupplierId, r.BranchId, policy);
        var date = r.ReceiptDate ?? BusinessDay.Today(biz.TimeZone, t.Clock);
        var charges = r.Charges ?? [];
        for (var i = 0; i < charges.Count; i++)
        {
            var c = charges[i];
            if (c.Kind is not ("freight" or "customs" or "insurance" or "other")) throw new KiteException(ErrorCodes.Validation, "A landed cost is freight, customs, insurance or other.", 400, $"charges[{i}].kind");
            if (c.Basis is not ("value" or "qty")) throw new KiteException(ErrorCodes.Validation, "Spread a landed cost by value or by quantity.", 400, $"charges[{i}].basis");
            if (Clean(c.Description) is null) throw new KiteException(ErrorCodes.Validation, "Describe the landed cost.", 400, $"charges[{i}].description");
            Money(c.Amount, $"charges[{i}].amount", positive: true);
            if (c.SupplierId is { } cs && cs != r.SupplierId) await SupplierForAsync(t, cs, r.BranchId, policy);
        }

        // The order first (its lock, then its open quantities).
        Dictionary<Guid, PoLineRow> poLines = [];
        if (r.PoId is { } poId)
        {
            await LockOrderAsync(t, poId);
            var o = await t.WithAllBranchesAsync(() => LoadOrderAsync(t, poId, forUpdate: true));
            if (o.SupplierId != r.SupplierId) throw new KiteException(ErrorCodes.Validation, "The order is with another supplier.", 400, "supplierId");
            if (o.Status is not ("approved" or "sent" or "partly_received"))
                throw new KiteException(ErrorCodes.PurchOrderState, $"Order {o.Number} is {o.Status.Replace('_', ' ')}; goods are received against an approved order.", 409, "poId");
            poLines = (await t.WithAllBranchesAsync(() => t.QueryAsync<PoLineRow>("""
                SELECT l.id, l.po_id AS PoId, l.variant_id AS VariantId, l.deliver_branch_id AS DeliverBranchId, l.qty, l.unit_price AS UnitPrice, l.tax_code_id AS TaxCodeId,
                       l.repair_job_id AS RepairJobId, coalesce((SELECT sum(rl.qty) FROM purch.receipt_line rl WHERE rl.po_line_id = l.id), 0) AS Received
                  FROM purch.po_line l WHERE l.po_id = @poId
                """, new { poId }))).ToDictionary(l => l.Id);
        }
        else if (r.Lines.Any(l => l.PoLineId is not null)) throw new KiteException(ErrorCodes.Validation, "Name the order of its lines.", 400, "poId");

        var items = await ItemsAsync(t, r.Lines.Select(l => l.PoLineId is { } pl && poLines.TryGetValue(pl, out var x) ? x.VariantId : l.VariantId ?? Guid.Empty));
        var plan = new List<(ReceiptLineInput L, ItemRow Item, PoLineRow? Po, decimal Price, Guid Tax, decimal Goods)>();
        for (var i = 0; i < r.Lines.Count; i++)
        {
            var l = r.Lines[i];
            Quantity(l.Qty, $"lines[{i}].qty");
            PoLineRow? po = null;
            Guid variant;
            decimal price;
            if (l.PoLineId is { } pl)
            {
                if (!poLines.TryGetValue(pl, out po)) throw new KiteException(ErrorCodes.Validation, "That line is not on the order.", 400, $"lines[{i}].poLineId");
                if (po.DeliverBranchId != r.BranchId) throw new KiteException(ErrorCodes.Validation, "That order line is delivered to another branch.", 400, $"lines[{i}].poLineId");
                var open = po.Qty * (1 + policy.OverReceiptPct / 100m) - po.Received - plan.Where(p => p.Po?.Id == pl).Sum(p => p.L.Qty);
                if (l.Qty > open)
                    throw new KiteException(ErrorCodes.PurchQuantity, $"Only {Math.Max(0, open):0.###} of that line are still to come.", 409, $"lines[{i}].qty");
                variant = po.VariantId;
                price = po.UnitPrice;   // the order's price: a difference appears on the supplier's invoice
            }
            else
            {
                variant = l.VariantId ?? throw new KiteException(ErrorCodes.Validation, "Choose the item.", 400, $"lines[{i}].variantId");
                price = l.UnitPrice ?? throw new KiteException(ErrorCodes.Validation, "Give the price agreed with the supplier.", 400, $"lines[{i}].unitPrice");
                Money(price, $"lines[{i}].unitPrice");
            }
            if (!items.TryGetValue(variant, out var item)) throw new KiteException(ErrorCodes.Validation, "An item is not in the catalog.", 400, $"lines[{i}].variantId");
            if (item.ProductType == "service") throw new KiteException(ErrorCodes.Validation, "Services are not received into stock.", 400, $"lines[{i}].variantId");
            if (item.Tracking != "none")
            {
                if (l.Qty != decimal.Truncate(l.Qty) || l.Units is null || l.Units.Count != (int)l.Qty)
                    throw new KiteException(ErrorCodes.DeviceUnitCount, $"Scan one IMEI or serial number per device: {l.Qty:0} needed, {l.Units?.Count ?? 0} scanned.", 400, $"lines[{i}].units");
                if (l.Units.Any(u => u.Entry == "manual") && !user.HasIn(Permissions.DeviceImeiManualEntry, r.BranchId))
                    throw new KiteException(ErrorCodes.Forbidden, "Typing an IMEI by hand needs the DEVICE.IMEI.MANUAL_ENTRY permission; scan it instead.", 403, $"lines[{i}].units");
            }
            else if (l.Units is { Count: > 0 }) throw new KiteException(ErrorCodes.Validation, "Only serialized items take IMEIs or serial numbers.", 400, $"lines[{i}].units");
            plan.Add((l, item, po, price, l.TaxCodeId ?? po?.TaxCodeId ?? item.TaxCodeId, PurchaseMath.LineNet(l.Qty, price)));
        }

        var warehouse = await t.ExecuteScalarAsync<Guid?>("""
            SELECT id FROM core.warehouse WHERE branch_id = @BranchId AND status = 'active' AND (@WarehouseId::uuid IS NULL OR id = @WarehouseId)
             ORDER BY (kind = 'shop') DESC, (code = 'SHOP') DESC, created_at LIMIT 1
            """, new { r.BranchId, r.WarehouseId }) ?? throw new KiteException(ErrorCodes.Validation, "That warehouse is not in the branch.", 400, "warehouseId");
        var shares = PurchaseMath.SpreadCharges(plan.Select(p => (p.Goods, p.L.Qty)).ToArray(), charges.Select(c => (c.Amount, c.Basis)).ToArray());
        var number = await DocNumbers.NextAsync(t, biz.Id, r.BranchId, "goods_receipt", await FiscalPeriods.FiscalYearOfAsync(t, biz.Id, date), "GR");
        await t.ExecuteAsync("""
            INSERT INTO purch.goods_receipt (id, business_id, branch_id, warehouse_id, number, supplier_id, po_id, receipt_date, delivery_note, note, goods_value,
                                             charges_value, created_by, origin_server_id)
            VALUES (@Id, @b, @BranchId, @warehouse, @number, @SupplierId, @PoId, @d::date, @dn, @note, @goods, @chg, @uid, (SELECT id FROM core.server WHERE is_self))
            """, new { r.Id, b = biz.Id, r.BranchId, warehouse, number, r.SupplierId, r.PoId, d = D(date), dn = Clean(r.DeliveryNote), note = Clean(r.Note),
                       goods = plan.Sum(p => p.Goods), chg = charges.Sum(c => c.Amount), uid });
        short cno = 0;
        foreach (var c in charges)
        {
            var cid = t.NewId();
            await t.ExecuteAsync("""
                INSERT INTO purch.receipt_charge (id, receipt_id, line_no, kind, description, supplier_id, amount, basis)
                VALUES (@cid, @Id, @no, @Kind, @desc, @sup, @Amount, @Basis)
                """, new { cid, r.Id, no = ++cno, c.Kind, desc = c.Description.Trim(), sup = c.SupplierId ?? r.SupplierId, c.Amount, c.Basis });
            await GrniAsync(t, biz.Id, r.BranchId, c.SupplierId ?? r.SupplierId, "receipt_charge", cid, "accrued", c.Amount, "goods_receipt", r.Id);
        }

        var facts = new PostingFacts();
        var unitIds = new List<Guid>();
        if (plan.Any(p => p.Item.Tracking != "none"))
            await CentralRegistry.LockAsync(t, (await DeviceIdentity.IdentifiersOfAsync(t, plan.Where(p => p.L.Units is { Count: > 0 })
                .Select(p => new AdjustmentLineInput(p.Item.Id, p.L.Qty, "purchase", null, p.L.Units)))).Select(CentralRegistry.Key.Of));
        short no = 0;
        foreach (var (p, k) in plan.Select((p, k) => (p, k)))
        {
            var lineId = t.NewId();
            var stock = p.Goods + shares[k];
            await t.ExecuteAsync("""
                INSERT INTO purch.receipt_line (id, receipt_id, line_no, po_line_id, variant_id, product_type, qty, unit_price, tax_code_id, goods_value, charge_share,
                                                stock_value, repair_job_id)
                VALUES (@lineId, @Id, @no, @po, @variant, @type, @Qty, @Price, @Tax, @Goods, @share, @stock, @job)
                """, new { lineId, r.Id, no = ++no, po = p.Po?.Id, variant = p.Item.Id, type = p.Item.ProductType, p.L.Qty, p.Price, p.Tax, p.Goods, share = shares[k], stock,
                           job = p.L.RepairJobId ?? p.Po?.RepairJobId });
            await GrniAsync(t, biz.Id, r.BranchId, r.SupplierId, "receipt_line", lineId, "accrued", p.Goods, "goods_receipt", r.Id, allowZero: true);
            if (p.Item.Tracking != "none")
            {
                var extra = PurchaseMath.SplitEven(shares[k], p.L.Units!.Count);
                for (var u = 0; u < p.L.Units.Count; u++)
                {
                    var cost = p.Price + extra[u];
                    var input = p.L.Units[u] with { UnitCost = cost };
                    var (unitId, value, cls) = await DeviceUnits.ReceiveAsync(t, biz.Id, r.BranchId, warehouse, (p.Item.Id, p.Item.ProductType, p.Item.Tracking, p.Item.BrandKey, p.Item.CategoryId),
                        input, "purchase", "goods_receipt", r.Id, t.NewId(), "purchase_receipt", $"lines[{k}].units[{u}]");
                    await t.ExecuteAsync("INSERT INTO purch.receipt_unit (device_unit_id, receipt_line_id, unit_cost) VALUES (@unitId, @lineId, @cost)", new { unitId, lineId, cost });
                    facts.Add("stock_by_class", cls, value);
                    unitIds.Add(unitId);
                }
            }
            else
            {
                var value = await t.ExecuteScalarAsync<decimal>("""
                    INSERT INTO inv.stock_movement (id, business_id, branch_id, warehouse_id, variant_id, movement_type, qty, value, source_doc_type, source_doc_id,
                                                    source_line_id, reason, created_by, origin_server_id)
                    VALUES (@mid, @b, @BranchId, @warehouse, @variant, 'purchase_receipt', @Qty, @stock, 'goods_receipt', @Id, @lineId, @number, @uid,
                            (SELECT id FROM core.server WHERE is_self))
                    RETURNING value
                    """, new { mid = t.NewId(), b = biz.Id, r.BranchId, warehouse, variant = p.Item.Id, p.L.Qty, stock, r.Id, lineId, number, uid });
                facts.Add("stock_by_class", p.Item.ProductType, value);
            }
        }
        var journal = await Posting.PostAsync(t, biz.Id, r.BranchId, PostingRuleTemplates.PurchReceipt, facts, date, "goods_receipt", r.Id,
            $"Goods receipt {number}", partySupplierId: r.SupplierId);
        await t.ExecuteAsync("UPDATE purch.goods_receipt SET journal_id = @journal WHERE id = @Id", new { journal, r.Id });
        if (r.PoId is { } po2) await t.WithAllBranchesAsync(async () => { await RefreshOrderStatusAsync(t, po2, uid); return 0; });
        await t.AuditAsync(new AuditEntry("purch.goods_received", "goods_receipt", r.Id,
            After: Json.Obj(new { number, r.SupplierId, r.PoId, lines = plan.Count, units = unitIds.Count, goods = plan.Sum(p => p.Goods), charges = charges.Sum(c => c.Amount) }), BranchId: r.BranchId));
        await t.PublishAsync("purch.goods_received", "goods_receipt", r.Id, 1, Json.Obj(new { number, r.SupplierId, r.PoId }), r.BranchId);
        var parts = plan.Any(p => p.Item.ProductType == "spare_part" || p.L.RepairJobId is not null || p.Po?.RepairJobId is not null);
        return (await ReceiptAsync(t, r.Id), unitIds, parts);
    }

    public static Task GrniAsync(DbTx t, Guid businessId, Guid branchId, Guid? supplierId, string sourceType, Guid sourceId, string kind, decimal amount,
        string docType, Guid docId, bool allowZero = false)
    {
        if (amount == 0) return Task.CompletedTask;   // free goods hold nothing in 2110
        return t.ExecuteAsync("""
            INSERT INTO purch.grni_entry (id, business_id, branch_id, supplier_id, source_type, source_id, kind, amount, doc_type, doc_id, origin_server_id)
            VALUES (@id, @businessId, @branchId, @supplierId, @sourceType, @sourceId, @kind, @amount, @docType, @docId, (SELECT id FROM core.server WHERE is_self))
            """, new { id = t.NewId(), businessId, branchId, supplierId, sourceType, sourceId, kind, amount, docType, docId });
    }

    public static async Task<object> ReceiptAsync(DbTx t, Guid id) =>
        await t.WithAllBranchesAsync(async () =>
        {
            var head = (await t.RowsAsync("""
                SELECT r.id, r.number, r.receipt_date::text AS "receiptDate", r.branch_id AS "branchId", b.code AS "branchCode", r.supplier_id AS "supplierId",
                       s.name_en AS "supplierNameEn", s.name_ar AS "supplierNameAr", r.po_id AS "poId", o.number AS "poNumber", r.delivery_note AS "deliveryNote", r.note,
                       r.goods_value AS "goodsValue", r.charges_value AS "chargesValue", u.display_name_en AS "receivedBy", r.created_at AS "createdAt",
                       (SELECT j.number FROM acct.journal j WHERE j.id = r.journal_id) AS "journalNumber"
                  FROM purch.goods_receipt r JOIN core.branch b ON b.id = r.branch_id JOIN purch.supplier s ON s.id = r.supplier_id
                  LEFT JOIN purch.purchase_order o ON o.id = r.po_id LEFT JOIN iam."user" u ON u.id = r.created_by
                 WHERE r.id = @id
                """, new { id })).SingleOrDefault() ?? throw new KiteException(ErrorCodes.NotFound, "That receipt was not found.", 404);
            var lines = await t.RowsAsync("""
                SELECT l.id, l.line_no AS "lineNo", l.variant_id AS "variantId", v.sku, p.name_en AS description, l.product_type AS "productType", l.qty, l.unit_price AS "unitPrice",
                       l.goods_value AS "goodsValue", l.charge_share AS "chargeShare", l.stock_value AS "stockValue", l.po_line_id AS "poLineId",
                       coalesce((SELECT sum(il.qty) FROM purch.invoice_line il JOIN purch.purchase_invoice i ON i.id = il.invoice_id
                                  WHERE il.receipt_line_id = l.id AND i.status = 'posted'), 0) AS invoiced,
                       coalesce((SELECT sum(x.qty) FROM purch.supplier_return_line x WHERE x.receipt_line_id = l.id), 0) AS returned,
                       coalesce((SELECT sum(g.amount) FROM purch.grni_entry g WHERE g.source_type = 'receipt_line' AND g.source_id = l.id), 0) AS "openValue",
                       (SELECT coalesce(json_agg(json_build_object('id', ru.device_unit_id, 'imei', (SELECT r2.value FROM device.imei_registry r2 WHERE r2.device_unit_id = ru.device_unit_id
                                 AND r2.status = 'active' ORDER BY r2.kind = 'imei' DESC, r2.slot LIMIT 1), 'cost', ru.unit_cost, 'status', du.status) ORDER BY du.received_at), '[]')
                          FROM purch.receipt_unit ru JOIN device.device_unit du ON du.id = ru.device_unit_id WHERE ru.receipt_line_id = l.id)::text AS units
                  FROM purch.receipt_line l JOIN catalog.variant v ON v.id = l.variant_id JOIN catalog.product p ON p.id = v.product_id
                 WHERE l.receipt_id = @id ORDER BY l.line_no
                """, new { id });
            foreach (var l in lines) l["units"] = System.Text.Json.JsonDocument.Parse((string)l["units"]!).RootElement.Clone();
            var charges = await t.RowsAsync("""
                SELECT c.id, c.line_no AS "lineNo", c.kind, c.description, c.amount, c.basis, c.supplier_id AS "supplierId", s.name_en AS "supplierNameEn",
                       coalesce((SELECT sum(g.amount) FROM purch.grni_entry g WHERE g.source_type = 'receipt_charge' AND g.source_id = c.id), 0) AS "openValue"
                  FROM purch.receipt_charge c JOIN purch.supplier s ON s.id = c.supplier_id WHERE c.receipt_id = @id ORDER BY c.line_no
                """, new { id });
            return (object)new { receipt = head, lines, charges };
        });

    // ================================================================ supplier invoices: the three-way match (16.2)
    private sealed class OpenReceiptLine
    {
        public Guid Id { get; set; }
        public Guid ReceiptId { get; set; }
        public Guid BranchId { get; set; }
        public Guid WarehouseId { get; set; }
        public Guid SupplierId { get; set; }
        public Guid VariantId { get; set; }
        public string ProductType { get; set; } = "";
        public string Tracking { get; set; } = "";
        public string Description { get; set; } = "";
        public decimal Qty { get; set; }
        public decimal UnitPrice { get; set; }
        public Guid TaxCodeId { get; set; }
        public decimal? OrderPrice { get; set; }
        public decimal Invoiced { get; set; }
        public decimal ReturnedOpen { get; set; }
        public decimal OpenValue { get; set; }
    }

    private const string OpenLineSelect = $"""
        SELECT l.id, l.receipt_id AS ReceiptId, r.branch_id AS BranchId, r.warehouse_id AS WarehouseId, r.supplier_id AS SupplierId, l.variant_id AS VariantId,
               l.product_type AS ProductType, p.tracking, {ItemPricing.DescriptionEn} AS Description, l.qty, l.unit_price AS UnitPrice, l.tax_code_id AS TaxCodeId,
               pl.unit_price AS OrderPrice,
               coalesce((SELECT sum(il.qty) FROM purch.invoice_line il JOIN purch.purchase_invoice i ON i.id = il.invoice_id
                          WHERE il.receipt_line_id = l.id AND i.status = 'posted'), 0) AS Invoiced,
               coalesce((SELECT sum(x.qty) FROM purch.supplier_return_line x WHERE x.receipt_line_id = l.id AND NOT x.invoiced), 0) AS ReturnedOpen,
               coalesce((SELECT sum(g.amount) FROM purch.grni_entry g WHERE g.source_type = 'receipt_line' AND g.source_id = l.id), 0) AS OpenValue
          FROM purch.receipt_line l JOIN purch.goods_receipt r ON r.id = l.receipt_id
          JOIN catalog.variant v ON v.id = l.variant_id JOIN catalog.product p ON p.id = v.product_id
          LEFT JOIN purch.po_line pl ON pl.id = l.po_line_id
        """;

    /// <summary>Matches every line of an invoice against what is open now (16.2): quantities, prices against the order, and where differences go.</summary>
    public static async Task<List<MatchedLine>> MatchAsync(DbTx t, InvoiceInput r, PurchPolicy policy, Guid? excludeInvoice)
    {
        var result = new List<MatchedLine>();
        var seen = new HashSet<(string, Guid)>();
        var vatStd = await t.ExecuteScalarAsync<Guid?>("SELECT id FROM tax.tax_code WHERE code = 'VAT-STD' LIMIT 1");
        for (var i = 0; i < r.Lines.Count; i++)
        {
            var l = r.Lines[i];
            var f = $"lines[{i}]";
            Quantity(l.Qty, f + ".qty");
            Money(l.UnitPrice, f + ".unitPrice");
            var net = PurchaseMath.LineNet(l.Qty, l.UnitPrice);
            switch (l.Kind)
            {
                case "receipt":
                {
                    var id = l.ReceiptLineId ?? throw new KiteException(ErrorCodes.Validation, "Choose the receipt line.", 400, f + ".receiptLineId");
                    if (!seen.Add(("r", id))) throw new KiteException(ErrorCodes.Validation, "A receipt line appears twice.", 400, f);
                    var rl = await t.WithAllBranchesAsync(() => t.QuerySingleOrDefaultAsync<OpenReceiptLine>(OpenLineSelect + " WHERE l.id = @id", new { id }))
                        ?? throw new KiteException(ErrorCodes.NotFound, "That receipt line was not found.", 404, f + ".receiptLineId");
                    if (rl.SupplierId != r.SupplierId) throw new KiteException(ErrorCodes.Validation, "Those goods came from another supplier.", 400, f + ".receiptLineId");
                    var openQty = rl.Qty - rl.Invoiced - rl.ReturnedOpen;
                    var m = PurchaseMath.Match(l.Qty, l.UnitPrice, openQty, rl.UnitPrice, rl.OrderPrice, policy.PriceTolerancePct, policy.PriceToleranceAmount);
                    var expected = PurchaseMath.Clearing(l.Qty, openQty, rl.OpenValue);
                    var variance = net - expected;
                    decimal onHand;
                    if (rl.Tracking != "none")
                        onHand = await t.WithAllBranchesAsync(() => t.ExecuteScalarAsync<decimal>("""
                            SELECT count(*) FROM purch.receipt_unit ru JOIN device.device_unit u ON u.id = ru.device_unit_id
                             WHERE ru.receipt_line_id = @id AND device.in_stock_status(u.status, u.held_from_status)
                            """, new { id }));
                    else
                        onHand = await t.WithAllBranchesAsync(() => t.ExecuteScalarAsync<decimal>("""
                            SELECT coalesce((SELECT qty FROM inv.stock_balance WHERE branch_id = @BranchId AND warehouse_id = @WarehouseId AND variant_id = @VariantId), 0)
                            """, new { rl.BranchId, rl.WarehouseId, rl.VariantId }));
                    var (vi, vp) = m.Status == "qty" ? (0m, 0m) : PurchaseMath.SplitVariance(variance, l.Qty, onHand);
                    var tax = l.TaxCodeId ?? rl.TaxCodeId;
                    var rate = await RateOnAsync(t, tax, r.InvoiceDate);
                    result.Add(new MatchedLine(l, "receipt", rl.BranchId, rl.WarehouseId, rl.VariantId, rl.ProductType, Clean(l.Description) ?? rl.Description, net, tax, rate,
                        PurchaseMath.LineTax(net, rate), m.Status == "qty" ? 0m : expected, rl.OrderPrice, m.Status, m.Status == "qty" ? 0m : variance, vi, vp, null, rl.Id,
                        rl.Tracking != "none", m.Status == "qty" ? $"Only {Math.Max(0, openQty):0.###} of these are received and not invoiced." : null));
                    break;
                }
                case "charge":
                case "outside_work":
                {
                    if (l.Qty != 1) throw new KiteException(ErrorCodes.Validation, "A landed cost or outside work is one line of quantity 1.", 400, f + ".qty");
                    string sourceType;
                    Guid sourceId;
                    Guid branch;
                    string description;
                    if (l.Kind == "charge")
                    {
                        sourceId = l.ReceiptChargeId ?? throw new KiteException(ErrorCodes.Validation, "Choose the landed cost.", 400, f + ".receiptChargeId");
                        sourceType = "receipt_charge";
                        var c = await t.WithAllBranchesAsync(() => t.QuerySingleOrDefaultAsync<(Guid Branch, Guid Supplier, string Description)>("""
                            SELECT r.branch_id, c.supplier_id, c.description || ' (' || r.number || ')' FROM purch.receipt_charge c JOIN purch.goods_receipt r ON r.id = c.receipt_id
                             WHERE c.id = @sourceId
                            """, new { sourceId }));
                        if (c.Branch == Guid.Empty) throw new KiteException(ErrorCodes.NotFound, "That landed cost was not found.", 404, f + ".receiptChargeId");
                        if (c.Supplier != r.SupplierId) throw new KiteException(ErrorCodes.Validation, "That landed cost is owed to another supplier.", 400, f + ".receiptChargeId");
                        branch = c.Branch;
                        description = c.Description;
                    }
                    else
                    {
                        sourceType = l.OutsideType is "refurb_cost" or "repair_line" ? l.OutsideType : throw new KiteException(ErrorCodes.Validation, "Outside work is a refurbishment's or a repair's.", 400, f + ".outsideType");
                        sourceId = l.OutsideId ?? throw new KiteException(ErrorCodes.Validation, "Choose the outside work.", 400, f + ".outsideId");
                        var o = await t.WithAllBranchesAsync(() => t.QuerySingleOrDefaultAsync<(Guid Branch, string Vendor)>("""
                            SELECT branch_id, coalesce(vendor, '') FROM purch.grni_entry WHERE source_type = @sourceType AND source_id = @sourceId AND kind = 'accrued' LIMIT 1
                            """, new { sourceType, sourceId }));
                        if (o.Branch == Guid.Empty) throw new KiteException(ErrorCodes.NotFound, "That outside work was not found.", 404, f + ".outsideId");
                        branch = o.Branch;
                        description = $"Outside work: {o.Vendor}";
                    }
                    if (!seen.Add((sourceType, sourceId))) throw new KiteException(ErrorCodes.Validation, "The same cost appears twice.", 400, f);
                    var open = await t.WithAllBranchesAsync(() => t.ExecuteScalarAsync<decimal>(
                        "SELECT coalesce(sum(amount), 0) FROM purch.grni_entry WHERE source_type = @sourceType AND source_id = @sourceId", new { sourceType, sourceId }));
                    var m = PurchaseMath.Match(1, l.UnitPrice, open > 0 ? 1 : 0, open, null, policy.PriceTolerancePct, policy.PriceToleranceAmount);
                    var tax = l.TaxCodeId ?? vatStd ?? throw new KiteException(ErrorCodes.Validation, "Choose the tax code.", 400, f + ".taxCodeId");
                    var rate = await RateOnAsync(t, tax, r.InvoiceDate);
                    var expected = m.Status == "qty" ? 0m : open;
                    result.Add(new MatchedLine(l, l.Kind, branch, null, null, null, Clean(l.Description) ?? description, net, tax, rate, PurchaseMath.LineTax(net, rate), expected,
                        null, m.Status, m.Status == "qty" ? 0m : net - expected, 0m, m.Status == "qty" ? 0m : net - expected, null, null, false,
                        m.Status == "qty" ? "That cost is already invoiced." : null));
                    break;
                }
                case "expense":
                {
                    var accountId = l.AccountId ?? throw new KiteException(ErrorCodes.Validation, "Choose the expense account.", 400, f + ".accountId");
                    var a = await t.QuerySingleOrDefaultAsync<(string Code, string Type, string Cff, bool Postable, string Status, string Name)>(
                        "SELECT code, type, cash_flow_class, is_postable, status, name_en FROM acct.account WHERE id = @accountId", new { accountId });
                    if (a.Code is null || !a.Postable || a.Status != "active" || !(a.Type == "expense" || a.Cff == "investing"))
                        throw new KiteException(ErrorCodes.Validation, "An expense line goes to an active expense account or a fixed asset account.", 400, f + ".accountId");
                    var branch = l.ExpenseBranchId ?? r.BranchId;
                    var tax = l.TaxCodeId ?? vatStd ?? throw new KiteException(ErrorCodes.Validation, "Choose the tax code.", 400, f + ".taxCodeId");
                    var rate = await RateOnAsync(t, tax, r.InvoiceDate);
                    result.Add(new MatchedLine(l, "expense", branch, null, null, null, Clean(l.Description) ?? a.Name, net, tax, rate, PurchaseMath.LineTax(net, rate), 0m, null,
                        "none", 0m, 0m, 0m, a.Code, null, false, null));
                    break;
                }
                default:
                    throw new KiteException(ErrorCodes.Validation, "A line is goods received, a landed cost, outside work or an expense.", 400, f + ".kind");
            }
        }
        return result;
    }

    public static async Task<object> SaveInvoiceAsync(DbTx t, InvoiceInput r, Guid uid, System.Security.Claims.ClaimsPrincipal user, bool update)
    {
        user.Demand(Permissions.PurchInvoice, r.BranchId);
        if (r.Lines is not { Count: > 0 }) throw new KiteException(ErrorCodes.Validation, "Add at least one line.", 400, "lines");
        if (Clean(r.SupplierInvoiceNo) is null) throw new KiteException(ErrorCodes.Validation, "Give the supplier's invoice number.", 400, "supplierInvoiceNo");
        var biz = await BusinessInfo.LoadAsync(t);
        var policy = await PolicyAsync(t);
        var supplier = await SupplierForAsync(t, r.SupplierId, r.BranchId, policy);
        var due = r.DueDate ?? r.InvoiceDate.AddDays(supplier.TermsDays);
        if (due < r.InvoiceDate) throw new KiteException(ErrorCodes.Validation, "The due date is on or after the invoice date.", 400, "dueDate");
        var existing = await t.QuerySingleOrDefaultAsync<(Guid Id, string Status, int Version, string Number)>(
            "SELECT id, status, version, number FROM purch.purchase_invoice WHERE id = @Id FOR UPDATE", new { r.Id });
        if (existing.Id != Guid.Empty && !update) return await InvoiceAsync(t, r.Id);   // the same request again
        if (update)
        {
            if (existing.Id == Guid.Empty) throw new KiteException(ErrorCodes.NotFound, "That invoice was not found.", 404);
            if (existing.Status is "posted" or "cancelled") throw new KiteException(ErrorCodes.PurchInvoiceState, $"Invoice {existing.Number} is {existing.Status}.", 409);
            if (r.Version is { } v && v != existing.Version) throw new KiteException(ErrorCodes.VersionMismatch, "Someone changed this invoice; open it again.", 412);
        }
        // A supplier's invoice number is booked once (16.2), whatever its spacing or case.
        if (await t.WithAllBranchesAsync(() => t.QuerySingleOrDefaultAsync<string?>("""
                SELECT number FROM purch.purchase_invoice WHERE supplier_id = @SupplierId AND lower(trim(supplier_invoice_no)) = lower(trim(@no)) AND status <> 'cancelled' AND id <> @Id
                """, new { r.SupplierId, no = r.SupplierInvoiceNo, r.Id })) is { } dup)
            throw new KiteException(ErrorCodes.PurchDuplicateInvoice, $"{supplier.NameEn}'s invoice {r.SupplierInvoiceNo.Trim()} is already booked as {dup}.", 409, "supplierInvoiceNo");
        var lines = await MatchAsync(t, r, policy, r.Id);
        // More than was received and not yet invoiced is never invoiced, with or without an override.
        if (lines.FirstOrDefault(l => l.Status == "qty") is { } q) throw new KiteException(ErrorCodes.PurchQuantity, $"{q.Description}: {q.Problem}", 409, "lines");
        var status = lines.All(l => l.Status is "ok" or "none") ? "matched" : "needs_approval";
        var net = lines.Sum(l => l.Net);
        var tax = lines.Sum(l => l.Tax);
        string number;
        if (update)
        {
            number = existing.Number;
            await t.ExecuteAsync("DELETE FROM purch.invoice_line WHERE invoice_id = @Id", new { r.Id });
            await t.ExecuteAsync("""
                UPDATE purch.purchase_invoice SET supplier_id = @SupplierId, supplier_invoice_no = @no, invoice_date = @d::date, due_date = @due::date, status = @status,
                       net_total = @net, tax_total = @tax, gross_total = @gross, variance_total = @var, note = @note, updated_by = @uid WHERE id = @Id
                """, new { r.Id, r.SupplierId, no = r.SupplierInvoiceNo.Trim(), d = D(r.InvoiceDate), due = D(due), status, net, tax, gross = net + tax,
                           var = lines.Sum(l => l.Variance), note = Clean(r.Note), uid });
        }
        else
        {
            number = await DocNumbers.NextAsync(t, biz.Id, r.BranchId, "purchase_invoice", await FiscalPeriods.FiscalYearOfAsync(t, biz.Id, r.InvoiceDate), "PI");
            await t.ExecuteAsync("""
                INSERT INTO purch.purchase_invoice (id, business_id, branch_id, number, supplier_id, supplier_invoice_no, invoice_date, due_date, status, net_total, tax_total,
                                                    gross_total, variance_total, note, created_by, origin_server_id)
                VALUES (@Id, @b, @BranchId, @number, @SupplierId, @no, @d::date, @due::date, @status, @net, @tax, @gross, @var, @note, @uid, (SELECT id FROM core.server WHERE is_self))
                """, new { r.Id, b = biz.Id, r.BranchId, number, r.SupplierId, no = r.SupplierInvoiceNo.Trim(), d = D(r.InvoiceDate), due = D(due), status, net, tax,
                           gross = net + tax, var = lines.Sum(l => l.Variance), note = Clean(r.Note), uid });
        }
        await InsertLinesAsync(t, r.Id, lines);
        await t.AuditAsync(new AuditEntry(update ? "purch.invoice_changed" : "purch.invoice_entered", "purchase_invoice", r.Id,
            After: Json.Obj(new { number, r.SupplierId, supplierInvoiceNo = r.SupplierInvoiceNo.Trim(), status, gross = net + tax }), BranchId: r.BranchId));
        return await InvoiceAsync(t, r.Id);
    }

    private static async Task InsertLinesAsync(DbTx t, Guid invoiceId, IReadOnlyList<MatchedLine> lines)
    {
        short no = 0;
        foreach (var l in lines)
            await t.ExecuteAsync("""
                INSERT INTO purch.invoice_line (id, invoice_id, line_no, kind, receipt_line_id, receipt_charge_id, outside_type, outside_id, account_id, expense_branch_id,
                                                description, qty, unit_price, net_amount, tax_code_id, tax_rate, tax_amount, expected_amount, order_price, match_status,
                                                variance, variance_inventory, variance_ppv)
                VALUES (@id, @invoiceId, @no, @Kind, @rl, @rc, @ot, @oid, @acc, @eb, @Description, @qty, @price, @Net, @TaxCodeId, @TaxRate, @Tax, @Expected, @OrderPrice,
                        @Status, @Variance, @VarianceInventory, @VariancePpv)
                """, new { id = t.NewId(), invoiceId, no = ++no, l.Kind, rl = l.Input.ReceiptLineId, rc = l.Input.ReceiptChargeId,
                           ot = l.Kind == "outside_work" ? l.Input.OutsideType : null, oid = l.Kind == "outside_work" ? l.Input.OutsideId : null,
                           acc = l.Kind == "expense" ? l.Input.AccountId : null, eb = l.Kind == "expense" ? l.Branch : (Guid?)null, l.Description, qty = l.Input.Qty,
                           price = l.Input.UnitPrice, l.Net, l.TaxCodeId, l.TaxRate, l.Tax, l.Expected, l.OrderPrice, l.Status, l.Variance, l.VarianceInventory, l.VariancePpv });
    }

    /// <summary>
    /// Posts a supplier invoice (16.2): matched again under the locks of what it clears; outside tolerance only with PURCH.INVOICE.OVERRIDE and a reason.
    /// Goods received not invoiced is cleared, stock still on hand takes the price difference, the rest goes to purchase price variance, the supplier's
    /// ledger and 2100 take the invoice, and branches that received for another branch's invoice owe it through the interbranch accounts.
    /// </summary>
    public static async Task<object> PostInvoiceAsync(DbTx t, Guid id, string? overrideReason, Guid uid, System.Security.Claims.ClaimsPrincipal user)
    {
        var h = await t.QuerySingleOrDefaultAsync<InvoiceHead>(InvoiceHead.Select + " WHERE id = @id", new { id })
            ?? throw new KiteException(ErrorCodes.NotFound, "That invoice was not found in your branches.", 404);
        user.Demand(Permissions.PurchInvoice, h.BranchId);
        if (h.Status == "posted") return await InvoiceAsync(t, id);
        if (h.Status == "cancelled") throw new KiteException(ErrorCodes.PurchInvoiceState, $"Invoice {h.Number} is cancelled.", 409);
        var input = await InputOfAsync(t, h);
        // Locks: every receipt and outside cost the invoice clears, in one order; then the supplier.
        var keys = new List<string>();
        foreach (var l in input.Lines)
        {
            if (l.ReceiptLineId is { } rl)
                keys.Add(ReceiptKey(await t.WithAllBranchesAsync(() => t.ExecuteScalarAsync<Guid>("SELECT receipt_id FROM purch.receipt_line WHERE id = @rl", new { rl }))));
            if (l.ReceiptChargeId is { } rc)
                keys.Add(ReceiptKey(await t.WithAllBranchesAsync(() => t.ExecuteScalarAsync<Guid>("SELECT receipt_id FROM purch.receipt_charge WHERE id = @rc", new { rc }))));
            if (l.OutsideId is { } oid && l.OutsideType is { } ot) keys.Add(OutsideKey(ot, oid));
        }
        await LockSourcesAsync(t, keys);
        await LockSupplierAsync(t, h.SupplierId);
        await t.ExecuteAsync("SELECT 1 FROM purch.purchase_invoice WHERE id = @id FOR UPDATE", new { id });
        var policy = await PolicyAsync(t);
        var biz = await BusinessInfo.LoadAsync(t);
        var supplier = await SupplierForAsync(t, h.SupplierId, h.BranchId, policy);
        var lines = await MatchAsync(t, input, policy, id);
        if (lines.FirstOrDefault(l => l.Status == "qty") is { } q)
            throw new KiteException(ErrorCodes.PurchQuantity, $"{q.Description}: {q.Problem}", 409);
        var outside = lines.Where(l => l.Status == "price").ToList();
        if (outside.Count > 0)
        {
            if (!user.HasIn(Permissions.PurchInvoiceOverride, h.BranchId))
                throw new KiteException(ErrorCodes.PurchMatch,
                    $"{outside.Count} line(s) differ from the order by more than the tolerance ({string.Join("; ", outside.Select(o => $"{o.Description}: {o.Net:0.000} invoiced, {o.Expected:0.000} expected"))}). Someone with PURCH.INVOICE.OVERRIDE posts it, with a reason.", 409);
            if (Clean(overrideReason) is null) throw new KiteException(ErrorCodes.Validation, "Say why the invoice is posted outside the tolerances.", 400, "overrideReason");
        }
        var net = lines.Sum(l => l.Net);
        var tax = lines.Sum(l => l.Tax);
        var gross = net + tax;

        // Stock still on hand takes its share of the price difference (16.3).
        foreach (var l in lines.Where(l => l.VarianceInventory != 0))
        {
            if (l.Serialized)
            {
                var units = (await t.WithAllBranchesAsync(() => t.QueryAsync<Guid>("""
                    SELECT ru.device_unit_id FROM purch.receipt_unit ru JOIN device.device_unit u ON u.id = ru.device_unit_id
                     WHERE ru.receipt_line_id = @rl AND device.in_stock_status(u.status, u.held_from_status) ORDER BY u.received_at, u.id
                    """, new { rl = l.ReceiptLineId }))).Take((int)l.Input.Qty).ToList();
                var parts = PurchaseMath.SplitEven(l.VarianceInventory, units.Count);
                for (var k = 0; k < units.Count; k++)
                {
                    if (parts[k] == 0) continue;
                    var u = await t.WithAllBranchesAsync(() => DeviceUnits.LoadAsync(t, units[k]));
                    if (u.AccumulatedCost + parts[k] < 0) throw new KiteException(ErrorCodes.PurchMatch, $"{u.Identifier}: the invoice would take its cost below zero.", 409);
                    var uk = units[k];
                    var amount = parts[k];
                    await t.WithAllBranchesAsync(async () =>
                    {
                        await DeviceUnits.ApplyEventAsync(t, uk, "cost_corrected", null, docType: "purchase_invoice", docId: id,
                            details: new { invoice = h.Number, amount }, costDelta: amount);
                        await DeviceUnits.MoveAsync(t, u, "unit_cost", 0, amount, "purchase_invoice", id, null, $"Invoice {h.Number}");
                        return 0;
                    });
                }
            }
            else
                await t.WithAllBranchesAsync(() => t.ExecuteAsync("""
                    INSERT INTO inv.stock_movement (id, business_id, branch_id, warehouse_id, variant_id, movement_type, qty, value, source_doc_type, source_doc_id,
                                                    reason, created_by, origin_server_id)
                    VALUES (@mid, @b, @Branch, @Warehouse, @Variant, 'cost_adjustment', 0, @VarianceInventory, 'purchase_invoice', @id, @reason, @uid,
                            (SELECT id FROM core.server WHERE is_self))
                    """, new { mid = t.NewId(), b = biz.Id, l.Branch, l.Warehouse, l.Variant, l.VarianceInventory, id, reason = $"Invoice {h.Number}", uid }));
        }
        // What the invoice clears from goods received not invoiced.
        foreach (var l in lines.Where(l => l.Expected != 0))
        {
            var (type, src) = l.Kind switch
            {
                "receipt" => ("receipt_line", l.ReceiptLineId!.Value),
                "charge" => ("receipt_charge", l.Input.ReceiptChargeId!.Value),
                _ => (l.Input.OutsideType!, l.Input.OutsideId!.Value),
            };
            await t.WithAllBranchesAsync(async () => { await GrniAsync(t, biz.Id, l.Branch, h.SupplierId, type, src, "invoiced", -l.Expected, "purchase_invoice", id); return 0; });
        }
        // The supplier's ledger, at the booking branch.
        var entry = t.NewId();
        await t.ExecuteAsync("""
            INSERT INTO purch.supplier_ledger (id, business_id, branch_id, supplier_id, kind, amount, doc_date, due_date, number, source_doc_type, source_doc_id, note,
                                               created_by, origin_server_id)
            VALUES (@entry, @b, @BranchId, @SupplierId, 'invoice', @gross, @d::date, @due::date, @Number, 'purchase_invoice', @id, @note, @uid, (SELECT id FROM core.server WHERE is_self))
            """, new { entry, b = biz.Id, h.BranchId, h.SupplierId, gross, d = D(h.InvoiceDate), due = D(h.DueDate), h.Number, id,
                       note = $"Invoice {h.SupplierInvoiceNo}", uid });
        // The journal: each branch's part, the booking branch owed by the others.
        var parts2 = new List<(string, Guid, PostingFacts)>();
        var main = new PostingFacts().Set("vat", tax).Set("payable", gross);
        foreach (var g in lines.GroupBy(l => l.Branch))
        {
            var f = g.Key == h.BranchId ? main : new PostingFacts();
            var grni = g.Sum(l => l.Expected);
            var ppv = g.Sum(l => l.VariancePpv);
            if (grni != 0) f.Set("grni_cleared", grni);
            if (ppv != 0) f.Set("ppv", ppv);
            foreach (var l in g.Where(l => l.VarianceInventory != 0))
                f.Add("inventory_variance_by_class", await ClassOfLineAsync(t, l), l.VarianceInventory);
            foreach (var l in g.Where(l => l.Kind == "expense")) f.Add("expense_by_account", l.AccountCode!, l.Net);
            if (g.Key != h.BranchId)
            {
                var owed = grni + ppv + g.Sum(l => l.VarianceInventory) + g.Where(l => l.Kind == "expense").Sum(l => l.Net);
                f.Set("due_to_branch", owed);
                main.Set("due_from_branch", (main.TryScalar("due_from_branch", out var x) ? x : 0m) + owed);
                parts2.Add((PostingRuleTemplates.PurchInvoice, g.Key, f));
            }
        }
        parts2.Insert(0, (PostingRuleTemplates.PurchInvoice, h.BranchId, main));
        var journal = await Posting.PostManyAsync(t, biz.Id, h.BranchId, PostingRuleTemplates.PurchInvoice, parts2, h.InvoiceDate, "purchase_invoice", id,
            $"Supplier invoice {h.Number} ({supplier.NameEn} {h.SupplierInvoiceNo})", partySupplierId: h.SupplierId);
        await t.ExecuteAsync("DELETE FROM purch.invoice_line WHERE invoice_id = @id", new { id });
        await InsertLinesAsync(t, id, lines);
        await t.ExecuteAsync("""
            UPDATE purch.purchase_invoice SET status = 'posted', net_total = @net, tax_total = @tax, gross_total = @gross, variance_total = @var, journal_id = @journal,
                   ledger_entry_id = @entry, posted_at = now(), posted_by = @uid, override_by = @ob, override_reason = @reason, updated_by = @uid WHERE id = @id
            """, new { id, net, tax, gross, var = lines.Sum(l => l.Variance), journal, entry, uid, ob = outside.Count > 0 ? uid : (Guid?)null,
                       reason = outside.Count > 0 ? Clean(overrideReason) : null });
        // An advance or a debit note already with the supplier at this branch settles the new invoice (oldest first).
        await AllocateOpenCreditsAsync(t, biz.Id, h.SupplierId, h.BranchId);
        await t.AuditAsync(new AuditEntry("purch.invoice_posted", "purchase_invoice", id,
            After: Json.Obj(new { h.Number, h.SupplierInvoiceNo, net, tax, gross, variance = lines.Sum(l => l.Variance), overridden = outside.Count }),
            Reason: outside.Count > 0 ? Clean(overrideReason) : null, BranchId: h.BranchId));
        await t.PublishAsync("purch.invoice_posted", "purchase_invoice", id, 1, Json.Obj(new { h.Number, h.SupplierId, gross }), h.BranchId);
        return await InvoiceAsync(t, id);
    }

    private static async Task<string> ClassOfLineAsync(DbTx t, MatchedLine l)
    {
        if (!l.Serialized) return l.ProductType!;
        var cond = await t.WithAllBranchesAsync(() => t.ExecuteScalarAsync<string?>("""
            SELECT u.condition FROM purch.receipt_unit ru JOIN device.device_unit u ON u.id = ru.device_unit_id WHERE ru.receipt_line_id = @rl ORDER BY u.received_at LIMIT 1
            """, new { rl = l.ReceiptLineId }));
        return PostingRuleTemplates.ClassOf(l.ProductType!, cond);
    }

    public sealed class InvoiceHead
    {
        public Guid Id { get; set; }
        public Guid BranchId { get; set; }
        public string Number { get; set; } = "";
        public Guid SupplierId { get; set; }
        public string SupplierInvoiceNo { get; set; } = "";
        public DateTime InvoiceDateRaw { get; set; }
        public DateTime DueDateRaw { get; set; }
        public string Status { get; set; } = "";
        public string? Note { get; set; }
        public int Version { get; set; }
        public DateOnly InvoiceDate => DateOnly.FromDateTime(InvoiceDateRaw);
        public DateOnly DueDate => DateOnly.FromDateTime(DueDateRaw);
        public const string Select = """
            SELECT id, branch_id AS BranchId, number, supplier_id AS SupplierId, supplier_invoice_no AS SupplierInvoiceNo, invoice_date AS InvoiceDateRaw,
                   due_date AS DueDateRaw, status, note, version FROM purch.purchase_invoice
            """;
    }

    private static async Task<InvoiceInput> InputOfAsync(DbTx t, InvoiceHead h)
    {
        var lines = (await t.QueryAsync<(string Kind, decimal Qty, decimal UnitPrice, Guid? Rl, Guid? Rc, string? Ot, Guid? Oid, Guid? Acc, Guid? Eb, string Desc, Guid Tax)>("""
            SELECT kind, qty, unit_price, receipt_line_id, receipt_charge_id, outside_type, outside_id, account_id, expense_branch_id, description, tax_code_id
              FROM purch.invoice_line WHERE invoice_id = @Id ORDER BY line_no
            """, new { h.Id })).Select(x => new InvoiceLineInput(x.Kind, x.Qty, x.UnitPrice, x.Rl, x.Rc, x.Ot, x.Oid, x.Acc, x.Eb, x.Desc, x.Tax)).ToList();
        return new InvoiceInput(h.Id, h.BranchId, h.SupplierId, h.SupplierInvoiceNo, h.InvoiceDate, lines, h.DueDate, h.Note, h.Version);
    }

    public static async Task CancelInvoiceAsync(DbTx t, Guid id, Guid uid, System.Security.Claims.ClaimsPrincipal user)
    {
        var h = await t.QuerySingleOrDefaultAsync<InvoiceHead>(InvoiceHead.Select + " WHERE id = @id FOR UPDATE", new { id })
            ?? throw new KiteException(ErrorCodes.NotFound, "That invoice was not found in your branches.", 404);
        user.Demand(Permissions.PurchInvoice, h.BranchId);
        if (h.Status is "posted" or "cancelled") throw new KiteException(ErrorCodes.PurchInvoiceState, $"Invoice {h.Number} is {h.Status}; a posted invoice is corrected by a return or a debit note.", 409);
        await t.ExecuteAsync("DELETE FROM purch.invoice_line WHERE invoice_id = @id", new { id });
        await t.ExecuteAsync("UPDATE purch.purchase_invoice SET status = 'cancelled', updated_by = @uid WHERE id = @id", new { id, uid });
        await t.AuditAsync(new AuditEntry("purch.invoice_cancelled", "purchase_invoice", id, After: Json.Obj(new { h.Number }), BranchId: h.BranchId));
    }

    public static async Task<object> InvoiceAsync(DbTx t, Guid id) =>
        await t.WithAllBranchesAsync(async () =>
        {
            var head = (await t.RowsAsync("""
                SELECT i.id, i.number, i.status, i.supplier_invoice_no AS "supplierInvoiceNo", i.invoice_date::text AS "invoiceDate", i.due_date::text AS "dueDate",
                       i.branch_id AS "branchId", b.code AS "branchCode", i.supplier_id AS "supplierId", s.number AS "supplierNumber", s.name_en AS "supplierNameEn",
                       s.name_ar AS "supplierNameAr", i.net_total AS "netTotal", i.tax_total AS "taxTotal", i.gross_total AS "grossTotal", i.variance_total AS "varianceTotal",
                       i.note, i.version, i.override_reason AS "overrideReason", ou.display_name_en AS "overrideBy", i.posted_at AS "postedAt", pu.display_name_en AS "postedBy",
                       (SELECT j.number FROM acct.journal j WHERE j.id = i.journal_id) AS "journalNumber", i.journal_id AS "journalId",
                       coalesce((SELECT sum(a.amount) FROM purch.supplier_allocation a WHERE a.debit_entry_id = i.ledger_entry_id), 0) AS paid
                  FROM purch.purchase_invoice i JOIN core.branch b ON b.id = i.branch_id JOIN purch.supplier s ON s.id = i.supplier_id
                  LEFT JOIN iam."user" ou ON ou.id = i.override_by LEFT JOIN iam."user" pu ON pu.id = i.posted_by
                 WHERE i.id = @id
                """, new { id })).SingleOrDefault() ?? throw new KiteException(ErrorCodes.NotFound, "That invoice was not found.", 404);
            var lines = await t.RowsAsync("""
                SELECT l.id, l.line_no AS "lineNo", l.kind, l.description, l.qty, l.unit_price AS "unitPrice", l.net_amount AS "netAmount", l.tax_code_id AS "taxCodeId",
                       l.tax_rate AS "taxRate", l.tax_amount AS "taxAmount", l.expected_amount AS "expectedAmount", l.order_price AS "orderPrice", l.match_status AS "matchStatus",
                       l.variance, l.variance_inventory AS "varianceInventory", l.variance_ppv AS "variancePpv", l.receipt_line_id AS "receiptLineId",
                       l.receipt_charge_id AS "receiptChargeId", l.outside_type AS "outsideType", l.outside_id AS "outsideId", l.account_id AS "accountId",
                       a.code AS "accountCode", l.expense_branch_id AS "expenseBranchId", r.number AS "receiptNumber", rl.unit_price AS "receiptPrice", br.code AS "branchCode"
                  FROM purch.invoice_line l LEFT JOIN purch.receipt_line rl ON rl.id = l.receipt_line_id
                  LEFT JOIN purch.goods_receipt r ON r.id = coalesce(rl.receipt_id, (SELECT c.receipt_id FROM purch.receipt_charge c WHERE c.id = l.receipt_charge_id))
                  LEFT JOIN acct.account a ON a.id = l.account_id
                  LEFT JOIN core.branch br ON br.id = coalesce(r.branch_id, l.expense_branch_id)
                 WHERE l.invoice_id = @id ORDER BY l.line_no
                """, new { id });
            return (object)new { invoice = head, lines };
        });

    /// <summary>Everything a supplier's invoice can match (16.2): receipt lines and landed costs not yet invoiced, and outside work.</summary>
    public static async Task<object> OpenForSupplierAsync(DbTx t, Guid supplierId) =>
        await t.WithAllBranchesAsync(async () => (object)new
        {
            // The open-line query's aliases are unquoted, so PostgreSQL folds them to lower case.
            receiptLines = await t.RowsAsync($"""
                SELECT x.id, x.receiptid AS "receiptId", r.number AS "receiptNumber", r.receipt_date::text AS "receiptDate", b.code AS "branchCode", x.description,
                       x.qty, x.qty - x.invoiced - x.returnedopen AS "openQty", x.unitprice AS "unitPrice", x.orderprice AS "orderPrice", x.openvalue AS "openValue",
                       x.taxcodeid AS "taxCodeId", x.tracking
                  FROM ({OpenLineSelect}) x
                  JOIN purch.goods_receipt r ON r.id = x.receiptid JOIN core.branch b ON b.id = r.branch_id
                 WHERE x.supplierid = @supplierId AND x.qty - x.invoiced - x.returnedopen > 0
                 ORDER BY r.receipt_date, r.number
                """, new { supplierId }),
            charges = await t.RowsAsync("""
                SELECT c.id, r.number AS "receiptNumber", r.receipt_date::text AS "receiptDate", b.code AS "branchCode", c.kind, c.description, c.amount,
                       (SELECT sum(g.amount) FROM purch.grni_entry g WHERE g.source_type = 'receipt_charge' AND g.source_id = c.id) AS "openValue"
                  FROM purch.receipt_charge c JOIN purch.goods_receipt r ON r.id = c.receipt_id JOIN core.branch b ON b.id = r.branch_id
                 WHERE c.supplier_id = @supplierId
                   AND (SELECT sum(g.amount) FROM purch.grni_entry g WHERE g.source_type = 'receipt_charge' AND g.source_id = c.id) > 0
                 ORDER BY r.receipt_date
                """, new { supplierId }),
            outsideWork = await OpenOutsideWorkAsync(t),
        });

    public static Task<IReadOnlyList<Dictionary<string, object?>>> OpenOutsideWorkAsync(DbTx t) => t.RowsAsync("""
        SELECT g.source_type AS "outsideType", g.source_id AS "outsideId", b.code AS "branchCode", min(g.vendor) AS vendor, sum(g.amount) AS "openValue",
               min(g.created_at)::date::text AS "date",
               coalesce((SELECT j.number FROM repair.repair_line rl JOIN repair.repair_job j ON j.id = rl.job_id WHERE g.source_type = 'repair_line' AND rl.id = g.source_id),
                        (SELECT o.number FROM refurb.refurb_cost c JOIN refurb.refurb_order o ON o.id = c.refurb_order_id WHERE g.source_type = 'refurb_cost' AND c.id = g.source_id)) AS reference
          FROM purch.grni_entry g JOIN core.branch b ON b.id = g.branch_id
         WHERE g.source_type IN ('refurb_cost','repair_line')
         GROUP BY g.source_type, g.source_id, b.code HAVING sum(g.amount) > 0
         ORDER BY min(g.created_at)
        """);

    // ================================================================ supplier returns: debit notes (16.2)
    public static async Task<object> ReturnAsync(DbTx t, ReturnInput r, Guid uid, System.Security.Claims.ClaimsPrincipal user)
    {
        if (await t.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM purch.supplier_return WHERE id = @Id)", new { r.Id })) return await ReturnDetailAsync(t, r.Id);
        user.Demand(Permissions.PurchReturn, r.BranchId);
        if (r.Lines is not { Count: > 0 }) throw new KiteException(ErrorCodes.Validation, "Add at least one line.", 400, "lines");
        if (Clean(r.Reason) is null) throw new KiteException(ErrorCodes.Validation, "Say why the goods go back.", 400, "reason");
        var biz = await BusinessInfo.LoadAsync(t);
        var policy = await PolicyAsync(t);
        await SupplierForAsync(t, r.SupplierId, r.BranchId, policy);
        var date = r.ReturnDate ?? BusinessDay.Today(biz.TimeZone, t.Clock);
        var receiptIds = new List<Guid>();
        foreach (var l in r.Lines)
            receiptIds.Add(await t.ExecuteScalarAsync<Guid?>("SELECT receipt_id FROM purch.receipt_line WHERE id = @ReceiptLineId", new { l.ReceiptLineId })
                ?? throw new KiteException(ErrorCodes.NotFound, "That receipt line was not found in your branches.", 404, "lines"));
        await LockSourcesAsync(t, receiptIds.Select(ReceiptKey));
        await LockSupplierAsync(t, r.SupplierId);
        var number = await DocNumbers.NextAsync(t, biz.Id, r.BranchId, "supplier_return", await FiscalPeriods.FiscalYearOfAsync(t, biz.Id, date), "DN");
        var warehouse = Guid.Empty;
        var facts = new PostingFacts();
        decimal grniBack = 0, debitNet = 0, debitTax = 0, stockOut = 0;
        var rows = new List<(Guid ReceiptLine, Guid? Unit, decimal Qty, decimal Stock, bool Invoiced, decimal Net, decimal Tax)>();
        for (var i = 0; i < r.Lines.Count; i++)
        {
            var l = r.Lines[i];
            var f = $"lines[{i}]";
            Quantity(l.Qty, f + ".qty");
            var rl = await t.QuerySingleOrDefaultAsync<OpenReceiptLine>(OpenLineSelect + " WHERE l.id = @id", new { id = l.ReceiptLineId })
                ?? throw new KiteException(ErrorCodes.NotFound, "That receipt line was not found in your branches.", 404, f + ".receiptLineId");
            if (rl.BranchId != r.BranchId) throw new KiteException(ErrorCodes.Validation, "Those goods were received at another branch.", 400, f);
            if (rl.SupplierId != r.SupplierId) throw new KiteException(ErrorCodes.Validation, "Those goods came from another supplier.", 400, f);
            if (warehouse == Guid.Empty) warehouse = rl.WarehouseId;
            var returnedAll = await t.ExecuteScalarAsync<decimal>("SELECT coalesce(sum(qty), 0) FROM purch.supplier_return_line WHERE receipt_line_id = @Id", new { rl.Id });
            var openUninvoiced = rl.Qty - rl.Invoiced - rl.ReturnedOpen;
            var returnable = rl.Qty - returnedAll - rows.Where(x => x.ReceiptLine == rl.Id).Sum(x => x.Qty);
            if (l.Qty > returnable) throw new KiteException(ErrorCodes.PurchQuantity, $"Only {returnable:0.###} of {rl.Description} can still go back.", 409, f + ".qty");
            // Goods not yet invoiced go back out of goods received not invoiced; the rest on a debit note at the invoice's price, with its VAT.
            var fromOpen = Math.Min(l.Qty, Math.Max(0, openUninvoiced - rows.Where(x => x.ReceiptLine == rl.Id && !x.Invoiced).Sum(x => x.Qty)));
            var fromInvoiced = l.Qty - fromOpen;
            var invoicePrice = fromInvoiced > 0 ? await t.QuerySingleOrDefaultAsync<(decimal Price, decimal Rate)>("""
                SELECT il.unit_price, il.tax_rate FROM purch.invoice_line il JOIN purch.purchase_invoice i ON i.id = il.invoice_id
                 WHERE il.receipt_line_id = @Id AND i.status = 'posted' ORDER BY i.posted_at DESC LIMIT 1
                """, new { rl.Id }) : default;
            List<Guid?> unitIds;
            if (rl.Tracking != "none")
            {
                if (l.DeviceUnitIds is null || l.DeviceUnitIds.Count != (int)l.Qty || l.Qty != decimal.Truncate(l.Qty))
                    throw new KiteException(ErrorCodes.DeviceUnitCount, "Scan each phone that goes back.", 400, f + ".deviceUnitIds");
                unitIds = l.DeviceUnitIds.Select(x => (Guid?)x).ToList();
            }
            else
            {
                if (l.DeviceUnitIds is { Count: > 0 }) throw new KiteException(ErrorCodes.Validation, "Only serialized items are returned by IMEI.", 400, f + ".deviceUnitIds");
                unitIds = [null];
            }
            var perUnitQty = rl.Tracking != "none" ? 1m : l.Qty;
            var openLeft = fromOpen;
            foreach (var unit in unitIds)
            {
                decimal stock;
                if (unit is { } uid2)
                {
                    if (!await t.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM purch.receipt_unit WHERE device_unit_id = @uid2 AND receipt_line_id = @Id)", new { uid2, rl.Id }))
                        throw new KiteException(ErrorCodes.Validation, "That phone was not received on that line.", 400, f + ".deviceUnitIds");
                    var u = await DeviceUnits.LoadAsync(t, uid2);
                    if (u.Status is not ("in_stock" or "supplier_return"))
                        throw new KiteException(ErrorCodes.DeviceTransition, $"{u.Identifier} is {u.Status.Replace('_', ' ')}; only a phone in stock goes back.", 409, f + ".deviceUnitIds");
                    await DeviceUnits.ApplyEventAsync(t, uid2, "supplier_returned", "supplier_returned", docType: "supplier_return", docId: r.Id, details: new { number });
                    var (_, v) = await DeviceUnits.MoveAsync(t, u, "supplier_return", -1, null, "supplier_return", r.Id, null, number);
                    stock = -v;
                    facts.Add("stock_by_class", u.PostingClass, stock);
                }
                else
                {
                    var v = await t.ExecuteScalarAsync<decimal>("""
                        INSERT INTO inv.stock_movement (id, business_id, branch_id, warehouse_id, variant_id, movement_type, qty, source_doc_type, source_doc_id, reason,
                                                        created_by, origin_server_id)
                        VALUES (@mid, @b, @BranchId, @WarehouseId, @VariantId, 'supplier_return', @q, 'supplier_return', @rid, @number, @uid,
                                (SELECT id FROM core.server WHERE is_self))
                        RETURNING value
                        """, new { mid = t.NewId(), b = biz.Id, rl.BranchId, rl.WarehouseId, rl.VariantId, q = -perUnitQty, rid = r.Id, number, uid });
                    stock = -v;
                    facts.Add("stock_by_class", rl.ProductType, stock);
                }
                stockOut += stock;
                // Split this unit (or the quantity) between the open part and the invoiced part.
                var openQ = Math.Min(perUnitQty, openLeft);
                openLeft -= openQ;
                var invQ = perUnitQty - openQ;
                var openQtyNow = rl.Qty - rl.Invoiced - rl.ReturnedOpen - rows.Where(x => x.ReceiptLine == rl.Id && !x.Invoiced).Sum(x => x.Qty);
                var openValueNow = rl.OpenValue + rows.Where(x => x.ReceiptLine == rl.Id && !x.Invoiced).Sum(x => -x.Net);
                if (openQ > 0)
                {
                    var back = PurchaseMath.Clearing(openQ, openQtyNow, openValueNow);
                    var share = invQ > 0 ? PurchaseMath.Round3(stock * openQ / perUnitQty) : stock;
                    rows.Add((rl.Id, unit, openQ, share, false, back, 0m));
                    grniBack += back;
                    await GrniAsync(t, biz.Id, r.BranchId, r.SupplierId, "receipt_line", rl.Id, "returned", -back, "supplier_return", r.Id);
                    stock -= share;
                }
                if (invQ > 0)
                {
                    var netBack = PurchaseMath.LineNet(invQ, invoicePrice.Price);
                    var taxBack = PurchaseMath.LineTax(netBack, invoicePrice.Rate);
                    rows.Add((rl.Id, unit, invQ, stock, true, netBack, taxBack));
                    debitNet += netBack;
                    debitTax += taxBack;
                }
            }
        }
        var ppv = grniBack + debitNet - stockOut;
        if (grniBack != 0) facts.Set("grni_back", grniBack);
        if (debitNet + debitTax != 0) facts.Set("payable_back", debitNet + debitTax);
        if (debitTax != 0) facts.Set("vat_back", debitTax);
        if (ppv != 0) facts.Set("ppv", ppv);
        Guid? entry = null;
        if (debitNet + debitTax > 0)
        {
            entry = t.NewId();
            await t.ExecuteAsync("""
                INSERT INTO purch.supplier_ledger (id, business_id, branch_id, supplier_id, kind, amount, doc_date, number, source_doc_type, source_doc_id, note, created_by, origin_server_id)
                VALUES (@entry, @b, @BranchId, @SupplierId, 'debit_note', @neg, @d::date, @number, 'supplier_return', @Id, @note, @uid, (SELECT id FROM core.server WHERE is_self))
                """, new { entry, b = biz.Id, r.BranchId, r.SupplierId, neg = -(debitNet + debitTax), d = D(date), number, r.Id, note = $"Debit note {number}: {r.Reason.Trim()}", uid });
        }
        await t.ExecuteAsync("""
            INSERT INTO purch.supplier_return (id, business_id, branch_id, warehouse_id, number, supplier_id, return_date, reason, net_total, tax_total, stock_value, ledger_entry_id,
                                               created_by, origin_server_id)
            VALUES (@Id, @b, @BranchId, @warehouse, @number, @SupplierId, @d::date, @reason, @net, @tax, @stockOut, @entry, @uid, (SELECT id FROM core.server WHERE is_self))
            """, new { r.Id, b = biz.Id, r.BranchId, warehouse, number, r.SupplierId, d = D(date), reason = r.Reason.Trim(), net = grniBack + debitNet, tax = debitTax, stockOut, entry, uid });
        short no = 0;
        foreach (var x in rows)
            await t.ExecuteAsync("""
                INSERT INTO purch.supplier_return_line (id, return_id, line_no, receipt_line_id, device_unit_id, qty, stock_value, invoiced, net_amount, tax_amount)
                VALUES (@lineId, @returnId, @no, @ReceiptLine, @Unit, @Qty, @Stock, @Invoiced, @Net, @Tax)
                """, new { lineId = t.NewId(), returnId = r.Id, no = ++no, x.ReceiptLine, x.Unit, x.Qty, Stock = Math.Max(0, x.Stock), x.Invoiced, x.Net, x.Tax });
        var journal = await Posting.PostAsync(t, biz.Id, r.BranchId, PostingRuleTemplates.PurchReturn, facts, date, "supplier_return", r.Id,
            $"Goods returned to the supplier, {number}", partySupplierId: r.SupplierId);
        await t.ExecuteAsync("UPDATE purch.supplier_return SET journal_id = @journal WHERE id = @Id", new { journal, r.Id });
        if (entry is not null) await AllocateOpenCreditsAsync(t, biz.Id, r.SupplierId, r.BranchId);
        await t.AuditAsync(new AuditEntry("purch.goods_returned", "supplier_return", r.Id,
            After: Json.Obj(new { number, r.SupplierId, grniBack, debitNet, debitTax, stockOut, reason = r.Reason.Trim() }), BranchId: r.BranchId));
        await t.PublishAsync("purch.goods_returned", "supplier_return", r.Id, 1, Json.Obj(new { number, r.SupplierId }), r.BranchId);
        return await ReturnDetailAsync(t, r.Id);
    }

    public static async Task<object> ReturnDetailAsync(DbTx t, Guid id) =>
        await t.WithAllBranchesAsync(async () => (object)new
        {
            supplierReturn = (await t.RowsAsync("""
                SELECT r.id, r.number, r.return_date::text AS "returnDate", r.reason, r.net_total AS "netTotal", r.tax_total AS "taxTotal", r.stock_value AS "stockValue",
                       b.code AS "branchCode", r.supplier_id AS "supplierId", s.name_en AS "supplierNameEn", s.name_ar AS "supplierNameAr",
                       (SELECT j.number FROM acct.journal j WHERE j.id = r.journal_id) AS "journalNumber", r.created_at AS "createdAt"
                  FROM purch.supplier_return r JOIN core.branch b ON b.id = r.branch_id JOIN purch.supplier s ON s.id = r.supplier_id WHERE r.id = @id
                """, new { id })).SingleOrDefault() ?? throw new KiteException(ErrorCodes.NotFound, "That return was not found.", 404),
            lines = await t.RowsAsync("""
                SELECT x.line_no AS "lineNo", x.qty, x.stock_value AS "stockValue", x.invoiced, x.net_amount AS "netAmount", x.tax_amount AS "taxAmount",
                       p.name_en AS description, g.number AS "receiptNumber",
                       (SELECT r2.value FROM device.imei_registry r2 WHERE r2.device_unit_id = x.device_unit_id AND r2.status = 'active' ORDER BY r2.kind = 'imei' DESC, r2.slot LIMIT 1) AS imei
                  FROM purch.supplier_return_line x JOIN purch.receipt_line rl ON rl.id = x.receipt_line_id JOIN purch.goods_receipt g ON g.id = rl.receipt_id
                  JOIN catalog.variant v ON v.id = rl.variant_id JOIN catalog.product p ON p.id = v.product_id
                 WHERE x.return_id = @id ORDER BY x.line_no
                """, new { id }),
        });

    // ================================================================ supplier payments and allocations (16.1, 16.2)
    private sealed class OpenEntry
    {
        public Guid Id { get; set; }
        public Guid BranchId { get; set; }
        public string Number { get; set; } = "";
        public DateTime Due { get; set; }
        public decimal Open { get; set; }
    }

    private const string OpenDebits = """
        SELECT l.id, l.branch_id AS BranchId, l.number, coalesce(l.due_date, l.doc_date) AS Due,
               l.amount - coalesce((SELECT sum(a.amount) FROM purch.supplier_allocation a WHERE a.debit_entry_id = l.id), 0) AS Open
          FROM purch.supplier_ledger l
         WHERE l.supplier_id = @supplierId AND l.amount > 0 AND l.kind IN ('invoice','opening','adjustment')
           AND l.amount > coalesce((SELECT sum(a.amount) FROM purch.supplier_allocation a WHERE a.debit_entry_id = l.id), 0)
        """;

    /// <summary>Advances and debit notes still open at a branch settle that branch's oldest open invoices.</summary>
    public static async Task AllocateOpenCreditsAsync(DbTx t, Guid businessId, Guid supplierId, Guid branchId)
    {
        var credits = (await t.QueryAsync<(Guid Id, decimal Left)>("""
            SELECT l.id, -l.amount - coalesce((SELECT sum(a.amount) FROM purch.supplier_allocation a WHERE a.credit_entry_id = l.id), 0)
              FROM purch.supplier_ledger l
             WHERE l.supplier_id = @supplierId AND l.branch_id = @branchId AND l.amount < 0 AND l.kind IN ('payment','debit_note','adjustment')
             ORDER BY l.doc_date, l.created_at
            """, new { supplierId, branchId })).Where(c => c.Left > 0).ToList();
        if (credits.Count == 0) return;
        var debits = (await t.QueryAsync<OpenEntry>(OpenDebits + " AND l.branch_id = @branchId", new { supplierId, branchId }))
            .Select(d => new OpenItem(d.Id, DateOnly.FromDateTime(d.Due), d.Open)).ToList();
        foreach (var (cid, left) in credits)
        {
            var (allocs, _) = CreditMath.AllocateOldestFirst(debits, left);
            foreach (var a in allocs)
            {
                await t.ExecuteAsync("""
                    INSERT INTO purch.supplier_allocation (id, business_id, debit_entry_id, credit_entry_id, amount, created_by)
                    VALUES (@id, @businessId, @ItemId, @cid, @Amount, @by)
                    """, new { id = t.NewId(), businessId, a.ItemId, cid, a.Amount, by = t.Actor.UserId });
                var k = debits.FindIndex(d => d.Id == a.ItemId);
                debits[k] = debits[k] with { Open = debits[k].Open - a.Amount };
            }
        }
    }

    public static async Task<object> PayAsync(DbTx t, SupplierPaymentInput r, Guid uid, System.Security.Claims.ClaimsPrincipal user)
    {
        if (await t.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM purch.supplier_payment WHERE id = @Id)", new { r.Id })) return await PaymentAsync(t, r.Id);
        user.Demand(Permissions.PurchPay, r.BranchId);
        if (r.Method is not ("cash" or "bank_transfer" or "cheque")) throw new KiteException(ErrorCodes.Validation, "A supplier is paid in cash, by transfer or by cheque.", 400, "method");
        Money(r.Amount, "amount", positive: true);
        if (r.Method != "cash" && Clean(r.Reference) is null) throw new KiteException(ErrorCodes.Validation, "Give the transfer or cheque reference.", 400, "reference");
        var biz = await BusinessInfo.LoadAsync(t);
        var policy = await PolicyAsync(t);
        var supplier = await SupplierForAsync(t, r.SupplierId, r.BranchId, policy, forPayment: true);
        var date = r.PaymentDate ?? BusinessDay.Today(biz.TimeZone, t.Clock);
        await LockSupplierAsync(t, r.SupplierId);
        var open = (await t.WithAllBranchesAsync(() => t.QueryAsync<OpenEntry>(OpenDebits, new { supplierId = r.SupplierId }))).ToDictionary(e => e.Id);
        var allocations = new List<(OpenEntry Entry, decimal Amount)>();
        if (r.Allocations is { Count: > 0 })
        {
            foreach (var (a, i) in r.Allocations.Select((a, i) => (a, i)))
            {
                Money(a.Amount, $"allocations[{i}].amount", positive: true);
                if (!open.TryGetValue(a.EntryId, out var e)) throw new KiteException(ErrorCodes.PurchAllocation, "That invoice is not open for this supplier.", 409, $"allocations[{i}].entryId");
                if (a.Amount > e.Open - allocations.Where(x => x.Entry.Id == e.Id).Sum(x => x.Amount))
                    throw new KiteException(ErrorCodes.PurchAllocation, $"{e.Number} has only {e.Open:0.000} open.", 409, $"allocations[{i}].amount");
                if (e.BranchId != r.BranchId && !t.Server.IsHeadOffice)
                    throw new KiteException(ErrorCodes.NotHeadOffice, "Only head office pays another branch's invoices.", 409, $"allocations[{i}].entryId");
                allocations.Add((e, a.Amount));
            }
            if (allocations.Sum(a => a.Amount) > r.Amount) throw new KiteException(ErrorCodes.PurchAllocation, "The allocations add up to more than the payment.", 409, "allocations");
        }
        else
        {
            var mine = open.Values.Where(e => e.BranchId == r.BranchId).Select(e => new OpenItem(e.Id, DateOnly.FromDateTime(e.Due), e.Open)).ToList();
            var (allocs, _) = CreditMath.AllocateOldestFirst(mine, r.Amount);
            allocations.AddRange(allocs.Select(a => (open[a.ItemId], a.Amount)));
        }
        var number = await DocNumbers.NextAsync(t, biz.Id, r.BranchId, "supplier_payment", await FiscalPeriods.FiscalYearOfAsync(t, biz.Id, date), "PV");
        await t.ExecuteAsync("""
            INSERT INTO purch.supplier_payment (id, business_id, branch_id, number, supplier_id, payment_date, method, reference, amount, note, created_by, origin_server_id)
            VALUES (@Id, @b, @BranchId, @number, @SupplierId, @d::date, @Method, @reference, @Amount, @note, @uid, (SELECT id FROM core.server WHERE is_self))
            """, new { r.Id, b = biz.Id, r.BranchId, number, r.SupplierId, d = D(date), r.Method, reference = Clean(r.Reference), r.Amount, note = Clean(r.Note), uid });
        // One ledger row per branch whose invoices it settles, the rest (an advance) at the paying branch.
        var byBranch = allocations.GroupBy(a => a.Entry.BranchId).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
        var rest = r.Amount - allocations.Sum(a => a.Amount);
        if (rest > 0) byBranch[r.BranchId] = byBranch.GetValueOrDefault(r.BranchId) + rest;
        var entries = new Dictionary<Guid, Guid>();
        await t.WithAllBranchesAsync(async () =>
        {
            foreach (var (branch, amount) in byBranch)
            {
                var entry = t.NewId();
                entries[branch] = entry;
                await t.ExecuteAsync("""
                    INSERT INTO purch.supplier_ledger (id, business_id, branch_id, supplier_id, kind, amount, doc_date, number, source_doc_type, source_doc_id, note, payment_id,
                                                       created_by, origin_server_id)
                    VALUES (@entry, @b, @branch, @SupplierId, 'payment', @neg, @d::date, @number, 'supplier_payment', @Id, @note, @Id, @uid, (SELECT id FROM core.server WHERE is_self))
                    """, new { entry, b = biz.Id, branch, r.SupplierId, neg = -amount, d = D(date), number, r.Id,
                               note = $"Payment {number} ({r.Method.Replace('_', ' ')}{(Clean(r.Reference) is { } rf ? " " + rf : "")})", uid });
            }
            foreach (var (e, amount) in allocations)
                await t.ExecuteAsync("""
                    INSERT INTO purch.supplier_allocation (id, business_id, debit_entry_id, credit_entry_id, amount, created_by)
                    VALUES (@id, @b, @debit, @credit, @amount, @uid)
                    """, new { id = t.NewId(), b = biz.Id, debit = e.Id, credit = entries[e.BranchId], amount, uid });
            return 0;
        });
        var main = new PostingFacts().Add("paid_by_method", r.Method, r.Amount).Set("paid", byBranch.GetValueOrDefault(r.BranchId));
        var parts = new List<(string, Guid, PostingFacts)> { (PostingRuleTemplates.PurchPayment, r.BranchId, main) };
        foreach (var (branch, amount) in byBranch.Where(b => b.Key != r.BranchId))
        {
            parts.Add((PostingRuleTemplates.PurchPayment, branch, new PostingFacts().Set("paid", amount).Set("due_to_branch", amount)));
            main.Set("due_from_branch", (main.TryScalar("due_from_branch", out var x) ? x : 0m) + amount);
        }
        var journal = await Posting.PostManyAsync(t, biz.Id, r.BranchId, PostingRuleTemplates.PurchPayment, parts, date, "supplier_payment", r.Id,
            $"Supplier payment {number}: {supplier.NameEn}", partySupplierId: r.SupplierId);
        await t.ExecuteAsync("UPDATE purch.supplier_payment SET journal_id = @journal WHERE id = @Id", new { journal, r.Id });
        await t.AuditAsync(new AuditEntry("purch.supplier_paid", "supplier_payment", r.Id,
            After: Json.Obj(new { number, r.SupplierId, r.Method, r.Amount, allocated = allocations.Sum(a => a.Amount), branches = byBranch.Count }), BranchId: r.BranchId));
        await t.PublishAsync("purch.supplier_paid", "supplier_payment", r.Id, 1, Json.Obj(new { number, r.SupplierId, r.Amount }), r.BranchId);
        return await PaymentAsync(t, r.Id);
    }

    public static async Task<object> PaymentAsync(DbTx t, Guid id) =>
        await t.WithAllBranchesAsync(async () => (object)new
        {
            payment = (await t.RowsAsync("""
                SELECT p.id, p.number, p.payment_date::text AS "paymentDate", p.method, p.reference, p.amount, p.note, b.code AS "branchCode", p.supplier_id AS "supplierId",
                       s.name_en AS "supplierNameEn", s.name_ar AS "supplierNameAr", (SELECT j.number FROM acct.journal j WHERE j.id = p.journal_id) AS "journalNumber",
                       p.created_at AS "createdAt"
                  FROM purch.supplier_payment p JOIN core.branch b ON b.id = p.branch_id JOIN purch.supplier s ON s.id = p.supplier_id WHERE p.id = @id
                """, new { id })).SingleOrDefault() ?? throw new KiteException(ErrorCodes.NotFound, "That payment was not found.", 404),
            allocations = await t.RowsAsync("""
                SELECT d.number, d.kind, b.code AS "branchCode", a.amount, d.due_date::text AS "dueDate"
                  FROM purch.supplier_ledger c JOIN purch.supplier_allocation a ON a.credit_entry_id = c.id JOIN purch.supplier_ledger d ON d.id = a.debit_entry_id
                  JOIN core.branch b ON b.id = d.branch_id
                 WHERE c.payment_id = @id ORDER BY d.due_date, d.number
                """, new { id }),
        });

    /// <summary>A supplier's open invoices, everywhere this server knows of (for choosing what a payment settles).</summary>
    public static Task<IReadOnlyList<Dictionary<string, object?>>> OpenItemsAsync(DbTx t, Guid supplierId) =>
        t.WithAllBranchesAsync(() => t.RowsAsync("""
            SELECT l.id, l.number, l.kind, l.doc_date::text AS "docDate", coalesce(l.due_date, l.doc_date)::text AS "dueDate", b.id AS "branchId", b.code AS "branchCode",
                   l.amount, l.amount - coalesce((SELECT sum(a.amount) FROM purch.supplier_allocation a WHERE a.debit_entry_id = l.id), 0) AS open,
                   (SELECT i.supplier_invoice_no FROM purch.purchase_invoice i WHERE i.ledger_entry_id = l.id) AS "supplierInvoiceNo"
              FROM purch.supplier_ledger l JOIN core.branch b ON b.id = l.branch_id
             WHERE (l.supplier_id = @supplierId OR l.supplier_id IN (SELECT id FROM purch.supplier WHERE merged_into_id = @supplierId))
               AND l.amount > 0 AND l.kind IN ('invoice','opening','adjustment')
               AND l.amount > coalesce((SELECT sum(a.amount) FROM purch.supplier_allocation a WHERE a.debit_entry_id = l.id), 0)
             ORDER BY coalesce(l.due_date, l.doc_date), l.number
            """, new { supplierId }));

    // ================================================================ price history (16.1)
    public static async Task<object> PriceHistoryAsync(DbTx t, Guid variantId) =>
        await t.WithAllBranchesAsync(async () => (object)new
        {
            last = (await t.RowsAsync("""
                SELECT l.unit_price AS "unitPrice", r.receipt_date::text AS date, s.name_en AS "supplierNameEn", s.name_ar AS "supplierNameAr", b.code AS "branchCode", r.number
                  FROM purch.receipt_line l JOIN purch.goods_receipt r ON r.id = l.receipt_id JOIN purch.supplier s ON s.id = r.supplier_id JOIN core.branch b ON b.id = r.branch_id
                 WHERE l.variant_id = @variantId ORDER BY r.receipt_date DESC, r.created_at DESC LIMIT 1
                """, new { variantId })).SingleOrDefault(),
            average = await t.ExecuteScalarAsync<decimal?>("""
                SELECT round(sum(l.goods_value) / nullif(sum(l.qty), 0), 3) FROM purch.receipt_line l JOIN purch.goods_receipt r ON r.id = l.receipt_id
                 WHERE l.variant_id = @variantId AND r.receipt_date >= current_date - 365
                """, new { variantId }),
            bySupplier = await t.RowsAsync("""
                SELECT DISTINCT ON (r.supplier_id) r.supplier_id AS "supplierId", s.name_en AS "supplierNameEn", s.name_ar AS "supplierNameAr", l.unit_price AS "unitPrice",
                       r.receipt_date::text AS date
                  FROM purch.receipt_line l JOIN purch.goods_receipt r ON r.id = l.receipt_id JOIN purch.supplier s ON s.id = r.supplier_id
                 WHERE l.variant_id = @variantId ORDER BY r.supplier_id, r.receipt_date DESC, r.created_at DESC
                """, new { variantId }),
        });
}
