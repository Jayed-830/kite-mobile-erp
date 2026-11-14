using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kite.Host.Infrastructure;
using Kite.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Kite.IntegrationTests;

/// <summary>
/// End-to-end tests of the Phase 2 server against a real PostgreSQL database
/// (blueprint 26.4 definition of done).
/// </summary>
[Collection("server")]
public sealed class ServerTests(KiteServerFixture fx)
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task AssertProblem(HttpResponseMessage r, HttpStatusCode status, string code)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == status, $"expected {(int)status} {code} but got {(int)r.StatusCode}: {body}");
        Assert.Contains($"\"code\":\"{code}\"", body, StringComparison.Ordinal);
    }

    private async Task<Guid> RoleIdAsync(string ownerToken, string code)
    {
        using var c = fx.Client(ownerToken);
        var roles = await c.GetFromJsonAsync<JsonElement>("/api/v1/roles");
        return roles.EnumerateArray().Single(r => r.GetProperty("code").GetString() == code).GetProperty("id").GetGuid();
    }

    private Task<Guid> HeadOfficeIdAsync() => Task.FromResult(fx.SetupResult.GetProperty("branchId").GetGuid());

    /// <summary>Creates a user with a temporary password and signs in once to set a real one.</summary>
    private async Task<(Guid Id, string Password)> CreateUserAsync(string username, string roleCode, Guid? branchId, string? ownerToken = null)
    {
        ownerToken ??= await fx.OwnerTokenAsync();
        var roleId = await RoleIdAsync(ownerToken, roleCode);
        using var c = fx.Client(ownerToken);
        var r = await c.PostAsJsonAsync("/api/v1/users", new
        {
            username, displayNameEn = username, displayNameAr = (string?)null, email = (string?)null, phone = (string?)null,
            temporaryPassword = "Temp-Pass-2026", preferredLanguage = "en", defaultBranchId = branchId,
            roles = new[] { new { roleId, branchId } },
        });
        Assert.True(r.StatusCode == HttpStatusCode.Created, await r.Content.ReadAsStringAsync());
        var id = (await Json(r)).GetProperty("id").GetGuid();

        var token = await fx.TokenAsync(username, "Temp-Pass-2026");
        using var uc = fx.Client(token);
        var change = await uc.PostAsJsonAsync("/api/v1/auth/password", new { currentPassword = "Temp-Pass-2026", newPassword = "Real-Pass-2026" });
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
        return (id, "Real-Pass-2026");
    }

    [PgFact]
    public void Every_endpoint_declares_its_access_rule()
    {
        var endpoints = fx.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToArray();
        Assert.NotEmpty(endpoints);
        var missing = endpoints.Where(e => e.Metadata.GetMetadata<KiteAccess>() is null).Select(e => e.RoutePattern.RawText).ToArray();
        Assert.True(missing.Length == 0, "Endpoints without an access declaration: " + string.Join(", ", missing));
    }

    [PgFact]
    public async Task Setup_creates_the_full_business_foundation_once()
    {
        using var c = fx.Client();
        var status = await c.GetFromJsonAsync<JsonElement>("/api/v1/setup/status");
        Assert.True(status.GetProperty("configured").GetBoolean());

        var again = await c.PostAsJsonAsync("/api/v1/setup", KiteServerFixture.SetupBody("WHATEVER"));
        await AssertProblem(again, HttpStatusCode.Conflict, "SET-001");

        Assert.Equal(5L, await fx.QueryDbAsync<long>("SELECT count(*) FROM tax.tax_code WHERE needs_accountant_review"));
        Assert.Equal(5.0m, await fx.QueryDbAsync<decimal>("SELECT r.rate_percent FROM tax.tax_rate r JOIN tax.tax_code c ON c.id = r.tax_code_id WHERE c.code = 'VAT-STD'"));
        Assert.Equal("inactive", await fx.QueryDbAsync<string>("SELECT status FROM tax.tax_code WHERE code = 'VAT-MARGIN'"));
        Assert.Equal((long)Kite.Accounting.ChartTemplate.Accounts.Count, await fx.QueryDbAsync<long>("SELECT count(*) FROM acct.account"));
        Assert.Equal(12L, await fx.QueryDbAsync<long>("SELECT count(*) FROM acct.fiscal_period"));
        Assert.Equal(8L, await fx.QueryDbAsync<long>("SELECT count(*) FROM iam.role WHERE is_system"));
        // Only the series setup made: other tests sharing this database add branches, and each branch gets its own series.
        Assert.Equal(8L, await fx.QueryDbAsync<long>("SELECT count(*) FROM core.number_series WHERE branch_id IS NULL OR branch_id = (SELECT id FROM core.branch WHERE is_head_office)"));
        Assert.True(await fx.QueryDbAsync<bool>("SELECT bool_and(origin_seq = rn) FROM (SELECT origin_seq, row_number() OVER (ORDER BY origin_seq) rn FROM sync.outbox) x"),
            "outbox sequence is gap-free");
        Assert.Equal(0L, await fx.QueryDbAsync<long>("SELECT count(*) FROM sync.outbox WHERE payload::text ILIKE '%argon2%'"));
    }

    [PgFact]
    public void Setup_code_check_is_case_insensitive_and_rejects_wrong_codes()
    {
        // The fixture's server is configured, so verify the code check directly.
        var store = new SetupCodeStore(Path.Combine(Path.GetTempPath(), "kite-code-" + Guid.NewGuid().ToString("N")));
        var code = store.GetOrCreate();
        Assert.True(store.Matches(code.ToLowerInvariant()));
        Assert.False(store.Matches("WRONG123"));
        store.Consume();
    }

    [PgFact]
    public async Task Owner_signs_in_and_sees_every_permission()
    {
        var token = await fx.OwnerTokenAsync();
        using var c = fx.Client(token);
        var me = await c.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        Assert.True(me.GetProperty("allBranches").GetBoolean());
        Assert.Equal(Kite.Platform.Permissions.All.Count, me.GetProperty("permissions").GetArrayLength());
        Assert.Equal("OMR", me.GetProperty("business").GetProperty("currency").GetString());
        Assert.Equal(3, me.GetProperty("business").GetProperty("decimals").GetInt32());
    }

    [PgFact]
    public async Task Wrong_password_is_refused_and_five_failures_lock_the_account_until_unlocked()
    {
        var owner = await fx.OwnerTokenAsync();
        var (id, password) = await CreateUserAsync("lock_me", "cashier", await HeadOfficeIdAsync(), owner);
        using var anon = fx.Client();
        for (var i = 0; i < 5; i++)
        {
            var bad = await anon.PostAsJsonAsync("/api/v1/auth/login", new { username = "lock_me", password = "nope-nope-nope" });
            await AssertProblem(bad, HttpStatusCode.Unauthorized, "AUTH-001");
        }
        var locked = await anon.PostAsJsonAsync("/api/v1/auth/login", new { username = "lock_me", password });
        await AssertProblem(locked, (HttpStatusCode)423, "AUTH-002");

        using var oc = fx.Client(owner);
        Assert.Equal(HttpStatusCode.NoContent, (await oc.PostAsync($"/api/v1/users/{id}/unlock", null)).StatusCode);
        await fx.LoginAsync("lock_me", password);
        Assert.True(await fx.QueryDbAsync<long>("SELECT count(*) FROM audit.audit_log WHERE action = 'auth.locked'") >= 1);
    }

    [PgFact]
    public async Task A_new_user_must_change_the_temporary_password_and_gets_only_their_permissions()
    {
        var owner = await fx.OwnerTokenAsync();
        var roleId = await RoleIdAsync(owner, "cashier");
        var branch = await HeadOfficeIdAsync();
        using (var oc = fx.Client(owner))
        {
            var r = await oc.PostAsJsonAsync("/api/v1/users", new
            {
                username = "cashier_amal", displayNameEn = "Amal", temporaryPassword = "Temp-Pass-2026", preferredLanguage = "ar",
                defaultBranchId = branch, roles = new[] { new { roleId, branchId = (Guid?)branch } },
            });
            Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        }
        var login = await fx.LoginAsync("cashier_amal", "Temp-Pass-2026");
        Assert.True(login.GetProperty("mustChangePassword").GetBoolean());
        using var c = fx.Client(login.GetProperty("accessToken").GetString());
        await AssertProblem(await c.GetAsync("/api/v1/users"), HttpStatusCode.Forbidden, "AUTH-009");
        Assert.Equal(HttpStatusCode.NoContent,
            (await c.PostAsJsonAsync("/api/v1/auth/password", new { currentPassword = "Temp-Pass-2026", newPassword = "Amal-Secure-2026" })).StatusCode);

        using var c2 = fx.Client(await fx.TokenAsync("cashier_amal", "Amal-Secure-2026"));
        await AssertProblem(await c2.GetAsync("/api/v1/users"), HttpStatusCode.Forbidden, "AUTH-403");
        var me = await c2.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        Assert.False(me.GetProperty("allBranches").GetBoolean());
        Assert.Equal(1, me.GetProperty("branches").GetArrayLength());
        Assert.Contains("POS.SELL", me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
    }

    [PgFact]
    public async Task A_user_manager_cannot_grant_permissions_they_do_not_hold()
    {
        var owner = await fx.OwnerTokenAsync();
        using (var oc = fx.Client(owner))
        {
            var r = await oc.PostAsJsonAsync("/api/v1/roles", new { code = "user_admin", nameEn = "User admin", nameAr = "مدير المستخدمين", permissions = new[] { "USERS.VIEW", "USERS.MANAGE" } });
            Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        }
        var (_, pw) = await CreateUserAsync("useradmin1", "user_admin", null, owner);
        var adminToken = await fx.TokenAsync("useradmin1", pw);
        var ownerRole = await RoleIdAsync(owner, "owner");
        using var ac = fx.Client(adminToken);
        var escalate = await ac.PostAsJsonAsync("/api/v1/users", new
        {
            username = "sneaky", displayNameEn = "Sneaky", temporaryPassword = "Temp-Pass-2026", preferredLanguage = "en",
            roles = new[] { new { roleId = ownerRole, branchId = (Guid?)null } },
        });
        await AssertProblem(escalate, HttpStatusCode.Forbidden, "AUTH-403");
        var roleEscalate = await ac.PostAsJsonAsync("/api/v1/roles", new { code = "x", nameEn = "x", nameAr = "x", permissions = new[] { "USERS.VIEW" } });
        await AssertProblem(roleEscalate, HttpStatusCode.Forbidden, "AUTH-403"); // lacks ROLES.MANAGE
    }

    [PgFact]
    public async Task The_owner_role_is_locked_and_the_last_owner_cannot_be_disabled()
    {
        var owner = await fx.OwnerTokenAsync();
        using var c = fx.Client(owner);
        var ownerRole = await RoleIdAsync(owner, "owner");
        var edit = await c.PutAsJsonAsync($"/api/v1/roles/{ownerRole}", new { nameEn = "Boss", nameAr = "الرئيس", permissions = Array.Empty<string>(), version = 1 });
        await AssertProblem(edit, HttpStatusCode.Conflict, "IAM-001");

        var users = await c.GetFromJsonAsync<JsonElement>("/api/v1/users");
        var me = users.EnumerateArray().Single(u => u.GetProperty("username").GetString() == KiteServerFixture.OwnerUsername);
        var disable = await c.PutAsJsonAsync($"/api/v1/users/{me.GetProperty("id").GetGuid()}", new
        {
            displayNameEn = "Owner", displayNameAr = "المالك", preferredLanguage = "en", status = "disabled",
            roles = new[] { new { roleId = ownerRole, branchId = (Guid?)null } }, version = me.GetProperty("version").GetInt32(),
        });
        await AssertProblem(disable, HttpStatusCode.Conflict, "IAM-002");
    }

    [PgFact]
    public async Task Module_switches_enforce_dependencies_and_block_switched_off_modules()
    {
        var owner = await fx.OwnerTokenAsync();
        using var c = fx.Client(owner);
        await AssertProblem(await c.PutAsJsonAsync("/api/v1/settings/modules", new { changes = new Dictionary<string, bool> { ["tradein"] = true } }),
            HttpStatusCode.BadRequest, "MOD-001");
        await AssertProblem(await c.PutAsJsonAsync("/api/v1/settings/modules", new { changes = new Dictionary<string, bool> { ["sales"] = false } }),
            HttpStatusCode.BadRequest, "MOD-002");

        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/accounting/accounts")).StatusCode);
        Assert.True((await c.PutAsJsonAsync("/api/v1/settings/modules", new { changes = new Dictionary<string, bool> { ["acct_ui"] = false } })).IsSuccessStatusCode);
        await AssertProblem(await c.GetAsync("/api/v1/accounting/accounts"), HttpStatusCode.Forbidden, "MOD-403");
        Assert.True((await c.PutAsJsonAsync("/api/v1/settings/modules", new { changes = new Dictionary<string, bool> { ["acct_ui"] = true } })).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/accounting/accounts")).StatusCode);
        Assert.True(await fx.QueryDbAsync<long>("SELECT count(*) FROM audit.audit_log WHERE action = 'settings.modules_changed'") >= 2);
    }

    [PgFact]
    public async Task Two_factor_sign_in_blocks_everything_until_the_code_is_entered()
    {
        var owner = await fx.OwnerTokenAsync();
        var (_, pw) = await CreateUserAsync("mfa_user", "accountant", null, owner);
        using (var c = fx.Client(await fx.TokenAsync("mfa_user", pw)))
        {
            var setup = await Json(await c.PostAsync("/api/v1/auth/2fa/setup", null));
            var secret = Base32.Decode(setup.GetProperty("secret").GetString()!);
            Assert.StartsWith("otpauth://totp/", setup.GetProperty("otpauthUri").GetString(), StringComparison.Ordinal);
            var enableCode = Totp.Code(secret, DateTimeOffset.UtcNow);
            Assert.Equal(HttpStatusCode.NoContent,
                (await c.PostAsJsonAsync("/api/v1/auth/2fa/enable", new { code = enableCode })).StatusCode);

            var login = await fx.LoginAsync("mfa_user", pw);
            Assert.True(login.GetProperty("mfaRequired").GetBoolean());
            using var pending = fx.Client(login.GetProperty("accessToken").GetString());
            await AssertProblem(await pending.GetAsync("/api/v1/settings/business"), HttpStatusCode.Unauthorized, "AUTH-004");
            await AssertProblem(await pending.PostAsJsonAsync("/api/v1/auth/mfa", new { code = "000000" == Totp.Code(secret, DateTimeOffset.UtcNow) ? "111111" : "000000" }),
                HttpStatusCode.Unauthorized, "AUTH-005");
            // The code that turned two-step on cannot be used again (RFC 6238 replay protection).
            await AssertProblem(await pending.PostAsJsonAsync("/api/v1/auth/mfa", new { code = enableCode }),
                HttpStatusCode.Unauthorized, "AUTH-005");
            // A code for a later time step is accepted (one step of clock drift is allowed).
            var usedStep = await fx.QueryDbAsync<long>("SELECT totp_last_step FROM iam.\"user\" WHERE username = 'mfa_user'");
            Assert.Equal(HttpStatusCode.NoContent,
                (await pending.PostAsJsonAsync("/api/v1/auth/mfa", new { code = Totp.CodeForStep(secret, usedStep + 1) })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await pending.GetAsync("/api/v1/settings/business")).StatusCode);
            Assert.Equal(usedStep + 1, await fx.QueryDbAsync<long>("SELECT totp_last_step FROM iam.\"user\" WHERE username = 'mfa_user'"));
        }
        Assert.Equal(0L, await fx.QueryDbAsync<long>("SELECT count(*) FROM iam.\"user\" WHERE username = 'mfa_user' AND totp_secret_enc IS NULL"));
    }

    [PgFact]
    public async Task Refresh_tokens_rotate_and_old_tokens_stop_working()
    {
        var login = await fx.LoginAsync(KiteServerFixture.OwnerUsername, KiteServerFixture.OwnerPassword);
        var oldAccess = login.GetProperty("accessToken").GetString();
        var oldRefresh = login.GetProperty("refreshToken").GetString();
        using var anon = fx.Client();
        var refreshed = await anon.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = oldRefresh });
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var fresh = await Json(refreshed);
        Assert.NotEqual(oldAccess, fresh.GetProperty("accessToken").GetString());

        await AssertProblem(await anon.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = oldRefresh }), HttpStatusCode.Unauthorized, "AUTH-401");
        using var oldClient = fx.Client(oldAccess);
        await AssertProblem(await oldClient.GetAsync("/api/v1/auth/me"), HttpStatusCode.Unauthorized, "AUTH-401");
        using var newClient = fx.Client(fresh.GetProperty("accessToken").GetString());
        Assert.Equal(HttpStatusCode.OK, (await newClient.GetAsync("/api/v1/auth/me")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await newClient.PostAsync("/api/v1/auth/logout", null)).StatusCode);
        await AssertProblem(await newClient.GetAsync("/api/v1/auth/me"), HttpStatusCode.Unauthorized, "AUTH-401");
    }

    [PgFact]
    public async Task Terminals_need_approval_and_pin_sign_in_works_only_on_an_active_terminal()
    {
        var owner = await fx.OwnerTokenAsync();
        var branch = await HeadOfficeIdAsync();
        var (_, pw) = await CreateUserAsync("pin_cashier", "cashier", branch, owner);
        using (var uc = fx.Client(await fx.TokenAsync("pin_cashier", pw)))
            Assert.Equal(HttpStatusCode.NoContent, (await uc.PostAsJsonAsync("/api/v1/auth/pin", new { pin = "4826", currentPassword = pw })).StatusCode);

        using var oc = fx.Client(owner);
        var req = await oc.PostAsJsonAsync("/api/v1/terminals", new { branchId = branch, code = "T07", name = "Till 7", kind = "windows" });
        Assert.Equal(HttpStatusCode.Created, req.StatusCode);
        var terminalId = (await Json(req)).GetProperty("id").GetGuid();

        using var anon = fx.Client();
        await AssertProblem(await anon.PostAsJsonAsync("/api/v1/auth/pin-login", new { username = "pin_cashier", pin = "4826", terminalId }),
            HttpStatusCode.Forbidden, "AUTH-007");
        Assert.Equal(HttpStatusCode.OK, (await oc.PostAsJsonAsync($"/api/v1/terminals/{terminalId}/approve", new { emergencyEnabled = true })).StatusCode);
        var pinLogin = await anon.PostAsJsonAsync("/api/v1/auth/pin-login", new { username = "pin_cashier", pin = "4826", terminalId });
        Assert.Equal(HttpStatusCode.OK, pinLogin.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await oc.PostAsync($"/api/v1/terminals/{terminalId}/revoke", null)).StatusCode);
        using var revoked = fx.Client((await Json(pinLogin)).GetProperty("accessToken").GetString());
        await AssertProblem(await revoked.GetAsync("/api/v1/auth/me"), HttpStatusCode.Unauthorized, "AUTH-401");
    }

    [PgFact]
    public async Task Business_edits_use_optimistic_concurrency_and_are_audited()
    {
        var owner = await fx.OwnerTokenAsync();
        using var c = fx.Client(owner);
        var biz = await c.GetFromJsonAsync<JsonElement>("/api/v1/settings/business");
        var version = biz.GetProperty("version").GetInt32();
        object Body(int v) => new
        {
            legalNameEn = "Al Noor Mobiles LLC", legalNameAr = "شركة النور للهواتف", tradeNameEn = "Al Noor", tradeNameAr = "النور",
            crNumber = "1234567", vatNumber = "OM1100223344", phone = "+968 2400 0000", pricesIncludeTax = true, version = v,
        };
        await AssertProblem(await c.PutAsJsonAsync("/api/v1/settings/business", Body(version + 5)), HttpStatusCode.PreconditionFailed, "GEN-412");
        var ok = await c.PutAsJsonAsync("/api/v1/settings/business", Body(version));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(version + 1, (await Json(ok)).GetProperty("version").GetInt32());
    }

    [PgFact]
    public async Task The_audit_chain_verifies_and_every_write_produced_a_sync_event()
    {
        var owner = await fx.OwnerTokenAsync();
        using var c = fx.Client(owner);
        var verify = await Json(await c.PostAsync("/api/v1/audit/verify", null));
        Assert.True(verify.GetProperty("ok").GetBoolean());
        Assert.True(verify.GetProperty("rowsChecked").GetInt64() > 0);
        var rows = await c.GetFromJsonAsync<JsonElement>("/api/v1/audit?limit=10");
        Assert.InRange(rows.GetArrayLength(), 1, 10);
        Assert.True(await fx.QueryDbAsync<long>("SELECT count(*) FROM sync.outbox WHERE event_type = 'core.business_created'") == 1);
    }

    [PgFact]
    public async Task Health_endpoints_report_readiness_and_unknown_api_routes_are_404()
    {
        using var c = fx.Client();
        var ready = await c.GetFromJsonAsync<JsonElement>("/health/ready");
        Assert.True(ready.GetProperty("configured").GetBoolean());
        // The schema version is the number of the newest embedded migration, so adding one never needs a test edit.
        var newest = typeof(Kite.Persistence.Migrator).Assembly.GetManifestResourceNames()
            .Select(n => System.Text.RegularExpressions.Regex.Match(n, @"V(\d{4})__"))
            .Where(m => m.Success).Max(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(newest >= 10);
        Assert.Equal(newest, ready.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/v1/does-not-exist")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/users")).StatusCode);
    }
}
