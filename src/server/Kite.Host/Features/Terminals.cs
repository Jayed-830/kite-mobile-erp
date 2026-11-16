using System.Text.RegularExpressions;
using Kite.Host.Infrastructure;
using Kite.Persistence;
using Kite.Platform;

namespace Kite.Host.Features;

public sealed record TerminalRequest(Guid BranchId, string Code, string Name, string Kind, string? DeviceKey = null);
public sealed record DeviceKeyRequest(string DeviceKey);
public sealed record ApproveTerminalRequest(bool EmergencyEnabled = false);
public sealed record TerminalDto(Guid Id, Guid BranchId, string Code, string Name, string Kind, string Status, bool EmergencyEnabled,
    DateTime RequestedAt, DateTime? ApprovedAt, int Version);

/// <summary>
/// Terminal registration (blueprint 18.1 step 4): a signed-in user asks to register a terminal, and a manager of
/// that branch approves or revokes it. Phase 4: the terminal keeps a random device key; the server stores only its
/// SHA-256 fingerprint, and sign-in on the terminal must present the key.
/// </summary>
public static partial class TerminalEndpoints
{
    [GeneratedRegex("^[A-Z0-9-]{1,12}$")]
    private static partial Regex CodePattern();

    private const string Select = """
        SELECT id, branch_id AS BranchId, code, name, kind, status, emergency_enabled AS EmergencyEnabled,
               requested_at AS RequestedAt, approved_at AS ApprovedAt, version FROM core.terminal
        """;

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/terminals");

        g.MapPost("", async (TerminalRequest req, HttpContext http, KiteDb db, ServerContext server, CancellationToken ct) =>
        {
            var code = (req.Code ?? "").Trim().ToUpperInvariant();
            if (!CodePattern().IsMatch(code)) throw new KiteException(ErrorCodes.Validation, "Terminal code must be 1 to 12 letters, digits or hyphens.", 400, "code");
            if (req.Kind is not ("windows" or "browser")) throw new KiteException(ErrorCodes.Validation, "Kind must be windows or browser.", 400, "kind");
            if (string.IsNullOrWhiteSpace(req.Name)) throw new KiteException(ErrorCodes.Validation, "Name is required.", 400, "name");
            var fingerprint = req.DeviceKey is null ? null : DeviceKeys.Fingerprint(req.DeviceKey);
            var id = Ids.New();
            // The request is written with system scope (any signed-in user may ask); approval is branch-scoped.
            await db.InTransactionAsync(AccessScope.System, http.ActorOf() with { BranchId = req.BranchId }, async t =>
            {
                await t.ExecuteAsync("""
                    INSERT INTO core.terminal (id, business_id, branch_id, code, name, kind, status, device_key_fingerprint, created_by)
                    VALUES (@id, @b, @BranchId, @code, @Name, @Kind, 'pending', @fingerprint, @actor)
                    """, new { id, b = server.BusinessId, req.BranchId, code, req.Name, req.Kind, fingerprint, actor = http.User.UserIdOf() });
                await t.AuditAsync(new AuditEntry("terminal.requested", "terminal", id, After: Json.Obj(new { code, req.Name, req.Kind }), BranchId: req.BranchId));
            }, ct);
            return Results.Created($"/api/v1/terminals/{id}", new { id, status = "pending" });
        }).RequireSignedIn();

        g.MapGet("", async (HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(),
                t => t.QueryAsync<TerminalDto>(Select + " ORDER BY code"), ct)))
            .RequirePermission(Permissions.TerminalsApprove);

        g.MapPost("/{id:guid}/approve", async (Guid id, ApproveTerminalRequest? req, HttpContext http, KiteDb db, LicenceService licence, CancellationToken ct) =>
        {
            var allowed = (await licence.StatusAsync(ct)).TerminalsAllowed;
            var dto = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var before = await t.QuerySingleOrDefaultAsync<TerminalDto>(Select + " WHERE id = @id FOR UPDATE", new { id })
                    ?? throw new KiteException(ErrorCodes.NotFound, "Terminal not found in your branches.", 404);
                http.User.Demand(Permissions.TerminalsApprove, before.BranchId);
                // The licence limit counts every active terminal of the business (blueprint 24.5).
                var active = await t.ExecuteScalarAsync<long>("SELECT core.active_terminal_count()");
                if (before.Status != "active" && active >= allowed)
                    throw new KiteException(ErrorCodes.LicenceLimit, $"The licence allows {allowed} active terminal{(allowed == 1 ? "" : "s")}. Ask Nextdab to extend it, or revoke a terminal you no longer use.", 403);
                await t.ExecuteAsync("""
                    UPDATE core.terminal SET status = 'active', approved_at = now(), approved_by = @actor,
                           emergency_enabled = @emergency, updated_by = @actor WHERE id = @id
                    """, new { id, actor = http.User.UserIdOf(), emergency = (req?.EmergencyEnabled ?? false) && before.Kind == "windows" });
                await t.AuditAsync(new AuditEntry("terminal.approved", "terminal", id, Before: Json.Obj(before), BranchId: before.BranchId));
                await t.PublishAsync("core.terminal_approved", "terminal", id, before.Version + 1, Json.Obj(new { before.Code, before.Kind }), before.BranchId);
                return await t.QuerySingleOrDefaultAsync<TerminalDto>(Select + " WHERE id = @id", new { id });
            }, ct);
            return Results.Ok(dto);
        }).RequirePermission(Permissions.TerminalsApprove);

        // A reinstalled terminal gets a new device key; a manager binds it (the old key stops working).
        g.MapPost("/{id:guid}/device-key", async (Guid id, DeviceKeyRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            var fingerprint = DeviceKeys.Fingerprint(req.DeviceKey);
            await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var before = await t.QuerySingleOrDefaultAsync<TerminalDto>(Select + " WHERE id = @id FOR UPDATE", new { id })
                    ?? throw new KiteException(ErrorCodes.NotFound, "Terminal not found in your branches.", 404);
                http.User.Demand(Permissions.TerminalsApprove, before.BranchId);
                await t.ExecuteAsync("UPDATE core.terminal SET device_key_fingerprint = @fingerprint, updated_by = @actor WHERE id = @id", new { id, fingerprint, actor = http.User.UserIdOf() });
                await t.ExecuteAsync("UPDATE iam.session SET revoked_at = now(), revoked_reason = 'device_key_changed' WHERE terminal_id = @id AND revoked_at IS NULL", new { id });
                await t.AuditAsync(new AuditEntry("terminal.device_key_bound", "terminal", id, BranchId: before.BranchId));
            }, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.TerminalsApprove);

        g.MapPost("/{id:guid}/revoke", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var before = await t.QuerySingleOrDefaultAsync<TerminalDto>(Select + " WHERE id = @id FOR UPDATE", new { id })
                    ?? throw new KiteException(ErrorCodes.NotFound, "Terminal not found in your branches.", 404);
                http.User.Demand(Permissions.TerminalsApprove, before.BranchId);
                await t.ExecuteAsync("UPDATE core.terminal SET status = 'revoked', updated_by = @actor WHERE id = @id", new { id, actor = http.User.UserIdOf() });
                await t.ExecuteAsync("UPDATE iam.session SET revoked_at = now(), revoked_reason = 'terminal_revoked' WHERE terminal_id = @id AND revoked_at IS NULL", new { id });
                await t.AuditAsync(new AuditEntry("terminal.revoked", "terminal", id, Before: Json.Obj(before), BranchId: before.BranchId));
                await t.PublishAsync("core.terminal_revoked", "terminal", id, before.Version + 1, Json.Obj(new { before.Code }), before.BranchId);
            }, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.TerminalsApprove);
    }
}

public static class DeviceKeys
{
    /// <summary>SHA-256 (hex) of a terminal's device key; the key is 32 or more random bytes, base64url, never stored on the server.</summary>
    public static string Fingerprint(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length is < 43 or > 200 || !key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '=' or '+' or '/'))
            throw new KiteException(ErrorCodes.TerminalKeyInvalid, "The terminal's device key is missing or not valid.", 400, "deviceKey");
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(key.Trim())));
    }

    public static bool Matches(string? fingerprint, string? key) =>
        fingerprint is null || (key is not null && key.Length >= 43 && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(fingerprint), System.Text.Encoding.ASCII.GetBytes(Fingerprint(key))));
}
