using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kite.Sales;
using Xunit;
using Xunit.Abstractions;
using static Kite.IntegrationTests.PosKit;

namespace Kite.IntegrationTests;

/// <summary>
/// Phase 4 exit gate (blueprint row 4): sales, returns, shifts, the stock ledger and the journals stay balanced under a
/// 20-terminal load. Every terminal runs a full shift at the same time (quotes, sales with mixed tenders and discounts,
/// returns, manager-approved voids, cash events, a blind close) while tracking on its own side what the drawer, the
/// bank terminals and the stock should be. Afterwards the server's figures must match those exactly, with zero
/// variance, and the 23.2 consistency checks must pass. Size: KITE_LOAD_TERMINALS (20) x KITE_LOAD_SALES (12).
/// </summary>
[Collection("server")]
public sealed class Phase4LoadTests(KiteServerFixture fx, ITestOutputHelper output)
{
    private readonly PosKit kit = new(fx);

    private sealed class Ledger
    {
        public decimal Cash;
        public decimal PayOuts;
        public decimal Net;
        public decimal Tax;
        public decimal CreditNet;
        public decimal CreditTax;
        public int Sales;
        public int Returns;
        public int Voids;
        public int StockRefusals;
        public readonly Dictionary<string, decimal> Cards = new(StringComparer.Ordinal);
        public readonly Dictionary<Guid, decimal> Sold = [];
        public readonly Dictionary<Guid, decimal> Restocked = [];
        public decimal Expected(decimal opening, decimal payIn, decimal drops) => opening + Cash + payIn - PayOuts - drops;
    }

    private sealed record SoldLine(Guid LineId, Guid VariantId, decimal Qty, decimal Net, decimal Tax, decimal Gross, decimal Discount, bool Stocked);
    private sealed record SoldSale(Guid Id, decimal Cash, string? CardTerminal, decimal Card, List<SoldLine> Lines, decimal Net, decimal Tax) { public bool Touched; }

    private static int Env(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;

    [PgFact]
    public async Task Twenty_terminals_sell_return_void_and_close_with_every_ledger_balanced()
    {
        var terminals = Env("KITE_LOAD_TERMINALS", 20);
        var salesEach = Env("KITE_LOAD_SALES", 12);
        using var owner = await kit.OwnerAsync();
        await kit.SetPolicyAsync(owner, cashRounding: 0.005m, cashRefundCap: 10000m, variance: 0m);

        var prices = new[] { 2.345m, 7.250m, 0.999m, 15.000m, 1.555m, 4.100m, 9.990m, 3.333m };
        var items = new List<Item>();
        for (var i = 0; i < prices.Length; i++)
            items.Add(await kit.ProductAsync(owner, $"Load item {i}", prices[i], type: i % 3 == 0 ? "spare_part" : i % 3 == 1 ? "accessory" : "consumable",
                openingQty: 100000, unitCost: decimal.Round(prices[i] * 0.55m, 3)));
        var scarce = await kit.ProductAsync(owner, "Scarce item", 5.500m, openingQty: terminals * 2, unitCost: 3.000m);
        var service = await kit.ProductAsync(owner, "Load service", 2.000m, type: "service");
        var catalog = items.Append(scarce).Append(service).ToArray();
        var manager = await kit.UserAsync(owner, "loadmgr", "branch_manager", pin: "9753");

        var stockBefore = new Dictionary<Guid, decimal>();
        foreach (var it in catalog.Where(c => c.Type != "service")) stockBefore[it.VariantId] = await kit.StockAsync(owner, it);
        async Task<decimal> GlAsync(string codes, bool debitPositive = true) => await fx.QueryDbAsync<decimal>($"""
            SELECT coalesce(sum({(debitPositive ? "l.debit - l.credit" : "l.credit - l.debit")}), 0) FROM acct.journal_line l JOIN acct.account a ON a.id = l.account_id
             WHERE a.code IN ({codes})
            """);
        var cashBefore = await GlAsync("'1100'");
        var revenueBefore = await GlAsync("'4100','4120','4130','4200'", false);
        var returnsBefore = await GlAsync("'4900'");
        var vatBefore = await GlAsync("'2200'", false);
        var inventoryBefore = await GlAsync("'1400','1420','1430'");
        var cogsBefore = await GlAsync("'5100','5120','5200'");

        // Terminals and cashiers first (registration is not part of the load).
        var setups = new List<(Terminal Term, (Guid Id, string Username, string Password) User)>();
        for (var i = 0; i < terminals; i++)
            setups.Add((await kit.TerminalAsync(owner), await kit.UserAsync(owner, $"load{i:00}", "senior_cashier")));

        var latencies = new ConcurrentBag<double>();
        var ledgers = new Ledger[terminals];
        var shiftIds = new Guid[terminals];
        var clock = Stopwatch.StartNew();

        await Task.WhenAll(setups.Select((s, n) => Task.Run(async () =>
        {
            var rnd = new Random(1000 + n);
            var led = ledgers[n] = new Ledger();
            using var pos = await kit.PosAsync(s.User.Username, s.User.Password, s.Term);
            var shift = await OpenShiftAsync(pos, 50.000m);
            shiftIds[n] = shift.GetProperty("id").GetGuid();
            var mine = new List<SoldSale>();
            decimal payIn = 0, drops = 0;

            for (var k = 0; k < salesEach; k++)
            {
                // A sale: 1-4 lines, sometimes a discount within the senior cashier's limit (10% / 10.000).
                var lines = Enumerable.Range(0, rnd.Next(1, 5)).Select(_ => catalog[rnd.Next(catalog.Length)]).DistinctBy(i => i.VariantId)
                    .Select(i => (Item: i, Qty: (decimal)rnd.Next(1, 4), Discount: rnd.Next(4) == 0 ? 0.100m : 0m)).ToArray();
                var body = lines.Select(l => Line(l.Item, l.Qty, l.Discount == 0 ? null : l.Discount)).ToArray();
                var quote = await Ok(await pos.PostAsJsonAsync("/api/v1/sales/quote", new { lines = body }));
                var gross = quote.GetProperty("totals").GetProperty("gross").GetDecimal();
                var mode = rnd.Next(3);
                var card = mode == 0 ? 0m : mode == 1 ? gross : decimal.Round(gross / 2, 3);
                var cash = PosMath.RoundCash(gross - card, 0.005m);
                var bank = $"BANK-{rnd.Next(1, 3)}";
                var pays = new List<object>();
                if (card > 0) pays.Add(Card(card, bank));
                if (cash > 0 || card == 0) pays.Add(Cash(cash, cash == 0 ? null : decimal.Ceiling(cash)));
                var id = Guid.CreateVersion7();
                var sw = Stopwatch.StartNew();
                var r = await pos.PostAsJsonAsync($"/api/v1/sales/{id}/complete", new { lines = body, payments = pays, expectedTotal = gross });
                latencies.Add(sw.Elapsed.TotalMilliseconds);
                if (r.StatusCode == HttpStatusCode.Conflict && (await r.Content.ReadAsStringAsync()).Contains("INV-001", StringComparison.Ordinal))
                {
                    led.StockRefusals++;
                    continue;
                }
                await Ok(r);
                led.Sales++;
                led.Cash += cash;
                if (card > 0) led.Cards[bank] = led.Cards.GetValueOrDefault(bank) + card;
                var sale = await pos.GetFromJsonAsync<JsonElement>($"/api/v1/sales/{id}");
                var soldLines = sale.GetProperty("lines").EnumerateArray().Select(l => new SoldLine(l.GetProperty("id").GetGuid(), l.GetProperty("variantId").GetGuid(),
                    l.GetProperty("qty").GetDecimal(), l.GetProperty("net").GetDecimal(), l.GetProperty("tax").GetDecimal(), l.GetProperty("gross").GetDecimal(),
                    l.GetProperty("discount").GetDecimal(), l.GetProperty("productType").GetString() != "service")).ToList();
                foreach (var l in soldLines.Where(l => l.Stocked)) led.Sold[l.VariantId] = led.Sold.GetValueOrDefault(l.VariantId) + l.Qty;
                led.Net += soldLines.Sum(l => l.Net);
                led.Tax += soldLines.Sum(l => l.Tax);
                mine.Add(new SoldSale(id, cash, card > 0 ? bank : null, card, soldLines, soldLines.Sum(l => l.Net), soldLines.Sum(l => l.Tax)));

                if (k % 5 == 4)
                {
                    // Return one unit of a line of an earlier sale, refunded in cash; stock back or written off.
                    var target = mine.Where(m => !m.Touched).SkipLast(1).OrderBy(_ => rnd.Next()).FirstOrDefault();
                    if (target is not null)
                    {
                        target.Touched = true;
                        var line = target.Lines[rnd.Next(target.Lines.Count)];
                        var share = PosMath.ApportionReturn(new ReturnableLine(line.Qty, line.Net, line.Tax, line.Gross, line.Discount, 0, 0, 0, 0, 0), 1m);
                        var refund = PosMath.RoundCash(share.Gross, 0.005m);
                        var restock = rnd.Next(4) != 0;
                        await Ok(await pos.PostAsJsonAsync($"/api/v1/sales/{target.Id}/return", new
                        {
                            id = Guid.CreateVersion7(), reason = "load test return", lines = new[] { new { lineId = line.LineId, qty = 1m, restock } }, refunds = new[] { Cash(refund) },
                        }));
                        led.Returns++;
                        led.Cash -= refund;
                        led.CreditNet += share.Net;
                        led.CreditTax += share.Tax;
                        if (restock && line.Stocked) led.Restocked[line.VariantId] = led.Restocked.GetValueOrDefault(line.VariantId) + 1;
                    }
                }
                if (k % 7 == 6)
                {
                    // Void the latest sale with the manager's PIN: the money goes back the way it came.
                    var last = mine[^1];
                    if (!last.Touched)
                    {
                        last.Touched = true;
                        var a = await Ok(await pos.PostAsJsonAsync("/api/v1/approvals/pin", new { action = "POS.SALE.VOID", approverUsername = manager.Username, pin = "9753" }));
                        await Ok(await pos.PostAsJsonAsync($"/api/v1/sales/{last.Id}/void", new { id = Guid.CreateVersion7(), reason = "load test void", approvalId = a.GetProperty("id").GetGuid() }));
                        led.Voids++;
                        led.Cash -= last.Cash;
                        if (last.CardTerminal is { } ct) led.Cards[ct] -= last.Card;
                        led.CreditNet += last.Net;
                        led.CreditTax += last.Tax;
                        foreach (var l in last.Lines.Where(l => l.Stocked)) led.Restocked[l.VariantId] = led.Restocked.GetValueOrDefault(l.VariantId) + l.Qty;
                    }
                }
                if (k == salesEach / 2)
                {
                    await Ok(await pos.PostAsJsonAsync("/api/v1/pos/shifts/current/cash-events", new { kind = "pay_in", amount = 10.000m, reason = "coins" }));
                    payIn += 10m;
                    await Ok(await pos.PostAsJsonAsync("/api/v1/pos/shifts/current/cash-events", new { kind = "pay_out", amount = 1.250m, reason = "water" }));
                    led.PayOuts += 1.250m;
                    var drop = decimal.Round(led.Expected(50m, payIn, drops) / 3, 3);
                    if (drop > 0)
                    {
                        await Ok(await pos.PostAsJsonAsync("/api/v1/pos/shifts/current/cash-events", new { kind = "drop", amount = drop, reason = "safe" }));
                        drops += drop;
                    }
                }
            }

            // Blind close with exactly what the terminal says is in the drawer and on each bank batch.
            var counted = led.Expected(50m, payIn, drops);
            var settlements = led.Cards.Where(c => c.Value != 0).Select(c => new { cardTerminal = c.Key, batchTotal = c.Value, reason = (string?)null }).ToArray();
            var closed = await Ok(await pos.PostAsJsonAsync("/api/v1/pos/shifts/current/close", new { countedCash = counted, cardSettlements = settlements }));
            Assert.True(closed.GetProperty("variance").GetDecimal() == 0m, $"terminal {n}: variance {closed.GetProperty("variance").GetDecimal()} (counted {counted})");
        })));
        clock.Stop();

        // ---- Reconciliation
        var check = await owner.GetFromJsonAsync<JsonElement>("/api/v1/inventory/check");
        Assert.True(check.GetProperty("ok").GetBoolean(), "consistency check failed: " + check.GetRawText());

        for (var n = 0; n < terminals; n++)
        {
            var z = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/pos/shifts/{shiftIds[n]}");
            var f = z.GetProperty("figures");
            Assert.Equal("Z", z.GetProperty("report").GetString());
            Assert.Equal(0m, z.GetProperty("shift").GetProperty("variance").GetDecimal());
            Assert.Equal(ledgers[n].Cash, f.GetProperty("cashPayments").GetDecimal());
            Assert.Equal(ledgers[n].Cards.Values.Sum(), f.GetProperty("cardPayments").GetDecimal());
            Assert.Equal(ledgers[n].Sales, f.GetProperty("salesCount").GetInt32());
            Assert.Equal(ledgers[n].Returns + ledgers[n].Voids, f.GetProperty("creditNotesCount").GetInt32());
            Assert.All(z.GetProperty("cardSettlements").EnumerateArray(), c => Assert.Equal(0m, c.GetProperty("difference").GetDecimal()));
        }

        foreach (var it in catalog.Where(c => c.Type != "service"))
        {
            var sold = ledgers.Sum(l => l.Sold.GetValueOrDefault(it.VariantId));
            var back = ledgers.Sum(l => l.Restocked.GetValueOrDefault(it.VariantId));
            Assert.Equal(stockBefore[it.VariantId] - sold + back, await kit.StockAsync(owner, it));
        }
        Assert.True(await kit.StockAsync(owner, scarce) >= 0, "scarce stock went negative under a block policy");

        var cashMoved = ledgers.Sum(l => l.Cash) - ledgers.Sum(l => l.PayOuts);
        Assert.Equal(cashMoved, await GlAsync("'1100'") - cashBefore);
        Assert.Equal(ledgers.Sum(l => l.Net), await GlAsync("'4100','4120','4130','4200'", false) - revenueBefore);
        Assert.Equal(ledgers.Sum(l => l.CreditNet), await GlAsync("'4900'") - returnsBefore);
        Assert.Equal(ledgers.Sum(l => l.Tax) - ledgers.Sum(l => l.CreditTax), await GlAsync("'2200'", false) - vatBefore);
        var stockValueMoved = await fx.QueryDbAsync<decimal>($"""
            SELECT coalesce(sum(m.value), 0) FROM inv.stock_movement m JOIN sales.sale s ON m.source_doc_type = 'sale' AND s.id = m.source_doc_id
             WHERE s.shift_id IN ({string.Join(",", shiftIds.Select(i => $"'{i}'"))})
            """);
        Assert.Equal(stockValueMoved, await GlAsync("'1400','1420','1430'") - inventoryBefore);
        Assert.Equal(-stockValueMoved, await GlAsync("'5100','5120','5200'") - cogsBefore);

        // Gap-free numbering per branch and year: the highest invoice number equals the number of invoices.
        Assert.True(await fx.QueryDbAsync<bool>("""
            SELECT bool_and(n = c) FROM (SELECT max(right(number, 6)::int) AS n, count(*) AS c FROM sales.sale
                                          WHERE doc_type <> 'credit_note' AND NOT is_emergency GROUP BY branch_id, split_part(number, '-', 3)) x
            """), "invoice numbers have gaps");

        var sorted = latencies.OrderBy(x => x).ToArray();
        var p95 = sorted[(int)Math.Floor(sorted.Length * 0.95)];
        var totalSales = ledgers.Sum(l => l.Sales);
        var summary = string.Create(CultureInfo.InvariantCulture,
            $"{terminals} terminals, {totalSales} sales, {ledgers.Sum(l => l.Returns)} returns, {ledgers.Sum(l => l.Voids)} voids, {ledgers.Sum(l => l.StockRefusals)} stock refusals, " +
            $"{clock.Elapsed.TotalSeconds:0.0} s, {totalSales / clock.Elapsed.TotalSeconds:0.0} sales/s, completion p50 {sorted[sorted.Length / 2]:0} ms, p95 {p95:0} ms, max {sorted[^1]:0} ms");
        output.WriteLine(summary);
        if (Environment.GetEnvironmentVariable("KITE_LOAD_REPORT") is { Length: > 0 } path)
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
            {
                terminals, salesPerTerminal = salesEach, sales = totalSales, returns = ledgers.Sum(l => l.Returns), voids = ledgers.Sum(l => l.Voids),
                stockRefusals = ledgers.Sum(l => l.StockRefusals), seconds = clock.Elapsed.TotalSeconds, p50Ms = sorted[sorted.Length / 2], p95Ms = p95, maxMs = sorted[^1],
                cash = cashMoved, revenue = ledgers.Sum(l => l.Net), vat = ledgers.Sum(l => l.Tax) - ledgers.Sum(l => l.CreditTax), summary,
            }));
    }
}
