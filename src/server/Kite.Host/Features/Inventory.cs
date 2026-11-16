using Kite.Accounting;
using Kite.Host.Infrastructure;
using Kite.Persistence;
using Kite.Platform;

namespace Kite.Host.Features;

public sealed record AdjustmentLineInput(Guid VariantId, decimal Qty, string ReasonCode, decimal? UnitCost = null);
public sealed record AdjustmentRequest(Guid BranchId, string Kind, List<AdjustmentLineInput> Lines, Guid? WarehouseId = null, DateOnly? Date = null, string? Note = null);
public sealed record CountRequest(Guid BranchId, Guid? WarehouseId = null, string? Note = null);
public sealed record CountLineInput(Guid VariantId, decimal CountedQty);
public sealed record ResolveRequest(string Note);

public sealed record BalanceRow(Guid VariantId, string Sku, string DescriptionEn, string DescriptionAr, string ProductType, Guid BranchId, string BranchCode,
    Guid WarehouseId, string WarehouseCode, decimal Qty, decimal Value, decimal AvgCost, decimal? ReorderLevel, DateTime? LastMovementAt);

/// <summary>Inventory (9.3, 16.3): balances, the stock ledger, adjustments and opening stock, counts, exceptions and the consistency check.</summary>
public static class InventoryEndpoints
{
    private static readonly string[] Reasons = ["opening", "damaged", "lost", "found", "expired", "correction", "other"];

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/inventory");

        g.MapGet("/balances", async (Guid? branchId, Guid? warehouseId, string? q, bool? lowOnly, int? limit, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            http.User.Demand(Permissions.InvView, branchId);
            return Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.QueryAsync<BalanceRow>($"""
                SELECT v.id AS VariantId, v.sku, {ItemPricing.DescriptionEn} AS DescriptionEn, {ItemPricing.DescriptionAr} AS DescriptionAr,
                       p.product_type AS ProductType, sb.branch_id AS BranchId, b.code AS BranchCode, sb.warehouse_id AS WarehouseId, w.code AS WarehouseCode,
                       sb.qty, sb.value, sb.avg_cost AS AvgCost, bv.reorder_level AS ReorderLevel, sb.last_movement_at AS LastMovementAt
                  FROM inv.stock_balance sb
                  JOIN catalog.variant v ON v.id = sb.variant_id JOIN catalog.product p ON p.id = v.product_id
                  JOIN core.branch b ON b.id = sb.branch_id JOIN core.warehouse w ON w.id = sb.warehouse_id
                  LEFT JOIN catalog.branch_variant bv ON bv.branch_id = sb.branch_id AND bv.variant_id = sb.variant_id
                 WHERE (@branchId::uuid IS NULL OR sb.branch_id = @branchId) AND (@warehouseId::uuid IS NULL OR sb.warehouse_id = @warehouseId)
                   AND (@q::text IS NULL OR p.name_en ILIKE '%' || @q || '%' OR p.name_ar ILIKE '%' || @q || '%' OR upper(v.sku) LIKE '%' || upper(@q) || '%')
                   AND (NOT @low OR sb.qty <= coalesce(bv.reorder_level, 0))
                 ORDER BY b.code, lower(p.name_en), v.sku LIMIT @limit
                """, new { branchId, warehouseId, q = string.IsNullOrWhiteSpace(q) ? null : q.Trim(), low = lowOnly ?? false, limit = Math.Clamp(limit ?? 200, 1, 5000) }), ct));
        }).RequirePermission(Permissions.InvView);

        g.MapGet("/movements", async (Guid variantId, Guid? branchId, int? limit, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            http.User.Demand(Permissions.InvView, branchId);
            return Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.RowsAsync("""
                SELECT m.id, m.movement_at AS "movementAt", m.movement_type AS "movementType", b.code AS "branchCode", w.code AS "warehouseCode",
                       m.qty, m.unit_cost AS "unitCost", m.value, m.qty_after AS "qtyAfter", m.value_after AS "valueAfter", m.source_doc_type AS "sourceDocType",
                       m.source_doc_id AS "sourceDocId", coalesce(s.number, a.number, c.number) AS "sourceNumber", m.reason
                  FROM inv.stock_movement m JOIN core.branch b ON b.id = m.branch_id JOIN core.warehouse w ON w.id = m.warehouse_id
                  LEFT JOIN sales.sale s ON m.source_doc_type = 'sale' AND s.id = m.source_doc_id
                  LEFT JOIN inv.adjustment a ON m.source_doc_type = 'adjustment' AND a.id = m.source_doc_id
                  LEFT JOIN inv.stock_count c ON m.source_doc_type = 'stock_count' AND c.id = m.source_doc_id
                 WHERE m.variant_id = @variantId AND (@branchId::uuid IS NULL OR m.branch_id = @branchId)
                 ORDER BY m.movement_at DESC, m.id DESC LIMIT @limit
                """, new { variantId, branchId, limit = Math.Clamp(limit ?? 200, 1, 2000) }), ct));
        }).RequirePermission(Permissions.InvView);

        g.MapPost("/adjustments", async (AdjustmentRequest req, HttpContext http, KiteDb db, ServerContext server, LicenceService licence, CancellationToken ct) =>
        {
            http.User.Demand(Permissions.InvAdjust, req.BranchId);
            await licence.RequireTransactAsync(ct);
            if (req.Kind is not ("opening" or "adjustment")) throw new KiteException(ErrorCodes.Validation, "Kind must be opening or adjustment.", 400, "kind");
            if (req.Lines is null || req.Lines.Count == 0) throw new KiteException(ErrorCodes.Validation, "Add at least one line.", 400, "lines");
            if (req.Lines.Select(l => l.VariantId).Distinct().Count() != req.Lines.Count) throw new KiteException(ErrorCodes.Validation, "An item appears twice.", 400, "lines");
            for (var i = 0; i < req.Lines.Count; i++)
            {
                var l = req.Lines[i];
                if (l.Qty == 0 || l.Qty != decimal.Round(l.Qty, 3)) throw new KiteException(ErrorCodes.Validation, "Quantity cannot be zero (at most 3 decimals).", 400, $"lines[{i}].qty");
                if (!Reasons.Contains(l.ReasonCode)) throw new KiteException(ErrorCodes.Validation, "Choose a reason.", 400, $"lines[{i}].reasonCode");
                if (l.UnitCost is < 0) throw new KiteException(ErrorCodes.Validation, "Cost cannot be negative.", 400, $"lines[{i}].unitCost");
                if (req.Kind == "opening" && (l.Qty < 0 || l.UnitCost is null || l.ReasonCode != "opening"))
                    throw new KiteException(ErrorCodes.Validation, "Opening stock lines need a positive quantity, a unit cost and the reason opening.", 400, $"lines[{i}]");
                if (req.Kind == "adjustment" && l.ReasonCode == "opening") throw new KiteException(ErrorCodes.Validation, "Use an opening-stock document for opening stock.", 400, $"lines[{i}].reasonCode");
                if (l.Qty < 0 && l.UnitCost is not null) throw new KiteException(ErrorCodes.Validation, "Stock going out leaves at the average cost; leave the cost empty.", 400, $"lines[{i}].unitCost");
            }
            var result = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var biz = await BusinessInfo.LoadAsync(t);
                var date = req.Date ?? BusinessDay.Today(biz.TimeZone, t.Clock);
                var warehouse = await WarehouseAsync(t, req.BranchId, req.WarehouseId);
                var number = await DocNumbers.NextAsync(t, biz.Id, req.BranchId, "stock_adjustment", await FiscalPeriods.FiscalYearOfAsync(t, biz.Id, date), "ADJ");
                var id = t.NewId();
                var uid = http.User.UserIdOf();
                await t.ExecuteAsync("""
                    INSERT INTO inv.adjustment (id, business_id, branch_id, warehouse_id, number, kind, adjustment_date, note, posted_by, origin_server_id)
                    VALUES (@id, @b, @BranchId, @warehouse, @number, @Kind, @d::date, @Note, @uid, (SELECT id FROM core.server WHERE is_self))
                    """, new { id, b = biz.Id, req.BranchId, warehouse, number, req.Kind, d = date.ToDateTime(TimeOnly.MinValue), req.Note, uid });
                var types = (await t.QueryAsync<(Guid Id, string Type)>("SELECT v.id, p.product_type FROM catalog.variant v JOIN catalog.product p ON p.id = v.product_id WHERE v.id = ANY(@ids)",
                    new { ids = req.Lines.Select(l => l.VariantId).ToArray() })).ToDictionary(x => x.Id, x => x.Type);
                var facts = new PostingFacts();
                short no = 0;
                foreach (var l in req.Lines.OrderBy(l => l.VariantId))
                {
                    if (!types.TryGetValue(l.VariantId, out var type)) throw new KiteException(ErrorCodes.Validation, "An item is not in the catalog.", 400, "lines");
                    var lineId = t.NewId();
                    await t.ExecuteAsync("""
                        INSERT INTO inv.adjustment_line (id, adjustment_id, line_no, variant_id, qty, unit_cost, reason_code)
                        VALUES (@lineId, @id, @no, @VariantId, @Qty, @UnitCost, @ReasonCode)
                        """, new { lineId, id, no = ++no, l.VariantId, l.Qty, l.UnitCost, l.ReasonCode });
                    var value = await MoveAsync(t, biz.Id, req.BranchId, warehouse, l.VariantId, req.Kind == "opening" ? "opening" : "adjustment", l.Qty, l.UnitCost,
                        "adjustment", id, lineId, l.ReasonCode, uid);
                    facts.Add(req.Kind == "opening" ? "opening_by_product_type" : value >= 0 ? "gain_by_product_type" : "loss_by_product_type", type, Math.Abs(value));
                }
                await Posting.PostAsync(t, biz.Id, req.BranchId, PostingRuleTemplates.StockAdjustment, facts, date, "stock_adjustment", id,
                    $"{(req.Kind == "opening" ? "Opening stock" : "Stock adjustment")} {number}");
                await t.AuditAsync(new AuditEntry("inv.adjustment_posted", "stock_adjustment", id, After: Json.Obj(new { number, req.Kind, lines = req.Lines.Count, req.Note }), BranchId: req.BranchId));
                await t.PublishAsync("inv.adjustment_posted", "stock_adjustment", id, 1, Json.Obj(new { number, req.Kind, req.Lines }), req.BranchId);
                return new { id, number };
            }, ct);
            return Results.Created($"/api/v1/inventory/adjustments/{result.id}", result);
        }).RequirePermission(Permissions.InvAdjust);

        g.MapGet("/adjustments", async (Guid? branchId, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.RowsAsync("""
                SELECT a.id, a.number, a.kind, a.adjustment_date AS "date", b.code AS "branchCode", a.note, a.posted_at AS "postedAt",
                       u.display_name_en AS "postedBy", (SELECT count(*) FROM inv.adjustment_line l WHERE l.adjustment_id = a.id) AS "lines",
                       (SELECT coalesce(sum(m.value), 0) FROM inv.stock_movement m WHERE m.source_doc_type = 'adjustment' AND m.source_doc_id = a.id) AS "value"
                  FROM inv.adjustment a JOIN core.branch b ON b.id = a.branch_id JOIN iam."user" u ON u.id = a.posted_by
                 WHERE (@branchId::uuid IS NULL OR a.branch_id = @branchId) ORDER BY a.posted_at DESC LIMIT 200
                """, new { branchId }), ct)))
            .RequirePermission(Permissions.InvView);

        // ---------------------------------------------------------- counts
        g.MapPost("/counts", async (CountRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            http.User.Demand(Permissions.InvCount, req.BranchId);
            var result = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var biz = await BusinessInfo.LoadAsync(t);
                var warehouse = await WarehouseAsync(t, req.BranchId, req.WarehouseId);
                var number = await DocNumbers.NextAsync(t, biz.Id, req.BranchId, "stock_count",
                    await FiscalPeriods.FiscalYearOfAsync(t, biz.Id, BusinessDay.Today(biz.TimeZone, t.Clock)), "CNT");
                var id = t.NewId();
                await t.ExecuteAsync("""
                    INSERT INTO inv.stock_count (id, business_id, branch_id, warehouse_id, number, note, started_by, origin_server_id)
                    VALUES (@id, @b, @BranchId, @warehouse, @number, @Note, @uid, (SELECT id FROM core.server WHERE is_self))
                    """, new { id, b = biz.Id, req.BranchId, warehouse, number, req.Note, uid = http.User.UserIdOf() });
                await t.AuditAsync(new AuditEntry("inv.count_started", "stock_count", id, After: Json.Obj(new { number }), BranchId: req.BranchId));
                return new { id, number };
            }, ct);
            return Results.Created($"/api/v1/inventory/counts/{result.id}", result);
        }).RequirePermission(Permissions.InvCount);

        g.MapGet("/counts", async (Guid? branchId, string? status, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.RowsAsync("""
                SELECT c.id, c.number, c.status, b.code AS "branchCode", w.code AS "warehouseCode", c.started_at AS "startedAt", c.posted_at AS "postedAt",
                       (SELECT count(*) FROM inv.stock_count_line l WHERE l.stock_count_id = c.id) AS "lines"
                  FROM inv.stock_count c JOIN core.branch b ON b.id = c.branch_id JOIN core.warehouse w ON w.id = c.warehouse_id
                 WHERE (@branchId::uuid IS NULL OR c.branch_id = @branchId) AND (@status::text IS NULL OR c.status = @status)
                 ORDER BY c.started_at DESC LIMIT 200
                """, new { branchId, status }), ct)))
            .RequirePermission(Permissions.InvView);

        g.MapGet("/counts/{id:guid}", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var c = await t.QuerySingleOrDefaultAsync<(Guid Id, string Number, string Status, Guid BranchId, Guid WarehouseId)>(
                    "SELECT id, number, status, branch_id, warehouse_id FROM inv.stock_count WHERE id = @id", new { id });
                if (c.Id == Guid.Empty) throw new KiteException(ErrorCodes.NotFound, "Count not found in your branches.", 404);
                var lines = await t.RowsAsync($"""
                    SELECT l.variant_id AS "variantId", v.sku, {ItemPricing.DescriptionEn} AS "descriptionEn", l.counted_qty AS "countedQty",
                           coalesce(l.system_qty, sb.qty, 0) AS "systemQty", coalesce(l.difference, l.counted_qty - coalesce(sb.qty, 0)) AS "difference"
                      FROM inv.stock_count_line l JOIN catalog.variant v ON v.id = l.variant_id JOIN catalog.product p ON p.id = v.product_id
                      LEFT JOIN inv.stock_balance sb ON sb.variant_id = l.variant_id AND sb.warehouse_id = @w
                     WHERE l.stock_count_id = @id ORDER BY v.sku
                    """, new { id, w = c.WarehouseId });
                return new { c.Id, c.Number, c.Status, c.BranchId, lines };
            }, ct))).RequirePermission(Permissions.InvView);

        g.MapPut("/counts/{id:guid}/lines", async (Guid id, List<CountLineInput> lines, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var c = await OpenCountAsync(t, id);
                http.User.Demand(Permissions.InvCount, c.BranchId);
                foreach (var l in lines)
                {
                    if (l.CountedQty < 0 || l.CountedQty != decimal.Round(l.CountedQty, 3)) throw new KiteException(ErrorCodes.Validation, "Counted quantity is zero or more.", 400, "countedQty");
                    await t.ExecuteAsync("""
                        INSERT INTO inv.stock_count_line (id, stock_count_id, variant_id, counted_qty, counted_by)
                        VALUES (@lid, @id, @VariantId, @CountedQty, @uid)
                        ON CONFLICT (stock_count_id, variant_id) DO UPDATE SET counted_qty = EXCLUDED.counted_qty, counted_by = EXCLUDED.counted_by, counted_at = now()
                        """, new { lid = t.NewId(), id, l.VariantId, l.CountedQty, uid = http.User.UserIdOf() });
                }
            }, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.InvCount);

        // Posting writes the differences between counted and system stock at that moment to the ledger.
        g.MapPost("/counts/{id:guid}/post", async (Guid id, HttpContext http, KiteDb db, LicenceService licence, CancellationToken ct) =>
        {
            await licence.RequireTransactAsync(ct);
            var result = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var c = await OpenCountAsync(t, id);
                http.User.Demand(Permissions.InvCount, c.BranchId);
                var biz = await BusinessInfo.LoadAsync(t);
                var uid = http.User.UserIdOf();
                var lines = (await t.QueryAsync<(Guid LineId, Guid VariantId, decimal Counted, string Type)>("""
                    SELECT l.id, l.variant_id, l.counted_qty, p.product_type FROM inv.stock_count_line l
                      JOIN catalog.variant v ON v.id = l.variant_id JOIN catalog.product p ON p.id = v.product_id
                     WHERE l.stock_count_id = @id ORDER BY l.variant_id
                    """, new { id })).ToArray();
                if (lines.Length == 0) throw new KiteException(ErrorCodes.Validation, "Count at least one item before posting.", 400);
                var facts = new PostingFacts();
                var changed = 0;
                foreach (var l in lines)
                {
                    var system = await t.ExecuteScalarAsync<decimal>("SELECT inv.lock_balance(@w, @v)", new { w = c.WarehouseId, v = l.VariantId });
                    var diff = l.Counted - system;
                    await t.ExecuteAsync("UPDATE inv.stock_count_line SET system_qty = @system, difference = @diff WHERE id = @LineId", new { system, diff, l.LineId });
                    if (diff == 0) continue;
                    changed++;
                    var value = await MoveAsync(t, biz.Id, c.BranchId, c.WarehouseId, l.VariantId, "count", diff, null, "stock_count", id, l.LineId, "count", uid);
                    facts.Add(value >= 0 ? "gain_by_product_type" : "loss_by_product_type", l.Type, Math.Abs(value));
                }
                await t.ExecuteAsync("UPDATE inv.stock_count SET status = 'posted', posted_at = now(), posted_by = @uid WHERE id = @id", new { id, uid });
                await Posting.PostAsync(t, biz.Id, c.BranchId, PostingRuleTemplates.StockAdjustment, facts, BusinessDay.Today(biz.TimeZone, t.Clock), "stock_count", id, $"Stock count {c.Number}");
                await t.AuditAsync(new AuditEntry("inv.count_posted", "stock_count", id, After: Json.Obj(new { c.Number, lines = lines.Length, changed }), BranchId: c.BranchId));
                await t.PublishAsync("inv.count_posted", "stock_count", id, 2, Json.Obj(new { c.Number, changed }), c.BranchId);
                return new { id, c.Number, lines = lines.Length, changed };
            }, ct);
            return Results.Ok(result);
        }).RequirePermission(Permissions.InvCount);

        g.MapPost("/counts/{id:guid}/cancel", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var c = await OpenCountAsync(t, id);
                http.User.Demand(Permissions.InvCount, c.BranchId);
                await t.ExecuteAsync("UPDATE inv.stock_count SET status = 'cancelled' WHERE id = @id", new { id });
                await t.AuditAsync(new AuditEntry("inv.count_cancelled", "stock_count", id, BranchId: c.BranchId));
            }, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.InvCount);

        // ---------------------------------------------------------- exceptions and the consistency check
        g.MapGet("/exceptions", async (string? status, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.RowsAsync($"""
                SELECT e.id, e.kind, e.status, b.code AS "branchCode", v.sku, {ItemPricing.DescriptionEn} AS "descriptionEn", e.qty_after AS "qtyAfter",
                       e.source_doc_type AS "sourceDocType", s.number AS "sourceNumber", e.created_at AS "createdAt", e.resolution_note AS "resolutionNote"
                  FROM inv.stock_exception e JOIN core.branch b ON b.id = e.branch_id JOIN catalog.variant v ON v.id = e.variant_id
                  JOIN catalog.product p ON p.id = v.product_id LEFT JOIN sales.sale s ON e.source_doc_type = 'sale' AND s.id = e.source_doc_id
                 WHERE (@status::text IS NULL OR e.status = @status) ORDER BY e.created_at DESC LIMIT 500
                """, new { status }), ct)))
            .RequirePermission(Permissions.InvView);

        g.MapPost("/exceptions/{id:guid}/resolve", async (Guid id, ResolveRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Note)) throw new KiteException(ErrorCodes.Validation, "Say how it was resolved.", 400, "note");
            await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var branch = await t.ExecuteScalarAsync<Guid?>("SELECT branch_id FROM inv.stock_exception WHERE id = @id AND status = 'open' FOR UPDATE", new { id })
                    ?? throw new KiteException(ErrorCodes.NotFound, "Open exception not found.", 404);
                http.User.Demand(Permissions.InvAdjust, branch);
                await t.ExecuteAsync("UPDATE inv.stock_exception SET status = 'resolved', resolved_at = now(), resolved_by = @uid, resolution_note = @n WHERE id = @id",
                    new { id, uid = http.User.UserIdOf(), n = req.Note.Trim() });
                await t.AuditAsync(new AuditEntry("inv.exception_resolved", "stock_exception", id, After: Json.Obj(req), BranchId: branch));
            }, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.InvAdjust);

        g.MapGet("/check", async (HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), ConsistencyAsync, ct)))
            .RequirePermission(Permissions.InvView);
    }

    /// <summary>
    /// The 23.2 checks for Phase 4: every stock balance equals its ledger, the inventory accounts equal the stock
    /// valuation per branch, every journal balances, and every sale has its journals and payments.
    /// </summary>
    public static async Task<object> ConsistencyAsync(DbTx t)
    {
        var balances = await t.RowsAsync("""
            SELECT branch_id AS "branchId", warehouse_id AS "warehouseId", variant_id AS "variantId", balance_qty AS "balanceQty", ledger_qty AS "ledgerQty",
                   balance_value AS "balanceValue", ledger_value AS "ledgerValue" FROM inv.check_balances()
            """);
        var valuation = await t.RowsAsync("""
            WITH stock AS (
                SELECT sb.branch_id, CASE p.product_type WHEN 'device' THEN '1400' WHEN 'spare_part' THEN '1430' ELSE '1420' END AS code, sum(sb.value) AS value
                  FROM inv.stock_balance sb JOIN catalog.variant v ON v.id = sb.variant_id JOIN catalog.product p ON p.id = v.product_id GROUP BY 1, 2),
            gl AS (
                SELECT jl.branch_id, a.code, sum(jl.debit - jl.credit) AS value
                  FROM acct.journal_line jl JOIN acct.account a ON a.id = jl.account_id WHERE a.code IN ('1400','1420','1430') GROUP BY 1, 2)
            SELECT coalesce(s.branch_id, g.branch_id) AS "branchId", coalesce(s.code, g.code) AS "account", coalesce(s.value, 0) AS "stockValue", coalesce(g.value, 0) AS "ledgerValue"
              FROM stock s FULL JOIN gl g ON g.branch_id = s.branch_id AND g.code = s.code
             WHERE coalesce(s.value, 0) <> coalesce(g.value, 0)
            """);
        var unbalanced = await t.RowsAsync("""
            SELECT j.number, sum(l.debit) AS debit, sum(l.credit) AS credit FROM acct.journal j JOIN acct.journal_line l ON l.journal_id = j.id
             GROUP BY j.id, j.number HAVING sum(l.debit) <> sum(l.credit)
            """);
        var sales = await t.RowsAsync("""
            SELECT s.number, s.gross_total + s.cash_rounding AS due, coalesce((SELECT sum(abs(p.amount)) FROM sales.payment p WHERE p.sale_id = s.id), 0) AS paid,
                   EXISTS (SELECT 1 FROM acct.journal j WHERE j.source_doc_type = 'sale' AND j.source_doc_id = s.id AND j.posting_kind IN ('pos.sale','pos.credit_note')) AS journal
              FROM sales.sale s
             WHERE s.gross_total + s.cash_rounding <> coalesce((SELECT sum(abs(p.amount)) FROM sales.payment p WHERE p.sale_id = s.id), 0)
                OR (s.gross_total + s.cash_rounding <> 0 AND NOT EXISTS (SELECT 1 FROM acct.journal j WHERE j.source_doc_type = 'sale' AND j.source_doc_id = s.id
                                                                        AND j.posting_kind IN ('pos.sale','pos.credit_note')))
            """);
        var ok = balances.Count == 0 && valuation.Count == 0 && unbalanced.Count == 0 && sales.Count == 0;
        return new { ok, balanceMismatches = balances, valuationMismatches = valuation, unbalancedJournals = unbalanced, salesMismatches = sales };
    }

    private static async Task<Guid> WarehouseAsync(DbTx t, Guid branchId, Guid? warehouseId) =>
        await t.ExecuteScalarAsync<Guid?>("""
            SELECT id FROM core.warehouse WHERE branch_id = @branchId AND status = 'active' AND (@warehouseId::uuid IS NULL OR id = @warehouseId)
             ORDER BY (kind = 'shop') DESC, (code = 'SHOP') DESC, created_at LIMIT 1
            """, new { branchId, warehouseId })
        ?? throw new KiteException(ErrorCodes.Validation, "That warehouse is not in the branch.", 400, "warehouseId");

    private static async Task<(Guid Id, string Number, Guid BranchId, Guid WarehouseId)> OpenCountAsync(DbTx t, Guid id)
    {
        var c = await t.QuerySingleOrDefaultAsync<(Guid Id, string Number, Guid BranchId, Guid WarehouseId, string Status)>(
            "SELECT id, number, branch_id, warehouse_id, status FROM inv.stock_count WHERE id = @id FOR UPDATE", new { id });
        if (c.Id == Guid.Empty) throw new KiteException(ErrorCodes.NotFound, "Count not found in your branches.", 404);
        if (c.Status != "open") throw new KiteException(ErrorCodes.CountClosed, $"Count {c.Number} is {c.Status}.", 409);
        return (c.Id, c.Number, c.BranchId, c.WarehouseId);
    }

    /// <summary>Writes one stock movement; the ledger trigger values it and returns the signed value.</summary>
    public static Task<decimal> MoveAsync(DbTx t, Guid businessId, Guid branchId, Guid warehouseId, Guid variantId, string type, decimal qty, decimal? unitCost,
        string sourceDocType, Guid sourceDocId, Guid? sourceLineId, string? reason, Guid userId) =>
        t.ExecuteScalarAsync<decimal>("""
            INSERT INTO inv.stock_movement (id, business_id, branch_id, warehouse_id, variant_id, movement_type, qty, unit_cost, source_doc_type, source_doc_id,
                                            source_line_id, reason, created_by, origin_server_id)
            VALUES (@id, @businessId, @branchId, @warehouseId, @variantId, @type, @qty, @unitCost, @sourceDocType, @sourceDocId, @sourceLineId, @reason, @userId,
                    (SELECT id FROM core.server WHERE is_self))
            RETURNING value
            """, new { id = t.NewId(), businessId, branchId, warehouseId, variantId, type, qty, unitCost, sourceDocType, sourceDocId, sourceLineId, reason, userId });
}
