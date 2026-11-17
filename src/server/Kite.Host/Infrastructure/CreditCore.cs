using System.Text.Json;
using Kite.Accounting;
using Kite.Host.Features;
using Kite.Persistence;
using Kite.Platform;
using Kite.Sales;

namespace Kite.Host.Infrastructure;

/// <summary>The business's credit policy (14.2 to 14.5), kept by head office and copied to branch servers.</summary>
public sealed class CreditPolicy
{
    public int DefaultTermsDays { get; set; } = 30;
    public int BlockOverdueDays { get; set; } = 30;
    public int HoldOverdueDays { get; set; } = 60;
    public decimal OfflineSaleCap { get; set; } = 100m;
    public decimal OverrideCap { get; set; } = 50m;
    public bool AllowOvercommit { get; set; }
    public int OnlineTimeoutMs { get; set; } = 2000;
    public int AuthorisationMinutes { get; set; } = 15;
    public int MaxInstalments { get; set; } = 12;
    public int RemindBeforeDays { get; set; } = 3;
    public int RemindOverdueEvery { get; set; } = 7;
    public int Version { get; set; } = 1;

    public const string Select = """
        SELECT default_terms_days AS DefaultTermsDays, block_overdue_days AS BlockOverdueDays, hold_overdue_days AS HoldOverdueDays,
               offline_sale_cap AS OfflineSaleCap, override_cap AS OverrideCap, allow_overcommit AS AllowOvercommit, online_timeout_ms AS OnlineTimeoutMs,
               authorisation_minutes AS AuthorisationMinutes, max_instalments AS MaxInstalments, remind_before_days AS RemindBeforeDays,
               remind_overdue_every AS RemindOverdueEvery, version
          FROM crm.credit_policy
        """;
}

/// <summary>A customer's credit account (14.1), with head office's last reported exposure.</summary>
public sealed class CreditAccount
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }
    public Guid CustomerId { get; set; }
    public decimal CreditLimit { get; set; }
    public int TermsDays { get; set; }
    public string Status { get; set; } = "active";
    public string? HoldKind { get; set; }
    public string? HoldReason { get; set; }
    public DateTime? HeldAt { get; set; }
    public decimal? OfflineSaleCap { get; set; }
    public string? Note { get; set; }
    public decimal Exposure { get; set; }
    public DateTime? OldestDue { get; set; }
    public DateTime? ExposureAsOf { get; set; }
    public long SyncSeq { get; set; }
    public int Version { get; set; }
    public string CustomerNumber { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public string? CustomerNameAr { get; set; }

    public const string Select = """
        SELECT a.id, a.business_id AS BusinessId, a.customer_id AS CustomerId, a.credit_limit AS CreditLimit, a.terms_days AS TermsDays, a.status,
               a.hold_kind AS HoldKind, a.hold_reason AS HoldReason, a.held_at AS HeldAt, a.offline_sale_cap AS OfflineSaleCap, a.note, a.exposure,
               a.oldest_due AS OldestDue, a.exposure_as_of AS ExposureAsOf, a.sync_seq AS SyncSeq, a.version,
               c.number AS CustomerNumber, c.name_en AS CustomerName, c.name_ar AS CustomerNameAr
          FROM crm.credit_account a JOIN crm.customer c ON c.id = a.customer_id
        """;
}

public sealed record ReceivableStatus(decimal Balance, decimal OpenItems, decimal Unallocated, DateTime? OldestDue);

/// <summary>A credit sale's instalment plan as the till sends it (14.4): how many, and the days between them.</summary>
public sealed record CreditPlanInput(int Instalments = 1, int? IntervalDays = null);

/// <summary>How a credit sale was checked and what was available (stored on the sale as its credit decision, 14.3).</summary>
public sealed record CreditCheck(Guid CustomerId, Guid AccountId, string Mode, decimal Amount, decimal Available, decimal Limit, decimal Exposure,
    decimal? Allowance, decimal OverBy, int OverdueDays, Guid? ApprovalId, Guid? ApprovedBy, int TermsDays, CreditPlanInput? Plan);

/// <summary>Head office's evaluation of a credit sale: the account, the policy, the figures and the decision.</summary>
public sealed record HeadOfficeEvaluation(CreditAccount Account, CreditPolicy Policy, decimal Exposure, decimal Available, int OverdueDays, CreditDecisionResult Decision);

// ------------------------------------------------------------------------------------------------ wire formats (sync port)
public sealed record CreditUnsynced(Guid SaleId, decimal Amount);
public sealed record CreditAuthoriseRequest(Guid CustomerId, Guid BranchId, Guid SaleId, decimal Amount, bool OverrideApproved, List<CreditUnsynced>? Unsynced = null);
public sealed record CreditAuthoriseResponse(bool Ok, string Outcome, decimal Available, decimal Exposure, decimal Limit, decimal OverBy, int OverdueDays, string Status,
    string? HoldReason, int TermsDays);
public sealed record CreditReleaseRequest(Guid SaleId);
public sealed record CreditItemPush(int Seq, DateOnly Due, decimal Amount, Guid Id);
public sealed record CreditDecisionPush(Guid Id, string Mode, decimal Amount, decimal Available, decimal Limit, decimal Exposure, decimal? Allowance, decimal OverBy,
    int OverdueDays, Guid? ApprovalId, Guid? ApprovedBy, DateTimeOffset CreatedAt, Guid CreatedBy);
public sealed record CreditEntryPush(Guid Id, Guid BranchId, Guid CustomerId, string Kind, decimal Amount, string Number, DateOnly DocDate, string SourceDocType,
    Guid SourceDocId, string? Note, DateTimeOffset CreatedAt, Guid CreatedBy, List<CreditItemPush> Items, CreditDecisionPush? Decision);
public sealed record CreditPushRequest(List<CreditEntryPush> Entries);
public sealed record CreditSnapshot(Guid AccountId, Guid CustomerId, string CustomerNumber, string NameEn, string? NameAr, string? Phone, decimal CreditLimit,
    int TermsDays, string Status, string? HoldKind, string? HoldReason, decimal? OfflineSaleCap, string? Note, int Version, long Seq, decimal Exposure,
    DateOnly? OldestDue, DateTimeOffset AsOf, Guid? AllowanceId, decimal? Allowance);
public sealed record CreditPushResponse(List<Guid> Acked, List<CreditSnapshot> Snapshots, List<string> Errors);

/// <summary>
/// Customer credit (blueprint 14): the decision at the till, charges with their instalments, credit notes, allocation of
/// payments to open items with interbranch settlement, holds, and what head office and branch servers exchange.
/// Lock order: the customer's credit lock comes after trade-ins and store credit and before any document number (ADR 54, 68).
/// </summary>
public static class CreditCore
{
    public static Task LockAsync(DbTx t, Guid customerId) =>
        t.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended('kite:credit:' || @customerId::text, 0))", new { customerId });

    public static async Task<CreditPolicy> PolicyAsync(DbTx t) =>
        await t.QuerySingleOrDefaultAsync<CreditPolicy>(CreditPolicy.Select + " LIMIT 1") ?? new CreditPolicy();

    public static Task<CreditAccount?> AccountAsync(DbTx t, Guid customerId) =>
        t.QuerySingleOrDefaultAsync<CreditAccount>(CreditAccount.Select + " WHERE a.customer_id = @customerId", new { customerId });

    public static async Task<ReceivableStatus> StatusAsync(DbTx t, Guid customerId) =>
        await t.QuerySingleOrDefaultAsync<ReceivableStatus>("""
            SELECT (s).balance AS Balance, (s).open_items AS OpenItems, (s).unallocated AS Unallocated, (s).oldest_due AS OldestDue
              FROM (SELECT crm.receivable_status(@customerId) AS s) x
            """, new { customerId }) ?? new ReceivableStatus(0m, 0m, 0m, null);

    public static Task<decimal> ExposureAsync(DbTx t, Guid customerId) =>
        t.ExecuteScalarAsync<decimal>("SELECT crm.credit_exposure(@customerId)", new { customerId });

    public static Task<bool> ModuleOnAsync(DbTx t) =>
        t.ExecuteScalarAsync<bool>("""
            SELECT coalesce((SELECT enabled FROM core.module_setting WHERE module_code = 'credit' AND branch_id IS NULL LIMIT 1), false)
               AND NOT coalesce('credit'::text = ANY((SELECT modules_off FROM lic.control_state LIMIT 1)::text[]), false)
               AND coalesce('credit'::text = ANY((SELECT modules FROM lic.licence WHERE is_current LIMIT 1)::text[]), true)
            """);

    private static DateOnly? Day(DateTime? d) => d is { } x ? DateOnly.FromDateTime(x) : null;

    // ------------------------------------------------------------------ the decision (14.2, 14.3)
    /// <summary>Head office's view: the limit less everything owed and reserved, and the oldest unpaid due date. The caller holds the credit lock.</summary>
    public static async Task<HeadOfficeEvaluation> EvaluateAsync(DbTx t, Guid customerId, decimal amount, decimal extraExposure, bool hasApproval, string timeZone)
    {
        var acc = await AccountAsync(t, customerId)
            ?? throw new KiteException(ErrorCodes.CreditNoAccount, "This customer has no credit account. Head office opens one with a limit.", 409, "customerId");
        var policy = await PolicyAsync(t);
        var exposure = await ExposureAsync(t, customerId) + extraExposure;
        var status = await StatusAsync(t, customerId);
        var overdue = CreditMath.OverdueDays(Day(status.OldestDue), BusinessDay.Today(timeZone, t.Clock));
        var available = CreditMath.AvailableOnline(acc.CreditLimit, exposure, 0m);
        return new HeadOfficeEvaluation(acc, policy, exposure, available, overdue,
            CreditMath.Decide(amount, available, policy.OverrideCap, overdue, policy.BlockOverdueDays, acc.Status, hasApproval));
    }

    /// <summary>Throws the refusal for a decision that does not allow the sale.</summary>
    public static void ThrowRefusal(CreditDecisionResult d, decimal available, int overdueDays, decimal overrideCap, string? holdReason)
    {
        switch (d.Outcome)
        {
            case CreditOutcome.Hold:
                throw new KiteException(ErrorCodes.CreditHold, $"The customer's credit account is on hold{(string.IsNullOrWhiteSpace(holdReason) ? "" : $": {holdReason}")}. No new credit until head office releases it.", 409, "payments");
            case CreditOutcome.Closed:
                throw new KiteException(ErrorCodes.CreditHold, "The customer's credit account is closed.", 409, "payments");
            case CreditOutcome.OverCap:
                throw new KiteException(ErrorCodes.CreditLimit,
                    $"Available credit is {available:0.000}. This is {d.OverBy:0.000} over, more than a manager may approve ({overrideCap:0.000}).", 409, "payments");
            case CreditOutcome.NeedsApproval:
                var why = new List<string>();
                if (d.OverBy > 0) why.Add($"it is {d.OverBy:0.000} over the available credit of {available:0.000}");
                if (overdueDays > 0) why.Add($"an invoice is {overdueDays} days overdue");
                throw new KiteException(ErrorCodes.CreditApprovalRequired, $"This credit sale needs a manager's approval: {string.Join(" and ", why)}.", 403, "creditApprovalId");
        }
    }

    /// <summary>
    /// Checks a sale "on account" at the till (14.2, 14.3) and returns its decision. On head office (a main or combined server)
    /// it is decided here, under the customer's lock. A branch server asks head office, which reserves the amount; if head
    /// office does not answer within the policy's time, the branch decides on its own copy with the offline rules.
    /// </summary>
    public static async Task<CreditCheck> CheckSaleAsync(DbTx t, CreditLink? link, PosTerminal term, Guid customerId, Guid saleId, decimal amount,
        Guid? approvalId, Guid cashierId, CreditPlanInput? plan, string timeZone, bool cashierMayOverride = false)
    {
        if (!await ModuleOnAsync(t)) throw new KiteException(ErrorCodes.ModuleDisabled, "Customer credit is switched off.", 409, "payments");
        await LockAsync(t, customerId);
        var policy = await PolicyAsync(t);
        if (plan is not null && (plan.Instalments < 1 || plan.Instalments > policy.MaxInstalments))
            throw new KiteException(ErrorCodes.Validation, $"A credit sale is paid in 1 to {policy.MaxInstalments} instalments.", 400, "creditPlan.instalments");
        if (plan?.IntervalDays is < 1 or > 365)
            throw new KiteException(ErrorCodes.Validation, "Instalments are 1 to 365 days apart.", 400, "creditPlan.intervalDays");

        // The approval is checked here, before anyone is asked: an approval for another sale or cashier never reaches head office.
        // A cashier who holds CREDIT.OVERRIDE approves for themselves (as with prices and discounts); anyone else needs a manager's approval.
        Guid? approvedBy = null;
        async Task<Guid> UseApproval()
        {
            if (approvalId is null) return cashierId;
            var a = await Approvals.UseAsync(t, approvalId.Value, cashierId, term.BranchId, Permissions.CreditOverride, "sale", saleId);
            return a.ApproverId ?? cashierId;
        }
        var mayGoOver = approvalId is not null || cashierMayOverride;

        if (t.Server.IsHeadOffice)
        {
            var e = await EvaluateAsync(t, customerId, amount, 0m, mayGoOver, timeZone);
            if (!e.Decision.Allowed) ThrowRefusal(e.Decision, e.Available, e.OverdueDays, e.Policy.OverrideCap, e.Account.HoldReason);
            if (e.Decision.Outcome == CreditOutcome.Approved) approvedBy = await UseApproval();
            return new CreditCheck(customerId, e.Account.Id, "local", amount, e.Available, e.Account.CreditLimit, e.Exposure, null, e.Decision.OverBy,
                e.OverdueDays, approvedBy is null ? null : approvalId, approvedBy, e.Account.TermsDays, plan);
        }

        var acc = await AccountAsync(t, customerId)
            ?? throw new KiteException(ErrorCodes.CreditNoAccount, "This customer has no credit account here yet. Head office opens accounts; they arrive with the next sync.", 409, "customerId");
        // Charges this branch server wrote that head office has not confirmed yet (14.2: "this branch's unsynced charges").
        var unsynced = (await t.WithAllBranchesAsync(() => t.QueryAsync<CreditUnsynced>("""
            SELECT l.source_doc_id AS SaleId, l.amount FROM crm.credit_ledger l
             WHERE l.customer_id = @customerId AND l.balance_type = 'receivable' AND l.kind = 'charge'
               AND l.origin_server_id = (SELECT id FROM core.server WHERE is_self)
               AND NOT EXISTS (SELECT 1 FROM crm.credit_entry_ack k WHERE k.entry_id = l.id)
            """, new { customerId }))).ToList();
        if (mayGoOver) approvedBy = await UseApproval();

        var online = link is null ? null : await link.AuthoriseAsync(new CreditAuthoriseRequest(customerId, term.BranchId, saleId, amount, approvedBy is not null, unsynced),
            policy.OnlineTimeoutMs);
        if (online is not null)
        {
            var d = new CreditDecisionResult(online.Outcome switch
            {
                "ok" => CreditOutcome.Ok, "approved" => CreditOutcome.Approved, "needs_approval" => CreditOutcome.NeedsApproval,
                "over_cap" => CreditOutcome.OverCap, "hold" => CreditOutcome.Hold, _ => CreditOutcome.Closed,
            }, online.OverBy);
            if (!online.Ok) ThrowRefusal(d, online.Available, online.OverdueDays, policy.OverrideCap, online.HoldReason);
            if (d.Outcome == CreditOutcome.Ok) approvedBy = null;   // the approval was not needed: it stays spent on the sale, but nothing went over
            return new CreditCheck(customerId, acc.Id, "online", amount, online.Available, online.Limit, online.Exposure, null, online.OverBy, online.OverdueDays,
                approvedBy is null ? null : approvalId, approvedBy, online.TermsDays, plan);
        }

        // Offline (14.2): the lower of the allowance and the limit less the last known exposure, less charges since the last sync, capped per sale.
        var allowance = await t.ExecuteScalarAsync<decimal?>("SELECT amount FROM crm.credit_allowance WHERE account_id = @a AND branch_id = @b",
            new { a = acc.Id, b = term.BranchId }) ?? 0m;
        var sinceSync = unsynced.Sum(u => u.Amount);
        var cap = acc.OfflineSaleCap ?? policy.OfflineSaleCap;
        var available = CreditMath.AvailableOffline(allowance, acc.CreditLimit, acc.Exposure, sinceSync, cap);
        var overdue = CreditMath.OverdueDays(Day(acc.OldestDue), BusinessDay.Today(timeZone, t.Clock));
        var decision = CreditMath.Decide(amount, available, policy.OverrideCap, overdue, policy.BlockOverdueDays, acc.Status, approvedBy is not null);
        if (!decision.Allowed) ThrowRefusal(decision, available, overdue, policy.OverrideCap, acc.HoldReason);
        if (decision.Outcome == CreditOutcome.Ok) approvedBy = null;
        return new CreditCheck(customerId, acc.Id, "offline", amount, available, acc.CreditLimit, acc.Exposure + sinceSync, allowance, decision.OverBy, overdue,
            approvedBy is null ? null : approvalId, approvedBy, acc.TermsDays, plan);
    }

    // ------------------------------------------------------------------ charges and credit notes (14.3, 14.4)
    /// <summary>The sale's charge to the customer's account, its instalments and its decision. Returns the ledger row.</summary>
    public static async Task<Guid> WriteChargeAsync(DbTx t, Guid businessId, Guid branchId, Guid saleId, string number, DateOnly date, CreditCheck c, Guid cashierId)
    {
        var entry = t.NewId();
        await t.ExecuteAsync("""
            INSERT INTO crm.credit_ledger (id, business_id, branch_id, customer_id, balance_type, kind, amount, number, doc_date, source_doc_type, source_doc_id, note,
                                           created_by, origin_server_id)
            VALUES (@entry, @businessId, @branchId, @CustomerId, 'receivable', 'charge', @Amount, @number, @date::date, 'sale', @saleId, @note, @cashierId,
                    (SELECT id FROM core.server WHERE is_self))
            """, new { entry, businessId, branchId, c.CustomerId, c.Amount, number, date = date.ToDateTime(TimeOnly.MinValue), saleId, note = $"Sale {number}", cashierId });
        foreach (var i in CreditMath.Instalments(c.Amount, c.Plan?.Instalments ?? 1, date, c.TermsDays, c.Plan?.IntervalDays ?? 30))
            await t.ExecuteAsync("""
                INSERT INTO crm.receivable_item (id, business_id, branch_id, customer_id, entry_id, seq, due_date, amount, origin_server_id)
                VALUES (@id, @businessId, @branchId, @CustomerId, @entry, @Seq, @due::date, @Amount, (SELECT id FROM core.server WHERE is_self))
                """, new { id = t.NewId(), businessId, branchId, c.CustomerId, entry, i.Seq, due = i.Due.ToDateTime(TimeOnly.MinValue), i.Amount });
        await t.ExecuteAsync("""
            INSERT INTO crm.credit_decision (id, business_id, branch_id, customer_id, sale_id, entry_id, mode, amount, available, credit_limit, exposure, allowance,
                                             over_by, overdue_days, approval_id, approved_by, created_by, origin_server_id)
            VALUES (@id, @businessId, @branchId, @CustomerId, @saleId, @entry, @Mode, @Amount, @Available, @Limit, @Exposure, @Allowance,
                    @OverBy, @OverdueDays, @ApprovalId, @ApprovedBy, @cashierId, (SELECT id FROM core.server WHERE is_self))
            """, new { id = t.NewId(), businessId, branchId, c.CustomerId, saleId, entry, c.Mode, c.Amount, c.Available, c.Limit, c.Exposure, c.Allowance,
                       c.OverBy, c.OverdueDays, c.ApprovalId, c.ApprovedBy, cashierId });
        if (c.ApprovedBy is not null)
            await t.AuditAsync(new AuditEntry("credit.override", "sale", saleId,
                After: Json.Obj(new { number, c.CustomerId, c.Mode, c.Amount, c.Available, c.OverBy, c.OverdueDays, c.ApprovedBy }), BranchId: branchId));
        return entry;
    }

    /// <summary>A credit note taken off the customer's account; head office settles the original sale's instalments with it first.</summary>
    public static async Task<Guid> WriteCreditNoteAsync(DbTx t, Guid businessId, Guid branchId, Guid customerId, Guid creditNoteId, string number, DateOnly date,
        decimal amount, Guid by)
    {
        var entry = t.NewId();
        await t.ExecuteAsync("""
            INSERT INTO crm.credit_ledger (id, business_id, branch_id, customer_id, balance_type, kind, amount, number, doc_date, source_doc_type, source_doc_id, note,
                                           created_by, origin_server_id)
            VALUES (@entry, @businessId, @branchId, @customerId, 'receivable', 'credit_note', @neg, @number, @date::date, 'sale', @creditNoteId, @note, @by,
                    (SELECT id FROM core.server WHERE is_self))
            """, new { entry, businessId, branchId, customerId, neg = -amount, number, date = date.ToDateTime(TimeOnly.MinValue), creditNoteId, note = $"Credit note {number}", by });
        return entry;
    }

    /// <summary>What an original sale charged on account, less what credit notes already took back (the most a refund "on account" may be).</summary>
    public static Task<decimal> RefundableOnAccountAsync(DbTx t, Guid originalSaleId) =>
        t.WithAllBranchesAsync(() => t.ExecuteScalarAsync<decimal>("""
            SELECT coalesce(sum(p.amount), 0) FROM sales.payment p JOIN sales.sale s ON s.id = p.sale_id
             WHERE p.method = 'credit' AND (s.id = @originalSaleId OR s.original_sale_id = @originalSaleId)
            """, new { originalSaleId }));

    // ------------------------------------------------------------------ allocation and interbranch settlement (14.4, 14.6)
    private sealed record OpenRow(Guid Id, Guid BranchId, Guid EntryId, Guid SourceDocId, DateTime DueDate, short Seq, decimal Open);
    private sealed record CreditRow(Guid Id, Guid BranchId, string Number, decimal LeftOver);

    /// <summary>
    /// Settles open items with the customer's unallocated payments and credits (head office only, the caller holds the credit
    /// lock): the given allocations first, then the original sale's instalments for a credit note, then oldest due first.
    /// A credit taken at one branch for another branch's invoice moves the receivable by two transfer rows and one journal
    /// numbered at <paramref name="ownerBranchId"/>: receiving branch Dr receivable / Cr due to; issuing branch Dr due from /
    /// Cr receivable. Returns the number of allocations written.
    /// </summary>
    public static async Task<int> AllocateAsync(DbTx t, Guid businessId, Guid customerId, Guid ownerBranchId, DateOnly date, string sourceDocType, Guid sourceDocId,
        IReadOnlyList<(Guid CreditEntryId, Guid ItemId, decimal Amount)>? manual = null, Guid? preferSaleId = null)
    {
        if (!t.Server.IsHeadOffice) return 0;
        return await t.WithAllBranchesAsync(async () =>
        {
            var items = (await t.QueryAsync<OpenRow>("""
                SELECT i.id, i.branch_id AS BranchId, i.entry_id AS EntryId, l.source_doc_id AS SourceDocId, i.due_date AS DueDate, i.seq,
                       i.amount - coalesce((SELECT sum(a.amount) FROM crm.receivable_allocation a WHERE a.item_id = i.id), 0) AS Open
                  FROM crm.receivable_item i JOIN crm.credit_ledger l ON l.id = i.entry_id
                 WHERE i.customer_id = @customerId
                 ORDER BY i.due_date, l.doc_date, l.created_at, i.seq
                """, new { customerId })).Where(i => i.Open > 0).ToList();
            var credits = (await t.QueryAsync<CreditRow>("""
                SELECT l.id, l.branch_id AS BranchId, l.number,
                       -l.amount - coalesce((SELECT sum(a.amount) FROM crm.receivable_allocation a WHERE a.credit_entry_id = l.id), 0) AS LeftOver
                  FROM crm.credit_ledger l
                 WHERE l.customer_id = @customerId AND l.balance_type = 'receivable' AND l.amount < 0 AND l.kind <> 'transfer'
                 ORDER BY l.doc_date, l.created_at, l.id
                """, new { customerId })).Where(c => c.LeftOver > 0).ToList();
            if (items.Count == 0 || credits.Count == 0) return 0;

            var open = items.ToDictionary(i => i.Id, i => i.Open);
            var left = credits.ToDictionary(c => c.Id, c => c.LeftOver);
            var plan = new List<(CreditRow Credit, OpenRow Item, decimal Amount)>();
            foreach (var (creditId, itemId, amount) in manual ?? [])
            {
                var c = credits.FirstOrDefault(x => x.Id == creditId) ?? throw new KiteException(ErrorCodes.CreditAllocation, "That payment has nothing left to allocate.", 409, "allocations");
                var i = items.FirstOrDefault(x => x.Id == itemId) ?? throw new KiteException(ErrorCodes.CreditAllocation, "That instalment is not open.", 409, "allocations");
                if (amount <= 0 || amount > open[i.Id] || amount > left[c.Id])
                    throw new KiteException(ErrorCodes.CreditAllocation, $"At most {Math.Min(open[i.Id], left[c.Id]):0.000} can be allocated to instalment {i.Seq}.", 409, "allocations");
                plan.Add((c, i, amount));
                open[i.Id] -= amount;
                left[c.Id] -= amount;
            }
            foreach (var c in credits)
            {
                if (left[c.Id] <= 0) continue;
                var preferred = preferSaleId is { } sid ? items.Where(i => i.SourceDocId == sid).ToList() : [];
                foreach (var group in new[] { preferred, items.Except(preferred).ToList() })
                {
                    var (allocations, rest) = CreditMath.AllocateOldestFirst(group.Select(i => new OpenItem(i.Id, DateOnly.FromDateTime(i.DueDate), open[i.Id])).ToList(), left[c.Id]);
                    foreach (var a in allocations)
                    {
                        plan.Add((c, items.First(i => i.Id == a.ItemId), a.Amount));
                        open[a.ItemId] -= a.Amount;
                    }
                    left[c.Id] = rest;
                }
            }
            if (plan.Count == 0) return 0;

            foreach (var (c, i, amount) in plan)
                await t.ExecuteAsync("""
                    INSERT INTO crm.receivable_allocation (id, business_id, customer_id, item_id, credit_entry_id, amount, created_by, origin_server_id)
                    VALUES (@id, @businessId, @customerId, @item, @credit, @amount, @by, (SELECT id FROM core.server WHERE is_self))
                    """, new { id = t.NewId(), businessId, customerId, item = i.Id, credit = c.Id, amount, by = t.Actor.UserId });

            // Interbranch: one pair of transfer rows per (receiving branch, issuing branch), one journal for all of them.
            var moves = plan.Where(p => p.Credit.BranchId != p.Item.BranchId)
                .GroupBy(p => (From: p.Credit.BranchId, To: p.Item.BranchId)).Select(g => (g.Key.From, g.Key.To, Amount: g.Sum(x => x.Amount),
                    Numbers: string.Join(", ", g.Select(x => x.Credit.Number).Distinct()))).OrderBy(m => m.From).ThenBy(m => m.To).ToList();
            if (moves.Count > 0)
            {
                var parts = new List<(string, Guid, PostingFacts)>();
                foreach (var m in moves)
                {
                    foreach (var (branch, amount) in new[] { (m.From, m.Amount), (m.To, -m.Amount) })
                        await t.ExecuteAsync("""
                            INSERT INTO crm.credit_ledger (id, business_id, branch_id, customer_id, balance_type, kind, amount, number, doc_date, source_doc_type,
                                                           source_doc_id, note, created_by, origin_server_id)
                            VALUES (@id, @businessId, @branch, @customerId, 'receivable', 'transfer', @amount, @numbers, @date::date, @sourceDocType, @sourceDocId,
                                    @note, @by, (SELECT id FROM core.server WHERE is_self))
                            """, new { id = t.NewId(), businessId, branch, customerId, amount, numbers = m.Numbers.Length > 120 ? m.Numbers[..120] : m.Numbers,
                                       date = date.ToDateTime(TimeOnly.MinValue), sourceDocType, sourceDocId,
                                       note = amount > 0 ? "Payment here settled another branch's invoice" : "Invoice settled by a payment at another branch",
                                       by = t.Actor.UserId ?? Guid.Empty });
                    parts.Add((PostingRuleTemplates.CreditInterbranchOut, m.From, new PostingFacts().Set("amount", m.Amount)));
                    parts.Add((PostingRuleTemplates.CreditInterbranchIn, m.To, new PostingFacts().Set("amount", m.Amount)));
                }
                await Posting.PostManyAsync(t, businessId, ownerBranchId, "credit.interbranch", parts, date, sourceDocType, sourceDocId,
                    $"Interbranch settlement of customer payments ({string.Join("; ", moves.Select(m => m.Numbers))})", customerId);
            }
            return plan.Count;
        });
    }

    // ------------------------------------------------------------------ holds (14.5)
    public static async Task HoldAsync(DbTx t, CreditAccount acc, string kind, string reason, decimal? exposure)
    {
        await t.ExecuteAsync("""
            UPDATE crm.credit_account SET status = 'hold', hold_kind = @kind, hold_reason = @reason, held_at = now(), held_by = @by, updated_by = @by
             WHERE id = @id
            """, new { id = acc.Id, kind, reason, by = t.Actor.UserId });
        await t.ExecuteAsync("""
            INSERT INTO crm.credit_hold_event (id, business_id, account_id, action, kind, reason, exposure, credit_limit, created_by, origin_server_id)
            VALUES (@id, @b, @account, 'hold', @kind, @reason, @exposure, @limit, @by, (SELECT id FROM core.server WHERE is_self))
            """, new { id = t.NewId(), b = acc.BusinessId, account = acc.Id, kind, reason, exposure, limit = acc.CreditLimit, by = t.Actor.UserId });
        await t.AuditAsync(new AuditEntry("credit.hold", "credit_account", acc.Id, After: Json.Obj(new { acc.CustomerNumber, kind, reason, exposure, acc.CreditLimit })));
        await t.PublishAsync("crm.credit_hold", "credit_account", acc.Id, acc.Version + 1, Json.Obj(new { acc.CustomerId, kind, reason }));
        acc.Status = "hold";
        acc.HoldKind = kind;
        acc.HoldReason = reason;
    }

    public static async Task ReleaseAsync(DbTx t, CreditAccount acc, string reason, decimal? exposure)
    {
        var kind = acc.HoldKind ?? "manual";
        await t.ExecuteAsync("""
            UPDATE crm.credit_account SET status = 'active', hold_kind = NULL, hold_reason = NULL, held_at = NULL, held_by = NULL, updated_by = @by WHERE id = @id
            """, new { id = acc.Id, by = t.Actor.UserId });
        await t.ExecuteAsync("""
            INSERT INTO crm.credit_hold_event (id, business_id, account_id, action, kind, reason, exposure, credit_limit, created_by, origin_server_id)
            VALUES (@id, @b, @account, 'release', @kind, @reason, @exposure, @limit, @by, (SELECT id FROM core.server WHERE is_self))
            """, new { id = t.NewId(), b = acc.BusinessId, account = acc.Id, kind, reason, exposure, limit = acc.CreditLimit, by = t.Actor.UserId });
        await t.AuditAsync(new AuditEntry("credit.released", "credit_account", acc.Id, After: Json.Obj(new { acc.CustomerNumber, kind, reason, exposure })));
        await t.PublishAsync("crm.credit_released", "credit_account", acc.Id, acc.Version + 1, Json.Obj(new { acc.CustomerId, kind, reason }));
        acc.Status = "active";
        acc.HoldKind = null;
        acc.HoldReason = null;
    }

    /// <summary>
    /// Automatic holds (14.5), head office only, under the customer's lock: over the limit after a branch's offline charges
    /// arrived (<paramref name="afterSync"/>), or an instalment overdue past the policy; an automatic hold lifts when its
    /// cause has gone (a manual hold stays until someone releases it). Returns the exposure.
    /// </summary>
    public static async Task<decimal> EvaluateHoldAsync(DbTx t, CreditAccount acc, CreditPolicy policy, string timeZone, bool afterSync)
    {
        var exposure = await ExposureAsync(t, acc.CustomerId);
        var status = await StatusAsync(t, acc.CustomerId);
        var overdue = CreditMath.OverdueDays(Day(status.OldestDue), BusinessDay.Today(timeZone, t.Clock));
        if (acc.Status == "active")
        {
            if (afterSync && exposure > acc.CreditLimit)
                await HoldAsync(t, acc, "exposure", $"Owed {exposure:0.000} against a limit of {acc.CreditLimit:0.000} once offline sales arrived", exposure);
            else if (overdue > policy.HoldOverdueDays)
                await HoldAsync(t, acc, "overdue", $"An instalment is {overdue} days overdue (the policy holds after {policy.HoldOverdueDays})", exposure);
        }
        else if (acc.Status == "hold" && acc.HoldKind == "exposure" && exposure <= acc.CreditLimit && overdue <= policy.HoldOverdueDays)
            await ReleaseAsync(t, acc, $"Back within the limit: owed {exposure:0.000} of {acc.CreditLimit:0.000}", exposure);
        else if (acc.Status == "hold" && acc.HoldKind == "overdue" && overdue <= policy.HoldOverdueDays)
            await ReleaseAsync(t, acc, overdue == 0 ? "Nothing is overdue now" : $"Overdue {overdue} days, within the policy", exposure);
        return exposure;
    }

    // ------------------------------------------------------------------ head office: the stream to branch servers
    private sealed record SnapshotRow(Guid AccountId, Guid CustomerId, string CustomerNumber, string NameEn, string? NameAr, string? Phone, decimal CreditLimit,
        int TermsDays, string Status, string? HoldKind, string? HoldReason, decimal? OfflineSaleCap, string? Note, int Version, long Seq, Guid? AllowanceId, decimal? Allowance);

    /// <summary>Accounts changed since <paramref name="sinceSeq"/> (or the given customers), with the exposure and oldest due date head office counts now.</summary>
    public static async Task<List<CreditSnapshot>> SnapshotsAsync(DbTx t, long sinceSeq, Guid branchId, IReadOnlyCollection<Guid>? customers = null, int limit = 500)
    {
        var rows = await t.QueryAsync<SnapshotRow>($"""
            SELECT a.id AS AccountId, a.customer_id AS CustomerId, c.number AS CustomerNumber, c.name_en AS NameEn, c.name_ar AS NameAr, c.phone_e164 AS Phone,
                   a.credit_limit AS CreditLimit, a.terms_days AS TermsDays, a.status, a.hold_kind AS HoldKind, a.hold_reason AS HoldReason,
                   a.offline_sale_cap AS OfflineSaleCap, a.note, a.version, a.sync_seq AS Seq, w.id AS AllowanceId, w.amount AS Allowance
              FROM crm.credit_account a JOIN crm.customer c ON c.id = a.customer_id
              LEFT JOIN crm.credit_allowance w ON w.account_id = a.id AND w.branch_id = @branchId
             WHERE {(customers is null ? "a.sync_seq > @sinceSeq" : "a.customer_id = ANY(@ids)")}
             ORDER BY a.sync_seq LIMIT @limit
            """, new { sinceSeq, branchId, ids = customers?.ToArray(), limit });
        var list = new List<CreditSnapshot>();
        foreach (var r in rows)
        {
            var exposure = await ExposureAsync(t, r.CustomerId);
            var st = await StatusAsync(t, r.CustomerId);
            list.Add(new CreditSnapshot(r.AccountId, r.CustomerId, r.CustomerNumber, r.NameEn, r.NameAr, r.Phone, r.CreditLimit, r.TermsDays, r.Status, r.HoldKind,
                r.HoldReason, r.OfflineSaleCap, r.Note, r.Version, r.Seq, exposure, Day(st.OldestDue), t.Clock.UtcNow, r.AllowanceId, r.Allowance));
        }
        return list;
    }

    /// <summary>
    /// A branch server applies head office's copies (14.1): the policy, each account with its exposure and this branch's
    /// allowance, and the customer if this server does not have it yet. Head office's numbers and versions are kept.
    /// </summary>
    public static async Task ApplySnapshotsAsync(DbTx t, CreditPolicy? policy, IReadOnlyList<CreditSnapshot> snapshots, Guid branchId, bool advanceSeq)
    {
        await t.ExecuteAsync("SELECT set_config('kite.credit_replica', 'on', true)");
        var businessId = await t.ExecuteScalarAsync<Guid>("SELECT id FROM core.business LIMIT 1");
        if (policy is not null)
            await t.ExecuteAsync("""
                INSERT INTO crm.credit_policy (business_id, default_terms_days, block_overdue_days, hold_overdue_days, offline_sale_cap, override_cap, allow_overcommit,
                                               online_timeout_ms, authorisation_minutes, max_instalments, remind_before_days, remind_overdue_every, version)
                VALUES (@businessId, @DefaultTermsDays, @BlockOverdueDays, @HoldOverdueDays, @OfflineSaleCap, @OverrideCap, @AllowOvercommit, @OnlineTimeoutMs,
                        @AuthorisationMinutes, @MaxInstalments, @RemindBeforeDays, @RemindOverdueEvery, @Version)
                ON CONFLICT (business_id) DO UPDATE SET default_terms_days = EXCLUDED.default_terms_days, block_overdue_days = EXCLUDED.block_overdue_days,
                       hold_overdue_days = EXCLUDED.hold_overdue_days, offline_sale_cap = EXCLUDED.offline_sale_cap, override_cap = EXCLUDED.override_cap,
                       allow_overcommit = EXCLUDED.allow_overcommit, online_timeout_ms = EXCLUDED.online_timeout_ms, authorisation_minutes = EXCLUDED.authorisation_minutes,
                       max_instalments = EXCLUDED.max_instalments, remind_before_days = EXCLUDED.remind_before_days, remind_overdue_every = EXCLUDED.remind_overdue_every,
                       version = EXCLUDED.version
                 WHERE crm.credit_policy.version <> EXCLUDED.version
                """, new { businessId, policy.DefaultTermsDays, policy.BlockOverdueDays, policy.HoldOverdueDays, policy.OfflineSaleCap, policy.OverrideCap,
                           policy.AllowOvercommit, policy.OnlineTimeoutMs, policy.AuthorisationMinutes, policy.MaxInstalments, policy.RemindBeforeDays,
                           policy.RemindOverdueEvery, policy.Version });
        foreach (var s in snapshots)
        {
            if (!await t.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM crm.customer WHERE id = @CustomerId)", new { s.CustomerId }))
            {
                // A customer head office numbered; if this server used that number for someone else, head office's copy is marked.
                var taken = await t.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM crm.customer WHERE business_id = @businessId AND number = @CustomerNumber)",
                    new { businessId, s.CustomerNumber });
                await t.ExecuteAsync("""
                    INSERT INTO crm.customer (id, business_id, number, kind, name_en, name_ar, phone_e164, origin_server_id)
                    VALUES (@CustomerId, @businessId, @number, 'individual', @NameEn, @NameAr, @Phone, (SELECT id FROM core.server WHERE role IN ('combined','main') LIMIT 1))
                    """, new { s.CustomerId, businessId, number = taken ? s.CustomerNumber + "-HO" : s.CustomerNumber, s.NameEn, s.NameAr, s.Phone });
            }
            await t.ExecuteAsync("""
                INSERT INTO crm.credit_account (id, business_id, customer_id, credit_limit, terms_days, status, hold_kind, hold_reason, offline_sale_cap, note,
                                                approved_by, exposure, oldest_due, exposure_as_of, sync_seq, version)
                VALUES (@AccountId, @businessId, @CustomerId, @CreditLimit, @TermsDays, @Status, @HoldKind, @HoldReason, @OfflineSaleCap, @Note,
                        '00000000-0000-0000-0000-000000000000', @Exposure, @oldest::date, @AsOf, @Seq, @Version)
                ON CONFLICT (id) DO UPDATE SET credit_limit = EXCLUDED.credit_limit, terms_days = EXCLUDED.terms_days, status = EXCLUDED.status,
                       hold_kind = EXCLUDED.hold_kind, hold_reason = EXCLUDED.hold_reason, offline_sale_cap = EXCLUDED.offline_sale_cap, note = EXCLUDED.note,
                       exposure = EXCLUDED.exposure, oldest_due = EXCLUDED.oldest_due, exposure_as_of = EXCLUDED.exposure_as_of,
                       sync_seq = EXCLUDED.sync_seq, version = EXCLUDED.version, updated_at = now()
                 WHERE crm.credit_account.exposure_as_of IS NULL OR crm.credit_account.exposure_as_of <= EXCLUDED.exposure_as_of
                """, new { s.AccountId, businessId, s.CustomerId, s.CreditLimit, s.TermsDays, s.Status, s.HoldKind, s.HoldReason, s.OfflineSaleCap, s.Note,
                           s.Exposure, oldest = s.OldestDue?.ToDateTime(TimeOnly.MinValue), s.AsOf, s.Seq, s.Version });
            if (s.AllowanceId is { } aid)
                await t.ExecuteAsync("""
                    INSERT INTO crm.credit_allowance (id, business_id, account_id, branch_id, amount) VALUES (@aid, @businessId, @AccountId, @branchId, @Allowance)
                    ON CONFLICT (account_id, branch_id) DO UPDATE SET amount = EXCLUDED.amount
                    """, new { aid, businessId, s.AccountId, branchId, s.Allowance });
            else
                await t.ExecuteAsync("UPDATE crm.credit_allowance SET amount = 0 WHERE account_id = @AccountId AND branch_id = @branchId", new { s.AccountId, branchId });
        }
        if (advanceSeq && snapshots.Count > 0)
            await t.ExecuteAsync("""
                INSERT INTO crm.credit_link (business_id, credit_seq, last_snapshot_at) VALUES (@businessId, @seq, now())
                ON CONFLICT (business_id) DO UPDATE SET credit_seq = greatest(crm.credit_link.credit_seq, EXCLUDED.credit_seq), last_snapshot_at = now(), updated_at = now()
                """, new { businessId, seq = snapshots.Max(s => s.Seq) });
        await t.ExecuteAsync("SELECT set_config('kite.credit_replica', '', true)");
    }

    private sealed record AuthRow(decimal Amount, decimal Available, decimal OverBy);
    private sealed record DecisionRow(Guid Id, string Mode, decimal Amount, decimal Available, decimal Limit, decimal Exposure, decimal? Allowance,
        decimal OverBy, int OverdueDays, Guid? ApprovalId, Guid? ApprovedBy, DateTime CreatedAt, Guid CreatedBy);

    // ------------------------------------------------------------------ head office: online authorisation (14.2)
    /// <summary>
    /// A branch server asks before writing a credit sale. Under the customer's lock head office counts everything owed, every
    /// live reservation and the branch's charges it has not seen, decides, and reserves the amount for the sale when it fits.
    /// The same sale asked again gets the same answer.
    /// </summary>
    public static async Task<CreditAuthoriseResponse> AuthoriseAsync(DbTx t, PkiService.BranchIdentity who, CreditAuthoriseRequest req, string timeZone)
    {
        if (req.Amount <= 0) throw new KiteException(ErrorCodes.Validation, "The amount is above zero.", 400, "amount");
        await LockAsync(t, req.CustomerId);
        var existing = await t.QuerySingleOrDefaultAsync<AuthRow>("""
            SELECT amount AS Amount, available AS Available, over_by AS OverBy FROM crm.credit_authorisation
             WHERE server_id = @s AND sale_id = @SaleId AND released_at IS NULL
            """, new { s = who.ServerId, req.SaleId });
        // Charges head office has not seen: not in the ledger yet and not reserved by a live authorisation.
        var extra = 0m;
        foreach (var u in req.Unsynced ?? [])
            if (u.SaleId != req.SaleId && !await t.ExecuteScalarAsync<bool>("""
                    SELECT EXISTS (SELECT 1 FROM crm.credit_ledger WHERE source_doc_id = @SaleId AND balance_type = 'receivable')
                        OR EXISTS (SELECT 1 FROM crm.credit_authorisation WHERE server_id = @s AND sale_id = @SaleId AND consumed_at IS NULL AND released_at IS NULL AND expires_at > now())
                    """, new { u.SaleId, s = who.ServerId }))
                extra += u.Amount;
        if (existing is not null)
        {
            var ex = existing;
            var acc0 = await AccountAsync(t, req.CustomerId);
            return new CreditAuthoriseResponse(true, ex.OverBy > 0 ? "approved" : "ok", ex.Available, await ExposureAsync(t, req.CustomerId), acc0!.CreditLimit,
                ex.OverBy, 0, acc0.Status, acc0.HoldReason, acc0.TermsDays);
        }
        var e = await EvaluateAsync(t, req.CustomerId, req.Amount, extra, req.OverrideApproved, timeZone);
        var outcome = e.Decision.Outcome switch
        {
            CreditOutcome.Ok => "ok", CreditOutcome.Approved => "approved", CreditOutcome.NeedsApproval => "needs_approval",
            CreditOutcome.OverCap => "over_cap", CreditOutcome.Hold => "hold", _ => "closed",
        };
        if (e.Decision.Allowed)
            await t.ExecuteAsync("""
                INSERT INTO crm.credit_authorisation (id, business_id, customer_id, branch_id, server_id, sale_id, amount, available, over_by, expires_at)
                VALUES (@id, @b, @CustomerId, @BranchId, @s, @SaleId, @Amount, @available, @over, now() + make_interval(mins => @mins))
                ON CONFLICT (server_id, sale_id) DO UPDATE SET amount = EXCLUDED.amount, available = EXCLUDED.available, over_by = EXCLUDED.over_by,
                       expires_at = EXCLUDED.expires_at, released_at = NULL, created_at = now()
                """, new { id = t.NewId(), b = e.Account.BusinessId, req.CustomerId, req.BranchId, s = who.ServerId, req.SaleId, req.Amount, available = e.Available,
                           over = e.Decision.OverBy, mins = e.Policy.AuthorisationMinutes });
        return new CreditAuthoriseResponse(e.Decision.Allowed, outcome, e.Available, e.Exposure, e.Account.CreditLimit, e.Decision.OverBy, e.OverdueDays,
            e.Account.Status, e.Account.HoldReason, e.Account.TermsDays);
    }

    // ------------------------------------------------------------------ branch → head office: the ledger rows written offline (14.5)
    /// <summary>A branch server's receivable rows head office has not confirmed, oldest first, with their instalments and decisions.</summary>
    public static async Task<List<CreditEntryPush>> PendingPushAsync(DbTx t, int limit = 200)
    {
        var rows = (await t.QueryAsync<(Guid Id, Guid BranchId, Guid CustomerId, string Kind, decimal Amount, string Number, DateTime DocDate, string SourceDocType,
            Guid SourceDocId, string? Note, DateTime CreatedAt, Guid CreatedBy)>("""
            SELECT l.id, l.branch_id, l.customer_id, l.kind, l.amount, l.number, l.doc_date, l.source_doc_type, l.source_doc_id, l.note, l.created_at, l.created_by
              FROM crm.credit_ledger l
             WHERE l.balance_type = 'receivable' AND l.origin_server_id = (SELECT id FROM core.server WHERE is_self)
               AND NOT EXISTS (SELECT 1 FROM crm.credit_entry_ack k WHERE k.entry_id = l.id)
             ORDER BY l.created_at, l.id LIMIT @limit
            """, new { limit })).ToList();
        var list = new List<CreditEntryPush>();
        foreach (var r in rows)
        {
            var items = (await t.QueryAsync<(int Seq, DateTime Due, decimal Amount, Guid Id)>(
                "SELECT seq, due_date, amount, id FROM crm.receivable_item WHERE entry_id = @id ORDER BY seq", new { r.Id }))
                .Select(i => new CreditItemPush(i.Seq, DateOnly.FromDateTime(i.Due), i.Amount, i.Id)).ToList();
            var d = await t.QuerySingleOrDefaultAsync<DecisionRow>("""
                SELECT id AS Id, mode AS Mode, amount AS Amount, available AS Available, credit_limit AS Limit, exposure AS Exposure, allowance AS Allowance,
                       over_by AS OverBy, overdue_days AS OverdueDays, approval_id AS ApprovalId, approved_by AS ApprovedBy, created_at AS CreatedAt, created_by AS CreatedBy
                  FROM crm.credit_decision WHERE entry_id = @id
                """, new { r.Id });
            list.Add(new CreditEntryPush(r.Id, r.BranchId, r.CustomerId, r.Kind, r.Amount, r.Number, DateOnly.FromDateTime(r.DocDate), r.SourceDocType, r.SourceDocId,
                r.Note, new DateTimeOffset(DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc)), r.CreatedBy, items,
                d is not null && d is var x ? new CreditDecisionPush(x.Id, x.Mode, x.Amount, x.Available, x.Limit, x.Exposure, x.Allowance, x.OverBy, x.OverdueDays, x.ApprovalId,
                    x.ApprovedBy, new DateTimeOffset(DateTime.SpecifyKind(x.CreatedAt, DateTimeKind.Utc)), x.CreatedBy) : null));
        }
        return list;
    }

    /// <summary>
    /// Head office takes a branch server's rows (14.5): each once, with its instalments and decision; a charge consumes the
    /// sale's reservation. Then, customer by customer in a fixed order, open items are settled and the exposure checked: past
    /// the limit, the breach is recorded and the account goes on hold, and the hold reaches every branch with the stream.
    /// </summary>
    public static async Task<CreditPushResponse> AcceptPushAsync(DbTx t, PkiService.BranchIdentity who, CreditPushRequest req, string timeZone)
    {
        var acked = new List<Guid>();
        var errors = new List<string>();
        var touched = new SortedSet<Guid>();
        var businessId = await t.ExecuteScalarAsync<Guid>("SELECT id FROM core.business LIMIT 1");
        var newCount = new Dictionary<Guid, int>();
        foreach (var e in (req.Entries ?? []).OrderBy(e => e.CustomerId).ThenBy(e => e.CreatedAt))
        {
            if (await t.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM crm.credit_ledger WHERE id = @Id)", new { e.Id })) { acked.Add(e.Id); continue; }
            if (e.BranchId != who.BranchId) { errors.Add($"{e.Number}: written for another branch"); continue; }
            if (!await t.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM crm.credit_account WHERE customer_id = @CustomerId)", new { e.CustomerId }))
            { errors.Add($"{e.Number}: the customer has no credit account at head office"); continue; }
            await LockAsync(t, e.CustomerId);
            await t.ExecuteAsync("""
                INSERT INTO crm.credit_ledger (id, business_id, branch_id, customer_id, balance_type, kind, amount, number, doc_date, source_doc_type, source_doc_id,
                                               note, created_at, created_by, origin_server_id)
                VALUES (@Id, @businessId, @BranchId, @CustomerId, 'receivable', @Kind, @Amount, @Number, @doc::date, @SourceDocType, @SourceDocId, @Note, @CreatedAt,
                        @CreatedBy, @server)
                """, new { e.Id, businessId, e.BranchId, e.CustomerId, e.Kind, e.Amount, e.Number, doc = e.DocDate.ToDateTime(TimeOnly.MinValue), e.SourceDocType,
                           e.SourceDocId, e.Note, e.CreatedAt, e.CreatedBy, server = who.ServerId });
            foreach (var i in e.Items)
                await t.ExecuteAsync("""
                    INSERT INTO crm.receivable_item (id, business_id, branch_id, customer_id, entry_id, seq, due_date, amount, origin_server_id)
                    VALUES (@Id, @businessId, @branch, @customer, @entry, @Seq, @due::date, @Amount, @server)
                    """, new { i.Id, businessId, branch = e.BranchId, customer = e.CustomerId, entry = e.Id, i.Seq, due = i.Due.ToDateTime(TimeOnly.MinValue), i.Amount,
                               server = who.ServerId });
            if (e.Decision is { } d)
                await t.ExecuteAsync("""
                    INSERT INTO crm.credit_decision (id, business_id, branch_id, customer_id, sale_id, entry_id, mode, amount, available, credit_limit, exposure, allowance,
                                                     over_by, overdue_days, approval_id, approved_by, created_at, created_by, origin_server_id)
                    VALUES (@Id, @businessId, @branch, @customer, @sale, @entry, @Mode, @Amount, @Available, @Limit, @Exposure, @Allowance, @OverBy, @OverdueDays,
                            @ApprovalId, @ApprovedBy, @CreatedAt, @CreatedBy, @server)
                    ON CONFLICT (sale_id) DO NOTHING
                    """, new { d.Id, businessId, branch = e.BranchId, customer = e.CustomerId, sale = e.SourceDocId, entry = e.Id, d.Mode, d.Amount, d.Available, d.Limit,
                               d.Exposure, d.Allowance, d.OverBy, d.OverdueDays, d.ApprovalId, d.ApprovedBy, d.CreatedAt, d.CreatedBy, server = who.ServerId });
            if (e.Kind == "charge")
                await t.ExecuteAsync("UPDATE crm.credit_authorisation SET consumed_at = now() WHERE server_id = @s AND sale_id = @SourceDocId AND consumed_at IS NULL",
                    new { s = who.ServerId, e.SourceDocId });
            acked.Add(e.Id);
            touched.Add(e.CustomerId);
            newCount[e.CustomerId] = newCount.GetValueOrDefault(e.CustomerId) + 1;
        }

        if (touched.Count > 0)
        {
            var policy = await PolicyAsync(t);
            var today = BusinessDay.Today(timeZone, t.Clock);
            var hoBranch = t.Server.BranchId ?? await t.ExecuteScalarAsync<Guid>("SELECT id FROM core.branch WHERE is_head_office LIMIT 1");
            var syncId = t.NewId();
            foreach (var customerId in touched)
            {
                await AllocateAsync(t, businessId, customerId, hoBranch, today, "credit_sync", syncId);
                var acc = (await AccountAsync(t, customerId))!;
                var exposure = await ExposureAsync(t, customerId);
                if (exposure > acc.CreditLimit)
                    await t.ExecuteAsync("""
                        INSERT INTO crm.credit_breach (id, business_id, account_id, customer_id, exposure, credit_limit, from_server_id, branch_id, entries)
                        VALUES (@id, @businessId, @account, @customerId, @exposure, @limit, @s, @branch, @n)
                        """, new { id = t.NewId(), businessId, account = acc.Id, customerId, exposure, limit = acc.CreditLimit, s = who.ServerId, branch = who.BranchId,
                                   n = newCount[customerId] });
                await EvaluateHoldAsync(t, acc, policy, timeZone, afterSync: true);
            }
            await t.AuditAsync(new AuditEntry("credit.sync_received", "server", who.ServerId,
                After: Json.Obj(new { entries = acked.Count, customers = touched.Count, errors = errors.Count }), BranchId: who.BranchId));
        }
        return new CreditPushResponse(acked, await SnapshotsAsync(t, 0, who.BranchId, touched.ToArray()), errors);
    }
}
