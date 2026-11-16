using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kite.Host.Infrastructure;
using Kite.Identity;
using Kite.Persistence;
using Kite.Platform;

namespace Kite.Host.Features;

public static class AppInfo
{
    public static readonly string Version =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static async Task<int> SchemaVersionAsync(KiteDb db, CancellationToken ct) =>
        await db.InTransactionAsync(AccessScope.System, ActorContext.System,
            t => t.ExecuteScalarAsync<int?>("SELECT max(version) FROM meta.schema_info"), ct) ?? 0;
}

// ------------------------------------------------------------------------------------------------ wire formats
public sealed record RegisterBranchRequest(string Token, string CsrPem, string ServerName, string AppVersion, int SchemaVersion);
public sealed record RegisterBranchResponse(Guid ServerId, Guid BranchId, Guid BusinessId, Guid MainServerId, string CertPem, string CaPem, JsonObject Snapshot);
public sealed record HeartbeatRequest(string AppVersion, int SchemaVersion, DateTimeOffset Time, long OutboxLastSeq, long OutboxPending,
    DateTimeOffset? LastBackupOkAt, string? BackupStatus, string? LicenceId, long CrlNumber, long SuspendSeq, long ModulesSeq,
    long ImeiInstructionSeq = 0, long BlockedSeq = 0);
public sealed record HeartbeatResponse(DateTimeOffset ServerTime, string? LicenceFile, JsonArray Control, long CrlNumber, string? CrlPem,
    List<CentralInstructionDto>? ImeiInstructions = null, List<BlockedDto>? Blocked = null);
public sealed record CaInfo(string CaPem, string FingerprintCode, string Fingerprint, string BusinessName, Guid MainServerId);

public sealed record CreateBranchRequest(string Code, string NameEn, string NameAr, string? AddressEn, string? AddressAr, string? Phone);
public sealed record UpdateBranchRequest(string NameEn, string NameAr, string? AddressEn, string? AddressAr, string? Phone, string Status, int Version);
public sealed record RevokeServerRequest(string Reason);
public sealed record RecoveryCopyRequest(string Password, string Passphrase);
public sealed record JoinInspectRequest(string MainUrl, string SetupCode);
public sealed record JoinRequest(string MainUrl, string SetupCode, string Token, string ConfirmedFingerprint, string? ServerName);

/// <summary>
/// The business certificate authority on the main/combined server (blueprint 18.1): creates the CA and the
/// sync-port TLS certificate, issues branch server certificates against one-time tokens, revokes them and
/// publishes the revocation list.
/// </summary>
public sealed class PkiService(KiteDb db, ServerContext server, KeyStore keys, KiteOptions opts, IClock clock, ILogger<PkiService> log)
{
    private const string CaKeyFile = "business-ca.key", CaPurpose = "business-ca";
    private const string TlsKeyFile = "sync-tls.key", TlsPurpose = "sync-tls";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private X509Certificate2? _authority;
    private volatile X509Certificate2? _tls;

    /// <summary>The certificate Kestrel presents on the sync port (null until set up).</summary>
    public X509Certificate2? SyncTlsCertificate => _tls;

    public X509Certificate2 Authority => _authority ?? throw new KiteException(ErrorCodes.AuthorityNotReady, "The business certificate authority is not ready yet.", 503);

    private sealed record AuthorityRow(Guid Id, string CertPem, string? KeyFile);
    private sealed record CertRow(string Serial, string CertPem, DateTime NotAfter);

    /// <summary>Creates or loads the CA and the sync TLS certificate. Safe to call repeatedly.</summary>
    public async Task EnsureAsync(CancellationToken ct)
    {
        if (!server.IsConfigured || !server.IsHeadOffice) return;
        await _gate.WaitAsync(ct);
        try
        {
            var now = clock.UtcNow;
            var row = await db.InTransactionAsync(AccessScope.System, ActorContext.System,
                t => t.QuerySingleOrDefaultAsync<AuthorityRow>("SELECT id, cert_pem AS CertPem, key_file AS KeyFile FROM pki.authority WHERE status = 'active'"), ct);
            if (row is null)
            {
                var name = await db.InTransactionAsync(AccessScope.System, ActorContext.System,
                    t => t.ExecuteScalarAsync<string>("SELECT legal_name_en FROM core.business"), ct);
                var ca = BusinessCa.CreateAuthority(server.BusinessId!.Value, name, now);
                keys.Save(CaKeyFile, CaPurpose, Convert.FromBase64String(BusinessCa.ExportPrivateKey(ca)));
                var id = Ids.New();
                await db.InTransactionAsync(AccessScope.System, ActorContext.System, async t =>
                {
                    await t.ExecuteAsync("""
                        INSERT INTO pki.authority (id, business_id, subject, cert_pem, fingerprint_sha256, not_before, not_after, key_file)
                        VALUES (@id, @b, @subject, @pem, @fp, @nb, @na, @kf)
                        """, new { id, b = server.BusinessId, subject = ca.Subject, pem = ca.ExportCertificatePem(), fp = BusinessCa.Fingerprint(ca),
                                   nb = ca.NotBefore.ToUniversalTime(), na = ca.NotAfter.ToUniversalTime(), kf = CaKeyFile });
                    await t.AuditAsync(new AuditEntry("pki.authority_created", "authority", id,
                        After: Json.Obj(new { fingerprintCode = BusinessCa.FingerprintCode(ca), keyProtection = keys.Protector.Kind })));
                }, ct);
                _authority = ca;
                log.LogInformation("Business certificate authority created; fingerprint code {Code}", BusinessCa.FingerprintCode(ca));
            }
            else
            {
                if (row.KeyFile is null) throw new InvalidOperationException("This server holds the business CA certificate but not its key.");
                var key = keys.TryLoad(row.KeyFile, CaPurpose)
                    ?? throw new InvalidOperationException($"The business CA key file {row.KeyFile} is missing from the data folder. Restore it from the owner's recovery copy.");
                _authority = BusinessCa.FromPemAndKey(row.CertPem, key);
            }
            await EnsureTlsAsync(now, ct);
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureTlsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var hosts = SyncHosts();
        var current = await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => t.QuerySingleOrDefaultAsync<CertRow>("""
            SELECT serial, cert_pem AS CertPem, not_after AS NotAfter FROM pki.certificate
             WHERE kind = 'server_tls' AND server_id = @s AND revoked_at IS NULL ORDER BY issued_at DESC LIMIT 1
            """, new { s = server.ServerId }), ct);
        var key = keys.TryLoad(TlsKeyFile, TlsPurpose);
        if (current is not null && key is not null && current.NotAfter > now.UtcDateTime.AddDays(30))
        {
            var cert = BusinessCa.FromPemAndKey(current.CertPem, key);
            var san = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
            var named = san is null ? [] : san.EnumerateDnsNames().Concat(san.EnumerateIPAddresses().Select(i => i.ToString())).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (hosts.All(named.Contains)) { _tls = cert; return; }
        }
        var issued = BusinessCa.IssueServerTls(Authority, server.ServerId!.Value, hosts, now);
        keys.Save(TlsKeyFile, TlsPurpose, Convert.FromBase64String(BusinessCa.ExportPrivateKey(issued)));
        await db.InTransactionAsync(AccessScope.System, ActorContext.System, async t =>
        {
            if (current is not null)
                await t.ExecuteAsync("UPDATE pki.certificate SET revoked_at = now(), revoked_reason = 'superseded' WHERE serial = @s", new { s = current.Serial });
            await InsertCertAsync(t, issued, "server_tls", server.ServerId, null, null);
        }, ct);
        _tls = issued;
        log.LogInformation("Sync TLS certificate issued for {Hosts}", string.Join(", ", hosts));
    }

    public IReadOnlyList<string> SyncHosts()
    {
        var hosts = new List<string> { "localhost", "127.0.0.1", Environment.MachineName };
        hosts.AddRange(opts.Sync.Hosts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        try
        {
            hosts.AddRange(NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString()));
        }
        catch (NetworkInformationException) { /* addresses are a convenience; configured names still apply */ }
        return hosts.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public CaInfo Info(string businessName) =>
        new(Authority.ExportCertificatePem(), BusinessCa.FingerprintCode(Authority), BusinessCa.Fingerprint(Authority), businessName, server.ServerId!.Value);

    public static Task InsertCertAsync(DbTx t, X509Certificate2 cert, string kind, Guid? serverId, Guid? branchId, Guid? issuedBy) =>
        t.ExecuteAsync("""
            INSERT INTO pki.certificate (serial, authority_id, kind, server_id, branch_id, subject, cert_pem, fingerprint_sha256, not_before, not_after, issued_by)
            VALUES (@serial, (SELECT id FROM pki.authority WHERE status = 'active'), @kind, @serverId, @branchId, @subject, @pem, @fp, @nb, @na, @issuedBy)
            """, new { serial = BusinessCa.SerialHex(cert), kind, serverId, branchId, subject = cert.Subject, pem = cert.ExportCertificatePem(),
                       fp = BusinessCa.Fingerprint(cert), nb = cert.NotBefore.ToUniversalTime(), na = cert.NotAfter.ToUniversalTime(), issuedBy });

    // ------------------------------------------------------------------------------------------ tokens
    public static string NewToken()
    {
        var raw = Base32.Encode(RandomNumberGenerator.GetBytes(15)); // 24 characters, 120 bits
        return string.Join("-", Enumerable.Range(0, 6).Select(i => raw.Substring(i * 4, 4)));
    }

    public static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(new string(token.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant()))).ToLowerInvariant();

    // ------------------------------------------------------------------------------------------ registration
    private sealed record TokenRow(Guid Id, Guid BranchId, DateTime ExpiresAt, DateTime? UsedAt, DateTime? CancelledAt, string BranchStatus, bool IsHeadOffice, string BranchCode);

    public async Task<RegisterBranchResponse> RegisterAsync(RegisterBranchRequest req, string? ip, LicenceService licence, ISecretProtector secrets, CancellationToken ct)
    {
        if (!server.IsHeadOffice) throw new KiteException(ErrorCodes.NotHeadOffice, "Branch servers register with the main server.", 409);
        var schema = await AppInfo.SchemaVersionAsync(db, ct);
        if (req.SchemaVersion != schema)
            throw new KiteException(ErrorCodes.ServerVersionMismatch, $"The branch server runs database version {req.SchemaVersion}; head office runs {schema}. Install the same KITE version on both.", 409);
        var status = await licence.StatusAsync(ct);
        if (status.State == LicenceState.Suspended) throw new KiteException(ErrorCodes.Suspended, "KITE is switched off by Nextdab.", 403);
        if (!status.CanTransact)
            throw new KiteException(status.State == LicenceState.None ? ErrorCodes.NoLicence : ErrorCodes.LicenceExpired, "A valid licence is needed to add branch servers.", 403);

        var hash = HashToken(req.Token ?? "");
        var serverId = Ids.New();
        var now = clock.UtcNow;
        X509Certificate2? issued = null;
        Guid branchId = Guid.Empty;
        var snapshot = await db.InTransactionAsync(AccessScope.System, new ActorContext(null, null, null, ip), async t =>
        {
            var tok = await t.QuerySingleOrDefaultAsync<TokenRow>("""
                SELECT r.id, r.branch_id AS BranchId, r.expires_at AS ExpiresAt, r.used_at AS UsedAt, r.cancelled_at AS CancelledAt,
                       b.status AS BranchStatus, b.is_head_office AS IsHeadOffice, b.code AS BranchCode
                  FROM core.registration_token r JOIN core.branch b ON b.id = r.branch_id
                 WHERE r.token_hash = @hash FOR UPDATE OF r
                """, new { hash });
            if (tok is null || tok.UsedAt is not null || tok.CancelledAt is not null || tok.ExpiresAt <= now.UtcDateTime)
                throw new KiteException(ErrorCodes.RegistrationTokenInvalid,
                    tok is null ? "The registration token is not correct." : tok.UsedAt is not null ? "This registration token was already used."
                    : tok.CancelledAt is not null ? "This registration token was cancelled." : "This registration token has expired (tokens last 24 hours).", 403, "token");
            if (tok.BranchStatus != "active") throw new KiteException(ErrorCodes.Validation, "The branch is not active.", 409);
            if (tok.IsHeadOffice) throw new KiteException(ErrorCodes.Validation, "The head office branch is served by the main server.", 409);
            if (await t.ExecuteScalarAsync<long>("SELECT count(*) FROM core.server WHERE branch_id = @b AND role = 'branch' AND status = 'active'", new { b = tok.BranchId }) > 0)
                throw new KiteException(ErrorCodes.BranchHasServer, $"Branch {tok.BranchCode} already has an active server. Revoke it first (Replace server).", 409);
            branchId = tok.BranchId;
            try { issued = BusinessCa.SignBranchRequest(Authority, req.CsrPem ?? "", server.BusinessId!.Value, branchId, serverId, now); }
            catch (CryptographicException e) { throw new KiteException(ErrorCodes.CertificateRejected, e.Message, 400, "csrPem"); }
            var name = string.IsNullOrWhiteSpace(req.ServerName) ? "Branch server " + tok.BranchCode : req.ServerName.Trim()[..Math.Min(80, req.ServerName.Trim().Length)];
            await t.ExecuteAsync("""
                INSERT INTO core.server (id, business_id, branch_id, role, name, is_self, cert_fingerprint, app_version, schema_version, status, last_ip)
                VALUES (@serverId, @b, @branchId, 'branch', @name, false, @fp, @app, @schema, 'active', @ip)
                """, new { serverId, b = server.BusinessId, branchId, name, fp = BusinessCa.Fingerprint(issued), app = req.AppVersion, schema = req.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), ip });
            await InsertCertAsync(t, issued, "branch_server", serverId, branchId, null);
            await t.ExecuteAsync("UPDATE core.registration_token SET used_at = now(), used_by_server_id = @serverId WHERE id = @id", new { serverId, id = tok.Id });
            await t.AuditAsync(new AuditEntry("server.registered", "server", serverId,
                After: Json.Obj(new { branch = tok.BranchCode, name, certificate = BusinessCa.SerialHex(issued), req.AppVersion, ip })));
            await t.PublishAsync("core.server_registered", "server", serverId, 1,
                Json.Obj(new { branchId, name, role = "branch", certFingerprint = BusinessCa.Fingerprint(issued) }), branchId);
            return await BranchSnapshot.BuildAsync(t, branchId, secrets);
        }, ct);
        return new RegisterBranchResponse(serverId, branchId, server.BusinessId!.Value, server.ServerId!.Value, issued!.ExportCertificatePem(), Authority.ExportCertificatePem(), snapshot);
    }

    // ------------------------------------------------------------------------------------------ revocation
    public async Task RevokeServersAsync(DbTx t, IEnumerable<Guid> serverIds, string reason)
    {
        var ids = serverIds.ToArray();
        if (ids.Length == 0) return;
        await t.ExecuteAsync("""
            UPDATE pki.certificate SET revoked_at = now(), revoked_reason = @reason, revoked_by = @by
             WHERE server_id = ANY(@ids) AND kind = 'branch_server' AND revoked_at IS NULL
            """, new { ids, reason, by = t.Actor.UserId });
        await t.ExecuteAsync("UPDATE core.server SET status = 'revoked' WHERE id = ANY(@ids) AND role = 'branch'", new { ids });
        foreach (var id in ids)
        {
            await t.AuditAsync(new AuditEntry("server.revoked", "server", id, After: Json.Obj(new { reason })));
            await t.PublishAsync("core.server_revoked", "server", id, 2, Json.Obj(new { reason }));
        }
        await PublishCrlAsync(t);
    }

    public async Task<long> PublishCrlAsync(DbTx t)
    {
        var revoked = (await t.QueryAsync<(string Serial, DateTime At)>(
            "SELECT serial, revoked_at FROM pki.certificate WHERE revoked_at IS NOT NULL AND kind = 'branch_server' ORDER BY revoked_at")).ToArray();
        var number = await t.ExecuteScalarAsync<long>("SELECT coalesce(max(number), 0) + 1 FROM pki.crl");
        var now = clock.UtcNow;
        var pem = BusinessCa.BuildCrlPem(Authority, number, revoked.Select(r => (r.Serial, new DateTimeOffset(DateTime.SpecifyKind(r.At, DateTimeKind.Utc)))), now);
        await t.ExecuteAsync("""
            INSERT INTO pki.crl (number, authority_id, issued_at, next_update, revoked_count, crl_pem)
            VALUES (@number, (SELECT id FROM pki.authority WHERE status = 'active'), @now, @next, @count, @pem)
            """, new { number, now, next = now.AddDays(7), count = revoked.Length, pem });
        return number;
    }

    // ------------------------------------------------------------------------------------------ heartbeat
    public sealed record BranchIdentity(Guid ServerId, Guid BranchId);

    /// <summary>Mutual TLS: the client certificate must chain to this business's CA and be on record, unrevoked.</summary>
    public async Task<BranchIdentity> AuthenticateAsync(X509Certificate2? cert, CancellationToken ct)
    {
        if (cert is null) throw new KiteException(ErrorCodes.CertificateRejected, "A branch server certificate is required.", 401);
        if (!BusinessCa.Chains(cert, Authority, clock.UtcNow, BusinessCa.ClientAuthOid, out var why))
            throw new KiteException(ErrorCodes.CertificateRejected, "The certificate is not accepted: " + why, 401);
        var row = await db.InTransactionAsync(AccessScope.System, ActorContext.System,
            t => t.QuerySingleOrDefaultAsync<(Guid ServerId, Guid BranchId, DateTime? RevokedAt, string ServerStatus, string BranchStatus)>("""
                SELECT c.server_id, c.branch_id, c.revoked_at, s.status, b.status
                  FROM pki.certificate c JOIN core.server s ON s.id = c.server_id JOIN core.branch b ON b.id = c.branch_id
                 WHERE c.fingerprint_sha256 = @fp AND c.kind = 'branch_server'
                """, new { fp = BusinessCa.Fingerprint(cert) }), ct);
        if (row.ServerId == Guid.Empty) throw new KiteException(ErrorCodes.CertificateRejected, "This certificate is not on record at head office.", 401);
        if (row.RevokedAt is not null || row.ServerStatus != "active" || row.BranchStatus != "active")
            throw new KiteException(ErrorCodes.ServerRevoked, "This branch server's certificate was revoked by head office.", 403);
        return new BranchIdentity(row.ServerId, row.BranchId);
    }
}

/// <summary>
/// Master data a new branch server starts with (blueprint 3.4 step 2): business, branches, users and roles,
/// tax, chart of accounts, its own warehouses and number series, settings, the CA certificate and the licence.
/// Two-step secrets are re-encrypted on arrival with the branch's own key.
/// </summary>
public static class BranchSnapshot
{
    // Order matters on import (foreign keys).
    public static readonly (string Table, string Where)[] Tables =
    [
        ("core.business", "true"),
        ("core.branch", "true"),
        ("core.server", "true"),
        ("core.warehouse", "branch_id = @branch"),
        ("core.number_series", "branch_id = @branch"),
        ("core.module_setting", "true"),
        ("core.setting", "scope = 'business' OR (scope = 'branch' AND scope_id = @branch)"),
        ("tax.tax_code", "true"),
        ("tax.tax_rate", "true"),
        ("acct.account", "true"),
        ("acct.fiscal_period", "branch_id IS NULL OR branch_id = @branch"),
        ("acct.posting_rule", "true"),
        ("iam.role", "true"),
        ("iam.role_permission", "true"),
        ("iam.\"user\"", "true"),
        ("iam.user_branch_role", "true"),
        ("pki.authority", "status = 'active'"),
        ("lic.licence", "is_current"),
    ];

    public static async Task<JsonObject> BuildAsync(DbTx t, Guid branchId, ISecretProtector secrets)
    {
        var tables = new JsonObject();
        foreach (var (table, where) in Tables)
        {
            var json = await t.ExecuteScalarAsync<string>($"SELECT coalesce(json_agg(x), '[]')::text FROM {table} x WHERE {where}", new { branch = branchId });
            var rows = JsonNode.Parse(json) as JsonArray ?? [];
            foreach (var r in rows.OfType<JsonObject>())
            {
                if (table == "core.server") r["is_self"] = false;
                if (table == "pki.authority") r["key_file"] = null;
                if (table == "lic.licence") r["source"] = "head_office";
            }
            tables[table] = rows;
        }
        var totp = new JsonArray();
        foreach (var u in (tables["iam.\"user\""] as JsonArray)!.OfType<JsonObject>())
        {
            if (u["totp_secret_enc"] is JsonValue v && v.TryGetValue<string>(out var hex) && hex.StartsWith("\\x", StringComparison.Ordinal))
            {
                var plain = secrets.Unprotect(Convert.FromHexString(hex[2..]));
                totp.Add(new JsonObject { ["id"] = u["id"]!.DeepClone(), ["secret"] = Convert.ToBase64String(plain) });
            }
            u["totp_secret_enc"] = null;
        }
        return new JsonObject { ["tables"] = tables, ["totp"] = totp };
    }

    public static async Task ImportAsync(DbTx t, JsonObject snapshot, ISecretProtector secrets)
    {
        var tables = snapshot["tables"] as JsonObject ?? throw new KiteException(ErrorCodes.Validation, "The head office answer has no master data.", 502);
        foreach (var (table, _) in Tables)
        {
            var rows = tables[table] as JsonArray ?? [];
            if (rows.Count == 0) continue;
            await t.ExecuteAsync($"INSERT INTO {table} SELECT * FROM json_populate_recordset(NULL::{table}, @rows::json)", new { rows = rows.ToJsonString() });
        }
        foreach (var e in (snapshot["totp"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var id = Guid.Parse(e["id"]!.GetValue<string>());
            var enc = secrets.Protect(Convert.FromBase64String(e["secret"]!.GetValue<string>()));
            await t.ExecuteAsync("UPDATE iam.\"user\" SET totp_secret_enc = @enc WHERE id = @id", new { enc, id });
        }
    }
}

/// <summary>
/// A branch server's link to head office (blueprint 4.4): joins with a token, then sends a heartbeat every
/// 30 seconds over mutual TLS and receives the licence, Nextdab control commands and the revocation list.
/// </summary>
public sealed class BranchLinkService(KiteDb db, ServerContext server, KeyStore keys, KiteOptions opts, IClock clock,
    LicenceService licence, ISecretProtector secrets, IServiceProvider services, ILogger<BranchLinkService> log) : BackgroundService
{
    private const string ClientKeyFile = "branch-client.key", ClientPurpose = "branch-client";
    public DateTimeOffset? LastOkAt { get; private set; }
    public string? LastError { get; private set; }
    public bool Revoked { get; private set; }
    public string? MainUrl { get; private set; }

    // ------------------------------------------------------------------------------------------ join (setup)
    public async Task<CaInfo> InspectAsync(string mainUrl, CancellationToken ct)
    {
        var url = NormaliseUrl(mainUrl);
        X509Certificate2? presented = null;
        using var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            SslOptions = { RemoteCertificateValidationCallback = (_, cert, _, _) => { presented = AsCert2(cert); return true; } },
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        CaInfo info;
        try { info = await http.GetFromJsonAsync<CaInfo>(url + "/sync/v1/ca", ct) ?? throw new HttpRequestException("empty answer"); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        { throw new KiteException(ErrorCodes.HeadOfficeUnreachable, $"Head office did not answer at {url}: {e.Message}", 502, "mainUrl"); }
        var ca = BusinessCa.FromPem(info.CaPem);
        // The TLS endpoint must hold a key certified by the CA it hands out.
        if (presented is null || !BusinessCa.Chains(presented, ca, clock.UtcNow, BusinessCa.ServerAuthOid, out var why))
            throw new KiteException(ErrorCodes.CertificateRejected, "The head office address did not present a certificate from its own authority.", 502, "mainUrl");
        return info with { FingerprintCode = BusinessCa.FingerprintCode(ca), Fingerprint = BusinessCa.Fingerprint(ca) };
    }

    public async Task<(Guid BranchId, string BusinessName)> JoinAsync(JoinRequest req, CancellationToken ct)
    {
        var url = NormaliseUrl(req.MainUrl);
        var info = await InspectAsync(url, ct);
        if (!string.Equals(info.Fingerprint, req.ConfirmedFingerprint?.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new KiteException(ErrorCodes.FingerprintMismatch, "The head office certificate changed since you compared the code. Start again and compare the code.", 409);
        var ca = BusinessCa.FromPem(info.CaPem);
        var name = string.IsNullOrWhiteSpace(req.ServerName) ? Environment.MachineName : req.ServerName.Trim();
        var (csr, privateKey) = BusinessCa.CreateBranchRequest(name);
        var schema = await AppInfo.SchemaVersionAsync(db, ct);
        using var http = PinnedClient(ca, info.MainServerId, null);
        HttpResponseMessage res;
        try { res = await http.PostAsJsonAsync(url + "/sync/v1/register", new RegisterBranchRequest(req.Token, csr, name, AppInfo.Version, schema), ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        { throw new KiteException(ErrorCodes.HeadOfficeUnreachable, $"Head office did not answer at {url}: {e.Message}", 502, "mainUrl"); }
        if (!res.IsSuccessStatusCode)
        {
            var problem = await res.Content.ReadFromJsonAsync<JsonObject>(ct);
            throw new KiteException(problem?["code"]?.GetValue<string>() ?? ErrorCodes.HeadOfficeUnreachable,
                problem?["detail"]?.GetValue<string>() ?? $"Head office refused the registration (HTTP {(int)res.StatusCode}).", (int)res.StatusCode is >= 400 and < 500 ? (int)res.StatusCode : 502, "token");
        }
        var reg = await res.Content.ReadFromJsonAsync<RegisterBranchResponse>(ct) ?? throw new KiteException(ErrorCodes.HeadOfficeUnreachable, "Empty answer from head office.", 502);
        var cert = BusinessCa.WithPrivateKey(BusinessCa.FromPem(reg.CertPem), privateKey);
        if (!BusinessCa.Chains(cert, ca, clock.UtcNow, BusinessCa.ClientAuthOid, out var why))
            throw new KiteException(ErrorCodes.CertificateRejected, "Head office returned a certificate that does not chain to its authority: " + why, 502);

        keys.Save(ClientKeyFile, ClientPurpose, privateKey);
        string businessName = "";
        await db.InTransactionAsync(AccessScope.System, ActorContext.System, async t =>
        {
            if (await t.ExecuteScalarAsync<long>("SELECT count(*) FROM core.business") > 0)
                throw new KiteException(ErrorCodes.SetupAlreadyDone, "This server is already set up.", 409);
            await BranchSnapshot.ImportAsync(t, reg.Snapshot, secrets);
            await t.ExecuteAsync("UPDATE core.server SET is_self = true, sync_url = @url, installation_id = @mid WHERE id = @id",
                new { url, id = reg.ServerId, mid = MachineId.New() });
            await PkiService.InsertCertAsync(t, cert, "branch_server", reg.ServerId, reg.BranchId, null);
            businessName = await t.ExecuteScalarAsync<string>("SELECT legal_name_en FROM core.business");
            await t.ExecuteAsync("INSERT INTO lic.control_state (checkin_key) VALUES (@k) ON CONFLICT (id) DO NOTHING", new { k = NewCheckinKey() });
            await t.AuditAsync(new AuditEntry("server.joined", "server", reg.ServerId,
                After: Json.Obj(new { headOffice = url, branchId = reg.BranchId, certificate = BusinessCa.SerialHex(cert), fingerprintCode = info.FingerprintCode })));
        }, ct);
        await server.LoadAsync(db.DataSource, ct);
        licence.Invalidate();
        MainUrl = url;
        log.LogInformation("Joined head office {Url} as branch {Branch}", url, reg.BranchId);
        return (reg.BranchId, businessName);
    }

    private static X509Certificate2? AsCert2(X509Certificate? cert) =>
        cert switch { null => null, X509Certificate2 c2 => c2, _ => X509CertificateLoader.LoadCertificate(cert.GetRawCertData()) };

    public static string NewCheckinKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(30)).Replace('+', '-').Replace('/', '_');

    private static string NormaliseUrl(string s)
    {
        var t = (s ?? "").Trim().TrimEnd('/');
        if (!t.Contains("://", StringComparison.Ordinal)) t = "https://" + t;
        if (!Uri.TryCreate(t, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
            throw new KiteException(ErrorCodes.Validation, "Enter the head office address, for example https://192.168.1.10:5443", 400, "mainUrl");
        return u.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>
    /// HTTPS to head office, trusting only the business CA and the expected main server certificate
    /// (name mismatches are allowed: the pinned CA is the trust anchor, and branches may use an IP address).
    /// </summary>
    private HttpClient PinnedClient(X509Certificate2 ca, Guid mainServerId, X509Certificate2? clientCert)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    AsCert2(cert) is { } c && BusinessCa.Chains(c, ca, clock.UtcNow, BusinessCa.ServerAuthOid, out _)
                    && c.GetNameInfo(X509NameType.SimpleName, false) == "kite-server:" + mainServerId.ToString("D"),
            },
        };
        if (clientCert is not null) handler.SslOptions.ClientCertificates = [clientCert];
        return new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(20) };
    }

    // ------------------------------------------------------------------------------------------ heartbeat loop
    private sealed record LinkRow(string? SyncUrl, string? CaPem, Guid MainServerId);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(Math.Max(5, opts.Sync.HeartbeatSeconds));
            try
            {
                if (server.IsConfigured && server.Role == "branch" && !Revoked) await HeartbeatOnceAsync(stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                LastError = e.Message;
                log.LogWarning("Heartbeat to head office failed: {Error}", e.Message);
            }
            try { await Task.Delay(delay, stop); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// An HTTP client to head office's sync port with this branch's certificate, head office pinned by its CA and server id;
    /// null when this server is not registered (or not a branch server).
    /// </summary>
    public async Task<(HttpClient Http, string SyncUrl)?> MainClientAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (server.Role != "branch" || Revoked) return null;
        var link = await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => t.QuerySingleOrDefaultAsync<LinkRow>("""
            SELECT s.sync_url AS SyncUrl, a.cert_pem AS CaPem, m.id AS MainServerId
              FROM core.server s CROSS JOIN pki.authority a CROSS JOIN core.server m
             WHERE s.is_self AND a.status = 'active' AND m.role IN ('combined','main')
            """), ct);
        if (link?.SyncUrl is null || link.CaPem is null) return null;
        MainUrl = link.SyncUrl;
        var key = keys.TryLoad(ClientKeyFile, ClientPurpose);
        if (key is null) return null;
        var certPem = await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => t.ExecuteScalarAsync<string>(
            "SELECT cert_pem FROM pki.certificate WHERE server_id = @s AND kind = 'branch_server' ORDER BY issued_at DESC LIMIT 1", new { s = server.ServerId }), ct);
        var http = PinnedClient(BusinessCa.FromPem(link.CaPem), link.MainServerId, BusinessCa.FromPemAndKey(certPem, key));
        http.Timeout = timeout;
        return (http, link.SyncUrl);
    }

    /// <summary>True when head office answered recently, or when nothing has failed yet: worth asking online before a receipt.</summary>
    public bool HeadOfficeLikelyReachable => LastError is null || (LastOkAt is { } ok && clock.UtcNow - ok < TimeSpan.FromSeconds(Math.Max(15, opts.Sync.HeartbeatSeconds) * 3));

    public async Task HeartbeatOnceAsync(CancellationToken ct)
    {
        var link = await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => t.QuerySingleOrDefaultAsync<LinkRow>("""
            SELECT s.sync_url AS SyncUrl, a.cert_pem AS CaPem, m.id AS MainServerId
              FROM core.server s CROSS JOIN pki.authority a CROSS JOIN core.server m
             WHERE s.is_self AND a.status = 'active' AND m.role IN ('combined','main')
            """), ct);
        if (link?.SyncUrl is null || link.CaPem is null) { LastError = "Not registered with head office."; return; }
        MainUrl = link.SyncUrl;
        var key = keys.TryLoad(ClientKeyFile, ClientPurpose) ?? throw new InvalidOperationException("The branch certificate key is missing from the data folder.");
        var certPem = await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => t.ExecuteScalarAsync<string>(
            "SELECT cert_pem FROM pki.certificate WHERE server_id = @s AND kind = 'branch_server' ORDER BY issued_at DESC LIMIT 1", new { s = server.ServerId }), ct);
        var cert = BusinessCa.FromPemAndKey(certPem, key);
        var ca = BusinessCa.FromPem(link.CaPem);
        // Devices first (11.4): units received while head office was away, and status changes, go up before the heartbeat.
        var central = services.GetRequiredService<CentralClient>();
        try { await central.PushAsync(null, ct); }
        catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning("Pushing devices to head office failed: {Error}", e.Message); }
        var (imeiSeq, blockedSeq) = await central.SeqsAsync(ct);

        var state = await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => t.QuerySingleOrDefaultAsync<(long LastSeq, long Pending, string? LicenceId, long Crl, long SuspendSeq, long ModulesSeq)>("""
            SELECT (SELECT coalesce(max(origin_seq), 0) FROM sync.outbox WHERE origin_server_id = @s),
                   (SELECT count(*) FROM sync.outbox WHERE origin_server_id = @s AND acked_at IS NULL),
                   (SELECT id FROM lic.licence WHERE is_current),
                   (SELECT coalesce(max(number), 0) FROM pki.crl),
                   coalesce((SELECT suspend_seq FROM lic.control_state), 0),
                   coalesce((SELECT modules_seq FROM lic.control_state), 0)
            """, new { s = server.ServerId }), ct);
        var backup = await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => t.QuerySingleOrDefaultAsync<(DateTime? At, string? Status)>(
            "SELECT max(finished_at) FILTER (WHERE status = 'succeeded'), (SELECT status FROM ops.backup_run WHERE kind IN ('logical','base') ORDER BY started_at DESC LIMIT 1) FROM ops.backup_run WHERE kind IN ('logical','base')"), ct);
        var schema = await AppInfo.SchemaVersionAsync(db, ct);

        using var http = PinnedClient(ca, link.MainServerId, cert);
        var res = await http.PostAsJsonAsync(link.SyncUrl + "/sync/v1/heartbeat", new HeartbeatRequest(AppInfo.Version, schema, clock.UtcNow,
            state.LastSeq, state.Pending, backup.At is null ? null : new DateTimeOffset(DateTime.SpecifyKind(backup.At.Value, DateTimeKind.Utc)), backup.Status,
            state.LicenceId, state.Crl, state.SuspendSeq, state.ModulesSeq, imeiSeq, blockedSeq), ct);
        if (res.StatusCode == HttpStatusCode.Forbidden)
        {
            var p = await res.Content.ReadFromJsonAsync<JsonObject>(ct);
            if (p?["code"]?.GetValue<string>() == ErrorCodes.ServerRevoked)
            {
                Revoked = true;
                LastError = "This server's certificate was revoked by head office.";
                await db.InTransactionAsync(AccessScope.System, ActorContext.System, async t =>
                {
                    await t.ExecuteAsync("UPDATE core.server SET status = 'revoked' WHERE is_self");
                    await t.AuditAsync(new AuditEntry("server.revoked_by_head_office", "server", server.ServerId));
                }, ct);
                return;
            }
        }
        res.EnsureSuccessStatusCode();
        var hb = await res.Content.ReadFromJsonAsync<HeartbeatResponse>(ct) ?? throw new InvalidOperationException("Empty heartbeat answer.");
        if (hb.LicenceFile is not null) await licence.ImportAsync(hb.LicenceFile, "head_office", ActorContext.System, ct);
        await central.ApplyFromHeadOfficeAsync(hb.ImeiInstructions ?? [], hb.Blocked ?? [], ct);
        foreach (var c in hb.Control.OfType<JsonObject>()) await licence.ApplyControlFromHeadOfficeAsync(c, ct);
        if (hb.CrlPem is not null && hb.CrlNumber > state.Crl)
            await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => t.ExecuteAsync("""
                INSERT INTO pki.crl (number, authority_id, issued_at, next_update, revoked_count, crl_pem)
                VALUES (@n, (SELECT id FROM pki.authority WHERE status = 'active'), now(), now() + interval '7 days', 0, @pem) ON CONFLICT DO NOTHING
                """, new { n = hb.CrlNumber, pem = hb.CrlPem }), ct);
        await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => t.ExecuteAsync(
            "UPDATE core.server SET last_seen_at = now(), heartbeat = @hb::jsonb WHERE role IN ('combined','main')",
            new { hb = JsonSerializer.Serialize(new { serverTime = hb.ServerTime, skewSeconds = (clock.UtcNow - hb.ServerTime).TotalSeconds }) }), ct);
        LastOkAt = clock.UtcNow;
        LastError = null;
    }
}

// ------------------------------------------------------------------------------------------------ endpoints
public static class ServerEndpoints
{
    public static void Map(WebApplication app)
    {
        // ---- sync port (HTTPS only; head office) ----
        var sync = app.MapGroup("/sync/v1").AddEndpointFilter(async (ctx, next) =>
        {
            if (!ctx.HttpContext.Request.IsHttps)
                throw new KiteException(ErrorCodes.NotFound, "Sync requests are accepted only on the TLS sync port.", 404);
            return await next(ctx);
        });

        sync.MapGet("/ca", async (KiteDb db, ServerContext server, PkiService pki, CancellationToken ct) =>
        {
            if (!server.IsConfigured || !server.IsHeadOffice) throw new KiteException(ErrorCodes.NotHeadOffice, "This is not a main server.", 404);
            var name = await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => t.ExecuteScalarAsync<string>("SELECT legal_name_en FROM core.business"), ct);
            return Results.Ok(pki.Info(name));
        }).AllowEveryone();

        sync.MapPost("/register", async (RegisterBranchRequest req, HttpContext http, PkiService pki, LicenceService licence, ISecretProtector secrets, CancellationToken ct) =>
            Results.Ok(await pki.RegisterAsync(req, http.Connection.RemoteIpAddress?.ToString(), licence, secrets, ct)))
            .AllowEveryone();

        sync.MapPost("/heartbeat", async (HeartbeatRequest req, HttpContext http, KiteDb db, PkiService pki, LicenceService licence, CancellationToken ct) =>
        {
            var who = await pki.AuthenticateAsync(http.Connection.ClientCertificate ?? await http.Connection.GetClientCertificateAsync(ct), ct);
            var info = await db.InTransactionAsync(AccessScope.System, ActorContext.System, async t =>
            {
                await t.ExecuteAsync("""
                    UPDATE core.server SET last_seen_at = now(), app_version = @app, schema_version = @schema, last_ip = @ip, heartbeat = @hb::jsonb
                     WHERE id = @id
                    """, new { id = who.ServerId, app = req.AppVersion, schema = req.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                               ip = http.Connection.RemoteIpAddress?.ToString(), hb = JsonSerializer.Serialize(req, Json.Web) });
                return await t.QuerySingleOrDefaultAsync<(string? LicenceId, string? LicenceFile, long Crl, string? CrlPem)>("""
                    SELECT l.id, l.file_text, coalesce(c.number, 0), c.crl_pem
                      FROM (SELECT 1) one
                      LEFT JOIN lic.licence l ON l.is_current
                      LEFT JOIN LATERAL (SELECT number, crl_pem FROM pki.crl ORDER BY number DESC LIMIT 1) c ON true
                    """);
            }, ct);
            var control = await licence.ControlCommandsForBranchAsync(req.SuspendSeq, req.ModulesSeq, ct);
            var (instructions, blocked) = await db.InTransactionAsync(AccessScope.System, ActorContext.System, async t =>
                (await CentralSync.InstructionsForAsync(t, who.ServerId, req.ImeiInstructionSeq), await CentralSync.BlockedSinceAsync(t, req.BlockedSeq)), ct);
            return Results.Ok(new HeartbeatResponse(DateTimeOffset.UtcNow,
                info.LicenceId is not null && info.LicenceId != req.LicenceId ? info.LicenceFile : null,
                control, info.Crl, info.Crl > req.CrlNumber ? info.CrlPem : null, instructions, blocked));
        }).AllowEveryone(); // authenticated by the client certificate, not a session
        CentralSync.Map(sync);   // devices: online IMEI check, registration of units, look-up (Phase 6)

        // ---- branch setup: join an existing business ----
        app.MapPost("/api/v1/setup/join/inspect", async (JoinInspectRequest req, ServerContext server, SetupCodeStore codes, BranchLinkService link, CancellationToken ct) =>
        {
            if (server.IsConfigured) throw new KiteException(ErrorCodes.SetupAlreadyDone, "This server is already set up.", 409);
            if (!codes.Matches(req.SetupCode)) throw new KiteException(ErrorCodes.SetupCodeInvalid, "The setup code is not correct. It is shown on the server screen and in its data folder.", 403, "setupCode");
            var info = await link.InspectAsync(req.MainUrl, ct);
            return Results.Ok(new { info.BusinessName, info.FingerprintCode, info.Fingerprint });
        }).AllowEveryone();

        app.MapPost("/api/v1/setup/join", async (JoinRequest req, ServerContext server, SetupCodeStore codes, BranchLinkService link, ModuleStateCache modules, CancellationToken ct) =>
        {
            if (server.IsConfigured) throw new KiteException(ErrorCodes.SetupAlreadyDone, "This server is already set up.", 409);
            if (!codes.Matches(req.SetupCode)) throw new KiteException(ErrorCodes.SetupCodeInvalid, "The setup code is not correct. It is shown on the server screen and in its data folder.", 403, "setupCode");
            var (branchId, businessName) = await link.JoinAsync(req, ct);
            codes.Consume();
            modules.Invalidate();
            return Results.Ok(new { branchId, businessName });
        }).AllowEveryone();

        // ---- branches ----
        app.MapPost("/api/v1/branches", async (CreateBranchRequest req, HttpContext http, KiteDb db, ServerContext server, LicenceService licence, CancellationToken ct) =>
        {
            if (!server.IsHeadOffice) throw new KiteException(ErrorCodes.NotHeadOffice, "Branches are added on the main server.", 409);
            var code = (req.Code ?? "").Trim().ToUpperInvariant();
            if (!System.Text.RegularExpressions.Regex.IsMatch(code, "^[A-Z0-9-]{2,12}$")) throw new KiteException(ErrorCodes.Validation, "Branch code must be 2 to 12 letters, digits or hyphens.", 400, "code");
            if (string.IsNullOrWhiteSpace(req.NameEn)) throw new KiteException(ErrorCodes.Validation, "Name in English is required.", 400, "nameEn");
            if (string.IsNullOrWhiteSpace(req.NameAr)) throw new KiteException(ErrorCodes.Validation, "Name in Arabic is required.", 400, "nameAr");
            var status = await licence.StatusAsync(ct);
            var id = Ids.New();
            await db.InTransactionAsync(AccessScope.System, http.ActorOf(), async t =>
            {
                var active = await t.ExecuteScalarAsync<long>("SELECT count(*) FROM core.branch WHERE status = 'active'");
                if (active >= status.BranchesAllowed)
                    throw new KiteException(ErrorCodes.LicenceLimit, $"The licence allows {status.BranchesAllowed} branch{(status.BranchesAllowed == 1 ? "" : "es")}. Ask Nextdab to extend it.", 403);
                if (await t.ExecuteScalarAsync<long>("SELECT count(*) FROM core.branch WHERE code = @code", new { code }) > 0)
                    throw new KiteException(ErrorCodes.Conflict, $"Branch code {code} is already used.", 409, "code");
                await t.ExecuteAsync("""
                    INSERT INTO core.branch (id, business_id, code, name_en, name_ar, address_en, address_ar, phone, is_head_office, created_by)
                    VALUES (@id, @b, @code, @NameEn, @NameAr, @AddressEn, @AddressAr, @Phone, false, @by)
                    """, new { id, b = server.BusinessId, code, req.NameEn, req.NameAr, req.AddressEn, req.AddressAr, req.Phone, by = t.Actor.UserId });
                await t.ExecuteAsync("""
                    INSERT INTO core.warehouse (id, business_id, branch_id, code, name_en, name_ar, kind, created_by)
                    VALUES (@wid, @b, @id, 'SHOP', 'Shop floor', 'صالة العرض', 'shop', @by)
                    """, new { wid = Ids.New(), b = server.BusinessId, id, by = t.Actor.UserId });
                var fy = await t.ExecuteScalarAsync<int>("SELECT max(fiscal_year) FROM acct.fiscal_period");
                foreach (var (docType, prefix) in SetupEndpoints.NumberSeries)
                    await t.ExecuteAsync("""
                        INSERT INTO core.number_series (id, business_id, branch_id, doc_type, prefix, fiscal_year, created_by)
                        VALUES (@nid, @b, @id, @docType, @prefix, @fy, @by)
                        """, new { nid = Ids.New(), b = server.BusinessId, id, docType, prefix = $"{prefix}-{code}-{fy}-", fy, by = t.Actor.UserId });
                await t.AuditAsync(new AuditEntry("branch.created", "branch", id, After: Json.Obj(new { code, req.NameEn, req.NameAr })));
                await t.PublishAsync("core.branch_created", "branch", id, 1, Json.Obj(new { code, req.NameEn, req.NameAr, isHeadOffice = false }), id);
            }, ct);
            return Results.Created($"/api/v1/branches/{id}", new { id, code });
        }).RequirePermission(Permissions.BranchesManage);

        app.MapPut("/api/v1/branches/{id:guid}", async (Guid id, UpdateBranchRequest req, HttpContext http, KiteDb db, ServerContext server, PkiService pki, LicenceService licence, CancellationToken ct) =>
        {
            if (!server.IsHeadOffice) throw new KiteException(ErrorCodes.NotHeadOffice, "Branches are changed on the main server.", 409);
            if (req.Status is not ("active" or "inactive")) throw new KiteException(ErrorCodes.Validation, "Status must be active or inactive.", 400, "status");
            var status = await licence.StatusAsync(ct);
            await db.InTransactionAsync(AccessScope.System, http.ActorOf(), async t =>
            {
                var before = await t.QuerySingleOrDefaultAsync<(string Status, bool IsHeadOffice, int Version)>(
                    "SELECT status, is_head_office, version FROM core.branch WHERE id = @id FOR UPDATE", new { id });
                if (before.Version == 0) throw new KiteException(ErrorCodes.NotFound, "Branch not found.", 404);
                if (before.Version != req.Version) throw new KiteException(ErrorCodes.VersionMismatch, "Someone else changed this branch. Reload and try again.", 412);
                if (before.IsHeadOffice && req.Status == "inactive") throw new KiteException(ErrorCodes.Validation, "The head office branch cannot be deactivated.", 409);
                if (before.Status == "inactive" && req.Status == "active"
                    && await t.ExecuteScalarAsync<long>("SELECT count(*) FROM core.branch WHERE status = 'active'") >= status.BranchesAllowed)
                    throw new KiteException(ErrorCodes.LicenceLimit, $"The licence allows {status.BranchesAllowed} active branches.", 403);
                await t.ExecuteAsync("""
                    UPDATE core.branch SET name_en = @NameEn, name_ar = @NameAr, address_en = @AddressEn, address_ar = @AddressAr, phone = @Phone,
                           status = @Status, updated_by = @by, version = version + 1 WHERE id = @id
                    """, new { id, req.NameEn, req.NameAr, req.AddressEn, req.AddressAr, req.Phone, req.Status, by = t.Actor.UserId });
                await t.AuditAsync(new AuditEntry("branch.updated", "branch", id, Before: Json.Obj(new { before.Status }), After: Json.Obj(req)));
                await t.PublishAsync("core.branch_updated", "branch", id, before.Version + 1, Json.Obj(req), id);
                if (before.Status == "active" && req.Status == "inactive")
                {
                    // Deactivating a branch revokes its server's certificate (blueprint 18.1 step 3).
                    var servers = await t.QueryAsync<Guid>("SELECT id FROM core.server WHERE branch_id = @id AND role = 'branch' AND status = 'active'", new { id });
                    await pki.RevokeServersAsync(t, servers, "branch_deactivated");
                }
            }, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.BranchesManage);

        // ---- servers, tokens, revocation ----
        app.MapGet("/api/v1/servers", async (KiteDb db, ServerContext server, PkiService pki, BranchLinkService link, CancellationToken ct) =>
        {
            var rows = await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => t.QueryAsync<ServerRow>("""
                SELECT s.id, s.branch_id AS BranchId, b.code AS BranchCode, s.role, s.name, s.is_self AS IsSelf, s.status, s.app_version AS AppVersion,
                       s.last_seen_at AS LastSeenAt, s.last_ip AS LastIp, s.installation_id AS InstallationId, s.heartbeat::text AS Heartbeat,
                       (SELECT serial FROM pki.certificate c WHERE c.server_id = s.id AND c.kind = 'branch_server' ORDER BY issued_at DESC LIMIT 1) AS CertificateSerial,
                       (SELECT not_after FROM pki.certificate c WHERE c.server_id = s.id AND c.kind = 'branch_server' ORDER BY issued_at DESC LIMIT 1) AS CertificateExpires
                  FROM core.server s LEFT JOIN core.branch b ON b.id = s.branch_id
                 ORDER BY s.is_self DESC, b.code
                """), ct);
            var tokens = await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => t.QueryAsync<TokenDto>("""
                SELECT r.id, r.branch_id AS BranchId, b.code AS BranchCode, r.expires_at AS ExpiresAt, r.created_at AS CreatedAt,
                       r.used_at AS UsedAt, r.cancelled_at AS CancelledAt
                  FROM core.registration_token r JOIN core.branch b ON b.id = r.branch_id
                 WHERE r.created_at > now() - interval '30 days' ORDER BY r.created_at DESC
                """), ct);
            var branches = await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => t.QueryAsync<BranchAdminRow>("""
                SELECT id, code, name_en AS NameEn, name_ar AS NameAr, address_en AS AddressEn, address_ar AS AddressAr, phone, is_head_office AS IsHeadOffice,
                       status, version FROM core.branch ORDER BY is_head_office DESC, code
                """), ct);
            object? ca = null;
            if (server.IsHeadOffice && pki.SyncTlsCertificate is not null)
                ca = new { fingerprintCode = BusinessCa.FingerprintCode(pki.Authority), hosts = pki.SyncHosts() };
            var now = DateTime.UtcNow;
            return Results.Ok(new
            {
                self = new { server.ServerId, server.Role, server.InstallationId, version = AppInfo.Version, ca },
                link = server.Role == "branch" ? new { link.MainUrl, link.LastOkAt, link.LastError, link.Revoked } : null,
                servers = rows.Select(r => new
                {
                    r.Id, r.BranchId, r.BranchCode, r.Role, r.Name, r.IsSelf, r.Status, r.AppVersion, r.LastSeenAt, r.LastIp, r.InstallationId,
                    r.CertificateSerial, r.CertificateExpires,
                    online = r.IsSelf || (r.LastSeenAt is { } seen && now - seen < TimeSpan.FromSeconds(90)),
                    heartbeat = r.Heartbeat is null ? null : JsonNode.Parse(r.Heartbeat),
                }),
                tokens,
                branches,
                syncPort = app.Services.GetRequiredService<KiteOptions>().Sync.Port,
            });
        }).RequirePermission(Permissions.SettingsView);

        app.MapPost("/api/v1/branches/{branchId:guid}/registration-tokens", async (Guid branchId, HttpContext http, KiteDb db, ServerContext server, PkiService pki, LicenceService licence, IClock clock, CancellationToken ct) =>
        {
            http.User.Demand(Permissions.ServersManage, branchId);
            if (!server.IsHeadOffice) throw new KiteException(ErrorCodes.NotHeadOffice, "Branch servers are registered on the main server.", 409);
            await licence.RequireTransactAsync(ct);
            var token = PkiService.NewToken();
            var expires = clock.UtcNow.AddHours(24);
            var id = Ids.New();
            await db.InTransactionAsync(AccessScope.System, http.ActorOf(), async t =>
            {
                var b = await t.QuerySingleOrDefaultAsync<(string Status, bool IsHeadOffice, string Code)>("SELECT status, is_head_office, code FROM core.branch WHERE id = @branchId", new { branchId });
                if (b.Code is null) throw new KiteException(ErrorCodes.NotFound, "Branch not found.", 404);
                if (b.Status != "active") throw new KiteException(ErrorCodes.Validation, "The branch is not active.", 409);
                if (b.IsHeadOffice) throw new KiteException(ErrorCodes.Validation, "The head office branch is served by this server.", 409);
                await t.ExecuteAsync("""
                    INSERT INTO core.registration_token (id, business_id, branch_id, token_hash, expires_at, created_at, created_by)
                    VALUES (@id, @biz, @branchId, @hash, @expires, @now, @by)
                    """, new { id, biz = server.BusinessId, branchId, hash = PkiService.HashToken(token), expires, now = clock.UtcNow, by = t.Actor.UserId });
                await t.AuditAsync(new AuditEntry("server.registration_token_created", "branch", branchId, After: Json.Obj(new { tokenId = id, expires })));
            }, ct);
            await pki.EnsureAsync(ct);
            return Results.Ok(new { id, token, expiresAt = expires, fingerprintCode = BusinessCa.FingerprintCode(pki.Authority), hosts = pki.SyncHosts(), port = app.Services.GetRequiredService<KiteOptions>().Sync.Port });
        }).RequireSignedIn();

        app.MapPost("/api/v1/registration-tokens/{id:guid}/cancel", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            await db.InTransactionAsync(AccessScope.System, http.ActorOf(), async t =>
            {
                var branchId = await t.ExecuteScalarAsync<Guid?>("SELECT branch_id FROM core.registration_token WHERE id = @id AND used_at IS NULL AND cancelled_at IS NULL", new { id })
                    ?? throw new KiteException(ErrorCodes.NotFound, "No open token with this id.", 404);
                http.User.Demand(Permissions.ServersManage, branchId);
                await t.ExecuteAsync("UPDATE core.registration_token SET cancelled_at = now(), cancelled_by = @by WHERE id = @id", new { id, by = t.Actor.UserId });
                await t.AuditAsync(new AuditEntry("server.registration_token_cancelled", "branch", branchId, After: Json.Obj(new { tokenId = id })));
            }, ct);
            return Results.NoContent();
        }).RequireSignedIn();

        app.MapPost("/api/v1/servers/{id:guid}/revoke", async (Guid id, RevokeServerRequest req, HttpContext http, KiteDb db, ServerContext server, PkiService pki, CancellationToken ct) =>
        {
            if (!server.IsHeadOffice) throw new KiteException(ErrorCodes.NotHeadOffice, "Servers are revoked on the main server.", 409);
            if (req.Reason is not ("server_replaced" or "key_compromise")) throw new KiteException(ErrorCodes.Validation, "Reason must be server_replaced or key_compromise.", 400, "reason");
            await db.InTransactionAsync(AccessScope.System, http.ActorOf(), async t =>
            {
                var s = await t.QuerySingleOrDefaultAsync<(Guid? BranchId, string Role, string Status)>("SELECT branch_id, role, status FROM core.server WHERE id = @id FOR UPDATE", new { id });
                if (s.Role is null) throw new KiteException(ErrorCodes.NotFound, "Server not found.", 404);
                if (s.Role != "branch") throw new KiteException(ErrorCodes.Validation, "Only branch servers can be revoked.", 409);
                http.User.Demand(Permissions.ServersManage, s.BranchId);
                if (s.Status == "revoked") throw new KiteException(ErrorCodes.Conflict, "This server is already revoked.", 409);
                await pki.RevokeServersAsync(t, [id], req.Reason);
            }, ct);
            return Results.NoContent();
        }).RequireSignedIn();

        app.MapPost("/api/v1/pki/recovery-copy", async (RecoveryCopyRequest req, HttpContext http, KiteDb db, ServerContext server, PkiService pki, IPasswordHasher hasher, CancellationToken ct) =>
        {
            if (!server.IsHeadOffice) throw new KiteException(ErrorCodes.NotHeadOffice, "The certificate authority is on the main server.", 409);
            await Reauth.RequirePasswordAsync(db, hasher, http, req.Password, ct);
            if ((req.Passphrase ?? "").Length < 12) throw new KiteException(ErrorCodes.Validation, "The passphrase must have at least 12 characters. Keep it with the printed copy.", 400, "passphrase");
            var pem = BusinessCa.ExportRecoveryCopy(pki.Authority, req.Passphrase!);
            await db.InTransactionAsync(AccessScope.System, http.ActorOf(), t => t.AuditAsync(new AuditEntry("pki.recovery_copy_exported", "authority", null)), ct);
            return Results.Text(pem, "application/x-pem-file");
        }).RequirePermission(Permissions.ServersManage);
    }

    private sealed record ServerRow(Guid Id, Guid? BranchId, string? BranchCode, string Role, string Name, bool IsSelf, string Status, string? AppVersion,
        DateTime? LastSeenAt, string? LastIp, string? InstallationId, string? Heartbeat, string? CertificateSerial, DateTime? CertificateExpires);
    private sealed record BranchAdminRow(Guid Id, string Code, string NameEn, string NameAr, string? AddressEn, string? AddressAr, string? Phone, bool IsHeadOffice, string Status, int Version);
    public sealed record TokenDto(Guid Id, Guid BranchId, string BranchCode, DateTime ExpiresAt, DateTime CreatedAt, DateTime? UsedAt, DateTime? CancelledAt);
}

/// <summary>Sensitive actions ask for the user's password again.</summary>
public static class Reauth
{
    public static async Task RequirePasswordAsync(KiteDb db, IPasswordHasher hasher, HttpContext http, string? password, CancellationToken ct)
    {
        var uid = http.User.UserIdOf();
        var hash = await db.InTransactionAsync(AccessScope.System, ActorContext.System,
            t => t.ExecuteScalarAsync<string>("SELECT password_hash FROM iam.\"user\" WHERE id = @uid", new { uid }), ct);
        if (string.IsNullOrEmpty(password) || !hasher.Verify(password, hash))
            throw new KiteException(ErrorCodes.BadCredentials, "Your password is not correct.", 403, "password");
    }
}

/// <summary>
/// Startup for installations set up before Phase 3, and for every start of a head office server: makes sure
/// this server has a Machine ID and a Nextdab check-in key, and loads (or creates) the business CA.
/// </summary>
public static class Phase3Startup
{
    public static async Task RunAsync(WebApplication app)
    {
        var server = app.Services.GetRequiredService<ServerContext>();
        if (!server.IsConfigured) return;
        var db = app.Services.GetRequiredService<KiteDb>();
        await db.InTransactionAsync(AccessScope.System, ActorContext.System, async t =>
        {
            if (server.InstallationId is null)
                await t.ExecuteAsync("UPDATE core.server SET installation_id = @mid WHERE is_self AND installation_id IS NULL", new { mid = MachineId.New() });
            await t.ExecuteAsync("INSERT INTO lic.control_state (checkin_key) VALUES (@k) ON CONFLICT (id) DO NOTHING", new { k = BranchLinkService.NewCheckinKey() });
        });
        await server.LoadAsync(db.DataSource);
        if (!server.IsHeadOffice) return;
        try { await app.Services.GetRequiredService<PkiService>().EnsureAsync(CancellationToken.None); }
        catch (Exception e) { app.Logger.LogError(e, "The business certificate authority could not be loaded; branch registration is unavailable until this is fixed."); }
    }
}
