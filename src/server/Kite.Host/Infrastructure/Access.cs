using System.Security.Claims;
using Kite.Persistence;
using Kite.Platform;

namespace Kite.Host.Infrastructure;

public enum AccessKind { Anonymous, Authenticated, Permission }

/// <summary>
/// Every endpoint declares exactly one of these (blueprint 26.3 rule 2). A test enumerates all
/// endpoints and fails if any is missing.
/// </summary>
public sealed record KiteAccess(AccessKind Kind, string? Permission = null, bool AllowMfaPending = false, bool AllowPasswordChangePending = false)
{
    public static readonly KiteAccess Anonymous = new(AccessKind.Anonymous);
}

/// <summary>Marks an endpoint as belonging to a switchable module (blueprint 2.4).</summary>
public sealed record KiteModule(string Code);

public static class KiteClaims
{
    public const string UserId = "kite:uid";
    public const string SessionId = "kite:sid";
    public const string Permission = "kite:perm";
    /// <summary>"{branchId}|{permission}": granted through a role assigned for one branch.</summary>
    public const string BranchPermission = "kite:bperm";
    public const string Branch = "kite:branch";
    public const string AllBranches = "kite:allbranches";
    public const string MfaPending = "kite:mfa_pending";
    public const string MustChangePassword = "kite:must_change_password";
    public const string TerminalId = "kite:terminal";
    public const string Username = "kite:username";

    public static Guid UserIdOf(this ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue(UserId) ?? throw Unauth());
    public static Guid SessionIdOf(this ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue(SessionId) ?? throw Unauth());

    public static Guid? TerminalIdOf(this ClaimsPrincipal p) =>
        Guid.TryParse(p.FindFirstValue(TerminalId), out var t) ? t : null;

    /// <summary>Granted business-wide or in at least one branch (for endpoints with no branch context).</summary>
    public static bool Has(this ClaimsPrincipal p, string permission) =>
        p.HasClaim(Permission, permission) || p.FindAll(BranchPermission).Any(c => c.Value.EndsWith("|" + permission, StringComparison.Ordinal));

    /// <summary>Granted business-wide, or in this branch. A null branch needs a business-wide grant.</summary>
    public static bool HasIn(this ClaimsPrincipal p, string permission, Guid? branchId) =>
        p.HasClaim(Permission, permission) || (branchId is { } b && p.HasClaim(BranchPermission, $"{b:D}|{permission}"));

    public static void Demand(this ClaimsPrincipal p, string permission, Guid? branchId)
    {
        if (!p.HasIn(permission, branchId))
            throw new KiteException(ErrorCodes.Forbidden, branchId is null
                ? $"You do not have permission {permission}."
                : $"You do not have permission {permission} in this branch.", 403);
    }

    /// <summary>Every permission the user holds anywhere (for the web app's menus).</summary>
    public static IEnumerable<string> AllPermissions(this ClaimsPrincipal p) =>
        p.FindAll(Permission).Select(c => c.Value)
            .Concat(p.FindAll(BranchPermission).Select(c => c.Value[(c.Value.IndexOf('|', StringComparison.Ordinal) + 1)..]))
            .Distinct(StringComparer.Ordinal);

    public static AccessScope ScopeOf(this ClaimsPrincipal p) =>
        p.HasClaim(AllBranches, "true")
            ? AccessScope.System
            : AccessScope.ForBranches(p.FindAll(Branch).Select(c => Guid.Parse(c.Value)));

    public static ActorContext ActorOf(this HttpContext ctx)
    {
        var u = ctx.User;
        Guid? uid = Guid.TryParse(u.FindFirstValue(UserId), out var id) ? id : null;
        return new ActorContext(uid, null, u.TerminalIdOf(), ctx.Connection.RemoteIpAddress?.ToString());
    }

    private static KiteException Unauth() => new(ErrorCodes.Unauthenticated, "Sign in required.", 401);
}

public static class AccessExtensions
{
    public static RouteHandlerBuilder RequirePermission(this RouteHandlerBuilder b, string permission) =>
        b.WithMetadata(new KiteAccess(AccessKind.Permission, permission)).AddEndpointFilter<AccessFilter>();

    public static RouteHandlerBuilder RequireSignedIn(this RouteHandlerBuilder b, bool allowMfaPending = false, bool allowPasswordChangePending = false) =>
        b.WithMetadata(new KiteAccess(AccessKind.Authenticated, null, allowMfaPending, allowPasswordChangePending)).AddEndpointFilter<AccessFilter>();

    public static RouteHandlerBuilder AllowEveryone(this RouteHandlerBuilder b) =>
        b.WithMetadata(KiteAccess.Anonymous).AllowAnonymous();

    public static RouteHandlerBuilder RequireModule(this RouteHandlerBuilder b, string module) =>
        b.WithMetadata(new KiteModule(module)).AddEndpointFilter<ModuleFilter>();
}

/// <summary>Enforces the endpoint's KiteAccess declaration.</summary>
public sealed class AccessFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var access = http.GetEndpoint()?.Metadata.GetMetadata<KiteAccess>()
            ?? throw new KiteException(ErrorCodes.Forbidden, "Endpoint has no access declaration.", 403);
        if (access.Kind == AccessKind.Anonymous) return await next(context);

        var user = http.User;
        if (user.Identity?.IsAuthenticated != true)
            throw new KiteException(ErrorCodes.Unauthenticated, "Sign in required.", 401);
        if (user.HasClaim(KiteClaims.MfaPending, "true") && !access.AllowMfaPending)
            throw new KiteException(ErrorCodes.MfaRequired, "Enter the code from your authenticator app to continue.", 401);
        if (user.HasClaim(KiteClaims.MustChangePassword, "true") && !access.AllowPasswordChangePending)
            throw new KiteException(ErrorCodes.PasswordChangeRequired, "Change your password to continue.", 403);
        if (access.Kind == AccessKind.Permission && !user.Has(access.Permission!))
            throw new KiteException(ErrorCodes.Forbidden, $"You do not have permission {access.Permission}.", 403);
        return await next(context);
    }
}

/// <summary>Blocks new commands to a module that is switched off (blueprint 2.4).</summary>
public sealed class ModuleFilter(ModuleStateCache modules) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var module = context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<KiteModule>();
        if (module is not null && !await modules.IsEnabledAsync(module.Code, context.HttpContext.RequestAborted))
            throw new KiteException(ErrorCodes.ModuleDisabled, $"The {ModuleCatalog.Get(module.Code).NameEn} module is switched off.", 403);
        return await next(context);
    }
}

public sealed record ModuleRestrictions(IReadOnlySet<string> OffByNextdab, IReadOnlySet<string>? Licensed)
{
    public string? LockOf(string code) =>
        OffByNextdab.Contains(code) ? "nextdab" : Licensed is not null && !Licensed.Contains(code) ? "licence" : null;
}

/// <summary>
/// Module states for this business, cached briefly and refreshed after changes. The effective state is the
/// owner's switch, unless Nextdab switched the module off or the licence does not include it (blueprint 24.5).
/// </summary>
public sealed class ModuleStateCache(KiteDb db, ServerContext server)
{
    private sealed record Snapshot(IReadOnlyDictionary<string, bool> Configured, IReadOnlyDictionary<string, bool> Effective, ModuleRestrictions Restrictions);
    private Snapshot? _snap;
    private DateTimeOffset _loadedAt;

    private async Task<Snapshot> LoadAsync(CancellationToken ct)
    {
        if (_snap is not null && DateTimeOffset.UtcNow - _loadedAt < TimeSpan.FromSeconds(30)) return _snap;
        var current = new Dictionary<string, bool>(StringComparer.Ordinal);
        string[] off = [];
        string[]? licensed = null;
        if (server.IsConfigured)
        {
            (var rows, off, licensed) = await db.InTransactionAsync(AccessScope.System, ActorContext.System, async t =>
            (
                await t.QueryAsync<(string Code, bool Enabled)>(
                    "SELECT module_code, enabled FROM core.module_setting WHERE business_id = @b AND branch_id IS NULL", new { b = server.BusinessId }),
                await t.ExecuteScalarAsync<string[]?>("SELECT modules_off FROM lic.control_state") ?? [],
                await t.ExecuteScalarAsync<string[]?>("SELECT modules FROM lic.licence WHERE is_current")
            ), ct);
            foreach (var (code, enabled) in rows) current[code] = enabled;
        }
        var restrictions = new ModuleRestrictions(off.ToHashSet(StringComparer.Ordinal), licensed?.ToHashSet(StringComparer.Ordinal));
        // Reading is forgiving; the rules are enforced when switches are changed.
        var configured = ModuleCatalog.All.ToDictionary(m => m.Code,
            m => m.AlwaysOn || (current.TryGetValue(m.Code, out var on) ? on : m.DefaultEnabled), StringComparer.Ordinal);
        var effective = ModuleCatalog.All.ToDictionary(m => m.Code,
            m => m.AlwaysOn || (configured[m.Code] && restrictions.LockOf(m.Code) is null), StringComparer.Ordinal);
        _snap = new Snapshot(configured, effective, restrictions);
        _loadedAt = DateTimeOffset.UtcNow;
        return _snap;
    }

    public async Task<IReadOnlyDictionary<string, bool>> GetAsync(CancellationToken ct) => (await LoadAsync(ct)).Effective;
    public async Task<IReadOnlyDictionary<string, bool>> GetConfiguredAsync(CancellationToken ct) => (await LoadAsync(ct)).Configured;
    public async Task<ModuleRestrictions> RestrictionsAsync(CancellationToken ct) => (await LoadAsync(ct)).Restrictions;

    public async Task<bool> IsEnabledAsync(string code, CancellationToken ct) =>
        (await GetAsync(ct)).TryGetValue(code, out var on) && on;

    public void Invalidate() => _snap = null;
}
