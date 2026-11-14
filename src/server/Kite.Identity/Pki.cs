using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Kite.Identity;

/// <summary>
/// The business certificate authority (blueprint 18.1): created on the main server, it signs the certificate
/// of every branch server (mutual TLS for sync) and the main server's own TLS certificate, and publishes a
/// revocation list. ECDSA P-256 throughout.
/// </summary>
public static class BusinessCa
{
    public const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";
    public const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";
    private static readonly TimeSpan CaLifetime = TimeSpan.FromDays(365 * 20);
    private static readonly TimeSpan LeafLifetime = TimeSpan.FromDays(365 * 2);

    /// <summary>A new self-signed authority with its private key.</summary>
    public static X509Certificate2 CreateAuthority(Guid businessId, string businessName, DateTimeOffset now)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var subject = new X500DistinguishedNameBuilder();
        subject.AddCommonName("KITE Business CA");
        subject.AddOrganizationName(Clip(businessName, 60));
        subject.AddOrganizationalUnitName("kite-business:" + businessId.ToString("D"));
        var req = new CertificateRequest(subject.Build(), key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        using var cert = req.CreateSelfSigned(now.AddMinutes(-5), now.Add(CaLifetime));
        return Reload(cert);
    }

    /// <summary>The main server's TLS certificate for the sync port, naming every address branches use.</summary>
    public static X509Certificate2 IssueServerTls(X509Certificate2 authority, Guid serverId, IEnumerable<string> hosts, DateTimeOffset now)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var subject = new X500DistinguishedNameBuilder();
        subject.AddCommonName("kite-server:" + serverId.ToString("D"));
        var req = new CertificateRequest(subject.Build(), key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var h in hosts.Select(h => h.Trim()).Where(h => h.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (IPAddress.TryParse(h, out var ip)) san.AddIpAddress(ip); else san.AddDnsName(h);
        }
        san.AddUri(new Uri("urn:kite:server:" + serverId.ToString("D")));
        req.CertificateExtensions.Add(san.Build());
        AddLeafExtensions(req, authority, [ServerAuthOid]);
        using var issued = req.Create(authority.SubjectName, Signer(authority), now.AddMinutes(-5), Min(now.Add(LeafLifetime), authority.NotAfter), NewSerial());
        using var withKey = issued.CopyWithPrivateKey(key);
        return Reload(withKey);
    }

    /// <summary>Branch side: a new key pair and the certificate request that goes to head office with the token.</summary>
    public static (string CsrPem, byte[] Pkcs8PrivateKey) CreateBranchRequest(string serverName)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var subject = new X500DistinguishedNameBuilder();
        subject.AddCommonName(Clip(serverName, 60));
        var req = new CertificateRequest(subject.Build(), key, HashAlgorithmName.SHA256);
        return (req.CreateSigningRequestPem(), key.ExportPkcs8PrivateKey());
    }

    /// <summary>
    /// Head office side: checks the request's own signature (proof the branch holds the key), then issues a
    /// certificate with OUR subject and extensions - nothing from the request except its public key is used.
    /// </summary>
    public static X509Certificate2 SignBranchRequest(X509Certificate2 authority, string csrPem, Guid businessId, Guid branchId, Guid serverId, DateTimeOffset now)
    {
        CertificateRequest incoming;
        try { incoming = CertificateRequest.LoadSigningRequestPem(csrPem, HashAlgorithmName.SHA256); }
        catch (CryptographicException e) { throw new CryptographicException("The certificate request is not valid: " + e.Message, e); }
        if (incoming.PublicKey.Oid.Value != "1.2.840.10045.2.1")
            throw new CryptographicException("The certificate request must use an EC P-256 key.");
        var subject = new X500DistinguishedNameBuilder();
        subject.AddCommonName("kite-server:" + serverId.ToString("D"));
        subject.AddOrganizationalUnitName("kite-branch:" + branchId.ToString("D"));
        subject.AddOrganizationName("kite-business:" + businessId.ToString("D"));
        var req = new CertificateRequest(subject.Build(), incoming.PublicKey, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddUri(new Uri("urn:kite:server:" + serverId.ToString("D")));
        san.AddUri(new Uri("urn:kite:branch:" + branchId.ToString("D")));
        req.CertificateExtensions.Add(san.Build());
        AddLeafExtensions(req, authority, [ClientAuthOid, ServerAuthOid]);
        using var issued = req.Create(authority.SubjectName, Signer(authority), now.AddMinutes(-5), Min(now.Add(LeafLifetime), authority.NotAfter), NewSerial());
        return X509CertificateLoader.LoadCertificate(issued.RawData);
    }

    /// <summary>Branch side: the issued certificate joined with the key that never left the branch.</summary>
    public static X509Certificate2 WithPrivateKey(X509Certificate2 cert, byte[] pkcs8PrivateKey)
    {
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(pkcs8PrivateKey, out _);
        using var joined = cert.CopyWithPrivateKey(key);
        return Reload(joined);
    }

    /// <summary>A signed revocation list. <paramref name="revoked"/> = (serial hex, revoked at).</summary>
    public static string BuildCrlPem(X509Certificate2 authority, long number, IEnumerable<(string SerialHex, DateTimeOffset RevokedAt)> revoked, DateTimeOffset now)
    {
        var builder = new CertificateRevocationListBuilder();
        foreach (var (serial, at) in revoked)
            builder.AddEntry(Convert.FromHexString(serial), at);
        var der = builder.Build(authority, new BigInteger(number), now.AddDays(7), HashAlgorithmName.SHA256, null, now);
        return PemEncoding.WriteString("X509 CRL", der);
    }

    /// <summary>
    /// Checks a presented certificate chains to this business's authority, is in date and allowed for the
    /// purpose. Revocation is checked by the caller against its own records (the main server is the authority).
    /// </summary>
    public static bool Chains(X509Certificate2 cert, X509Certificate2 authority, DateTimeOffset now, string purposeOid, out string error)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(authority);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = now.UtcDateTime;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(purposeOid));
        if (!chain.Build(cert))
        {
            error = string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation.Trim()).Where(s => s.Length > 0).DefaultIfEmpty("not trusted"));
            return false;
        }
        if (!string.Equals(chain.ChainElements[^1].Certificate.Thumbprint, authority.Thumbprint, StringComparison.OrdinalIgnoreCase))
        {
            error = "issued by another authority";
            return false;
        }
        error = "";
        return true;
    }

    public static string Fingerprint(X509Certificate2 cert) => cert.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant();

    /// <summary>
    /// The code shown on both screens during branch registration (blueprint 18.1): five groups of four digits
    /// taken from the authority's SHA-256 fingerprint, easy to compare aloud.
    /// </summary>
    public static string FingerprintCode(X509Certificate2 cert)
    {
        var h = SHA256.HashData(cert.RawData);
        return string.Join("-", Enumerable.Range(0, 5).Select(i => (((h[2 * i] << 8) | h[2 * i + 1]) % 10000).ToString("D4", System.Globalization.CultureInfo.InvariantCulture)));
    }

    public static string SerialHex(X509Certificate2 cert) => cert.SerialNumber.ToLowerInvariant();

    public static string ExportPrivateKey(X509Certificate2 certWithKey) =>
        Convert.ToBase64String((certWithKey.GetECDsaPrivateKey() ?? throw new CryptographicException("No private key.")).ExportPkcs8PrivateKey());

    public static X509Certificate2 FromPem(string certPem) => X509Certificate2.CreateFromPem(certPem);

    public static X509Certificate2 FromPemAndKey(string certPem, byte[] pkcs8PrivateKey) => WithPrivateKey(FromPem(certPem), pkcs8PrivateKey);

    /// <summary>The owner's offline recovery copy: certificate plus the private key encrypted with a passphrase.</summary>
    public static string ExportRecoveryCopy(X509Certificate2 authority, string passphrase)
    {
        using var key = authority.GetECDsaPrivateKey() ?? throw new CryptographicException("No private key.");
        var pbe = new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000);
        return authority.ExportCertificatePem() + "\n" + key.ExportEncryptedPkcs8PrivateKeyPem(passphrase, pbe) + "\n";
    }

    private static void AddLeafExtensions(CertificateRequest req, X509Certificate2 authority, string[] ekus)
    {
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyAgreement, true));
        var oids = new OidCollection();
        foreach (var e in ekus) oids.Add(new Oid(e));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(oids, false));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(authority, true, false));
    }

    private static X509SignatureGenerator Signer(X509Certificate2 authority) =>
        X509SignatureGenerator.CreateForECDsa(authority.GetECDsaPrivateKey() ?? throw new CryptographicException("The authority's private key is not available."));

    private static byte[] NewSerial()
    {
        var s = RandomNumberGenerator.GetBytes(16);
        s[0] &= 0x7F; // positive
        s[0] |= 0x10; // never a leading zero byte, so the hex form is always 32 characters
        return s;
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTime b) => a < new DateTimeOffset(b.ToUniversalTime()) ? a : new DateTimeOffset(b.ToUniversalTime());

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];

    // Ephemeral keys do not work with SslStream on Windows; a PKCS#12 round trip gives a usable key handle.
    private static X509Certificate2 Reload(X509Certificate2 cert) =>
        X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
}
