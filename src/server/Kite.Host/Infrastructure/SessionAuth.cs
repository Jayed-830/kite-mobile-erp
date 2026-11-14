using System.Security.Claims;
using System.Text.Encodings.Web;
using Kite.Identity;
using Kite.Persistence;
using Kite.Platform;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Kite.Host.Infrastructure;

/// <summary>
/// Authenticates opaque bearer tokens against iam.session (blueprint 18.2). Only the SHA-256
/// hash of a token is stored, so a copy of the database cannot be used to sign in.
/// </summary>
public sealed class SessionAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    KiteDb db, IClock clock)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "KiteSession";

    private sealed record SessionRow(
        Guid SessionId, Guid UserId, Guid? TerminalId, bool MfaSatisfied, DateTime AccessExpiresAt, DateTime? RevokedAt,
        string Status, bool TotpEnabled, bool MustChangePassword, string Username, string DisplayNameEn, DateTime? LockedUntil);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        var token = header["Bearer ".Length..].Trim();
        if (token.Length is < 20 or > 200) return AuthenticateResult.Fail("Malformed token.");

        var now = clock.UtcNow;
        var hash = Tokens.Hash(token);
        var (row, perms, branches) = await db.InTransactionAsync(AccessScope.System, ActorContext.System, async t =>
        {
            var s = await t.QuerySingleOrDefaultAsync<SessionRow>("""
                SELECT s.id AS SessionId, s.user_id AS UserId, s.terminal_id AS TerminalId, s.mfa_satisfied AS MfaSatisfied,
                       s.access_expires_at AS AccessExpiresAt, s.revoked_at AS RevokedAt, u.status AS Status,
                       u.totp_enabled AS TotpEnabled, u.must_change_password AS MustChangePassword,
                       u.username AS Username, u.display_name_en AS DisplayNameEn, u.locked_until AS LockedUntil
                  FROM iam.session s JOIN iam."user" u ON u.id = s.user_id
                 WHERE s.access_hash = @hash
                """, new { hash });
            if (s is null) return ((SessionRow?)null, Array.Empty<string>(), Array.Empty<Guid?>());
            var p = (await t.QueryAsync<string>("""
                SELECT DISTINCT rp.permission_code
                  FROM iam.user_branch_role ubr JOIN iam.role_permission rp ON rp.role_id = ubr.role_id
                 WHERE ubr.user_id = @u
                """, new { u = s.UserId })).ToArray();
            var b = (await t.QueryAsync<Guid?>("SELECT DISTINCT branch_id FROM iam.user_branch_role WHERE user_id = @u", new { u = s.UserId })).ToArray();
            return (s, p, b);
        }, Context.RequestAborted);

        if (row is null) return AuthenticateResult.Fail("Unknown session.");
        if (row.RevokedAt is not null) return AuthenticateResult.Fail("Session revoked.");
        if (new DateTimeOffset(DateTime.SpecifyKind(row.AccessExpiresAt, DateTimeKind.Utc)) <= now) return AuthenticateResult.Fail("Session expired.");
        if (row.Status != "active") return AuthenticateResult.Fail("User disabled.");

        var claims = new List<Claim>
        {
            new(KiteClaims.UserId, row.UserId.ToString()),
            new(KiteClaims.SessionId, row.SessionId.ToString()),
            new(KiteClaims.Username, row.Username),
            new(ClaimTypes.Name, row.DisplayNameEn),
        };
        if (row.TerminalId is { } term) claims.Add(new Claim(KiteClaims.TerminalId, term.ToString()));
        if (row.TotpEnabled && !row.MfaSatisfied) claims.Add(new Claim(KiteClaims.MfaPending, "true"));
        if (row.MustChangePassword) claims.Add(new Claim(KiteClaims.MustChangePassword, "true"));
        // Phase 2: permissions are the union of the user's roles across branches; per-branch
        // evaluation arrives with branch-context requests in Phase 3 (see ADR 0006).
        claims.AddRange(perms.Select(p => new Claim(KiteClaims.Permission, p)));
        if (branches.Any(b => b is null)) claims.Add(new Claim(KiteClaims.AllBranches, "true"));
        claims.AddRange(branches.Where(b => b is not null).Select(b => new Claim(KiteClaims.Branch, b!.Value.ToString())));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        return Task.CompletedTask;
    }
}
