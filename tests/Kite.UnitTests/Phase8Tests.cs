using System.Globalization;
using System.Text.Json;
using Kite.Accounting;
using Kite.Platform;
using Kite.Sales;
using Xunit;

namespace Kite.UnitTests;

/// <summary>Phase 8 repair arithmetic against the vectors of an independent Python implementation, and the repair posting rules.</summary>
public sealed class RepairVectorTests
{
    private static readonly JsonElement V =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot.Path, "tests", "vectors", "repair-math-vectors.json"))).RootElement;

    private static decimal D(JsonElement e) => decimal.Parse(e.GetString()!, CultureInfo.InvariantCulture);

    [Fact]
    public void Labour_by_the_hour_matches_the_vectors()
    {
        foreach (var v in V.GetProperty("labour").EnumerateArray())
            Assert.Equal(D(v.GetProperty("expect")), RepairMath.LabourPrice(v.GetProperty("minutes").GetInt32(), D(v.GetProperty("rate"))));
    }

    [Fact]
    public void Deposit_VAT_and_its_shares_match_the_vectors()
    {
        foreach (var v in V.GetProperty("depositTax").EnumerateArray())
            Assert.Equal(D(v.GetProperty("expect")), RepairMath.DepositTax(D(v.GetProperty("amount")), D(v.GetProperty("rate"))));
        foreach (var v in V.GetProperty("taxShare").EnumerateArray())
            Assert.Equal(D(v.GetProperty("expect")), RepairMath.TaxShare(D(v.GetProperty("held")), D(v.GetProperty("balance")), D(v.GetProperty("part"))));
    }

    [Fact]
    public void Delivery_splits_match_the_vectors_and_always_add_up()
    {
        foreach (var v in V.GetProperty("split").EnumerateArray())
        {
            var gross = D(v.GetProperty("gross"));
            var balance = D(v.GetProperty("balance"));
            var tax = D(v.GetProperty("tax"));
            var e = v.GetProperty("expect");
            var s = RepairMath.Split(gross, balance, tax);
            Assert.Equal(D(e.GetProperty("applied")), s.Applied);
            Assert.Equal(D(e.GetProperty("due")), s.Due);
            Assert.Equal(D(e.GetProperty("excess")), s.Excess);
            Assert.Equal(D(e.GetProperty("appliedTax")), s.AppliedTax);
            Assert.Equal(D(e.GetProperty("excessTax")), s.ExcessTax);
            Assert.Equal(gross, s.Applied + s.Due);
            Assert.Equal(balance, s.Applied + s.Excess);
            Assert.Equal(tax, s.AppliedTax + s.ExcessTax);
        }
    }

    [Fact]
    public void Cost_allocation_and_returns_match_the_vectors()
    {
        foreach (var v in V.GetProperty("allocate").EnumerateArray())
        {
            var r = RepairMath.AllocateCost(D(v.GetProperty("cost")), v.GetProperty("weights").EnumerateArray().Select(D).ToArray());
            Assert.Equal(v.GetProperty("expect").EnumerateArray().Select(D).ToArray(), r);
            Assert.Equal(D(v.GetProperty("cost")), r.Sum());
        }
        foreach (var v in V.GetProperty("returnCost").EnumerateArray())
            Assert.Equal(D(v.GetProperty("expect")), RepairMath.ReturnCost(D(v.GetProperty("fittedQty")), D(v.GetProperty("fittedCost")),
                D(v.GetProperty("returnedQty")), D(v.GetProperty("returnedCost")), D(v.GetProperty("qty"))));
    }

    [Fact]
    public void Refusals()
    {
        Assert.Equal(ErrorCodes.Validation, Assert.Throws<KiteException>(() => RepairMath.LabourPrice(0, 10m)).Code);
        Assert.Throws<KiteException>(() => RepairMath.TaxShare(1m, 10m, 11m));
        Assert.Throws<KiteException>(() => RepairMath.ReturnCost(2m, 24m, 1m, 12m, 2m));
        Assert.Throws<KiteException>(() => RepairMath.Split(-1m, 0m, 0m));
    }
}

public sealed class RepairPostingTests
{
    private static JournalDraft Post(string eventType, PostingFacts facts) =>
        PostingEngine.Build(PostingRuleTemplates.All.Single(r => r.EventType == eventType).Definition, facts, new DateOnly(2026, 11, 16), "test", Guid.NewGuid(), eventType, Currency.OMR)!;

    private static (string Code, decimal Dr, decimal Cr)[] Lines(JournalDraft d) =>
        d.Lines.Select(l => (l.AccountCode, l.Debit, l.Credit)).OrderBy(x => x.AccountCode, StringComparer.Ordinal).ToArray();

    [Fact]
    public void Each_repair_event_posts_its_journal()
    {
        // A deposit in cash; with deposits as a VAT point its VAT goes to VAT output at once.
        Assert.Equal([("1100", 10m, 0m), ("2300", 0m, 10m)], Lines(Post(PostingRuleTemplates.RepairDeposit, new PostingFacts().Add("tenders", "cash", 10m).Set("deposit", 10m).Set("tax", 0m))));
        Assert.Equal([("1150", 21m, 0m), ("2200", 0m, 1m), ("2300", 0m, 20m)], Lines(Post(PostingRuleTemplates.RepairDeposit, new PostingFacts().Add("tenders", "card", 21m).Set("deposit", 20m).Set("tax", 1m))));
        // Given back as store credit.
        Assert.Equal([("2300", 17m, 0m), ("2310", 0m, 17m)],
            Lines(Post(PostingRuleTemplates.RepairDepositRefund, new PostingFacts().Set("deposit", 17m).Set("tax", 0m).Add("tenders", "store_credit", 17m))));
        // A part fitted and one returned.
        Assert.Equal([("1430", 0m, 24m), ("1460", 24m, 0m)], Lines(Post(PostingRuleTemplates.RepairParts, new PostingFacts().Add("parts_by_product_type", "spare_part", 24m))));
        Assert.Equal([("1430", 12m, 0m), ("1460", 0m, 12m)], Lines(Post(PostingRuleTemplates.RepairParts, new PostingFacts().Add("parts_by_product_type", "spare_part", -12m))));
        // Work-in-progress out: cost of the repair, warranty, write-off.
        Assert.Equal([("1460", 0m, 12m), ("5200", 12m, 0m)], Lines(Post(PostingRuleTemplates.RepairCost, new PostingFacts().Set("cost_of_repair", 12m))));
        Assert.Equal([("1460", 0m, 12m), ("6600", 12m, 0m)], Lines(Post(PostingRuleTemplates.RepairCost, new PostingFacts().Set("warranty", 12m))));
        Assert.Equal([("1460", 0m, 12m), ("6700", 12m, 0m)], Lines(Post(PostingRuleTemplates.RepairCost, new PostingFacts().Set("written_off", 12m))));
        // The repair invoice: labour and parts income, the deposit applied (its declared VAT back out of VAT output), the rest in cash.
        var sale = Post(PostingRuleTemplates.Sale, new PostingFacts().Add("tenders", "deposit", 20m).Add("tenders", "cash", 21m).Set("deposit_tax", 1m)
            .Add("net_by_product_type", "service", 20m).Add("net_by_product_type", "repair_part", 20m).Set("tax", 2m));
        Assert.Equal([("1100", 21m, 0m), ("2200", 1m, 0m), ("2200", 0m, 2m), ("2300", 20m, 0m), ("4200", 0m, 20m), ("4210", 0m, 20m)], Lines(sale));
    }

    [Fact]
    public void Earlier_defaults_still_upgrade_a_Phase_7_business()
    {
        var v3 = PostingRuleTemplates.Version3.Single(r => r.EventType == PostingRuleTemplates.Sale).Definition.ToJson();
        Assert.Contains("\"trade_in\":\"2320\"", v3, StringComparison.Ordinal);
        Assert.DoesNotContain("deposit", v3, StringComparison.Ordinal);
        Assert.DoesNotContain("repair_part", v3, StringComparison.Ordinal);
        Assert.Equal(13, PostingRuleTemplates.Version3.Count);
        var now = PostingRuleTemplates.All.Single(r => r.EventType == PostingRuleTemplates.Sale).Definition.ToJson();
        Assert.Contains("\"deposit\":\"2300\"", now, StringComparison.Ordinal);
        Assert.Contains("\"repair_part\":\"4210\"", now, StringComparison.Ordinal);
    }
}
