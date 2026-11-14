using System.Text.RegularExpressions;
using FsCheck;
using FsCheck.Xunit;
using Kite.Platform;
using Xunit;

namespace Kite.UnitTests;

public class IdsTests
{
    [Fact]
    public void New_ids_are_uuid_version_7_and_time_ordered()
    {
        var a = Ids.New();
        Thread.Sleep(2);
        var b = Ids.New();
        Assert.Equal(7, Ids.VersionOf(a));
        Assert.True(string.CompareOrdinal(a.ToString(), b.ToString()) < 0, "later UUIDv7 sorts after earlier one");
    }
}

public class HybridLogicalClockTests
{
    [Fact]
    public void Local_events_are_strictly_increasing_even_when_the_wall_clock_stands_still()
    {
        var clock = new FixedClock(DateTimeOffset.Parse("2026-11-14T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var hlc = new HybridLogicalClock(clock, "srv1");
        var t1 = hlc.Now();
        var t2 = hlc.Now();
        Assert.True(t2 > t1);
        Assert.Equal(t1.WallMs, t2.WallMs);
        Assert.Equal(t1.Counter + 1, t2.Counter);
    }

    [Fact]
    public void A_wall_clock_moving_backwards_never_produces_an_earlier_timestamp()
    {
        var clock = new FixedClock(DateTimeOffset.Parse("2026-11-14T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var hlc = new HybridLogicalClock(clock, "srv1");
        var t1 = hlc.Now();
        clock.Advance(TimeSpan.FromMinutes(-5));
        Assert.True(hlc.Now() > t1);
    }

    [Fact]
    public void Receiving_a_remote_timestamp_from_the_future_moves_the_local_clock_past_it()
    {
        var clock = new FixedClock(DateTimeOffset.Parse("2026-11-14T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var hlc = new HybridLogicalClock(clock, "srvA");
        var remote = new HlcTimestamp(clock.UtcNow.ToUnixTimeMilliseconds() + 60_000, 4, "srvB");
        var received = hlc.Receive(remote);
        Assert.True(received > remote);
        Assert.True(hlc.Now() > received);
    }

    [Fact]
    public void Text_form_round_trips_and_sorts_like_the_timestamp()
    {
        var a = new HlcTimestamp(1_763_114_400_000, 3, "srv1");
        var b = new HlcTimestamp(1_763_114_400_000, 12, "srv1");
        Assert.Equal(a, HlcTimestamp.Parse(a.ToString()));
        Assert.True(string.CompareOrdinal(a.ToString(), b.ToString()) < 0);
    }
}

public class MoneyTests
{
    [Theory]
    [InlineData("1.0004", "1.000")]
    [InlineData("1.0005", "1.001")]
    [InlineData("-1.0005", "-1.001")]
    [InlineData("12.5", "12.500")]
    public void Omr_amounts_are_rounded_to_three_decimals(string input, string expected)
    {
        var m = Money.Omr(decimal.Parse(input, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(expected, m.ToInvariantString());
    }

    [Fact]
    public void Half_even_rounding_is_available_when_configured()
    {
        Assert.Equal(0.002m, Currency.OMR.Round(0.0025m, RoundingMode.HalfEven));
        Assert.Equal(0.003m, Currency.OMR.Round(0.0025m, RoundingMode.HalfAwayFromZero));
    }

    [Fact]
    public void Mixing_currencies_is_refused()
    {
        var usd = new Currency("USD", 2);
        Assert.Throws<InvalidOperationException>(() => Money.Omr(1) + new Money(1, usd));
    }

    [Fact]
    public void Parse_rejects_more_decimals_than_the_currency_allows()
    {
        Assert.Equal(12.5m, Money.Parse("12.500", Currency.OMR).Amount);
        Assert.Throws<FormatException>(() => Money.Parse("12.5001", Currency.OMR));
        Assert.Throws<FormatException>(() => Money.Parse("12,500", Currency.OMR));
    }

    [Fact]
    public void Allocating_ten_rial_three_ways_loses_no_baisa()
    {
        var parts = Money.Omr(10m).Allocate([1m, 1m, 1m]);
        Assert.Equal(["3.334", "3.333", "3.333"], parts.Select(p => p.ToInvariantString()));
    }

    [Fact]
    public void Allocating_by_commission_shares_matches_the_blueprint_example()
    {
        // Blueprint 15.2: 4.800 OMR shared 60/40 -> 2.880 and 1.920.
        var parts = Money.Omr(4.8m).Allocate([60m, 40m]);
        Assert.Equal(["2.880", "1.920"], parts.Select(p => p.ToInvariantString()));
    }

    [Property(MaxTest = 300)]
    public Property Allocation_parts_always_add_up_to_the_original(int baisa, PositiveInt w1, PositiveInt w2, NonNegativeInt w3)
    {
        var amount = Money.Omr(baisa / 1000m);
        var parts = amount.Allocate([w1.Get, w2.Get, w3.Get]);
        var sum = parts.Aggregate(Money.Zero(Currency.OMR), (acc, p) => acc + p);
        return (sum == amount && parts.All(p => decimal.Round(p.Amount, 3) == p.Amount)).ToProperty();
    }
}

public class ModuleCatalogTests
{
    [Fact]
    public void Default_module_states_satisfy_every_dependency()
    {
        var states = ModuleCatalog.Apply(ModuleCatalog.Defaults(), new Dictionary<string, bool>());
        Assert.True(states["sales"]);
        Assert.False(states["tradein"]);
    }

    [Fact]
    public void Enabling_trade_in_without_refurbishment_is_refused()
    {
        var ex = Assert.Throws<KiteException>(() => ModuleCatalog.Apply(ModuleCatalog.Defaults(), new Dictionary<string, bool> { ["tradein"] = true }));
        Assert.Equal(ErrorCodes.ModuleDependency, ex.Code);
    }

    [Fact]
    public void Enabling_trade_in_together_with_refurbishment_is_allowed()
    {
        var states = ModuleCatalog.Apply(ModuleCatalog.Defaults(), new Dictionary<string, bool> { ["tradein"] = true, ["refurb"] = true });
        Assert.True(states["tradein"]);
    }

    [Fact]
    public void Always_on_modules_cannot_be_switched_off()
    {
        var ex = Assert.Throws<KiteException>(() => ModuleCatalog.Apply(ModuleCatalog.Defaults(), new Dictionary<string, bool> { ["sales"] = false }));
        Assert.Equal(ErrorCodes.ModuleAlwaysOn, ex.Code);
    }

    [Fact]
    public void A_module_cannot_be_switched_off_while_another_enabled_module_needs_it()
    {
        var on = ModuleCatalog.Apply(ModuleCatalog.Defaults(), new Dictionary<string, bool> { ["tradein"] = true, ["refurb"] = true });
        var ex = Assert.Throws<KiteException>(() => ModuleCatalog.Apply(on, new Dictionary<string, bool> { ["device"] = false }));
        Assert.Equal(ErrorCodes.ModuleDependency, ex.Code);
    }

    [Fact]
    public void Every_dependency_names_a_real_module()
    {
        foreach (var m in ModuleCatalog.All)
            foreach (var d in m.DependsOn)
                Assert.True(ModuleCatalog.Exists(d), $"{m.Code} depends on unknown module {d}");
    }
}

public partial class PermissionCatalogTests
{
    [GeneratedRegex(@"\('([A-Z_]+(?:\.[A-Z0-9_]+)+)',")]
    private static partial Regex SeedCode();

    [Fact]
    public void Code_permissions_match_the_database_seed_exactly()
    {
        // Every migration that seeds permissions (V0009, V0011, ...): the INSERT INTO iam.permission statements only.
        var seeded = Directory.GetFiles(Path.Combine(RepoRoot.Path, "db", "migrations"), "V*.sql")
            .Select(File.ReadAllText)
            .SelectMany(sql => Regex.Matches(sql, @"INSERT INTO iam\.permission[^;]*;", RegexOptions.Singleline).Select(m => m.Value))
            .SelectMany(stmt => SeedCode().Matches(stmt).Select(m => m.Groups[1].Value))
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(seeded, Permissions.All);
    }

    [Fact]
    public void Role_templates_use_only_known_permissions_and_the_owner_has_all()
    {
        foreach (var role in RoleTemplates.All)
            Assert.All(role.Permissions, p => Assert.Contains(p, Permissions.All));
        Assert.Equal(Permissions.All, RoleTemplates.All.Single(r => r.Code == RoleTemplates.OwnerCode).Permissions.Order(StringComparer.Ordinal));
    }
}

public partial class EngineeringRuleTests
{
    [GeneratedRegex(@"\b(float|double)\b")]
    private static partial Regex FloatingPoint();

    /// <summary>Blueprint 1.1 decision 7 and 26.3 rule 1: no floating point in server code.</summary>
    [Fact]
    public void Server_code_contains_no_floating_point_types()
    {
        var offenders = Directory.EnumerateFiles(Path.Combine(RepoRoot.Path, "src", "server"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (f, i, line)))
            .Where(x => !x.line.TrimStart().StartsWith("//", StringComparison.Ordinal) && FloatingPoint().IsMatch(x.line))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}")
            .ToArray();
        Assert.True(offenders.Length == 0, "Floating point found: " + string.Join(", ", offenders));
    }
}

internal static class RepoRoot
{
    public static readonly string Path = Find();

    private static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "global.json"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root (global.json) not found.");
    }
}
