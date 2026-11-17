using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;
using static Kite.IntegrationTests.PosKit;
using static Kite.IntegrationTests.PurchKit;

namespace Kite.IntegrationTests;

/// <summary>
/// Phase 11 exit gate (blueprint 25, row 11): "Three-way match; trial balance and subledger reconciliation clean". Test table (23):
/// "Accounting reconciliation | Daily reconciliation after all scenarios | Subledgers equal control accounts; trial balance balances;
/// interbranch eliminates".
///
/// 1. Races: workers receive against the same order line, post invoices for the same receipt line, pay the same supplier invoice and
///    return the same goods at once. Nothing is ever received beyond the order, invoiced beyond what came in, paid beyond what is
///    owed or returned beyond what was received; goods received not invoiced never goes below zero.
/// 2. A full day at three branches: orders and receipts with landed costs and IMEIs, invoices matched three ways (one outside
///    tolerance, posted with an override after a phone was sold, so the difference splits between stock and purchase price
///    variance), head office booking an invoice for goods two branches received, returns before and after invoicing, head office
///    paying a branch's invoice, expenses, card sales and their settlement, a manual journal, a sale on account at one branch paid
///    at another.
/// 3. The quarter's months closed at every branch, its VAT return filed (the VAT report agreeing with the VAT accounts branch by
///    branch), then the daily ledger check over everything in the database: every subledger equals its control account at every
///    branch, each branch's trial balance balances, interbranch receivable equals interbranch payable (so consolidation eliminates
///    them), the consolidated and per-branch balance sheets balance and the cash flow statement explains the cash.
/// Size: KITE_P11_WORKERS (8), KITE_P11_SALES (6 per branch). KITE_P11_REPORT writes a JSON report.
/// </summary>
[Collection("server")]
public sealed class Phase11ExitGateTests(KiteServerFixture fx, ITestOutputHelper output)
{
    private readonly PosKit kit = new(fx);
    private PurchKit P => new(fx, kit);

    private static int Env(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;

    private static readonly DateOnly Day = new(2025, 3, 15);   // the purchasing day: a quarter no other test uses

    private static async Task<(int Ok, Dictionary<string, int> Refused)> RaceAsync(int workers, Func<int, Task<HttpResponseMessage>> attempt)
    {
        var refused = new ConcurrentDictionary<string, int>();
        var ok = 0;
        await Task.WhenAll(Enumerable.Range(0, workers).Select(async i =>
        {
            using var r = await attempt(i);
            if (r.IsSuccessStatusCode) Interlocked.Increment(ref ok);
            else
            {
                var body = await r.Content.ReadFromJsonAsync<JsonElement>();
                refused.AddOrUpdate(body.GetProperty("code").GetString()!, 1, (_, n) => n + 1);
            }
        }));
        return (ok, refused.ToDictionary(x => x.Key, x => x.Value));
    }

    [PgFact]
    public async Task Races_never_overrun_and_a_full_day_reconciles_with_the_vat_return_filed()
    {
        var workers = Env("KITE_P11_WORKERS", 8);
        var salesPerBranch = Env("KITE_P11_SALES", 6);
        var watch = Stopwatch.StartNew();
        using var owner = await kit.OwnerAsync();
        await EnableAsync(owner);
        await CreditKit.EnableAsync(owner);
        await kit.SetPolicyAsync(owner, cashRounding: 0m);
        var credit = new CreditKit(fx, kit);
        var (b2, pos2) = await credit.SecondBranchAsync(owner);
        var (b3, pos3) = await credit.SecondBranchAsync(owner);
        using var _2 = pos2;
        using var _3 = pos3;
        var b1 = kit.Branch;
        using var pos1 = await kit.OwnerPosAsync(await kit.TerminalAsync(owner));
        await OpenShiftAsync(pos1, 50m);
        var supplier = await SupplierAsync(owner, "Gate Wholesale");
        var freightCo = await SupplierAsync(owner, "Gate Freight");
        var cases = await P.AccessoryAsync(owner, "Gate case");
        var cables = await P.AccessoryAsync(owner, "Gate cable");
        var phone = await P.PhoneAsync(owner, "Gate phone");
        var vatStd = await kit.TaxCodeAsync(owner);
        var races = new Dictionary<string, object>();

        // ------------------------------------------------------------ 1. races
        // (a) Receiving: 20 ordered, every worker tries to receive 3 at once.
        var (po, order) = await OrderAsync(owner, b1, supplier, OrderLine(cases, 20, 4m));
        await Ok(await owner.PostAsync($"/api/v1/purchasing/orders/{po}/approve", null));
        var poLine = PoLine(order, 0);
        var recv = await RaceAsync(workers, _ => ReceiveRawAsync(owner, b1, supplier, po, [new { poLineId = poLine, qty = 3m }]));
        var received = await fx.QueryDbAsync<decimal>($"SELECT coalesce(sum(qty), 0) FROM purch.receipt_line WHERE po_line_id = '{poLine}'");
        Assert.Equal(Math.Min(workers, 6) * 3m, received);
        Assert.Equal(Math.Min(workers, 6), recv.Ok);
        Assert.All(recv.Refused.Keys, k => Assert.Equal("PUR-002", k));
        races["receive"] = new { workers, recv.Ok, recv.Refused, received };

        // (b) Invoicing: one receipt of 10; every worker drafts an invoice for 3 of it, then all post at once.
        var gr = await ReceiveAsync(owner, b1, supplier, null, [new { variantId = cables.VariantId, qty = 10m, unitPrice = 2m }]);
        var line = ReceiptLine(gr, 0);
        var drafts = new List<Guid>();
        for (var i = 0; i < workers; i++)
        {
            var id = Guid.CreateVersion7();
            await Ok(await InvoiceRawAsync(owner, b1, supplier, "GATE-" + Suffix(), [Goods(line, 3, 2m)], id, Day));
            drafts.Add(id);
        }
        var inv = await RaceAsync(workers, i => owner.PostAsJsonAsync($"/api/v1/purchasing/invoices/{drafts[i]}/post", new { }));
        var invoiced = await fx.QueryDbAsync<decimal>($"""
            SELECT coalesce(sum(il.qty), 0) FROM purch.invoice_line il JOIN purch.purchase_invoice i ON i.id = il.invoice_id WHERE il.receipt_line_id = '{line}' AND i.status = 'posted'
            """);
        Assert.Equal(Math.Min(workers, 3) * 3m, invoiced);
        Assert.True(await fx.QueryDbAsync<decimal>($"SELECT sum(amount) FROM purch.grni_entry WHERE source_id = '{line}'") >= 0);
        Assert.All(inv.Refused.Keys, k => Assert.Equal("PUR-002", k));
        races["invoice"] = new { workers, inv.Ok, inv.Refused, invoiced };
        foreach (var d in drafts) await owner.PostAsync($"/api/v1/purchasing/invoices/{d}/cancel", null);   // the refused drafts are dropped

        // (c) Paying: an invoice of 105 open; every worker pays 40 of it by transfer at once.
        var gr2 = await ReceiveAsync(owner, b1, supplier, null, [new { variantId = cables.VariantId, qty = 50m, unitPrice = 2m }]);
        var invId = Guid.CreateVersion7();
        await Ok(await InvoiceRawAsync(owner, b1, supplier, "GATE-" + Suffix(), [Goods(ReceiptLine(gr2, 0), 50, 2m)], invId, Day));
        await PostInvoiceAsync(owner, invId);
        var entry = await fx.QueryDbAsync<Guid>($"SELECT ledger_entry_id FROM purch.purchase_invoice WHERE id = '{invId}'");
        var pay = await RaceAsync(workers, _ => owner.PostAsJsonAsync("/api/v1/purchasing/payments", new
        {
            id = Guid.CreateVersion7(), branchId = b1, supplierId = supplier, method = "bank_transfer", amount = 40m, reference = "GATE-" + Suffix(), paymentDate = Day,
            allocations = new[] { new { entryId = entry, amount = 40m } },
        }));
        var paid = await fx.QueryDbAsync<decimal>($"SELECT coalesce(sum(amount), 0) FROM purch.supplier_allocation WHERE debit_entry_id = '{entry}'");
        Assert.Equal(80m, paid);
        Assert.Equal(Math.Min(workers, 2), pay.Ok);
        Assert.All(pay.Refused.Keys, k => Assert.Equal("PUR-005", k));
        races["pay"] = new { workers, pay.Ok, pay.Refused, paid };

        // (d) Returning: 10 received, not invoiced; every worker sends 3 back at once.
        var gr3 = await ReceiveAsync(owner, b1, supplier, null, [new { variantId = cases.VariantId, qty = 10m, unitPrice = 4m }]);
        var rl = ReceiptLine(gr3, 0);
        var ret = await RaceAsync(workers, _ => owner.PostAsJsonAsync("/api/v1/purchasing/returns", new
        {
            id = Guid.CreateVersion7(), branchId = b1, supplierId = supplier, reason = "Race", returnDate = Day, lines = new[] { new { receiptLineId = rl, qty = 3m } },
        }));
        var returned = await fx.QueryDbAsync<decimal>($"SELECT coalesce(sum(qty), 0) FROM purch.supplier_return_line WHERE receipt_line_id = '{rl}'");
        Assert.Equal(Math.Min(workers, 3) * 3m, returned);
        Assert.All(ret.Refused.Keys, k => Assert.Equal("PUR-002", k));
        races["return"] = new { workers, ret.Ok, ret.Refused, returned };
        Assert.Empty(await ReconcileAsync(owner));

        // ------------------------------------------------------------ 2. a full day at three branches
        // Stock for the tills: each branch receives cases; branch 2 also two phones; freight on branch 3's delivery from another supplier.
        var r1 = await ReceiveAsync(owner, b1, supplier, null, [new { variantId = cases.VariantId, qty = 40m, unitPrice = 4m }]);
        var r2 = await ReceiveAsync(owner, b2, supplier, null,
            [new { variantId = cases.VariantId, qty = 30m, unitPrice = 4m }, new { variantId = phone.VariantId, qty = 2m, unitPrice = 150m, units = Units(2) }]);
        var r3 = await ReceiveAsync(owner, b3, supplier, null, [new { variantId = cases.VariantId, qty = 30m, unitPrice = 4m }],
            [new { kind = "freight", description = "Truck", amount = 6m, basis = "qty", supplierId = freightCo }]);
        var phoneUnits = r2.GetProperty("lines")[1].GetProperty("units").EnumerateArray().Select(u => u.GetProperty("id").GetGuid()).ToArray();

        // Sales at the tills: cash and card at each branch; one phone sold at branch 2 before its invoice arrives.
        var tills = new[] { pos1, pos2, pos3 };
        var sold = 0;
        foreach (var (pos, n) in tills.Select((p, i) => (p, i)))
            for (var s = 0; s < salesPerBranch; s++)
            {
                await SellAsync(pos, [Line(cases, 2)], s % 2 == 0 ? [Cash(18m)] : [Card(18m)]);
                sold++;
            }
        await SellAsync(pos2, [DeviceKit.UnitLine(phone, phoneUnits[0])], [Card(300m)]);

        // Head office books one invoice for branch 2's and branch 3's cases (4.050 each: within 2%), and branch 2's phones at 160 (outside):
        // the phone sold takes its share of the difference to purchase price variance, the one on hand to stock.
        var big = Guid.CreateVersion7();
        var draft = await Ok(await InvoiceRawAsync(owner, b1, supplier, "GATE-" + Suffix(),
            [Goods(ReceiptLine(r2, 0), 30, 4.05m), Goods(ReceiptLine(r3, 0), 30, 4.05m), Goods(ReceiptLine(r2, 1), 2, 160m)], big, Day));
        Assert.Equal("needs_approval", draft.GetProperty("invoice").GetProperty("status").GetString());
        var ppv = await P.GlAsync("5300");
        var posted = await PostInvoiceAsync(owner, big, "Price increase agreed");
        var phoneLine = posted.GetProperty("lines")[2];
        Assert.Equal((10m, 10m), (phoneLine.GetProperty("varianceInventory").GetDecimal(), phoneLine.GetProperty("variancePpv").GetDecimal()));
        Assert.True(await P.GlAsync("5300") - ppv >= 10m);
        // The freight company invoices its landed cost to branch 3; branch 1's own cases are invoiced at its branch.
        var freightCharge = r3.GetProperty("charges")[0].GetProperty("id").GetGuid();
        var fi = Guid.CreateVersion7();
        await Ok(await InvoiceRawAsync(owner, b3, freightCo, "FR-" + Suffix(), [Charge(freightCharge, 6m)], fi, Day));
        await PostInvoiceAsync(owner, fi);
        var own = Guid.CreateVersion7();
        await Ok(await InvoiceRawAsync(owner, b1, supplier, "GATE-" + Suffix(), [Goods(ReceiptLine(r1, 0), 40, 4m)], own, Day));
        await PostInvoiceAsync(owner, own);
        // Branch 2 invoices a later delivery itself; head office pays it.
        var r4 = await ReceiveAsync(owner, b2, supplier, null, [new { variantId = cables.VariantId, qty = 20m, unitPrice = 2m }]);
        var b2inv = Guid.CreateVersion7();
        await Ok(await InvoiceRawAsync(owner, b2, supplier, "GATE-" + Suffix(), [Goods(ReceiptLine(r4, 0), 20, 2m)], b2inv, Day));
        await PostInvoiceAsync(owner, b2inv);
        var b2Entry = await fx.QueryDbAsync<Guid>($"SELECT ledger_entry_id FROM purch.purchase_invoice WHERE id = '{b2inv}'");
        await Ok(await owner.PostAsJsonAsync("/api/v1/purchasing/payments", new
        {
            id = Guid.CreateVersion7(), branchId = b1, supplierId = supplier, method = "cheque", amount = 42m, reference = "CHQ-" + Suffix(), paymentDate = Day,
            allocations = new[] { new { entryId = b2Entry, amount = 42m } },
        }));
        // Returns at branch 3: its cases are all invoiced, so 2 go back on a debit note with their VAT.
        await Ok(await owner.PostAsJsonAsync("/api/v1/purchasing/returns", new
        {
            id = Guid.CreateVersion7(), branchId = b3, supplierId = supplier, reason = "Cracked", returnDate = Day, lines = new[] { new { receiptLineId = ReceiptLine(r3, 0), qty = 2m } },
        }));
        // And at branch 2, cables not yet invoiced go back out of goods received not invoiced.
        var r5 = await ReceiveAsync(owner, b2, supplier, null, [new { variantId = cables.VariantId, qty = 5m, unitPrice = 2m }]);
        await Ok(await owner.PostAsJsonAsync("/api/v1/purchasing/returns", new
        {
            id = Guid.CreateVersion7(), branchId = b2, supplierId = supplier, reason = "Wrong colour", returnDate = Day, lines = new[] { new { receiptLineId = ReceiptLine(r5, 0), qty = 2m } },
        }));
        // Expenses at every branch, a manual journal, the bank's card settlement at head office.
        var rent = await AccountIdAsync(owner, "6100");
        foreach (var b in new[] { b1, b2, b3 })
            await Ok(await ExpenseRawAsync(owner, b, rent, 150m, Day, paidBy: "bank_transfer", taxCode: vatStd));
        var mj = Guid.CreateVersion7();
        await Ok(await owner.PostAsJsonAsync("/api/v1/accounting/manual-journals", new
        {
            id = mj, branchId = b1, journalDate = Day, description = "Owner's capital into the bank",
            lines = new object[] { new { accountId = await AccountIdAsync(owner, "1200"), branchId = b1, debit = 800m, credit = 0m },
                                   new { accountId = await AccountIdAsync(owner, "3100"), branchId = b1, debit = 0m, credit = 800m } },
        }));
        await Ok(await owner.PostAsync($"/api/v1/accounting/manual-journals/{mj}/submit", null));
        var clearing = (await Ok(await owner.GetAsync("/api/v1/accounting/card-settlements"))).GetProperty("clearing").EnumerateArray()
            .Single(c => c.GetProperty("branchId").GetGuid() == b1).GetProperty("balance").GetDecimal();
        Assert.True(clearing >= 18m, $"card clearing {clearing}");
        await Problem(await owner.PostAsJsonAsync("/api/v1/accounting/card-settlements", new { id = Guid.CreateVersion7(), branchId = b1, grossAmount = clearing + 1m, fees = 0m, reference = "X" }),
            HttpStatusCode.Conflict, "ACC-010");
        await Ok(await owner.PostAsJsonAsync("/api/v1/accounting/card-settlements", new { id = Guid.CreateVersion7(), branchId = b1, grossAmount = 18m, fees = 0.45m, reference = "STL-" + Suffix() }));
        // On account at branch 2, paid at branch 3 (14.6).
        var service = await kit.ProductAsync(owner, "Gate service " + Suffix(), 1m, type: "service");
        var customer = await TradeInKit.CustomerAsync(pos2, "Gate customer " + Suffix());
        await CreditKit.AccountAsync(owner, customer, 500m, 30, (b2, 300m), (b3, 200m));
        await Ok(await CreditKit.SellOnAccountAsync(pos2, service, 60m, customer));
        await Ok(await pos3.PostAsJsonAsync("/api/v1/credit/payments", new { id = Guid.CreateVersion7(), customerId = customer, method = "cash", amount = 25m }));
        await DeviceKit.AssertConsistentAsync(owner);

        // ------------------------------------------------------------ 3. close the quarter, file its VAT, check everything
        await Ok(await owner.PostAsync("/api/v1/accounting/vat/boxes/review", null), HttpStatusCode.NoContent);
        var vr = await Ok(await owner.PostAsJsonAsync("/api/v1/accounting/vat/returns", new { periodFrom = new DateOnly(2025, 1, 1), periodTo = new DateOnly(2025, 3, 31) }));
        Assert.True(vr.GetProperty("agrees").GetBoolean(), vr.GetProperty("check").GetRawText());
        var vrId = vr.GetProperty("vatReturn").GetProperty("id").GetGuid();
        foreach (var m in new[] { 1, 2, 3 }) await PeriodStatusAsync(owner, 2025, m, "soft_closed");
        var filed = await Ok(await owner.PostAsJsonAsync($"/api/v1/accounting/vat/returns/{vrId}/file", new { filingReference = "OTA-GATE" }));
        Assert.Equal("filed", filed.GetProperty("vatReturn").GetProperty("status").GetString());
        var inputVat = filed.GetProperty("vatReturn").GetProperty("boxes").GetProperty("6e").GetProperty("vat").GetDecimal();
        Assert.True(inputVat > 0);
        // Nothing more lands in the quarter: a late receipt dated in it posts to April.
        var late = await ReceiveAsync(owner, b2, supplier, null, [new { variantId = cables.VariantId, qty = 1m, unitPrice = 2m }], date: Day);
        Assert.Equal("2025-04-01", await fx.QueryDbAsync<string>(
            $"SELECT j.journal_date::text FROM acct.journal j JOIN purch.goods_receipt r ON r.journal_id = j.id WHERE r.id = '{late.GetProperty("receipt").GetProperty("id").GetGuid()}'"));

        var findings = await ReconcileAsync(owner);
        Assert.Empty(findings);
        var check = await Ok(await owner.GetAsync("/api/v1/accounting/reconciliation"));
        Assert.True(check.GetProperty("clean").GetBoolean());
        var ledgers = check.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("ledger").GetString()).Distinct().ToList();
        foreach (var l in new[] { "suppliers", "inventory", "grni", "vat_output", "vat_input", "customers", "interbranch", "trial_balance" }) Assert.Contains(l, ledgers);
        var today = Today.ToString("yyyy-MM-dd");
        var tb = await Ok(await owner.GetAsync($"/api/v1/accounting/reports/trial-balance?from=2025-01-01&to={today}"));
        Assert.True(tb.GetProperty("balanced").GetBoolean(), tb.GetProperty("totals").GetRawText());
        var bs = await Ok(await owner.GetAsync($"/api/v1/accounting/reports/balance-sheet?asOf={today}"));
        Assert.True(bs.GetProperty("balanced").GetBoolean());
        Assert.Equal(0m, bs.GetProperty("elimination").GetProperty("difference").GetDecimal());
        Assert.True(bs.GetProperty("elimination").GetProperty("eliminated").GetDecimal() > 0);
        foreach (var b in new[] { b1, b2, b3 })
        {
            Assert.True((await Ok(await owner.GetAsync($"/api/v1/accounting/reports/trial-balance?from=2025-01-01&to={today}&branchId={b}"))).GetProperty("balanced").GetBoolean());
            Assert.True((await Ok(await owner.GetAsync($"/api/v1/accounting/reports/balance-sheet?asOf={today}&branchId={b}"))).GetProperty("balanced").GetBoolean());
        }
        var cf = await Ok(await owner.GetAsync($"/api/v1/accounting/reports/cash-flow?from=2025-01-01&to={today}"));
        Assert.Equal(0m, cf.GetProperty("difference").GetDecimal());
        output.WriteLine($"races {JsonSerializer.Serialize(races)}; {sold} sales; vat input {inputVat}; {watch.Elapsed.TotalSeconds:0}s");

        if (Environment.GetEnvironmentVariable("KITE_P11_REPORT") is { Length: > 0 } report)
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
            {
                workers, salesPerBranch, seconds = (int)watch.Elapsed.TotalSeconds, races, sales = sold,
                vatReturn = new { output = filed.GetProperty("vatReturn").GetProperty("outputVat").GetDecimal(), input = inputVat },
                reconciliation = check.GetProperty("rows"), trialBalance = tb.GetProperty("totals"),
                eliminated = bs.GetProperty("elimination").GetProperty("eliminated").GetDecimal(), cashFlowDifference = cf.GetProperty("difference").GetDecimal(),
            }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
