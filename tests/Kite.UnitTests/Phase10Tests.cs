using System.Globalization;
using System.Text.Json;
using Kite.Accounting;
using Kite.Platform;
using Kite.Sales;
using Xunit;

namespace Kite.UnitTests;

/// <summary>Phase 10 credit arithmetic against the vectors of an independent Python implementation, and the new posting rules.</summary>
public sealed class CreditVectorTests
{
    private static readonly JsonElement V =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot.Path, "tests", "vectors", "credit-math-vectors.json"))).RootElement;

    private static decimal D(JsonElement e) => decimal.Parse(e.GetString()!, CultureInfo.InvariantCulture);
    private static DateOnly Day(JsonElement e) => DateOnly.ParseExact(e.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Fact]
    public void Available_credit_online_and_offline_matches_the_vectors()
    {
        var n = 0;
        foreach (var v in V.GetProperty("online").EnumerateArray())
        {
            Assert.Equal(D(v.GetProperty("expect")), CreditMath.AvailableOnline(D(v.GetProperty("limit")), D(v.GetProperty("exposure")), D(v.GetProperty("unsynced"))));
            n++;
        }
        foreach (var v in V.GetProperty("offline").EnumerateArray())
        {
            Assert.Equal(D(v.GetProperty("expect")), CreditMath.AvailableOffline(D(v.GetProperty("allowance")), D(v.GetProperty("limit")),
                D(v.GetProperty("lastExposure")), D(v.GetProperty("sinceSync")), D(v.GetProperty("cap"))));
            n++;
        }
        Assert.True(n >= 160, $"only {n} vectors");
    }

    [Fact]
    public void Decisions_match_the_vectors()
    {
        var names = new Dictionary<string, CreditOutcome>
        {
            ["ok"] = CreditOutcome.Ok, ["needs_approval"] = CreditOutcome.NeedsApproval, ["approved"] = CreditOutcome.Approved,
            ["over_cap"] = CreditOutcome.OverCap, ["hold"] = CreditOutcome.Hold, ["closed"] = CreditOutcome.Closed,
        };
        var n = 0;
        foreach (var v in V.GetProperty("decide").EnumerateArray())
        {
            var r = CreditMath.Decide(D(v.GetProperty("amount")), D(v.GetProperty("available")), D(v.GetProperty("overrideCap")),
                v.GetProperty("overdueDays").GetInt32(), v.GetProperty("blockDays").GetInt32(), v.GetProperty("status").GetString()!, v.GetProperty("hasApproval").GetBoolean());
            Assert.Equal(names[v.GetProperty("expect").GetProperty("outcome").GetString()!], r.Outcome);
            Assert.Equal(D(v.GetProperty("expect").GetProperty("overBy")), r.OverBy);
            n++;
        }
        Assert.True(n >= 100, $"only {n} vectors");
    }

    [Fact]
    public void Instalments_allocation_and_aging_match_the_vectors()
    {
        foreach (var v in V.GetProperty("instalments").EnumerateArray())
        {
            var plan = CreditMath.Instalments(D(v.GetProperty("total")), v.GetProperty("count").GetInt32(), Day(v.GetProperty("saleDate")),
                v.GetProperty("terms").GetInt32(), v.GetProperty("interval").GetInt32());
            var expect = v.GetProperty("expect").EnumerateArray().ToArray();
            Assert.Equal(expect.Length, plan.Count);
            for (var i = 0; i < plan.Count; i++)
            {
                Assert.Equal(expect[i].GetProperty("seq").GetInt32(), plan[i].Seq);
                Assert.Equal(Day(expect[i].GetProperty("due")), plan[i].Due);
                Assert.Equal(D(expect[i].GetProperty("amount")), plan[i].Amount);
            }
            Assert.Equal(D(v.GetProperty("total")), plan.Sum(x => x.Amount));
        }
        foreach (var v in V.GetProperty("allocate").EnumerateArray())
        {
            var ids = new Dictionary<string, Guid>();
            var items = v.GetProperty("items").EnumerateArray().Select(i =>
            {
                var id = Guid.NewGuid();
                ids[i.GetProperty("id").GetString()!] = id;
                return new OpenItem(id, Day(i.GetProperty("due")), D(i.GetProperty("open")));
            }).ToArray();
            var (allocations, left) = CreditMath.AllocateOldestFirst(items, D(v.GetProperty("amount")));
            var expect = v.GetProperty("expect");
            Assert.Equal(expect.GetProperty("allocations").EnumerateArray().Select(a => (ids[a.GetProperty("id").GetString()!], D(a.GetProperty("amount")))).ToArray(),
                allocations.Select(a => (a.ItemId, a.Amount)).ToArray());
            Assert.Equal(D(expect.GetProperty("left")), left);
        }
        foreach (var v in V.GetProperty("aging").EnumerateArray())
        {
            var b = CreditMath.Aging(v.GetProperty("items").EnumerateArray().Select(i => (Day(i.GetProperty("due")), D(i.GetProperty("open")))),
                D(v.GetProperty("unallocated")), Day(v.GetProperty("asOf")));
            var e = v.GetProperty("expect");
            Assert.Equal((D(e.GetProperty("current")), D(e.GetProperty("d1_30")), D(e.GetProperty("d31_60")), D(e.GetProperty("d61_90")), D(e.GetProperty("d90plus")),
                D(e.GetProperty("onAccount")), D(e.GetProperty("total"))), (b.Current, b.D1To30, b.D31To60, b.D61To90, b.D90Plus, b.OnAccount, b.Total));
        }
    }

    [Fact]
    public void Worked_examples()
    {
        // The test table (25): limit 500, allowances 300 and 200, 100 already owed at the last sync.
        Assert.Equal(300m, CreditMath.AvailableOffline(300m, 500m, 100m, 0m, 1000m));
        Assert.Equal(200m, CreditMath.AvailableOffline(200m, 500m, 100m, 0m, 1000m));
        Assert.Equal(100m, CreditMath.AvailableOffline(300m, 500m, 100m, 0m, 100m));            // the per-sale cap
        Assert.Equal(0m, CreditMath.AvailableOffline(300m, 500m, 100m, 450m, 1000m));           // never below zero
        Assert.Equal(25m, CreditMath.AvailableOnline(500m, 450m, 25m));
        Assert.Equal(0, CreditMath.OverdueDays(null, new DateOnly(2026, 11, 17)));
        Assert.Equal(17, CreditMath.OverdueDays(new DateOnly(2026, 10, 31), new DateOnly(2026, 11, 17)));
        Assert.Equal(CreditOutcome.NeedsApproval, CreditMath.Decide(10m, 50m, 50m, 31, 30, "active", false).Outcome);
        Assert.Equal(CreditOutcome.Ok, CreditMath.Decide(10m, 50m, 50m, 30, 30, "active", false).Outcome);
        Assert.Throws<ArgumentOutOfRangeException>(() => CreditMath.Instalments(10m, 0, new DateOnly(2026, 1, 1), 30, 30));
    }
}

public sealed class Phase10PostingTests
{
    private static JournalDraft Post(string eventType, PostingFacts facts) =>
        PostingEngine.Build(PostingRuleTemplates.All.Single(r => r.EventType == eventType).Definition, facts, new DateOnly(2026, 11, 17), "test", Guid.NewGuid(), eventType, Currency.OMR)!;

    private static (string Code, decimal Dr, decimal Cr)[] Lines(JournalDraft d) =>
        d.Lines.Select(l => (l.AccountCode, l.Debit, l.Credit)).OrderBy(x => x.AccountCode, StringComparer.Ordinal).ToArray();

    [Fact]
    public void Credit_sales_payments_interbranch_write_offs_and_opening_balances_post_their_journals()
    {
        // A sale on account: receivable against revenue and VAT (14.6); a credit note takes the receivable back.
        Assert.Equal([("1300", 105m, 0m), ("2200", 0m, 5m), ("4100", 0m, 100m)], Lines(Post(PostingRuleTemplates.Sale,
            new PostingFacts().Add("tenders", "credit", 105m).Add("net_by_product_type", "device", 100m).Set("tax", 5m))));
        Assert.Equal([("1300", 0m, 105m), ("2200", 5m, 0m), ("4900", 100m, 0m)], Lines(Post(PostingRuleTemplates.CreditNote,
            new PostingFacts().Add("tenders", "credit", 105m).Add("net_by_product_type", "device", 100m).Set("tax", 5m))));
        // A payment by each method.
        Assert.Equal([("1100", 40m, 0m), ("1300", 0m, 40m)], Lines(Post(PostingRuleTemplates.CreditPayment, new PostingFacts().Add("received_by_method", "cash", 40m))));
        Assert.Equal([("1150", 40m, 0m), ("1300", 0m, 40m)], Lines(Post(PostingRuleTemplates.CreditPayment, new PostingFacts().Add("received_by_method", "card", 40m))));
        Assert.Equal([("1200", 40m, 0m), ("1300", 0m, 40m)], Lines(Post(PostingRuleTemplates.CreditPayment, new PostingFacts().Add("received_by_method", "bank_transfer", 40m))));
        // Paid at branch B for branch A's invoice: B owes A, A is owed by B; each branch balances on its own.
        Assert.Equal([("1300", 40m, 0m), ("2500", 0m, 40m)], Lines(Post(PostingRuleTemplates.CreditInterbranchOut, new PostingFacts().Set("amount", 40m))));
        Assert.Equal([("1300", 0m, 40m), ("1310", 40m, 0m)], Lines(Post(PostingRuleTemplates.CreditInterbranchIn, new PostingFacts().Set("amount", 40m))));
        Assert.Equal([("1300", 0m, 12m), ("6500", 12m, 0m)], Lines(Post(PostingRuleTemplates.CreditWriteOff, new PostingFacts().Set("amount", 12m))));
        Assert.Equal([("1300", 30m, 0m), ("3900", 0m, 30m)], Lines(Post(PostingRuleTemplates.CreditOpening, new PostingFacts().Set("amount", 30m))));
        Assert.Equal([("1300", 0m, 30m), ("3900", 30m, 0m)], Lines(Post(PostingRuleTemplates.CreditOpening, new PostingFacts().Set("amount", -30m))));
        Assert.Equal([("2100", 0m, 70m), ("3900", 70m, 0m)], Lines(Post(PostingRuleTemplates.SupplierOpening, new PostingFacts().Set("amount", 70m))));
        Assert.Equal([("2100", 70m, 0m), ("3900", 0m, 70m)], Lines(Post(PostingRuleTemplates.SupplierOpening, new PostingFacts().Set("amount", -70m))));
    }

    [Fact]
    public void Earlier_default_sets_leave_out_the_Phase_10_rules_and_the_credit_tender()
    {
        var phase10 = new[] { PostingRuleTemplates.CreditPayment, PostingRuleTemplates.CreditWriteOff, PostingRuleTemplates.CreditInterbranchOut,
            PostingRuleTemplates.CreditInterbranchIn, PostingRuleTemplates.CreditOpening, PostingRuleTemplates.SupplierOpening };
        foreach (var set in PostingRuleTemplates.Earlier)
        {
            Assert.DoesNotContain(set, r => phase10.Contains(r.EventType));
            Assert.DoesNotContain("\"credit\":\"1300\"", set.Single(r => r.EventType == PostingRuleTemplates.Sale).Definition.ToJson().Replace(" ", ""));
        }
        Assert.Contains("1300", PostingRuleTemplates.All.Single(r => r.EventType == PostingRuleTemplates.Sale).Definition.ToJson());
        // The Phase 9 sale rule is version 4 exactly, so a business still on it is upgraded.
        Assert.NotEqual(PostingRuleTemplates.Version4.Single(r => r.EventType == PostingRuleTemplates.Sale).Definition.ToJson(),
            PostingRuleTemplates.All.Single(r => r.EventType == PostingRuleTemplates.Sale).Definition.ToJson());
        Assert.Contains(Permissions.All, p => p == "CREDIT.MANAGE");
    }
}
