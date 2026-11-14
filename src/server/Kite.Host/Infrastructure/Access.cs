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

    public static bool Has(this ClaimsPrincipal p, string permission) =>
        p.HasClaim(Permission, permission);

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

/// <summary>Module states for this business, cached briefly and refreshed after changes.</summary>
public sealed class ModuleStateCache(KiteDb db, ServerContext server)
{
    private IReadOnlyDictionary<string, bool>? _states;
    private DateTimeOffset _loadedAt;

    public async Task<IReadOnlyDictionary<string, bool>> GetAsync(CancellationToken ct)
    {
        if (_states is not null && DateTimeOffset.UtcNow - _loadedAt < TimeSpan.FromSeconds(30)) return _states;
        var current = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (server.IsConfigured)
        {
            var rows = await db.InTransactionAsync(AccessScope.System, ActorContext.System, t =>
                t.QueryAsync<(string Code, bool Enabled)>(
                    "SELECT module_code, enabled FROM core.module_setting WHERE business_id = @b AND branch_id IS NULL",
                    new { b = server.BusinessId }), ct);
            foreach (var (code, enabled) in rows) current[code] = enabled;
        }
        // Reading is forgiving; the rules are enforced when switches are changed.
        _states = ModuleCatalog.All.ToDictionary(m => m.Code,
            m => m.AlwaysOn || (current.TryGetValue(m.Code, out var on) ? on : m.DefaultEnabled), StringComparer.Ordinal);
        _loadedAt = DateTimeOffset.UtcNow;
        return _states;
    }

    public async Task<bool> IsEnabledAsync(string code, CancellationToken ct) =>
        (await GetAsync(ct)).TryGetValue(code, out var on) && on;

    public void Invalidate() => _states = null;
}
