using System.Globalization;
using Kite.Host.Infrastructure;
using Kite.Persistence;
using Kite.Platform;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Kite.Host.Features;

public sealed record OrderReasonRequest(string Reason);
public sealed record InvoicePostRequest(string? OverrideReason = null);
public sealed record BranchSuppliersRequest(List<Guid> SupplierIds);

/// <summary>Purchasing (blueprint 16): policy, orders, goods receipts, supplier invoices, returns and payments.</summary>
public static class PurchaseEndpoints
{
    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static string M(decimal v) => v.ToString("0.000", CultureInfo.InvariantCulture);
    private static readonly string[] Fonts = ["Segoe UI", "Tahoma", "DejaVu Sans", "Noto Sans Arabic"];

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/purchasing");

        // ------------------------------------------------------------ policy (16.1, 16.2)
        g.MapGet("/policy", async (HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t => (object)new
            {
                policy = await PurchCore.PolicyAsync(t),
                limits = await t.WithAllBranchesAsync(() => t.RowsAsync("""
                    SELECT l.id, l.role_id AS "roleId", r.code AS "roleCode", r.name_en AS "roleNameEn", r.name_ar AS "roleNameAr", l.branch_id AS "branchId",
                           b.code AS "branchCode", l.max_amount AS "maxAmount"
                      FROM purch.approval_limit l JOIN iam.role r ON r.id = l.role_id LEFT JOIN core.branch b ON b.id = l.branch_id
                     ORDER BY r.code, b.code NULLS FIRST
                    """)),
                roles = await t.RowsAsync("""SELECT id, code, name_en AS "nameEn", name_ar AS "nameAr" FROM iam.role ORDER BY code"""),
            }, ct))).RequirePermission(Permissions.PurchView).RequireModule("purch");

        g.MapPut("/policy", async (PurchPolicyInput req, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            if (req.PriceTolerancePct is < 0 or > 100 || req.PriceToleranceAmount < 0 || req.OverReceiptPct is < 0 or > 100)
                throw new KiteException(ErrorCodes.Validation, "Tolerances are between 0 and 100 per cent, and amounts zero or more.", 400);
            if (req.SupplierList is not ("all" or "approved")) throw new KiteException(ErrorCodes.Validation, "Branches buy from every supplier or from their approved list.", 400, "supplierList");
            await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var before = await PurchCore.PolicyAsync(t);
                var n = await t.ExecuteAsync("""
                    UPDATE purch.policy SET price_tolerance_pct = @PriceTolerancePct, price_tolerance_amount = @PriceToleranceAmount, over_receipt_pct = @OverReceiptPct,
                           supplier_list = @SupplierList, updated_by = @uid WHERE version = @Version
                    """, new { req.PriceTolerancePct, req.PriceToleranceAmount, req.OverReceiptPct, req.SupplierList, req.Version, uid = http.User.UserIdOf() });
                if (n == 0) throw new KiteException(ErrorCodes.VersionMismatch, "Someone changed the purchasing settings; open them again.", 412);
                await t.AuditAsync(new AuditEntry("purch.policy_changed", "purchase_policy", null, Before: Json.Obj(before), After: Json.Obj(req)));
                return 0;
            }, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.PurchSettings).RequireModule("purch");

        g.MapPut("/approval-limits", async (List<ApprovalLimitInput> req, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            if (req.Any(l => l.MaxAmount is < 0) || req.GroupBy(l => (l.RoleId, l.BranchId)).Any(x => x.Count() > 1))
                throw new KiteException(ErrorCodes.Validation, "One limit per role and branch, zero or more (empty: no limit).", 400);
            await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var biz = await BusinessInfo.LoadAsync(t);
                await t.WithAllBranchesAsync(async () =>
                {
                    await t.ExecuteAsync("DELETE FROM purch.approval_limit");
                    foreach (var l in req)
                        await t.ExecuteAsync("""
                            INSERT INTO purch.approval_limit (id, business_id, role_id, branch_id, max_amount, updated_by) VALUES (@id, @b, @RoleId, @BranchId, @MaxAmount, @uid)
                            """, new { id = t.NewId(), b = biz.Id, l.RoleId, l.BranchId, l.MaxAmount, uid = http.User.UserIdOf() });
                    return 0;
                });
                await t.AuditAsync(new AuditEntry("purch.approval_limits_changed", "approval_limit", null, After: Json.Obj(new { limits = req })));
                return 0;
            }, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.PurchSettings).RequireModule("purch");

        g.MapGet("/branches/{id:guid}/suppliers", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.RowsAsync("""
                SELECT s.id, s.number, s.name_en AS "nameEn", s.name_ar AS "nameAr", s.status,
                       EXISTS (SELECT 1 FROM purch.branch_supplier x WHERE x.branch_id = @id AND x.supplier_id = s.id) AS approved
                  FROM purch.supplier s WHERE s.status IN ('active','provisional') ORDER BY s.name_en
                """, new { id }), ct))).RequirePermission(Permissions.PurchView).RequireModule("purch");

        g.MapPut("/branches/{id:guid}/suppliers", async (Guid id, BranchSuppliersRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var biz = await BusinessInfo.LoadAsync(t);
                await t.WithAllBranchesAsync(async () =>
                {
                    await t.ExecuteAsync("DELETE FROM purch.branch_supplier WHERE branch_id = @id", new { id });
                    foreach (var s in req.SupplierIds.Distinct())
                        await t.ExecuteAsync("INSERT INTO purch.branch_supplier (branch_id, supplier_id, business_id, added_by) VALUES (@id, @s, @b, @uid)", new { id, s, b = biz.Id, uid = http.User.UserIdOf() });
                    return 0;
                });
                await t.AuditAsync(new AuditEntry("purch.branch_suppliers_changed", "branch", id, After: Json.Obj(new { suppliers = req.SupplierIds.Count }), BranchId: id));
                return 0;
            }, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.PurchSettings).RequireModule("purch");

        // ------------------------------------------------------------ orders (16.2)
        g.MapGet("/orders", async (string? status, Guid? supplierId, Guid? branchId, string? q, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.RowsAsync("""
                SELECT o.id, o.number, o.status, o.order_date::text AS "orderDate", o.expected_date::text AS "expectedDate", o.gross_total AS "grossTotal",
                       b.code AS "branchCode", o.supplier_id AS "supplierId", s.name_en AS "supplierNameEn", s.name_ar AS "supplierNameAr",
                       (SELECT count(*) FROM purch.po_line l WHERE l.po_id = o.id) AS lines,
                       (SELECT string_agg(DISTINCT db.code, ', ') FROM purch.po_line l JOIN core.branch db ON db.id = l.deliver_branch_id WHERE l.po_id = o.id) AS "deliverTo"
                  FROM purch.purchase_order o JOIN core.branch b ON b.id = o.branch_id JOIN purch.supplier s ON s.id = o.supplier_id
                 WHERE (@status::text IS NULL OR o.status = @status OR (@status = 'open' AND o.status IN ('approved','sent','partly_received')))
                   AND (@supplierId::uuid IS NULL OR o.supplier_id = @supplierId) AND (@branchId::uuid IS NULL OR o.branch_id = @branchId
                        OR EXISTS (SELECT 1 FROM purch.po_line l WHERE l.po_id = o.id AND l.deliver_branch_id = @branchId))
                   AND (@q::text IS NULL OR o.number ILIKE '%' || @q || '%' OR s.name_en ILIKE '%' || @q || '%' OR s.name_ar ILIKE '%' || @q || '%')
                 ORDER BY o.order_date DESC, o.created_at DESC LIMIT 300
                """, new { status = Clean(status), supplierId, branchId, q = Clean(q) }), ct))).RequirePermission(Permissions.PurchView).RequireModule("purch");

        g.MapPost("/orders", async (OrderInput req, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            http.User.Demand(Permissions.PurchOrder, req.BranchId);
            return Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => PurchCore.SaveOrderAsync(t, req, http.User.UserIdOf(), null), ct));
        }).RequirePermission(Permissions.PurchOrder).RequireModule("purch");

        g.MapGet("/orders/{id:guid}", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                await PurchCore.LoadOrderAsync(t, id);   // visible to the caller's branches
                return await PurchCore.OrderAsync(t, id, http.User.UserIdOf(), http.User);
            }, ct))).RequirePermission(Permissions.PurchView).RequireModule("purch");

        g.MapPut("/orders/{id:guid}", async (Guid id, OrderInput req, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            http.User.Demand(Permissions.PurchOrder, req.BranchId);
            return Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => PurchCore.SaveOrderAsync(t, req, http.User.UserIdOf(), id), ct));
        }).RequirePermission(Permissions.PurchOrder).RequireModule("purch");

        g.MapPost("/orders/{id:guid}/approve", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                await PurchCore.ApproveOrderAsync(t, id, http.User.UserIdOf(), http.User);
                return await PurchCore.OrderAsync(t, id, http.User.UserIdOf(), http.User);
            }, ct))).RequirePermission(Permissions.PurchApprove).RequireModule("purch");

        g.MapPost("/orders/{id:guid}/send", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                await PurchCore.LockOrderAsync(t, id);
                var o = await PurchCore.LoadOrderAsync(t, id, forUpdate: true);
                http.User.Demand(Permissions.PurchOrder, o.BranchId);
                if (o.Status == "sent") return await PurchCore.OrderAsync(t, id, http.User.UserIdOf(), http.User);
                if (o.Status != "approved") throw new KiteException(ErrorCodes.PurchOrderState, $"Order {o.Number} is {o.Status.Replace('_', ' ')}; an approved order is sent.", 409);
                await t.ExecuteAsync("UPDATE purch.purchase_order SET status = 'sent', sent_at = now(), sent_by = @uid, updated_by = @uid WHERE id = @id",
                    new { id, uid = http.User.UserIdOf() });
                await t.AuditAsync(new AuditEntry("purch.order_sent", "purchase_order", id, After: Json.Obj(new { o.Number }), BranchId: o.BranchId));
                return await PurchCore.OrderAsync(t, id, http.User.UserIdOf(), http.User);
            }, ct))).RequirePermission(Permissions.PurchOrder).RequireModule("purch");

        g.MapPost("/orders/{id:guid}/cancel", async (Guid id, OrderReasonRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                if (Clean(req.Reason) is null) throw new KiteException(ErrorCodes.Validation, "Say why the order is cancelled.", 400, "reason");
                await PurchCore.LockOrderAsync(t, id);
                var o = await PurchCore.LoadOrderAsync(t, id, forUpdate: true);
                http.User.Demand(Permissions.PurchOrder, o.BranchId);
                if (o.Status is not ("draft" or "approved" or "sent"))
                    throw new KiteException(ErrorCodes.PurchOrderState, $"Order {o.Number} is {o.Status.Replace('_', ' ')}; goods have come in against it, so it is closed instead.", 409);
                await t.ExecuteAsync("""
                    UPDATE purch.purchase_order SET status = 'cancelled', closed_at = now(), closed_by = @uid, close_reason = @reason, updated_by = @uid WHERE id = @id
                    """, new { id, uid = http.User.UserIdOf(), reason = req.Reason.Trim() });
                await t.AuditAsync(new AuditEntry("purch.order_cancelled", "purchase_order", id, After: Json.Obj(new { o.Number }), Reason: req.Reason.Trim(), BranchId: o.BranchId));
                return await PurchCore.OrderAsync(t, id, http.User.UserIdOf(), http.User);
            }, ct))).RequirePermission(Permissions.PurchOrder).RequireModule("purch");

        g.MapPost("/orders/{id:guid}/close", async (Guid id, OrderReasonRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                if (Clean(req.Reason) is null) throw new KiteException(ErrorCodes.Validation, "Say why the rest will not come.", 400, "reason");
                await PurchCore.LockOrderAsync(t, id);
                var o = await PurchCore.LoadOrderAsync(t, id, forUpdate: true);
                http.User.Demand(Permissions.PurchOrder, o.BranchId);
                if (o.Status is not ("partly_received" or "received" or "approved" or "sent"))
                    throw new KiteException(ErrorCodes.PurchOrderState, $"Order {o.Number} is {o.Status.Replace('_', ' ')}.", 409);
                await t.ExecuteAsync("""
                    UPDATE purch.purchase_order SET status = 'closed', closed_at = now(), closed_by = @uid, close_reason = @reason, updated_by = @uid WHERE id = @id
                    """, new { id, uid = http.User.UserIdOf(), reason = req.Reason.Trim() });
                await t.AuditAsync(new AuditEntry("purch.order_closed", "purchase_order", id, After: Json.Obj(new { o.Number }), Reason: req.Reason.Trim(), BranchId: o.BranchId));
                return await PurchCore.OrderAsync(t, id, http.User.UserIdOf(), http.User);
            }, ct))).RequirePermission(Permissions.PurchOrder).RequireModule("purch");

        g.MapGet("/orders/{id:guid}/pdf", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            var (pdf, number) = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var o = await PurchCore.LoadOrderAsync(t, id);
                if (o.Status is "draft" or "cancelled") throw new KiteException(ErrorCodes.PurchOrderState, $"Order {o.Number} is {o.Status}; only an approved order goes to the supplier.", 409);
                var biz = await BusinessInfo.LoadAsync(t);
                var data = await t.WithAllBranchesAsync(async () => (
                    Head: await t.QuerySingleOrDefaultAsync<(string Number, DateTime Date, DateTime? Expected, string SupplierEn, string SupplierAr, string? SupplierVat, string? SupplierPhone,
                        decimal Net, decimal Tax, decimal Gross, string? Note, string BranchEn)>("""
                        SELECT o.number, o.order_date, o.expected_date, s.name_en, s.name_ar, s.vat_number, s.phone_e164, o.net_total, o.tax_total, o.gross_total, o.note, b.name_en
                          FROM purch.purchase_order o JOIN purch.supplier s ON s.id = o.supplier_id JOIN core.branch b ON b.id = o.branch_id WHERE o.id = @id
                        """, new { id }),
                    Lines: (await t.QueryAsync<(short No, string Sku, string Description, string Deliver, decimal Qty, decimal Price, decimal Net)>("""
                        SELECT l.line_no, v.sku, l.description_en, b.name_en, l.qty, l.unit_price, l.net_amount
                          FROM purch.po_line l JOIN catalog.variant v ON v.id = l.variant_id JOIN core.branch b ON b.id = l.deliver_branch_id WHERE l.po_id = @id ORDER BY l.line_no
                        """, new { id })).ToList()));
                return (OrderPdf(biz, data.Head, data.Lines), o.Number);
            }, ct);
            return Results.File(pdf, "application/pdf", $"{number}.pdf");
        }).RequirePermission(Permissions.PurchView).RequireModule("purch");

        g.MapGet("/price-history/{variantId:guid}", async (Guid variantId, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => PurchCore.PriceHistoryAsync(t, variantId), ct)))
            .RequirePermission(Permissions.PurchView).RequireModule("purch");

        // ------------------------------------------------------------ goods receipts (16.2, 16.3)
        g.MapGet("/receipts", async (Guid? supplierId, Guid? branchId, string? q, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.RowsAsync("""
                SELECT r.id, r.number, r.receipt_date::text AS "receiptDate", b.code AS "branchCode", s.name_en AS "supplierNameEn", s.name_ar AS "supplierNameAr",
                       o.number AS "poNumber", r.goods_value AS "goodsValue", r.charges_value AS "chargesValue", r.delivery_note AS "deliveryNote",
                       (SELECT coalesce(sum(g.amount), 0) FROM purch.grni_entry g JOIN purch.receipt_line l ON g.source_type = 'receipt_line' AND l.id = g.source_id
                         WHERE l.receipt_id = r.id) AS "openValue"
                  FROM purch.goods_receipt r JOIN core.branch b ON b.id = r.branch_id JOIN purch.supplier s ON s.id = r.supplier_id LEFT JOIN purch.purchase_order o ON o.id = r.po_id
                 WHERE (@supplierId::uuid IS NULL OR r.supplier_id = @supplierId) AND (@branchId::uuid IS NULL OR r.branch_id = @branchId)
                   AND (@q::text IS NULL OR r.number ILIKE '%' || @q || '%' OR s.name_en ILIKE '%' || @q || '%' OR coalesce(r.delivery_note, '') ILIKE '%' || @q || '%')
                 ORDER BY r.receipt_date DESC, r.created_at DESC LIMIT 300
                """, new { supplierId, branchId, q = Clean(q) }), ct))).RequirePermission(Permissions.PurchView).RequireModule("purch");

        g.MapGet("/receipts/{id:guid}", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                if (!await t.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM purch.goods_receipt WHERE id = @id)", new { id }))
                    throw new KiteException(ErrorCodes.NotFound, "That receipt was not found in your branches.", 404);
                return await PurchCore.ReceiptAsync(t, id);
            }, ct))).RequirePermission(Permissions.PurchView).RequireModule("purch");

        g.MapPost("/receipts", async (ReceiptInput req, HttpContext http, KiteDb db, CentralClient central, LicenceService licence, CancellationToken ct) =>
        {
            await licence.RequireTransactAsync(ct);
            http.User.Demand(Permissions.PurchReceive, req.BranchId);
            if (await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => PurchCore.ReceiptDoneAsync(t, req.Id), ct) is { } done) return Results.Ok(done);
            // A branch server asks head office first whether any scanned IMEI is already somewhere else (11.4).
            var offline = false;
            var lines = req.Lines.Where(l => l.Units is { Count: > 0 }).Select(l => new AdjustmentLineInput(l.VariantId ?? Guid.Empty, l.Qty, "purchase", null, l.Units)).ToList();
            if (central.IsBranch && lines.Count > 0)
            {
                var ids = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => DeviceIdentity.IdentifiersOfAsync(t, lines), ct);
                var answer = await central.CheckAsync(ids, null, ct);
                if (answer is null) offline = true;
                else DeviceIdentity.RefuseHeld(answer);
            }
            var (result, units, parts) = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => PurchCore.ReceiveAsync(t, req, http.User.UserIdOf(), http.User), ct);
            if (central.IsBranch && units.Count > 0 && !offline)
            {
                try { await central.PushAsync(units, ct); }
                catch (Exception e) when (e is not OperationCanceledException)
                { http.RequestServices.GetRequiredService<ILogger<CentralClient>>().LogWarning("Device push failed: {Error}", e.Message); }
            }
            // Parts that came in go straight to the repair jobs waiting for them (13.5).
            if (parts)
            {
                try { await RepairParts.FillAsync(db, ct); }
                catch (Exception e) when (e is not OperationCanceledException)
                { http.RequestServices.GetRequiredService<ILogger<KiteDb>>().LogWarning("Waiting repair jobs not filled: {Error}", e.Message); }
            }
            return Results.Ok(result);
        }).RequirePermission(Permissions.PurchReceive).RequireModule("purch");

        // ------------------------------------------------------------ supplier invoices (16.2)
        g.MapGet("/suppliers/{id:guid}/open", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => PurchCore.OpenForSupplierAsync(t, id), ct)))
            .RequirePermission(Permissions.PurchInvoice).RequireModule("purch");

        g.MapGet("/suppliers/{id:guid}/open-items", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => PurchCore.OpenItemsAsync(t, id), ct)))
            .RequirePermission(Permissions.PurchView).RequireModule("purch");

        g.MapGet("/outside-work", async (HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.WithAllBranchesAsync(() => PurchCore.OpenOutsideWorkAsync(t)), ct)))
            .RequirePermission(Permissions.PurchInvoice).RequireModule("purch");

        g.MapGet("/invoices", async (string? status, Guid? supplierId, string? q, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.RowsAsync("""
                SELECT i.id, i.number, i.status, i.supplier_invoice_no AS "supplierInvoiceNo", i.invoice_date::text AS "invoiceDate", i.due_date::text AS "dueDate",
                       b.code AS "branchCode", s.name_en AS "supplierNameEn", s.name_ar AS "supplierNameAr", i.gross_total AS "grossTotal", i.variance_total AS "varianceTotal",
                       coalesce((SELECT sum(a.amount) FROM purch.supplier_allocation a WHERE a.debit_entry_id = i.ledger_entry_id), 0) AS paid
                  FROM purch.purchase_invoice i JOIN core.branch b ON b.id = i.branch_id JOIN purch.supplier s ON s.id = i.supplier_id
                 WHERE (@status::text IS NULL OR i.status = @status OR (@status = 'open' AND i.status IN ('draft','matched','needs_approval')))
                   AND (@supplierId::uuid IS NULL OR i.supplier_id = @supplierId)
                   AND (@q::text IS NULL OR i.number ILIKE '%' || @q || '%' OR i.supplier_invoice_no ILIKE '%' || @q || '%' OR s.name_en ILIKE '%' || @q || '%')
                 ORDER BY i.invoice_date DESC, i.created_at DESC LIMIT 300
                """, new { status = Clean(status), supplierId, q = Clean(q) }), ct))).RequirePermission(Permissions.PurchView).RequireModule("purch");

        g.MapPost("/invoices", async (InvoiceInput req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => PurchCore.SaveInvoiceAsync(t, req, http.User.UserIdOf(), http.User, update: false), ct)))
            .RequirePermission(Permissions.PurchInvoice).RequireModule("purch");

        g.MapGet("/invoices/{id:guid}", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                if (!await t.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM purch.purchase_invoice WHERE id = @id)", new { id }))
                    throw new KiteException(ErrorCodes.NotFound, "That invoice was not found in your branches.", 404);
                return await PurchCore.InvoiceAsync(t, id);
            }, ct))).RequirePermission(Permissions.PurchView).RequireModule("purch");

        g.MapPut("/invoices/{id:guid}", async (Guid id, InvoiceInput req, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            if (req.Id != id) throw new KiteException(ErrorCodes.Validation, "The invoice in the address and in the body differ.", 400, "id");
            return Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => PurchCore.SaveInvoiceAsync(t, req, http.User.UserIdOf(), http.User, update: true), ct));
        }).RequirePermission(Permissions.PurchInvoice).RequireModule("purch");

        g.MapPost("/invoices/{id:guid}/post", async (Guid id, InvoicePostRequest req, HttpContext http, KiteDb db, LicenceService licence, CancellationToken ct) =>
        {
            await licence.RequireTransactAsync(ct);
            return Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => PurchCore.PostInvoiceAsync(t, id, req.OverrideReason, http.User.UserIdOf(), http.User), ct));
        }).RequirePermission(Permissions.PurchInvoice).RequireModule("purch");

        g.MapPost("/invoices/{id:guid}/cancel", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t => { await PurchCore.CancelInvoiceAsync(t, id, http.User.UserIdOf(), http.User); return 0; }, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.PurchInvoice).RequireModule("purch");

        // ------------------------------------------------------------ returns (debit notes) and payments (16.1, 16.2)
        g.MapGet("/returns", async (Guid? supplierId, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.RowsAsync("""
                SELECT r.id, r.number, r.return_date::text AS "returnDate", b.code AS "branchCode", s.name_en AS "supplierNameEn", s.name_ar AS "supplierNameAr", r.reason,
                       r.net_total AS "netTotal", r.tax_total AS "taxTotal", r.stock_value AS "stockValue"
                  FROM purch.supplier_return r JOIN core.branch b ON b.id = r.branch_id JOIN purch.supplier s ON s.id = r.supplier_id
                 WHERE @supplierId::uuid IS NULL OR r.supplier_id = @supplierId ORDER BY r.return_date DESC, r.created_at DESC LIMIT 300
                """, new { supplierId }), ct))).RequirePermission(Permissions.PurchView).RequireModule("purch");

        g.MapGet("/returns/{id:guid}", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                if (!await t.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM purch.supplier_return WHERE id = @id)", new { id }))
                    throw new KiteException(ErrorCodes.NotFound, "That return was not found in your branches.", 404);
                return await PurchCore.ReturnDetailAsync(t, id);
            }, ct))).RequirePermission(Permissions.PurchView).RequireModule("purch");

        g.MapPost("/returns", async (ReturnInput req, HttpContext http, KiteDb db, CentralClient central, LicenceService licence, CancellationToken ct) =>
        {
            await licence.RequireTransactAsync(ct);
            var result = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => PurchCore.ReturnAsync(t, req, http.User.UserIdOf(), http.User), ct);
            var units = req.Lines.SelectMany(l => l.DeviceUnitIds ?? []).ToList();
            if (central.IsBranch && units.Count > 0)
            {
                try { await central.PushAsync(units, ct); }
                catch (Exception e) when (e is not OperationCanceledException)
                { http.RequestServices.GetRequiredService<ILogger<CentralClient>>().LogWarning("Device push failed: {Error}", e.Message); }
            }
            return Results.Ok(result);
        }).RequirePermission(Permissions.PurchReturn).RequireModule("purch");

        g.MapGet("/payments", async (Guid? supplierId, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.RowsAsync("""
                SELECT p.id, p.number, p.payment_date::text AS "paymentDate", b.code AS "branchCode", s.name_en AS "supplierNameEn", s.name_ar AS "supplierNameAr",
                       p.method, p.reference, p.amount
                  FROM purch.supplier_payment p JOIN core.branch b ON b.id = p.branch_id JOIN purch.supplier s ON s.id = p.supplier_id
                 WHERE @supplierId::uuid IS NULL OR p.supplier_id = @supplierId ORDER BY p.payment_date DESC, p.created_at DESC LIMIT 300
                """, new { supplierId }), ct))).RequirePermission(Permissions.PurchView).RequireModule("purch");

        g.MapGet("/payments/{id:guid}", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                if (!await t.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM purch.supplier_payment WHERE id = @id)", new { id }))
                    throw new KiteException(ErrorCodes.NotFound, "That payment was not found in your branches.", 404);
                return await PurchCore.PaymentAsync(t, id);
            }, ct))).RequirePermission(Permissions.PurchView).RequireModule("purch");

        g.MapPost("/payments", async (SupplierPaymentInput req, HttpContext http, KiteDb db, LicenceService licence, CancellationToken ct) =>
        {
            await licence.RequireTransactAsync(ct);
            return Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => PurchCore.PayAsync(t, req, http.User.UserIdOf(), http.User), ct));
        }).RequirePermission(Permissions.PurchPay).RequireModule("purch");
    }

    /// <summary>The purchase order the supplier receives (16.2), English with the business's Arabic name.</summary>
    private static byte[] OrderPdf(BusinessInfo biz,
        (string Number, DateTime Date, DateTime? Expected, string SupplierEn, string SupplierAr, string? SupplierVat, string? SupplierPhone, decimal Net, decimal Tax, decimal Gross,
         string? Note, string BranchEn) h,
        IReadOnlyList<(short No, string Sku, string Description, string Deliver, decimal Qty, decimal Price, decimal Net)> lines) =>
        Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(14, Unit.Millimetre);
            page.DefaultTextStyle(x => x.FontSize(9).FontFamily(Fonts));
            page.Header().Row(r =>
            {
                r.RelativeItem().Column(c =>
                {
                    c.Item().Text(biz.SellerNameEn).Bold().FontSize(13);
                    c.Item().ContentFromRightToLeft().Text(biz.SellerNameAr).Bold();
                    if (biz.AddressEn is { } a) c.Item().Text(a);
                    if (biz.VatNumber is { } v) c.Item().Text($"VAT {v}");
                    if (biz.Phone is { } p) c.Item().Text(p);
                });
                r.RelativeItem().AlignRight().Column(c =>
                {
                    c.Item().AlignRight().Text("Purchase order / أمر شراء").Bold().FontSize(13);
                    c.Item().AlignRight().Text(h.Number).Bold();
                    c.Item().AlignRight().Text($"Date: {h.Date:yyyy-MM-dd}");
                    if (h.Expected is { } e) c.Item().AlignRight().Text($"Expected: {e:yyyy-MM-dd}");
                    c.Item().AlignRight().Text($"Ordered by: {h.BranchEn}");
                });
            });
            page.Content().PaddingTop(10).Column(c =>
            {
                c.Item().Text("Supplier / المورد").Bold();
                c.Item().Text(h.SupplierEn);
                c.Item().ContentFromRightToLeft().Text(h.SupplierAr);
                if (h.SupplierVat is { } sv) c.Item().Text($"VAT {sv}");
                if (h.SupplierPhone is { } sp) c.Item().Text(sp);
                c.Item().PaddingTop(10).Table(tb =>
                {
                    tb.ColumnsDefinition(cd =>
                    {
                        cd.ConstantColumn(22); cd.RelativeColumn(2); cd.RelativeColumn(5); cd.RelativeColumn(2); cd.ConstantColumn(40); cd.ConstantColumn(60); cd.ConstantColumn(66);
                    });
                    tb.Header(hd =>
                    {
                        foreach (var t in new[] { "#", "SKU", "Item", "Deliver to", "Qty", "Price", "Amount" })
                            hd.Cell().BorderBottom(1).PaddingVertical(3).Text(t).Bold();
                    });
                    foreach (var l in lines)
                    {
                        tb.Cell().PaddingVertical(2).Text(l.No.ToString(CultureInfo.InvariantCulture));
                        tb.Cell().PaddingVertical(2).Text(l.Sku);
                        tb.Cell().PaddingVertical(2).Text(l.Description);
                        tb.Cell().PaddingVertical(2).Text(l.Deliver);
                        tb.Cell().PaddingVertical(2).AlignRight().Text(l.Qty.ToString("0.###", CultureInfo.InvariantCulture));
                        tb.Cell().PaddingVertical(2).AlignRight().Text(M(l.Price));
                        tb.Cell().PaddingVertical(2).AlignRight().Text(M(l.Net));
                    }
                });
                c.Item().PaddingTop(8).AlignRight().Text($"Net / الصافي: OMR {M(h.Net)}");
                c.Item().AlignRight().Text($"VAT / الضريبة: OMR {M(h.Tax)}");
                c.Item().AlignRight().Text($"Total / الإجمالي: OMR {M(h.Gross)}").Bold();
                if (h.Note is { } n) c.Item().PaddingTop(10).Text($"Note: {n}");
                c.Item().PaddingTop(16).Text("Please quote the order number on your delivery note and invoice.").FontSize(8);
            });
            page.Footer().AlignCenter().Text(x => { x.CurrentPageNumber(); x.Span(" / "); x.TotalPages(); });
        })).GeneratePdf();
}
