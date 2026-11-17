using System.Globalization;
using Kite.Accounting;
using Kite.Persistence;
using Kite.Platform;
using Kite.Sales;

namespace Kite.Host.Infrastructure;

// ---------------------------------------------------------------- requests (blueprint 17)
public sealed record AccountInput(string Code, string NameEn, string NameAr, string Type, Guid? ParentId = null, bool IsPostable = true, bool IsControl = false,
    string? CashFlowClass = null);
public sealed record AccountChange(string NameEn, string NameAr, Guid? ParentId, bool IsControl, string CashFlowClass, string Status, int Version);
public sealed record PeriodStatusRequest(string Status, Guid? BranchId = null, string? Reason = null);
public sealed record YearEndRequest(int FiscalYear);
public sealed record ManualLineInput(Guid AccountId, Guid BranchId, decimal Debit, decimal Credit, string? Memo = null);
public sealed record ManualJournalInput(Guid Id, Guid BranchId, DateOnly JournalDate, string Description, List<ManualLineInput> Lines, bool IntoSoftClosed = false,
    int? Version = null);
public sealed record ReasonRequest(string Reason, DateOnly? Date = null);
public sealed record ExpenseInput(Guid Id, Guid BranchId, Guid AccountId, string Description, decimal NetAmount, Guid TaxCodeId, string PaidBy,
    DateOnly? ExpenseDate = null, Guid? SupplierId = null, string? Payee = null, string? Reference = null);
public sealed record BankCardSettlementInput(Guid Id, Guid BranchId, decimal GrossAmount, decimal Fees, string Reference, DateOnly? SettlementDate = null);
public sealed record VatReturnRequest(DateOnly PeriodFrom, DateOnly PeriodTo);
public sealed record VatAdjustmentsRequest(Dictionary<string, VatFigure>? Boxes, bool RefundRequested, int Version);
public sealed record VatFigure(decimal Value, decimal Vat);
public sealed record VatFileRequest(string? FilingReference = null);
public sealed record VatPayRequest(string Reference, DateOnly? Date = null);
public sealed record AcctPolicyInput(decimal ManualJournalApprovalAbove, short VatPeriodMonths, short VatFirstMonth, int Version);

/// <summary>Accounting (blueprint 17): the chart, periods by branch, manual journals, statements, VAT returns, expenses and card settlements.</summary>
public static class AcctCore
{
    private static DateTime D(DateOnly d) => d.ToDateTime(TimeOnly.MinValue);
    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static readonly string[] Statuses = ["open", "soft_closed", "closed"];

    public static async Task<(decimal ApprovalAbove, short VatMonths, short VatFirst, int Version)> PolicyAsync(DbTx t) =>
        await t.QuerySingleOrDefaultAsync<(decimal, short, short, int)>(
            "SELECT manual_journal_approval_above, vat_period_months, vat_first_month, version FROM acct.policy LIMIT 1") is { Item4: > 0 } p ? p : (1000m, (short)3, (short)1, 1);

    public static Task<IReadOnlyList<Dictionary<string, object?>>> BranchesAsync(DbTx t) =>
        t.WithAllBranchesAsync(() => t.RowsAsync("""
            SELECT b.id, b.code, b.name_en AS "nameEn", b.name_ar AS "nameAr", b.is_head_office AS "isHeadOffice",
                   (SELECT s.last_seen_at FROM core.server s WHERE s.branch_id = b.id AND s.role = 'branch' AND NOT s.is_self AND s.status = 'active' LIMIT 1) AS "branchServerSeenAt"
              FROM core.branch b WHERE b.status = 'active' ORDER BY b.code
            """));

    // ================================================================ chart of accounts (17.2)
    public static Task<IReadOnlyList<Dictionary<string, object?>>> AccountsAsync(DbTx t) => t.WithAllBranchesAsync(() => t.RowsAsync("""
        SELECT a.id, a.code, a.name_en AS "nameEn", a.name_ar AS "nameAr", a.type, a.parent_id AS "parentId", p.code AS "parentCode", a.is_postable AS "isPostable",
               a.is_control AS "isControl", a.status, a.cash_flow_class AS "cashFlowClass", a.version,
               coalesce((SELECT sum(l.debit - l.credit) FROM acct.journal_line l WHERE l.account_id = a.id), 0) AS balance,
               EXISTS (SELECT 1 FROM acct.journal_line l WHERE l.account_id = a.id) AS used
          FROM acct.account a LEFT JOIN acct.account p ON p.id = a.parent_id
         ORDER BY a.code
        """));

    public static async Task<object> CreateAccountAsync(DbTx t, AccountInput r, Guid uid)
    {
        var biz = await BusinessInfo.LoadAsync(t);
        var code = r.Code.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(code, "^[0-9]{3,10}$")) throw new KiteException(ErrorCodes.Validation, "An account code is 3 to 10 digits.", 400, "code");
        if (r.Type is not ("asset" or "liability" or "equity" or "revenue" or "expense")) throw new KiteException(ErrorCodes.Validation, "Choose the account type.", 400, "type");
        if (Clean(r.NameEn) is null || Clean(r.NameAr) is null) throw new KiteException(ErrorCodes.Validation, "Give the account's name in English and Arabic.", 400, "nameEn");
        var expected = code[0] switch { '1' => "asset", '2' => "liability", '3' => "equity", '4' => "revenue", '5' or '6' => "expense", _ => null };
        if (expected is not null && expected != r.Type)
            throw new KiteException(ErrorCodes.Validation, $"Codes starting with {code[0]} are {expected} accounts (17.2).", 400, "type");
        if (r.ParentId is { } pid)
        {
            var parent = await t.QuerySingleOrDefaultAsync<(string Type, bool Postable)>("SELECT type, is_postable FROM acct.account WHERE id = @pid", new { pid });
            if (parent.Type is null || parent.Postable || parent.Type != r.Type) throw new KiteException(ErrorCodes.Validation, "The parent is a heading of the same type.", 400, "parentId");
        }
        var type = Enum.Parse<AccountType>(r.Type, ignoreCase: true);
        var cff = r.CashFlowClass ?? CashFlow.DefaultClass(code, type, r.IsPostable);
        if (!CashFlow.Classes.Contains(cff)) throw new KiteException(ErrorCodes.Validation, "Choose the cash-flow class.", 400, "cashFlowClass");
        if (r.Type is "revenue" or "expense" && cff != "none") throw new KiteException(ErrorCodes.Validation, "Revenue and expense accounts are in the profit, not the cash-flow classes.", 400, "cashFlowClass");
        var id = t.NewId();
        await t.ExecuteAsync("""
            INSERT INTO acct.account (id, business_id, code, name_en, name_ar, type, parent_id, is_postable, is_control, cash_flow_class, created_by)
            VALUES (@id, @b, @code, @en, @ar, @Type, @ParentId, @IsPostable, @IsControl, @cff, @uid)
            """, new { id, b = biz.Id, code, en = r.NameEn.Trim(), ar = r.NameAr.Trim(), r.Type, r.ParentId, r.IsPostable, r.IsControl, cff, uid });
        await t.AuditAsync(new AuditEntry("acct.account_created", "account", id, After: Json.Obj(new { code, r.NameEn, r.Type, r.IsControl, cff })));
        return new { id, code };
    }

    public static async Task UpdateAccountAsync(DbTx t, Guid id, AccountChange r, Guid uid)
    {
        var a = await t.QuerySingleOrDefaultAsync<(string Code, string Type, bool Postable, bool Control, string Status, int Version)>(
            "SELECT code, type, is_postable, is_control, status, version FROM acct.account WHERE id = @id FOR UPDATE", new { id });
        if (a.Code is null) throw new KiteException(ErrorCodes.NotFound, "That account was not found.", 404);
        if (a.Version != r.Version) throw new KiteException(ErrorCodes.VersionMismatch, "Someone changed this account; open it again.", 412);
        if (Clean(r.NameEn) is null || Clean(r.NameAr) is null) throw new KiteException(ErrorCodes.Validation, "Give the account's name in English and Arabic.", 400, "nameEn");
        if (r.Status is not ("active" or "inactive")) throw new KiteException(ErrorCodes.Validation, "An account is active or inactive.", 400, "status");
        if (!CashFlow.Classes.Contains(r.CashFlowClass)) throw new KiteException(ErrorCodes.Validation, "Choose the cash-flow class.", 400, "cashFlowClass");
        if (r.Status == "inactive" && a.Status == "active")
        {
            var balance = await t.WithAllBranchesAsync(() => t.ExecuteScalarAsync<decimal>("SELECT coalesce(sum(debit - credit), 0) FROM acct.journal_line WHERE account_id = @id", new { id }));
            if (balance != 0) throw new KiteException(ErrorCodes.AccountInUse, $"Account {a.Code} has a balance of {balance:0.000}; move it before switching the account off.", 409);
            var rules = await t.ExecuteScalarAsync<string?>("""
                SELECT string_agg(DISTINCT r.event_type, ', ') FROM acct.posting_rule r
                 WHERE r.definition::text LIKE '%"' || @code || '"%'
                   AND r.rule_version = (SELECT max(x.rule_version) FROM acct.posting_rule x WHERE x.business_id = r.business_id AND x.event_type = r.event_type)
                """, new { code = a.Code });
            if (rules is not null) throw new KiteException(ErrorCodes.AccountInUse, $"Account {a.Code} is used by the posting rules {rules}.", 409);
        }
        if (r.ParentId is { } pid)
        {
            var parent = await t.QuerySingleOrDefaultAsync<(string Type, bool Postable)>("SELECT type, is_postable FROM acct.account WHERE id = @pid", new { pid });
            if (parent.Type is null || parent.Postable || parent.Type != a.Type || pid == id) throw new KiteException(ErrorCodes.Validation, "The parent is a heading of the same type.", 400, "parentId");
        }
        await t.ExecuteAsync("""
            UPDATE acct.account SET name_en = @en, name_ar = @ar, parent_id = @ParentId, is_control = @IsControl, cash_flow_class = @CashFlowClass, status = @Status, updated_by = @uid
             WHERE id = @id
            """, new { id, en = r.NameEn.Trim(), ar = r.NameAr.Trim(), r.ParentId, r.IsControl, r.CashFlowClass, r.Status, uid });
        await t.AuditAsync(new AuditEntry("acct.account_changed", "account", id, Before: Json.Obj(new { a.Control, a.Status }), After: Json.Obj(r)));
    }

    // ================================================================ periods by branch (17.5)
    public static async Task<object> PeriodsAsync(DbTx t, int? fiscalYear)
    {
        var biz = await BusinessInfo.LoadAsync(t);
        var year = fiscalYear ?? await FiscalPeriods.FiscalYearOfAsync(t, biz.Id, BusinessDay.Today(biz.TimeZone, t.Clock));
        await FiscalPeriods.EnsureYearAsync(t, biz.Id, BusinessDay.Today(biz.TimeZone, t.Clock));   // the current year is always listed
        return await t.WithAllBranchesAsync(async () => (object)new
        {
            fiscalYear = year,
            years = await t.QueryAsync<int>("SELECT DISTINCT fiscal_year FROM acct.fiscal_period ORDER BY 1"),
            branches = await BranchesAsync(t),
            periods = await t.RowsAsync("""
                SELECT p.id, p.period_no AS "periodNo", p.starts_on::text AS "startsOn", p.ends_on::text AS "endsOn", p.status AS "businessStatus",
                       coalesce((SELECT json_object_agg(b.id, acct.period_status(p.id, b.id)) FROM core.branch b WHERE b.status = 'active'), '{}')::text AS branches,
                       (SELECT count(*) FROM acct.journal j WHERE j.fiscal_period_id = p.id) AS journals,
                       EXISTS (SELECT 1 FROM tax.vat_return v WHERE v.status = 'filed' AND v.period_from <= p.ends_on AND v.period_to >= p.starts_on) AS "vatFiled"
                  FROM acct.fiscal_period p WHERE p.branch_id IS NULL AND p.fiscal_year = @year ORDER BY p.period_no
                """, new { year }),
            yearClosed = await t.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM acct.journal j JOIN acct.fiscal_period p ON p.id = j.fiscal_period_id WHERE p.fiscal_year = @year AND j.posting_kind = 'acct.year_end')", new { year }),
        });
    }

    /// <summary>Closes (soft or hard) or reopens a period for one branch or every branch. A period of a closed year, or one a filed VAT return covers, is not reopened.</summary>
    public static async Task<object> SetPeriodStatusAsync(DbTx t, Guid periodId, PeriodStatusRequest r, Guid uid)
    {
        if (!Statuses.Contains(r.Status)) throw new KiteException(ErrorCodes.Validation, "A period is open, soft-closed or closed.", 400, "status");
        if (Clean(r.Reason) is null) throw new KiteException(ErrorCodes.Validation, "Say why.", 400, "reason");
        return await t.WithAllBranchesAsync(async () =>
        {
            var p = await t.QuerySingleOrDefaultAsync<(Guid Id, Guid Business, int Year, short No, DateTime Starts, DateTime Ends, string Status)>("""
                SELECT id, business_id, fiscal_year, period_no, starts_on, ends_on, status FROM acct.fiscal_period WHERE id = @periodId AND branch_id IS NULL FOR UPDATE
                """, new { periodId });
            if (p.Id == Guid.Empty) throw new KiteException(ErrorCodes.NotFound, "That period was not found.", 404);
            if (p.No == 13) throw new KiteException(ErrorCodes.YearEndState, "The year-end period is closed by the year-end close.", 409);
            if (r.Status == "open" || (r.Status == "soft_closed"))
            {
                if (await t.ExecuteScalarAsync<bool>("""
                        SELECT EXISTS (SELECT 1 FROM acct.journal j JOIN acct.fiscal_period q ON q.id = j.fiscal_period_id WHERE q.fiscal_year = @Year AND j.posting_kind = 'acct.year_end')
                        """, new { p.Year }))
                    throw new KiteException(ErrorCodes.YearEndState, $"Fiscal year {p.Year} is closed; its periods stay closed.", 409);
                if (r.Status == "open" && await t.ExecuteScalarAsync<bool>("""
                        SELECT EXISTS (SELECT 1 FROM tax.vat_return v WHERE v.status = 'filed' AND v.period_from <= @Ends AND v.period_to >= @Starts)
                        """, new { p.Starts, p.Ends }))
                    throw new KiteException(ErrorCodes.VatReturnState, "A filed VAT return covers this period; it stays closed so the return keeps its figures.", 409);
            }
            var branches = r.BranchId is { } one ? [one] : (await t.QueryAsync<Guid>("SELECT id FROM core.branch WHERE status = 'active'")).ToList();
            var changed = 0;
            foreach (var b in branches)
            {
                var before = await t.ExecuteScalarAsync<string>("SELECT acct.period_status(@periodId, @b)", new { periodId, b });
                var n = await t.ExecuteAsync("""
                    INSERT INTO acct.fiscal_period (id, business_id, branch_id, fiscal_year, period_no, starts_on, ends_on, status, updated_by)
                    VALUES (@id, @Business, @b, @Year, @No, @Starts, @Ends, @Status, @uid)
                    ON CONFLICT ON CONSTRAINT fiscal_period_unique DO UPDATE SET status = EXCLUDED.status, updated_by = EXCLUDED.updated_by
                     WHERE acct.fiscal_period.status <> EXCLUDED.status
                    """, new { id = t.NewId(), p.Business, b, p.Year, p.No, p.Starts, p.Ends, r.Status, uid });
                if (n > 0 && before != r.Status)
                {
                    changed++;
                    await t.AuditAsync(new AuditEntry("acct.period_status", "fiscal_period", periodId, Before: Json.Obj(new { status = before }),
                        After: Json.Obj(new { status = r.Status, p.Year, p.No }), Reason: r.Reason!.Trim(), BranchId: b));
                }
            }
            return (object)new { changed };
        });
    }

    /// <summary>
    /// Year-end close (17.5), at head office for the business: every revenue and expense balance of the year, branch by branch, into retained
    /// earnings (3300) in the closing period (13), dated the last day of the year. The year's periods must be closed at every branch first; then
    /// all of them, and the closing period, are closed for good.
    /// </summary>
    public static async Task<object> YearEndAsync(DbTx t, int year, Guid uid)
    {
        if (!t.Server.IsHeadOffice) throw new KiteException(ErrorCodes.NotHeadOffice, "The year is closed at head office, for the whole business.", 409);
        var biz = await BusinessInfo.LoadAsync(t);
        return await t.WithAllBranchesAsync(async () =>
        {
            await t.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended('kite:year_end', 0))");
            var periods = (await t.QueryAsync<(Guid Id, short No, DateTime Starts, DateTime Ends)>(
                "SELECT id, period_no, starts_on, ends_on FROM acct.fiscal_period WHERE branch_id IS NULL AND fiscal_year = @year AND period_no <= 12 ORDER BY period_no", new { year })).ToList();
            if (periods.Count != 12) throw new KiteException(ErrorCodes.YearEndState, $"Fiscal year {year} does not have its twelve periods.", 409);
            var branches = (await t.QueryAsync<(Guid Id, string Code)>("SELECT id, code FROM core.branch WHERE status = 'active' ORDER BY code")).ToList();
            foreach (var (pid, no, _, _) in periods)
                foreach (var (bid, code) in branches)
                    if (await t.ExecuteScalarAsync<string>("SELECT acct.period_status(@pid, @bid)", new { pid, bid }) == "open")
                        throw new KiteException(ErrorCodes.YearEndState, $"Period {no} of {year} is still open at {code}; close every period of the year first.", 409);
            var end = DateOnly.FromDateTime(periods[^1].Ends);
            var p13 = await t.ExecuteScalarAsync<Guid?>("SELECT id FROM acct.fiscal_period WHERE branch_id IS NULL AND fiscal_year = @year AND period_no = 13", new { year });
            if (p13 is { } existing && await t.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM acct.journal WHERE fiscal_period_id = @existing AND posting_kind = 'acct.year_end')", new { existing }))
                throw new KiteException(ErrorCodes.YearEndState, $"Fiscal year {year} is already closed.", 409);
            var periodId = p13 ?? t.NewId();
            if (p13 is null)
                await t.ExecuteAsync("""
                    INSERT INTO acct.fiscal_period (id, business_id, branch_id, fiscal_year, period_no, starts_on, ends_on, status) VALUES (@periodId, @b, NULL, @year, 13, @e, @e, 'open')
                    """, new { periodId, b = biz.Id, year, e = D(end) });
            var balances = (await t.QueryAsync<(string Code, Guid Branch, decimal Balance)>("""
                SELECT a.code, l.branch_id, sum(l.debit - l.credit)
                  FROM acct.journal_line l JOIN acct.journal j ON j.id = l.journal_id JOIN acct.account a ON a.id = l.account_id
                  JOIN acct.fiscal_period p ON p.id = j.fiscal_period_id
                 WHERE p.fiscal_year = @year AND a.type IN ('revenue','expense') AND j.posting_kind <> 'acct.year_end' AND l.branch_id IS NOT NULL
                 GROUP BY a.code, l.branch_id HAVING sum(l.debit - l.credit) <> 0
                """, new { year })).ToList();
            var lines = new List<Posting.DirectLine>();
            foreach (var g in balances.GroupBy(b => b.Branch))
            {
                foreach (var (code, branch, bal) in g)
                    lines.Add(new Posting.DirectLine(code, branch, bal < 0 ? -bal : 0, bal > 0 ? bal : 0, $"Close {year}"));
                var result = -g.Sum(x => x.Balance);   // profit is a credit balance
                if (result != 0) lines.Add(new Posting.DirectLine("3300", g.Key, result < 0 ? -result : 0, result > 0 ? result : 0, $"Result {year}"));
            }
            Guid? journal = null;
            var ho = branches.FirstOrDefault(b => true).Id;
            var headOffice = await t.ExecuteScalarAsync<Guid?>("SELECT id FROM core.branch WHERE is_head_office LIMIT 1") ?? ho;
            if (lines.Count >= 2)
            {
                var period = new PeriodRef(periodId, year, end, end, "open");
                journal = await Posting.PostLinesAsync(t, biz.Id, headOffice, "acct.year_end", lines, end, "year_end", periodId, $"Year-end close {year}", period: period);
            }
            await t.ExecuteAsync("UPDATE acct.fiscal_period SET status = 'closed', updated_by = @uid WHERE fiscal_year = @year", new { year, uid });
            foreach (var (pid, no, starts, ends) in periods)
                foreach (var (bid, _) in branches)
                    await t.ExecuteAsync("""
                        INSERT INTO acct.fiscal_period (id, business_id, branch_id, fiscal_year, period_no, starts_on, ends_on, status, updated_by)
                        VALUES (@id, @b, @bid, @year, @no, @starts, @ends, 'closed', @uid)
                        ON CONFLICT ON CONSTRAINT fiscal_period_unique DO UPDATE SET status = 'closed', updated_by = EXCLUDED.updated_by
                        """, new { id = t.NewId(), b = biz.Id, bid, year, no, starts, ends, uid });
            var profit = -balances.Sum(b => b.Balance);
            await t.AuditAsync(new AuditEntry("acct.year_closed", "fiscal_year", periodId, After: Json.Obj(new { year, profit, accounts = balances.Count })));
            return (object)new { year, profit, journalId = journal };
        });
    }

    // ================================================================ manual journals (17.6)
    public static async Task<object> SaveManualAsync(DbTx t, ManualJournalInput r, Guid uid, System.Security.Claims.ClaimsPrincipal user, bool update)
    {
        user.Demand(Permissions.AcctJournalManual, r.BranchId);
        if (Clean(r.Description) is null) throw new KiteException(ErrorCodes.Validation, "Describe the journal.", 400, "description");
        if (r.Lines is not { Count: >= 2 }) throw new KiteException(ErrorCodes.Validation, "A journal needs at least two lines.", 400, "lines");
        for (var i = 0; i < r.Lines.Count; i++)
        {
            var l = r.Lines[i];
            if (l.Debit < 0 || l.Credit < 0 || (l.Debit == 0) == (l.Credit == 0) || l.Debit != decimal.Round(l.Debit, 3) || l.Credit != decimal.Round(l.Credit, 3))
                throw new KiteException(ErrorCodes.Validation, "Each line has a debit or a credit above zero, in baisa.", 400, $"lines[{i}]");
        }
        var biz = await BusinessInfo.LoadAsync(t);
        var total = r.Lines.Sum(l => l.Debit);
        var existing = await t.QuerySingleOrDefaultAsync<(Guid Id, string Status, int Version, string Number)>(
            "SELECT id, status, version, number FROM acct.manual_journal WHERE id = @Id FOR UPDATE", new { r.Id });
        if (existing.Id != Guid.Empty && !update) return await ManualAsync(t, r.Id);
        string number;
        if (update)
        {
            if (existing.Id == Guid.Empty) throw new KiteException(ErrorCodes.NotFound, "That journal was not found.", 404);
            if (existing.Status != "draft") throw new KiteException(ErrorCodes.ManualJournalState, $"Journal {existing.Number} is {existing.Status.Replace('_', ' ')}; only a draft is changed.", 409);
            if (r.Version is { } v && v != existing.Version) throw new KiteException(ErrorCodes.VersionMismatch, "Someone changed this journal; open it again.", 412);
            number = existing.Number;
            await t.ExecuteAsync("DELETE FROM acct.manual_journal_line WHERE manual_journal_id = @Id", new { r.Id });
            await t.ExecuteAsync("""
                UPDATE acct.manual_journal SET journal_date = @d::date, description = @desc, total = @total, into_soft_closed = @IntoSoftClosed, updated_by = @uid WHERE id = @Id
                """, new { r.Id, d = D(r.JournalDate), desc = r.Description.Trim(), total, r.IntoSoftClosed, uid });
        }
        else
        {
            number = await DocNumbers.NextAsync(t, biz.Id, r.BranchId, "manual_journal", await FiscalPeriods.FiscalYearOfAsync(t, biz.Id, r.JournalDate), "MJ");
            await t.ExecuteAsync("""
                INSERT INTO acct.manual_journal (id, business_id, branch_id, number, journal_date, description, total, into_soft_closed, created_by, origin_server_id)
                VALUES (@Id, @b, @BranchId, @number, @d::date, @desc, @total, @IntoSoftClosed, @uid, (SELECT id FROM core.server WHERE is_self))
                """, new { r.Id, b = biz.Id, r.BranchId, number, d = D(r.JournalDate), desc = r.Description.Trim(), total, r.IntoSoftClosed, uid });
        }
        short no = 0;
        await t.WithAllBranchesAsync(async () =>
        {
            foreach (var l in r.Lines)
                await t.ExecuteAsync("""
                    INSERT INTO acct.manual_journal_line (id, manual_journal_id, line_no, account_id, branch_id, debit, credit, memo)
                    VALUES (@lineId, @journalId, @no, @AccountId, @BranchId, @Debit, @Credit, @memo)
                    """, new { lineId = t.NewId(), journalId = r.Id, no = ++no, l.AccountId, l.BranchId, l.Debit, l.Credit, memo = Clean(l.Memo) });
            return 0;
        });
        await t.AuditAsync(new AuditEntry(update ? "acct.manual_changed" : "acct.manual_created", "manual_journal", r.Id, After: Json.Obj(new { number, total }), BranchId: r.BranchId));
        return await ManualAsync(t, r.Id);
    }

    private sealed class ManualHead
    {
        public Guid Id { get; set; }
        public Guid BranchId { get; set; }
        public string Number { get; set; } = "";
        public DateTime JournalDate { get; set; }
        public string Description { get; set; } = "";
        public string Status { get; set; } = "";
        public decimal Total { get; set; }
        public bool IntoSoftClosed { get; set; }
        public Guid CreatedBy { get; set; }
        public Guid? JournalId { get; set; }
        public Guid? ReversalJournalId { get; set; }
    }

    private static async Task<ManualHead> ManualHeadAsync(DbTx t, Guid id) =>
        await t.QuerySingleOrDefaultAsync<ManualHead>("""
            SELECT id, branch_id AS BranchId, number, journal_date AS JournalDate, description, status, total, into_soft_closed AS IntoSoftClosed, created_by AS CreatedBy,
                   journal_id AS JournalId, reversal_journal_id AS ReversalJournalId
              FROM acct.manual_journal WHERE id = @id FOR UPDATE
            """, new { id }) ?? throw new KiteException(ErrorCodes.NotFound, "That journal was not found in your branches.", 404);

    private static async Task<List<(string Code, Guid Branch, decimal Debit, decimal Credit, string? Memo, bool Control)>> ManualLinesAsync(DbTx t, Guid id) =>
        (await t.WithAllBranchesAsync(() => t.QueryAsync<(string, Guid, decimal, decimal, string?, bool)>("""
            SELECT a.code, l.branch_id, l.debit, l.credit, l.memo, a.is_control FROM acct.manual_journal_line l JOIN acct.account a ON a.id = l.account_id
             WHERE l.manual_journal_id = @id ORDER BY l.line_no
            """, new { id }))).ToList();

    /// <summary>
    /// Submits a manual journal (17.6): checked, then posted at once when it is within the threshold, or left for a second approver above it. Control
    /// accounts need ACCT.CONTROL_ACCOUNT.POST; posting into a soft-closed period needs ACCT.PERIOD.CLOSE.
    /// </summary>
    public static async Task<object> SubmitManualAsync(DbTx t, Guid id, Guid uid, System.Security.Claims.ClaimsPrincipal user)
    {
        var h = await ManualHeadAsync(t, id);
        user.Demand(Permissions.AcctJournalManual, h.BranchId);
        if (h.Status != "draft") throw new KiteException(ErrorCodes.ManualJournalState, $"Journal {h.Number} is {h.Status.Replace('_', ' ')}.", 409);
        var lines = await ManualLinesAsync(t, id);
        await CheckManualAsync(t, h, lines, user);
        var policy = await PolicyAsync(t);
        if (h.Total > policy.ApprovalAbove)
        {
            await t.ExecuteAsync("UPDATE acct.manual_journal SET status = 'pending_approval', submitted_at = now(), updated_by = @uid WHERE id = @id", new { id, uid });
            await t.AuditAsync(new AuditEntry("acct.manual_submitted", "manual_journal", id, After: Json.Obj(new { h.Number, h.Total, threshold = policy.ApprovalAbove }), BranchId: h.BranchId));
            return await ManualAsync(t, id);
        }
        await PostManualAsync(t, h, lines, null, uid);
        return await ManualAsync(t, id);
    }

    private static async Task CheckManualAsync(DbTx t, ManualHead h, List<(string Code, Guid Branch, decimal Debit, decimal Credit, string? Memo, bool Control)> lines,
        System.Security.Claims.ClaimsPrincipal user)
    {
        if (lines.Count < 2) throw new KiteException(ErrorCodes.Validation, "A journal needs at least two lines.", 400, "lines");
        if (lines.Sum(l => l.Debit) != lines.Sum(l => l.Credit))
            throw new KiteException(ErrorCodes.JournalUnbalanced, $"The journal does not balance: debit {lines.Sum(l => l.Debit):0.000}, credit {lines.Sum(l => l.Credit):0.000}.", 400, "lines");
        if (lines.FirstOrDefault(l => l.Control) is { Code: not null } c && !user.HasIn(Permissions.AcctControlAccountPost, h.BranchId))
            throw new KiteException(ErrorCodes.ControlAccount, $"Account {c.Code} is a control account (its balance comes from its own ledger); posting to it by hand needs ACCT.CONTROL_ACCOUNT.POST.", 403);
        if (h.IntoSoftClosed && !user.HasIn(Permissions.AcctPeriodClose, h.BranchId))
            throw new KiteException(ErrorCodes.Forbidden, "Posting into a soft-closed period is for the accountant (ACCT.PERIOD.CLOSE).", 403);
    }

    private static async Task PostManualAsync(DbTx t, ManualHead h, List<(string Code, Guid Branch, decimal Debit, decimal Credit, string? Memo, bool Control)> lines,
        Guid? approver, Guid uid)
    {
        var biz = await BusinessInfo.LoadAsync(t);
        var journal = await Posting.PostLinesAsync(t, biz.Id, h.BranchId, "acct.manual", lines.Select(l => new Posting.DirectLine(l.Code, l.Branch, l.Debit, l.Credit, l.Memo)).ToList(),
            DateOnly.FromDateTime(h.JournalDate), "manual_journal", h.Id, $"{h.Number}: {h.Description}", allowSoftClosed: h.IntoSoftClosed);
        await t.ExecuteAsync("""
            UPDATE acct.manual_journal SET status = 'posted', journal_id = @journal, approved_by = @approver, approved_at = CASE WHEN @approver::uuid IS NULL THEN NULL ELSE now() END,
                   submitted_at = coalesce(submitted_at, now()), updated_by = @uid WHERE id = @Id
            """, new { journal, approver, uid, h.Id });
        await t.AuditAsync(new AuditEntry("acct.manual_posted", "manual_journal", h.Id, After: Json.Obj(new { h.Number, h.Total, approved = approver is not null }), BranchId: h.BranchId));
    }

    public static async Task<object> ApproveManualAsync(DbTx t, Guid id, Guid uid, System.Security.Claims.ClaimsPrincipal user)
    {
        var h = await ManualHeadAsync(t, id);
        user.Demand(Permissions.AcctJournalApprove, h.BranchId);
        if (h.Status != "pending_approval") throw new KiteException(ErrorCodes.ManualJournalState, $"Journal {h.Number} is {h.Status.Replace('_', ' ')}.", 409);
        if (h.CreatedBy == uid) throw new KiteException(ErrorCodes.SecondApprover, "Someone other than the person who entered it approves this journal.", 403);
        var lines = await ManualLinesAsync(t, id);
        await PostManualAsync(t, h, lines, uid, uid);
        return await ManualAsync(t, id);
    }

    public static async Task<object> RejectManualAsync(DbTx t, Guid id, string reason, Guid uid, System.Security.Claims.ClaimsPrincipal user)
    {
        var h = await ManualHeadAsync(t, id);
        user.Demand(Permissions.AcctJournalApprove, h.BranchId);
        if (Clean(reason) is null) throw new KiteException(ErrorCodes.Validation, "Say why it is rejected.", 400, "reason");
        if (h.Status != "pending_approval") throw new KiteException(ErrorCodes.ManualJournalState, $"Journal {h.Number} is {h.Status.Replace('_', ' ')}.", 409);
        await t.ExecuteAsync("UPDATE acct.manual_journal SET status = 'rejected', rejected_reason = @reason, updated_by = @uid WHERE id = @id", new { id, reason = reason.Trim(), uid });
        await t.AuditAsync(new AuditEntry("acct.manual_rejected", "manual_journal", id, Reason: reason.Trim(), BranchId: h.BranchId));
        return await ManualAsync(t, id);
    }

    /// <summary>A posted manual journal is corrected by its reversal (17.6): the mirror journal, linked to it, on the date given.</summary>
    public static async Task<object> ReverseManualAsync(DbTx t, Guid id, ReasonRequest r, Guid uid, System.Security.Claims.ClaimsPrincipal user)
    {
        var h = await ManualHeadAsync(t, id);
        user.Demand(Permissions.AcctJournalManual, h.BranchId);
        if (Clean(r.Reason) is null) throw new KiteException(ErrorCodes.Validation, "Say why it is reversed.", 400, "reason");
        if (h.Status != "posted" || h.JournalId is null) throw new KiteException(ErrorCodes.ManualJournalState, $"Journal {h.Number} is not posted.", 409);
        if (h.ReversalJournalId is not null) throw new KiteException(ErrorCodes.ManualJournalState, $"Journal {h.Number} is already reversed.", 409);
        var lines = await ManualLinesAsync(t, id);
        if (lines.Any(l => l.Control) && !user.HasIn(Permissions.AcctControlAccountPost, h.BranchId))
            throw new KiteException(ErrorCodes.ControlAccount, "The journal posts to a control account; reversing it needs ACCT.CONTROL_ACCOUNT.POST.", 403);
        var biz = await BusinessInfo.LoadAsync(t);
        var date = r.Date ?? BusinessDay.Today(biz.TimeZone, t.Clock);
        var rev = await Posting.PostLinesAsync(t, biz.Id, h.BranchId, "acct.manual.reversal",
            lines.Select(l => new Posting.DirectLine(l.Code, l.Branch, l.Credit, l.Debit, l.Memo)).ToList(), date, "manual_journal", id,
            $"Reversal of {h.Number}: {r.Reason.Trim()}", reversalOf: h.JournalId);
        await t.ExecuteAsync("UPDATE acct.manual_journal SET reversal_journal_id = @rev, reversed_by = @uid, reversed_reason = @reason, updated_by = @uid WHERE id = @id",
            new { id, rev, uid, reason = r.Reason.Trim() });
        await t.AuditAsync(new AuditEntry("acct.manual_reversed", "manual_journal", id, Reason: r.Reason.Trim(), BranchId: h.BranchId));
        return await ManualAsync(t, id);
    }

    public static async Task<object> ManualAsync(DbTx t, Guid id) => await t.WithAllBranchesAsync(async () => (object)new
    {
        journal = (await t.RowsAsync("""
            SELECT m.id, m.number, m.journal_date::text AS "journalDate", m.description, m.status, m.total, m.into_soft_closed AS "intoSoftClosed", m.version,
                   m.branch_id AS "branchId", b.code AS "branchCode", cu.display_name_en AS "createdBy", m.created_by AS "createdById", au.display_name_en AS "approvedBy",
                   m.approved_at AS "approvedAt", m.rejected_reason AS "rejectedReason", m.journal_id AS "journalId", j.number AS "journalNumber",
                   m.reversal_journal_id AS "reversalJournalId", rj.number AS "reversalNumber", m.reversed_reason AS "reversedReason", m.created_at AS "createdAt",
                   (SELECT manual_journal_approval_above FROM acct.policy LIMIT 1) AS "approvalAbove"
              FROM acct.manual_journal m JOIN core.branch b ON b.id = m.branch_id LEFT JOIN iam."user" cu ON cu.id = m.created_by
              LEFT JOIN iam."user" au ON au.id = m.approved_by LEFT JOIN acct.journal j ON j.id = m.journal_id LEFT JOIN acct.journal rj ON rj.id = m.reversal_journal_id
             WHERE m.id = @id
            """, new { id })).SingleOrDefault() ?? throw new KiteException(ErrorCodes.NotFound, "That journal was not found.", 404),
        lines = await t.RowsAsync("""
            SELECT l.line_no AS "lineNo", l.account_id AS "accountId", a.code AS "accountCode", a.name_en AS "accountNameEn", a.name_ar AS "accountNameAr",
                   a.is_control AS "isControl", l.branch_id AS "branchId", b.code AS "branchCode", l.debit, l.credit, l.memo
              FROM acct.manual_journal_line l JOIN acct.account a ON a.id = l.account_id JOIN core.branch b ON b.id = l.branch_id
             WHERE l.manual_journal_id = @id ORDER BY l.line_no
            """, new { id }),
    });

    // ================================================================ journals (17.1)
    public static async Task<object> JournalAsync(DbTx t, Guid id) => await t.WithAllBranchesAsync(async () => (object)new
    {
        journal = (await t.RowsAsync("""
            SELECT j.id, j.number, j.journal_date::text AS "journalDate", j.posting_kind AS "postingKind", j.description, j.source_doc_type AS "sourceDocType",
                   j.source_doc_id AS "sourceDocId", b.code AS "branchCode", p.fiscal_year AS "fiscalYear", p.period_no AS "periodNo", j.posted_at AS "postedAt",
                   u.display_name_en AS "postedBy", j.reversal_of_id AS "reversalOfId", (SELECT x.number FROM acct.journal x WHERE x.id = j.reversal_of_id) AS "reversalOfNumber",
                   (SELECT x.id FROM acct.journal x WHERE x.reversal_of_id = j.id) AS "reversedById", (SELECT x.number FROM acct.journal x WHERE x.reversal_of_id = j.id) AS "reversedByNumber"
              FROM acct.journal j LEFT JOIN core.branch b ON b.id = j.branch_id JOIN acct.fiscal_period p ON p.id = j.fiscal_period_id LEFT JOIN iam."user" u ON u.id = j.posted_by
             WHERE j.id = @id
            """, new { id })).SingleOrDefault() ?? throw new KiteException(ErrorCodes.NotFound, "That journal was not found.", 404),
        lines = await t.RowsAsync("""
            SELECT l.line_no AS "lineNo", a.code AS "accountCode", a.name_en AS "accountNameEn", a.name_ar AS "accountNameAr", b.code AS "branchCode", l.debit, l.credit,
                   l.party_type AS "partyType", coalesce(c.name_en, s.name_en) AS "partyName", l.memo
              FROM acct.journal_line l JOIN acct.account a ON a.id = l.account_id LEFT JOIN core.branch b ON b.id = l.branch_id
              LEFT JOIN crm.customer c ON l.party_type = 'customer' AND c.id = l.party_id LEFT JOIN purch.supplier s ON l.party_type = 'supplier' AND s.id = l.party_id
             WHERE l.journal_id = @id ORDER BY l.line_no
            """, new { id }),
    });

    // ================================================================ statements (17.7)
    /// <summary>"Data as of" for every branch: now for the branches this server keeps; a branch server's last contact for the others (its journals arrive with sync).</summary>
    private static async Task<object> AsOfAsync(DbTx t) => (await BranchesAsync(t)).Select(b => new
    {
        branchId = b["id"], code = b["code"], nameEn = b["nameEn"], nameAr = b["nameAr"],
        asOf = b["branchServerSeenAt"] ?? (object)t.Clock.UtcNow, onThisServer = b["branchServerSeenAt"] is null,
    }).ToList();

    private static (DateOnly From, DateOnly To) Range(DateOnly? from, DateOnly? to, BusinessInfo biz, IClock clock)
    {
        var today = BusinessDay.Today(biz.TimeZone, clock);
        var f = from ?? new DateOnly(today.Year, today.Month, 1);
        var e = to ?? today;
        if (e < f) throw new KiteException(ErrorCodes.Validation, "The end date is on or after the start date.", 400, "to");
        return (f, e);
    }

    private static async Task<DateOnly> YearStartAsync(DbTx t, Guid businessId, DateOnly d)
    {
        var start = await t.ExecuteScalarAsync<short>("SELECT fiscal_year_start_month FROM core.business WHERE id = @businessId", new { businessId });
        return FiscalCalendar.YearContaining(d, start)[0].StartsOn;
    }

    /// <summary>
    /// The trial balance (17.7): each account's opening, movements and closing for the dates, one branch or all. Revenue and expense accounts open at the
    /// start of the fiscal year; the result of earlier years not yet closed shows on its own line, so the debits always equal the credits.
    /// </summary>
    public static async Task<object> TrialBalanceAsync(DbTx t, DateOnly? from, DateOnly? to, Guid? branchId)
    {
        var biz = await BusinessInfo.LoadAsync(t);
        var (f, e) = Range(from, to, biz, t.Clock);
        var ys = await YearStartAsync(t, biz.Id, f);
        return await t.WithAllBranchesAsync(async () =>
        {
            var rows = await t.RowsAsync("""
                WITH l AS (
                  SELECT jl.account_id, j.journal_date, jl.debit, jl.credit FROM acct.journal_line jl JOIN acct.journal j ON j.id = jl.journal_id
                   WHERE (@branchId::uuid IS NULL OR jl.branch_id = @branchId) AND j.journal_date <= @e
                )
                SELECT a.id, a.code, a.name_en AS "nameEn", a.name_ar AS "nameAr", a.type, a.is_control AS "isControl",
                       coalesce(sum(l.debit - l.credit) FILTER (WHERE l.journal_date < @f AND (a.type IN ('asset','liability','equity') OR l.journal_date >= @ys)), 0) AS opening,
                       coalesce(sum(l.debit) FILTER (WHERE l.journal_date >= @f), 0) AS debit,
                       coalesce(sum(l.credit) FILTER (WHERE l.journal_date >= @f), 0) AS credit
                  FROM acct.account a LEFT JOIN l ON l.account_id = a.id
                 WHERE a.is_postable
                 GROUP BY a.id, a.code, a.name_en, a.name_ar, a.type, a.is_control
                HAVING coalesce(sum(l.debit - l.credit) FILTER (WHERE l.journal_date < @f AND (a.type IN ('asset','liability','equity') OR l.journal_date >= @ys)), 0) <> 0
                    OR coalesce(sum(l.debit) FILTER (WHERE l.journal_date >= @f), 0) <> 0 OR coalesce(sum(l.credit) FILTER (WHERE l.journal_date >= @f), 0) <> 0
                 ORDER BY a.code
                """, new { branchId, f = D(f), e = D(e), ys = D(ys) });
            var earlier = await t.ExecuteScalarAsync<decimal>("""
                SELECT coalesce(sum(jl.debit - jl.credit), 0) FROM acct.journal_line jl JOIN acct.journal j ON j.id = jl.journal_id JOIN acct.account a ON a.id = jl.account_id
                 WHERE a.type IN ('revenue','expense') AND j.journal_date < @ys AND (@branchId::uuid IS NULL OR jl.branch_id = @branchId)
                """, new { branchId, ys = D(ys) });
            foreach (var r in rows) r["closing"] = (decimal)r["opening"]! + (decimal)r["debit"]! - (decimal)r["credit"]!;
            decimal Sum(string k, Func<decimal, bool> keep) => rows.Select(r => (decimal)r[k]!).Where(keep).Sum();
            var openDr = Sum("opening", x => x > 0) + (earlier > 0 ? earlier : 0);
            var openCr = -Sum("opening", x => x < 0) + (earlier < 0 ? -earlier : 0);
            var closeDr = Sum("closing", x => x > 0) + (earlier > 0 ? earlier : 0);
            var closeCr = -Sum("closing", x => x < 0) + (earlier < 0 ? -earlier : 0);
            return (object)new
            {
                from = f.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), to = e.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), branchId,
                rows, earlierResult = earlier,
                totals = new { openingDebit = openDr, openingCredit = openCr, debit = Sum("debit", _ => true), credit = Sum("credit", _ => true), closingDebit = closeDr, closingCredit = closeCr },
                balanced = openDr == openCr && closeDr == closeCr && Sum("debit", _ => true) == Sum("credit", _ => true),
                asOf = await AsOfAsync(t),
            };
        });
    }

    public static async Task<object> LedgerAsync(DbTx t, Guid accountId, DateOnly? from, DateOnly? to, Guid? branchId)
    {
        var biz = await BusinessInfo.LoadAsync(t);
        var (f, e) = Range(from, to, biz, t.Clock);
        var ys = await YearStartAsync(t, biz.Id, f);
        return await t.WithAllBranchesAsync(async () =>
        {
            var a = (await t.RowsAsync("""SELECT id, code, name_en AS "nameEn", name_ar AS "nameAr", type FROM acct.account WHERE id = @accountId""", new { accountId })).SingleOrDefault()
                ?? throw new KiteException(ErrorCodes.NotFound, "That account was not found.", 404);
            var pl = (string)a["type"]! is "revenue" or "expense";
            var opening = await t.ExecuteScalarAsync<decimal>("""
                SELECT coalesce(sum(jl.debit - jl.credit), 0) FROM acct.journal_line jl JOIN acct.journal j ON j.id = jl.journal_id
                 WHERE jl.account_id = @accountId AND j.journal_date < @f AND (NOT @pl OR j.journal_date >= @ys) AND (@branchId::uuid IS NULL OR jl.branch_id = @branchId)
                """, new { accountId, f = D(f), ys = D(ys), pl, branchId });
            var lines = await t.RowsAsync("""
                SELECT j.id AS "journalId", j.number, j.journal_date::text AS date, j.description, j.posting_kind AS "postingKind", j.source_doc_type AS "sourceDocType",
                       j.source_doc_id AS "sourceDocId", b.code AS "branchCode", jl.debit, jl.credit, jl.memo,
                       coalesce(c.name_en, s.name_en) AS "partyName"
                  FROM acct.journal_line jl JOIN acct.journal j ON j.id = jl.journal_id LEFT JOIN core.branch b ON b.id = jl.branch_id
                  LEFT JOIN crm.customer c ON jl.party_type = 'customer' AND c.id = jl.party_id LEFT JOIN purch.supplier s ON jl.party_type = 'supplier' AND s.id = jl.party_id
                 WHERE jl.account_id = @accountId AND j.journal_date BETWEEN @f AND @e AND (@branchId::uuid IS NULL OR jl.branch_id = @branchId)
                 ORDER BY j.journal_date, j.posted_at, j.number, jl.line_no LIMIT 5000
                """, new { accountId, f = D(f), e = D(e), branchId });
            var running = opening;
            foreach (var l in lines) { running += (decimal)l["debit"]! - (decimal)l["credit"]!; l["balance"] = running; }
            return (object)new { account = a, from = f.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), to = e.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), opening, lines, closing = running };
        });
    }

    private sealed record Bal(string Code, string NameEn, string NameAr, string Type, string Cff, decimal Amount);

    /// <summary>Balances of postable accounts over a range of journal dates, the year-end close left out unless asked for.</summary>
    private static async Task<List<Bal>> BalancesAsync(DbTx t, DateOnly? from, DateOnly to, Guid? branchId, bool includeYearEnd, string? only = null) =>
        (await t.QueryAsync<Bal>($"""
            SELECT a.code, a.name_en AS NameEn, a.name_ar AS NameAr, a.type, a.cash_flow_class AS Cff, sum(jl.debit - jl.credit) AS Amount
              FROM acct.journal_line jl JOIN acct.journal j ON j.id = jl.journal_id JOIN acct.account a ON a.id = jl.account_id
             WHERE j.journal_date <= @to AND (@from::date IS NULL OR j.journal_date >= @from) AND (@branchId::uuid IS NULL OR jl.branch_id = @branchId)
               {(includeYearEnd ? "" : "AND j.posting_kind <> 'acct.year_end'")} {(only is null ? "" : "AND a.type = ANY(@types)")}
             GROUP BY a.code, a.name_en, a.name_ar, a.type, a.cash_flow_class HAVING sum(jl.debit - jl.credit) <> 0
             ORDER BY a.code
            """, new { from = from is { } x ? D(x) : (DateTime?)null, to = D(to), branchId, types = only?.Split(',') })).ToList();

    public static async Task<object> ProfitAndLossAsync(DbTx t, DateOnly? from, DateOnly? to, Guid? branchId)
    {
        var biz = await BusinessInfo.LoadAsync(t);
        var (f, e) = Range(from, to, biz, t.Clock);
        return await t.WithAllBranchesAsync(async () =>
        {
            var b = await BalancesAsync(t, f, e, branchId, includeYearEnd: false, "revenue,expense");
            object Rows(Func<Bal, bool> p, int sign) => b.Where(p).Select(x => new { x.Code, nameEn = x.NameEn, nameAr = x.NameAr, amount = sign * x.Amount }).ToList();
            var revenue = -b.Where(x => x.Type == "revenue").Sum(x => x.Amount);
            var cost = b.Where(x => x.Type == "expense" && x.Code.StartsWith('5')).Sum(x => x.Amount);
            var expenses = b.Where(x => x.Type == "expense" && !x.Code.StartsWith('5')).Sum(x => x.Amount);
            return (object)new
            {
                from = f.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), to = e.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), branchId,
                revenue = Rows(x => x.Type == "revenue", -1), costOfSales = Rows(x => x.Type == "expense" && x.Code.StartsWith('5'), 1),
                expenses = Rows(x => x.Type == "expense" && !x.Code.StartsWith('5'), 1),
                totalRevenue = revenue, totalCostOfSales = cost, grossProfit = revenue - cost, totalExpenses = expenses, netProfit = revenue - cost - expenses,
                asOf = await AsOfAsync(t),
            };
        });
    }

    /// <summary>
    /// The balance sheet at a date (17.7). Consolidated, the interbranch accounts (1310 due from branches, 2500 due to branches) are eliminated against
    /// each other; any difference between them shows. The year's result so far, and earlier years' result not yet closed, are shown in equity.
    /// </summary>
    public static async Task<object> BalanceSheetAsync(DbTx t, DateOnly? asOf, Guid? branchId)
    {
        var biz = await BusinessInfo.LoadAsync(t);
        var day = asOf ?? BusinessDay.Today(biz.TimeZone, t.Clock);
        var ys = await YearStartAsync(t, biz.Id, day);
        return await t.WithAllBranchesAsync(async () =>
        {
            var b = await BalancesAsync(t, null, day, branchId, includeYearEnd: true);
            var consolidated = branchId is null;
            var dueFrom = b.Where(x => x.Code == "1310").Sum(x => x.Amount);
            var dueTo = -b.Where(x => x.Code == "2500").Sum(x => x.Amount);
            var elimination = consolidated ? Math.Min(dueFrom, dueTo) : 0m;
            var assets = b.Where(x => x.Type == "asset").Select(x => new { x.Code, nameEn = x.NameEn, nameAr = x.NameAr, amount = x.Amount }).ToList();
            var liabilities = b.Where(x => x.Type == "liability").Select(x => new { x.Code, nameEn = x.NameEn, nameAr = x.NameAr, amount = -x.Amount }).ToList();
            var equity = b.Where(x => x.Type == "equity").Select(x => new { x.Code, nameEn = x.NameEn, nameAr = x.NameAr, amount = -x.Amount }).ToList();
            var yearResult = -(await BalancesAsync(t, ys, day, branchId, includeYearEnd: false, "revenue,expense")).Sum(x => x.Amount);
            var allPl = -b.Where(x => x.Type is "revenue" or "expense").Sum(x => x.Amount);
            var earlier = allPl - yearResult;
            var totalAssets = assets.Sum(x => x.amount) - elimination;
            var totalLiabilities = liabilities.Sum(x => x.amount) - elimination;
            var totalEquity = equity.Sum(x => x.amount) + yearResult + earlier;
            return (object)new
            {
                asOf = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), branchId, assets, liabilities, equity,
                yearResult, earlierResult = earlier, elimination = new { dueFromBranches = dueFrom, dueToBranches = dueTo, eliminated = elimination, difference = dueFrom - dueTo },
                totalAssets, totalLiabilities, totalEquity, balanced = totalAssets == totalLiabilities + totalEquity,
                branches = await AsOfAsync(t),
            };
        });
    }

    /// <summary>
    /// The cash flow statement by the indirect method (17.7): the profit, then each balance sheet account's movement by its cash-flow class
    /// (operating, investing, financing), adding up to the movement of the cash accounts. The year-end close is not a cash movement and is left out.
    /// </summary>
    public static async Task<object> CashFlowAsync(DbTx t, DateOnly? from, DateOnly? to, Guid? branchId)
    {
        var biz = await BusinessInfo.LoadAsync(t);
        var (f, e) = Range(from, to, biz, t.Clock);
        return await t.WithAllBranchesAsync(async () =>
        {
            var b = await BalancesAsync(t, f, e, branchId, includeYearEnd: false);
            var profit = -b.Where(x => x.Type is "revenue" or "expense").Sum(x => x.Amount);
            object Section(string cls) => b.Where(x => x.Type is not ("revenue" or "expense") && x.Cff == cls)
                .Select(x => new { x.Code, nameEn = x.NameEn, nameAr = x.NameAr, amount = -x.Amount }).ToList();
            decimal Total(string cls) => -b.Where(x => x.Type is not ("revenue" or "expense") && x.Cff == cls).Sum(x => x.Amount);
            var unclassified = -b.Where(x => x.Type is not ("revenue" or "expense") && x.Cff == "none").Sum(x => x.Amount);
            var operating = profit + Total("operating") + unclassified;
            var investing = Total("investing");
            var financing = Total("financing");
            var cashMove = b.Where(x => x.Type is not ("revenue" or "expense") && x.Cff == "cash").Sum(x => x.Amount);
            var openingCash = (await BalancesAsync(t, null, f.AddDays(-1), branchId, includeYearEnd: true)).Where(x => x.Cff == "cash").Sum(x => x.Amount);
            return (object)new
            {
                from = f.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), to = e.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), branchId,
                netProfit = profit, operatingChanges = Section("operating"), operating, investingItems = Section("investing"), investing,
                financingItems = Section("financing"), financing, netChange = operating + investing + financing,
                openingCash, closingCash = openingCash + cashMove, cashMovement = cashMove, difference = operating + investing + financing - cashMove,
                asOf = await AsOfAsync(t),
            };
        });
    }

    // ================================================================ expenses and card settlements (17.3)
    public static async Task<object> ExpenseAsync(DbTx t, ExpenseInput r, Guid uid, System.Security.Claims.ClaimsPrincipal user)
    {
        if (await t.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM exp.expense WHERE id = @Id)", new { r.Id })) return await ExpenseDetailAsync(t, r.Id);
        user.Demand(Permissions.ExpCreate, r.BranchId);
        if (Clean(r.Description) is null) throw new KiteException(ErrorCodes.Validation, "Describe the expense.", 400, "description");
        if (r.NetAmount <= 0 || r.NetAmount != decimal.Round(r.NetAmount, 3)) throw new KiteException(ErrorCodes.Validation, "The amount is above zero, in baisa.", 400, "netAmount");
        if (r.PaidBy is not ("cash" or "bank_transfer" or "cheque")) throw new KiteException(ErrorCodes.Validation, "An expense is paid in cash, by transfer or by cheque.", 400, "paidBy");
        var a = await t.QuerySingleOrDefaultAsync<(string Code, string Type, string Cff, bool Postable, string Status)>(
            "SELECT code, type, cash_flow_class, is_postable, status FROM acct.account WHERE id = @AccountId", new { r.AccountId });
        if (a.Code is null || !a.Postable || a.Status != "active" || !(a.Type == "expense" || a.Cff == "investing"))
            throw new KiteException(ErrorCodes.Validation, "An expense goes to an active expense account or a fixed asset account.", 400, "accountId");
        if (r.SupplierId is { } sid && await SupplierCore.LoadAsync(t, sid) is null) throw new KiteException(ErrorCodes.Validation, "That supplier was not found.", 400, "supplierId");
        var biz = await BusinessInfo.LoadAsync(t);
        var date = r.ExpenseDate ?? BusinessDay.Today(biz.TimeZone, t.Clock);
        var rate = await PurchCore.RateOnAsync(t, r.TaxCodeId, date);
        var tax = PurchaseMath.LineTax(r.NetAmount, rate);
        var number = await DocNumbers.NextAsync(t, biz.Id, r.BranchId, "expense", await FiscalPeriods.FiscalYearOfAsync(t, biz.Id, date), "EX");
        var facts = new PostingFacts().Add("net_by_account", a.Code, r.NetAmount).Add("paid_by_method", r.PaidBy, r.NetAmount + tax);
        if (tax != 0) facts.Set("vat", tax);
        // The expense row is immutable: its journal is written first.
        var journal = await Posting.PostAsync(t, biz.Id, r.BranchId, PostingRuleTemplates.Expense, facts, date, "expense", r.Id, $"Expense {number}: {r.Description.Trim()}");
        await t.ExecuteAsync("""
            INSERT INTO exp.expense (id, business_id, branch_id, number, expense_date, account_id, description, supplier_id, payee, reference, net_amount, tax_code_id, tax_rate,
                                     tax_amount, gross_amount, paid_by, journal_id, created_by, origin_server_id)
            VALUES (@Id, @b, @BranchId, @number, @d::date, @AccountId, @desc, @SupplierId, @payee, @reference, @NetAmount, @TaxCodeId, @rate, @tax, @gross, @PaidBy, @journal, @uid,
                    (SELECT id FROM core.server WHERE is_self))
            """, new { r.Id, b = biz.Id, r.BranchId, number, d = D(date), r.AccountId, desc = r.Description.Trim(), r.SupplierId, payee = Clean(r.Payee), reference = Clean(r.Reference),
                       r.NetAmount, r.TaxCodeId, rate, tax, gross = r.NetAmount + tax, r.PaidBy, journal, uid });
        await t.AuditAsync(new AuditEntry("exp.expense_posted", "expense", r.Id, After: Json.Obj(new { number, account = a.Code, r.NetAmount, tax, r.PaidBy }), BranchId: r.BranchId));
        return await ExpenseDetailAsync(t, r.Id);
    }

    public static async Task<object> ExpenseDetailAsync(DbTx t, Guid id) => (await t.WithAllBranchesAsync(() => t.RowsAsync("""
        SELECT e.id, e.number, e.expense_date::text AS "expenseDate", a.code AS "accountCode", a.name_en AS "accountNameEn", a.name_ar AS "accountNameAr", e.description,
               s.name_en AS "supplierNameEn", e.payee, e.reference, e.net_amount AS "netAmount", e.tax_amount AS "taxAmount", e.gross_amount AS "grossAmount", e.paid_by AS "paidBy",
               b.code AS "branchCode", u.display_name_en AS "createdBy", e.created_at AS "createdAt", (SELECT j.number FROM acct.journal j WHERE j.id = e.journal_id) AS "journalNumber"
          FROM exp.expense e JOIN acct.account a ON a.id = e.account_id JOIN core.branch b ON b.id = e.branch_id LEFT JOIN purch.supplier s ON s.id = e.supplier_id
          LEFT JOIN iam."user" u ON u.id = e.created_by WHERE e.id = @id
        """, new { id }))).SingleOrDefault() ?? throw new KiteException(ErrorCodes.NotFound, "That expense was not found.", 404);

    public static async Task<object> CardSettlementAsync(DbTx t, BankCardSettlementInput r, Guid uid, System.Security.Claims.ClaimsPrincipal user)
    {
        if (await t.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM acct.card_settlement WHERE id = @Id)", new { r.Id }))
            return (await t.RowsAsync("SELECT id, number FROM acct.card_settlement WHERE id = @Id", new { r.Id })).Single();
        user.Demand(Permissions.AcctCardSettle, r.BranchId);
        if (r.GrossAmount <= 0 || r.GrossAmount != decimal.Round(r.GrossAmount, 3)) throw new KiteException(ErrorCodes.Validation, "The settled amount is above zero, in baisa.", 400, "grossAmount");
        if (r.Fees < 0 || r.Fees > r.GrossAmount || r.Fees != decimal.Round(r.Fees, 3)) throw new KiteException(ErrorCodes.Validation, "The fees are zero or more, and no more than the amount.", 400, "fees");
        if (Clean(r.Reference) is null) throw new KiteException(ErrorCodes.Validation, "Give the bank's settlement reference.", 400, "reference");
        var biz = await BusinessInfo.LoadAsync(t);
        var date = r.SettlementDate ?? BusinessDay.Today(biz.TimeZone, t.Clock);
        var clearing = await t.ExecuteScalarAsync<decimal>("""
            SELECT coalesce(sum(jl.debit - jl.credit), 0) FROM acct.journal_line jl JOIN acct.account a ON a.id = jl.account_id WHERE a.code = '1150' AND jl.branch_id = @BranchId
            """, new { r.BranchId });
        if (r.GrossAmount > clearing)
            throw new KiteException(ErrorCodes.CardSettlementTooMuch, $"Card clearing at this branch holds {clearing:0.000}; the bank cannot settle {r.GrossAmount:0.000}.", 409, "grossAmount");
        var number = await DocNumbers.NextAsync(t, biz.Id, r.BranchId, "card_settlement", await FiscalPeriods.FiscalYearOfAsync(t, biz.Id, date), "CS");
        var journal = await Posting.PostAsync(t, biz.Id, r.BranchId, PostingRuleTemplates.CardSettlement,
            new PostingFacts().Set("gross", r.GrossAmount).Set("fees", r.Fees).Set("net", r.GrossAmount - r.Fees), date, "card_settlement", r.Id, $"Card settlement {number} ({r.Reference.Trim()})");
        await t.ExecuteAsync("""
            INSERT INTO acct.card_settlement (id, business_id, branch_id, number, settlement_date, gross_amount, fees, net_amount, reference, journal_id, created_by, origin_server_id)
            VALUES (@Id, @b, @BranchId, @number, @d::date, @GrossAmount, @Fees, @net, @reference, @journal, @uid, (SELECT id FROM core.server WHERE is_self))
            """, new { r.Id, b = biz.Id, r.BranchId, number, d = D(date), r.GrossAmount, r.Fees, net = r.GrossAmount - r.Fees, reference = r.Reference.Trim(), journal, uid });
        await t.AuditAsync(new AuditEntry("acct.card_settled", "card_settlement", r.Id, After: Json.Obj(new { number, r.GrossAmount, r.Fees }), BranchId: r.BranchId));
        return new { id = r.Id, number };
    }

    // ================================================================ VAT return (17.4)
    private sealed record VatLine(Guid BranchId, string Direction, string TaxKind, string? AccountClass, decimal Net, decimal Vat);

    private static async Task<List<VatLine>> VatLinesAsync(DbTx t, Guid businessId, DateOnly from, DateOnly to) =>
        (await t.WithAllBranchesAsync(() => t.QueryAsync<VatLine>("""
            SELECT branch_id AS BranchId, direction, tax_kind AS TaxKind, account_class AS AccountClass, net, vat FROM tax.vat_lines(@businessId, @from::date, @to::date)
            """, new { businessId, from = D(from), to = D(to) }))).ToList();

    private sealed class Box
    {
        public string Code { get; set; } = "";
        public short Sort { get; set; }
        public string NameEn { get; set; } = "";
        public string NameAr { get; set; } = "";
        public string Source { get; set; } = "";
        public string[]? TaxKinds { get; set; }
        public string? AccountClass { get; set; }
        public string? Formula { get; set; }
        public bool NeedsAccountantReview { get; set; }
    }

    /// <summary>Each box's value (before VAT) and VAT from the period's VAT lines, the manual boxes from the adjustments, the totals by formula.</summary>
    private static async Task<(Dictionary<string, VatFigure> Boxes, List<Box> Defs, List<VatLine> Lines)> ComputeVatAsync(DbTx t, Guid businessId, DateOnly from, DateOnly to,
        IReadOnlyDictionary<string, VatFigure> adjustments)
    {
        var defs = (await t.QueryAsync<Box>("""
            SELECT code, sort, name_en AS NameEn, name_ar AS NameAr, source, tax_kinds AS TaxKinds, account_class AS AccountClass, formula, needs_accountant_review AS NeedsAccountantReview
              FROM tax.vat_box ORDER BY sort
            """)).ToList();
        var lines = await VatLinesAsync(t, businessId, from, to);
        var boxes = new Dictionary<string, VatFigure>(StringComparer.Ordinal);
        foreach (var b in defs)
            boxes[b.Code] = b.Source switch
            {
                "output" or "input" => lines.Where(l => l.Direction == b.Source && b.TaxKinds!.Contains(l.TaxKind) && (b.AccountClass is null || (l.AccountClass ?? "other") == b.AccountClass))
                    .Aggregate(new VatFigure(0, 0), (s, l) => new VatFigure(s.Value + l.Net, s.Vat + l.Vat)),
                "manual" => adjustments.TryGetValue(b.Code, out var a) ? a : new VatFigure(0, 0),
                _ => new VatFigure(0, 0),
            };
        foreach (var b in defs.Where(d => d.Source == "formula"))
        {
            decimal value = 0, vat = 0;
            foreach (var (sign, code) in Terms(b.Formula!))
                if (boxes.TryGetValue(code, out var x)) { value += sign * x.Value; vat += sign * x.Vat; }
            boxes[b.Code] = new VatFigure(value, vat);
        }
        return (boxes, defs, lines);
    }

    private static IEnumerable<(int Sign, string Code)> Terms(string formula)
    {
        var sign = 1;
        var cur = new System.Text.StringBuilder();
        foreach (var ch in formula.Replace(" ", "", StringComparison.Ordinal) + "+")
        {
            if (ch is '+' or '-')
            {
                if (cur.Length > 0) yield return (sign, cur.ToString());
                cur.Clear();
                sign = ch == '+' ? 1 : -1;
            }
            else cur.Append(ch);
        }
    }

    /// <summary>The VAT accounts' movements over the dates, by branch, the settlement journals left out (the "VAT report vs VAT accounts" check).</summary>
    private static async Task<List<(Guid Branch, decimal Output, decimal Input)>> VatAccountsAsync(DbTx t, DateOnly from, DateOnly to) =>
        (await t.WithAllBranchesAsync(() => t.QueryAsync<(Guid, decimal, decimal)>("""
            SELECT jl.branch_id, coalesce(sum(jl.credit - jl.debit) FILTER (WHERE a.code = '2200'), 0), coalesce(sum(jl.debit - jl.credit) FILTER (WHERE a.code = '1500'), 0)
              FROM acct.journal_line jl JOIN acct.journal j ON j.id = jl.journal_id JOIN acct.account a ON a.id = jl.account_id
             WHERE a.code IN ('2200','1500') AND j.journal_date BETWEEN @from AND @to AND j.posting_kind NOT IN ('vat.settlement')
             GROUP BY jl.branch_id
            """, new { from = D(from), to = D(to) }))).ToList();

    public static async Task<object> VatPeriodsAsync(DbTx t)
    {
        var biz = await BusinessInfo.LoadAsync(t);
        var p = await PolicyAsync(t);
        var today = BusinessDay.Today(biz.TimeZone, t.Clock);
        // Periods of vat_period_months starting from vat_first_month: the current one and the four before it.
        var monthIndex = (today.Year * 12 + today.Month - 1) - (p.VatFirst - 1);
        var startIndex = monthIndex - ((monthIndex % p.VatMonths) + p.VatMonths) % p.VatMonths + (p.VatFirst - 1);
        var list = new List<object>();
        for (var k = 0; k < 5; k++)
        {
            var s = startIndex - k * p.VatMonths;
            var from = new DateOnly(s / 12, s % 12 + 1, 1);
            var to = from.AddMonths(p.VatMonths).AddDays(-1);
            list.Add(new { from = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), to = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });
        }
        return new { months = p.VatMonths, periods = list };
    }

    public static async Task<object> PrepareVatAsync(DbTx t, VatReturnRequest r, Guid uid)
    {
        if (r.PeriodTo < r.PeriodFrom) throw new KiteException(ErrorCodes.Validation, "The period ends on or after it starts.", 400, "periodTo");
        var biz = await BusinessInfo.LoadAsync(t);
        return await t.WithAllBranchesAsync(async () =>
        {
            var existing = await t.QuerySingleOrDefaultAsync<(Guid Id, string Status, string Adjustments)>(
                "SELECT id, status, adjustments::text FROM tax.vat_return WHERE period_from = @f FOR UPDATE", new { f = D(r.PeriodFrom) });
            if (existing.Id != Guid.Empty && existing.Status == "filed") return await VatReturnAsync(t, existing.Id);
            var adjustments = existing.Id == Guid.Empty ? new Dictionary<string, VatFigure>() :
                System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, VatFigure>>(existing.Adjustments, Json.Web) ?? [];
            var (boxes, _, _) = await ComputeVatAsync(t, biz.Id, r.PeriodFrom, r.PeriodTo, adjustments);
            var output = boxes.GetValueOrDefault("5a")?.Vat ?? 0;
            var input = boxes.GetValueOrDefault("6e")?.Vat ?? 0;
            var net = boxes.GetValueOrDefault("7")?.Vat ?? 0;
            var json = System.Text.Json.JsonSerializer.Serialize(boxes, Json.Web);
            Guid id;
            if (existing.Id == Guid.Empty)
            {
                id = t.NewId();
                var number = $"VAT-{r.PeriodFrom:yyyy-MM}";   // one return per period start (vat_return_period)
                await t.ExecuteAsync("""
                    INSERT INTO tax.vat_return (id, business_id, number, period_from, period_to, boxes, output_vat, input_vat, net_vat, created_by)
                    VALUES (@id, @b, @number, @f, @to, @json::jsonb, @output, @input, @net, @uid)
                    """, new { id, b = biz.Id, number, f = D(r.PeriodFrom), to = D(r.PeriodTo), json, output, input, net, uid });
            }
            else
            {
                id = existing.Id;
                await t.ExecuteAsync("""
                    UPDATE tax.vat_return SET period_to = @to, boxes = @json::jsonb, output_vat = @output, input_vat = @input, net_vat = @net, updated_by = @uid WHERE id = @id
                    """, new { id, to = D(r.PeriodTo), json, output, input, net, uid });
            }
            return await VatReturnAsync(t, id);
        });
    }

    public static async Task<object> VatAdjustAsync(DbTx t, Guid id, VatAdjustmentsRequest r, Guid uid)
    {
        var v = await t.QuerySingleOrDefaultAsync<(string Status, int Version, DateTime From, DateTime To)>(
            "SELECT status, version, period_from, period_to FROM tax.vat_return WHERE id = @id FOR UPDATE", new { id });
        if (v.Status is null) throw new KiteException(ErrorCodes.NotFound, "That return was not found.", 404);
        if (v.Status == "filed") throw new KiteException(ErrorCodes.VatReturnState, "A filed return is not changed.", 409);
        if (v.Version != r.Version) throw new KiteException(ErrorCodes.VersionMismatch, "Someone changed this return; open it again.", 412);
        var manual = (await t.QueryAsync<string>("SELECT code FROM tax.vat_box WHERE source = 'manual'")).ToHashSet(StringComparer.Ordinal);
        var adj = new Dictionary<string, VatFigure>(StringComparer.Ordinal);
        foreach (var (code, fig) in r.Boxes ?? [])
        {
            if (!manual.Contains(code)) throw new KiteException(ErrorCodes.Validation, $"Box {code} is worked out from the books; only the manual boxes are entered.", 400, "boxes");
            if (fig.Value != decimal.Round(fig.Value, 3) || fig.Vat != decimal.Round(fig.Vat, 3)) throw new KiteException(ErrorCodes.Validation, "Amounts are in baisa.", 400, "boxes");
            if (fig.Value != 0 || fig.Vat != 0) adj[code] = fig;
        }
        await t.ExecuteAsync("UPDATE tax.vat_return SET adjustments = @json::jsonb, refund_requested = @RefundRequested, updated_by = @uid WHERE id = @id",
            new { id, json = System.Text.Json.JsonSerializer.Serialize(adj, Json.Web), r.RefundRequested, uid });
        return await PrepareVatAsync(t, new VatReturnRequest(DateOnly.FromDateTime(v.From), DateOnly.FromDateTime(v.To)), uid);
    }

    /// <summary>
    /// Files a VAT return (17.4): its months must be closed (soft or hard) at every branch so nothing more lands in them; the boxes are worked out again;
    /// the settlement journal moves each branch's VAT output and input for the period into VAT payable (2210); adjustments (5b, 6d) go to 2210
    /// against general expenses for the accountant to reclassify.
    /// </summary>
    public static async Task<object> FileVatAsync(DbTx t, Guid id, VatFileRequest r, Guid uid)
    {
        var biz = await BusinessInfo.LoadAsync(t);
        return await t.WithAllBranchesAsync(async () =>
        {
            await t.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended('kite:vat_return', 0))");
            var v = await t.QuerySingleOrDefaultAsync<(string Status, DateTime From, DateTime To, string Number, string Adjustments)>(
                "SELECT status, period_from, period_to, number, adjustments::text FROM tax.vat_return WHERE id = @id FOR UPDATE", new { id });
            if (v.Status is null) throw new KiteException(ErrorCodes.NotFound, "That return was not found.", 404);
            if (v.Status == "filed") return await VatReturnAsync(t, id);
            var from = DateOnly.FromDateTime(v.From);
            var to = DateOnly.FromDateTime(v.To);
            var open = await t.QueryAsync<(int Year, short No, string Code)>("""
                SELECT p.fiscal_year, p.period_no, b.code FROM acct.fiscal_period p CROSS JOIN core.branch b
                 WHERE p.branch_id IS NULL AND p.period_no <= 12 AND p.starts_on <= @to AND p.ends_on >= @from AND b.status = 'active'
                   AND acct.period_status(p.id, b.id) = 'open'
                 ORDER BY p.starts_on, b.code
                """, new { from = D(from), to = D(to) });
            if (open.FirstOrDefault() is { Code: not null } o)
                throw new KiteException(ErrorCodes.VatPeriodOpen, $"Period {o.No} of {o.Year} is still open at {o.Code}. Close the return's months at every branch (soft close is enough) so nothing more lands in them.", 409);
            if (await t.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM tax.vat_box WHERE needs_accountant_review)"))
                throw new KiteException(ErrorCodes.VatReturnState, "The accountant confirms the VAT return boxes before the first return is filed (Accounting, VAT, Boxes).", 409);
            var adjustments = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, VatFigure>>(v.Adjustments, Json.Web) ?? [];
            var (boxes, _, lines) = await ComputeVatAsync(t, biz.Id, from, to, adjustments);
            var hoBranch = await t.ExecuteScalarAsync<Guid>("SELECT id FROM core.branch WHERE is_head_office LIMIT 1");
            var parts = new List<(string, Guid, PostingFacts)>();
            foreach (var g in lines.GroupBy(l => l.BranchId))
            {
                var output = g.Where(l => l.Direction == "output").Sum(l => l.Vat);
                var input = g.Where(l => l.Direction == "input").Sum(l => l.Vat);
                if (output == 0 && input == 0) continue;
                parts.Add((PostingRuleTemplates.VatSettlement, g.Key, new PostingFacts().Set("output", output).Set("input", input).Set("payable", output - input)));
            }
            var today = BusinessDay.Today(biz.TimeZone, t.Clock);
            // A nil return has no settlement journal.
            var journal = parts.Count == 0 ? null
                : await Posting.PostManyAsync(t, biz.Id, hoBranch, PostingRuleTemplates.VatSettlement, parts, today, "vat_return", id, $"VAT return {v.Number} ({from:yyyy-MM-dd} to {to:yyyy-MM-dd})");
            var adj = (boxes.GetValueOrDefault("5b")?.Vat ?? 0) - (boxes.GetValueOrDefault("6d")?.Vat ?? 0);
            if (adj != 0)
                await Posting.PostLinesAsync(t, biz.Id, hoBranch, "vat.adjustment",
                    [new Posting.DirectLine("6900", hoBranch, adj > 0 ? adj : 0, adj < 0 ? -adj : 0, "VAT return adjustments"),
                     new Posting.DirectLine("2210", hoBranch, adj < 0 ? -adj : 0, adj > 0 ? adj : 0, "VAT return adjustments")], today, "vat_return", id, $"VAT return {v.Number}: adjustments (5b, 6d)");
            var json = System.Text.Json.JsonSerializer.Serialize(boxes, Json.Web);
            await t.ExecuteAsync("""
                UPDATE tax.vat_return SET status = 'filed', boxes = @json::jsonb, output_vat = @o, input_vat = @i, net_vat = @n, filed_by = @uid, filed_at = now(),
                       filing_reference = @ref, settlement_journal_id = @journal, updated_by = @uid WHERE id = @id
                """, new { id, json, o = boxes.GetValueOrDefault("5a")?.Vat ?? 0, i = boxes.GetValueOrDefault("6e")?.Vat ?? 0, n = boxes.GetValueOrDefault("7")?.Vat ?? 0,
                           uid, @ref = Clean(r.FilingReference), journal });
            await t.AuditAsync(new AuditEntry("tax.vat_return_filed", "vat_return", id, After: Json.Obj(new { v.Number, from, to, net = boxes.GetValueOrDefault("7")?.Vat ?? 0 })));
            return await VatReturnAsync(t, id);
        });
    }

    /// <summary>The VAT of a filed return paid to (or refunded by) the Tax Authority: VAT payable against the bank, branch by branch as settled.</summary>
    public static async Task<object> PayVatAsync(DbTx t, Guid id, VatPayRequest r, Guid uid)
    {
        if (Clean(r.Reference) is null) throw new KiteException(ErrorCodes.Validation, "Give the payment reference.", 400, "reference");
        var biz = await BusinessInfo.LoadAsync(t);
        return await t.WithAllBranchesAsync(async () =>
        {
            var v = await t.QuerySingleOrDefaultAsync<(string Status, string Number, Guid? Paid, Guid? Settlement)>(
                "SELECT status, number, paid_journal_id, settlement_journal_id FROM tax.vat_return WHERE id = @id FOR UPDATE", new { id });
            if (v.Status is null) throw new KiteException(ErrorCodes.NotFound, "That return was not found.", 404);
            if (v.Status != "filed") throw new KiteException(ErrorCodes.VatReturnState, "File the return first.", 409);
            if (v.Paid is not null) return await VatReturnAsync(t, id);
            var owed = (await t.QueryAsync<(Guid Branch, decimal Amount)>("""
                SELECT jl.branch_id, sum(jl.credit - jl.debit) FROM acct.journal_line jl JOIN acct.journal j ON j.id = jl.journal_id JOIN acct.account a ON a.id = jl.account_id
                 WHERE a.code = '2210' AND j.source_doc_type = 'vat_return' AND j.source_doc_id = @id GROUP BY jl.branch_id HAVING sum(jl.credit - jl.debit) <> 0
                """, new { id })).ToList();
            var hoBranch = await t.ExecuteScalarAsync<Guid>("SELECT id FROM core.branch WHERE is_head_office LIMIT 1");
            var date = r.Date ?? BusinessDay.Today(biz.TimeZone, t.Clock);
            Guid? journal = owed.Count == 0 ? null : await Posting.PostManyAsync(t, biz.Id, hoBranch, PostingRuleTemplates.VatPayment,
                owed.Select(o => (PostingRuleTemplates.VatPayment, o.Branch, new PostingFacts().Set("amount", o.Amount))).ToList(), date, "vat_payment", id,
                $"VAT return {v.Number} paid ({r.Reference.Trim()})");
            await t.ExecuteAsync("UPDATE tax.vat_return SET paid_journal_id = @journal, paid_at = now(), updated_by = @uid WHERE id = @id", new { id, journal, uid });
            await t.AuditAsync(new AuditEntry("tax.vat_paid", "vat_return", id, After: Json.Obj(new { v.Number, amount = owed.Sum(o => o.Amount), reference = r.Reference.Trim() })));
            return await VatReturnAsync(t, id);
        });
    }

    public static async Task<object> VatReturnAsync(DbTx t, Guid id) => await t.WithAllBranchesAsync(async () =>
    {
        var head = (await t.RowsAsync("""
            SELECT v.id, v.number, v.period_from::text AS "periodFrom", v.period_to::text AS "periodTo", v.status, v.boxes::text AS boxes, v.adjustments::text AS adjustments,
                   v.output_vat AS "outputVat", v.input_vat AS "inputVat", v.net_vat AS "netVat", v.refund_requested AS "refundRequested", v.filed_at AS "filedAt",
                   fu.display_name_en AS "filedBy", v.filing_reference AS "filingReference", sj.number AS "settlementNumber", pj.number AS "paymentNumber", v.paid_at AS "paidAt", v.version
              FROM tax.vat_return v LEFT JOIN iam."user" fu ON fu.id = v.filed_by LEFT JOIN acct.journal sj ON sj.id = v.settlement_journal_id
              LEFT JOIN acct.journal pj ON pj.id = v.paid_journal_id WHERE v.id = @id
            """, new { id })).SingleOrDefault() ?? throw new KiteException(ErrorCodes.NotFound, "That return was not found.", 404);
        head["boxes"] = System.Text.Json.JsonDocument.Parse((string)head["boxes"]!).RootElement.Clone();
        head["adjustments"] = System.Text.Json.JsonDocument.Parse((string)head["adjustments"]!).RootElement.Clone();
        var from = DateOnly.Parse((string)head["periodFrom"]!, CultureInfo.InvariantCulture);
        var to = DateOnly.Parse((string)head["periodTo"]!, CultureInfo.InvariantCulture);
        var biz = await BusinessInfo.LoadAsync(t);
        var defs = await t.RowsAsync("""
            SELECT code, sort, name_en AS "nameEn", name_ar AS "nameAr", source, needs_accountant_review AS "needsReview" FROM tax.vat_box ORDER BY sort
            """);
        var lines = await VatLinesAsync(t, biz.Id, from, to);
        var accounts = await VatAccountsAsync(t, from, to);
        var branches = (await BranchesAsync(t)).ToDictionary(b => (Guid)b["id"]!, b => (string)b["code"]!);
        var check = lines.Select(l => l.BranchId).Union(accounts.Select(a => a.Branch)).Distinct().Select(b => new
        {
            branchCode = branches.GetValueOrDefault(b, "?"),
            output = lines.Where(l => l.BranchId == b && l.Direction == "output").Sum(l => l.Vat),
            outputAccount = accounts.Where(a => a.Branch == b).Sum(a => a.Output),
            input = lines.Where(l => l.BranchId == b && l.Direction == "input").Sum(l => l.Vat),
            inputAccount = accounts.Where(a => a.Branch == b).Sum(a => a.Input),
        }).OrderBy(x => x.branchCode).ToList();
        var bySource = lines.GroupBy(l => (l.Direction, l.TaxKind)).Select(g => new { direction = g.Key.Direction, taxKind = g.Key.TaxKind, net = g.Sum(x => x.Net), vat = g.Sum(x => x.Vat) }).ToList();
        return (object)new { vatReturn = head, boxes = defs, check, agrees = check.All(c => c.output == c.outputAccount && c.input == c.inputAccount), bySource };
    });

    public static async Task<IReadOnlyList<Dictionary<string, object?>>> VatBoxesAsync(DbTx t) => await t.RowsAsync("""
        SELECT b.id, b.code, b.sort, b.name_en AS "nameEn", b.name_ar AS "nameAr", b.source, b.tax_kinds AS "taxKinds", b.account_class AS "accountClass", b.formula,
               b.needs_accountant_review AS "needsReview", u.display_name_en AS "reviewedBy", b.reviewed_at AS "reviewedAt"
          FROM tax.vat_box b LEFT JOIN iam."user" u ON u.id = b.reviewed_by ORDER BY b.sort
        """);

    public static async Task ReviewVatBoxesAsync(DbTx t, Guid uid)
    {
        var n = await t.ExecuteAsync("UPDATE tax.vat_box SET needs_accountant_review = false, reviewed_by = @uid, reviewed_at = now() WHERE needs_accountant_review", new { uid });
        await t.AuditAsync(new AuditEntry("tax.vat_boxes_reviewed", "vat_box", null, After: Json.Obj(new { boxes = n })));
    }
}
