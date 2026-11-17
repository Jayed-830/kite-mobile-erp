using System.Globalization;
using System.Text.Json;
using Kite.Accounting;
using Kite.Platform;
using Kite.Sales;
using Xunit;

namespace Kite.UnitTests;

/// <summary>Phase 11 purchase arithmetic against the vectors of an independent Python implementation (tests/vectors/generators/purchase_vectors.py).</summary>
public sealed class PurchaseVectorTests
{
    private static readonly JsonElement V =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot.Path, "tests", "vectors", "purchase-math-vectors.json"))).RootElement;

    private static decimal D(JsonElement e) => decimal.Parse(e.GetString()!, CultureInfo.InvariantCulture);
    private static decimal? DN(JsonElement v, string name) => v.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? D(e) : null;
    private static decimal[] Ds(JsonElement e) => e.EnumerateArray().Select(D).ToArray();

    [Fact]
    public void Line_amounts_and_spreads_match_the_vectors()
    {
        var n = 0;
        foreach (var v in V.GetProperty("lines").EnumerateArray())
        {
            var net = PurchaseMath.LineNet(D(v.GetProperty("qty")), D(v.GetProperty("unitPrice")));
            Assert.Equal(D(v.GetProperty("net")), net);
            Assert.Equal(D(v.GetProperty("tax")), PurchaseMath.LineTax(net, D(v.GetProperty("rate"))));
            n++;
        }
        foreach (var v in V.GetProperty("spread").EnumerateArray())
        {
            var shares = PurchaseMath.Spread(D(v.GetProperty("amount")), Ds(v.GetProperty("weights")));
            Assert.Equal(Ds(v.GetProperty("shares")), shares);
            Assert.Equal(D(v.GetProperty("amount")), shares.Sum());   // nothing lost or made up by rounding
            n++;
        }
        foreach (var v in V.GetProperty("spreadCharges").EnumerateArray())
        {
            var lines = v.GetProperty("lines").EnumerateArray().Select(l => (D(l.GetProperty("value")), D(l.GetProperty("qty")))).ToArray();
            var charges = v.GetProperty("charges").EnumerateArray().Select(c => (D(c.GetProperty("amount")), c.GetProperty("basis").GetString()!)).ToArray();
            var shares = PurchaseMath.SpreadCharges(lines, charges);
            Assert.Equal(Ds(v.GetProperty("shares")), shares);
            Assert.Equal(charges.Sum(c => c.Item1), shares.Sum());
            n++;
        }
        foreach (var v in V.GetProperty("splitEven").EnumerateArray())
        {
            Assert.Equal(Ds(v.GetProperty("parts")), PurchaseMath.SplitEven(D(v.GetProperty("amount")), v.GetProperty("n").GetInt32()));
            n++;
        }
        Assert.True(n >= 250, $"only {n} vectors");
    }

    [Fact]
    public void The_three_way_match_clearing_and_variance_split_match_the_vectors()
    {
        var n = 0;
        foreach (var v in V.GetProperty("match").EnumerateArray())
        {
            var r = PurchaseMath.Match(D(v.GetProperty("invoiceQty")), D(v.GetProperty("invoicePrice")), D(v.GetProperty("openQty")), D(v.GetProperty("receiptPrice")),
                DN(v, "orderPrice"), D(v.GetProperty("tolerancePct")), D(v.GetProperty("toleranceAmount")));
            Assert.Equal(v.GetProperty("status").GetString(), r.Status);
            Assert.Equal(D(v.GetProperty("reference")), r.Reference);
            Assert.Equal(D(v.GetProperty("priceDifference")), r.PriceDifference);
            Assert.Equal(D(v.GetProperty("lineDifference")), r.LineDifference);
            n++;
        }
        foreach (var v in V.GetProperty("clearing").EnumerateArray())
        {
            Assert.Equal(D(v.GetProperty("cleared")), PurchaseMath.Clearing(D(v.GetProperty("invoiceQty")), D(v.GetProperty("openQty")), D(v.GetProperty("openValue"))));
            n++;
        }
        foreach (var v in V.GetProperty("splitVariance").EnumerateArray())
        {
            var (inv, ppv) = PurchaseMath.SplitVariance(D(v.GetProperty("variance")), D(v.GetProperty("invoiceQty")), D(v.GetProperty("onHandQty")));
            Assert.Equal((D(v.GetProperty("inventory")), D(v.GetProperty("ppv"))), (inv, ppv));
            Assert.Equal(D(v.GetProperty("variance")), inv + ppv);
            n++;
        }
        Assert.True(n >= 240, $"only {n} vectors");
    }

    [Fact]
    public void Worked_examples()
    {
        // Ordered at 100.000, invoiced at 101.500 with a 2% tolerance: inside (2.000 allowed).
        Assert.Equal("ok", PurchaseMath.Match(2, 101.500m, 2, 100m, 100m, 2m, 0m).Status);
        // At 103.000 the difference (3.000) is outside 2%, and 6.000 on the line is outside a 1.000 amount tolerance.
        Assert.Equal("price", PurchaseMath.Match(2, 103m, 2, 100m, 100m, 2m, 1m).Status);
        // More invoiced than received and not yet invoiced.
        Assert.Equal("qty", PurchaseMath.Match(3, 100m, 2, 100m, 100m, 2m, 1m).Status);
        // Clearing the last of a line takes all that is still open, so rounding never strands baisa in 2110.
        Assert.Equal(66.667m, PurchaseMath.Clearing(2, 3, 100m));
        Assert.Equal(33.333m, PurchaseMath.Clearing(1, 1, 33.333m));
        // Ten invoiced, four still on hand: four tenths of the difference to stock, the rest to purchase price variance.
        Assert.Equal((4.000m, 6.000m), PurchaseMath.SplitVariance(10m, 10, 4));
        Assert.Equal((10m, 0m), PurchaseMath.SplitVariance(10m, 10, 12));
        Assert.Equal([3.334m, 3.333m, 3.333m], PurchaseMath.SplitEven(10m, 3));
    }
}

/// <summary>The Phase 11 posting rules (16.2, 16.3, 17.3, 17.4): each journal balances and lands on the accounts the blueprint names.</summary>
public sealed class Phase11PostingTests
{
    private static JournalDraft? Build(string eventType, PostingFacts facts) =>
        PostingEngine.Build(PostingRuleTemplates.All.Single(r => r.EventType == eventType).Definition, facts, new DateOnly(2026, 11, 17), "test", Guid.NewGuid(), eventType, Currency.OMR);

    private static (string Code, decimal Dr, decimal Cr)[] Lines(string eventType, PostingFacts facts)
    {
        var d = Build(eventType, facts)!;
        Assert.Equal(d.TotalDebit, d.TotalCredit);
        return d.Lines.Select(l => (l.AccountCode, l.Debit, l.Credit)).OrderBy(x => x.AccountCode, StringComparer.Ordinal).ThenBy(x => x.Debit).ToArray();
    }

    [Fact]
    public void Goods_received_go_to_stock_by_class_against_goods_received_not_invoiced()
    {
        Assert.Equal([("1400", 600m, 0m), ("1410", 150m, 0m), ("1420", 40m, 0m), ("2110", 0m, 790m)], Lines(PostingRuleTemplates.PurchReceipt,
            new PostingFacts().Add("stock_by_class", "device", 600m).Add("stock_by_class", "device_used", 150m).Add("stock_by_class", "accessory", 40m)));
    }

    [Fact]
    public void A_supplier_invoice_clears_grni_splits_the_difference_and_owes_the_supplier()
    {
        // 600 received; invoiced 612 + 5% VAT: 12 over, 8 of it on stock still on hand, 4 to purchase price variance; a 20 expense line too.
        Assert.Equal([("1400", 8m, 0m), ("1500", 31.6m, 0m), ("2100", 0m, 663.6m), ("2110", 600m, 0m), ("5300", 4m, 0m), ("6100", 20m, 0m)],
            Lines(PostingRuleTemplates.PurchInvoice, new PostingFacts().Set("grni_cleared", 600m).Add("inventory_variance_by_class", "device", 8m).Set("ppv", 4m)
                .Add("expense_by_account", "6100", 20m).Set("vat", 31.6m).Set("payable", 663.6m)));
        // Invoiced below the receipt: the difference is a credit.
        Assert.Equal([("1400", 0m, 3m), ("2100", 0m, 597m), ("2110", 600m, 0m)],
            Lines(PostingRuleTemplates.PurchInvoice, new PostingFacts().Set("grni_cleared", 600m).Add("inventory_variance_by_class", "device", -3m).Set("payable", 597m)));
        // Booked at head office for goods branch B received: B clears its 2110 and owes head office; head office is owed and owes the supplier.
        Assert.Equal([("2110", 100m, 0m), ("2500", 0m, 100m)], Lines(PostingRuleTemplates.PurchInvoice, new PostingFacts().Set("grni_cleared", 100m).Set("due_to_branch", 100m)));
        Assert.Equal([("1310", 100m, 0m), ("1500", 5m, 0m), ("2100", 0m, 105m)],
            Lines(PostingRuleTemplates.PurchInvoice, new PostingFacts().Set("due_from_branch", 100m).Set("vat", 5m).Set("payable", 105m)));
    }

    [Fact]
    public void Returns_payments_expenses_card_settlements_and_vat_post_their_journals()
    {
        // A return out of goods received not invoiced at 100, stock left at 100.
        Assert.Equal([("1400", 0m, 100m), ("2110", 100m, 0m)], Lines(PostingRuleTemplates.PurchReturn,
            new PostingFacts().Set("grni_back", 100m).Add("stock_by_class", "device", 100m)));
        // A debit note at the invoice price 102 + VAT 5.1 on stock that cost 100: 2 to purchase price variance (a credit).
        Assert.Equal([("1400", 0m, 100m), ("1500", 0m, 5.1m), ("2100", 107.1m, 0m), ("5300", 0m, 2m)], Lines(PostingRuleTemplates.PurchReturn,
            new PostingFacts().Set("payable_back", 107.1m).Set("vat_back", 5.1m).Add("stock_by_class", "device", 100m).Set("ppv", 2m)));
        Assert.Equal([("1200", 0m, 250m), ("2100", 250m, 0m)], Lines(PostingRuleTemplates.PurchPayment,
            new PostingFacts().Set("paid", 250m).Add("paid_by_method", "bank_transfer", 250m)));
        Assert.Equal([("1100", 0m, 30m), ("2100", 30m, 0m)], Lines(PostingRuleTemplates.PurchPayment, new PostingFacts().Set("paid", 30m).Add("paid_by_method", "cash", 30m)));
        // Head office pays branch B's invoice: B's payable against what it owes head office.
        Assert.Equal([("2100", 80m, 0m), ("2500", 0m, 80m)], Lines(PostingRuleTemplates.PurchPayment, new PostingFacts().Set("paid", 80m).Set("due_to_branch", 80m)));
        Assert.Equal([("1200", 0m, 80m), ("1310", 80m, 0m)], Lines(PostingRuleTemplates.PurchPayment,
            new PostingFacts().Add("paid_by_method", "cheque", 80m).Set("due_from_branch", 80m)));
        Assert.Equal([("1100", 0m, 21m), ("1500", 1m, 0m), ("6900", 20m, 0m)], Lines(PostingRuleTemplates.Expense,
            new PostingFacts().Add("net_by_account", "6900", 20m).Set("vat", 1m).Add("paid_by_method", "cash", 21m)));
        Assert.Equal([("1150", 0m, 500m), ("1200", 488m, 0m), ("6800", 12m, 0m)], Lines(PostingRuleTemplates.CardSettlement,
            new PostingFacts().Set("gross", 500m).Set("fees", 12m).Set("net", 488m)));
        Assert.Equal([("1500", 0m, 30m), ("2200", 100m, 0m), ("2210", 0m, 70m)], Lines(PostingRuleTemplates.VatSettlement,
            new PostingFacts().Set("output", 100m).Set("input", 30m).Set("payable", 70m)));
        // More input than output: a refund due, VAT payable on the debit side.
        Assert.Equal([("1500", 0m, 50m), ("2200", 20m, 0m), ("2210", 30m, 0m)], Lines(PostingRuleTemplates.VatSettlement,
            new PostingFacts().Set("output", 20m).Set("input", 50m).Set("payable", -30m)));
        Assert.Equal([("1200", 0m, 70m), ("2210", 70m, 0m)], Lines(PostingRuleTemplates.VatPayment, new PostingFacts().Set("amount", 70m)));
    }

    [Fact]
    public void Cash_flow_classes_default_from_the_account()
    {
        Assert.Equal("cash", CashFlow.DefaultClass("1100", AccountType.Asset, true));
        Assert.Equal("cash", CashFlow.DefaultClass("1150", AccountType.Asset, true));
        Assert.Equal("investing", CashFlow.DefaultClass("1600", AccountType.Asset, true));
        Assert.Equal("operating", CashFlow.DefaultClass("2100", AccountType.Liability, true));
        Assert.Equal("financing", CashFlow.DefaultClass("3100", AccountType.Equity, true));
        Assert.Equal("none", CashFlow.DefaultClass("4100", AccountType.Revenue, true));
        Assert.Equal("none", CashFlow.DefaultClass("1000", AccountType.Asset, false));
    }
}
