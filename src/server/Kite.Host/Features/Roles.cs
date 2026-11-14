using System.Text.RegularExpressions;
using Kite.Host.Infrastructure;
using Kite.Persistence;
using Kite.Platform;

namespace Kite.Host.Features;

public sealed record CreateRoleRequest(string Code, string NameEn, string NameAr, List<string> Permissions);
public sealed record UpdateRoleRequest(string NameEn, string NameAr, List<string> Permissions, int Version);
public sealed record RoleDto(Guid Id, string Code, string NameEn, string NameAr, bool IsSystem, int Version, IReadOnlyList<string> Permissions);
public sealed record PermissionDto(string Code, string ModuleCode, string DescriptionEn, string DescriptionAr);

/// <summary>Roles and the permission catalogue (blueprint 18.3). The owner role cannot be edited.</summary>
public static partial class RoleEndpoints
{
    [GeneratedRegex("^[a-z][a-z0-9_]{1,40}$")]
    private static partial Regex CodePattern();

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/permissions", async (KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(AccessScope.System, ActorContext.System, t =>
                t.QueryAsync<PermissionDto>("SELECT code, module_code AS ModuleCode, description_en AS DescriptionEn, description_ar AS DescriptionAr FROM iam.permission ORDER BY code"), ct)))
            .RequirePermission(Permissions.UsersView);

        var g = app.MapGroup("/api/v1/roles");

        g.MapGet("", async (KiteDb db, ServerContext server, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => ListRoles(t, server, null), ct)))
            .RequirePermission(Permissions.UsersView);

        g.MapPost("", async (CreateRoleRequest req, HttpContext http, KiteDb db, ServerContext server, CancellationToken ct) =>
        {
            var code = (req.Code ?? "").Trim().ToLowerInvariant();
            if (!CodePattern().IsMatch(code)) throw new KiteException(ErrorCodes.Validation, "Role code must be lowercase letters, digits or underscores.", 400, "code");
            if (string.IsNullOrWhiteSpace(req.NameEn) || string.IsNullOrWhiteSpace(req.NameAr))
                throw new KiteException(ErrorCodes.Validation, "Role names in English and Arabic are required.", 400, "nameEn");
            var perms = CheckPermissions(http, req.Permissions);
            var id = Ids.New();
            var role = await db.InTransactionAsync(AccessScope.System, http.ActorOf(), async t =>
            {
                await t.ExecuteAsync("""
                    INSERT INTO iam.role (id, business_id, code, name_en, name_ar, is_system, created_by, updated_by)
                    VALUES (@id, @b, @code, @NameEn, @NameAr, false, @actor, @actor)
                    """, new { id, b = server.BusinessId, code, req.NameEn, req.NameAr, actor = http.User.UserIdOf() });
                foreach (var p in perms)
                    await t.ExecuteAsync("INSERT INTO iam.role_permission (role_id, permission_code) VALUES (@id, @p)", new { id, p });
                var after = Json.Obj(new { code, req.NameEn, req.NameAr, permissions = perms });
                await t.AuditAsync(new AuditEntry("role.created", "role", id, After: after));
                await t.PublishAsync("iam.role_created", "role", id, 1, after);
                return (await ListRoles(t, server, id)).Single();
            }, ct);
            return Results.Created($"/api/v1/roles/{id}", role);
        }).RequirePermission(Permissions.RolesManage);

        g.MapPut("/{id:guid}", async (Guid id, UpdateRoleRequest req, HttpContext http, KiteDb db, ServerContext server, CancellationToken ct) =>
        {
            var perms = CheckPermissions(http, req.Permissions);
            var role = await db.InTransactionAsync(AccessScope.System, http.ActorOf(), async t =>
            {
                var before = (await ListRoles(t, server, id)).SingleOrDefault() ?? throw new KiteException(ErrorCodes.NotFound, "Role not found.", 404);
                if (before.Code == RoleTemplates.OwnerCode)
                    throw new KiteException(ErrorCodes.SystemRoleLocked, "The owner role always has every permission and cannot be edited.", 409);
                if (before.Version != req.Version)
                    throw new KiteException(ErrorCodes.VersionMismatch, "Someone else changed this role. Reload and try again.", 412);
                await t.ExecuteAsync("UPDATE iam.role SET name_en = @NameEn, name_ar = @NameAr, updated_by = @actor WHERE id = @id",
                    new { id, req.NameEn, req.NameAr, actor = http.User.UserIdOf() });
                await t.ExecuteAsync("DELETE FROM iam.role_permission WHERE role_id = @id", new { id });
                foreach (var p in perms)
                    await t.ExecuteAsync("INSERT INTO iam.role_permission (role_id, permission_code) VALUES (@id, @p)", new { id, p });
                var after = Json.Obj(new { req.NameEn, req.NameAr, permissions = perms });
                await t.AuditAsync(new AuditEntry("role.updated", "role", id, Before: Json.Obj(before), After: after));
                await t.PublishAsync("iam.role_updated", "role", id, before.Version + 1, after);
                return (await ListRoles(t, server, id)).Single();
            }, ct);
            return Results.Ok(role);
        }).RequirePermission(Permissions.RolesManage);
    }

    private static string[] CheckPermissions(HttpContext http, List<string>? requested)
    {
        var perms = (requested ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var unknown = perms.Where(p => !Permissions.All.Contains(p)).ToArray();
        if (unknown.Length > 0) throw new KiteException(ErrorCodes.Validation, $"Unknown permission {unknown[0]}.", 400, "permissions");
        var notHeld = perms.Where(p => !http.User.Has(p)).ToArray();
        if (notHeld.Length > 0)
            throw new KiteException(ErrorCodes.Forbidden, $"You cannot give a role permissions you do not have ({string.Join(", ", notHeld.Take(3))}).", 403, "permissions");
        return perms;
    }

    private static async Task<IReadOnlyList<RoleDto>> ListRoles(DbTx t, ServerContext server, Guid? id)
    {
        var roles = await t.QueryAsync<(Guid Id, string Code, string NameEn, string NameAr, bool IsSystem, int Version)>(
            "SELECT id, code, name_en, name_ar, is_system, version FROM iam.role WHERE business_id = @b AND (@id::uuid IS NULL OR id = @id) ORDER BY is_system DESC, code",
            new { b = server.BusinessId, id });
        var perms = (await t.QueryAsync<(Guid RoleId, string Code)>(
            "SELECT role_id, permission_code FROM iam.role_permission WHERE (@id::uuid IS NULL OR role_id = @id) ORDER BY permission_code", new { id }))
            .ToLookup(p => p.RoleId, p => p.Code);
        return roles.Select(r => new RoleDto(r.Id, r.Code, r.NameEn, r.NameAr, r.IsSystem, r.Version, perms[r.Id].ToArray())).ToArray();
    }
}
