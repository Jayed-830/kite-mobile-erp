using System.Globalization;
using Kite.Host.Infrastructure;
using Kite.Persistence;
using Kite.Platform;
using Kite.Sales;

namespace Kite.Host.Features;

public sealed record SupplierMergeRequest(Guid IntoId, string? Reason = null);
public sealed record SupplierOpeningRequest(Guid BranchId, decimal Amount, DateOnly? Date = null, string? Note = null);

/// <summary>Suppliers (blueprint 16.1): business-wide records, provisional ones from branches, opening balances, statements and ageing.</summary>
public static class SupplierEndpoints
{
    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/suppliers");

        g.MapGet("", async (string? q, string? status, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.RowsAsync("""
                SELECT s.id, s.number, s.name_en AS "nameEn", s.name_ar AS "nameAr", s.vat_number AS "vatNumber", s.phone_e164 AS phone, s.terms_days AS "termsDays",
                       s.status, s.merged_into_id AS "mergedIntoId", m.number AS "mergedIntoNumber", b.code AS "originBranchCode", s.version,
                       purch.supplier_balance(s.id) AS balance, s.created_at AS "createdAt"
                  FROM purch.supplier s LEFT JOIN purch.supplier m ON m.id = s.merged_into_id LEFT JOIN core.branch b ON b.id = s.origin_branch_id
                 WHERE (@q::text IS NULL OR s.name_en ILIKE '%' || @q || '%' OR s.name_ar ILIKE '%' || @q || '%' OR s.number ILIKE '%' || @q || '%'
                        OR coalesce(s.vat_number, '') ILIKE '%' || @q || '%')
                   AND (@status::text IS NULL OR s.status = @status)
                 ORDER BY s.status = 'provisional' DESC, s.name_en LIMIT 500
                """, new { q = Clean(q), status = Clean(status) }), ct)))
            .RequirePermission(Permissions.SupplierView).RequireModule("purch");

        g.MapGet("/{id:guid}", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var s = await SupplierCore.LoadAsync(t, id) ?? throw new KiteException(ErrorCodes.NotFound, "That supplier was not found.", 404);
                return await t.WithAllBranchesAsync(async () => (object)new
                {
                    supplier = s, headOffice = t.Server.IsHeadOffice,
                    balance = await t.ExecuteScalarAsync<decimal>("SELECT purch.supplier_balance(@id)", new { id }),
                    byBranch = await t.RowsAsync("""
                        SELECT b.id AS "branchId", b.code, b.name_en AS "nameEn", b.name_ar AS "nameAr", sum(l.amount) AS balance
                          FROM purch.supplier_ledger l JOIN core.branch b ON b.id = l.branch_id
                         WHERE l.supplier_id = @id OR l.supplier_id IN (SELECT x.id FROM purch.supplier x WHERE x.merged_into_id = @id)
                         GROUP BY b.id, b.code, b.name_en, b.name_ar ORDER BY b.code
                        """, new { id }),
                    merged = await t.RowsAsync("""SELECT id, number, name_en AS "nameEn", name_ar AS "nameAr" FROM purch.supplier WHERE merged_into_id = @id ORDER BY number""", new { id }),
                    ledger = await t.RowsAsync("""
                        SELECT l.id, l.doc_date::text AS "docDate", l.due_date::text AS "dueDate", l.number, l.kind, b.code AS "branchCode", l.amount, l.note
                          FROM purch.supplier_ledger l JOIN core.branch b ON b.id = l.branch_id
                         WHERE l.supplier_id = @id OR l.supplier_id IN (SELECT x.id FROM purch.supplier x WHERE x.merged_into_id = @id)
                         ORDER BY l.doc_date DESC, l.created_at DESC LIMIT 100
                        """, new { id }),
                });
            }, ct))).RequirePermission(Permissions.SupplierView).RequireModule("purch");

        g.MapPost("", async (SupplierInput req, HttpContext http, KiteDb db, ServerContext server, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var branch = server.BranchId ?? throw new KiteException(ErrorCodes.Internal, "This server has no branch of its own.", 500);
                var (id, number, status) = await SupplierCore.CreateAsync(t, req, branch, http.User.UserIdOf());
                return new { id, number, status };
            }, ct))).RequirePermission(Permissions.SupplierManage).RequireModule("purch");

        g.MapPut("/{id:guid}", async (Guid id, SupplierInput req, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var s = await SupplierCore.LoadAsync(t, id, forUpdate: true) ?? throw new KiteException(ErrorCodes.NotFound, "That supplier was not found.", 404);
                await SupplierCore.UpdateAsync(t, s, req, http.User.UserIdOf());
                return 0;
            }, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.SupplierManage).RequireModule("purch");

        g.MapPost("/{id:guid}/approve", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var s = await SupplierCore.LoadAsync(t, id, forUpdate: true) ?? throw new KiteException(ErrorCodes.NotFound, "That supplier was not found.", 404);
                await SupplierCore.ApproveAsync(t, s, http.User.UserIdOf());
                return 0;
            }, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.SupplierApprove).RequireModule("purch");

        g.MapPost("/{id:guid}/merge", async (Guid id, SupplierMergeRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                // Both rows locked in id order, so two merges never wait on each other.
                foreach (var x in new[] { id, req.IntoId }.Order()) await SupplierCore.LoadAsync(t, x, forUpdate: true);
                var s = await SupplierCore.LoadAsync(t, id) ?? throw new KiteException(ErrorCodes.NotFound, "That supplier was not found.", 404);
                var into = await SupplierCore.LoadAsync(t, req.IntoId) ?? throw new KiteException(ErrorCodes.NotFound, "The supplier to merge into was not found.", 404, "intoId");
                await SupplierCore.MergeAsync(t, s, into, Clean(req.Reason), http.User.UserIdOf());
                return 0;
            }, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.SupplierApprove).RequireModule("purch");

        g.MapPost("/{id:guid}/opening", async (Guid id, SupplierOpeningRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                http.User.Demand(Permissions.SupplierManage, req.BranchId);
                var s = await SupplierCore.LoadAsync(t, id, forUpdate: true) ?? throw new KiteException(ErrorCodes.NotFound, "That supplier was not found.", 404);
                var biz = await BusinessInfo.LoadAsync(t);
                var (eid, number) = await SupplierCore.OpeningAsync(t, s, req.BranchId, req.Amount, req.Date ?? BusinessDay.Today(biz.TimeZone, t.Clock), req.Note, http.User.UserIdOf());
                return new { id = eid, number };
            }, ct))).RequirePermission(Permissions.SupplierManage).RequireModule("purch");

        g.MapGet("/{id:guid}/statement", async (Guid id, DateOnly? from, DateOnly? to, Guid? branchId, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var s = await SupplierCore.LoadAsync(t, id) ?? throw new KiteException(ErrorCodes.NotFound, "That supplier was not found.", 404);
                var biz = await BusinessInfo.LoadAsync(t);
                var end = to ?? BusinessDay.Today(biz.TimeZone, t.Clock);
                var start = from ?? new DateOnly(end.Year, end.Month, 1).AddMonths(-2);
                return await t.WithAllBranchesAsync(async () =>
                {
                    var filter = branchId is null ? "" : "AND l.branch_id = @branchId";
                    const string who = "(l.supplier_id = @id OR l.supplier_id IN (SELECT x.id FROM purch.supplier x WHERE x.merged_into_id = @id))";
                    var opening = await t.ExecuteScalarAsync<decimal>($"SELECT coalesce(sum(l.amount), 0) FROM purch.supplier_ledger l WHERE {who} AND l.doc_date < @start {filter}",
                        new { id, start = start.ToDateTime(TimeOnly.MinValue), branchId });
                    var rows = await t.QueryAsync<(DateTime Date, string Number, string Kind, string Code, string? Note, decimal Amount)>($"""
                        SELECT l.doc_date, l.number, l.kind, b.code, l.note, l.amount FROM purch.supplier_ledger l JOIN core.branch b ON b.id = l.branch_id
                         WHERE {who} AND l.doc_date BETWEEN @start AND @end {filter} ORDER BY l.doc_date, l.created_at
                        """, new { id, start = start.ToDateTime(TimeOnly.MinValue), end = end.ToDateTime(TimeOnly.MinValue), branchId });
                    var balance = opening;
                    var lines = rows.Select(r =>
                    {
                        balance += r.Amount;
                        return new { date = DateOnly.FromDateTime(r.Date), r.Number, r.Kind, branchCode = r.Code, r.Note, debit = r.Amount < 0 ? -r.Amount : 0m, credit = r.Amount > 0 ? r.Amount : 0m, balance };
                    }).ToList();
                    return (object)new { supplier = s, from = start, to = end, branchId, opening, lines, closing = balance };
                });
            }, ct))).RequirePermission(Permissions.SupplierView).RequireModule("purch");

        g.MapGet("/reports/aging", async (HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var biz = await BusinessInfo.LoadAsync(t);
                var today = BusinessDay.Today(biz.TimeZone, t.Clock);
                return await t.WithAllBranchesAsync(async () =>
                {
                    // Each entry as it stands after allocations (16.1): an invoice's open part, a payment's or debit note's part not yet set against one.
                    var lines = (await t.QueryAsync<(Guid Supplier, DateTime Due, decimal Amount)>("""
                        SELECT coalesce(s.merged_into_id, s.id), coalesce(l.due_date, l.doc_date),
                               l.amount - coalesce((SELECT sum(a.amount) FROM purch.supplier_allocation a WHERE a.debit_entry_id = l.id), 0)
                                        + coalesce((SELECT sum(a.amount) FROM purch.supplier_allocation a WHERE a.credit_entry_id = l.id), 0)
                          FROM purch.supplier_ledger l JOIN purch.supplier s ON s.id = l.supplier_id
                        """)).Where(l => l.Amount != 0).GroupBy(l => l.Supplier).ToList();
                    var names = (await t.QueryAsync<(Guid Id, string Number, string NameEn, string NameAr)>("SELECT id, number, name_en, name_ar FROM purch.supplier"))
                        .ToDictionary(x => x.Id);
                    return (object)lines.Select(gr =>
                    {
                        var a = SupplierCore.Aging(gr.Select(x => (DateOnly.FromDateTime(x.Due), x.Amount)).ToList(), today);
                        var n = names[gr.Key];
                        return new { supplierId = gr.Key, number = n.Number, nameEn = n.NameEn, nameAr = n.NameAr, current = a.Current, d1_30 = a.D1To30, d31_60 = a.D31To60,
                                     d61_90 = a.D61To90, d90plus = a.D90Plus, advance = a.OnAccount, total = a.Total };
                    }).Where(x => x.total != 0 || x.advance != 0).OrderByDescending(x => x.d90plus + x.d61_90).ThenBy(x => x.nameEn, StringComparer.Ordinal).ToList();
                });
            }, ct))).RequirePermission(Permissions.SupplierView).RequireModule("purch");
    }
}
