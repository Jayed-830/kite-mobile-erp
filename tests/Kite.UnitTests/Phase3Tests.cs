using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kite.Identity;
using Xunit;

namespace Kite.UnitTests;

public class NextdabSigningTests
{
    private static JsonObject Vector() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot.Path, "tests", "vectors", "nextdab-signing-vector.json")))!.AsObject();

    [Fact]
    public void Canonical_json_is_byte_for_byte_what_Nextdab_Admin_signs()
    {
        var v = Vector();
        var file = NextdabCanonicalJson.ParseObject(v["licence_file"]!.GetValue<string>());
        Assert.Equal(v["canonical_licence"]!.GetValue<string>(), NextdabCanonicalJson.Write(file["licence"]));
    }

    [Fact]
    public void A_licence_signed_by_Nextdab_Admin_code_verifies_and_reads()
    {
        var v = Vector();
        var pub = Convert.FromHexString(v["public_key_hex"]!.GetValue<string>());
        var terms = LicenceFile.Read(v["licence_file"]!.GetValue<string>(), pub, "1A2B-3C4D-5E6F-7A8B");
        Assert.Equal("kme-1763164800000-a1b2c3", terms.Id);
        Assert.Equal("subscription", terms.Kind);
        Assert.Equal(new DateOnly(2027, 11, 14), terms.ValidUntil);
        Assert.Equal(3, terms.BranchesAllowed);
        Assert.Equal(10, terms.TerminalsAllowed);
        Assert.Equal(["core", "iam", "sales", "repair"], terms.Modules);
        Assert.StartsWith("شركة النور", terms.Customer);
    }

    [Fact]
    public void A_changed_licence_or_the_wrong_installation_or_key_is_refused()
    {
        var v = Vector();
        var pub = Convert.FromHexString(v["public_key_hex"]!.GetValue<string>());
        var text = v["licence_file"]!.GetValue<string>();
        Assert.Equal("LIC-011", Assert.Throws<LicenceException>(() => LicenceFile.Read(text.Replace("\"terminals\": 10", "\"terminals\": 99"), pub, "1A2B-3C4D-5E6F-7A8B")).Code);
        Assert.Equal("LIC-013", Assert.Throws<LicenceException>(() => LicenceFile.Read(text, pub, "FFFF-FFFF-FFFF-FFFF")).Code);
        var otherKey = NextdabSignature.PublicKeyOf(RandomNumberGenerator.GetBytes(32));
        Assert.Equal("LIC-011", Assert.Throws<LicenceException>(() => LicenceFile.Read(text, otherKey, "1A2B-3C4D-5E6F-7A8B")).Code);
        Assert.Equal("LIC-010", Assert.Throws<LicenceException>(() => LicenceFile.Read("not json", pub, "1A2B-3C4D-5E6F-7A8B")).Code);
        Assert.Equal("LIC-011", Assert.Throws<LicenceException>(() => LicenceFile.Read(text, new byte[32], "1A2B-3C4D-5E6F-7A8B")).Code);
    }

    [Fact]
    public void A_signed_command_from_Nextdab_Admin_code_verifies_with_the_protocol_rules()
    {
        var v = Vector();
        var pub = Convert.FromHexString(v["public_key_hex"]!.GetValue<string>());
        var raw = NextdabCanonicalJson.ParseObject(v["command"]!.ToJsonString());
        var now = DateTimeOffset.Parse("2026-11-15T01:00:00Z");
        var cmd = NextdabCommand.Verify(raw, pub, "kite-mobile-erp", "1A2B-3C4D-5E6F-7A8B", now, out _);
        Assert.NotNull(cmd);
        Assert.Equal("suspend", cmd!.Type);
        Assert.Equal(1763164800000, cmd.Seq);
        Assert.Equal("Switched off", cmd.Data["message"]!.GetValue<string>());

        Assert.Null(NextdabCommand.Verify(raw, pub, "kite-rems", "1A2B-3C4D-5E6F-7A8B", now, out var why1));
        Assert.Equal("another product", why1);
        Assert.Null(NextdabCommand.Verify(raw, pub, "kite-mobile-erp", "0000-0000-0000-0001", now, out var why2));
        Assert.Equal("not for this installation", why2);
        var tampered = (JsonObject)raw.DeepClone();
        tampered["seq"] = 1763164800001;
        Assert.Null(NextdabCommand.Verify(tampered, pub, "kite-mobile-erp", "1A2B-3C4D-5E6F-7A8B", now, out var why3));
        Assert.Equal("bad signature", why3);
    }

    [Fact]
    public void Broadcast_commands_are_limited_to_messages_updates_and_checkins_and_expired_ones_are_ignored()
    {
        var seed = RandomNumberGenerator.GetBytes(32);
        var pub = NextdabSignature.PublicKeyOf(seed);
        JsonObject Make(string type, string mid, string? expires = null)
        {
            var c = NextdabCanonicalJson.ParseObject($$"""{"v":1,"id":"x-1","product":"kite-mobile-erp","mid":"{{mid}}","type":"{{type}}","data":{},"seq":5,"issued":"2026-11-15T00:00:00Z"{{(expires is null ? "" : $",\"expires\":\"{expires}\"")}}}""");
            c["sig"] = NextdabSignature.Sign(seed, c);
            return c;
        }
        var now = DateTimeOffset.Parse("2026-11-15T01:00:00Z");
        Assert.NotNull(NextdabCommand.Verify(Make("message", "*"), pub, "kite-mobile-erp", "1A2B-3C4D-5E6F-7A8B", now, out _));
        Assert.Null(NextdabCommand.Verify(Make("suspend", "*"), pub, "kite-mobile-erp", "1A2B-3C4D-5E6F-7A8B", now, out _));
        Assert.Null(NextdabCommand.Verify(Make("message", "1A2B-3C4D-5E6F-7A8B", "2026-11-14T00:00:00Z"), pub, "kite-mobile-erp", "1A2B-3C4D-5E6F-7A8B", now, out var why));
        Assert.Equal("expired", why);
    }

    [Fact]
    public void BouncyCastle_Ed25519_passes_the_RFC_8032_test_vector()
    {
        var pk = Convert.FromHexString("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");
        var sig = Convert.FromHexString("e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b");
        Assert.True(Org.BouncyCastle.Math.EC.Rfc8032.Ed25519.Verify(sig, 0, pk, 0, [], 0, 0));
        Assert.Equal(pk, NextdabSignature.PublicKeyOf(Convert.FromHexString("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60")));
    }

    [Fact]
    public void Machine_ids_have_the_Nextdab_shape()
    {
        Assert.Matches("^[0-9A-F]{4}(-[0-9A-F]{4}){3}$", MachineId.New());
        Assert.Equal("1A2B-3C4D-5E6F-7A8B", MachineId.Normalise("1a2b3c4d5e6f7a8b"));
        Assert.Null(MachineId.Normalise("1A2B-3C4D"));
    }
}

public class LicenceStatusTests
{
    private static LicenceTerms Terms(string until, int grace = 30) =>
        new("kme-1", "1A2B-3C4D-5E6F-7A8B", "subscription", "Test", DateOnly.Parse(until), grace, 3, 10, ["core", "sales"], DateTimeOffset.UtcNow);

    [Theory]
    [InlineData("2027-01-31", "2026-11-15", LicenceState.Active, true)]
    [InlineData("2026-11-29", "2026-11-15", LicenceState.Expiring, true)]   // 14 days left: warnings start
    [InlineData("2026-11-01", "2026-11-15", LicenceState.Grace, true)]      // ended, within 30 days of grace
    [InlineData("2026-10-01", "2026-11-15", LicenceState.Expired, false)]   // ended more than 30 days ago
    public void Expiry_warning_grace_and_expiry(string until, string today, LicenceState state, bool canTransact)
    {
        var s = LicenceStatus.Evaluate(Terms(until), false, null, null, DateOnly.Parse(today));
        Assert.Equal(state, s.State);
        Assert.Equal(canTransact, s.CanTransact);
        Assert.Equal(3, s.BranchesAllowed);
    }

    [Fact]
    public void Suspension_wins_and_no_licence_allows_setup_but_no_trading()
    {
        var suspended = LicenceStatus.Evaluate(Terms("2030-01-01"), true, "off", "متوقف", DateOnly.Parse("2026-11-15"));
        Assert.Equal(LicenceState.Suspended, suspended.State);
        Assert.False(suspended.CanTransact);
        Assert.Equal("off", suspended.SuspendMessage);
        var none = LicenceStatus.Evaluate(null, false, null, null, DateOnly.Parse("2026-11-15"));
        Assert.Equal(LicenceState.None, none.State);
        Assert.False(none.CanTransact);
        Assert.Equal(1, none.BranchesAllowed);
        Assert.Equal(1, none.TerminalsAllowed);
        Assert.True(none.ModuleLicensed("repair"));
        Assert.False(LicenceStatus.Evaluate(Terms("2030-01-01"), false, null, null, DateOnly.Parse("2026-11-15")).ModuleLicensed("repair"));
    }
}

public class BusinessCaTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void A_branch_certificate_is_issued_from_a_request_and_chains_to_the_business_ca_only()
    {
        using var ca = BusinessCa.CreateAuthority(Guid.NewGuid(), "Al Noor Mobiles LLC", Now);
        Assert.True(ca.HasPrivateKey);
        var (csr, key) = BusinessCa.CreateBranchRequest("SOHAR-PC");
        var branchId = Guid.NewGuid();
        var serverId = Guid.NewGuid();
        using var issued = BusinessCa.SignBranchRequest(ca, csr, Guid.NewGuid(), branchId, serverId, Now);
        Assert.Contains("kite-branch:" + branchId, issued.Subject);
        Assert.Equal("kite-server:" + serverId, issued.GetNameInfo(X509NameType.SimpleName, false));
        Assert.True(BusinessCa.Chains(issued, ca, Now, BusinessCa.ClientAuthOid, out var err), err);
        using var withKey = BusinessCa.WithPrivateKey(issued, key);
        Assert.True(withKey.HasPrivateKey);

        using var other = BusinessCa.CreateAuthority(Guid.NewGuid(), "Someone else", Now);
        Assert.False(BusinessCa.Chains(issued, other, Now, BusinessCa.ClientAuthOid, out _));
        Assert.False(BusinessCa.Chains(issued, ca, Now.AddYears(3), BusinessCa.ClientAuthOid, out _)); // expired after 2 years
    }

    [Fact]
    public void Nothing_from_the_request_is_copied_except_its_public_key_and_a_forged_request_is_refused()
    {
        using var ca = BusinessCa.CreateAuthority(Guid.NewGuid(), "Biz", Now);
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=evil", ecdsa, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true)); // asks to be a CA
        using var issued = BusinessCa.SignBranchRequest(ca, req.CreateSigningRequestPem(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now);
        Assert.False(issued.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
        Assert.DoesNotContain("evil", issued.Subject);

        var pem = req.CreateSigningRequestPem();
        var body = pem.Split('\n').Where(l => !l.StartsWith("-----", StringComparison.Ordinal)).Aggregate("", (a, b) => a + b.Trim());
        var der = Convert.FromBase64String(body);
        der[^5] ^= 0x01; // break the signature
        var broken = PemEncodingString("CERTIFICATE REQUEST", der);
        Assert.Throws<CryptographicException>(() => BusinessCa.SignBranchRequest(ca, broken, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now));
    }

    [Fact]
    public void The_server_tls_certificate_names_its_hosts_and_the_crl_lists_revoked_serials()
    {
        using var ca = BusinessCa.CreateAuthority(Guid.NewGuid(), "Biz", Now);
        using var tls = BusinessCa.IssueServerTls(ca, Guid.NewGuid(), ["localhost", "192.168.1.10", "hq.example.om"], Now);
        Assert.True(tls.HasPrivateKey);
        Assert.True(BusinessCa.Chains(tls, ca, Now, BusinessCa.ServerAuthOid, out _));
        var san = tls.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Assert.Contains("hq.example.om", san.EnumerateDnsNames());
        Assert.Contains("192.168.1.10", san.EnumerateIPAddresses().Select(i => i.ToString()));

        var (csr, _) = BusinessCa.CreateBranchRequest("x");
        using var leaf = BusinessCa.SignBranchRequest(ca, csr, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now);
        var pem = BusinessCa.BuildCrlPem(ca, 7, [(BusinessCa.SerialHex(leaf), Now)], Now);
        var crl = CertificateRevocationListBuilder.Load(PemDer(pem), out BigInteger number);
        Assert.Equal(7, (long)number);
        Assert.Matches("^\\d{4}(-\\d{4}){4}$", BusinessCa.FingerprintCode(ca));
        Assert.Equal(32, BusinessCa.SerialHex(leaf).Length);
        Assert.NotNull(crl);
    }

    [Fact]
    public void The_recovery_copy_is_encrypted_with_the_passphrase()
    {
        using var ca = BusinessCa.CreateAuthority(Guid.NewGuid(), "Biz", Now);
        var copy = BusinessCa.ExportRecoveryCopy(ca, "correct horse battery staple");
        Assert.Contains("BEGIN ENCRYPTED PRIVATE KEY", copy);
        Assert.DoesNotContain("BEGIN PRIVATE KEY", copy);
        using var key = ECDsa.Create();
        key.ImportFromEncryptedPem(copy, "correct horse battery staple");
        Assert.Throws<CryptographicException>(() => { using var k = ECDsa.Create(); k.ImportFromEncryptedPem(copy, "wrong"); });
    }

    private static byte[] PemDer(string pem) => Convert.FromBase64String(string.Concat(pem.Split('\n').Where(l => !l.StartsWith("-----", StringComparison.Ordinal)).Select(l => l.Trim())));
    private static string PemEncodingString(string label, byte[] der) => new(PemEncoding.Write(label, der));
}

public class BackupCryptoTests
{
    [Fact]
    public async Task Large_files_round_trip_across_chunks()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var data = RandomNumberGenerator.GetBytes(BackupCrypto.ChunkSize * 2 + 12345);
        var enc = new MemoryStream();
        await BackupCrypto.EncryptAsync(new MemoryStream(data), enc, key);
        Assert.True(enc.Length > data.Length);
        var dec = new MemoryStream();
        await BackupCrypto.DecryptAsync(new MemoryStream(enc.ToArray()), dec, key);
        Assert.Equal(data, dec.ToArray());
    }

    [Fact]
    public async Task Empty_and_exact_chunk_sizes_round_trip()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        foreach (var size in new[] { 0, 1, BackupCrypto.ChunkSize })
        {
            var data = RandomNumberGenerator.GetBytes(size);
            var enc = new MemoryStream();
            await BackupCrypto.EncryptAsync(new MemoryStream(data), enc, key);
            var dec = new MemoryStream();
            await BackupCrypto.DecryptAsync(new MemoryStream(enc.ToArray()), dec, key);
            Assert.Equal(data, dec.ToArray());
        }
    }

    [Fact]
    public async Task Changed_truncated_or_wrongly_keyed_files_are_refused()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var data = RandomNumberGenerator.GetBytes(BackupCrypto.ChunkSize + 100);
        var enc = new MemoryStream();
        await BackupCrypto.EncryptAsync(new MemoryStream(data), enc, key);
        var bytes = enc.ToArray();

        var flipped = (byte[])bytes.Clone();
        flipped[200] ^= 0x40;
        await Assert.ThrowsAsync<CryptographicException>(() => BackupCrypto.DecryptAsync(new MemoryStream(flipped), new MemoryStream(), key));
        // Cut after the first chunk: the last-chunk flag is missing.
        var firstChunkEnd = 29 + 4 + BackupCrypto.ChunkSize + 16;
        await Assert.ThrowsAsync<CryptographicException>(() => BackupCrypto.DecryptAsync(new MemoryStream(bytes[..firstChunkEnd]), new MemoryStream(), key));
        await Assert.ThrowsAsync<CryptographicException>(() => BackupCrypto.DecryptAsync(new MemoryStream(bytes), new MemoryStream(), RandomNumberGenerator.GetBytes(32)));
        await Assert.ThrowsAsync<CryptographicException>(() => BackupCrypto.DecryptAsync(new MemoryStream([.. bytes, 0]), new MemoryStream(), key));
    }

    [Fact]
    public void The_printed_recovery_key_round_trips_and_ignores_spacing()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var printed = BackupCrypto.FormatRecoveryKey(key);
        Assert.Matches("^[A-Z2-7]{4}(-[A-Z2-7]{1,4}){12}$", printed);
        Assert.Equal(key, BackupCrypto.ParseRecoveryKey(printed.ToLowerInvariant().Replace("-", " ")));
        Assert.Throws<CryptographicException>(() => BackupCrypto.ParseRecoveryKey("ABCD-EFGH"));
    }
}

public class KeyProtectionTests
{
    [Fact]
    public void Key_files_are_created_reloaded_and_a_Phase_2_raw_key_is_upgraded()
    {
        var dir = Directory.CreateTempSubdirectory("kite-keys-").FullName;
        var protector = KeyProtectors.ForThisMachine();
        var path = Path.Combine(dir, "a.key");
        var k1 = ProtectedKeyFile.LoadOrCreate(path, protector, "test");
        Assert.Equal(k1, ProtectedKeyFile.LoadOrCreate(path, protector, "test"));
        var expectedHeader = $"KITEKEY1|{protector.Kind}|test\n";
        Assert.Equal(expectedHeader, System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, expectedHeader.Length));
        Assert.Throws<CryptographicException>(() => ProtectedKeyFile.TryLoad(path, protector, "other"));

        var old = Path.Combine(dir, "secrets.key");
        var raw = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(old, raw);
        Assert.Equal(raw, ProtectedKeyFile.LoadOrCreate(old, protector, "secrets"));
        Assert.NotEqual(raw, File.ReadAllBytes(old)); // rewritten with the header (and DPAPI on Windows)
        Assert.Equal(raw, ProtectedKeyFile.LoadOrCreate(old, protector, "secrets"));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void On_Windows_keys_are_wrapped_with_DPAPI_machine_scope()
    {
        if (!OperatingSystem.IsWindows()) return;
        var p = KeyProtectors.ForThisMachine();
        Assert.Equal("dpapi-machine", p.Kind);
        var secret = RandomNumberGenerator.GetBytes(32);
        var wrapped = p.Protect(secret, "test");
        Assert.NotEqual(secret, wrapped);
        Assert.Equal(secret, p.Unprotect(wrapped, "test"));
        Assert.ThrowsAny<CryptographicException>(() => p.Unprotect(wrapped, "another purpose"));
    }
}
