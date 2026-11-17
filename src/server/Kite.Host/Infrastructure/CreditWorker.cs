using System.Globalization;
using System.Text.Json;
using Kite.Persistence;
using Kite.Platform;
using Kite.Sales;

namespace Kite.Host.Infrastructure;

/// <summary>
/// The daily check of the books (17): the customer ledger against accounts receivable (1300) and the supplier ledger against
/// supplier payables (2100), branch by branch, for the rows and journals this server wrote. A difference is reported, never
/// corrected.
/// </summary>
public static class Reconciliation
{
    private const string Own = "(SELECT id FROM core.server WHERE is_self)";

    /// <summary>
    /// A control account's balance by branch from this server's own journals, in the ledger's natural sign: debit balances for assets
    /// (receivables, stock, VAT input, work in progress), credit balances for liabilities.
    /// </summary>
    private static string Control(string codes, bool debit, string extra = "") => $"""
        SELECT jl.branch_id, sum({(debit ? "jl.debit - jl.credit" : "jl.credit - jl.debit")})
          FROM acct.journal_line jl JOIN acct.journal j ON j.id = jl.journal_id JOIN acct.account a ON a.id = jl.account_id
         WHERE a.code IN ({codes}) AND j.origin_server_id = {Own} AND jl.branch_id IS NOT NULL {extra}
         GROUP BY jl.branch_id
        """;

    /// <summary>
    /// The daily ledger check (17.8): every subledger against its control account, branch by branch, from what this server wrote itself
    /// (a branch's copy of another server's documents is checked where they were written), then the interbranch accounts across the business
    /// and the trial balance of each branch. A difference is a finding: it is recorded, never corrected here.
    /// </summary>
    public static async Task<object> RunAsync(DbTx t)
    {
        var biz = await BusinessInfo.LoadAsync(t);
        var today = BusinessDay.Today(biz.TimeZone, t.Clock);
        var run = t.NewId();
        var rows = new List<object>();
        var checks = new (string Ledger, string Sub, string Control)[]
        {
            ("customers", $"SELECT branch_id, sum(amount) FROM crm.credit_ledger WHERE balance_type = 'receivable' AND origin_server_id = {Own} GROUP BY branch_id",
                Control("'1300'", debit: true)),
            ("suppliers", $"SELECT branch_id, sum(amount) FROM purch.supplier_ledger WHERE origin_server_id = {Own} GROUP BY branch_id",
                Control("'2100'", debit: false)),
            ("inventory", $"SELECT branch_id, sum(value) FROM inv.stock_movement WHERE origin_server_id = {Own} GROUP BY branch_id",
                Control("'1400','1410','1420','1430'", debit: true)),
            ("grni", $"SELECT branch_id, sum(amount) FROM purch.grni_entry WHERE origin_server_id = {Own} GROUP BY branch_id",
                Control("'2110'", debit: false)),
            // VAT declared from the source documents against the VAT accounts, settlements left out (the VAT report check, 17.4).
            ("vat_output", "SELECT branch_id, sum(vat) FROM tax.vat_lines(@b, DATE '2000-01-01', DATE '2999-12-31') WHERE direction = 'output' GROUP BY branch_id",
                Control("'2200'", debit: false, "AND j.posting_kind <> 'vat.settlement'").Replace($"AND j.origin_server_id = {Own}", "")),
            ("vat_input", "SELECT branch_id, sum(vat) FROM tax.vat_lines(@b, DATE '2000-01-01', DATE '2999-12-31') WHERE direction = 'input' GROUP BY branch_id",
                Control("'1500'", debit: true, "AND j.posting_kind <> 'vat.settlement'").Replace($"AND j.origin_server_id = {Own}", "")),
            ("store_credit", $"SELECT branch_id, sum(amount) FROM crm.credit_ledger WHERE balance_type = 'store_credit' AND origin_server_id = {Own} GROUP BY branch_id",
                Control("'2310'", debit: false)),
            ("deposits", $"""
                SELECT branch_id, sum(CASE WHEN kind = 'deposit' THEN amount - tax ELSE tax - amount END) FROM repair.deposit
                 WHERE origin_server_id = {Own} GROUP BY branch_id
                """, Control("'2300'", debit: false)),
            ("repair_wip", $"""
                SELECT j.branch_id, sum(repair.wip(j.id)) FROM repair.repair_job j WHERE j.origin_server_id = {Own} GROUP BY j.branch_id
                """, Control("'1460'", debit: true)),
            // Each branch's part of every journal balances on its own (ADR 54).
            ("trial_balance", $"""
                SELECT jl.branch_id, sum(jl.debit) FROM acct.journal_line jl JOIN acct.journal j ON j.id = jl.journal_id
                 WHERE j.origin_server_id = {Own} AND jl.branch_id IS NOT NULL GROUP BY jl.branch_id
                """, $"""
                SELECT jl.branch_id, sum(jl.credit) FROM acct.journal_line jl JOIN acct.journal j ON j.id = jl.journal_id
                 WHERE j.origin_server_id = {Own} AND jl.branch_id IS NOT NULL GROUP BY jl.branch_id
                """),
        };
        await t.WithAllBranchesAsync(async () =>
        {
            foreach (var (ledger, sub, control) in checks)
            {
                var subs = (await t.QueryAsync<(Guid Branch, decimal Total)>(sub, new { b = biz.Id })).ToDictionary(x => x.Branch, x => x.Total);
                var controls = (await t.QueryAsync<(Guid Branch, decimal Total)>(control, new { b = biz.Id })).ToDictionary(x => x.Branch, x => x.Total);
                foreach (var branch in subs.Keys.Union(controls.Keys).OrderBy(b => b))
                    rows.Add(await RecordAsync(t, biz.Id, run, today, ledger, branch, subs.GetValueOrDefault(branch), controls.GetValueOrDefault(branch)));
            }
            // Interbranch (14.6): what branches are owed by others equals what the others owe, across the business; consolidation eliminates both.
            var due = await t.QuerySingleOrDefaultAsync<(decimal From, decimal To)>($"""
                SELECT coalesce(sum(jl.debit - jl.credit) FILTER (WHERE a.code = '1310'), 0), coalesce(sum(jl.credit - jl.debit) FILTER (WHERE a.code = '2500'), 0)
                  FROM acct.journal_line jl JOIN acct.journal j ON j.id = jl.journal_id JOIN acct.account a ON a.id = jl.account_id
                 WHERE a.code IN ('1310','2500') AND j.origin_server_id = {Own}
                """);
            rows.Add(await RecordAsync(t, biz.Id, run, today, "interbranch", null, due.From, due.To));
            return 0;
        });
        await t.AuditAsync(new AuditEntry("acct.subledger_checked", "subledger_check", run, After: Json.Obj(new { rows.Count })));
        return new { runId = run, asOf = today, rows };
    }

    private static async Task<object> RecordAsync(DbTx t, Guid businessId, Guid run, DateOnly today, string ledger, Guid? branch, decimal s, decimal c)
    {
        await t.ExecuteAsync("""
            INSERT INTO acct.subledger_check (id, business_id, ledger, branch_id, as_of, subledger, control, difference, run_id)
            VALUES (@id, @businessId, @ledger, @branch, @today::date, @s, @c, @d, @run)
            """, new { id = t.NewId(), businessId, ledger, branch, today = today.ToDateTime(TimeOnly.MinValue), s, c, d = s - c, run });
        return new { ledger, branchId = branch, subledger = s, control = c, difference = s - c };
    }

    /// <summary>The latest run of the ledger check, with branch codes.</summary>
    public static async Task<object> LatestAsync(DbTx t) => await t.WithAllBranchesAsync(async () =>
    {
        var run = await t.ExecuteScalarAsync<Guid?>("SELECT run_id FROM acct.subledger_check ORDER BY checked_at DESC LIMIT 1");
        if (run is null) return (object)new { runId = (Guid?)null, rows = Array.Empty<object>() };
        var rows = await t.RowsAsync("""
            SELECT c.ledger, c.branch_id AS "branchId", b.code AS "branchCode", c.subledger, c.control, c.difference, c.as_of::text AS "asOf", c.checked_at AS "checkedAt"
              FROM acct.subledger_check c LEFT JOIN core.branch b ON b.id = c.branch_id WHERE c.run_id = @run
             ORDER BY array_position(ARRAY['customers','suppliers','inventory','grni','vat_output','vat_input','store_credit','deposits','repair_wip','interbranch','trial_balance'], c.ledger), b.code
            """, new { run });
        return (object)new { runId = run, rows, clean = rows.All(r => (decimal)r["difference"]! == 0) };
    });
}

/// <summary>
/// Head office's credit housekeeping (14.5), every hour: automatic holds for overdue instalments (and lifting them when
/// paid), WhatsApp reminders to customers who agreed to messages, and the daily ledger check (17).
/// </summary>
public sealed class CreditWorker(KiteDb db, ServerContext server, ILogger<CreditWorker> log) : BackgroundService
{
    private DateOnly _lastCheck;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), stop); } catch (OperationCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (server.IsConfigured) await RunOnceAsync(stop);
            }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Credit housekeeping"); }
            try { await Task.Delay(TimeSpan.FromHours(1), stop); } catch (OperationCanceledException) { break; }
        }
    }

    public async Task<(int Holds, int Reminders, bool Checked)> RunOnceAsync(CancellationToken ct)
    {
        var holds = 0;
        var reminders = 0;
        if (server.IsHeadOffice)
        {
            var customers = await db.InTransactionAsync(AccessScope.System, ActorContext.System, async t =>
                (await CreditCore.ModuleOnAsync(t)) ? (await t.QueryAsync<Guid>("SELECT customer_id FROM crm.credit_account WHERE status <> 'closed' ORDER BY customer_id")).ToList() : [], ct);
            foreach (var c in customers)
                holds += await db.InTransactionAsync(AccessScope.System, ActorContext.System, async t =>
                {
                    await CreditCore.LockAsync(t, c);
                    var acc = await CreditCore.AccountAsync(t, c);
                    if (acc is null) return 0;
                    var before = acc.Status;
                    var biz = await BusinessInfo.LoadAsync(t);
                    await CreditCore.EvaluateHoldAsync(t, acc, await CreditCore.PolicyAsync(t), biz.TimeZone, afterSync: false);
                    return acc.Status != before ? 1 : 0;
                }, ct);
            reminders = await db.InTransactionAsync(AccessScope.System, ActorContext.System, RemindAsync, ct);
        }
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var ran = false;
        if (_lastCheck != today)
        {
            await db.InTransactionAsync(AccessScope.System, ActorContext.System, Reconciliation.RunAsync, ct);
            _lastCheck = today;
            ran = true;
        }
        return (holds, reminders, ran);
    }

    /// <summary>Queues WhatsApp reminders (14.5): once before an instalment is due, then every few days while it is overdue; only with consent.</summary>
    public static async Task<int> RemindAsync(DbTx t)
    {
        if (!await CreditCore.ModuleOnAsync(t)) return 0;
        var policy = await CreditCore.PolicyAsync(t);
        if (policy.RemindBeforeDays == 0 && policy.RemindOverdueEvery == 0) return 0;
        var notifOn = await t.ExecuteScalarAsync<bool>("""
            SELECT coalesce((SELECT enabled FROM core.module_setting WHERE module_code = 'notif' AND branch_id IS NULL LIMIT 1), false)
            """);
        if (!notifOn) return 0;
        var biz = await BusinessInfo.LoadAsync(t);
        var today = BusinessDay.Today(biz.TimeZone, t.Clock);
        var items = await t.WithAllBranchesAsync(() => t.QueryAsync<(Guid Item, Guid Customer, Guid Branch, DateTime Due, decimal Open, string Number, string Name,
            string? NameAr, string Phone, string Lang, DateTime OptInAt)>("""
            SELECT i.id, i.customer_id, i.branch_id, i.due_date,
                   i.amount - coalesce((SELECT sum(a.amount) FROM crm.receivable_allocation a WHERE a.item_id = i.id), 0), l.number, c.name_en, c.name_ar, c.phone_e164,
                   c.lang, c.whatsapp_opt_in_at
              FROM crm.receivable_item i JOIN crm.credit_ledger l ON l.id = i.entry_id JOIN crm.customer c ON c.id = i.customer_id
             WHERE c.whatsapp_opt_in AND c.phone_e164 IS NOT NULL
               AND i.amount > coalesce((SELECT sum(a.amount) FROM crm.receivable_allocation a WHERE a.item_id = i.id), 0)
            """));
        var n = 0;
        foreach (var it in items)
        {
            var due = DateOnly.FromDateTime(it.Due);
            var days = today.DayNumber - due.DayNumber;   // past due (negative: still to come)
            string kind;
            int step;
            if (days < 0 && policy.RemindBeforeDays > 0 && -days <= policy.RemindBeforeDays) { kind = "before_due"; step = 0; }
            else if (days > 0 && policy.RemindOverdueEvery > 0 && days >= policy.RemindOverdueEvery) { kind = "overdue"; step = days / policy.RemindOverdueEvery * policy.RemindOverdueEvery; }
            else continue;
            var first = (it.Lang == "ar" && !string.IsNullOrWhiteSpace(it.NameAr) ? it.NameAr! : it.Name).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            var amount = RepairCore.Money(it.Open, it.Lang);
            string[] args = kind == "before_due"
                ? [first, amount, due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), it.Number]
                : [first, amount, days.ToString(CultureInfo.InvariantCulture), it.Number];
            var reminderId = t.NewId();
            var inserted = await t.ExecuteAsync("""
                INSERT INTO crm.credit_reminder (id, business_id, customer_id, item_id, kind, days) VALUES (@reminderId, @b, @Customer, @Item, @kind, @step)
                ON CONFLICT (item_id, kind, days) DO NOTHING
                """, new { reminderId, b = biz.Id, it.Customer, it.Item, kind, step });
            if (inserted == 0) continue;
            var messageId = t.NewId();
            await t.WithAllBranchesAsync(() => t.ExecuteAsync("""
                INSERT INTO notif.outbound_message (id, business_id, branch_id, customer_id, phone_e164, lang, template, params, source_doc_type, source_doc_id,
                                                    opt_in_at, expires_at, origin_server_id)
                VALUES (@messageId, @b, @Branch, @Customer, @Phone, @Lang, @template, @args::jsonb, 'receivable_item', @Item, @OptInAt, now() + interval '24 hours',
                        (SELECT id FROM core.server WHERE is_self))
                """, new { messageId, b = biz.Id, it.Branch, it.Customer, it.Phone, it.Lang, template = kind == "before_due" ? "credit_due" : "credit_overdue",
                           args = JsonSerializer.Serialize(args), it.Item, it.OptInAt }));
            n++;
        }
        return n;
    }
}
