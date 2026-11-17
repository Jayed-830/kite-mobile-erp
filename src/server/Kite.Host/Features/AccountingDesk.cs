using Kite.Host.Infrastructure;
using Kite.Persistence;
using Kite.Platform;

namespace Kite.Host.Features;

/// <summary>
/// The accountant's screens (blueprint 17, module acct_ui): the chart, periods by branch, journals and manual journals, statements, VAT returns,
/// card settlements and the daily ledger check. Posting itself runs whether the screens are on or not (2.3 rule 5).
/// </summary>
public static class AccountingDeskEndpoints
{
    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/accounting");

        // ------------------------------------------------------------ chart of accounts (17.2)
        g.MapGet("/chart", async (HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), AcctCore.AccountsAsync, ct)))
            .RequirePermission(Permissions.AcctView).RequireModule("acct_ui");

        g.MapPost("/accounts", async (AccountInput req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => AcctCore.CreateAccountAsync(t, req, http.User.UserIdOf()), ct)))
            .RequirePermission(Permissions.AcctCoaManage).RequireModule("acct_ui");

        g.MapPut("/accounts/{id:guid}", async (Guid id, AccountChange req, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t => { await AcctCore.UpdateAccountAsync(t, id, req, http.User.UserIdOf()); return 0; }, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.AcctCoaManage).RequireModule("acct_ui");

        g.MapGet("/policy", async (HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var p = await AcctCore.PolicyAsync(t);
                return (object)new { manualJournalApprovalAbove = p.ApprovalAbove, vatPeriodMonths = p.VatMonths, vatFirstMonth = p.VatFirst, version = p.Version };
            }, ct))).RequirePermission(Permissions.AcctView).RequireModule("acct_ui");

        g.MapPut("/policy", async (AcctPolicyInput req, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            if (req.ManualJournalApprovalAbove < 0 || req.VatPeriodMonths is not (1 or 3) || req.VatFirstMonth is < 1 or > 12)
                throw new KiteException(ErrorCodes.Validation, "The approval threshold is zero or more; VAT periods are 1 or 3 months starting in months 1 to 12.", 400);
            await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var n = await t.ExecuteAsync("""
                    UPDATE acct.policy SET manual_journal_approval_above = @ManualJournalApprovalAbove, vat_period_months = @VatPeriodMonths, vat_first_month = @VatFirstMonth,
                           updated_by = @uid WHERE version = @Version
                    """, new { req.ManualJournalApprovalAbove, req.VatPeriodMonths, req.VatFirstMonth, req.Version, uid = http.User.UserIdOf() });
                if (n == 0) throw new KiteException(ErrorCodes.VersionMismatch, "Someone changed the accounting settings; open them again.", 412);
                await t.AuditAsync(new AuditEntry("acct.policy_changed", "accounting_policy", null, After: Json.Obj(req)));
                return 0;
            }, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.AcctCoaManage).RequireModule("acct_ui");

        // ------------------------------------------------------------ periods (17.5)
        g.MapGet("/periods", async (int? fiscalYear, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => AcctCore.PeriodsAsync(t, fiscalYear), ct)))
            .RequirePermission(Permissions.AcctView).RequireModule("acct_ui");

        g.MapPost("/periods/{id:guid}/status", async (Guid id, PeriodStatusRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            if (req.BranchId is { } b) http.User.Demand(Permissions.AcctPeriodClose, b);
            return Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => AcctCore.SetPeriodStatusAsync(t, id, req, http.User.UserIdOf()), ct));
        }).RequirePermission(Permissions.AcctPeriodClose).RequireModule("acct_ui");

        g.MapPost("/year-end", async (YearEndRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => AcctCore.YearEndAsync(t, req.FiscalYear, http.User.UserIdOf()), ct)))
            .RequirePermission(Permissions.AcctYearEnd).RequireModule("acct_ui");

        // ------------------------------------------------------------ journals (17.1, 17.6)
        g.MapGet("/journals", async (DateOnly? from, DateOnly? to, Guid? branchId, string? kind, string? q, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.WithAllBranchesAsync(() => t.RowsAsync("""
                SELECT j.id, j.number, j.journal_date::text AS "journalDate", j.posting_kind AS "postingKind", j.description, b.code AS "branchCode",
                       j.source_doc_type AS "sourceDocType", (SELECT sum(l.debit) FROM acct.journal_line l WHERE l.journal_id = j.id) AS total,
                       j.reversal_of_id IS NOT NULL AS "isReversal"
                  FROM acct.journal j LEFT JOIN core.branch b ON b.id = j.branch_id
                 WHERE (@from::date IS NULL OR j.journal_date >= @from) AND (@to::date IS NULL OR j.journal_date <= @to)
                   AND (@branchId::uuid IS NULL OR EXISTS (SELECT 1 FROM acct.journal_line l WHERE l.journal_id = j.id AND l.branch_id = @branchId))
                   AND (@kind::text IS NULL OR j.posting_kind LIKE @kind || '%')
                   AND (@q::text IS NULL OR j.number ILIKE '%' || @q || '%' OR j.description ILIKE '%' || @q || '%')
                 ORDER BY j.journal_date DESC, j.posted_at DESC LIMIT 500
                """, new { from = from?.ToDateTime(TimeOnly.MinValue), to = to?.ToDateTime(TimeOnly.MinValue), branchId, kind = Clean(kind), q = Clean(q) })), ct)))
            .RequirePermission(Permissions.AcctView).RequireModule("acct_ui");

        g.MapGet("/journals/{id:guid}", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => AcctCore.JournalAsync(t, id), ct)))
            .RequirePermission(Permissions.AcctView).RequireModule("acct_ui");

        g.MapGet("/manual-journals", async (string? status, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.RowsAsync("""
                SELECT m.id, m.number, m.journal_date::text AS "journalDate", m.description, m.status, m.total, b.code AS "branchCode", u.display_name_en AS "createdBy",
                       m.reversal_journal_id IS NOT NULL AS reversed
                  FROM acct.manual_journal m JOIN core.branch b ON b.id = m.branch_id LEFT JOIN iam."user" u ON u.id = m.created_by
                 WHERE @status::text IS NULL OR m.status = @status
                 ORDER BY m.created_at DESC LIMIT 300
                """, new { status = Clean(status) }), ct))).RequirePermission(Permissions.AcctView).RequireModule("acct_ui");

        g.MapGet("/manual-journals/{id:guid}", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => AcctCore.ManualAsync(t, id), ct)))
            .RequirePermission(Permissions.AcctView).RequireModule("acct_ui");

        g.MapPost("/manual-journals", async (ManualJournalInput req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => AcctCore.SaveManualAsync(t, req, http.User.UserIdOf(), http.User, update: false), ct)))
            .RequirePermission(Permissions.AcctJournalManual).RequireModule("acct_ui");

        g.MapPut("/manual-journals/{id:guid}", async (Guid id, ManualJournalInput req, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            if (req.Id != id) throw new KiteException(ErrorCodes.Validation, "The journal in the address and in the body differ.", 400, "id");
            return Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => AcctCore.SaveManualAsync(t, req, http.User.UserIdOf(), http.User, update: true), ct));
        }).RequirePermission(Permissions.AcctJournalManual).RequireModule("acct_ui");

        g.MapPost("/manual-journals/{id:guid}/submit", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => AcctCore.SubmitManualAsync(t, id, http.User.UserIdOf(), http.User), ct)))
            .RequirePermission(Permissions.AcctJournalManual).RequireModule("acct_ui");

        g.MapPost("/manual-journals/{id:guid}/approve", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => AcctCore.ApproveManualAsync(t, id, http.User.UserIdOf(), http.User), ct)))
            .RequirePermission(Permissions.AcctJournalApprove).RequireModule("acct_ui");

        g.MapPost("/manual-journals/{id:guid}/reject", async (Guid id, OrderReasonRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => AcctCore.RejectManualAsync(t, id, req.Reason, http.User.UserIdOf(), http.User), ct)))
            .RequirePermission(Permissions.AcctJournalApprove).RequireModule("acct_ui");

        g.MapPost("/manual-journals/{id:guid}/reverse", async (Guid id, ReasonRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => AcctCore.ReverseManualAsync(t, id, req, http.User.UserIdOf(), http.User), ct)))
            .RequirePermission(Permissions.AcctJournalManual).RequireModule("acct_ui");

        // ------------------------------------------------------------ statements (17.7)
        g.MapGet("/reports/trial-balance", async (DateOnly? from, DateOnly? to, Guid? branchId, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => AcctCore.TrialBalanceAsync(t, from, to, branchId), ct)))
            .RequirePermission(Permissions.AcctView).RequireModule("acct_ui");

        g.MapGet("/reports/ledger/{accountId:guid}", async (Guid accountId, DateOnly? from, DateOnly? to, Guid? branchId, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => AcctCore.LedgerAsync(t, accountId, from, to, branchId), ct)))
            .RequirePermission(Permissions.AcctView).RequireModule("acct_ui");

        g.MapGet("/reports/profit-and-loss", async (DateOnly? from, DateOnly? to, Guid? branchId, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => AcctCore.ProfitAndLossAsync(t, from, to, branchId), ct)))
            .RequirePermission(Permissions.AcctView).RequireModule("acct_ui");

        g.MapGet("/reports/balance-sheet", async (DateOnly? asOf, Guid? branchId, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => AcctCore.BalanceSheetAsync(t, asOf, branchId), ct)))
            .RequirePermission(Permissions.AcctView).RequireModule("acct_ui");

        g.MapGet("/reports/cash-flow", async (DateOnly? from, DateOnly? to, Guid? branchId, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => AcctCore.CashFlowAsync(t, from, to, branchId), ct)))
            .RequirePermission(Permissions.AcctView).RequireModule("acct_ui");

        // ------------------------------------------------------------ VAT (17.4)
        g.MapGet("/vat/periods", async (HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), AcctCore.VatPeriodsAsync, ct)))
            .RequirePermission(Permissions.AcctView).RequireModule("acct_ui");

        g.MapGet("/vat/boxes", async (HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), AcctCore.VatBoxesAsync, ct)))
            .RequirePermission(Permissions.AcctView).RequireModule("acct_ui");

        g.MapPost("/vat/boxes/review", async (HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t => { await AcctCore.ReviewVatBoxesAsync(t, http.User.UserIdOf()); return 0; }, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.AcctVatReturn).RequireModule("acct_ui");

        g.MapGet("/vat/returns", async (HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.RowsAsync("""
                SELECT v.id, v.number, v.period_from::text AS "periodFrom", v.period_to::text AS "periodTo", v.status, v.output_vat AS "outputVat", v.input_vat AS "inputVat",
                       v.net_vat AS "netVat", v.filed_at AS "filedAt", v.paid_at AS "paidAt"
                  FROM tax.vat_return v ORDER BY v.period_from DESC
                """), ct))).RequirePermission(Permissions.AcctView).RequireModule("acct_ui");

        g.MapPost("/vat/returns", async (VatReturnRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => AcctCore.PrepareVatAsync(t, req, http.User.UserIdOf()), ct)))
            .RequirePermission(Permissions.AcctVatReturn).RequireModule("acct_ui");

        g.MapGet("/vat/returns/{id:guid}", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => AcctCore.VatReturnAsync(t, id), ct)))
            .RequirePermission(Permissions.AcctView).RequireModule("acct_ui");

        g.MapPut("/vat/returns/{id:guid}/adjustments", async (Guid id, VatAdjustmentsRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => AcctCore.VatAdjustAsync(t, id, req, http.User.UserIdOf()), ct)))
            .RequirePermission(Permissions.AcctVatReturn).RequireModule("acct_ui");

        g.MapPost("/vat/returns/{id:guid}/file", async (Guid id, VatFileRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => AcctCore.FileVatAsync(t, id, req, http.User.UserIdOf()), ct)))
            .RequirePermission(Permissions.AcctVatReturn).RequireModule("acct_ui");

        g.MapPost("/vat/returns/{id:guid}/pay", async (Guid id, VatPayRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => AcctCore.PayVatAsync(t, id, req, http.User.UserIdOf()), ct)))
            .RequirePermission(Permissions.AcctVatReturn).RequireModule("acct_ui");

        // ------------------------------------------------------------ card settlements (17.3)
        g.MapGet("/card-settlements", async (HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t => (object)new
            {
                clearing = await t.RowsAsync("""
                    SELECT b.id AS "branchId", b.code AS "branchCode", b.name_en AS "nameEn", b.name_ar AS "nameAr",
                           coalesce((SELECT sum(jl.debit - jl.credit) FROM acct.journal_line jl JOIN acct.account a ON a.id = jl.account_id
                                      WHERE a.code = '1150' AND jl.branch_id = b.id), 0) AS balance
                      FROM core.branch b WHERE b.status = 'active' ORDER BY b.code
                    """),
                settlements = await t.RowsAsync("""
                    SELECT c.id, c.number, c.settlement_date::text AS "settlementDate", b.code AS "branchCode", c.gross_amount AS "grossAmount", c.fees, c.net_amount AS "netAmount",
                           c.reference
                      FROM acct.card_settlement c JOIN core.branch b ON b.id = c.branch_id ORDER BY c.settlement_date DESC, c.created_at DESC LIMIT 200
                    """),
            }, ct))).RequirePermission(Permissions.AcctView).RequireModule("acct_ui");

        g.MapPost("/card-settlements", async (BankCardSettlementInput req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => AcctCore.CardSettlementAsync(t, req, http.User.UserIdOf(), http.User), ct)))
            .RequirePermission(Permissions.AcctCardSettle).RequireModule("acct_ui");

        // ------------------------------------------------------------ the daily ledger check (17.8)
        g.MapGet("/reconciliation", async (HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), Reconciliation.LatestAsync, ct)))
            .RequirePermission(Permissions.AcctView).RequireModule("acct_ui");

        g.MapPost("/reconciliation/run", async (HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), Reconciliation.RunAsync, ct);
            return Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), Reconciliation.LatestAsync, ct));
        }).RequirePermission(Permissions.AcctView).RequireModule("acct_ui");
    }
}

/// <summary>Expenses paid in cash or by bank (17.3, module exp).</summary>
public static class ExpenseEndpoints
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/expenses");

        g.MapGet("", async (DateOnly? from, DateOnly? to, Guid? branchId, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.RowsAsync("""
                SELECT e.id, e.number, e.expense_date::text AS "expenseDate", b.code AS "branchCode", a.code AS "accountCode", a.name_en AS "accountNameEn",
                       a.name_ar AS "accountNameAr", e.description, coalesce(s.name_en, e.payee) AS payee, e.net_amount AS "netAmount", e.tax_amount AS "taxAmount",
                       e.gross_amount AS "grossAmount", e.paid_by AS "paidBy"
                  FROM exp.expense e JOIN core.branch b ON b.id = e.branch_id JOIN acct.account a ON a.id = e.account_id LEFT JOIN purch.supplier s ON s.id = e.supplier_id
                 WHERE (@from::date IS NULL OR e.expense_date >= @from) AND (@to::date IS NULL OR e.expense_date <= @to) AND (@branchId::uuid IS NULL OR e.branch_id = @branchId)
                 ORDER BY e.expense_date DESC, e.created_at DESC LIMIT 300
                """, new { from = from?.ToDateTime(TimeOnly.MinValue), to = to?.ToDateTime(TimeOnly.MinValue), branchId }), ct)))
            .RequirePermission(Permissions.ExpView).RequireModule("exp");

        // The accounts an expense can go to: expense accounts (not cost of sales) and fixed assets.
        g.MapGet("/accounts", async (HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.RowsAsync("""
                SELECT id, code, name_en AS "nameEn", name_ar AS "nameAr", type FROM acct.account
                 WHERE is_postable AND status = 'active' AND ((type = 'expense' AND code NOT LIKE '5%' AND code NOT IN ('6210','6400','6500','6700')) OR cash_flow_class = 'investing')
                 ORDER BY code
                """), ct))).RequirePermission(Permissions.ExpView).RequireModule("exp");

        g.MapGet("/{id:guid}", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => AcctCore.ExpenseDetailAsync(t, id), ct)))
            .RequirePermission(Permissions.ExpView).RequireModule("exp");

        g.MapPost("", async (ExpenseInput req, HttpContext http, KiteDb db, LicenceService licence, CancellationToken ct) =>
        {
            await licence.RequireTransactAsync(ct);
            return Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => AcctCore.ExpenseAsync(t, req, http.User.UserIdOf(), http.User), ct));
        }).RequirePermission(Permissions.ExpCreate).RequireModule("exp");
    }
}
