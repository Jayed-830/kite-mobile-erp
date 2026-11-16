using System.Globalization;
using System.Text.Json;
using Kite.Accounting;
using Kite.Platform;
using Kite.Sales;
using Xunit;

namespace Kite.UnitTests;

/// <summary>Phase 9 commission arithmetic against the vectors of an independent Python implementation, and the new posting rules.</summary>
public sealed class CommissionVectorTests
{
    private static readonly JsonElement V =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot.Path, "tests", "vectors", "commission-math-vectors.json"))).RootElement;

    private static decimal D(JsonElement e) => decimal.Parse(e.GetString()!, CultureInfo.InvariantCulture);
    private static decimal? N(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : D(e);

    [Fact]
    public void Every_basis_with_minimum_maximum_and_share_matches_the_vectors()
    {
        var n = 0;
        foreach (var v in V.GetProperty("commission").EnumerateArray())
        {
            var b = new CommissionBasis(D(v.GetProperty("labour")), D(v.GetProperty("partsRevenue")), D(v.GetProperty("partsCost")), D(v.GetProperty("external")));
            var r = CommissionMath.Compute(v.GetProperty("basis").GetString()!, N(v.GetProperty("amount")), N(v.GetProperty("rate")), N(v.GetProperty("min")),
                N(v.GetProperty("max")), b, D(v.GetProperty("share")));
            var e = v.GetProperty("expect");
            Assert.Equal(D(e.GetProperty("base")), r.Base);
            Assert.Equal(D(e.GetProperty("raw")), r.Raw);
            Assert.Equal(D(e.GetProperty("clamped")), r.Clamped);
            Assert.Equal(D(e.GetProperty("amount")), r.Amount);
            n++;
        }
        Assert.True(n >= 100, $"only {n} vectors");
    }

    [Fact]
    public void Worked_examples_and_refusals()
    {
        var job = new CommissionBasis(Labour: 20m, PartsRevenue: 30m, PartsCost: 18m, External: 5m);
        Assert.Equal(27m, job.Profit);
        Assert.Equal(2.5m, CommissionMath.Compute("fixed", 2.5m, null, null, null, job, 100m).Amount);
        Assert.Equal(2m, CommissionMath.Compute("labour", null, 10m, null, null, job, 100m).Amount);
        Assert.Equal(2.7m, CommissionMath.Compute("profit", null, 10m, null, null, job, 100m).Amount);
        // The minimum and maximum apply to the job, before the technician's share.
        Assert.Equal(1.5m, CommissionMath.Compute("labour", null, 10m, 3m, null, job, 50m).Amount);
        Assert.Equal(0.333m, CommissionMath.Compute("profit", null, 10m, null, 1m, job, 33.3333m).Amount);
        // A loss earns nothing on profit (but a minimum still applies).
        var loss = new CommissionBasis(5m, 10m, 30m, 0m);
        Assert.Equal(0m, CommissionMath.Compute("profit", null, 20m, null, null, loss, 100m).Amount);
        Assert.Equal(1m, CommissionMath.Compute("profit", null, 20m, 1m, null, loss, 100m).Amount);
        // Half a baisa rounds away from zero.
        Assert.Equal(0.002m, CommissionMath.Compute("labour", null, 1m, null, null, new CommissionBasis(0.15m, 0, 0, 0), 100m).Amount);

        Assert.Equal(ErrorCodes.CommissionShares, Assert.Throws<KiteException>(() => CommissionMath.Compute("fixed", 1m, null, null, null, job, 0m)).Code);
        Assert.Equal(ErrorCodes.CommissionShares, Assert.Throws<KiteException>(() => CommissionMath.Compute("fixed", 1m, null, null, null, job, 100.01m)).Code);
        Assert.Equal(ErrorCodes.CommissionRule, Assert.Throws<KiteException>(() => CommissionMath.Compute("fixed", null, null, null, null, job, 100m)).Code);
        Assert.Equal(ErrorCodes.CommissionRule, Assert.Throws<KiteException>(() => CommissionMath.Compute("labour", null, 101m, null, null, job, 100m)).Code);
        Assert.Equal(ErrorCodes.CommissionRule, Assert.Throws<KiteException>(() => CommissionMath.Compute("profit", null, 5m, 3m, 2m, job, 100m)).Code);
        Assert.Equal(ErrorCodes.CommissionRule, Assert.Throws<KiteException>(() => CommissionMath.Compute("sales", null, 5m, null, null, job, 100m)).Code);
    }
}

public sealed class Phase9PostingTests
{
    private static JournalDraft Post(string eventType, PostingFacts facts) =>
        PostingEngine.Build(PostingRuleTemplates.All.Single(r => r.EventType == eventType).Definition, facts, new DateOnly(2026, 11, 17), "test", Guid.NewGuid(), eventType, Currency.OMR)!;

    private static (string Code, decimal Dr, decimal Cr)[] Lines(JournalDraft d) =>
        d.Lines.Select(l => (l.AccountCode, l.Debit, l.Credit)).OrderBy(x => x.AccountCode, StringComparer.Ordinal).ToArray();

    [Fact]
    public void Outside_work_commissions_and_payouts_post_their_journals()
    {
        // Outside work into work-in-progress against the supplier, reversed by a negative fact, and out to its own cost at delivery.
        Assert.Equal([("1460", 15m, 0m), ("2110", 0m, 15m)], Lines(Post(PostingRuleTemplates.RepairOutsideWork, new PostingFacts().Set("incurred", 15m))));
        Assert.Equal([("1460", 0m, 15m), ("2110", 15m, 0m)], Lines(Post(PostingRuleTemplates.RepairOutsideWork, new PostingFacts().Set("incurred", -15m))));
        Assert.Equal([("1460", 0m, 15m), ("5210", 15m, 0m)], Lines(Post(PostingRuleTemplates.RepairOutsideWork, new PostingFacts().Set("cost", 15m))));
        // A commission earned, and reversed or clawed back.
        Assert.Equal([("2400", 0m, 2.88m), ("6300", 2.88m, 0m)], Lines(Post(PostingRuleTemplates.CommissionAccrual, new PostingFacts().Set("amount", 2.88m))));
        Assert.Equal([("2400", 2.88m, 0m), ("6300", 0m, 2.88m)], Lines(Post(PostingRuleTemplates.CommissionAccrual, new PostingFacts().Set("amount", -2.88m))));
        // Paid in cash, to the bank, or through payroll clearing.
        Assert.Equal([("1100", 0m, 10m), ("2400", 10m, 0m)], Lines(Post(PostingRuleTemplates.CommissionPayout, new PostingFacts().Add("paid_by_method", "cash", 10m))));
        Assert.Equal([("1200", 0m, 10m), ("2400", 10m, 0m)], Lines(Post(PostingRuleTemplates.CommissionPayout, new PostingFacts().Add("paid_by_method", "bank", 10m))));
        Assert.Equal([("2400", 10m, 0m), ("2410", 0m, 10m)], Lines(Post(PostingRuleTemplates.CommissionPayout, new PostingFacts().Add("paid_by_method", "payroll", 10m))));
    }

    [Fact]
    public void Earlier_default_sets_leave_out_the_Phase_9_rules()
    {
        foreach (var set in new[] { PostingRuleTemplates.Version1, PostingRuleTemplates.Version2, PostingRuleTemplates.Version3 })
            Assert.DoesNotContain(set, r => r.EventType is PostingRuleTemplates.RepairOutsideWork or PostingRuleTemplates.CommissionAccrual or PostingRuleTemplates.CommissionPayout);
        Assert.Contains(ChartTemplate.Accounts, a => a.Code == "2410");
        Assert.Contains(ChartTemplate.Accounts, a => a.Code == "5210");
    }
}
