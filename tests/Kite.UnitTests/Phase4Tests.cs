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
        Assert.Equal(7, PostingRuleTemplates.All.Count);
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
