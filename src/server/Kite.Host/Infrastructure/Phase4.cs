using Kite.Accounting;
using Kite.Persistence;
using Kite.Platform;

namespace Kite.Host.Infrastructure;

/// <summary>Gap-free document numbers per branch and fiscal year (9.4); a missing year's series is created on first use.</summary>
public static class DocNumbers
{
    public static Task<string> NextAsync(DbTx t, Guid businessId, Guid? branchId, string docType, int fiscalYear, string prefix) =>
        branchId is null ? t.WithAllBranchesAsync(() => NextCoreAsync(t, businessId, null, docType, fiscalYear, prefix))
                         : NextCoreAsync(t, businessId, branchId, docType, fiscalYear, prefix);

    private static async Task<string> NextCoreAsync(DbTx t, Guid businessId, Guid? branchId, string docType, int fiscalYear, string prefix)
    {
        var exists = await t.ExecuteScalarAsync<bool>("""
            SELECT EXISTS (SELECT 1 FROM core.number_series WHERE business_id = @businessId AND branch_id IS NOT DISTINCT FROM @branchId
                                 AND terminal_id IS NULL AND doc_type = @docType AND fiscal_year = @fiscalYear)
            """, new { businessId, branchId, docType, fiscalYear });
        if (!exists)
        {
            var code = branchId is null ? null : await t.ExecuteScalarAsync<string>("SELECT code FROM core.branch WHERE id = @branchId", new { branchId });
            await t.ExecuteAsync("""
                INSERT INTO core.number_series (id, business_id, branch_id, doc_type, prefix, fiscal_year)
                VALUES (@id, @businessId, @branchId, @docType, @p, @fiscalYear)
                ON CONFLICT DO NOTHING
                """, new { id = t.NewId(), businessId, branchId, docType, fiscalYear, p = code is null ? $"{prefix}-" : $"{prefix}-{code}-{fiscalYear}-" });
        }
        return await t.ExecuteScalarAsync<string>("SELECT core.next_document_number(@businessId, @branchId, NULL, @docType, @fiscalYear)",
            new { businessId, branchId, docType, fiscalYear });
    }
}

public sealed record PeriodRef(Guid Id, int FiscalYear, DateOnly StartsOn, DateOnly EndsOn, string Status);

/// <summary>Fiscal periods: found for a date, created for a new fiscal year, and a late document goes to the first open period (17.5).</summary>
public static class FiscalPeriods
{
    public static async Task<PeriodRef> ForPostingAsync(DbTx t, Guid businessId, DateOnly date)
    {
        var p = await FindAsync(t, businessId, date);
        if (p is null)
        {
            await EnsureYearAsync(t, businessId, date);
            p = await FindAsync(t, businessId, date) ?? throw new KiteException(ErrorCodes.Internal, $"No fiscal period for {date:yyyy-MM-dd}.", 500);
        }
        if (p.Status == "open") return p;
        var next = await t.QuerySingleOrDefaultAsync<PeriodRow>("""
            SELECT id, fiscal_year AS FiscalYear, starts_on AS StartsOn, ends_on AS EndsOn, status FROM acct.fiscal_period
             WHERE business_id = @businessId AND branch_id IS NULL AND status = 'open' AND starts_on > @d
             ORDER BY starts_on LIMIT 1
            """, new { businessId, d = date.ToDateTime(TimeOnly.MinValue) });
        if (next is null)
        {
            await EnsureYearAsync(t, businessId, p.EndsOn.AddDays(1));
            return await ForPostingAsync(t, businessId, p.EndsOn.AddDays(1));
        }
        return next.ToRef();
    }

    public static async Task<int> FiscalYearOfAsync(DbTx t, Guid businessId, DateOnly date)
    {
        var p = await FindAsync(t, businessId, date);
        if (p is not null) return p.FiscalYear;
        var start = await t.ExecuteScalarAsync<short>("SELECT fiscal_year_start_month FROM core.business WHERE id = @businessId", new { businessId });
        return FiscalCalendar.YearContaining(date, start)[0].FiscalYear;
    }

    private sealed record PeriodRow(Guid Id, int FiscalYear, DateTime StartsOn, DateTime EndsOn, string Status)
    {
        public PeriodRef ToRef() => new(Id, FiscalYear, DateOnly.FromDateTime(StartsOn), DateOnly.FromDateTime(EndsOn), Status);
    }

    private static async Task<PeriodRef?> FindAsync(DbTx t, Guid businessId, DateOnly date) =>
        (await t.QuerySingleOrDefaultAsync<PeriodRow>("""
            SELECT id, fiscal_year AS FiscalYear, starts_on AS StartsOn, ends_on AS EndsOn, status FROM acct.fiscal_period
             WHERE business_id = @businessId AND branch_id IS NULL AND @d BETWEEN starts_on AND ends_on
            """, new { businessId, d = date.ToDateTime(TimeOnly.MinValue) }))?.ToRef();

    private static Task EnsureYearAsync(DbTx t, Guid businessId, DateOnly date) =>
        t.WithAllBranchesAsync(async () => { await EnsureYearCoreAsync(t, businessId, date); return true; });

    private static async Task EnsureYearCoreAsync(DbTx t, Guid businessId, DateOnly date)
    {
        var start = await t.ExecuteScalarAsync<short>("SELECT fiscal_year_start_month FROM core.business WHERE id = @businessId", new { businessId });
        foreach (var p in FiscalCalendar.YearContaining(date, start))
            await t.ExecuteAsync("""
                INSERT INTO acct.fiscal_period (id, business_id, branch_id, fiscal_year, period_no, starts_on, ends_on)
                VALUES (@id, @businessId, NULL, @FiscalYear, @PeriodNo, @s::date, @e::date)
                ON CONFLICT ON CONSTRAINT fiscal_period_unique DO NOTHING
                """, new { id = t.NewId(), businessId, p.FiscalYear, p.PeriodNo, s = p.StartsOn.ToDateTime(TimeOnly.MinValue), e = p.EndsOn.ToDateTime(TimeOnly.MinValue) });
    }
}

/// <summary>Writes journals from the posting rules held as data (17.1), in the caller's transaction.</summary>
public static class Posting
{
    private sealed record RuleRow(string Definition);

    /// <summary>
    /// Inserts any default rule the business does not have yet (version 1, effective from 2000-01-01), and upgrades a
    /// rule that is still exactly an older default to the current default as a new version. A rule the business has
    /// changed is never touched.
    /// </summary>
    public static async Task EnsureDefaultRulesAsync(DbTx t, Guid businessId)
    {
        foreach (var (eventType, def) in PostingRuleTemplates.All)
        {
            await t.ExecuteAsync("""
                INSERT INTO acct.posting_rule (id, business_id, event_type, rule_version, effective_from, definition)
                SELECT @id, @businessId, @eventType, 1, DATE '2000-01-01', @def::jsonb
                 WHERE NOT EXISTS (SELECT 1 FROM acct.posting_rule WHERE business_id = @businessId AND event_type = @eventType)
                """, new { id = t.NewId(), businessId, eventType, def = def.ToJson() });
            // A rule still exactly one of the earlier defaults is upgraded; a rule the business changed is left alone.
            var olds = PostingRuleTemplates.Earlier.SelectMany(v => v).Where(r => r.EventType == eventType).Select(r => r.Definition.ToJson()).Distinct().ToArray();
            if (olds.Length == 0) continue;
            await t.ExecuteAsync("""
                INSERT INTO acct.posting_rule (id, business_id, event_type, rule_version, effective_from, definition)
                SELECT @id, @businessId, @eventType, r.rule_version + 1, r.effective_from, @def::jsonb
                  FROM (SELECT rule_version, effective_from, definition FROM acct.posting_rule
                         WHERE business_id = @businessId AND event_type = @eventType
                         ORDER BY effective_from DESC, rule_version DESC LIMIT 1) r
                 WHERE r.definition = ANY(@olds::jsonb[]) AND r.definition <> @def::jsonb
                """, new { id = t.NewId(), businessId, eventType, def = def.ToJson(), olds });
        }
    }

    /// <summary>Posts one journal for a document; returns its id, or null when the rule yields no lines.</summary>
    public static async Task<Guid?> PostAsync(DbTx t, Guid businessId, Guid? branchId, string eventType, PostingFacts facts, DateOnly docDate,
        string sourceDocType, Guid sourceDocId, string description, Guid? partyCustomerId = null)
    {
        var json = await t.ExecuteScalarAsync<string?>("""
            SELECT definition::text FROM acct.posting_rule
             WHERE business_id = @businessId AND event_type = @eventType AND effective_from <= @d
             ORDER BY effective_from DESC, rule_version DESC LIMIT 1
            """, new { businessId, eventType, d = docDate.ToDateTime(TimeOnly.MinValue) })
            ?? throw new KiteException(ErrorCodes.Internal, $"No posting rule for {eventType}.", 500);
        var rule = PostingRuleDefinition.Parse(json);
        var period = await FiscalPeriods.ForPostingAsync(t, businessId, docDate);
        var journalDate = period.StartsOn > docDate ? period.StartsOn : docDate;
        var draft = PostingEngine.Build(rule, facts, journalDate, sourceDocType, sourceDocId, eventType, Currency.OMR, branchId);
        if (draft is null) return null;

        var codes = draft.Lines.Select(l => l.AccountCode).Distinct().ToArray();
        var accounts = (await t.QueryAsync<(string Code, Guid Id, bool IsPostable)>(
            "SELECT code, id, is_postable FROM acct.account WHERE business_id = @businessId AND code = ANY(@codes) AND status = 'active'",
            new { businessId, codes })).ToDictionary(a => a.Code, StringComparer.Ordinal);
        foreach (var c in codes)
        {
            if (!accounts.TryGetValue(c, out var a)) throw new KiteException(ErrorCodes.Validation, $"Posting rule {eventType} uses account {c}, which does not exist or is inactive.");
            if (!a.IsPostable) throw new KiteException(ErrorCodes.Validation, $"Posting rule {eventType} uses account {c}, which is a heading, not a postable account.");
        }

        var number = await DocNumbers.NextAsync(t, businessId, branchId, "journal", period.FiscalYear, "JV");
        var journalId = t.NewId();
        var desc = journalDate == docDate ? description : $"{description} (document date {docDate:yyyy-MM-dd}, period was closed)";
        await t.ExecuteAsync("""
            INSERT INTO acct.journal (id, business_id, branch_id, number, journal_date, fiscal_period_id, source_doc_type, source_doc_id,
                                      posting_kind, description, posted_by, origin_server_id, origin_device_id)
            VALUES (@journalId, @businessId, @branchId, @number, @jd::date, @periodId, @sourceDocType, @sourceDocId, @eventType, @desc,
                    @by, (SELECT id FROM core.server WHERE is_self), @device)
            """, new { journalId, businessId, branchId, number, jd = journalDate.ToDateTime(TimeOnly.MinValue), periodId = period.Id,
                       sourceDocType, sourceDocId, eventType, desc, by = t.Actor.UserId, device = t.Actor.TerminalId });
        short no = 0;
        foreach (var l in draft.Lines)
        {
            no++;
            var isReceivable = partyCustomerId is not null && l.AccountCode == "1300";
            await t.ExecuteAsync("""
                INSERT INTO acct.journal_line (id, journal_id, line_no, account_id, branch_id, debit, credit, party_type, party_id, memo)
                VALUES (@id, @journalId, @no, @account, @branchId, @Debit, @Credit, @partyType, @partyId, @Memo)
                """, new { id = t.NewId(), journalId, no, account = accounts[l.AccountCode].Id, branchId, l.Debit, l.Credit,
                           partyType = isReceivable ? "customer" : null, partyId = isReceivable ? partyCustomerId : null, l.Memo });
        }
        return journalId;
    }
}

/// <summary>POS policy (a class: Npgsql reports array columns as System.Array, which positional records cannot take).</summary>
public sealed class PosPolicy
{
    public Guid Id { get; set; }
    public Guid? BranchId { get; set; }
    public string NegativeStock { get; set; } = "block";
    public int ReturnWindowDays { get; set; } = 14;
    public decimal CashRefundCap { get; set; } = 100m;
    public decimal VarianceThreshold { get; set; } = 1m;
    public bool BlindClose { get; set; } = true;
    public string RoundingLevel { get; set; } = "line";
    public decimal CashRounding { get; set; }
    public decimal[] Denominations { get; set; } = [50m, 20m, 10m, 5m, 1m, 0.5m, 0.1m, 0.05m, 0.025m, 0.01m, 0.005m];
    public string? ReceiptFooterEn { get; set; }
    public string? ReceiptFooterAr { get; set; }
    public int Version { get; set; }
}

public static class PosPolicies
{
    public const string Select = """
        SELECT id, branch_id AS BranchId, negative_stock AS NegativeStock, return_window_days AS ReturnWindowDays, cash_refund_cap AS CashRefundCap,
               variance_threshold AS VarianceThreshold, blind_close AS BlindClose, rounding_level AS RoundingLevel, cash_rounding AS CashRounding,
               denominations, receipt_footer_en AS ReceiptFooterEn, receipt_footer_ar AS ReceiptFooterAr, version
          FROM sales.pos_policy
        """;

    /// <summary>The branch's own policy if it has one, else the business default.</summary>
    public static async Task<PosPolicy> ForBranchAsync(DbTx t, Guid businessId, Guid branchId) =>
        await t.QuerySingleOrDefaultAsync<PosPolicy>(Select + " WHERE business_id = @businessId AND (branch_id = @branchId OR branch_id IS NULL) ORDER BY branch_id NULLS LAST LIMIT 1",
            new { businessId, branchId })
        ?? new PosPolicy();
}

/// <summary>Business data Phase 4 needs on every business, created at setup and for businesses set up before Phase 4.</summary>
public static class Phase4Seed
{
    public static async Task EnsureAsync(DbTx t, Guid businessId)
    {
        await Posting.EnsureDefaultRulesAsync(t, businessId);
        await t.ExecuteAsync("INSERT INTO sales.pos_policy (id, business_id) VALUES (@id, @businessId) ON CONFLICT DO NOTHING", new { id = t.NewId(), businessId });
        await t.ExecuteAsync("""
            INSERT INTO catalog.price_list (id, business_id, code, name_en, name_ar, is_default)
            SELECT @id, @businessId, 'RETAIL', 'Retail prices', 'أسعار التجزئة', true
             WHERE NOT EXISTS (SELECT 1 FROM catalog.price_list WHERE business_id = @businessId AND is_default)
            """, new { id = t.NewId(), businessId });
        await t.ExecuteAsync("""
            INSERT INTO core.number_series (id, business_id, branch_id, doc_type, prefix, fiscal_year)
            VALUES (@id, @businessId, NULL, 'customer', 'C-', 2000) ON CONFLICT DO NOTHING
            """, new { id = t.NewId(), businessId });
        await Phase5Seed.EnsureAsync(t, businessId);
    }
}

public static class Phase4Startup
{
    /// <summary>Businesses set up before Phase 4 get the default posting rules, POS policy, price list and customer series.</summary>
    public static async Task RunAsync(WebApplication app)
    {
        var server = app.Services.GetRequiredService<ServerContext>();
        if (!server.IsConfigured) return;
        try
        {
            await app.Services.GetRequiredService<KiteDb>().InTransactionAsync(AccessScope.System, ActorContext.System,
                t => Phase4Seed.EnsureAsync(t, server.BusinessId!.Value));
        }
        catch (Exception e) { app.Logger.LogError(e, "Could not prepare the Phase 4 defaults (posting rules, POS policy, price list)."); }
    }
}
