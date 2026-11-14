using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kite.Host.Features;
using Kite.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Kite.IntegrationTests;

/// <summary>
/// Phase 3 against the real server and PostgreSQL: licence import and offline enforcement, Nextdab signed
/// commands, branch registration with a one-time token, mutual-TLS heartbeat, revocation, per-branch permissions.
/// Every test leaves a full, valid licence in place so the other tests in the collection are not affected.
/// </summary>
[Collection("server")]
public sealed class Phase3Tests(KiteServerFixture fx)
{
    private static async Task AssertProblem(HttpResponseMessage r, HttpStatusCode status, string code)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == status, $"expected {(int)status} {code} but got {(int)r.StatusCode}: {body}");
        Assert.Contains($"\"code\":\"{code}\"", body, StringComparison.Ordinal);
    }

    private async Task ImportAsync(HttpClient owner, string file) =>
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync("/api/v1/licence/import", new { fileText = file })).StatusCode);

    private async Task FullLicenceAsync(HttpClient owner) => await ImportAsync(owner, fx.MakeLicence(KiteServerFixture.FutureDate(365), branches: 20, terminals: 50));

    [PgFact]
    public async Task A_licence_from_Nextdab_activates_and_forged_or_foreign_licences_are_refused()
    {
        using var owner = fx.Client(await fx.OwnerTokenAsync());
        var good = fx.MakeLicence(KiteServerFixture.FutureDate(365), branches: 4, terminals: 7);
        var forged = good.Replace("\"terminals\":7", "\"terminals\":700", StringComparison.Ordinal);
        await AssertProblem(await owner.PostAsJsonAsync("/api/v1/licence/import", new { fileText = forged }), HttpStatusCode.BadRequest, "LIC-011");
        await AssertProblem(await owner.PostAsJsonAsync("/api/v1/licence/import", new { fileText = fx.MakeLicence(KiteServerFixture.FutureDate(30), mid: "FFFF-FFFF-FFFF-FFFF") }),
            HttpStatusCode.BadRequest, "LIC-013");
        await ImportAsync(owner, good);
        var lic = await owner.GetFromJsonAsync<JsonElement>("/api/v1/licence");
        Assert.Equal("active", lic.GetProperty("state").GetString());
        Assert.Equal(4, lic.GetProperty("branchesAllowed").GetInt32());
        Assert.Equal(7, lic.GetProperty("terminalsAllowed").GetInt32());
        Assert.Equal(fx.InstallationId, lic.GetProperty("installationId").GetString());
        // An older licence cannot replace a newer one.
        var older = fx.MakeLicence(KiteServerFixture.FutureDate(900));
        await ImportAsync(owner, fx.MakeLicence(KiteServerFixture.FutureDate(365)));
        await AssertProblem(await owner.PostAsJsonAsync("/api/v1/licence/import", new { fileText = older }), HttpStatusCode.Conflict, "LIC-015");
        Assert.Equal(1L, await fx.QueryDbAsync<long>("SELECT count(*) FROM lic.licence WHERE is_current"));
        // Everybody signed in sees the state for banners.
        var status = await owner.GetFromJsonAsync<JsonElement>("/api/v1/licence/status");
        Assert.True(status.GetProperty("canTransact").GetBoolean());
    }

    [PgFact]
    public async Task An_expired_licence_stops_new_business_offline_and_grace_keeps_it_going()
    {
        using var owner = fx.Client(await fx.OwnerTokenAsync());
        await FullLicenceAsync(owner);
        var branch = await CreateBranchAsync(owner, "EX" + Random.Shared.Next(100, 999));
        // Ended 10 days ago with 30 days of grace: still allowed, state "grace".
        await ImportAsync(owner, fx.MakeLicence(KiteServerFixture.FutureDate(-10), graceDays: 30));
        Assert.Equal("grace", (await owner.GetFromJsonAsync<JsonElement>("/api/v1/licence/status")).GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/v1/branches/{branch}/registration-tokens", null)).StatusCode);
        // Ended 40 days ago: expired. Adding a branch server is refused; reading keeps working.
        await ImportAsync(owner, fx.MakeLicence(KiteServerFixture.FutureDate(-40), graceDays: 30));
        await AssertProblem(await owner.PostAsync($"/api/v1/branches/{branch}/registration-tokens", null), HttpStatusCode.Forbidden, "LIC-002");
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v1/users")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v1/audit")).StatusCode);
        await FullLicenceAsync(owner);
    }

    [PgFact]
    public async Task Licence_limits_apply_to_branches_and_modules()
    {
        using var owner = fx.Client(await fx.OwnerTokenAsync());
        var active = await fx.QueryDbAsync<long>("SELECT count(*) FROM core.branch WHERE status = 'active'");
        var modules = Kite.Platform.ModuleCatalog.All.Select(m => m.Code).Where(c => c != "notif").ToArray();
        await ImportAsync(owner, fx.MakeLicence(KiteServerFixture.FutureDate(200), branches: (int)active, modules: modules));
        await AssertProblem(await owner.PostAsJsonAsync("/api/v1/branches", new { code = "LIM1", nameEn = "Limit", nameAr = "حد" }), HttpStatusCode.Forbidden, "LIC-004");
        var list = await owner.GetFromJsonAsync<JsonElement>("/api/v1/settings/modules");
        var notif = list.EnumerateArray().Single(m => m.GetProperty("code").GetString() == "notif");
        Assert.Equal("licence", notif.GetProperty("locked").GetString());
        await AssertProblem(await owner.PutAsJsonAsync("/api/v1/settings/modules", new { changes = new Dictionary<string, bool> { ["notif"] = true } }),
            HttpStatusCode.Forbidden, "LIC-005");
        await FullLicenceAsync(owner);
    }

    [PgFact]
    public async Task Nextdab_can_switch_KITE_off_and_on_with_signed_commands_carried_out_once()
    {
        using var owner = fx.Client(await fx.OwnerTokenAsync());
        await FullLicenceAsync(owner);
        var licence = fx.Factory.Services.GetRequiredService<LicenceService>();
        var seq = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var off = fx.MakeCommand("suspend", new { message = "Switched off by Nextdab (test)", message_ar = "تم الإيقاف" }, seq);
        var ack = await licence.HandleCommandAsync(off, CancellationToken.None);
        Assert.True(ack!["ok"]!.GetValue<bool>());
        await AssertProblem(await owner.GetAsync("/api/v1/users"), HttpStatusCode.Forbidden, "LIC-003");
        var st = await owner.GetFromJsonAsync<JsonElement>("/api/v1/licence/status");
        Assert.Equal("suspended", st.GetProperty("state").GetString());
        Assert.Equal("Switched off by Nextdab (test)", st.GetProperty("suspendMessage").GetString());

        // The same command again (standing settings come back at every check-in) is not carried out twice.
        Assert.NotNull(await licence.HandleCommandAsync(off, CancellationToken.None)); // not yet acknowledged: ack repeated
        await licence.MarkAckedAsync([off["id"]!.GetValue<string>()], CancellationToken.None);
        Assert.Null(await licence.HandleCommandAsync(off, CancellationToken.None));

        // A forged resume is ignored; a genuine resume with a higher seq applies; an older suspend after it does not.
        var forged = fx.MakeCommand("resume", new { }, seq + 1);
        forged["seq"] = seq + 2;
        Assert.False((await licence.HandleCommandAsync(forged, CancellationToken.None))!["ok"]!.GetValue<bool>());
        await AssertProblem(await owner.GetAsync("/api/v1/users"), HttpStatusCode.Forbidden, "LIC-003");
        Assert.True((await licence.HandleCommandAsync(fx.MakeCommand("resume", new { }, seq + 3), CancellationToken.None))!["ok"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v1/users")).StatusCode);
        await licence.HandleCommandAsync(fx.MakeCommand("suspend", new { message = "old" }, seq - 1000), CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v1/users")).StatusCode);

        // Messages and a licence delivered by command.
        var msg = fx.MakeCommand("message", new { title = "Renewal", text = "Your licence renews next month", level = "info", to = "all" }, seq + 4);
        Assert.True((await licence.HandleCommandAsync(msg, CancellationToken.None))!["ok"]!.GetValue<bool>());
        var messages = (await owner.GetFromJsonAsync<JsonElement>("/api/v1/licence/status")).GetProperty("messages");
        Assert.Contains(messages.EnumerateArray(), m => m.GetProperty("title").GetString() == "Renewal");
        var renew = fx.MakeCommand("license", new { file = fx.MakeLicence(KiteServerFixture.FutureDate(400), terminals: 12) }, seq + 5);
        Assert.True((await licence.HandleCommandAsync(renew, CancellationToken.None))!["ok"]!.GetValue<bool>());
        Assert.Equal(12, (await owner.GetFromJsonAsync<JsonElement>("/api/v1/licence")).GetProperty("terminalsAllowed").GetInt32());
        Assert.Equal("nextdab", await fx.QueryDbAsync<string>("SELECT source FROM lic.licence WHERE is_current"));
    }

    [PgFact]
    public async Task The_checkin_report_carries_no_customer_data()
    {
        var report = await fx.Factory.Services.GetRequiredService<NextdabCheckinService>().ReportAsync(CancellationToken.None);
        Assert.Equal("kite-mobile-erp", report["product"]!.GetValue<string>());
        Assert.Equal("Al Noor Mobiles LLC", report["company"]!.GetValue<string>());
        Assert.NotNull(report["license"]);
        Assert.NotNull(report["counts"]);
        var text = report.ToJsonString();
        Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("totp", text, StringComparison.OrdinalIgnoreCase);
    }

    [PgFact]
    public async Task A_branch_server_registers_with_a_one_time_token_heartbeats_over_mutual_TLS_and_can_be_revoked()
    {
        using var owner = fx.Client(await fx.OwnerTokenAsync());
        await FullLicenceAsync(owner);
        var branch = await CreateBranchAsync(owner, "RG" + Random.Shared.Next(100, 999));
        var token = await TokenAsync(owner, branch);
        using var tls = fx.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var schema = await fx.QueryDbAsync<int>("SELECT max(version) FROM meta.schema_info");

        // Sync requests only on TLS.
        var (csr, key) = BusinessCa.CreateBranchRequest("TEST-BRANCH-PC");
        await AssertProblem(await owner.PostAsJsonAsync("/sync/v1/register", new { token, csrPem = csr, serverName = "x", appVersion = "0.3.0", schemaVersion = schema }),
            HttpStatusCode.NotFound, "GEN-404");
        // Wrong database version.
        await AssertProblem(await tls.PostAsJsonAsync("/sync/v1/register", new { token, csrPem = csr, serverName = "x", appVersion = "0.3.0", schemaVersion = schema - 1 }),
            HttpStatusCode.Conflict, "SRV-004");

        var reg = await tls.PostAsJsonAsync("/sync/v1/register", new { token, csrPem = csr, serverName = "TEST-BRANCH-PC", appVersion = "0.3.0", schemaVersion = schema });
        Assert.True(reg.IsSuccessStatusCode, await reg.Content.ReadAsStringAsync());
        var body = await reg.Content.ReadFromJsonAsync<JsonElement>();
        var serverId = body.GetProperty("serverId").GetGuid();
        var snapshot = body.GetProperty("snapshot").GetProperty("tables");
        Assert.True(snapshot.GetProperty("iam.\"user\"").GetArrayLength() >= 1);
        Assert.True(snapshot.GetProperty("core.number_series").GetArrayLength() >= 8);
        Assert.Contains(snapshot.GetProperty("core.server").EnumerateArray(), s => s.GetProperty("id").GetGuid() == serverId);
        Assert.All(snapshot.GetProperty("iam.\"user\"").EnumerateArray(), u => Assert.Equal(JsonValueKind.Null, u.GetProperty("totp_secret_enc").ValueKind));
        Assert.Equal(JsonValueKind.Null, snapshot.GetProperty("pki.authority")[0].GetProperty("key_file").ValueKind);
        var cert = BusinessCa.WithPrivateKey(BusinessCa.FromPem(body.GetProperty("certPem").GetString()!), key);

        // The token works once.
        await AssertProblem(await tls.PostAsJsonAsync("/sync/v1/register", new { token, csrPem = BusinessCa.CreateBranchRequest("y").CsrPem, serverName = "y", appVersion = "0.3.0", schemaVersion = schema }),
            HttpStatusCode.Forbidden, "SRV-002");
        // A second token for a branch that already has a server is refused at registration.
        var token2 = await TokenAsync(owner, branch);
        await AssertProblem(await tls.PostAsJsonAsync("/sync/v1/register", new { token = token2, csrPem = BusinessCa.CreateBranchRequest("z").CsrPem, serverName = "z", appVersion = "0.3.0", schemaVersion = schema }),
            HttpStatusCode.Conflict, "SRV-003");

        // Heartbeat with the client certificate: the branch gets the licence and the revocation list number.
        var (status, hb) = await HeartbeatAsync(cert, schema);
        Assert.Equal(200, status);
        Assert.False(string.IsNullOrEmpty(hb.GetProperty("licenceFile").GetString()));
        // Without a certificate, or with one from another CA: refused.
        Assert.Equal(401, (await HeartbeatAsync(null, schema)).Status);
        using (var stranger = BusinessCa.CreateAuthority(Guid.NewGuid(), "Stranger", DateTimeOffset.UtcNow))
        {
            var (c2, k2) = BusinessCa.CreateBranchRequest("evil");
            var foreign = BusinessCa.WithPrivateKey(BusinessCa.SignBranchRequest(stranger, c2, Guid.NewGuid(), branch, serverId, DateTimeOffset.UtcNow), k2);
            Assert.Equal(401, (await HeartbeatAsync(foreign, schema)).Status);
        }
        var servers = await owner.GetFromJsonAsync<JsonElement>("/api/v1/servers");
        var mine = servers.GetProperty("servers").EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == serverId);
        Assert.True(mine.GetProperty("online").GetBoolean());
        Assert.Equal("0.3.0", mine.GetProperty("appVersion").GetString());

        // Replace server: revocation is immediate and published in a new revocation list.
        var crlBefore = await fx.QueryDbAsync<long>("SELECT coalesce(max(number), 0) FROM pki.crl");
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsJsonAsync($"/api/v1/servers/{serverId}/revoke", new { reason = "server_replaced" })).StatusCode);
        var (revokedStatus, revoked) = await HeartbeatAsync(cert, schema);
        Assert.Equal(403, revokedStatus);
        Assert.Equal("SRV-009", revoked.GetProperty("code").GetString());
        Assert.True(await fx.QueryDbAsync<long>("SELECT coalesce(max(number), 0) FROM pki.crl") > crlBefore);
        Assert.Equal("server_replaced", await fx.QueryDbAsync<string>($"SELECT revoked_reason FROM pki.certificate WHERE server_id = '{serverId}'"));
        // The replacement server registers with a new token.
        var token3 = await TokenAsync(owner, branch);
        var again = await tls.PostAsJsonAsync("/sync/v1/register", new { token = token3, csrPem = BusinessCa.CreateBranchRequest("new").CsrPem, serverName = "NEW-PC", appVersion = "0.3.0", schemaVersion = schema });
        Assert.True(again.IsSuccessStatusCode, await again.Content.ReadAsStringAsync());

        // Deactivating the branch revokes its server too.
        var version = await fx.QueryDbAsync<int>($"SELECT version FROM core.branch WHERE id = '{branch}'");
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsJsonAsync($"/api/v1/branches/{branch}",
            new { nameEn = "Closed", nameAr = "مغلق", status = "inactive", version })).StatusCode);
        Assert.Equal(0L, await fx.QueryDbAsync<long>($"SELECT count(*) FROM core.server WHERE branch_id = '{branch}' AND status = 'active'"));
        Assert.Equal(0L, await fx.QueryDbAsync<long>($"SELECT count(*) FROM pki.certificate WHERE branch_id = '{branch}' AND revoked_at IS NULL"));
    }

    [PgFact]
    public async Task A_role_granted_for_one_branch_works_in_that_branch_only()
    {
        using var owner = fx.Client(await fx.OwnerTokenAsync());
        await FullLicenceAsync(owner);
        var b1 = await fx.QueryDbAsync<Guid>("SELECT id FROM core.branch WHERE is_head_office");
        var b2 = await CreateBranchAsync(owner, "PB" + Random.Shared.Next(100, 999));
        var managerRole = (await owner.GetFromJsonAsync<JsonElement>("/api/v1/roles")).EnumerateArray().Single(r => r.GetProperty("code").GetString() == "branch_manager").GetProperty("id").GetGuid();
        var username = "mgr" + Random.Shared.Next(1000, 9999);
        var create = await owner.PostAsJsonAsync("/api/v1/users", new
        {
            username, displayNameEn = "Branch two manager", temporaryPassword = "Temp-Sohar-2026!", preferredLanguage = "en",
            roles = new[] { new { roleId = managerRole, branchId = (Guid?)b2 } },
        });
        Assert.True(create.IsSuccessStatusCode, await create.Content.ReadAsStringAsync());
        // First sign-in forces a password change.
        var login = await fx.LoginAsync(username, "Temp-Sohar-2026!");
        using (var tmp = fx.Client(login.GetProperty("accessToken").GetString()))
            Assert.Equal(HttpStatusCode.NoContent, (await tmp.PostAsJsonAsync("/api/v1/auth/password", new { currentPassword = "Temp-Sohar-2026!", newPassword = "Sohar-Souq-2026!" })).StatusCode);
        using var mgr = fx.Client(await fx.TokenAsync(username, "Sohar-Souq-2026!"));

        var t1 = await NewTerminalAsync(owner, b1);
        var t2 = await NewTerminalAsync(owner, b2);
        Assert.Equal(HttpStatusCode.NotFound, (await mgr.PostAsJsonAsync($"/api/v1/terminals/{t1}/approve", new { })).StatusCode); // not visible from branch two
        Assert.Equal(HttpStatusCode.OK, (await mgr.PostAsJsonAsync($"/api/v1/terminals/{t2}/approve", new { })).StatusCode);
        // Registering a branch server needs SERVERS.MANAGE in that branch: a branch manager does not have it.
        await AssertProblem(await mgr.PostAsync($"/api/v1/branches/{b2}/registration-tokens", null), HttpStatusCode.Forbidden, "AUTH-403");
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/v1/terminals/{t2}/revoke", null)).StatusCode);
    }

    [PgFact]
    public async Task Backups_report_when_they_are_not_configured()
    {
        using var owner = fx.Client(await fx.OwnerTokenAsync());
        var status = await owner.GetFromJsonAsync<JsonElement>("/api/v1/backups");
        Assert.False(status.GetProperty("configured").GetBoolean());
        Assert.True(status.GetProperty("alert").GetBoolean());
        await AssertProblem(await owner.PostAsJsonAsync("/api/v1/backups/run", new { kind = "logical" }), HttpStatusCode.Conflict, "BAK-001");
        await AssertProblem(await owner.PostAsJsonAsync("/api/v1/backups/recovery-key", new { password = "wrong" }), HttpStatusCode.Forbidden, "AUTH-001");
        var key = await owner.PostAsJsonAsync("/api/v1/backups/recovery-key", new { password = KiteServerFixture.OwnerPassword });
        var k = await key.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Matches("^[A-Z2-7]{4}(-[A-Z2-7]{1,4}){12}$", k.GetProperty("recoveryKey").GetString());
        Assert.Equal(1L, await fx.QueryDbAsync<long>("SELECT least(count(*), 1) FROM audit.audit_log WHERE action = 'backup.recovery_key_shown'"));
    }

    [Fact]
    public void Backup_retention_keeps_daily_weekly_monthly_and_yearly_copies()
    {
        var now = new DateTime(2026, 11, 15, 2, 0, 0, DateTimeKind.Utc);
        var stamps = Enumerable.Range(0, 800).Select(d => now.AddDays(-d)).ToList();
        var keep = BackupService.Keep(stamps, "logical", now);
        Assert.Contains(now, keep);
        Assert.All(stamps.Where(s => now - s <= TimeSpan.FromDays(29)), s => Assert.Contains(s, keep));   // every day for 30 days
        Assert.True(keep.Count < 30 + 12 + 12 + 3 + 1);
        Assert.Contains(keep, s => now - s > TimeSpan.FromDays(400));                                     // a yearly copy
        var baseKeep = BackupService.Keep(stamps, "base", now);
        Assert.Equal(8, baseKeep.Count);                                                                  // 7 days (+ today)
    }

    // ---------------------------------------------------------------------------------------------- helpers
    private static async Task<Guid> CreateBranchAsync(HttpClient owner, string code)
    {
        var r = await owner.PostAsJsonAsync("/api/v1/branches", new { code, nameEn = "Branch " + code, nameAr = "فرع " + code });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<string> TokenAsync(HttpClient owner, Guid branch)
    {
        var r = await owner.PostAsync($"/api/v1/branches/{branch}/registration-tokens", null);
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Matches("^\\d{4}(-\\d{4}){4}$", body.GetProperty("fingerprintCode").GetString());
        return body.GetProperty("token").GetString()!;
    }

    private static async Task<Guid> NewTerminalAsync(HttpClient owner, Guid branch)
    {
        var r = await owner.PostAsJsonAsync("/api/v1/terminals", new { branchId = branch, code = "P" + Random.Shared.Next(100, 999), name = "Till", kind = "windows" });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    /// <summary>Sends a heartbeat through the in-memory server as if over TLS with this client certificate.</summary>
    private async Task<(int Status, JsonElement Body)> HeartbeatAsync(System.Security.Cryptography.X509Certificates.X509Certificate2? cert, int schema)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new HeartbeatRequest("0.3.0", schema, DateTimeOffset.UtcNow, 0, 0, null, null, null, 0, 0, 0), KiteServerFixture.JsonOptions);
        var ctx = await fx.Factory.Server.SendAsync(c =>
        {
            c.Request.Method = "POST";
            c.Request.Scheme = "https";
            c.Request.Host = new HostString("localhost");
            c.Request.Path = "/sync/v1/heartbeat";
            c.Request.ContentType = "application/json";
            c.Request.Body = new MemoryStream(json);
            c.Request.ContentLength = json.Length;
            c.Connection.ClientCertificate = cert;
            // TestServer reports "no body" for a hand-built request unless told otherwise; minimal APIs then refuse to bind it.
            c.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new HasBody());
        });
        if (ctx.Response.Body.CanSeek) ctx.Response.Body.Position = 0;
        var text = await new StreamReader(ctx.Response.Body, Encoding.UTF8).ReadToEndAsync();
        return (ctx.Response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }
    private sealed class HasBody : Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature { public bool CanHaveBody => true; }
}
