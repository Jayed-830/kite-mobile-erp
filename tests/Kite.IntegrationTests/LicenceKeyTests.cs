using System.Text.Json.Nodes;
using Kite.Host.Infrastructure;
using Kite.Identity;
using Xunit;

namespace Kite.IntegrationTests;

/// <summary>
/// The KITE MOBILE ERP public key from Nextdab Admin 5.14 is compiled in (ADR 17): a release build always uses it,
/// and only a Development build may use a test key from configuration.
/// </summary>
public sealed class LicenceKeyTests
{
    private const string NextdabKey = "baea0666fd8367c771021b8bf6e466cea8705175bf1a05ced86defc63f5fd4d9";
    private const string TestKey = "b9e919e294320a33f59b4a84eb4aca07d95bfcd36ab8b4fe15584b3b83be5645";

    [Fact]
    public void A_release_build_uses_the_Nextdab_key_even_if_a_test_key_is_configured()
    {
        Assert.Equal(NextdabKey, LicenceKeys.EmbeddedHex);
        var release = LicenceKeys.Create(new LicensingSettings { DevPublicKey = TestKey }, isDevelopment: false);
        Assert.True(release.IsConfigured);
        Assert.Equal(NextdabKey, release.Hex);
        Assert.Equal(NextdabKey, LicenceKeys.Create(new LicensingSettings(), isDevelopment: true).Hex);
        Assert.Equal(TestKey, LicenceKeys.Create(new LicensingSettings { DevPublicKey = TestKey }, isDevelopment: true).Hex);
    }

    [Fact]
    public void A_licence_signed_with_any_other_key_is_refused_by_the_Nextdab_key()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir!.FullName, "Kite.slnx"))) dir = dir.Parent;
        var v = JsonNode.Parse(File.ReadAllText(Path.Combine(dir.FullName, "tests", "vectors", "nextdab-admin-5.14-vector.json")))!.AsObject();
        var file = v["licence_file"]!.GetValue<string>();
        Assert.Equal(TestKey, v["public_key_hex"]!.GetValue<string>());
        Assert.Equal("subscription", LicenceFile.Read(file, Convert.FromHexString(TestKey), "1A2B-3C4D-5E6F-7A8B").Kind);   // the scratch key it was made with
        Assert.ThrowsAny<Exception>(() => LicenceFile.Read(file, Convert.FromHexString(LicenceKeys.EmbeddedHex), "1A2B-3C4D-5E6F-7A8B"));
    }
}
