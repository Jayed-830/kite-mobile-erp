using FsCheck;
using FsCheck.Xunit;
using Kite.Accounting;
using Kite.Platform;
using Xunit;

namespace Kite.UnitTests;

public class PostingEngineTests
{
    private static PostingRuleDefinition Rule(string eventType) =>
        PostingRuleTemplates.All.Single(r => r.EventType == eventType).Definition;

    private static readonly DateOnly Day = new(2026, 11, 16);

    [Fact]
    public void Every_default_rule_is_valid_and_survives_a_round_trip_as_data()
    {
        Assert.Equal(17, PostingRuleTemplates.All.Count);   // Phase 5 added refurbishment costs and the used-device reclass; Phase 6 the receipt reversal; Phase 7 three trade-in rules; Phase 8 four repair rules
        foreach (var (_, def) in PostingRuleTemplates.All)
        {
            def.Check();
            var again = PostingRuleDefinition.Parse(def.ToJson());
            Assert.Equal(def.ToJson(), again.ToJson());
        }
    }

    [Fact]
    public void A_mixed_cash_and_card_sale_posts_tenders_revenue_by_product_type_and_vat()
    {
        // Accessory 10.500 incl. 5% VAT (net 10.000, VAT 0.500) and a service 2.100 (net 2.000, VAT 0.100).
        var facts = new PostingFacts()
            .Add("tenders", "cash", 5.000m).Add("tenders", "card", 7.600m)
            .Add("net_by_product_type", "accessory", 10.000m).Add("net_by_product_type", "service", 2.000m)
            .Set("tax", 0.600m);
        var j = PostingEngine.Build(Rule(PostingRuleTemplates.Sale), facts, Day, "sale", Guid.NewGuid(), PostingRuleTemplates.Sale, Currency.OMR)!;
        Assert.Equal(12.600m, j.TotalDebit);
        Assert.Equal(j.TotalDebit, j.TotalCredit);
        Assert.Contains(j.Lines, l => l.AccountCode == "1100" && l.Debit == 5.000m);
        Assert.Contains(j.Lines, l => l.AccountCode == "1150" && l.Debit == 7.600m);
        Assert.Contains(j.Lines, l => l.AccountCode == "4120" && l.Credit == 10.000m);
        Assert.Contains(j.Lines, l => l.AccountCode == "4200" && l.Credit == 2.000m);
        Assert.Contains(j.Lines, l => l.AccountCode == "2200" && l.Credit == 0.600m);
        Assert.DoesNotContain(j.Lines, l => l.AccountCode == "6400");   // no rounding, no line
    }

    [Fact]
    public void Cash_rounding_and_shift_variance_post_on_the_side_their_sign_gives()
    {
        // Customer pays 10.005 for a 10.003 sale (cash rounding to 5 baisa): +0.002 to cash over/short.
        var sale = new PostingFacts().Add("tenders", "cash", 10.005m).Add("net_by_product_type", "accessory", 9.527m).Set("tax", 0.476m).Set("cash_rounding", 0.002m);
        var j = PostingEngine.Build(Rule(PostingRuleTemplates.Sale), sale, Day, "sale", Guid.NewGuid(), "pos.sale", Currency.OMR)!;
        Assert.Contains(j.Lines, l => l.AccountCode == "6400" && l.Credit == 0.002m);

        var shortBy = PostingEngine.Build(Rule(PostingRuleTemplates.ShiftVariance), new PostingFacts().Set("variance", -1.250m), Day, "shift", Guid.NewGuid(), "pos.shift_variance", Currency.OMR)!;
        Assert.Contains(shortBy.Lines, l => l.AccountCode == "6400" && l.Debit == 1.250m);
        Assert.Contains(shortBy.Lines, l => l.AccountCode == "1100" && l.Credit == 1.250m);
        var over = PostingEngine.Build(Rule(PostingRuleTemplates.ShiftVariance), new PostingFacts().Set("variance", 0.500m), Day, "shift", Guid.NewGuid(), "pos.shift_variance", Currency.OMR)!;
        Assert.Contains(over.Lines, l => l.AccountCode == "1100" && l.Debit == 0.500m);
        Assert.Null(PostingEngine.Build(Rule(PostingRuleTemplates.ShiftVariance), new PostingFacts().Set("variance", 0m), Day, "shift", Guid.NewGuid(), "pos.shift_variance", Currency.OMR));
    }

    [Fact]
    public void The_cost_of_a_sale_of_services_only_is_no_journal_and_a_missing_account_is_refused()
    {
        Assert.Null(PostingEngine.Build(Rule(PostingRuleTemplates.SaleCost), new PostingFacts(), Day, "sale", Guid.NewGuid(), "pos.sale_cost", Currency.OMR));
        var cost = PostingEngine.Build(Rule(PostingRuleTemplates.SaleCost), new PostingFacts().Add("cost_by_product_type", "accessory", 3.250m),
            Day, "sale", Guid.NewGuid(), "pos.sale_cost", Currency.OMR)!;
        Assert.Equal(["5120", "1420"], cost.Lines.Select(l => l.AccountCode).ToArray());
        var ex = Assert.Throws<KiteException>(() => PostingEngine.Build(Rule(PostingRuleTemplates.SaleCost),
            new PostingFacts().Add("cost_by_product_type", "service", 1m), Day, "sale", Guid.NewGuid(), "pos.sale_cost", Currency.OMR));
        Assert.Contains("no account for product_type 'service'", ex.Message);
    }

    [Fact]
    public void A_rule_that_does_not_balance_for_the_figures_is_refused_not_posted()
    {
        var facts = new PostingFacts().Add("tenders", "cash", 10m).Add("net_by_product_type", "accessory", 9m);   // tax figure missing
        var ex = Assert.Throws<KiteException>(() => PostingEngine.Build(Rule(PostingRuleTemplates.Sale), facts, Day, "sale", Guid.NewGuid(), "pos.sale", Currency.OMR));
        Assert.Contains("does not balance", ex.Message);
    }

    [Fact]
    public void Invalid_rules_are_refused_before_use()
    {
        Assert.Throws<KiteException>(() => PostingRuleDefinition.Parse("""{"description":"x","lines":[{"side":"debit","account":{"code":"1100"},"amount":"a"}]}"""));
        Assert.Throws<KiteException>(() => PostingRuleDefinition.Parse("""{"description":"x","lines":[{"side":"up","account":{"code":"1100"},"amount":"a"},{"side":"credit","account":{"code":"6400"},"amount":"a"}]}"""));
        Assert.Throws<KiteException>(() => PostingRuleDefinition.Parse("""{"description":"x","lines":[{"side":"debit","account":{"code":"1100","by":"tender"},"amount":"a"},{"side":"credit","account":{"code":"6400"},"amount":"a"}]}"""));
        Assert.Throws<KiteException>(() => PostingRuleDefinition.Parse("""{"description":"x","lines":[{"side":"debit","account":{"by":"tender"},"amount":"a"},{"side":"credit","account":{"code":"6400"},"amount":"a"}]}"""));
    }

    [Fact]
    public void A_stock_adjustment_with_gains_losses_and_opening_stock_posts_one_balanced_journal()
    {
        var facts = new PostingFacts()
            .Add("gain_by_product_type", "accessory", 1.500m)
            .Add("loss_by_product_type", "spare_part", 4.000m)
            .Add("opening_by_product_type", "accessory", 120.000m);
        var j = PostingEngine.Build(Rule(PostingRuleTemplates.StockAdjustment), facts, Day, "stock_adjustment", Guid.NewGuid(), "inv.adjustment", Currency.OMR)!;
        Assert.Equal(j.TotalDebit, j.TotalCredit);
        Assert.Contains(j.Lines, l => l.AccountCode == "1420" && l.Debit == 121.500m);   // gain and opening merged on one line
        Assert.Contains(j.Lines, l => l.AccountCode == "3900" && l.Credit == 120.000m);
        Assert.Contains(j.Lines, l => l.AccountCode == "1430" && l.Credit == 4.000m);
    }

    /// <summary>Property (23.1): any sale whose tenders equal its gross posts a balanced journal equal to the tenders.</summary>
    [Property(MaxTest = 500)]
    public Property Any_paid_sale_posts_a_balanced_journal(NonNegativeInt accessoryBaisa, NonNegativeInt serviceBaisa, NonNegativeInt deviceBaisa,
        PositiveInt rateTenths, bool split, int roundingBaisa)
    {
        var rate = (rateTenths.Get % 200) / 10m;   // 0% .. 19.9%
        decimal Net(NonNegativeInt b) => (b.Get % 5_000_000) / 1000m;
        var nets = new[] { ("accessory", Net(accessoryBaisa)), ("service", Net(serviceBaisa)), ("device", Net(deviceBaisa)) };
        var net = nets.Sum(n => n.Item2);
        var tax = Currency.OMR.Round(net * rate / 100m);
        var rounding = (roundingBaisa % 5) / 1000m;
        var paid = net + tax + rounding;
        var facts = new PostingFacts().Set("tax", tax).Set("cash_rounding", rounding);
        foreach (var (k, v) in nets) facts.Add("net_by_product_type", k, v);
        if (split && paid > 0.001m) { var card = Currency.OMR.Round(paid / 3m); facts.Add("tenders", "card", card).Add("tenders", "cash", paid - card); }
        else facts.Add("tenders", "cash", paid);
        var j = PostingEngine.Build(Rule(PostingRuleTemplates.Sale), facts, Day, "sale", Guid.NewGuid(), "pos.sale", Currency.OMR);
        if (paid == 0m && net == 0m && tax == 0m && rounding == 0m) return (j is null).ToProperty();
        return (j is not null && j.TotalDebit == j.TotalCredit && j.Lines.All(l => l.Debit >= 0 && l.Credit >= 0 && (l.Debit == 0) != (l.Credit == 0))).ToProperty();
    }
}

public class PosMathTests
{
    private static readonly Guid Vat = Guid.NewGuid();

    private static Kite.Sales.PricedItem Item(decimal? price, decimal? min = null, string tracking = "none", string kind = "standard", decimal rate = 5m, string type = "accessory") =>
        new(Guid.NewGuid(), "SKU-1", type, tracking, "Cable", "كابل", Vat, kind, rate, price, min, 30, true);

    private static readonly Kite.Sales.DiscountLimit Cashier = new(5m, 2.000m, false);

    [Fact]
    public void Tax_is_extracted_from_inclusive_prices_per_line()
    {
        var l = Kite.Sales.PosMath.PriceLine(1, new(Guid.NewGuid(), 3m), Item(2.333m), true, Cashier);
        Assert.Equal(6.999m, l.Gross);
        Assert.Equal(0.333m, l.Tax);      // 6.999 x 5 / 105 = 0.3333
        Assert.Equal(6.666m, l.Net);
        Assert.False(l.NeedsPriceOverride || l.NeedsDiscountApproval);
        var exclusive = Kite.Sales.PosMath.PriceLine(1, new(Guid.NewGuid(), 3m), Item(2.333m), false, Cashier);
        Assert.Equal((6.999m, 0.350m, 7.349m), (exclusive.Net, exclusive.Tax, exclusive.Gross));
    }

    [Fact]
    public void The_discount_limit_is_the_lower_of_percent_and_amount()
    {
        // 5% of 20.000 is 1.000 (below 2.000); 5% of 100.000 is 5.000, capped at 2.000.
        Assert.False(Kite.Sales.PosMath.PriceLine(1, new(Guid.NewGuid(), 2m, Discount: 1.000m), Item(10m), true, Cashier).NeedsDiscountApproval);
        Assert.True(Kite.Sales.PosMath.PriceLine(1, new(Guid.NewGuid(), 2m, Discount: 1.001m), Item(10m), true, Cashier).NeedsDiscountApproval);
        Assert.False(Kite.Sales.PosMath.PriceLine(1, new(Guid.NewGuid(), 1m, Discount: 2.000m), Item(100m), true, Cashier).NeedsDiscountApproval);
        Assert.True(Kite.Sales.PosMath.PriceLine(1, new(Guid.NewGuid(), 1m, Discount: 2.001m), Item(100m), true, Cashier).NeedsDiscountApproval);
        Assert.False(Kite.Sales.PosMath.PriceLine(1, new(Guid.NewGuid(), 1m, Discount: 50m), Item(100m), true, Kite.Sales.DiscountLimit.Any).NeedsDiscountApproval);
    }

    [Fact]
    public void A_changed_price_or_a_price_under_the_minimum_needs_an_override()
    {
        Assert.True(Kite.Sales.PosMath.PriceLine(1, new(Guid.NewGuid(), 1m, UnitPrice: 9m), Item(10m), true, Cashier).NeedsPriceOverride);
        Assert.True(Kite.Sales.PosMath.PriceLine(1, new(Guid.NewGuid(), 1m, Discount: 0.400m), Item(10m, min: 9.700m), true, Kite.Sales.DiscountLimit.Any).NeedsPriceOverride);
        Assert.False(Kite.Sales.PosMath.PriceLine(1, new(Guid.NewGuid(), 1m, Discount: 0.300m), Item(10m, min: 9.700m), true, Cashier).NeedsPriceOverride);
        Assert.True(Kite.Sales.PosMath.PriceLine(1, new(Guid.NewGuid(), 1m, UnitPrice: 4m), Item(null), true, Cashier).NeedsPriceOverride);   // no list price
    }

    [Fact]
    public void Lines_that_cannot_be_sold_yet_are_refused_with_their_codes()
    {
        KiteException Err(Kite.Sales.PricedItem i, Kite.Sales.CartLine? l = null) =>
            Assert.Throws<KiteException>(() => Kite.Sales.PosMath.PriceLine(1, l ?? new(i.VariantId, 1m), i, true, Cashier));
        Assert.Equal(ErrorCodes.DeviceUnitRequired, Err(Item(100m, tracking: "imei", type: "device")).Code);   // a phone needs its IMEI scanned (Phase 5)
        Assert.Equal(ErrorCodes.NoPrice, Err(Item(null)).Code);
        Assert.Equal(ErrorCodes.Validation, Err(Item(100m, kind: "margin")).Code);
        Assert.Equal(ErrorCodes.Validation, Err(Item(1m), new(Guid.NewGuid(), 1m, Discount: 2m)).Code);
        Assert.Equal(ErrorCodes.Validation, Err(Item(1m), new(Guid.NewGuid(), 0.0001m)).Code);
    }

    [Fact]
    public void Invoice_level_rounding_taxes_each_rate_once_and_spreads_it_over_the_lines()
    {
        var lines = new[] { 0.105m, 0.105m, 0.105m }.Select((p, i) => Kite.Sales.PosMath.PriceLine(i + 1, new(Guid.NewGuid(), 1m), Item(p), true, Cashier)).ToArray();
        Assert.Equal(0.015m, lines.Sum(l => l.Tax));                     // 0.005 per line
        var invoice = Kite.Sales.PosMath.ApplyInvoiceRounding(lines, true);
        Assert.Equal(0.015m, invoice.Sum(l => l.Tax));                   // 0.315 x 5 / 105 = 0.015
        Assert.All(invoice, l => Assert.Equal(l.Gross, l.Net + l.Tax));
        Assert.Equal(lines.Sum(l => l.Gross), invoice.Sum(l => l.Gross));
    }

    [Theory]
    [InlineData("6.999", "0.005", "7.000")]
    [InlineData("2.333", "0.005", "2.335")]
    [InlineData("2.332", "0.005", "2.330")]
    [InlineData("2.3325", "0.005", "2.335")]
    [InlineData("4.149", "0.100", "4.100")]
    [InlineData("4.150", "0.100", "4.200")]
    [InlineData("1.234", "0", "1.234")]
    public void Cash_rounds_half_away_from_zero_to_the_policy_step(string amount, string step, string expected) =>
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            Kite.Sales.PosMath.RoundCash(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), decimal.Parse(step, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void Tenders_must_match_the_total_with_cash_rounding()
    {
        var mixed = Kite.Sales.PosMath.CheckTenders([new("card", 3.500m, CardTerminal: "BANK-1", ApprovalCode: "123456"), new("cash", 3.500m, Tendered: 5m)], 6.999m, 0.005m);
        Assert.Equal(0.001m, mixed.CashRounding);
        Assert.Equal(7.000m, mixed.Paid);
        Assert.Equal(1.500m, mixed.Tenders[1].Change);
        Assert.Equal(0m, Kite.Sales.PosMath.CheckTenders([new("card", 6.999m, CardTerminal: "B", ApprovalCode: "1")], 6.999m, 0.005m).CashRounding);

        KiteException Err(Kite.Sales.TenderInput[] t, decimal gross = 6.999m, bool refund = false) =>
            Assert.Throws<KiteException>(() => Kite.Sales.PosMath.CheckTenders(t, gross, 0.005m, refund));
        Assert.Equal(ErrorCodes.PaymentMismatch, Err([new("cash", 6.999m)]).Code);                                // must be rounded
        Assert.Equal(ErrorCodes.PaymentMismatch, Err([new("cash", 3m), new("cash", 4m)]).Code);
        Assert.Equal(ErrorCodes.PaymentMismatch, Err([new("card", 8m, CardTerminal: "B", ApprovalCode: "1")]).Code);
        Assert.Equal(ErrorCodes.Validation, Err([new("card", 6.999m, CardTerminal: "B")]).Code);                 // no approval code
        Assert.Equal(ErrorCodes.Validation, Err([new("bank_transfer", 6.999m)]).Code);                          // no reference
        Assert.Equal(ErrorCodes.Validation, Err([new("cash", 7.000m, Tendered: 6m)]).Code);
        Assert.Equal(ErrorCodes.Validation, Err([new("cheque", 6.999m)]).Code);
        // A refund to card needs no new approval code; cash refunds round too.
        var refund = Kite.Sales.PosMath.CheckTenders([new("card", 1m, CardTerminal: "B"), new("cash", 1.335m)], 2.333m, 0.005m, refund: true);
        Assert.Equal(0.002m, refund.CashRounding);
    }

    [Fact]
    public void Returns_take_a_proportional_share_and_the_last_one_takes_the_rest()
    {
        var o = new Kite.Sales.ReturnableLine(3m, 6.666m, 0.333m, 6.999m, 0.100m, 0, 0, 0, 0, 0);
        var first = Kite.Sales.PosMath.ApportionReturn(o, 1m);
        Assert.Equal((2.222m, 0.111m, 2.333m), (first.Net, first.Tax, first.Gross));
        var after1 = o with { ReturnedQty = 1, ReturnedNet = first.Net, ReturnedTax = first.Tax, ReturnedGross = first.Gross, ReturnedDiscount = first.Discount };
        var second = Kite.Sales.PosMath.ApportionReturn(after1, 1m);
        var after2 = after1 with { ReturnedQty = 2, ReturnedNet = after1.ReturnedNet + second.Net, ReturnedTax = after1.ReturnedTax + second.Tax,
                                   ReturnedGross = after1.ReturnedGross + second.Gross, ReturnedDiscount = after1.ReturnedDiscount + second.Discount };
        var last = Kite.Sales.PosMath.ApportionReturn(after2, 1m);
        Assert.Equal(o.Gross, first.Gross + second.Gross + last.Gross);
        Assert.Equal(o.Tax, first.Tax + second.Tax + last.Tax);
        Assert.Equal(o.Net, first.Net + second.Net + last.Net);
        Assert.Equal(ErrorCodes.ReturnTooMany, Assert.Throws<KiteException>(() => Kite.Sales.PosMath.ApportionReturn(after2, 2m)).Code);
    }

    [Fact]
    public void Drawer_counts_use_the_policy_denominations()
    {
        decimal[] den = [50m, 20m, 10m, 5m, 1m, 0.5m, 0.1m, 0.05m, 0.025m, 0.01m, 0.005m];
        Assert.Equal(24.665m, Kite.Sales.PosMath.CountTotal(new Dictionary<string, int> { ["20"] = 1, ["1"] = 4, ["0.5"] = 1, ["0.1"] = 1, ["0.05"] = 1, ["0.01"] = 1, ["0.005"] = 1 }, den));
        Assert.Throws<KiteException>(() => Kite.Sales.PosMath.CountTotal(new Dictionary<string, int> { ["2"] = 1 }, den));
        Assert.Throws<KiteException>(() => Kite.Sales.PosMath.CountTotal(new Dictionary<string, int> { ["1"] = -1 }, den));
        Assert.Equal(24.665m, Kite.Sales.PosMath.ExpectedCash(20m, 11.665m, 5m, 2m, 10m));
    }

    [Fact]
    public void The_qr_payload_is_tlv_in_base64_and_reads_back()
    {
        var id = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 11, 16, 9, 30, 0, TimeSpan.FromHours(4));
        var qr = Kite.Sales.PosMath.QrPayload("شركة النور", "OM1100223344", at, 6.999m, 0.333m, id);
        var back = Kite.Sales.PosMath.ReadQr(qr);
        Assert.Equal("شركة النور", back[1]);
        Assert.Equal("OM1100223344", back[2]);
        Assert.Equal("2026-11-16T05:30:00Z", back[3]);
        Assert.Equal("6.999", back[4]);
        Assert.Equal("0.333", back[5]);
        Assert.Equal(id.ToString(), back[6]);
        Assert.Equal(1, Convert.FromBase64String(qr)[0]);
    }

    /// <summary>Property: any cart prices to lines whose gross is net plus tax, and cash rounding never exceeds half a step.</summary>
    [Property(MaxTest = 300)]
    public Property Any_cart_adds_up_and_cash_rounding_stays_within_half_a_step(PositiveInt baisa, PositiveInt qty, NonNegativeInt discountBaisa, bool inclusive)
    {
        var price = Math.Min(baisa.Get, 10_000_000) / 1000m;
        var q = Math.Min(qty.Get, 50);
        var discount = Math.Min(discountBaisa.Get / 1000m, Currency.OMR.Round(price * q));
        var l = Kite.Sales.PosMath.PriceLine(1, new(Guid.NewGuid(), q, Discount: discount), Item(price), inclusive, Kite.Sales.DiscountLimit.Any);
        var cash = Kite.Sales.PosMath.RoundCash(l.Gross, 0.005m);
        var paid = Kite.Sales.PosMath.CheckTenders(cash == 0 ? [] : [new("cash", cash)], l.Gross, 0.005m);
        return (l.Gross == l.Net + l.Tax && l.Net >= 0 && Math.Abs(paid.CashRounding) <= 0.0025m && paid.Paid == l.Gross + paid.CashRounding).ToProperty();
    }
}

/// <summary>The POS vectors shared with the TypeScript copy (@kite/pos), written by an independent implementation.</summary>
public class PosVectorTests
{
    private static System.Text.Json.JsonElement Vectors() =>
        System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot.Path, "tests", "vectors", "pos-math-vectors.json"))).RootElement;

    private static decimal Dec(System.Text.Json.JsonElement e) => decimal.Parse(e.GetString()!, System.Globalization.CultureInfo.InvariantCulture);
    private static decimal? DecOrNull(System.Text.Json.JsonElement e) => e.ValueKind == System.Text.Json.JsonValueKind.Null ? null : Dec(e);

    [Fact]
    public void Line_pricing_matches_the_shared_vectors()
    {
        var n = 0;
        foreach (var v in Vectors().GetProperty("lines").EnumerateArray())
        {
            var item = new Kite.Sales.PricedItem(Guid.NewGuid(), "V", "accessory", "none", "x", "x", Guid.NewGuid(), "standard", Dec(v.GetProperty("taxRate")),
                Dec(v.GetProperty("listPrice")), DecOrNull(v.GetProperty("minPrice")), 0, true);
            var lim = v.GetProperty("limit");
            var limit = new Kite.Sales.DiscountLimit(Dec(lim.GetProperty("percent")), Dec(lim.GetProperty("amount")), lim.GetProperty("unlimited").GetBoolean());
            var l = Kite.Sales.PosMath.PriceLine(1, new(item.VariantId, Dec(v.GetProperty("qty")), DecOrNull(v.GetProperty("unitPrice")), Dec(v.GetProperty("discount"))),
                item, v.GetProperty("inclusive").GetBoolean(), limit);
            var e = v.GetProperty("expect");
            Assert.Equal((Dec(e.GetProperty("net")), Dec(e.GetProperty("tax")), Dec(e.GetProperty("gross"))), (l.Net, l.Tax, l.Gross));
            Assert.Equal(e.GetProperty("needsPriceOverride").GetBoolean(), l.NeedsPriceOverride);
            Assert.Equal(e.GetProperty("needsDiscountApproval").GetBoolean(), l.NeedsDiscountApproval);
            n++;
        }
        Assert.True(n >= 15);
    }

    [Fact]
    public void Cash_rounding_and_return_shares_match_the_shared_vectors()
    {
        foreach (var v in Vectors().GetProperty("roundCash").EnumerateArray())
            Assert.Equal(Dec(v.GetProperty("expect")), Kite.Sales.PosMath.RoundCash(Dec(v.GetProperty("amount")), Dec(v.GetProperty("step"))));
        foreach (var v in Vectors().GetProperty("apportion").EnumerateArray())
        {
            var l = v.GetProperty("line");
            var o = new Kite.Sales.ReturnableLine(Dec(l.GetProperty("qty")), Dec(l.GetProperty("net")), Dec(l.GetProperty("tax")), Dec(l.GetProperty("gross")), Dec(l.GetProperty("discount")), 0, 0, 0, 0, 0);
            var expect = v.GetProperty("expect").EnumerateArray().ToArray();
            var i = 0;
            foreach (var q in v.GetProperty("returns").EnumerateArray())
            {
                var a = Kite.Sales.PosMath.ApportionReturn(o, Dec(q));
                Assert.Equal((Dec(expect[i].GetProperty("net")), Dec(expect[i].GetProperty("tax")), Dec(expect[i].GetProperty("gross")), Dec(expect[i].GetProperty("discount"))),
                    (a.Net, a.Tax, a.Gross, a.Discount));
                o = o with { ReturnedQty = o.ReturnedQty + Dec(q), ReturnedNet = o.ReturnedNet + a.Net, ReturnedTax = o.ReturnedTax + a.Tax, ReturnedGross = o.ReturnedGross + a.Gross, ReturnedDiscount = o.ReturnedDiscount + a.Discount };
                i++;
            }
            Assert.Equal(o.Qty, o.ReturnedQty);
            Assert.Equal(o.Gross, o.ReturnedGross);
        }
    }
}

/// <summary>Emergency mode checks PINs offline in the Windows app: its Argon2id hashes and the server's must be the same format.</summary>
public class PinHashCompatibilityTests
{
    [Fact]
    public void The_server_verifies_pin_hashes_written_by_the_windows_app()
    {
        var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot.Path, "tests", "vectors", "pin-hash-vectors.json"))).RootElement;
        var hasher = new Kite.Identity.Argon2PasswordHasher();
        foreach (var v in doc.GetProperty("vectors").EnumerateArray())
        {
            Assert.True(hasher.Verify(v.GetProperty("pin").GetString()!, v.GetProperty("hash").GetString()!));
            Assert.False(hasher.Verify("0000", v.GetProperty("hash").GetString()!));
        }
        // And the other way: the shape the server writes is the PHC form the app's verifier reads.
        Assert.Matches(@"^\$argon2id\$v=19\$m=\d+,t=\d+,p=\d+\$[A-Za-z0-9+/]+\$[A-Za-z0-9+/]+$", new Kite.Identity.Argon2PasswordHasher(Kite.Identity.Argon2Options.ForTests).Hash("2468"));
    }
}
