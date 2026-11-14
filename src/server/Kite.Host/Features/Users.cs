using System.Text.RegularExpressions;
using Kite.Host.Infrastructure;
using Kite.Identity;
using Kite.Persistence;
using Kite.Platform;

namespace Kite.Host.Features;

public sealed record RoleGrant(Guid RoleId, Guid? BranchId);

public sealed record CreateUserRequest(
    string Username, string DisplayNameEn, string? DisplayNameAr, string? Email, string? Phone,
    string TemporaryPassword, string PreferredLanguage, Guid? DefaultBranchId, List<RoleGrant> Roles);

public sealed record UpdateUserRequest(
    string DisplayNameEn, string? DisplayNameAr, string? Email, string? Phone, string PreferredLanguage,
    Guid? DefaultBranchId, string Status, List<RoleGrant> Roles, int Version);

public sealed record ResetPasswordRequest(string TemporaryPassword);

public sealed record UserRoleDto(Guid RoleId, string RoleCode, string RoleNameEn, string RoleNameAr, Guid? BranchId);

public sealed record UserDto(
    Guid Id, string Username, string DisplayNameEn, string? DisplayNameAr, string? Email, string? Phone, string Status,
    bool Locked, bool TotpEnabled, bool HasPin, string PreferredLanguage, Guid? DefaultBranchId, DateTime? LastLoginAt,
    int Version, IReadOnlyList<UserRoleDto> Roles);

/// <summary>User management (blueprint 19, 18.3). A user can grant only permissions they hold themselves.</summary>
public static partial class UserEndpoints
{
    [GeneratedRegex("^[a-z0-9._-]{3,40}$")]
    private static partial Regex UsernamePattern();

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/users");

        g.MapGet("", async (KiteDb db, ServerContext server, IClock clock, CancellationToken ct) =>
            Results.Ok(await ListUsers(db, server, clock, null, ct)))
            .RequirePermission(Permissions.UsersView);

        g.MapPost("", async (CreateUserRequest req, HttpContext http, KiteDb db, ServerContext server, IPasswordHasher hasher,
            IClock clock, CancellationToken ct) =>
        {
            var username = (req.Username ?? "").Trim().ToLowerInvariant();
            if (!UsernamePattern().IsMatch(username))
                throw new KiteException(ErrorCodes.Validation, "Username must be 3 to 40 letters, digits, dots, hyphens or underscores.", 400, "username");
            if (string.IsNullOrWhiteSpace(req.DisplayNameEn))
                throw new KiteException(ErrorCodes.Validation, "Name is required.", 400, "displayNameEn");
            if (req.Roles is null || req.Roles.Count == 0)
                throw new KiteException(ErrorCodes.Validation, "Give the user at least one role.", 400, "roles");
            PasswordPolicy.Check(req.TemporaryPassword, username);
            var hash = hasher.Hash(req.TemporaryPassword);
            var id = Ids.New();

            await db.InTransactionAsync(AccessScope.System, http.ActorOf(), async t =>
            {
                await CheckGrantable(t, http, req.Roles);
                await t.ExecuteAsync("""
                    INSERT INTO iam."user" (id, business_id, username, display_name_en, display_name_ar, email, phone, password_hash,
                                            must_change_password, preferred_language, default_branch_id, created_by, updated_by)
                    VALUES (@id, @b, @username, @DisplayNameEn, @DisplayNameAr, @Email, @Phone, @hash, true, @lang, @DefaultBranchId, @actor, @actor)
                    """, new { id, b = server.BusinessId, username, req.DisplayNameEn, req.DisplayNameAr, req.Email, req.Phone, hash,
                               lang = req.PreferredLanguage == "ar" ? "ar" : "en", req.DefaultBranchId, actor = http.User.UserIdOf() });
                await WriteRoles(t, id, req.Roles, http.User.UserIdOf());
                var after = Json.Obj(new { username, req.DisplayNameEn, req.DisplayNameAr, req.Email, req.Phone, roles = req.Roles });
                await t.AuditAsync(new AuditEntry("user.created", "user", id, After: after));
                await t.PublishAsync("iam.user_created", "user", id, 1, after);
            }, ct);
            return Results.Created($"/api/v1/users/{id}", (await ListUsers(db, server, clock, id, ct)).Single());
        }).RequirePermission(Permissions.UsersManage);

        g.MapPut("/{id:guid}", async (Guid id, UpdateUserRequest req, HttpContext http, KiteDb db, ServerContext server,
            IClock clock, CancellationToken ct) =>
        {
            if (req.Status is not ("active" or "disabled"))
                throw new KiteException(ErrorCodes.Validation, "Status must be active or disabled.", 400, "status");
            if (req.Roles is null || req.Roles.Count == 0)
                throw new KiteException(ErrorCodes.Validation, "Give the user at least one role.", 400, "roles");
            await db.InTransactionAsync(AccessScope.System, http.ActorOf(), async t =>
            {
                var before = (await ListUsersTx(t, server, clock, id)).SingleOrDefault()
                    ?? throw new KiteException(ErrorCodes.NotFound, "User not found.", 404);
                if (before.Version != req.Version)
                    throw new KiteException(ErrorCodes.VersionMismatch, "Someone else changed this user. Reload and try again.", 412);
                await CheckGrantable(t, http, req.Roles.Where(r => !before.Roles.Any(b => b.RoleId == r.RoleId && b.BranchId == r.BranchId)).ToList());
                await t.ExecuteAsync("""
                    UPDATE iam."user" SET display_name_en = @DisplayNameEn, display_name_ar = @DisplayNameAr, email = @Email, phone = @Phone,
                           preferred_language = @lang, default_branch_id = @DefaultBranchId, status = @Status, updated_by = @actor
                     WHERE id = @id
                    """, new { id, req.DisplayNameEn, req.DisplayNameAr, req.Email, req.Phone, lang = req.PreferredLanguage == "ar" ? "ar" : "en",
                               req.DefaultBranchId, req.Status, actor = http.User.UserIdOf() });
                await t.ExecuteAsync("DELETE FROM iam.user_branch_role WHERE user_id = @id", new { id });
                await WriteRoles(t, id, req.Roles, http.User.UserIdOf());
                await EnsureAnOwnerRemains(t, server);
                if (req.Status == "disabled")
                    await t.ExecuteAsync("UPDATE iam.session SET revoked_at = now(), revoked_reason = 'user_disabled' WHERE user_id = @id AND revoked_at IS NULL", new { id });
                var after = Json.Obj(new { req.DisplayNameEn, req.DisplayNameAr, req.Email, req.Phone, req.Status, roles = req.Roles });
                await t.AuditAsync(new AuditEntry("user.updated", "user", id, Before: Json.Obj(before), After: after));
                await t.PublishAsync("iam.user_updated", "user", id, before.Version + 1, after);
            }, ct);
            return Results.Ok((await ListUsers(db, server, clock, id, ct)).Single());
        }).RequirePermission(Permissions.UsersManage);

        g.MapPost("/{id:guid}/unlock", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            await db.InTransactionAsync(AccessScope.System, http.ActorOf(), async t =>
            {
                var n = await t.ExecuteAsync("""UPDATE iam."user" SET failed_login_count = 0, locked_until = NULL, updated_by = @actor WHERE id = @id""",
                    new { id, actor = http.User.UserIdOf() });
                if (n == 0) throw new KiteException(ErrorCodes.NotFound, "User not found.", 404);
                await t.AuditAsync(new AuditEntry("user.unlocked", "user", id));
            }, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.UsersManage);

        g.MapPost("/{id:guid}/reset-password", async (Guid id, ResetPasswordRequest req, HttpContext http, KiteDb db,
            IPasswordHasher hasher, CancellationToken ct) =>
        {
            await db.InTransactionAsync(AccessScope.System, http.ActorOf(), async t =>
            {
                var username = await t.QuerySingleOrDefaultAsync<string>("""SELECT username FROM iam."user" WHERE id = @id""", new { id })
                    ?? throw new KiteException(ErrorCodes.NotFound, "User not found.", 404);
                PasswordPolicy.Check(req.TemporaryPassword, username);
                await t.ExecuteAsync("""
                    UPDATE iam."user" SET password_hash = @hash, must_change_password = true, password_changed_at = now(),
                           failed_login_count = 0, locked_until = NULL, updated_by = @actor WHERE id = @id
                    """, new { id, hash = hasher.Hash(req.TemporaryPassword), actor = http.User.UserIdOf() });
                await t.ExecuteAsync("UPDATE iam.session SET revoked_at = now(), revoked_reason = 'password_reset' WHERE user_id = @id AND revoked_at IS NULL", new { id });
                await t.AuditAsync(new AuditEntry("user.password_reset", "user", id));
            }, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.UsersManage);
    }

    /// <summary>Blocks privilege escalation: every permission in the granted roles must be held by the actor.</summary>
    private static async Task CheckGrantable(DbTx t, HttpContext http, IReadOnlyCollection<RoleGrant> grants)
    {
        if (grants.Count == 0) return;
        var roleIds = grants.Select(g => g.RoleId).Distinct().ToArray();
        var perms = await t.QueryAsync<(Guid RoleId, string? Permission)>("""
            SELECT r.id, rp.permission_code FROM iam.role r LEFT JOIN iam.role_permission rp ON rp.role_id = r.id
             WHERE r.id = ANY(@roleIds)
            """, new { roleIds });
        var found = perms.Select(p => p.RoleId).ToHashSet();
        if (roleIds.Any(r => !found.Contains(r)))
            throw new KiteException(ErrorCodes.Validation, "One of the roles does not exist.", 400, "roles");
        // Each grant is checked where it applies: a role for one branch needs its permissions in that branch
        // (or business-wide); a role for all branches needs them business-wide.
        var byRole = perms.Where(p => p.Permission is not null).ToLookup(p => p.RoleId, p => p.Permission!);
        var missing = grants.SelectMany(g => byRole[g.RoleId].Where(p => !http.User.HasIn(p, g.BranchId))).Distinct().ToArray();
        if (missing.Length > 0)
            throw new KiteException(ErrorCodes.Forbidden, $"You cannot grant a role with permissions you do not have ({string.Join(", ", missing.Take(3))}).", 403, "roles");
        var scope = http.User.ScopeOf();
        if (grants.Any(g => g.BranchId is null && !scope.AllBranches) || grants.Any(g => g.BranchId is { } b && !scope.CanSee(b)))
            throw new KiteException(ErrorCodes.Forbidden, "You can only grant roles in your own branches.", 403, "roles");
    }

    private static async Task WriteRoles(DbTx t, Guid userId, IEnumerable<RoleGrant> roles, Guid actor)
    {
        foreach (var r in roles.DistinctBy(r => (r.RoleId, r.BranchId)))
            await t.ExecuteAsync("""
                INSERT INTO iam.user_branch_role (id, user_id, branch_id, role_id, created_by) VALUES (@id, @userId, @BranchId, @RoleId, @actor)
                """, new { id = Ids.New(), userId, r.BranchId, r.RoleId, actor });
    }

    /// <summary>The business must always keep at least one active owner (IAM-002).</summary>
    public static async Task EnsureAnOwnerRemains(DbTx t, ServerContext server)
    {
        var owners = await t.ExecuteScalarAsync<long>("""
            SELECT count(DISTINCT u.id) FROM iam."user" u
              JOIN iam.user_branch_role ubr ON ubr.user_id = u.id AND ubr.branch_id IS NULL
              JOIN iam.role r ON r.id = ubr.role_id AND r.code = 'owner'
             WHERE u.status = 'active' AND u.business_id = @b
            """, new { b = server.BusinessId });
        if (owners == 0)
            throw new KiteException(ErrorCodes.LastOwner, "At least one active owner with access to all branches must remain.", 409);
    }

    private sealed record UserRow(
        Guid Id, string Username, string DisplayNameEn, string? DisplayNameAr, string? Email, string? Phone, string Status,
        DateTime? LockedUntil, bool TotpEnabled, bool HasPin, string PreferredLanguage, Guid? DefaultBranchId, DateTime? LastLoginAt, int Version);

    private static Task<IReadOnlyList<UserDto>> ListUsers(KiteDb db, ServerContext server, IClock clock, Guid? id, CancellationToken ct) =>
        db.InTransactionAsync(AccessScope.System, ActorContext.System, t => ListUsersTx(t, server, clock, id), ct);

    private static async Task<IReadOnlyList<UserDto>> ListUsersTx(DbTx t, ServerContext server, IClock clock, Guid? id)
    {
        var users = await t.QueryAsync<UserRow>("""
            SELECT id, username, display_name_en AS DisplayNameEn, display_name_ar AS DisplayNameAr, email, phone, status,
                   locked_until AS LockedUntil, totp_enabled AS TotpEnabled, pin_hash IS NOT NULL AS HasPin,
                   preferred_language AS PreferredLanguage, default_branch_id AS DefaultBranchId, last_login_at AS LastLoginAt, version
              FROM iam."user" WHERE business_id = @b AND (@id::uuid IS NULL OR id = @id) ORDER BY username
            """, new { b = server.BusinessId, id });
        var roles = (await t.QueryAsync<(Guid UserId, Guid RoleId, string Code, string NameEn, string NameAr, Guid? BranchId)>("""
            SELECT ubr.user_id, r.id, r.code, r.name_en, r.name_ar, ubr.branch_id
              FROM iam.user_branch_role ubr JOIN iam.role r ON r.id = ubr.role_id
             WHERE (@id::uuid IS NULL OR ubr.user_id = @id)
            """, new { id })).ToLookup(r => r.UserId);
        var now = clock.UtcNow.UtcDateTime;
        return users.Select(u => new UserDto(u.Id, u.Username, u.DisplayNameEn, u.DisplayNameAr, u.Email, u.Phone, u.Status,
            u.LockedUntil is { } l && DateTime.SpecifyKind(l, DateTimeKind.Utc) > now, u.TotpEnabled, u.HasPin, u.PreferredLanguage,
            u.DefaultBranchId, u.LastLoginAt, u.Version,
            roles[u.Id].Select(r => new UserRoleDto(r.RoleId, r.Code, r.NameEn, r.NameAr, r.BranchId)).ToArray())).ToArray();
    }
}
