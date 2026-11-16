using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kite.Host.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;
using static Kite.IntegrationTests.PosKit;
using static Kite.IntegrationTests.RepairKit;

namespace Kite.IntegrationTests;

/// <summary>
/// Phase 8 exit gate: "Every status transition, estimate approval, deposit and delivery tested". At several branches at once:
/// every allowed transition passes and every other is refused (all 132 pairs at each branch); then workers run repairs end to end
/// - estimates approved by signature, by phone, by the customer's WhatsApp Approve and within the pre-authorised amount, extra work
/// approved again, waiting for parts, deposits in cash and by card, parts fitted, returned and written off, deliveries paid in cash
/// and by card, cancellations with a diagnosis fee, unrepairable devices with the deposit given back, warranty claims - each trying
/// disallowed transitions on the way, while a model in the test keeps what each step should post. Afterwards every account, the
/// ledgers, every job's history, the stock and every drawer match the model, and two races (many tills collecting one device, many
/// refunding one deposit) do it once.
/// </summary>
[Collection("server")]
public sealed class Phase8ExitGateTests(KiteServerFixture fx, ITestOutputHelper output)
{
    private readonly PosKit kit = new(fx);
    private DeviceKit Dev => new(fx, kit);

    private static int Env(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;
    private static decimal Vat(decimal gross) => Math.Round(gross * 5m / 105m, 3, MidpointRounding.AwayFromZero);
    private const decimal PartCost = 12m;
    private const decimal Fee = 3m;

    private sealed class Model
    {
        private readonly Lock _gate = new();
        public decimal Wip, Parts, Cost, Warranty, WriteOff, Deposits, StoreCredit, Labour, PartsIncome, VatOut, Cash, Card;
        public readonly Dictionary<int, decimal> Drawer = [];
        public readonly Dictionary<Guid, decimal> Stock = [];
        public readonly ConcurrentDictionary<Guid, int> History = new();
        public readonly ConcurrentDictionary<string, int> Counts = new();
        public void Do(Action a) { lock (_gate) a(); }
        public void Count(string what, int n = 1) => Counts.AddOrUpdate(what, n, (_, v) => v + n);
    }

    private sealed record Till(int No, Guid Branch, HttpClient Pos);

    private async Task<Guid> BranchAsync(HttpClient owner, string code)
    {
        var r = await Ok(await owner.PostAsJsonAsync("/api/v1/branches", new { code, nameEn = "Branch " + code, nameAr = "فرع " + code }), HttpStatusCode.Created);
        return r.GetProperty("id").GetGuid();
    }

    private async Task<Till> TillAsync(HttpClient owner, int no, Guid branch)
    {
        var code = "G" + Suffix();
        var key = NewDeviceKey();
        var r = await Ok(await owner.PostAsJsonAsync("/api/v1/terminals", new { branchId = branch, code, name = "Gate till " + code, kind = "browser", deviceKey = key }), HttpStatusCode.Created);
        var id = r.GetProperty("id").GetGuid();
        await Ok(await owner.PostAsJsonAsync($"/api/v1/terminals/{id}/approve", new { emergencyEnabled = false }));
        var pos = await kit.PosAsync(KiteServerFixture.OwnerUsername, KiteServerFixture.OwnerPassword, new Terminal(id, code, key));
        await OpenShiftAsync(pos, 500m);
        return new Till(no, branch, pos);
    }

    /// <summary>Where an estimate's WhatsApp round trip stands, for a failure message.</summary>
    private async Task<string> WhereAsync(Guid est) =>
        await fx.QueryDbAsync<string>($"""
            SELECT concat('estimate ', (SELECT status FROM repair.estimate WHERE id = '{est}'),
                          '; outbound ', (SELECT string_agg(o.status, ',') FROM notif.outbound_message o WHERE o.source_doc_id = '{est}'),
                          '; gateway ', (SELECT string_agg(g.status || ' ' || coalesce(g.wamid, '-'), ',') FROM notif.gateway_message g JOIN notif.outbound_message o ON o.id = g.id WHERE o.source_doc_id = '{est}'),
                          '; events ', (SELECT count(*) FROM notif.gateway_event), ' applied to ', (SELECT events_seq FROM notif.relay_state),
                          '; relay error ', coalesce((SELECT last_error FROM notif.relay_state), 'none'))
            """);

    private async Task CycleAsync()
    {
        var relay = fx.Factory.Services.GetRequiredService<NotifRelay>();
        await relay.RunOnceAsync(CancellationToken.None);
        await fx.Factory.Services.GetRequiredService<WhatsAppGateway>().SendDueAsync(CancellationToken.None);
        await relay.RunOnceAsync(CancellationToken.None);
    }

    [PgFact]
    public async Task Repairs_at_several_branches_at_once_pass_every_allowed_transition_refuse_every_other_and_post_exactly_the_expected_journals()
    {
        var branchCount = Env("KITE_P8_BRANCHES", 3);
        var workersPerBranch = Env("KITE_P8_WORKERS", 2);
        var rounds = Env("KITE_P8_ROUNDS", 4);
        using var owner = await kit.OwnerAsync();
        await EnableAsync(owner, whatsapp: true);
        await kit.SetPolicyAsync(owner, cashRounding: 0m);
        var ws = await Ok(await owner.GetAsync("/api/v1/whatsapp/settings"));
        await Ok(await owner.PutAsJsonAsync("/api/v1/whatsapp/settings", new
        {
            enabled = true, provider = "test", phoneNumberId = (string?)null, wabaId = (string?)null, displayPhone = (string?)null, accessToken = (string?)null,
            appSecret = (string?)null, dailyCap = 100000, staleHours = 24, version = ws.GetProperty("version").GetInt32(),
        }));
        var s = (await SetupAsync(owner)).GetProperty("settings");
        await Ok(await owner.PutAsJsonAsync("/api/v1/repair-setup/settings", new
        {
            preAuthorised = 5m, warrantyDays = 90, passcodeKeepDays = 30, uncollectedDays = new[] { 30, 60, 90 }, diagnosisFee = Fee, hourlyRate = 10m, depositVatPoint = false,
            notifyStatuses = new[] { "received", "waiting_approval", "ready" }, termsEn = s.GetProperty("termsEn").GetString(), termsAr = s.GetProperty("termsAr").GetString(),
            version = s.GetProperty("version").GetInt32(),
        }));
        var category = await CategoryAsync(owner);
        var labour = await ServiceAsync(owner);
        var screen = await kit.ProductAsync(owner, "Gate screen " + Suffix(), 30m, type: "spare_part");

        var branches = new List<Guid>();
        for (var i = 0; i < branchCount; i++)
        {
            var b = await BranchAsync(owner, "R" + Suffix()[..5]);
            branches.Add(b);
            await Ok(await owner.PostAsJsonAsync("/api/v1/inventory/adjustments", new
            {
                branchId = b, kind = "opening", note = "gate parts", lines = new[] { new { variantId = screen.VariantId, qty = 500m, unitCost = PartCost, reasonCode = "opening" } },
            }), HttpStatusCode.Created);
        }
        var tills = new List<Till>();
        foreach (var b in branches)
            for (var w = 0; w < workersPerBranch; w++) tills.Add(await TillAsync(owner, tills.Count, b));
        var m = new Model();
        foreach (var t in tills) m.Drawer[t.No] = 500m;
        foreach (var b in branches) m.Stock[b] = 500m;
        var gl = await Dev.GlSnapshotAsync("1460", "1430", "5200", "6600", "6700", "2300", "2310", "4200", "4210", "2200", "1100", "1150");

        // ---------------------------------------------------------------- A. every transition, at every branch at once
        var matrix = await Task.WhenAll(branches.Select(async b =>
        {
            var t = tills.First(x => x.Branch == b);
            int allowed = 0, refused = 0;
            foreach (var from in Statuses.Where(x => x != "delivered"))
                foreach (var to in Statuses.Where(x => x != from))
                {
                    var c = await TradeInKit.CustomerAsync(t.Pos, "M " + Suffix());
                    var id = Id(await IntakeAsync(t.Pos, c, category, preAuthorised: 50m));
                    await Phase8Tests.ToStatusAsync(t.Pos, id, from, labour);
                    if (from == "in_progress" && to == "waiting_approval") await EstimateAsync(t.Pos, id, Labour(labour, 90m));
                    if (from == "waiting_approval" && to is "in_progress" or "waiting_parts")
                        await Ok(await ApproveRawAsync(t.Pos, id, CurrentEstimate(await DetailAsync(t.Pos, id)), "phone"));
                    HttpResponseMessage r;
                    if (to == "delivered")
                    {
                        var q = await DeliveryAsync(t.Pos, id);
                        var due = q.GetProperty("due").GetDecimal();
                        r = await DeliverRawAsync(t.Pos, id, q.GetProperty("qr").GetString()!, due > 0 ? [Cash(due)] : []);
                        if (r.IsSuccessStatusCode) m.Do(() => { m.Drawer[t.No] += due; m.Cash += due; m.Labour -= due - Vat(due); m.VatOut -= Vat(due); });
                    }
                    else r = await MoveRawAsync(t.Pos, id, to);
                    var ok = Allowed[from].Contains(to);
                    Assert.True(ok ? r.IsSuccessStatusCode : r.StatusCode == HttpStatusCode.Conflict,
                        $"{from} -> {to}: {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
                    if (ok) allowed++; else refused++;
                }
            // From Delivered nothing moves.
            var dc = await TradeInKit.CustomerAsync(t.Pos, "D " + Suffix());
            var did = Id(await IntakeAsync(t.Pos, dc, category, preAuthorised: 50m));
            await Phase8Tests.ToStatusAsync(t.Pos, did, "ready", labour);
            await DeliverAsync(t.Pos, did, [Cash(5m)]);
            m.Do(() => { m.Drawer[t.No] += 5m; m.Cash += 5m; m.Labour -= 5m - Vat(5m); m.VatOut -= Vat(5m); });
            foreach (var to in Statuses.Where(x => x != "delivered"))
            {
                var r = await MoveRawAsync(t.Pos, did, to);
                Assert.True(r.StatusCode == HttpStatusCode.Conflict, $"delivered -> {to}: {(int)r.StatusCode}");
                refused++;
            }
            return (allowed, refused);
        }));
        Assert.All(matrix, x => Assert.Equal((22, 110), x));

        // ---------------------------------------------------------------- B. repairs end to end, at once
        var delivered = new ConcurrentBag<(Guid Job, Guid Branch)>();
        async Task Move(Till t, Guid job, params string[] path)
        {
            foreach (var to in path)
            {
                await Ok(await MoveRawAsync(t.Pos, job, to));
                m.History.AddOrUpdate(job, 2, (_, v) => v + 1);
            }
        }
        async Task TryBad(Till t, Guid job, Random rnd)
        {
            var d = await DetailAsync(t.Pos, job);
            var status = d.GetProperty("job").GetProperty("status").GetString()!;
            var bad = Statuses.Where(x => x != status && x != "delivered" && !Allowed[status].Contains(x)).ToArray();
            var to = bad[rnd.Next(bad.Length)];
            var r = await MoveRawAsync(t.Pos, job, to);
            var body = await r.Content.ReadAsStringAsync();
            Assert.True(r.StatusCode == HttpStatusCode.Conflict && (body.Contains("REP-001") || body.Contains("REP-002") || body.Contains("REP-005")), $"{status} -> {to}: {body}");
            Assert.Equal(status, (await DetailAsync(t.Pos, job)).GetProperty("job").GetProperty("status").GetString());
            m.Count("refused");
        }
        async Task<decimal> Deposit(Till t, Guid job, decimal amount, string method)
        {
            await DepositAsync(t.Pos, job, amount, method);
            m.Do(() =>
            {
                m.Deposits -= amount;
                if (method == "cash") { m.Cash += amount; m.Drawer[t.No] += amount; } else m.Card += amount;
            });
            m.Count("deposits");
            return amount;
        }
        async Task<Guid> Fit(Till t, Guid job, int qty)
        {
            var d = await FitAsync(t.Pos, job, screen, qty);
            m.Do(() => { m.Wip += PartCost * qty; m.Parts -= PartCost * qty; m.Stock[t.Branch] -= qty; });
            m.Count("parts fitted", qty);
            return LastFit(d);
        }
        // Collection: the invoice of the approved estimate (or the fee), the deposit applied, the rest paid; the excess back.
        async Task Deliver(Till t, Guid job, decimal labourGross, decimal partGross, decimal deposit, decimal wip, string kind, string pay, string excessTo = "cash",
            decimal? fee = null)
        {
            var gross = labourGross + partGross;
            var applied = Math.Min(gross, deposit);
            var due = gross - applied;
            var excess = deposit - applied;
            var q = await DeliveryAsync(t.Pos, job, fee);
            Assert.Equal(gross, q.GetProperty("gross").GetDecimal());
            Assert.Equal(due, q.GetProperty("due").GetDecimal());
            object[] payments = due == 0 ? [] : pay == "card" ? [Card(due)] : [Cash(due)];
            await Ok(await DeliverRawAsync(t.Pos, job, q.GetProperty("qr").GetString()!, payments, check: kind == "repaired" ? "qr" : "verified", fee: fee,
                excessTo: excessTo));
            m.History.AddOrUpdate(job, 2, (_, v) => v + 1);
            m.Do(() =>
            {
                m.Labour -= labourGross - Vat(labourGross);
                m.PartsIncome -= partGross - Vat(partGross);
                m.VatOut -= Vat(labourGross) + Vat(partGross);
                m.Deposits += applied + excess;
                if (due > 0) { if (pay == "card") m.Card += due; else { m.Cash += due; m.Drawer[t.No] += due; } }
                if (excess > 0) { if (excessTo == "store_credit") m.StoreCredit -= excess; else { m.Cash -= excess; m.Drawer[t.No] -= excess; } }
                m.Wip -= wip;
                if (kind == "repaired") m.Cost += wip; else if (kind == "warranty") m.Warranty += wip;
            });
            m.Count("delivered");
        }

        async Task<Guid> Repaired(Till t, Random rnd, string approval)
        {
            var labourGross = new[] { 8m, 10m, 15m }[rnd.Next(3)];
            var partGross = new[] { 21m, 30m, 35m }[rnd.Next(3)];
            var c = await TradeInKit.CustomerAsync(t.Pos, "W " + Suffix());
            var job = Id(await IntakeAsync(t.Pos, c, category, preAuthorised: approval == "pre" ? 60m : 5m, whatsapp: approval == "whatsapp"));
            m.History[job] = 1;
            var deposit = rnd.Next(3) switch { 0 => 0m, 1 => await Deposit(t, job, 10m, "cash"), _ => await Deposit(t, job, 50m, "card") };
            await Move(t, job, "inspection_pending", "under_diagnosis");
            await TryBad(t, job, rnd);
            var d = await EstimateAsync(t.Pos, job, Labour(labour, labourGross), Part(screen.VariantId, 1m, partGross));
            var est = CurrentEstimate(d);
            switch (approval)
            {
                case "pre":
                    await Move(t, job, "in_progress");
                    m.Count("approved within the pre-authorised amount");
                    break;
                case "phone":
                    await SendAsync(t.Pos, job, est); m.History.AddOrUpdate(job, 2, (_, v) => v + 1);
                    await Ok(await ApproveRawAsync(t.Pos, job, est, "phone", then: "in_progress")); m.History.AddOrUpdate(job, 2, (_, v) => v + 1);
                    m.Count("approved by phone");
                    break;
                case "signature":
                    await SendAsync(t.Pos, job, est); m.History.AddOrUpdate(job, 2, (_, v) => v + 1);
                    await Ok(await ApproveRawAsync(t.Pos, job, est, "signature", then: "waiting_parts")); m.History.AddOrUpdate(job, 2, (_, v) => v + 1);
                    await TryBad(t, job, rnd);
                    await Move(t, job, "in_progress");
                    m.Count("approved by signature");
                    break;
                case "whatsapp":
                    await SendAsync(t.Pos, job, est); m.History.AddOrUpdate(job, 2, (_, v) => v + 1);
                    // Other workers drive the same gateway at the same time, so wait by the clock (not by a count of cycles) until this
                    // estimate's own message has been sent, then tap Approve on that message by its id, and wait for the approval to come
                    // back through the relay. If it does not, say where it stopped.
                    var deadline = DateTime.UtcNow.AddSeconds(90);
                    var wamidSql = $"SELECT g.wamid FROM notif.gateway_message g JOIN notif.outbound_message o ON o.id = g.id WHERE o.source_doc_id = '{est}' AND g.wamid IS NOT NULL";
                    string? wamid = null;
                    while (DateTime.UtcNow < deadline && (wamid = await fx.QueryDbAsync<string?>(wamidSql)) is null)
                    {
                        await CycleAsync();
                        await Task.Delay(50);
                    }
                    Assert.True(wamid is not null, $"estimate {est}: its WhatsApp message was not sent in 90 s; {await WhereAsync(est)}");
                    var phone = await fx.QueryDbAsync<string>($"SELECT phone_e164 FROM crm.customer WHERE id = '{c}'");
                    await Ok(await owner.PostAsJsonAsync("/api/v1/whatsapp/test-chat", new { phone, action = "button", button = 0, wamid }));
                    while (DateTime.UtcNow < deadline && await fx.QueryDbAsync<string>($"SELECT status FROM repair.estimate WHERE id = '{est}'") != "approved")
                    {
                        await CycleAsync();
                        await Task.Delay(50);
                    }
                    var e = (await DetailAsync(t.Pos, job)).GetProperty("estimates")[0];
                    Assert.True(e.GetProperty("approvalMethod").GetString() == "whatsapp", $"estimate {est}: the Approve tap on {wamid} did not approve it in time; {await WhereAsync(est)}");
                    await Move(t, job, "in_progress");
                    m.Count("approved by WhatsApp");
                    break;
            }
            var wip = PartCost;
            var line = await Fit(t, job, rnd.Next(2) == 0 ? 1 : 2);
            if ((await DetailAsync(t.Pos, job)).GetProperty("money").GetProperty("wip").GetDecimal() == 2 * PartCost)
            {
                await Ok(await t.Pos.PostAsJsonAsync($"/api/v1/repairs/{job}/parts/{line}/return", new { qty = 1m }));
                m.Do(() => { m.Wip -= PartCost; m.Parts += PartCost; m.Stock[t.Branch] += 1; });
                m.Count("parts returned");
            }
            if (rnd.Next(4) == 0)
            {
                // Extra work found: a new version, sent, approved by phone again.
                labourGross += 5m;
                var d2 = await EstimateAsync(t.Pos, job, Labour(labour, labourGross), Part(screen.VariantId, 1m, partGross));
                var v2 = CurrentEstimate(d2);
                await SendAsync(t.Pos, job, v2); m.History.AddOrUpdate(job, 2, (_, v) => v + 1);
                await Ok(await ApproveRawAsync(t.Pos, job, v2, "phone", then: "in_progress")); m.History.AddOrUpdate(job, 2, (_, v) => v + 1);
                m.Count("extra work approved again");
            }
            await Move(t, job, "testing", "completed", "ready");
            await TryBad(t, job, rnd);
            await Deliver(t, job, labourGross, partGross, deposit, wip, "repaired", rnd.Next(2) == 0 ? "cash" : "card");
            delivered.Add((job, t.Branch));
            return job;
        }

        async Task Cancelled(Till t, Random rnd)
        {
            var c = await TradeInKit.CustomerAsync(t.Pos, "X " + Suffix());
            var job = Id(await IntakeAsync(t.Pos, c, category, preAuthorised: 60m));
            m.History[job] = 1;
            await Deposit(t, job, 20m, "cash");
            await Move(t, job, "inspection_pending", "under_diagnosis");
            await EstimateAsync(t.Pos, job, Labour(labour, 10m), Part(screen.VariantId, 1m, 30m));
            await Move(t, job, "in_progress");
            var line = await Fit(t, job, 1);
            var d = await EstimateAsync(t.Pos, job, Labour(labour, 80m));
            await SendAsync(t.Pos, job, CurrentEstimate(d)); m.History.AddOrUpdate(job, 2, (_, v) => v + 1);
            await Move(t, job, "cancelled");
            await TryBad(t, job, rnd);
            await Move(t, job, "ready");
            if (rnd.Next(2) == 0)
            {
                await Ok(await t.Pos.PostAsJsonAsync($"/api/v1/repairs/{job}/parts/{line}/return", new { qty = 1m }));
                m.Do(() => { m.Wip -= PartCost; m.Parts += PartCost; m.Stock[t.Branch] += 1; });
                m.Count("parts returned");
            }
            else
            {
                await Ok(await t.Pos.PostAsJsonAsync($"/api/v1/repairs/{job}/parts/{line}/write-off", new { reason = "Broken on removal" }));
                m.Do(() => { m.Wip -= PartCost; m.WriteOff += PartCost; });
                m.Count("parts written off");
            }
            await Deliver(t, job, Fee, 0m, 20m, 0m, "cancelled", "cash", excessTo: rnd.Next(2) == 0 ? "cash" : "store_credit", fee: Fee);
            m.Count("cancelled");
        }

        async Task Unrepairable(Till t, Random rnd)
        {
            var c = await TradeInKit.CustomerAsync(t.Pos, "U " + Suffix());
            var job = Id(await IntakeAsync(t.Pos, c, category, preAuthorised: 60m));
            m.History[job] = 1;
            await Deposit(t, job, 10m, "card");
            await Move(t, job, "inspection_pending", "under_diagnosis", "unrepairable");
            await TryBad(t, job, rnd);
            // The deposit goes back before collection, in cash; no fee is charged.
            await Ok(await t.Pos.PostAsJsonAsync($"/api/v1/repairs/{job}/deposits/refund", new { amount = 10m, method = "cash", reason = "Board cannot be repaired" }));
            m.Do(() => { m.Deposits += 10m; m.Cash -= 10m; m.Drawer[t.No] -= 10m; });
            m.Count("deposit refunds");
            await Move(t, job, "ready");
            await Deliver(t, job, 0m, 0m, 0m, 0m, "unrepairable", "cash", fee: 0m);
            m.Count("unrepairable");
        }

        async Task Warranty(Till t, Random rnd, Guid original)
        {
            var claim = await Ok(await t.Pos.PostAsJsonAsync($"/api/v1/repairs/{original}/warranty-claim", new
            {
                problem = "Same fault again", passcodeMode = "none", termsName = "Customer", termsSignature = Png,
            }), HttpStatusCode.Created);
            var job = Id(claim.GetProperty("job"));
            m.History[job] = 1;
            await Move(t, job, "inspection_pending", "under_diagnosis", "in_progress");
            await Fit(t, job, 1);
            await Move(t, job, "testing");
            await TryBad(t, job, rnd);
            await Move(t, job, "completed", "ready");
            await Deliver(t, job, 0m, 0m, 0m, PartCost, "warranty", "cash");
            m.Count("warranty claims");
        }

        var started = DateTime.UtcNow;
        await Task.WhenAll(tills.Select(async t =>
        {
            var rnd = new Random(8100 + t.No);
            var mine = new List<Guid>();
            var claimed = new HashSet<Guid>();
            for (var r = 0; r < rounds; r++)
            {
                foreach (var approval in new[] { "pre", "phone", "signature", "whatsapp" }) mine.Add(await Repaired(t, rnd, approval));
                await Cancelled(t, rnd);
                await Unrepairable(t, rnd);
                var orig = mine.First(j => !claimed.Contains(j));
                claimed.Add(orig);
                await Warranty(t, rnd, orig);
            }
        }));
        var elapsed = DateTime.UtcNow - started;

        // ---------------------------------------------------------------- C. races
        var raceBranch = branches[0];
        var raceTills = new List<Till>();
        for (var i = 0; i < Math.Max(4, workersPerBranch * 2); i++) raceTills.Add(await TillAsync(owner, 1000 + i, raceBranch));
        foreach (var rt in raceTills) m.Drawer[rt.No] = 500m;
        var rc = await TradeInKit.CustomerAsync(raceTills[0].Pos, "Race " + Suffix());
        var raceJob = Id(await IntakeAsync(raceTills[0].Pos, rc, category, preAuthorised: 60m));
        await Phase8Tests.ToStatusAsync(raceTills[0].Pos, raceJob, "ready", labour);
        var rq = await DeliveryAsync(raceTills[0].Pos, raceJob);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var collects = raceTills.Select(async rt =>
        {
            await gate.Task;
            return await DeliverRawAsync(rt.Pos, raceJob, rq.GetProperty("qr").GetString()!, [Cash(5m)]);
        }).ToArray();
        gate.SetResult();
        var collected = await Task.WhenAll(collects);
        Assert.Equal(1, collected.Count(r => r.IsSuccessStatusCode));
        Assert.All(collected.Where(r => !r.IsSuccessStatusCode), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        var winner = raceTills[Array.FindIndex(collected, r => r.IsSuccessStatusCode)];
        m.Do(() => { m.Drawer[winner.No] += 5m; m.Cash += 5m; m.Labour -= 5m - Vat(5m); m.VatOut -= Vat(5m); });

        var dc2 = await TradeInKit.CustomerAsync(raceTills[0].Pos, "Race deposit " + Suffix());
        var depJob = Id(await IntakeAsync(raceTills[0].Pos, dc2, category, preAuthorised: 60m));
        await DepositAsync(raceTills[0].Pos, depJob, 30m, "cash");
        m.Do(() => { m.Drawer[raceTills[0].No] += 30m; m.Cash += 30m; m.Deposits -= 30m; });
        var gate2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refunds = raceTills.Select(async rt =>
        {
            await gate2.Task;
            return await rt.Pos.PostAsJsonAsync($"/api/v1/repairs/{depJob}/deposits/refund", new { amount = 20m, method = "cash", reason = "race" });
        }).ToArray();
        gate2.SetResult();
        var refunded = await Task.WhenAll(refunds);
        Assert.Equal(1, refunded.Count(r => r.IsSuccessStatusCode));
        Assert.All(refunded.Where(r => !r.IsSuccessStatusCode), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        var refundWinner = raceTills[Array.FindIndex(refunded, r => r.IsSuccessStatusCode)];
        m.Do(() => { m.Drawer[refundWinner.No] -= 20m; m.Cash -= 20m; m.Deposits += 20m; });
        Assert.Equal(10m, (await DetailAsync(owner, depJob)).GetProperty("money").GetProperty("deposit").GetDecimal());

        // ---------------------------------------------------------------- the accounts against the model
        async Task<decimal> Moved(string code) => await Dev.GlAsync(code) - gl[code];
        var counts = string.Join(", ", m.Counts.OrderBy(x => x.Key).Select(x => $"{x.Value} {x.Key}"));
        output.WriteLine($"{branchCount} branches x {workersPerBranch} workers x {rounds} rounds in {elapsed.TotalSeconds:0}s: {counts}");
        var table = new (string Account, decimal Expected, decimal Moved)[]
        {
            ("1460 Repair work in progress", m.Wip, await Moved("1460")), ("1430 Spare parts", m.Parts, await Moved("1430")),
            ("5200 Cost of repair parts", m.Cost, await Moved("5200")), ("6600 Warranty expense", m.Warranty, await Moved("6600")),
            ("6700 Write-offs", m.WriteOff, await Moved("6700")), ("2300 Customer deposits", m.Deposits, await Moved("2300")),
            ("2310 Store credit", m.StoreCredit, await Moved("2310")), ("4200 Repair labour income", m.Labour, await Moved("4200")),
            ("4210 Repair parts income", m.PartsIncome, await Moved("4210")), ("2200 Output VAT", m.VatOut, await Moved("2200")),
            ("1100 Cash", m.Cash, await Moved("1100")), ("1150 Card clearing", m.Card, await Moved("1150")),
        };
        foreach (var (a, e, mv) in table) output.WriteLine($"  {a}: expected {e}, moved {mv}");
        if (Environment.GetEnvironmentVariable("KITE_P8_REPORT") is { Length: > 0 } report)
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
            {
                branches = branchCount, workersPerBranch, rounds, seconds = (int)elapsed.TotalSeconds,
                transitions = new { perBranch = new { allowed = 22, refused = 110 }, branches = branchCount },
                counts = m.Counts.OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Value),
                races = new { collect = new { tills = raceTills.Count, succeeded = 1 }, depositRefund = new { tills = raceTills.Count, succeeded = 1 } },
                accounts = table.ToDictionary(x => x.Account, x => new { expected = x.Expected, actual = x.Moved }),
            }, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var (account, expected, moved) in table) Assert.True(expected == moved, $"{account}: expected {expected}, moved {moved}");

        // The ledgers agree with the accounts as a whole.
        Assert.Equal(await fx.QueryDbAsync<decimal>("SELECT coalesce(sum(repair.wip(id)), 0) FROM repair.repair_job"), await Dev.GlAsync("1460"));
        Assert.Equal(-await fx.QueryDbAsync<decimal>("SELECT coalesce(sum(amount), 0) FROM crm.credit_ledger WHERE balance_type = 'deposit'"), await Dev.GlAsync("2300"));
        Assert.Equal(0L, await fx.QueryDbAsync<long>("SELECT count(*) FROM repair.deposit_mismatches()"));
        Assert.Equal(0L, await fx.QueryDbAsync<long>("SELECT count(*) FROM repair.repair_job WHERE status = 'delivered' AND repair.wip(id) <> 0"));
        // Every job's history has exactly its status changes.
        foreach (var (job, rows) in m.History)
            Assert.Equal((long)rows, await fx.QueryDbAsync<long>($"SELECT count(*) FROM repair.status_history WHERE job_id = '{job}'"));
        foreach (var b in branches) Assert.Equal(m.Stock[b], await fx.QueryDbAsync<decimal>($"SELECT qty FROM inv.stock_balance WHERE branch_id = '{b}' AND variant_id = '{screen.VariantId}'"));
        foreach (var t in tills.Concat(raceTills))
            Assert.Equal(m.Drawer[t.No], (await Ok(await t.Pos.GetAsync("/api/v1/pos/shifts/current"))).GetProperty("figures").GetProperty("expectedCash").GetDecimal());
        await DeviceKit.AssertConsistentAsync(owner);
        foreach (var t in tills.Concat(raceTills)) t.Pos.Dispose();
    }
}
