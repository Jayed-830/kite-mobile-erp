using System.Globalization;
using System.Text.Json;
using FsCheck;
using FsCheck.Xunit;
using Kite.Accounting;
using Kite.Devices;
using Kite.Platform;
using Xunit;

namespace Kite.UnitTests;

/// <summary>Phase 5 device rules against the vectors of an independent Python implementation, plus properties.</summary>
public sealed class DeviceVectorTests
{
    private static readonly JsonElement V =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot.Path, "tests", "vectors", "device-math-vectors.json"))).RootElement;

    private static decimal? Dec(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : decimal.Parse(e.GetString()!, CultureInfo.InvariantCulture);

    private static List<ChecklistItem> Checklist() => V.GetProperty("checklist").EnumerateArray().Select(i => new ChecklistItem(
        i.GetProperty("code").GetString()!, i.GetProperty("code").GetString()!, i.GetProperty("code").GetString()!, i.GetProperty("kind").GetString()!,
        i.GetProperty("required").GetBoolean(), i.GetProperty("minPercent").ValueKind == JsonValueKind.Null ? null : i.GetProperty("minPercent").GetInt32(),
        i.GetProperty("onFail").GetString()!)).ToList();

    [Fact]
    public void Imei_check_digits_match_the_vectors()
    {
        foreach (var v in V.GetProperty("imei").EnumerateArray())
            Assert.True(DeviceMath.ImeiValid(v.GetProperty("value").GetString()) == v.GetProperty("valid").GetBoolean(), v.GetProperty("value").GetString());
    }

    [Fact]
    public void Scanned_codes_are_recognised_as_in_the_vectors()
    {
        foreach (var v in V.GetProperty("classify").EnumerateArray())
        {
            var c = DeviceMath.Classify(v.GetProperty("raw").GetString());
            Assert.Equal((v.GetProperty("kind").GetString(), v.GetProperty("value").GetString()), (c.Kind, c.Value));
        }
    }

    [Fact]
    public void Labour_suggested_prices_and_unit_prices_match_the_vectors()
    {
        foreach (var v in V.GetProperty("labour").EnumerateArray())
            Assert.Equal(Dec(v.GetProperty("amount")), DeviceMath.LabourCost(v.GetProperty("minutes").GetInt32(), Dec(v.GetProperty("rate"))!.Value));
        foreach (var v in V.GetProperty("suggested").EnumerateArray())
            Assert.Equal(Dec(v.GetProperty("price")), DeviceMath.SuggestedPrice(Dec(v.GetProperty("newPrice")), Dec(v.GetProperty("percent")), Dec(v.GetProperty("step"))!.Value));
        foreach (var v in V.GetProperty("unitListPrice").EnumerateArray())
            Assert.Equal(Dec(v.GetProperty("price")), DeviceMath.UnitListPrice(Dec(v.GetProperty("sellingPrice")), v.GetProperty("condition").GetString()!,
                Dec(v.GetProperty("newPrice")), Dec(v.GetProperty("percent")), Dec(v.GetProperty("step"))!.Value));
    }

    [Fact]
    public void Inspections_read_the_checklist_as_in_the_vectors()
    {
        var items = Checklist();
        foreach (var v in V.GetProperty("evaluate").EnumerateArray())
        {
            var answers = v.GetProperty("answers").EnumerateObject().ToDictionary(p => p.Name, p => (string?)p.Value.GetString());
            var r = DeviceMath.Evaluate(items, answers, v.GetProperty("current").GetString()!);
            var e = v.GetProperty("expect");
            Assert.Equal(e.GetProperty("complete").GetBoolean(), r.Complete);
            Assert.Equal(e.GetProperty("missing").EnumerateArray().Select(x => x.GetString()), r.Missing);
            Assert.Equal(e.GetProperty("failed").EnumerateArray().Select(x => x.GetString()), r.Failed);
            Assert.Equal(e.GetProperty("outcome").GetString(), r.SuggestedOutcome);
            Assert.Equal(e.GetProperty("condition").GetString(), r.SuggestedCondition);
            Assert.Equal(e.GetProperty("battery").ValueKind == JsonValueKind.Null ? null : e.GetProperty("battery").GetInt32(), r.BatteryHealth);
        }
    }
}

public sealed class DeviceRuleTests
{
    [Property(MaxTest = 300)]
    public Property Every_wrong_last_digit_of_a_valid_imei_is_caught(PositiveInt seed)
    {
        var rnd = new System.Random(seed.Get);
        var body = "35" + string.Concat(Enumerable.Range(0, 12).Select(_ => rnd.Next(10)));
        var good = Enumerable.Range(0, 10).Select(d => body + d).Single(DeviceMath.ImeiValid);   // exactly one check digit fits
        var wrong = Enumerable.Range(0, 10).Select(d => body + d).Where(s => s != good);
        return wrong.All(s => !DeviceMath.ImeiValid(s)).ToProperty();
    }

    [Property(MaxTest = 300)]
    public Property A_swap_of_two_neighbouring_different_digits_is_caught(PositiveInt seed)
    {
        var rnd = new System.Random(seed.Get);
        var body = "35" + string.Concat(Enumerable.Range(0, 12).Select(_ => rnd.Next(10)));
        var imei = Enumerable.Range(0, 10).Select(d => body + d).Single(DeviceMath.ImeiValid);
        var i = rnd.Next(0, 14);
        var pair = new string([imei[i], imei[i + 1]]);
        if (imei[i] == imei[i + 1] || pair is "09" or "90") return true.ToProperty();   // the one swap Luhn cannot see
        var swapped = imei[..i] + imei[i + 1] + imei[i] + imei[(i + 2)..];
        return (!DeviceMath.ImeiValid(swapped)).ToProperty();
    }

    [Fact]
    public void Inspection_answers_are_checked()
    {
        var items = new List<ChecklistItem> { new("screen", "Screen", "الشاشة", "pass_fail", true, null, "refurbish"), new("battery", "Battery", "البطارية", "percent", true, 80, "refurbish") };
        Assert.Equal(ErrorCodes.Validation, Assert.Throws<KiteException>(() => DeviceMath.Evaluate(items, new Dictionary<string, string?> { ["screen"] = "maybe", ["battery"] = "90" }, "used")).Code);
        Assert.Equal(ErrorCodes.Validation, Assert.Throws<KiteException>(() => DeviceMath.Evaluate(items, new Dictionary<string, string?> { ["screen"] = "pass", ["battery"] = "101" }, "used")).Code);
        Assert.Equal(ErrorCodes.Validation, Assert.Throws<KiteException>(() => DeviceMath.Evaluate(items, new Dictionary<string, string?> { ["screen"] = "pass", ["battery"] = "-5" }, "used")).Code);
    }

    [Fact]
    public void An_inspection_gives_only_the_conditions_its_decision_allows()
    {
        Assert.Equal(["brand_new", "used", "damaged"], DeviceMath.AllowedConditions("restock", "brand_new"));
        Assert.Equal(["used", "damaged"], DeviceMath.AllowedConditions("restock", "used"));          // a used phone never becomes new
        Assert.Equal(["refurbished", "used", "damaged"], DeviceMath.AllowedConditions("restock", "refurbished"));
        Assert.Equal(["used", "damaged"], DeviceMath.AllowedConditions("refurbish", "brand_new"));
        Assert.Equal(["for_parts"], DeviceMath.AllowedConditions("for_parts", "used"));
    }

    [Fact]
    public void Used_and_refurbished_devices_post_to_their_own_accounts()
    {
        Assert.Equal("device", PostingRuleTemplates.ClassOf("device", "brand_new"));
        Assert.Equal("device_used", PostingRuleTemplates.ClassOf("device", "refurbished"));
        Assert.Equal("device_used", PostingRuleTemplates.ClassOf("device", "damaged"));
        Assert.Equal("accessory", PostingRuleTemplates.ClassOf("accessory", "used"));   // only devices have a used inventory
        Assert.Equal("1410", PostingRuleTemplates.InventoryAccount("device_used"));

        // A refurbishment cost: the device's inventory grows by parts, labour and outside work; a reversal posts the other way.
        var rule = PostingRuleTemplates.All.Single(r => r.EventType == PostingRuleTemplates.RefurbCost).Definition;
        var facts = new PostingFacts().Add("cost_by_product_type", "device_used", 17.250m).Add("parts_by_product_type", "spare_part", 6.500m).Set("labour", 3.000m).Set("external", 7.750m);
        var j = PostingEngine.Build(rule, facts, new DateOnly(2026, 11, 16), "refurb_cost", Guid.NewGuid(), "refurb.cost", Currency.OMR)!;
        Assert.Equal([("1410", 17.250m, 0m), ("1430", 0m, 6.500m), ("2110", 0m, 7.750m), ("6210", 0m, 3.000m)],
            j.Lines.Select(l => (l.AccountCode, l.Debit, l.Credit)).OrderBy(x => x.AccountCode).ToArray());
        var back = PostingEngine.Build(rule, new PostingFacts().Add("cost_by_product_type", "device_used", -7.750m).Set("external", -7.750m),
            new DateOnly(2026, 11, 16), "refurb_cost", Guid.NewGuid(), "refurb.cost", Currency.OMR)!;
        Assert.Equal([("1410", 0m, 7.750m), ("2110", 7.750m, 0m)], back.Lines.Select(l => (l.AccountCode, l.Debit, l.Credit)).OrderBy(x => x.AccountCode).ToArray());
    }

    [Fact]
    public void Phase_4_rules_left_unchanged_by_a_business_are_recognised_for_the_upgrade()
    {
        // The upgrade compares stored rules with the version-1 defaults, so these must be exactly what Phase 4 stored.
        Assert.Equal(7, PostingRuleTemplates.Version1.Count);
        var sale = PostingRuleTemplates.Version1.Single(r => r.EventType == PostingRuleTemplates.Sale).Definition.ToJson();
        Assert.DoesNotContain("device_used", sale, StringComparison.Ordinal);
        Assert.Contains("\"device\":\"4100\"", sale, StringComparison.Ordinal);
        Assert.Contains("device_used", PostingRuleTemplates.All.Single(r => r.EventType == PostingRuleTemplates.Sale).Definition.ToJson(), StringComparison.Ordinal);
    }
}
