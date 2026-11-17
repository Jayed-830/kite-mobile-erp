using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;
using static Kite.IntegrationTests.DeviceKit;
using static Kite.IntegrationTests.PosKit;
using static Kite.IntegrationTests.TradeInKit;

namespace Kite.IntegrationTests;

/// <summary>
/// Phase 7 exit gate: "All settlement types post correct journals". Tills work at the same time through every settlement type
/// (credit on a sale with the rest paid; credit on a sale with the excess left as store credit or cash; store credit only;
/// cash buy-back), spend store credit, void sales paid with trade-in credit and resell traded-in phones under the profit
/// margin scheme, while a model in the test keeps what each operation should have posted. Afterwards the accounts, every
/// trade-in's balance, every customer's store credit, every drawer and the VAT match the model, and two races (many tills
/// spending one trade-in's credit, many tills spending one customer's store credit) never overspend.
/// </summary>
[Collection("server")]
public sealed class Phase7ExitGateTests(KiteServerFixture fx, ITestOutputHelper output)
{
    private readonly PosKit kit = new(fx);
    private DeviceKit Dev => new(fx, kit);
    private TradeInKit Ti => new(fx);

    private static int Env(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;
    private static decimal Vat(decimal gross) => Math.Round(gross * 5m / 105m, 3, MidpointRounding.AwayFromZero);
    private static async Task<string?> CodeOfAsync(HttpResponseMessage r) =>
        r.IsSuccessStatusCode ? null : (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

    /// <summary>What the operations that went through should have posted.</summary>
    private sealed class Model
    {
        private readonly Lock _gate = new();
        public decimal UsedStock, Clearing, StoreCredit, Cash, Vat;
        public readonly Dictionary<Guid, decimal> Outstanding = [];
        public readonly Dictionary<Guid, decimal> Balance = [];
        public readonly Dictionary<int, decimal> Drawer = [];
        public readonly Dictionary<Guid, (decimal Cost, Guid Customer, int Till)> Units = [];
        public readonly ConcurrentBag<string> Refusals = [];
        public int Accepted, Settled, Sales, Voids, StoreCreditSpent, MarginSales;

        public void Do(Action a) { lock (_gate) a(); }
    }

    private sealed record Till(int No, HttpClient Pos);

    [PgFact]
    public async Task Tills_settling_trade_ins_every_way_at_once_post_exactly_the_expected_journals()
    {
        var n = Env("KITE_P7_TILLS", 8);
        var rounds = Env("KITE_P7_ROUNDS", 6);
        using var owner = await kit.OwnerAsync();
        await EnableAsync(owner);
        await MarginAsync(owner, true);
        await kit.SetPolicyAsync(owner, cashRounding: 0m);
        var phone = await Dev.PhoneAsync(owner, "Gate phone " + Suffix(), 180m);
        var cable = await kit.ProductAsync(owner, "Gate cable " + Suffix(), 20m, openingQty: 5000, unitCost: 8m);
        var gradeA = await GradeAsync(owner, "A");
        await GuideAsync(owner, phone, gradeA, 200m);
        var customers = new List<Guid>();
        for (var i = 0; i < 4; i++) customers.Add(await CustomerAsync(owner, $"Gate customer {i} {Suffix()}"));
        var tills = new List<Till>();
        for (var i = 0; i < n; i++)
        {
            var pos = await kit.OwnerPosAsync(await kit.TerminalAsync(owner));
            await OpenShiftAsync(pos, 500m);
            tills.Add(new Till(i, pos));
        }
        var m = new Model();
        foreach (var t in tills) m.Drawer[t.No] = 500m;
        foreach (var c in customers) m.Balance[c] = await BalanceAsync(owner, c);
        var gl = await Dev.GlSnapshotAsync("1410", "2320", "2310", "1100", "2200");

        // ---------------------------------------------------------------- every settlement type, at once
        async Task<JsonElement> Sell(Till t, int qty, object[] payments, Guid customer)
        {
            var s = await SellAsync(t.Pos, [Line(cable, qty)], payments, customerId: customer);
            m.Do(() => { m.Vat += Vat(20m * qty); m.Sales++; });
            return s;
        }

        async Task Run(Till t)
        {
            var rnd = new Random(7_000 + t.No);
            for (var r = 0; r < rounds; r++)
            {
                var customer = customers[rnd.Next(customers.Count)];
                var agreed = rnd.Next(20, 150) + rnd.Next(0, 1000) / 1000m;
                var kind = rnd.Next(4);
                decimal drawer = 0;
                m.Do(() => drawer = m.Drawer[t.No]);
                if (kind == 3 && drawer < agreed) kind = 2;   // the drawer only pays out what it holds (that rule is tested elsewhere)
                object? settleNow = kind switch { 2 => new { kind = "store_credit" }, 3 => new { kind = "cash" }, _ => null };
                var ti = await TakeInAsync(t.Pos, customer, phone, gradeA, agreed, settle: settleNow);
                var id = ti.GetProperty("id").GetGuid();
                Assert.True(ti.GetProperty("marginScheme").GetBoolean());
                m.Do(() =>
                {
                    m.Accepted++;
                    m.UsedStock += agreed;
                    m.Clearing -= agreed;
                    m.Outstanding[id] = agreed;
                    m.Units[ti.GetProperty("deviceUnitId").GetGuid()] = (agreed, customer, t.No);
                    if (kind == 2) { m.Clearing += agreed; m.Outstanding[id] = 0; m.StoreCredit -= agreed; m.Balance[customer] += agreed; m.Settled++; }
                    if (kind == 3) { m.Clearing += agreed; m.Outstanding[id] = 0; m.Cash -= agreed; m.Drawer[t.No] -= agreed; m.Settled++; }
                });
                if (kind == 0)
                {
                    // Credit on a sale, the customer pays the rest in cash; sometimes the sale is voided and the credit comes back.
                    var qty = (int)Math.Ceiling(agreed / 20m);
                    var cash = 20m * qty - agreed;
                    var payments = cash > 0 ? new[] { TradeIn(id, agreed), Cash(cash) } : new[] { TradeIn(id, agreed) };
                    var sale = await Sell(t, qty, payments, customer);
                    m.Do(() => { m.Clearing += agreed; m.Outstanding[id] = 0; m.Cash += cash; m.Drawer[t.No] += cash; m.Settled++; });
                    if (rnd.Next(4) == 0)
                    {
                        await Ok(await t.Pos.PostAsJsonAsync($"/api/v1/sales/{sale.GetProperty("id").GetGuid()}/void", new { id = Guid.CreateVersion7(), reason = "gate void" }));
                        m.Do(() => { m.Clearing -= agreed; m.Outstanding[id] = agreed; m.Cash -= cash; m.Drawer[t.No] -= cash; m.Vat -= Vat(20m * qty); m.Voids++; m.Settled--; });
                    }
                }
                else if (kind == 1)
                {
                    // Credit on a smaller sale; the excess is left as store credit or paid in cash.
                    var qty = Math.Max(1, (int)Math.Floor(agreed / 20m) - rnd.Next(0, 2));
                    var applied = Math.Min(agreed, 20m * qty);
                    var cash = 20m * qty - applied;
                    await Sell(t, qty, cash > 0 ? [TradeIn(id, applied), Cash(cash)] : [TradeIn(id, applied)], customer);
                    var rest = agreed - applied;
                    m.Do(() => { m.Clearing += applied; m.Outstanding[id] = rest; m.Cash += cash; m.Drawer[t.No] += cash; if (rest == 0) m.Settled++; });
                    if (rest > 0)
                    {
                        decimal inDrawer = 0;
                        m.Do(() => inDrawer = m.Drawer[t.No]);
                        var asCredit = rnd.Next(2) == 0 || inDrawer < rest;
                        await Ok(await t.Pos.PostAsJsonAsync($"/api/v1/tradeins/{id}/settle", new { kind = asCredit ? "store_credit" : "cash" }));
                        m.Do(() =>
                        {
                            m.Clearing += rest; m.Outstanding[id] = 0; m.Settled++;
                            if (asCredit) { m.StoreCredit -= rest; m.Balance[customer] += rest; }
                            else { m.Cash -= rest; m.Drawer[t.No] -= rest; }
                        });
                    }
                }
                // Now and then the customer spends store credit (several tills may reach for the same balance).
                if (rnd.Next(3) == 0)
                {
                    var who = customers[rnd.Next(customers.Count)];
                    var have = await BalanceAsync(t.Pos, who);
                    if (have >= 1m)
                    {
                        var qty = rnd.Next(1, 4);
                        var use = Math.Min(have, 20m * qty);
                        var cash = 20m * qty - use;
                        var res = await t.Pos.PostAsJsonAsync($"/api/v1/sales/{Guid.CreateVersion7()}/complete", new
                        {
                            lines = new[] { Line(cable, qty) }, payments = cash > 0 ? new[] { StoreCredit(use), Cash(cash) } : new[] { StoreCredit(use) }, customerId = who,
                        });
                        var code = await CodeOfAsync(res);
                        if (code is null)
                            m.Do(() => { m.StoreCredit += use; m.Balance[who] -= use; m.Cash += cash; m.Drawer[t.No] += cash; m.Vat += Vat(20m * qty); m.Sales++; m.StoreCreditSpent++; });
                        else { Assert.Equal("TRD-006", code); m.Refusals.Add(code); }
                    }
                }
            }
        }
        await Task.WhenAll(tills.Select(Run));

        // ---------------------------------------------------------------- races on one trade-in and on one balance
        var raceTill = tills[0];
        var race = await TakeInAsync(raceTill.Pos, customers[0], phone, gradeA, 100m);
        var raceId = race.GetProperty("id").GetGuid();
        m.Do(() => { m.Accepted++; m.UsedStock += 100m; m.Clearing -= 100m; m.Outstanding[raceId] = 100m; m.Units[race.GetProperty("deviceUnitId").GetGuid()] = (100m, customers[0], 0); });
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var spends = tills.Select(async t =>
        {
            await gate.Task;
            return await t.Pos.PostAsJsonAsync($"/api/v1/sales/{Guid.CreateVersion7()}/complete", new
            {
                lines = new[] { Line(cable, 3) }, payments = new[] { TradeIn(raceId, 60m) }, customerId = customers[0],
            });
        }).ToArray();
        gate.SetResult();
        var spent = await Task.WhenAll(spends);
        var codes = await Task.WhenAll(spent.Select(CodeOfAsync));
        Assert.Equal(1, codes.Count(c => c is null));                                   // 60 of 100: only one sale fits
        Assert.All(codes.Where(c => c is not null), c => Assert.Equal("TRD-005", c));
        var winner = codes.ToList().IndexOf(null);
        m.Do(() => { m.Clearing += 60m; m.Outstanding[raceId] = 40m; m.Vat += Vat(60m); m.Sales++; });

        var rich = await CustomerAsync(owner, "Race credit " + Suffix());
        var credit = await TakeInAsync(raceTill.Pos, rich, phone, gradeA, 100m, settle: new { kind = "store_credit" });
        m.Do(() => { m.Accepted++; m.Settled++; m.UsedStock += 100m; m.StoreCredit -= 100m; m.Balance[rich] = 100m; m.Outstanding[credit.GetProperty("id").GetGuid()] = 0;
                     m.Units[credit.GetProperty("deviceUnitId").GetGuid()] = (100m, rich, 0); });
        var gate2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var credits = tills.Select(async t =>
        {
            await gate2.Task;
            return await t.Pos.PostAsJsonAsync($"/api/v1/sales/{Guid.CreateVersion7()}/complete", new
            {
                lines = new[] { Line(cable, 2) }, payments = new[] { StoreCredit(40m) }, customerId = rich,
            });
        }).ToArray();
        gate2.SetResult();
        var creditCodes = await Task.WhenAll((await Task.WhenAll(credits)).Select(CodeOfAsync));
        var wins = creditCodes.Count(c => c is null);
        Assert.Equal(Math.Min(n, 2), wins);                                              // 40 + 40 of 100; a third would go below zero
        Assert.All(creditCodes.Where(c => c is not null), c => Assert.Equal("TRD-006", c));
        m.Do(() => { m.StoreCredit += 40m * wins; m.Balance[rich] -= 40m * wins; m.Vat += Vat(40m) * wins; m.Sales += wins; });

        // ---------------------------------------------------------------- margin-scheme resale of traded-in phones
        var resale = m.Units.Take(Math.Min(m.Units.Count, n * 2)).ToArray();
        foreach (var (unit, _) in resale) await Ok(await InspectAsync(owner, unit, AllPass(), "restock", "used", gradeA, price: 160m));
        await Task.WhenAll(resale.Select(async (u, i) =>
        {
            var t = tills[i % tills.Count];
            await SellAsync(t.Pos, [UnitLine(phone, u.Key)], [Cash(160m)]);
            m.Do(() => { m.UsedStock -= u.Value.Cost; m.Cash += 160m; m.Drawer[t.No] += 160m; m.Vat += Vat(160m - u.Value.Cost); m.MarginSales++; });
        }));

        // ---------------------------------------------------------------- the accounts against the model
        async Task<decimal> Moved(string code) => await Dev.GlAsync(code) - gl[code];
        output.WriteLine($"{n} tills x {rounds} rounds: {m.Accepted} trade-ins accepted, {m.Settled} settled, {m.Sales} sales, {m.Voids} voids, " +
                         $"{m.StoreCreditSpent} store-credit sales ({m.Refusals.Count} refused), {m.MarginSales} margin resales; race winner till {winner}, {wins} credit spends");
        output.WriteLine($"model: used stock {m.UsedStock}, clearing {m.Clearing}, store credit {m.StoreCredit}, cash {m.Cash}, VAT {m.Vat}");
        var table = new (string Account, decimal Expected, decimal Moved)[]
        {
            ("1410 Used device inventory", m.UsedStock, await Moved("1410")), ("2320 Trade-in clearing", m.Clearing, await Moved("2320")),
            ("2310 Store credit", m.StoreCredit, await Moved("2310")), ("1100 Cash", m.Cash, await Moved("1100")), ("2200 Output VAT", -m.Vat, await Moved("2200")),
        };
        if (Environment.GetEnvironmentVariable("KITE_P7_REPORT") is { Length: > 0 } report)
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
            {
                tills = n, rounds, accepted = m.Accepted, settled = m.Settled, sales = m.Sales, voids = m.Voids, storeCreditSales = m.StoreCreditSpent,
                storeCreditRefused = m.Refusals.Count, marginResales = m.MarginSales, tradeInRace = new { tills = n, succeeded = 1 }, storeCreditRace = new { tills = n, succeeded = wins },
                accounts = table.ToDictionary(x => x.Account, x => new { expected = x.Expected, actual = x.Moved }),
            }, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var (account, expected, moved) in table) Assert.True(expected == moved, $"{account}: expected {expected}, moved {moved}");
        // The ledgers agree with the accounts as a whole: clearing is what accepted trade-ins still owe, 2310 is everyone's store credit.
        Assert.Equal(-await Ti.DbAsync<decimal>("SELECT coalesce(sum(tradein.outstanding(id)), 0) FROM tradein.tradein WHERE status = 'accepted'"), await Dev.GlAsync("2320"));
        Assert.Equal(-await Ti.DbAsync<decimal>("SELECT coalesce(sum(amount), 0) FROM crm.credit_ledger WHERE balance_type = 'store_credit'"), await Dev.GlAsync("2310"));
        Assert.Equal(0L, await Ti.DbAsync<long>("SELECT count(*) FROM (SELECT customer_id FROM crm.credit_ledger WHERE balance_type = 'store_credit' GROUP BY 1 HAVING sum(amount) < 0) x"));
        foreach (var (id, left) in m.Outstanding)
        {
            var d = await TradeInAsync(owner, id);
            Assert.Equal(left, d.GetProperty("outstanding").GetDecimal());
            Assert.Equal(left == 0 ? "settled" : "accepted", d.GetProperty("status").GetString());
        }
        foreach (var (c, bal) in m.Balance) Assert.Equal(bal, await BalanceAsync(owner, c));
        foreach (var t in tills) Assert.Equal(m.Drawer[t.No], (await Ti.FiguresAsync(t.Pos)).GetProperty("expectedCash").GetDecimal());
        // Every margin resale was taxed on its margin and nothing else: no margin line without a margin purchase.
        Assert.Equal((long)m.MarginSales, await Ti.DbAsync<long>($"""
            SELECT count(*) FROM sales.sale_line l WHERE l.tax_scheme = 'margin' AND l.device_unit_id IN ({string.Join(',', resale.Select(u => $"'{u.Key}'"))})
               AND l.margin_purchase_price = (SELECT a.cost FROM device.acquisition a WHERE a.device_unit_id = l.device_unit_id ORDER BY a.acquired_at DESC, a.id DESC LIMIT 1)
            """));
        Assert.Equal((long)m.Accepted, await Ti.DbAsync<long>($"SELECT count(*) FROM device.acquisition WHERE device_unit_id IN ({string.Join(',', m.Units.Keys.Select(u => $"'{u}'"))})"));
        await AssertConsistentAsync(owner);
        foreach (var t in tills) t.Pos.Dispose();
    }
}
