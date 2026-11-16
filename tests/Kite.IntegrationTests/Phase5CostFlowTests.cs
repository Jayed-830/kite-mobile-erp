using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;
using static Kite.IntegrationTests.DeviceKit;
using static Kite.IntegrationTests.PosKit;

namespace Kite.IntegrationTests;

/// <summary>
/// The Phase 5 exit gate (blueprint 25, row 5): "Conditions, inspections, refurbishment costs flow to unit cost".
///
/// Several workers at once each take phones through random lives: received new or used, inspected (with the
/// checklist's suggestion or a reasoned override, sometimes changing a new phone to used), refurbished with parts,
/// labour and outside work (with mistakes taken back), sold at the till, voided, returned, inspected again, set aside
/// for parts and written off. The test keeps its own record of every phone's cost, status, condition and history,
/// and of what each step must do to the accounts, then checks the server against it: every phone's cost and state,
/// every sale line's cost and profit, the spare-parts ledger, every inventory, cost, revenue and clearing account,
/// and the consistency check. Size: KITE_P5_WORKERS (4) x KITE_P5_PHONES (5).
/// </summary>
[Collection("server")]
public sealed class Phase5CostFlowTests(KiteServerFixture fx, ITestOutputHelper output)
{
    private readonly PosKit kit = new(fx);

    private sealed class Phone
    {
        public required Guid Id; public required string Imei; public required PosKit.Item Item;
        public decimal Cost; public string Status = ""; public string Condition = ""; public int Events;
        public readonly List<(Guid SaleId, decimal Net, decimal Cost)> Sales = [];
    }

    /// <summary>What the steps must do to each account, as debit minus credit.</summary>
    private sealed class Ledger
    {
        private readonly ConcurrentDictionary<string, decimal> _d = new();
        public void Add(string account, decimal amount) => _d.AddOrUpdate(account, amount, (_, v) => v + amount);
        public decimal this[string account] => _d.GetValueOrDefault(account);
    }

    private static int Env(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;
    private static string Inv(string condition) => condition == "brand_new" ? "1400" : "1410";
    private static string Cogs(string condition) => condition == "brand_new" ? "5100" : "5110";
    private static string Rev(string condition) => condition == "brand_new" ? "4100" : "4110";
    private static decimal Net(decimal gross) => gross - Math.Round(gross * 5m / 105m, 3, MidpointRounding.AwayFromZero);   // 5% VAT included
    private static decimal Labour(int minutes, decimal rate) => Math.Round(minutes * rate / 60m, 3, MidpointRounding.AwayFromZero);

    [PgFact]
    public async Task Conditions_inspections_and_refurbishment_costs_flow_to_unit_cost_under_concurrent_work()
    {
        var workers = Env("KITE_P5_WORKERS", 4);
        var phonesEach = Env("KITE_P5_PHONES", 5);
        using var owner = await kit.OwnerAsync();
        await EnableRefurbAsync(owner);
        await kit.SetPolicyAsync(owner, cashRounding: 0m);
        var settings = await owner.GetFromJsonAsync<JsonElement>("/api/v1/devices/settings");
        const decimal rate = 3.600m;
        await Ok(await owner.PutAsJsonAsync("/api/v1/devices/settings", new
        {
            labourRatePerHour = rate, reservationMinutes = 15, parkedReservationMinutes = 240, suggestedPriceStep = 0.100m, version = settings.GetProperty("version").GetInt32(),
        }), HttpStatusCode.NoContent);
        var dev = new DeviceKit(fx, kit);
        var models = new[] { await dev.PhoneAsync(owner, "Model P5-A 256GB", 289.000m), await dev.PhoneAsync(owner, "Model P5-B 128GB", 149.500m) };
        var parts = new[]
        {
            await kit.ProductAsync(owner, "P5 battery", 15m, type: "spare_part", openingQty: 400, unitCost: 6.500m),
            await kit.ProductAsync(owner, "P5 screen", 45m, type: "spare_part", openingQty: 150, unitCost: 23.333m),
            await kit.ProductAsync(owner, "P5 charging port", 5m, type: "spare_part", openingQty: 300, unitCost: 1.725m),
        };
        await kit.OpeningAsync(owner, parts[1], 50, 24.100m);   // a second cost: the screen's average is no longer round
        var grades = new[] { await GradeAsync(owner, "A"), await GradeAsync(owner, "B"), await GradeAsync(owner, "C") };
        var accounts = new[] { "1400", "1410", "1430", "5100", "5110", "4100", "4110", "4900", "6210", "2110", "6700" };
        var before = await dev.GlSnapshotAsync(accounts);
        var partValueBefore = await PartValueAsync(parts);

        var expected = new Ledger();
        var phones = new ConcurrentBag<Phone>();
        var partAmounts = new ConcurrentBag<decimal>();   // net value of parts taken from stock per refurbishment (issues minus parts taken back)
        var counts = new ConcurrentDictionary<string, int>();
        void Count(string what) => counts.AddOrUpdate(what, 1, (_, v) => v + 1);
        var sw = Stopwatch.StartNew();

        await Task.WhenAll(Enumerable.Range(0, workers).Select(async w =>
        {
            var rnd = new Random(20261116 + w);
            var term = await kit.TerminalAsync(owner);
            using var pos = await kit.OwnerPosAsync(term);
            await OpenShiftAsync(pos, 0m);
            using var back = fx.Client(await fx.OwnerTokenAsync());   // the licence is already imported

            for (var n = 0; n < phonesEach; n++)
            {
                var item = models[rnd.Next(models.Length)];
                var isNew = rnd.NextDouble() < 0.55;
                var cost = isNew ? rnd.Next(150_000, 260_000) / 1000m : rnd.Next(30_000, 140_001) / 1000m;
                var unit = (await dev.ReceiveAsync(back, item, (cost, isNew ? "brand_new" : "used", isNew ? "in_stock" : "under_inspection", null, null)))[0];
                var p = new Phone { Id = unit.Id, Imei = unit.Imei, Item = item, Cost = cost, Status = isNew ? "in_stock" : "under_inspection", Condition = isNew ? "brand_new" : "used", Events = 1 };
                phones.Add(p);
                expected.Add(Inv(p.Condition), cost);
                Count("received");

                for (var step = 0; step < 14 && p.Status is not ("written_off"); step++)
                {
                    switch (p.Status)
                    {
                        case "under_inspection":
                        case "returned_inspection":
                            await InspectAsync(back, p, rnd, grades, expected, Count);
                            break;
                        case "refurbishing":
                            partAmounts.Add(await RefurbishAsync(back, p, rnd, parts, grades, rate, expected, Count));
                            break;
                        case "for_parts":
                            await WriteOffAsync(back, p, expected, Count);
                            break;
                        case "in_stock":
                            if (rnd.NextDouble() < 0.06) { await WriteOffAsync(back, p, expected, Count); break; }
                            await SellAsync(pos, p, rnd, expected, Count);
                            break;
                        case "sold":
                            var r = rnd.NextDouble();
                            if (r < 0.15) await VoidAsync(pos, p, expected, Count);
                            else if (r < 0.50) await ReturnAsync(pos, p, expected, Count);
                            else step = 99;   // stays sold
                            break;
                    }
                }
            }
        }));
        sw.Stop();
        var partsUsed = partAmounts.Sum();

        // Every phone: its cost, status, condition and the length of its history are what the steps made them.
        var mismatches = new List<string>();
        foreach (var p in phones)
        {
            var d = await DetailAsync(owner, p.Id);
            var u = d.GetProperty("unit");
            var got = (d.GetProperty("cost").GetProperty("total").GetDecimal(), u.GetProperty("status").GetString(), u.GetProperty("condition").GetString(), d.GetProperty("events").GetArrayLength());
            if (got != (p.Cost, p.Status, p.Condition, p.Events)) mismatches.Add($"{p.Imei}: expected {(p.Cost, p.Status, p.Condition, p.Events)}, got {got}");
            // Cost = acquisition + parts + labour + outside work, as the history page adds it up.
            var c = d.GetProperty("cost");
            Assert.Equal(c.GetProperty("total").GetDecimal(), c.GetProperty("acquisition").GetDecimal() + c.GetProperty("parts").GetDecimal()
                + c.GetProperty("labour").GetDecimal() + c.GetProperty("external").GetDecimal());
            // Each sale line carries the phone's cost at that moment and the profit on it.
            foreach (var (saleId, net, cost) in p.Sales)
            {
                var line = await fx.QueryDbAsync<string>($"SELECT net_amount || '|' || cost_value || '|' || profit FROM sales.sale_line WHERE sale_id = '{saleId}' AND device_unit_id = '{p.Id}'");
                if (line != $"{net:0.000}|{cost:0.000}|{net - cost:0.000}") mismatches.Add($"{p.Imei} sale {saleId}: expected {net:0.000}|{cost:0.000}|{net - cost:0.000}, got {line}");
            }
        }
        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));

        // The spare-parts ledger lost exactly the parts that went into phones.
        Assert.Equal(partValueBefore - partsUsed, await PartValueAsync(parts));
        expected.Add("1430", -partsUsed);

        // Every account moved by exactly what the steps say.
        var report = new Dictionary<string, object>();
        foreach (var a in accounts)
        {
            var moved = await dev.GlAsync(a) - before[a];
            report[a] = new { expected = expected[a], actual = moved };
            Assert.True(expected[a] == moved, $"account {a}: expected {expected[a]:0.000}, moved {moved:0.000}");
        }
        await AssertConsistentAsync(owner);

        var summary = new
        {
            workers, phones = phones.Count, seconds = Math.Round(sw.Elapsed.TotalSeconds, 1), steps = counts.OrderBy(k => k.Key).ToDictionary(k => k.Key, k => k.Value),
            refurbished = phones.Count(p => p.Condition == "refurbished"), soldNow = phones.Count(p => p.Status == "sold"), writtenOff = phones.Count(p => p.Status == "written_off"),
            partsUsed, accounts = report,
        };
        var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
        output.WriteLine(json);
        if (Environment.GetEnvironmentVariable("KITE_P5_REPORT") is { Length: > 0 } path) await File.WriteAllTextAsync(path, json);
    }

    private async Task<decimal> PartValueAsync(PosKit.Item[] parts) =>
        await fx.QueryDbAsync<decimal>($"SELECT coalesce(sum(value), 0) FROM inv.stock_balance WHERE variant_id IN ({string.Join(",", parts.Select(p => $"'{p.VariantId}'"))})");

    private static async Task InspectAsync(HttpClient c, Phone p, Random rnd, Guid[] grades, Ledger expected, Action<string> count)
    {
        var answers = AllPass(rnd.Next(81, 101));
        var scenario = rnd.NextDouble();
        if (scenario < 0.30) answers["battery"] = rnd.Next(60, 80).ToString(System.Globalization.CultureInfo.InvariantCulture);   // refurbish
        else if (scenario < 0.40) answers["body"] = "fail";                                                                      // damaged
        else if (scenario < 0.47) answers["water"] = "fail";                                                                     // for parts
        var suggested = scenario < 0.30 ? "refurbish" : scenario < 0.40 ? "restock" : scenario < 0.47 ? "for_parts" : "restock";
        var outcome = suggested;
        string? note = null;
        if (suggested == "restock" && rnd.NextDouble() < 0.15) { outcome = "refurbish"; note = "cosmetic refresh before sale"; count("override"); }
        var condition = outcome switch
        {
            "for_parts" => "for_parts",
            "refurbish" => scenario is >= 0.30 and < 0.40 ? "damaged" : "used",
            _ => scenario is >= 0.30 and < 0.40 ? "damaged" : p.Condition == "brand_new" && rnd.NextDouble() < 0.5 ? "used" : p.Condition,
        };
        var r = await Ok(await DeviceKit.InspectAsync(c, p.Id, answers, outcome, condition, condition is "for_parts" ? null : grades[rnd.Next(grades.Length)], note));
        Assert.Equal(suggested, r.GetProperty("suggestedOutcome").GetString());
        if (Inv(p.Condition) != Inv(condition)) { expected.Add(Inv(p.Condition), -p.Cost); expected.Add(Inv(condition), p.Cost); count("reclassified"); }
        p.Condition = condition;
        p.Status = outcome switch { "restock" => "in_stock", "refurbish" => "refurbishing", "for_parts" => "for_parts", _ => "supplier_return" };
        p.Events++;
        count("inspected_" + outcome);
    }

    /// <summary>One to four costs, sometimes one taken back, then completion. Returns the net value of parts used.</summary>
    private static async Task<decimal> RefurbishAsync(HttpClient c, Phone p, Random rnd, PosKit.Item[] parts, Guid[] grades, decimal rate, Ledger expected, Action<string> count)
    {
        var order = await OpenOrderAsync(c, p.Id);
        var used = 0m;
        var added = new List<(Guid Id, string Kind, decimal Amount)>();
        var n = rnd.Next(1, 5);
        for (var i = 0; i < n; i++)
        {
            var kind = rnd.Next(3) switch { 0 => "part", 1 => "labour", _ => "external" };
            JsonElement r;
            decimal amount;
            switch (kind)
            {
                case "part":
                    var qty = rnd.Next(1, 3);
                    r = await AddCostAsync(c, order, new { kind, variantId = parts[rnd.Next(parts.Length)].VariantId, qty = (decimal)qty });
                    amount = r.GetProperty("amount").GetDecimal();
                    Assert.True(amount > 0);
                    used += amount;
                    break;
                case "labour":
                    var minutes = rnd.Next(5, 181);
                    r = await AddCostAsync(c, order, new { kind, minutes });
                    amount = r.GetProperty("amount").GetDecimal();
                    Assert.Equal(Labour(minutes, rate), amount);   // the test's own arithmetic, not the server's
                    expected.Add("6210", -amount);
                    break;
                default:
                    amount = rnd.Next(1_000, 40_001) / 1000m;
                    r = await AddCostAsync(c, order, new { kind, vendor = "Outside lab", reference = "INV-" + rnd.Next(1000, 9999), amount });
                    Assert.Equal(amount, r.GetProperty("amount").GetDecimal());
                    expected.Add("2110", -amount);
                    break;
            }
            added.Add((r.GetProperty("id").GetGuid(), kind, amount));
            p.Cost += amount;
            p.Events++;
            expected.Add(Inv(p.Condition), amount);
            count("cost_" + kind);
        }
        if (rnd.NextDouble() < 0.3)
        {
            var (id, kind, amount) = added[rnd.Next(added.Count)];
            await Ok(await c.PostAsJsonAsync($"/api/v1/refurbishments/{order}/costs/{id}/reverse", new { reason = "recorded on the wrong phone" }));
            p.Cost -= amount;
            p.Events++;
            expected.Add(Inv(p.Condition), -amount);
            if (kind == "part") used -= amount;
            else if (kind == "labour") expected.Add("6210", amount);
            else expected.Add("2110", amount);
            count("cost_reversed");
        }
        var damaged = rnd.NextDouble() < 0.2;
        await CompleteAsync(c, order, damaged ? "damaged" : "refurbished", grades[rnd.Next(grades.Length)]);
        p.Condition = damaged ? "damaged" : "refurbished";
        p.Status = "in_stock";
        p.Events++;
        count("refurb_completed");
        return used;
    }

    private static async Task SellAsync(HttpClient pos, Phone p, Random rnd, Ledger expected, Action<string> count)
    {
        var unit = await Ok(await pos.GetAsync($"/api/v1/pos/units/code/{p.Imei}"));
        var price = unit.GetProperty("listPrice").GetDecimal();
        var cart = Guid.CreateVersion7();
        if (rnd.NextDouble() < 0.5) await Ok(await pos.PostAsJsonAsync($"/api/v1/pos/units/{p.Id}/reserve", new { cartId = cart }));
        var sale = await PosKit.SellAsync(pos, [UnitLine(p.Item, p.Id)], [Card(price)], id: cart);
        Assert.Equal(price, sale.GetProperty("gross").GetDecimal());
        var net = Net(price);
        p.Sales.Add((cart, net, p.Cost));
        expected.Add(Rev(p.Condition), -net);
        expected.Add(Inv(p.Condition), -p.Cost);
        expected.Add(Cogs(p.Condition), p.Cost);
        p.Status = "sold";
        p.Events++;
        count("sold");
    }

    private static async Task VoidAsync(HttpClient pos, Phone p, Ledger expected, Action<string> count)
    {
        var (saleId, net, cost) = p.Sales[^1];
        await Ok(await pos.PostAsJsonAsync($"/api/v1/sales/{saleId}/void", new { id = Guid.CreateVersion7(), reason = "wrong phone handed over" }));
        expected.Add("4900", net);
        expected.Add(Inv(p.Condition), cost);
        expected.Add(Cogs(p.Condition), -cost);
        p.Status = "in_stock";
        p.Events++;
        count("voided");
    }

    private static async Task ReturnAsync(HttpClient pos, Phone p, Ledger expected, Action<string> count)
    {
        var (saleId, net, cost) = p.Sales[^1];
        var sale = await Ok(await pos.GetAsync($"/api/v1/sales/{saleId}"));
        var line = sale.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("deviceUnitId").GetGuid() == p.Id);
        await Ok(await pos.PostAsJsonAsync($"/api/v1/sales/{saleId}/return", new
        {
            id = Guid.CreateVersion7(), reason = "customer returned it", refunds = new[] { Card(line.GetProperty("gross").GetDecimal()) },
            lines = new[] { new { lineId = line.GetProperty("id").GetGuid(), qty = 1m, code = p.Imei } },
        }));
        expected.Add("4900", net);
        expected.Add(Inv(p.Condition), cost);
        expected.Add(Cogs(p.Condition), -cost);
        p.Status = "returned_inspection";
        p.Events++;
        count("returned");
    }

    private static async Task WriteOffAsync(HttpClient c, Phone p, Ledger expected, Action<string> count)
    {
        var r = await Ok(await c.PostAsJsonAsync($"/api/v1/devices/{p.Id}/write-off", new { reason = p.Status == "for_parts" ? "harvested for parts" : "lost in storage", reasonCode = p.Status == "for_parts" ? "other" : "lost" }));
        Assert.Equal(p.Cost, r.GetProperty("cost").GetDecimal());
        expected.Add(Inv(p.Condition), -p.Cost);
        expected.Add("6700", p.Cost);
        p.Status = "written_off";
        p.Events++;
        count("written_off");
    }
}
