using Kite.Host.Infrastructure;
using Kite.Identity;
using Kite.Persistence;
using Kite.Platform;

namespace Kite.Host.Features;

public sealed record LoginRequest(string Username, string Password, Guid? TerminalId = null, string? DeviceKey = null);
public sealed record PinLoginRequest(string Username, string Pin, Guid TerminalId, string? DeviceKey = null);
public sealed record RefreshRequest(string? RefreshToken);
public sealed record MfaRequest(string Code);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record SetPinRequest(string Pin, string CurrentPassword);
public sealed record DisableTotpRequest(string CurrentPassword);

public sealed record TokenResponse(
    string AccessToken, DateTimeOffset AccessExpiresAt, string RefreshToken, DateTimeOffset RefreshExpiresAt,
    bool MfaRequired, bool MustChangePassword);

/// <summary>Sign-in, sessions, password, PIN and two-factor authentication (blueprint 18.2).</summary>
public static class AuthEndpoints
{
    public const string RefreshCookie = "kite_rt";

    private sealed record UserRow(
        Guid Id, string Username, string PasswordHash, string? PinHash, string Status, int FailedLoginCount,
        DateTime? LockedUntil, bool TotpEnabled, byte[]? TotpSecretEnc, bool MustChangePassword, long? TotpLastStep);

    // Spends the same time as a real check when the username does not exist.
    private static string? _dummyHash;

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/auth");

        g.MapPost("/login", async (LoginRequest req, HttpContext http, KiteDb db, ServerContext server, IPasswordHasher hasher,
            LockoutPolicy lockout, SessionPolicy policy, IClock clock, CancellationToken ct) =>
        {
            RequireConfigured(server);
            var user = await FindUser(db, server, req.Username, ct);
            if (user is null)
            {
                _dummyHash ??= hasher.Hash("not-a-real-password");
                hasher.Verify(req.Password ?? "", _dummyHash);
                throw BadCredentials();
            }
            await EnsureNotLocked(user, clock);
            if (!hasher.Verify(req.Password ?? "", user.PasswordHash))
            {
                await RecordFailure(db, http, user, lockout, clock, "password", ct);
                throw BadCredentials();
            }
            if (user.Status != "active")
                throw new KiteException(ErrorCodes.AccountDisabled, "This account is disabled. Ask your manager.", 403);
            if (req.TerminalId is { } terminalId) await EnsureTerminalActive(db, terminalId, req.DeviceKey, ct);

            var rehash = hasher.NeedsRehash(user.PasswordHash) ? hasher.Hash(req.Password!) : null;
            var tokens = await CreateSession(db, http, user, req.TerminalId, "password", !user.TotpEnabled, policy, clock, rehash, ct);
            SetRefreshCookie(http, tokens);
            return Results.Ok(tokens);
        }).AllowEveryone();

        g.MapPost("/pin-login", async (PinLoginRequest req, HttpContext http, KiteDb db, ServerContext server, IPasswordHasher hasher,
            LockoutPolicy lockout, SessionPolicy policy, IClock clock, CancellationToken ct) =>
        {
            RequireConfigured(server);
            await EnsureTerminalActive(db, req.TerminalId, req.DeviceKey, ct);
            var user = await FindUser(db, server, req.Username, ct) ?? throw BadCredentials();
            await EnsureNotLocked(user, clock);
            if (user.PinHash is null) throw new KiteException(ErrorCodes.PinNotSet, "This user has no PIN. Sign in with the password.", 400);
            if (!hasher.Verify(req.Pin ?? "", user.PinHash))
            {
                await RecordFailure(db, http, user, lockout, clock, "pin", ct);
                throw BadCredentials();
            }
            if (user.Status != "active") throw new KiteException(ErrorCodes.AccountDisabled, "This account is disabled.", 403);
            // A PIN never replaces two-factor: users with TOTP still have to enter a code.
            var tokens = await CreateSession(db, http, user, req.TerminalId, "pin", !user.TotpEnabled, policy, clock, null, ct);
            SetRefreshCookie(http, tokens);
            return Results.Ok(tokens);
        }).AllowEveryone();

        g.MapPost("/refresh", async (RefreshRequest? req, HttpContext http, KiteDb db, SessionPolicy policy, IClock clock, CancellationToken ct) =>
        {
            var token = req?.RefreshToken ?? http.Request.Cookies[RefreshCookie];
            if (string.IsNullOrEmpty(token)) throw new KiteException(ErrorCodes.Unauthenticated, "Sign in required.", 401);
            var now = clock.UtcNow;
            var access = Tokens.New();
            var refresh = Tokens.New();
            var result = await db.InTransactionAsync<TokenResponse?>(AccessScope.System, ActorContext.System, async t =>
            {
                var s = await t.QuerySingleOrDefaultAsync<(Guid Id, Guid UserId, DateTime RefreshExpiresAt, DateTime? RevokedAt, bool Mfa, string Status, bool MustChange)>("""
                    SELECT s.id, s.user_id, s.refresh_expires_at, s.revoked_at, s.mfa_satisfied, u.status, u.must_change_password
                      FROM iam.session s JOIN iam."user" u ON u.id = s.user_id
                     WHERE s.refresh_hash = @h FOR UPDATE OF s
                    """, new { h = Tokens.Hash(token) });
                if (s.Id == Guid.Empty || s.RevokedAt is not null || s.Status != "active"
                    || new DateTimeOffset(DateTime.SpecifyKind(s.RefreshExpiresAt, DateTimeKind.Utc)) <= now)
                    return null;
                var accessExp = now + policy.AccessLifetime;
                var refreshExp = now + policy.RefreshLifetime;
                await t.ExecuteAsync("""
                    UPDATE iam.session SET access_hash = @ah, refresh_hash = @rh, access_expires_at = @ae,
                           refresh_expires_at = @re, last_seen_at = now() WHERE id = @id
                    """, new { ah = Tokens.Hash(access), rh = Tokens.Hash(refresh), ae = accessExp, re = refreshExp, id = s.Id });
                return new TokenResponse(access, accessExp, refresh, refreshExp, !s.Mfa, s.MustChange);
            }, ct) ?? throw new KiteException(ErrorCodes.Unauthenticated, "Your session has ended. Sign in again.", 401);
            SetRefreshCookie(http, result);
            return Results.Ok(result);
        }).AllowEveryone();

        g.MapPost("/logout", async (HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            var sid = http.User.SessionIdOf();
            await db.InTransactionAsync(AccessScope.System, http.ActorOf(), async t =>
            {
                await t.ExecuteAsync("UPDATE iam.session SET revoked_at = now(), revoked_reason = 'logout' WHERE id = @sid AND revoked_at IS NULL", new { sid });
                await t.AuditAsync(new AuditEntry("auth.logout", "session", sid));
            }, ct);
            http.Response.Cookies.Delete(RefreshCookie, new CookieOptions { Path = "/api/v1/auth" });
            return Results.NoContent();
        }).RequireSignedIn(allowMfaPending: true, allowPasswordChangePending: true);

        g.MapPost("/mfa", async (MfaRequest req, HttpContext http, KiteDb db, ISecretProtector protector, LockoutPolicy lockout,
            IClock clock, CancellationToken ct) =>
        {
            var uid = http.User.UserIdOf();
            var sid = http.User.SessionIdOf();
            var user = await LoadUser(db, uid, ct);
            await EnsureNotLocked(user, clock);
            if (!user.TotpEnabled || user.TotpSecretEnc is null) return Results.NoContent();
            var secret = protector.Unprotect(user.TotpSecretEnc);
            var step = Totp.MatchStep(secret, req.Code ?? "", clock.UtcNow);
            if (step is null || step <= user.TotpLastStep)
            {
                await RecordFailure(db, http, user, lockout, clock, "totp", ct);
                throw new KiteException(ErrorCodes.MfaInvalid, "That code is not correct. Check the time on your phone and try again.", 401, "code");
            }
            await db.InTransactionAsync(AccessScope.System, http.ActorOf(), async t =>
            {
                await ConsumeTotpStep(t, uid, step.Value);
                await t.ExecuteAsync("UPDATE iam.session SET mfa_satisfied = true WHERE id = @sid", new { sid });
                await t.ExecuteAsync("""UPDATE iam."user" SET failed_login_count = 0, locked_until = NULL WHERE id = @uid AND failed_login_count > 0""", new { uid });
                await t.AuditAsync(new AuditEntry("auth.mfa_passed", "user", uid));
            }, ct);
            return Results.NoContent();
        }).RequireSignedIn(allowMfaPending: true, allowPasswordChangePending: true);

        g.MapGet("/me", async (HttpContext http, KiteDb db, ServerContext server, CancellationToken ct) =>
        {
            var uid = http.User.UserIdOf();
            var me = await db.InTransactionAsync(AccessScope.System, ActorContext.System, async t =>
            {
                var user = await t.QuerySingleOrDefaultAsync<(Guid Id, string Username, string DisplayNameEn, string? DisplayNameAr, string PreferredLanguage, bool TotpEnabled, Guid? DefaultBranchId)>(
                    """SELECT id, username, display_name_en, display_name_ar, preferred_language, totp_enabled, default_branch_id FROM iam."user" WHERE id = @uid""", new { uid });
                var business = await t.QuerySingleOrDefaultAsync<(string LegalNameEn, string LegalNameAr, string BaseCurrency, short CurrencyDecimals, bool PricesIncludeTax)>(
                    "SELECT legal_name_en, legal_name_ar, base_currency, currency_decimals, prices_include_tax FROM core.business WHERE id = @b", new { b = server.BusinessId });
                var branches = (await t.QueryAsync<BranchDto>(
                    "SELECT id, code, name_en AS NameEn, name_ar AS NameAr, is_head_office AS IsHeadOffice, status FROM core.branch ORDER BY code")).ToArray();
                return new { user, business, branches };
            }, ct);
            var scope = http.User.ScopeOf();
            return Results.Ok(new
            {
                user = new
                {
                    me.user.Id, me.user.Username, me.user.DisplayNameEn, me.user.DisplayNameAr, me.user.PreferredLanguage,
                    me.user.TotpEnabled, me.user.DefaultBranchId,
                },
                business = new { me.business.LegalNameEn, me.business.LegalNameAr, currency = me.business.BaseCurrency, decimals = me.business.CurrencyDecimals, me.business.PricesIncludeTax },
                permissions = http.User.AllPermissions().Order(StringComparer.Ordinal).ToArray(),
                allBranches = scope.AllBranches,
                branches = me.branches.Where(b => scope.CanSee(b.Id)).ToArray(),
                mfaPending = http.User.HasClaim(KiteClaims.MfaPending, "true"),
                mustChangePassword = http.User.HasClaim(KiteClaims.MustChangePassword, "true"),
                serverRole = server.Role,
                // A branch server works for its own branch only: the web app uses it for receiving, counts and terminals there.
                serverBranchId = server.Role == "branch" ? server.BranchId : null,
            });
        }).RequireSignedIn(allowMfaPending: true, allowPasswordChangePending: true);

        g.MapPost("/password", async (ChangePasswordRequest req, HttpContext http, KiteDb db, IPasswordHasher hasher, CancellationToken ct) =>
        {
            var uid = http.User.UserIdOf();
            var sid = http.User.SessionIdOf();
            var user = await LoadUser(db, uid, ct);
            if (!hasher.Verify(req.CurrentPassword ?? "", user.PasswordHash))
                throw new KiteException(ErrorCodes.BadCredentials, "Current password is not correct.", 400, "currentPassword");
            PasswordPolicy.Check(req.NewPassword, user.Username);
            if (hasher.Verify(req.NewPassword, user.PasswordHash))
                throw new KiteException(ErrorCodes.PasswordPolicy, "Choose a password different from the current one.", 400, "newPassword");
            var hash = hasher.Hash(req.NewPassword);
            await db.InTransactionAsync(AccessScope.System, http.ActorOf(), async t =>
            {
                await t.ExecuteAsync("""UPDATE iam."user" SET password_hash = @hash, password_changed_at = now(), must_change_password = false, updated_by = @uid WHERE id = @uid""", new { hash, uid });
                await t.ExecuteAsync("UPDATE iam.session SET revoked_at = now(), revoked_reason = 'password_changed' WHERE user_id = @uid AND id <> @sid AND revoked_at IS NULL", new { uid, sid });
                await t.AuditAsync(new AuditEntry("auth.password_changed", "user", uid));
            }, ct);
            return Results.NoContent();
        }).RequireSignedIn(allowPasswordChangePending: true);

        g.MapPost("/pin", async (SetPinRequest req, HttpContext http, KiteDb db, IPasswordHasher hasher, CancellationToken ct) =>
        {
            var uid = http.User.UserIdOf();
            var user = await LoadUser(db, uid, ct);
            if (!hasher.Verify(req.CurrentPassword ?? "", user.PasswordHash))
                throw new KiteException(ErrorCodes.BadCredentials, "Current password is not correct.", 400, "currentPassword");
            PasswordPolicy.CheckPin(req.Pin);
            var hash = hasher.Hash(req.Pin);
            await db.InTransactionAsync(AccessScope.System, http.ActorOf(), async t =>
            {
                await t.ExecuteAsync("""UPDATE iam."user" SET pin_hash = @hash, updated_by = @uid WHERE id = @uid""", new { hash, uid });
                await t.AuditAsync(new AuditEntry("auth.pin_set", "user", uid));
            }, ct);
            return Results.NoContent();
        }).RequireSignedIn();

        g.MapPost("/2fa/setup", async (HttpContext http, KiteDb db, ISecretProtector protector, CancellationToken ct) =>
        {
            var uid = http.User.UserIdOf();
            var user = await LoadUser(db, uid, ct);
            if (user.TotpEnabled) throw new KiteException(ErrorCodes.Conflict, "Two-factor sign-in is already on.", 409);
            var secret = Totp.NewSecret();
            await db.InTransactionAsync(AccessScope.System, http.ActorOf(), async t =>
            {
                await t.ExecuteAsync("""UPDATE iam."user" SET totp_secret_enc = @enc, totp_last_step = NULL WHERE id = @uid""", new { enc = protector.Protect(secret), uid });
                await t.AuditAsync(new AuditEntry("auth.totp_setup_started", "user", uid));
            }, ct);
            return Results.Ok(new { secret = Base32.Encode(secret), otpauthUri = Totp.EnrolmentUri(secret, user.Username) });
        }).RequireSignedIn();

        g.MapPost("/2fa/enable", async (MfaRequest req, HttpContext http, KiteDb db, ISecretProtector protector, IClock clock, CancellationToken ct) =>
        {
            var uid = http.User.UserIdOf();
            var sid = http.User.SessionIdOf();
            var user = await LoadUser(db, uid, ct);
            if (user.TotpSecretEnc is null) throw new KiteException(ErrorCodes.Validation, "Start two-factor setup first.", 400);
            var step = Totp.MatchStep(protector.Unprotect(user.TotpSecretEnc), req.Code ?? "", clock.UtcNow);
            if (step is null || step <= user.TotpLastStep)
                throw new KiteException(ErrorCodes.MfaInvalid, "That code is not correct.", 400, "code");
            await db.InTransactionAsync(AccessScope.System, http.ActorOf(), async t =>
            {
                await ConsumeTotpStep(t, uid, step.Value);
                await t.ExecuteAsync("""UPDATE iam."user" SET totp_enabled = true, updated_by = @uid WHERE id = @uid""", new { uid });
                await t.ExecuteAsync("UPDATE iam.session SET mfa_satisfied = true WHERE id = @sid", new { sid });
                await t.AuditAsync(new AuditEntry("auth.totp_enabled", "user", uid));
            }, ct);
            return Results.NoContent();
        }).RequireSignedIn();

        g.MapPost("/2fa/disable", async (DisableTotpRequest req, HttpContext http, KiteDb db, IPasswordHasher hasher, CancellationToken ct) =>
        {
            var uid = http.User.UserIdOf();
            var user = await LoadUser(db, uid, ct);
            if (!hasher.Verify(req.CurrentPassword ?? "", user.PasswordHash))
                throw new KiteException(ErrorCodes.BadCredentials, "Current password is not correct.", 400, "currentPassword");
            await db.InTransactionAsync(AccessScope.System, http.ActorOf(), async t =>
            {
                await t.ExecuteAsync("""UPDATE iam."user" SET totp_enabled = false, totp_secret_enc = NULL, totp_last_step = NULL, updated_by = @uid WHERE id = @uid""", new { uid });
                await t.AuditAsync(new AuditEntry("auth.totp_disabled", "user", uid));
            }, ct);
            return Results.NoContent();
        }).RequireSignedIn();
    }

    private static void RequireConfigured(ServerContext server)
    {
        if (!server.IsConfigured) throw new KiteException(ErrorCodes.SetupRequired, "Set up the business first.", 409);
    }

    private static KiteException BadCredentials() =>
        new(ErrorCodes.BadCredentials, "Username or password is not correct.", 401);

    private static async Task<UserRow?> FindUser(KiteDb db, ServerContext server, string? username, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        return await db.InTransactionAsync(AccessScope.System, ActorContext.System, t =>
            t.QuerySingleOrDefaultAsync<UserRow>("""
                SELECT id, username, password_hash AS PasswordHash, pin_hash AS PinHash, status, failed_login_count AS FailedLoginCount,
                       locked_until AS LockedUntil, totp_enabled AS TotpEnabled, totp_secret_enc AS TotpSecretEnc,
                       must_change_password AS MustChangePassword, totp_last_step AS TotpLastStep
                  FROM iam."user" WHERE business_id = @b AND username = @u
                """, new { b = server.BusinessId, u = username.Trim().ToLowerInvariant() }), ct);
    }

    // Records the step of an accepted code. The WHERE clause makes two simultaneous requests with the same code
    // race safely: only one updates the row, the other is refused and its transaction rolls back.
    private static async Task ConsumeTotpStep(DbTx t, Guid uid, long step)
    {
        var updated = await t.ExecuteAsync(
            """UPDATE iam."user" SET totp_last_step = @step WHERE id = @uid AND (totp_last_step IS NULL OR totp_last_step < @step)""",
            new { uid, step });
        if (updated != 1) throw new KiteException(ErrorCodes.MfaInvalid, "That code is not correct. Check the time on your phone and try again.", 401, "code");
    }

    private static async Task<UserRow> LoadUser(KiteDb db, Guid uid, CancellationToken ct) =>
        await db.InTransactionAsync(AccessScope.System, ActorContext.System, t =>
            t.QuerySingleOrDefaultAsync<UserRow>("""
                SELECT id, username, password_hash AS PasswordHash, pin_hash AS PinHash, status, failed_login_count AS FailedLoginCount,
                       locked_until AS LockedUntil, totp_enabled AS TotpEnabled, totp_secret_enc AS TotpSecretEnc,
                       must_change_password AS MustChangePassword, totp_last_step AS TotpLastStep
                  FROM iam."user" WHERE id = @uid
                """, new { uid }), ct)
        ?? throw new KiteException(ErrorCodes.Unauthenticated, "Sign in required.", 401);

    private static Task EnsureNotLocked(UserRow user, IClock clock)
    {
        if (user.LockedUntil is { } until && new DateTimeOffset(DateTime.SpecifyKind(until, DateTimeKind.Utc)) > clock.UtcNow)
        {
            var minutes = Math.Max(1, (int)Math.Ceiling((DateTime.SpecifyKind(until, DateTimeKind.Utc) - clock.UtcNow.UtcDateTime).TotalMinutes));
            throw new KiteException(ErrorCodes.AccountLocked, $"Too many failed attempts. Try again in {minutes} minute(s) or ask your manager to unlock the account.", 423);
        }
        return Task.CompletedTask;
    }

    private static async Task EnsureTerminalActive(KiteDb db, Guid terminalId, string? deviceKey, CancellationToken ct)
    {
        var term = await db.InTransactionAsync(AccessScope.System, ActorContext.System,
            t => t.QuerySingleOrDefaultAsync<(string Status, string? Fingerprint)>("SELECT status, device_key_fingerprint FROM core.terminal WHERE id = @terminalId", new { terminalId }), ct);
        if (term.Status != "active")
            throw new KiteException(ErrorCodes.TerminalNotActive, "This terminal is not approved. Ask a manager to approve it.", 403, "terminalId");
        // Phase 4: a terminal registered with a device key must present it (18.1).
        if (!DeviceKeys.Matches(term.Fingerprint, deviceKey))
            throw new KiteException(ErrorCodes.TerminalKeyInvalid, "This device is not the registered terminal. Ask a manager to bind this device.", 403, "deviceKey");
    }

    /// <summary>Commits the failure (and any lock) in its own transaction before the error is returned.</summary>
    private static async Task RecordFailure(KiteDb db, HttpContext http, UserRow user, LockoutPolicy lockout, IClock clock, string method, CancellationToken ct)
    {
        var count = user.FailedLoginCount + 1;
        var lockFor = lockout.LockFor(count);
        DateTimeOffset? lockedUntil = lockFor is null ? null : clock.UtcNow + lockFor.Value;
        await db.InTransactionAsync(AccessScope.System, http.ActorOf() with { UserId = user.Id }, async t =>
        {
            await t.ExecuteAsync("""UPDATE iam."user" SET failed_login_count = @count, locked_until = coalesce(@lockedUntil, locked_until) WHERE id = @id""",
                new { count, lockedUntil, id = user.Id });
            await t.AuditAsync(new AuditEntry("auth.login_failed", "user", user.Id, After: Json.Obj(new { method, failedCount = count })));
            if (lockedUntil is not null)
                await t.AuditAsync(new AuditEntry("auth.locked", "user", user.Id, After: Json.Obj(new { until = lockedUntil })));
        }, ct);
    }

    private static async Task<TokenResponse> CreateSession(KiteDb db, HttpContext http, UserRow user, Guid? terminalId, string method,
        bool mfaSatisfied, SessionPolicy policy, IClock clock, string? rehash, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var access = Tokens.New();
        var refresh = Tokens.New();
        var accessExp = now + policy.AccessLifetime;
        var refreshExp = now + policy.RefreshLifetime;
        var sessionId = Ids.New();
        await db.InTransactionAsync(AccessScope.System, http.ActorOf() with { UserId = user.Id, TerminalId = terminalId }, async t =>
        {
            await t.ExecuteAsync("""
                INSERT INTO iam.session (id, user_id, terminal_id, access_hash, refresh_hash, access_expires_at, refresh_expires_at,
                                         mfa_satisfied, auth_method, ip, user_agent)
                VALUES (@sessionId, @uid, @terminalId, @ah, @rh, @ae, @re, @mfaSatisfied, @method, @ip::inet, @ua)
                """, new
            {
                sessionId, uid = user.Id, terminalId, ah = Tokens.Hash(access), rh = Tokens.Hash(refresh), ae = accessExp, re = refreshExp,
                mfaSatisfied, method, ip = http.Connection.RemoteIpAddress?.ToString(), ua = http.Request.Headers.UserAgent.ToString(),
            });
            await t.ExecuteAsync("""
                UPDATE iam."user" SET failed_login_count = 0, locked_until = NULL, last_login_at = now(),
                       password_hash = coalesce(@rehash, password_hash) WHERE id = @uid
                """, new { uid = user.Id, rehash });
            await t.AuditAsync(new AuditEntry("auth.login", "session", sessionId, After: Json.Obj(new { method, terminalId, mfaPending = !mfaSatisfied })));
        }, ct);
        return new TokenResponse(access, accessExp, refresh, refreshExp, !mfaSatisfied, user.MustChangePassword);
    }

    private static void SetRefreshCookie(HttpContext http, TokenResponse tokens) =>
        http.Response.Cookies.Append(RefreshCookie, tokens.RefreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = "/api/v1/auth",
            Expires = tokens.RefreshExpiresAt,
        });
}

public sealed record BranchDto(Guid Id, string Code, string NameEn, string NameAr, bool IsHeadOffice, string Status);
