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
    public static async Task<object> RunAsync(DbTx t)
    {
        var biz = await BusinessInfo.LoadAsync(t);
        var today = BusinessDay.Today(biz.TimeZone, t.Clock);
        var run = t.NewId();
        var rows = new List<object>();
        foreach (var (ledger, sub, code) in new[]
        {
            ("customers", "SELECT branch_id, sum(amount) FROM crm.credit_ledger WHERE balance_type = 'receivable' AND origin_server_id = (SELECT id FROM core.server WHERE is_self) GROUP BY branch_id", "1300"),
            ("suppliers", "SELECT branch_id, sum(amount) FROM purch.supplier_ledger WHERE origin_server_id = (SELECT id FROM core.server WHERE is_self) GROUP BY branch_id", "2100"),
        })
        {
            var subs = (await t.QueryAsync<(Guid Branch, decimal Total)>(sub)).ToDictionary(x => x.Branch, x => x.Total);
            // Receivable is a debit balance, payables a credit balance: each ledger is compared in its own sign.
            var controls = (await t.QueryAsync<(Guid Branch, decimal Total)>("""
                SELECT jl.branch_id, sum(CASE WHEN @code = '1300' THEN jl.debit - jl.credit ELSE jl.credit - jl.debit END)
                  FROM acct.journal_line jl JOIN acct.journal j ON j.id = jl.journal_id JOIN acct.account a ON a.id = jl.account_id
                 WHERE a.code = @code AND j.origin_server_id = (SELECT id FROM core.server WHERE is_self) AND jl.branch_id IS NOT NULL
                 GROUP BY jl.branch_id
                """, new { code })).ToDictionary(x => x.Branch, x => x.Total);
            foreach (var branch in subs.Keys.Union(controls.Keys).OrderBy(b => b))
            {
                var s = subs.GetValueOrDefault(branch);
                var c = controls.GetValueOrDefault(branch);
                await t.ExecuteAsync("""
                    INSERT INTO acct.subledger_check (id, business_id, ledger, branch_id, as_of, subledger, control, difference, run_id)
                    VALUES (@id, @b, @ledger, @branch, @today::date, @s, @c, @d, @run)
                    """, new { id = t.NewId(), b = biz.Id, ledger, branch, today = today.ToDateTime(TimeOnly.MinValue), s, c, d = s - c, run });
                rows.Add(new { ledger, branchId = branch, subledger = s, control = c, difference = s - c });
            }
        }
        await t.AuditAsync(new AuditEntry("acct.subledger_checked", "subledger_check", run, After: Json.Obj(new { rows.Count })));
        return new { runId = run, asOf = today, rows };
    }
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
