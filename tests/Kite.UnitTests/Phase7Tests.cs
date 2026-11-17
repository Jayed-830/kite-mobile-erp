using System.Globalization;
using System.Text.Json;
using FsCheck;
using FsCheck.Xunit;
using Kite.Accounting;
using Kite.Platform;
using Kite.Sales;
using Xunit;

namespace Kite.UnitTests;

/// <summary>Phase 7 trade-in rules against the vectors of an independent Python implementation, margin pricing, tenders and posting rules.</summary>
public sealed class TradeInVectorTests
{
    private static readonly JsonElement V =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot.Path, "tests", "vectors", "tradein-math-vectors.json"))).RootElement;

    private static decimal? Dec(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : decimal.Parse(e.GetString()!, CultureInfo.InvariantCulture);
    private static decimal D(JsonElement e) => Dec(e)!.Value;

    [Fact]
    public void Valuations_match_the_vectors()
    {
        foreach (var v in V.GetProperty("valuation").EnumerateArray())
        {
            var guide = Dec(v.GetProperty("guide"));
            var ds = v.GetProperty("deductions").EnumerateArray()
                .Select(d => new TradeInDeduction(d.GetProperty("code").GetString()!, d.GetProperty("kind").GetString()!, D(d.GetProperty("value")))).ToList();
            var expect = v.GetProperty("expect");
            if (expect.ValueKind == JsonValueKind.Null)
            {
                var e = Assert.Throws<KiteException>(() => TradeInMath.Value(guide, ds));
                Assert.Equal(ErrorCodes.TradeInNoValue, e.Code);
                continue;
            }
            var r = TradeInMath.Value(guide, ds);
            Assert.Equal(expect.GetProperty("amounts").EnumerateArray().Select(D).ToArray(), r.Amounts.ToArray());
            Assert.Equal(D(expect.GetProperty("total")), r.DeductionsTotal);
            Assert.Equal(Dec(expect.GetProperty("offered")), r.Offered);
        }
    }

    [Fact]
    public void Approval_needs_match_the_vectors()
    {
        foreach (var v in V.GetProperty("approval").EnumerateArray())
            Assert.True(v.GetProperty("expect").GetBoolean() == TradeInMath.NeedsApproval(D(v.GetProperty("agreed")), Dec(v.GetProperty("offered")),
                D(v.GetProperty("staffLimit")), D(v.GetProperty("overGuidePercent"))), v.ToString());
    }

    [Fact]
    public void Margin_vat_matches_the_vectors()
    {
        foreach (var v in V.GetProperty("margin").EnumerateArray())
        {
            var r = TradeInMath.MarginLine(D(v.GetProperty("gross")), D(v.GetProperty("purchase")), D(v.GetProperty("rate")));
            var e = v.GetProperty("expect");
            Assert.Equal((D(e.GetProperty("net")), D(e.GetProperty("tax")), D(e.GetProperty("gross")), D(e.GetProperty("margin"))), (r.Net, r.Tax, r.Gross, r.Margin));
        }
    }

    [Fact]
    public void Applying_a_trade_in_to_a_sale_matches_the_vectors()
    {
        foreach (var v in V.GetProperty("apply").EnumerateArray())
        {
            var (applied, left) = TradeInMath.Apply(D(v.GetProperty("outstanding")), D(v.GetProperty("due")));
            Assert.Equal((D(v.GetProperty("expect").GetProperty("applied")), D(v.GetProperty("expect").GetProperty("left"))), (applied, left));
        }
    }

    [Property(MaxTest = 300)]
    public bool Margin_tax_is_never_negative_and_lines_add_up(PositiveInt grossBaisa, PositiveInt purchaseBaisa)
    {
        var gross = grossBaisa.Get % 1_000_000 / 1000m;
        var purchase = purchaseBaisa.Get % 1_000_000 / 1000m;
        var r = TradeInMath.MarginLine(gross, purchase, 5m);
        return r.Tax >= 0 && r.Net + r.Tax == r.Gross && (gross > purchase || r.Tax == 0) && r.Tax <= Math.Max(0, gross - purchase);
    }

    [Fact]
    public void Civil_ids_are_shown_by_their_last_four_characters()
    {
        Assert.Equal("4567", TradeInMath.Last4("12 34-567"));
        Assert.Equal("12", TradeInMath.Last4("12"));
    }
}

public sealed class MarginPricingTests
{
    private static PricedItem Phone(decimal list, decimal? marginCost, string kind = "margin") =>
        new(Guid.NewGuid(), "IP13-128", "device", "imei", "iPhone 13 128GB", "آيفون 13", Guid.NewGuid(), kind, 5m, list, null, 90, true, marginCost);

    [Fact]
    public void A_margin_scheme_line_is_taxed_on_the_margin_whether_or_not_prices_include_tax()
    {
        foreach (var inclusive in new[] { true, false })
        {
            var l = PosMath.PriceLine(1, new CartLine(Guid.NewGuid(), 1m, null, 3m, null, Guid.NewGuid()), Phone(190m, 95m), inclusive, DiscountLimit.Any);
            Assert.Equal((187.000m, 4.381m, 182.619m), (l.Gross, l.Tax, l.Net));   // (187 - 95) x 5 / 105 = 4.381
        }
    }

    [Fact]
    public void A_margin_scheme_line_needs_its_purchase_price_and_keeps_its_tax_under_invoice_rounding()
    {
        Assert.Throws<KiteException>(() => PosMath.PriceLine(1, new CartLine(Guid.NewGuid(), 1m, null, 0m, null, Guid.NewGuid()), Phone(190m, null), true, DiscountLimit.Any));
        var margin = PosMath.PriceLine(1, new CartLine(Guid.NewGuid(), 1m, null, 0m, null, Guid.NewGuid()), Phone(150m, 100m), true, DiscountLimit.Any);
        var cable = PosMath.PriceLine(2, new CartLine(Guid.NewGuid(), 3m), new PricedItem(Guid.NewGuid(), "CBL", "accessory", "none", "Cable", "كابل",
            Guid.NewGuid(), "standard", 5m, 2.333m, null, 30, true), true, DiscountLimit.Any);
        var rounded = PosMath.ApplyInvoiceRounding([margin, cable], true);
        Assert.Equal(2.381m, rounded[0].Tax);                     // 50 x 5 / 105 = 2.381, untouched
        Assert.Equal(cable.Gross, rounded[1].Gross);
    }

    [Fact]
    public void Trade_in_credit_names_its_trade_in_and_is_never_a_refund()
    {
        var ti = Guid.NewGuid();
        var ok = PosMath.CheckTenders([new TenderInput("trade_in", 120m, TradeInId: ti), new TenderInput("cash", 99m, 100m)], 219m, 0.005m);
        Assert.Equal(ti, ok.Tenders[0].TradeInId);
        Assert.Equal(1m, ok.Tenders[1].Change);
        Assert.Throws<KiteException>(() => PosMath.CheckTenders([new TenderInput("trade_in", 10m)], 10m, 0m));
        Assert.Throws<KiteException>(() => PosMath.CheckTenders([new TenderInput("trade_in", 5m, TradeInId: ti), new TenderInput("trade_in", 5m, TradeInId: ti)], 10m, 0m));
        Assert.Throws<KiteException>(() => PosMath.CheckTenders([new TenderInput("trade_in", 10m, TradeInId: ti)], 10m, 0m, refund: true));
        Assert.Throws<KiteException>(() => PosMath.CheckTenders([new TenderInput("trade_in", 11m, TradeInId: ti)], 10m, 0m));   // more than the sale
        Assert.Equal("store_credit", PosMath.CheckTenders([new TenderInput("store_credit", 10m)], 10m, 0m, refund: true).Tenders[0].Method);
    }
}

public sealed class TradeInPostingTests
{
    private static JournalDraft Post(string eventType, PostingFacts f) =>
        PostingEngine.Build(PostingRuleTemplates.All.Single(r => r.EventType == eventType).Definition, f, new DateOnly(2026, 11, 16), "tradein",
            Guid.NewGuid(), eventType, Currency.OMR)!;

    private static (string, decimal, decimal)[] Lines(JournalDraft j) => j.Lines.Select(l => (l.AccountCode, l.Debit, l.Credit)).OrderBy(x => x.AccountCode).ToArray();

    [Fact]
    public void Every_settlement_type_posts_its_journal()
    {
        Assert.Equal([("1410", 120m, 0m), ("2320", 0m, 120m)], Lines(Post(PostingRuleTemplates.TradeInAccept, new PostingFacts().Add("value_by_product_type", "device_used", 120m))));
        Assert.Equal([("2310", 0m, 50m), ("2320", 50m, 0m)], Lines(Post(PostingRuleTemplates.TradeInStoreCredit, new PostingFacts().Set("amount", 50m))));
        Assert.Equal([("1100", 0m, 30m), ("2320", 30m, 0m)], Lines(Post(PostingRuleTemplates.TradeInPayout, new PostingFacts().Set("amount", 30m))));
        // Applied to a sale: a tender that takes trade-in clearing out; store credit used takes the liability out.
        var sale = Post(PostingRuleTemplates.Sale, new PostingFacts().Add("tenders", "trade_in", 40m).Add("tenders", "store_credit", 20m).Add("tenders", "cash", 45m)
            .Add("net_by_product_type", "device_used", 100m).Set("tax", 5m));
        Assert.Equal([("1100", 45m, 0m), ("2200", 0m, 5m), ("2310", 20m, 0m), ("2320", 40m, 0m), ("4110", 0m, 100m)], Lines(sale));
        // A void gives them back.
        var cn = Post(PostingRuleTemplates.CreditNote, new PostingFacts().Add("tenders", "trade_in", 40m).Add("tenders", "store_credit", 20m).Add("tenders", "cash", 45m)
            .Add("net_by_product_type", "device_used", 100m).Set("tax", 5m));
        Assert.Equal([("1100", 0m, 45m), ("2200", 5m, 0m), ("2310", 0m, 20m), ("2320", 0m, 40m), ("4900", 100m, 0m)], Lines(cn));
    }

    [Fact]
    public void Earlier_defaults_are_recognised_for_the_upgrade()
    {
        Assert.Equal(4, PostingRuleTemplates.Earlier.Count);   // Phase 10 added version 4 (before the credit tender)
        Assert.Equal(10, PostingRuleTemplates.Version2.Count);
        var v2 = PostingRuleTemplates.Version2.Single(r => r.EventType == PostingRuleTemplates.Sale).Definition.ToJson();
        Assert.Contains("device_used", v2, StringComparison.Ordinal);
        Assert.DoesNotContain("trade_in", v2, StringComparison.Ordinal);
        Assert.Contains("\"trade_in\":\"2320\"", PostingRuleTemplates.All.Single(r => r.EventType == PostingRuleTemplates.Sale).Definition.ToJson(), StringComparison.Ordinal);
    }
}
