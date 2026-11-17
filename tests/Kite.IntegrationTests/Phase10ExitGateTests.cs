using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kite.Host.Infrastructure;
using Kite.Sales;
using Xunit;
using Xunit.Abstractions;
using static Kite.IntegrationTests.CreditKit;
using static Kite.IntegrationTests.PosKit;

namespace Kite.IntegrationTests;

/// <summary>
/// Phase 10 exit gate (blueprint 25, row 10): "Online and offline credit rules pass; statements reconcile". Test table (23):
/// "Customer credit | Online limit; two branches offline both selling on credit | Allowances respected offline; breach detected
/// and hold applied on sync".
///
/// 1. Online: head office tills (cashiers without CREDIT.OVERRIDE) and branch servers asking head office race to sell on account
///    to the same customers. Head office decides under each customer's lock, so no customer ever owes more than the limit;
///    every sale written has its charge, instalment and decision; the branch servers' reservations become charges when pushed.
/// 2. Offline: branch servers (this test, over mutual TLS, applying exactly the rule a branch server applies: CreditMath) sell on
///    their copies — each within the lower of its allowance and the limit less the last known exposure, and the per-sale cap.
///    Pushed together, head office finds every customer whose exposure passed the limit: the breach is recorded, the account
///    goes on hold, the hold reaches every branch with its heartbeat and head office refuses online; a customer still within the
///    limit stays active. Payments that bring a customer back under the limit lift the automatic hold.
/// 3. Statements reconcile: each statement's closing balance is opening + lines and equals the customer ledger; head office's own
///    ledger rows equal receivables (1300) customer by customer and branch by branch.
/// Size: KITE_P10_TILLS (6), KITE_P10_BRANCHES (2), KITE_P10_CUSTOMERS (6), KITE_P10_ATTEMPTS (8 per till and branch server per
/// customer). KITE_P10_REPORT writes a JSON report.
/// </summary>
[Collection("server")]
public sealed class Phase10ExitGateTests(KiteServerFixture fx, ITestOutputHelper output)
{
    private readonly PosKit kit = new(fx);
    private CreditKit Credit => new(fx, kit);

    private static int Env(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;

    private sealed record Till(int No, HttpClient Pos);

    private async Task<Till> TillAsync(HttpClient owner, int no)
    {
        var cashier = await kit.UserAsync(owner, $"gate{no}", "senior_cashier");
        var pos = await kit.PosAsync(cashier.Username, cashier.Password, await kit.TerminalAsync(owner));
        await OpenShiftAsync(pos, 100m);
        return new Till(no, pos);
    }

    [PgFact]
    public async Task Online_limits_hold_under_racing_tills_and_branch_servers_and_offline_breaches_are_held_on_sync()
    {
        var tills = Env("KITE_P10_TILLS", 6);
        var branchCount = Math.Max(2, Env("KITE_P10_BRANCHES", 2));
        var customerCount = Math.Max(4, Env("KITE_P10_CUSTOMERS", 6));
        var attempts = Env("KITE_P10_ATTEMPTS", 8);
        var counts = new ConcurrentDictionary<string, int>();
        void Count(string what, int n = 1) => counts.AddOrUpdate(what, n, (_, v) => v + n);
        var watch = Stopwatch.StartNew();
        var rnd = new Random(20261117);

        using var owner = await kit.OwnerAsync();
        await EnableAsync(owner);
        await kit.SetPolicyAsync(owner, cashRounding: 0m);
        var unit = await Credit.UnitServiceAsync(owner);
        var tillList = new List<Till>();
        for (var i = 0; i < tills; i++) tillList.Add(await TillAsync(owner, i + 1));
        var branches = new List<CentralKit.Branch>();
        for (var i = 0; i < branchCount; i++) branches.Add(await Credit.Central.RegisterBranchAsync(owner));
        var counter = tillList[0].Pos;

        // Customers: half race online, half are sold to offline. Limits 300 to 800; allowances split the limit among the branch servers.
        var customers = new List<(Guid Id, decimal Limit, Dictionary<Guid, decimal> Allowance, bool Online)>();
        for (var i = 0; i < customerCount; i++)
        {
            var limit = rnd.Next(30, 81) * 10m;
            var shares = branches.Select(_ => rnd.Next(2, 6)).ToArray();
            var allowance = branches.Select((b, k) => (b.BranchId, decimal.Floor(limit * shares[k] / shares.Sum()))).ToDictionary(x => x.BranchId, x => x.Item2);
            var id = await TradeInKit.CustomerAsync(counter, $"Gate credit {i} {Suffix()}");
            await AccountAsync(owner, id, limit, 30, allowance.Select(a => (a.Key, a.Value)).ToArray());
            customers.Add((id, limit, allowance, i % 2 == 0));
        }

        // ---------------------------------------------------------------- 1. online: tills and branch servers race
        var tillOk = new ConcurrentBag<(Guid Customer, decimal Amount)>();
        var reserved = new ConcurrentBag<(CentralKit.Branch Branch, Guid Customer, Guid Sale, decimal Amount)>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new List<Task>();
        foreach (var c in customers.Where(c => c.Online))
        {
            foreach (var t in tillList)
            {
                var amounts = Enumerable.Range(0, attempts).Select(_ => rnd.Next(10, 121) * 1m).ToArray();
                work.Add(Task.Run(async () =>
                {
                    await gate.Task;
                    foreach (var a in amounts)
                    {
                        var r = await SellOnAccountAsync(t.Pos, unit, a, c.Id);
                        if (r.IsSuccessStatusCode) { tillOk.Add((c.Id, a)); Count("online_till_sales"); continue; }
                        var code = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();
                        Assert.True(code is "CRD-004" or "CRD-003", $"till refusal {code}");
                        Count("online_till_refused");
                    }
                }));
            }
            foreach (var b in branches)
            {
                var amounts = Enumerable.Range(0, attempts).Select(_ => rnd.Next(10, 121) * 1m).ToArray();
                work.Add(Task.Run(async () =>
                {
                    await gate.Task;
                    foreach (var a in amounts)
                    {
                        var sale = Guid.CreateVersion7();
                        var r = await Credit.AuthoriseAsync(b, c.Id, sale, a);
                        if (r.GetProperty("ok").GetBoolean()) { reserved.Add((b, c.Id, sale, a)); Count("online_branch_reserved"); }
                        else
                        {
                            Assert.Contains(r.GetProperty("outcome").GetString(), new[] { "needs_approval", "over_cap" });
                            Count("online_branch_refused");
                        }
                    }
                }));
            }
        }
        gate.SetResult();
        await Task.WhenAll(work);

        foreach (var c in customers.Where(c => c.Online))
        {
            var exposure = await fx.QueryDbAsync<decimal>($"SELECT crm.credit_exposure('{c.Id}')");
            var expected = tillOk.Where(x => x.Customer == c.Id).Sum(x => x.Amount) + reserved.Where(x => x.Customer == c.Id).Sum(x => x.Amount);
            Assert.Equal(expected, exposure);
            Assert.True(exposure <= c.Limit, $"customer owes {exposure} against a limit of {c.Limit}");
            Assert.True(c.Limit - exposure < 10m * tills, "the limit was reached: the race was real");
            Assert.Equal((long)tillOk.Count(x => x.Customer == c.Id), await fx.QueryDbAsync<long>($"SELECT count(*) FROM crm.credit_decision WHERE customer_id = '{c.Id}' AND mode = 'local'"));
        }

        // The branch servers write those sales and push them: each reservation becomes its charge; nothing is counted twice.
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(4));
        foreach (var g in reserved.GroupBy(r => r.Branch))
        {
            var entries = g.Select(r =>
            {
                var e = Charge(g.Key, r.Customer, r.Amount, "online", 0m, customers.Single(c => c.Id == r.Customer).Limit, 0m, null, today);
                return e with { SourceDocId = r.Sale, Decision = e.Decision! with { Mode = "online" } };
            }).ToArray();
            Assert.Equal(entries.Length, (await Credit.PushAsync(g.Key, entries)).GetProperty("acked").GetArrayLength());
        }
        foreach (var c in customers.Where(c => c.Online))
        {
            Assert.Equal(tillOk.Where(x => x.Customer == c.Id).Sum(x => x.Amount) + reserved.Where(x => x.Customer == c.Id).Sum(x => x.Amount),
                await fx.QueryDbAsync<decimal>($"SELECT crm.credit_exposure('{c.Id}')"));
            Assert.Equal(0L, await fx.QueryDbAsync<long>($"SELECT count(*) FROM crm.credit_authorisation WHERE customer_id = '{c.Id}' AND consumed_at IS NULL AND released_at IS NULL"));
            Assert.Equal("active", (await DetailAsync(owner, c.Id)).GetProperty("account").GetProperty("status").GetString());
        }

        // ---------------------------------------------------------------- 2. offline: each branch on its own copy, then sync
        // Head office sells about half of each offline customer's limit first, so the last known exposure is real.
        var offline = customers.Where(c => !c.Online).ToList();
        foreach (var c in offline) await Ok(await SellOnAccountAsync(counter, unit, decimal.Floor(c.Limit / 2), c.Id));
        var seqs = new Dictionary<Guid, long>();
        var copies = new Dictionary<(Guid Branch, Guid Customer), (decimal Exposure, decimal Allowance, decimal Limit, decimal Cap)>();
        foreach (var b in branches)
        {
            var hb = await Credit.HeartbeatAsync(b);
            seqs[b.ServerId] = hb.GetProperty("credit").EnumerateArray().Select(s => s.GetProperty("seq").GetInt64()).DefaultIfEmpty(0).Max();
            var cap = hb.GetProperty("creditPolicy").GetProperty("offlineSaleCap").GetDecimal();
            foreach (var c in offline)
            {
                var s = Snapshot(hb, c.Id)!.Value;
                Assert.Equal(c.Allowance[b.BranchId], s.GetProperty("allowance").GetDecimal());
                copies[(b.BranchId, c.Id)] = (s.GetProperty("exposure").GetDecimal(), s.GetProperty("allowance").GetDecimal(), s.GetProperty("creditLimit").GetDecimal(), cap);
            }
        }
        // The last offline customer is sold to by one branch only, a little: it must stay active.
        var quiet = offline[^1];
        var pushes = new Dictionary<CentralKit.Branch, List<CreditEntryPush>>();
        var soldOffline = new Dictionary<(Guid Branch, Guid Customer), decimal>();
        foreach (var b in branches)
        {
            var list = new List<CreditEntryPush>();
            foreach (var c in offline)
            {
                if (c.Id == quiet.Id && b != branches[0]) continue;
                var (exposure, allowance, limit, cap) = copies[(b.BranchId, c.Id)];
                var since = 0m;
                for (var k = 0; k < (c.Id == quiet.Id ? 1 : 40); k++)
                {
                    var available = CreditMath.AvailableOffline(allowance, limit, exposure, since, cap);
                    var amount = Math.Min(available, rnd.Next(2, 11) * 10m);
                    if (c.Id == quiet.Id) amount = Math.Min(available, 10m);
                    if (amount <= 0) break;
                    Assert.Equal(CreditOutcome.Ok, CreditMath.Decide(amount, available, 50m, 0, 30, "active", false).Outcome);
                    list.Add(Charge(b, c.Id, amount, "offline", available, limit, exposure + since, allowance, today));
                    since += amount;
                }
                // Allowances respected offline: never more than the allowance, nor than the limit less what was known to be owed.
                Assert.True(since <= allowance, $"{b.Code} sold {since} against an allowance of {allowance}");
                Assert.True(since <= limit - exposure, $"{b.Code} sold {since}, more than {limit - exposure} left on the limit");
                soldOffline[(b.BranchId, c.Id)] = since;
                Count("offline_sales", list.Count(e => e.CustomerId == c.Id));
            }
            pushes[b] = list;
        }
        var syncGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var syncs = pushes.Select(async p => { await syncGate.Task; return await Credit.PushAsync(p.Key, p.Value.ToArray()); }).ToArray();
        syncGate.SetResult();
        foreach (var r in await Task.WhenAll(syncs)) Assert.Empty(r.GetProperty("errors").EnumerateArray());

        foreach (var c in offline)
        {
            var known = copies[(branches[0].BranchId, c.Id)].Exposure;
            var total = known + branches.Sum(b => soldOffline.GetValueOrDefault((b.BranchId, c.Id)));
            Assert.Equal(total, await fx.QueryDbAsync<decimal>($"SELECT crm.credit_exposure('{c.Id}')"));
            var acc = (await DetailAsync(owner, c.Id)).GetProperty("account");
            var breaches = await fx.QueryDbAsync<long>($"SELECT count(*) FROM crm.credit_breach WHERE customer_id = '{c.Id}'");
            if (total > c.Limit)
            {
                Assert.Equal("hold", acc.GetProperty("status").GetString());
                Assert.Equal("exposure", acc.GetProperty("holdKind").GetString());
                Assert.True(breaches >= 1, "the breach is recorded");
                Count("breaches");
                // Every branch learns of the hold with its next heartbeat; head office refuses it online, approved or not.
                foreach (var b in branches)
                    Assert.Equal("hold", Snapshot(await Credit.HeartbeatAsync(b, seqs[b.ServerId]), c.Id)!.Value.GetProperty("status").GetString());
                Assert.Equal("hold", (await Credit.AuthoriseAsync(branches[^1], c.Id, Guid.CreateVersion7(), 1m, approved: true)).GetProperty("outcome").GetString());
                await Problem(await SellOnAccountAsync(counter, unit, 1m, c.Id), HttpStatusCode.Conflict, "CRD-002");
            }
            else
            {
                Assert.Equal("active", acc.GetProperty("status").GetString());
                Assert.Equal(0L, breaches);
                Count("within_limit_after_sync");
            }
        }
        Assert.True(counts.GetValueOrDefault("breaches") >= offline.Count - 1, "two branches selling offline on their own allowances pass the limit together");
        Assert.Equal("active", (await DetailAsync(owner, quiet.Id)).GetProperty("account").GetProperty("status").GetString());

        // Payments at head office bring the held customers back under the limit: the automatic hold lifts.
        foreach (var c in offline.Where(c => c.Id != quiet.Id))
        {
            var owed = await fx.QueryDbAsync<decimal>($"SELECT crm.credit_exposure('{c.Id}')");
            await Ok(await counter.PostAsJsonAsync("/api/v1/credit/payments", new { id = Guid.CreateVersion7(), customerId = c.Id, method = "card", amount = owed - c.Limit + 10m,
                cardTerminal = "BANK-1", approvalCode = "A" + Suffix() }));
            Assert.Equal("active", (await DetailAsync(owner, c.Id)).GetProperty("account").GetProperty("status").GetString());
            Count("holds_lifted_by_payment");
        }

        // ---------------------------------------------------------------- 3. statements reconcile
        foreach (var c in customers)
        {
            var st = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/credit/customers/{c.Id}/statement?from={today.AddDays(-1):yyyy-MM-dd}&to={today:yyyy-MM-dd}");
            var lines = st.GetProperty("lines").EnumerateArray().ToList();
            Assert.Equal(st.GetProperty("opening").GetDecimal() + lines.Sum(l => l.GetProperty("debit").GetDecimal() - l.GetProperty("credit").GetDecimal()), st.GetProperty("closing").GetDecimal());
            Assert.Equal(await fx.QueryDbAsync<decimal>($"SELECT coalesce(sum(amount), 0) FROM crm.credit_ledger WHERE customer_id = '{c.Id}' AND balance_type = 'receivable'"),
                st.GetProperty("closing").GetDecimal());
            // Head office's own rows are its receivables, with the customer as the party.
            Assert.Equal(
                await fx.QueryDbAsync<decimal>($"SELECT coalesce(sum(amount), 0) FROM crm.credit_ledger WHERE customer_id = '{c.Id}' AND balance_type = 'receivable' AND origin_server_id = (SELECT id FROM core.server WHERE is_self)"),
                await fx.QueryDbAsync<decimal>($"SELECT coalesce(sum(jl.debit - jl.credit), 0) FROM acct.journal_line jl JOIN acct.account a ON a.id = jl.account_id WHERE a.code = '1300' AND jl.party_id = '{c.Id}'"));
            Count("statements_reconciled");
        }
        var run = await Ok(await owner.PostAsync("/api/v1/credit/reconciliation/run", null));
        Assert.All(run.GetProperty("rows").EnumerateArray(), r => Assert.Equal(0m, r.GetProperty("difference").GetDecimal()));
        Count("ledger_checks", run.GetProperty("rows").GetArrayLength());

        watch.Stop();
        output.WriteLine(JsonSerializer.Serialize(counts.OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Value)));
        if (Environment.GetEnvironmentVariable("KITE_P10_REPORT") is { Length: > 0 } report)
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
            {
                tills, branchServers = branchCount, customers = customerCount, attempts, seconds = (int)watch.Elapsed.TotalSeconds,
                counts = counts.OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Value),
            }, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var b in branches) await Credit.Central.DeactivateAsync(owner, b);
        foreach (var t in tillList) t.Pos.Dispose();
    }
}
