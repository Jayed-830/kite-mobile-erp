using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kite.Host.Infrastructure;
using Kite.Identity;
using Kite.Persistence;
using Kite.Platform;

namespace Kite.Host.Features;

public sealed record ImportLicenceRequest(string FileText);

/// <summary>
/// The KITE MOBILE ERP licence (blueprint 24.5): verified locally against the Nextdab public key, so trading
/// never waits for Nextdab. Nextdab Admin can renew it, switch modules off, switch KITE off and send messages
/// through signed commands; branch servers receive the same signed data from head office.
/// </summary>
public sealed class LicenceService(KiteDb db, ServerContext server, LicenceKeys keys, IClock clock, ModuleStateCache modules, WebAddressService web,
    ILogger<LicenceService> log)
{
    public const string Product = LicenceFile.Product;
    private LicenceStatus? _cached;
    private DateTimeOffset _cachedAt;

    // Classes with settable properties (not positional records): Npgsql reports text[] columns as System.Array, which Dapper's
    // constructor matching does not accept for a string[] parameter; property assignment converts it.
    private sealed class LicenceRow
    {
        public string Id { get; init; } = "";
        public string InstallationId { get; init; } = "";
        public string Kind { get; init; } = "";
        public string Customer { get; init; } = "";
        public DateTime ValidUntil { get; init; }
        public int GraceDays { get; init; }
        public int BranchesAllowed { get; init; }
        public int TerminalsAllowed { get; init; }
        public string[] Modules { get; init; } = [];
        public DateTime IssuedAt { get; init; }
    }
    private sealed class ControlRow
    {
        public bool Suspended { get; init; }
        public string? SuspendMessage { get; init; }
        public string? SuspendMessageAr { get; init; }
        public long SuspendSeq { get; init; }
        public long ModulesSeq { get; init; }
        public string[] ModulesOff { get; init; } = [];
        public string? SuspendCmd { get; init; }
        public string? ModulesCmd { get; init; }
    }

    public void Invalidate() { _cached = null; modules.Invalidate(); }

    public static DateOnly Today(DateTimeOffset utcNow) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, SetupEndpoints.BusinessTimeZone()).DateTime);

    public async Task<LicenceStatus> StatusAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        if (_cached is { } c && now - _cachedAt < TimeSpan.FromSeconds(30)) return c;
        if (!server.IsConfigured) return LicenceStatus.Evaluate(null, false, null, null, Today(now));
        var (lic, ctl) = await db.InTransactionAsync(AccessScope.System, ActorContext.System, async t =>
        (
            await t.QuerySingleOrDefaultAsync<LicenceRow>("""
                SELECT id, installation_id AS InstallationId, kind, customer, valid_until AS ValidUntil, grace_days AS GraceDays,
                       branches_allowed AS BranchesAllowed, terminals_allowed AS TerminalsAllowed, modules, issued_at AS IssuedAt
                  FROM lic.licence WHERE is_current
                """),
            await ControlAsync(t)
        ), ct);
        var terms = lic is null ? null : new LicenceTerms(lic.Id, lic.InstallationId, lic.Kind, lic.Customer, DateOnly.FromDateTime(lic.ValidUntil),
            lic.GraceDays, lic.BranchesAllowed, lic.TerminalsAllowed, lic.Modules, new DateTimeOffset(DateTime.SpecifyKind(lic.IssuedAt, DateTimeKind.Utc)));
        var status = LicenceStatus.Evaluate(terms, ctl?.Suspended ?? false, ctl?.SuspendMessage, ctl?.SuspendMessageAr, Today(now));
        _cached = status;
        _cachedAt = now;
        return status;
    }

    private static Task<ControlRow?> ControlAsync(DbTx t) => t.QuerySingleOrDefaultAsync<ControlRow>("""
        SELECT suspended, suspend_message AS SuspendMessage, suspend_message_ar AS SuspendMessageAr, suspend_seq AS SuspendSeq,
               modules_seq AS ModulesSeq, modules_off AS ModulesOff, suspend_cmd::text AS SuspendCmd, modules_cmd::text AS ModulesCmd
          FROM lic.control_state
        """);

    /// <summary>Modules Nextdab switched off (they stay off whatever the customer sets).</summary>
    public async Task<IReadOnlyCollection<string>> ModulesOffAsync(CancellationToken ct) =>
        await db.InTransactionAsync(AccessScope.System, ActorContext.System,
            async t => (IReadOnlyCollection<string>)((await ControlAsync(t))?.ModulesOff ?? []), ct);

    public async Task RequireTransactAsync(CancellationToken ct)
    {
        var s = await StatusAsync(ct);
        if (s.CanTransact) return;
        throw s.State switch
        {
            LicenceState.Suspended => new KiteException(ErrorCodes.Suspended, s.SuspendMessage ?? "KITE is switched off by Nextdab. Please contact Nextdab.", 403),
            LicenceState.None => new KiteException(ErrorCodes.NoLicence, "This installation has no licence yet. Ask Nextdab for a licence file and import it on the Licence page.", 403),
            _ => new KiteException(ErrorCodes.LicenceExpired, $"The licence ended on {s.EndsOn:yyyy-MM-dd}. New transactions are stopped; data, reports and backups keep working. Ask Nextdab to renew it.", 403),
        };
    }

    /// <summary>The Machine ID the licence must name: this server's, or head office's on a branch server.</summary>
    public async Task<string> LicensedInstallationIdAsync(CancellationToken ct)
    {
        if (server.IsHeadOffice) return server.InstallationId ?? throw new KiteException(ErrorCodes.SetupRequired, "This server has no installation ID yet.", 409);
        return await db.InTransactionAsync(AccessScope.System, ActorContext.System,
            t => t.ExecuteScalarAsync<string?>("SELECT installation_id FROM core.server WHERE role IN ('combined','main')"), ct)
            ?? throw new KiteException(ErrorCodes.SetupRequired, "Head office's installation ID is not known yet.", 409);
    }

    public async Task<LicenceTerms> ImportAsync(string fileText, string source, ActorContext actor, CancellationToken ct)
    {
        if (!keys.IsConfigured)
            throw new KiteException(ErrorCodes.LicenceKeyMissing, "This KITE build has no Nextdab licence key yet, so licences cannot be checked. Install a release build.", 409);
        var mid = await LicensedInstallationIdAsync(ct);
        LicenceTerms terms;
        try { terms = LicenceFile.Read(fileText ?? "", keys.PublicKey, mid); }
        catch (LicenceException e) { throw new KiteException(e.Code, e.Message, 400, "fileText"); }
        var known = Kite.Platform.ModuleCatalog.All.Select(m => m.Code).ToHashSet(StringComparer.Ordinal);
        await db.InTransactionAsync(AccessScope.System, actor, async t =>
        {
            var current = await t.QuerySingleOrDefaultAsync<(string? Id, DateTime IssuedAt)>("SELECT id, issued_at FROM lic.licence WHERE is_current");
            if (current.Id == terms.Id) return;
            if (current.Id is not null && current.IssuedAt > terms.IssuedAt.UtcDateTime)
                throw new KiteException(ErrorCodes.LicenceOlder, "A newer licence is already in use; this file is older.", 409, "fileText");
            var exists = await t.ExecuteScalarAsync<long>("SELECT count(*) FROM lic.licence WHERE id = @id", new { id = terms.Id }) > 0;
            await t.ExecuteAsync("UPDATE lic.licence SET is_current = false WHERE is_current");
            if (exists)
                await t.ExecuteAsync("UPDATE lic.licence SET is_current = true WHERE id = @id", new { id = terms.Id });
            else
                await t.ExecuteAsync("""
                    INSERT INTO lic.licence (id, installation_id, kind, customer, valid_until, grace_days, branches_allowed, terminals_allowed,
                                             modules, issued_at, file_text, source, imported_by, is_current)
                    VALUES (@Id, @InstallationId, @Kind, @Customer, @until, @GraceDays, @BranchesAllowed, @TerminalsAllowed,
                            @mods, @issued, @fileText, @source, @by, true)
                    """, new { terms.Id, terms.InstallationId, terms.Kind, terms.Customer, until = terms.ValidUntil.ToDateTime(TimeOnly.MinValue),
                               terms.GraceDays, terms.BranchesAllowed, terms.TerminalsAllowed, mods = terms.Modules.Where(known.Contains).ToArray(),
                               issued = terms.IssuedAt.UtcDateTime, fileText, source, by = actor.UserId });
            await t.AuditAsync(new AuditEntry("licence.imported", "licence", null, After: Json.Obj(new
            {
                terms.Id, terms.Kind, terms.Customer, validUntil = terms.ValidUntil.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                terms.BranchesAllowed, terms.TerminalsAllowed, terms.Modules, source,
            })));
        }, ct);
        Invalidate();
        log.LogInformation("Licence {Id} ({Kind}, until {Until}) is now in use", terms.Id, terms.Kind, terms.ValidUntil);
        return terms;
    }

    // ------------------------------------------------------------------------------------------ Nextdab commands
    /// <summary>
    /// Carries out one signed command at most once (NEXTDAB PROTOCOL 1, part 3). Returns the ack to send, or
    /// null when this command was already handled and acknowledged (standing settings come back every check-in).
    /// </summary>
    public async Task<JsonObject?> HandleCommandAsync(JsonObject raw, CancellationToken ct)
    {
        var mid = server.InstallationId ?? "";
        var cmd = NextdabCommand.Verify(raw, keys.PublicKey, Product, mid, clock.UtcNow, out var reason);
        var id = (raw["id"] as JsonValue)?.TryGetValue<string>(out var rid) == true ? rid : "";
        var type = (raw["type"] as JsonValue)?.TryGetValue<string>(out var rt) == true ? rt : "";
        if (id.Length is < 3 or > 80) return null;
        var done = await db.InTransactionAsync(AccessScope.System, ActorContext.System,
            t => t.QuerySingleOrDefaultAsync<(string? Id, DateTime? AckedAt)>("SELECT id, acked_at FROM lic.command WHERE id = @id", new { id }), ct);
        if (done.Id is not null) return done.AckedAt is null ? await AckOfAsync(id, ct) : null;

        bool ok; string msg;
        if (cmd is null) { ok = false; msg = "ignored: " + reason; }
        else
        {
            try { (ok, msg) = await ApplyAsync(cmd, "nextdab", ct); }
            catch (KiteException e) { ok = false; msg = e.Message; }
        }
        // A "web" command's tunnel token is kept only encrypted (lic.web_state): the stored copy of the command leaves it out.
        var stored = raw.DeepClone().AsObject();
        if (type == "web" && stored["data"] is JsonObject sd && sd.ContainsKey("token")) sd["token"] = "(kept encrypted)";
        await db.InTransactionAsync(AccessScope.System, ActorContext.System, async t =>
        {
            await t.ExecuteAsync("""
                INSERT INTO lic.command (id, type, seq, issued_at, body, ok, msg) VALUES (@id, @type, @seq, @issued, @body::jsonb, @ok, @msg)
                ON CONFLICT (id) DO NOTHING
                """, new { id, type, seq = cmd?.Seq, issued = cmd?.Issued?.UtcDateTime, body = stored.ToJsonString(), ok, msg });
            if (cmd?.Type == "message" && ok)
                await t.ExecuteAsync("""
                    INSERT INTO lic.message (id, title, text, title_ar, text_ar, level, audience, until)
                    VALUES (@id, @title, @text, @titleAr, @textAr, @level, @audience, @until) ON CONFLICT (id) DO NOTHING
                    """, new { id, title = Str(cmd.Data, "title"), text = Str(cmd.Data, "text"), titleAr = Str(cmd.Data, "title_ar"), textAr = Str(cmd.Data, "text_ar"),
                               level = Str(cmd.Data, "level") == "important" ? "important" : "info", audience = Str(cmd.Data, "to") == "admin" ? "admin" : "all",
                               until = DateTime.TryParseExact(Str(cmd.Data, "until"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var u) ? u : (DateTime?)null });
            if (cmd is not null && cmd.Type != "checkin")
                await t.AuditAsync(new AuditEntry("licence.command", "licence", null, After: Json.Obj(new { id, type, ok, msg })));
        }, ct);
        return new JsonObject { ["id"] = id, ["type"] = type, ["ok"] = ok, ["msg"] = msg };
    }

    private async Task<JsonObject> AckOfAsync(string id, CancellationToken ct)
    {
        var r = await db.InTransactionAsync(AccessScope.System, ActorContext.System,
            t => t.QuerySingleOrDefaultAsync<(string Type, bool Ok, string? Msg)>("SELECT type, ok, msg FROM lic.command WHERE id = @id", new { id }), ct);
        return new JsonObject { ["id"] = id, ["type"] = r.Type, ["ok"] = r.Ok, ["msg"] = r.Msg ?? "" };
    }

    public Task MarkAckedAsync(IEnumerable<string> ids, CancellationToken ct) =>
        db.InTransactionAsync(AccessScope.System, ActorContext.System,
            t => t.ExecuteAsync("UPDATE lic.command SET acked_at = now() WHERE id = ANY(@ids) AND acked_at IS NULL", new { ids = ids.ToArray() }), ct);

    private async Task<(bool Ok, string Msg)> ApplyAsync(NextdabCommand cmd, string source, CancellationToken ct)
    {
        switch (cmd.Type)
        {
            case "suspend":
            case "resume":
            {
                var applied = await db.InTransactionAsync(AccessScope.System, ActorContext.System, async t =>
                {
                    var n = await t.ExecuteAsync("""
                        UPDATE lic.control_state SET suspended = @s, suspend_message = @m, suspend_message_ar = @ma, suspend_seq = @seq,
                               suspend_cmd = @raw::jsonb, updated_at = now()
                         WHERE suspend_seq < @seq
                        """, new { s = cmd.Type == "suspend", m = Str(cmd.Data, "message"), ma = Str(cmd.Data, "message_ar"), seq = cmd.Seq ?? 0, raw = cmd.Raw.ToJsonString() });
                    if (n > 0) await t.AuditAsync(new AuditEntry(cmd.Type == "suspend" ? "licence.suspended" : "licence.resumed", "licence", null,
                        After: Json.Obj(new { source, message = Str(cmd.Data, "message") })));
                    return n > 0;
                }, ct);
                Invalidate();
                return (true, applied ? (cmd.Type == "suspend" ? "switched off" : "switched on") : "already newer state");
            }
            case "modules":
            {
                var off = (cmd.Data["off"] as JsonArray ?? []).Select(x => (x as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null)
                    .Where(s => s is not null).Cast<string>().Where(s => ModuleCatalog.All.Any(m => m.Code == s && !m.AlwaysOn)).Distinct().ToArray();
                await db.InTransactionAsync(AccessScope.System, ActorContext.System, async t =>
                {
                    var n = await t.ExecuteAsync("""
                        UPDATE lic.control_state SET modules_off = @off, modules_seq = @seq, modules_cmd = @raw::jsonb, updated_at = now() WHERE modules_seq < @seq
                        """, new { off, seq = cmd.Seq ?? 0, raw = cmd.Raw.ToJsonString() });
                    if (n > 0) await t.AuditAsync(new AuditEntry("licence.modules_off", "licence", null, After: Json.Obj(new { source, off })));
                }, ct);
                Invalidate();
                return (true, off.Length == 0 ? "all modules allowed" : "off: " + string.Join(",", off));
            }
            case "license":
            {
                var file = Str(cmd.Data, "file") ?? throw new KiteException(ErrorCodes.LicenceFileInvalid, "The command has no licence file.", 400);
                var terms = await ImportAsync(file, "nextdab", ActorContext.System, ct);
                return (true, $"licence {terms.Id} in use until {terms.ValidUntil:yyyy-MM-dd}");
            }
            case "message": return (true, "shown");
            case "message_clear":
            {
                var target = Str(cmd.Data, "id") ?? "";
                await db.InTransactionAsync(AccessScope.System, ActorContext.System,
                    t => t.ExecuteAsync("UPDATE lic.message SET cleared_at = now() WHERE id = @target AND cleared_at IS NULL", new { target }), ct);
                return (true, "removed");
            }
            case "checkin": return (true, "");
            // The main server's internet address for the WhatsApp webhook (Nextdab Admin 5.19, like MOBILE STORE ERP's "web").
            case "web":
                if (source != "nextdab" || server.Role == "branch") return (false, "only the main server takes an internet address from Nextdab");
                return await web.ApplyAsync(cmd.Id, cmd.Data, ct);
            case "update": return (false, "updates are installed with the KITE installer (Phase 14)");
            default: return (false, "unknown command type");
        }
    }

    /// <summary>Branch servers: the same signed suspend/modules commands, received from head office.</summary>
    public async Task ApplyControlFromHeadOfficeAsync(JsonObject raw, CancellationToken ct)
    {
        var mid = await LicensedInstallationIdAsync(ct);
        var cmd = NextdabCommand.Verify(raw, keys.PublicKey, Product, mid, clock.UtcNow.AddYears(-10), out var reason);
        if (cmd is null || cmd.Type is not ("suspend" or "resume" or "modules")) { log.LogWarning("Ignored a control command from head office: {Reason}", reason); return; }
        await ApplyAsync(cmd, "head_office", ct);
    }

    public async Task<JsonArray> ControlCommandsForBranchAsync(long suspendSeq, long modulesSeq, CancellationToken ct)
    {
        var ctl = await db.InTransactionAsync(AccessScope.System, ActorContext.System, ControlAsync, ct);
        var list = new JsonArray();
        if (ctl?.SuspendCmd is { } s && ctl.SuspendSeq > suspendSeq) list.Add(JsonNode.Parse(s));
        if (ctl?.ModulesCmd is { } m && ctl.ModulesSeq > modulesSeq) list.Add(JsonNode.Parse(m));
        return list;
    }

    private static string? Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

/// <summary>
/// Checks in with Nextdab's control server (NEXTDAB PROTOCOL 1): 30 seconds after start, then every "next"
/// seconds; after an error every 30 minutes. Only the main/combined server checks in; it sends a report with
/// no data about the shop's customers, and carries out the signed commands it gets back.
/// </summary>
public sealed class NextdabCheckinService(KiteDb db, ServerContext server, LicenceService licence, KiteOptions opts, IClock clock,
    IHttpClientFactory httpFactory, WebAddressService web, ILogger<NextdabCheckinService> log) : BackgroundService
{
    private readonly SemaphoreSlim _now = new(0, 1);

    public void CheckInSoon() { if (_now.CurrentCount == 0) _now.Release(); }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var wait = TimeSpan.FromSeconds(30);
        while (!stop.IsCancellationRequested)
        {
            try { await _now.WaitAsync(wait, stop); }
            catch (OperationCanceledException) { break; }
            if (!opts.Licensing.CheckinEnabled || !server.IsConfigured || !server.IsHeadOffice) { wait = TimeSpan.FromMinutes(1); continue; }
            try { wait = await CheckInOnceAsync(stop); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                log.LogWarning("Nextdab check-in failed: {Error}", e.Message);
                await RecordAsync(false, e.Message, TimeSpan.FromMinutes(30), stop);
                wait = TimeSpan.FromMinutes(30);
            }
        }
    }

    public async Task<TimeSpan> CheckInOnceAsync(CancellationToken ct)
    {
        var key = await db.InTransactionAsync(AccessScope.System, ActorContext.System,
            t => t.ExecuteScalarAsync<string>("SELECT checkin_key FROM lic.control_state"), ct);
        var acks = await db.InTransactionAsync(AccessScope.System, ActorContext.System,
            t => t.QueryAsync<(string Id, string Type, bool Ok, string? Msg)>("SELECT id, type, ok, msg FROM lic.command WHERE acked_at IS NULL ORDER BY received_at LIMIT 100"), ct);
        var body = new JsonObject
        {
            ["product"] = LicenceService.Product,
            ["mid"] = server.InstallationId,
            ["key"] = key,
            ["report"] = await ReportAsync(ct),
            ["acks"] = new JsonArray(acks.Select(a => (JsonNode)new JsonObject { ["id"] = a.Id, ["type"] = a.Type, ["ok"] = a.Ok, ["msg"] = a.Msg ?? "" }).ToArray()),
        };
        var http = httpFactory.CreateClient("nextdab");
        using var res = await http.PostAsync(opts.Licensing.ControlUrl.TrimEnd('/') + "/v1/kme/checkin",
            new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"), ct);
        if (res.StatusCode == HttpStatusCode.Forbidden)
        {
            await RecordAsync(false, "Nextdab has another installation registered with this Machine ID (copied PC or reinstall). Ask Nextdab to reset the device key.", TimeSpan.FromMinutes(30), ct);
            return TimeSpan.FromMinutes(30);
        }
        res.EnsureSuccessStatusCode();
        var answer = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct)) as JsonObject ?? throw new InvalidOperationException("Nextdab answered with no data.");
        await licence.MarkAckedAsync(acks.Select(a => a.Id), ct);
        var next = answer["next"] is JsonValue nv && nv.TryGetValue<int>(out var n) ? Math.Clamp(n, 60, 7 * 24 * 3600) : 6 * 3600;
        var handled = 0;
        foreach (var c in (answer["commands"] as JsonArray ?? []).OfType<JsonObject>())
            if (await licence.HandleCommandAsync(c, ct) is not null) handled++;
        await RecordAsync(true, null, TimeSpan.FromSeconds(next), ct);
        // After carrying out commands, check in once more at once so Nextdab sees the results (protocol part 1).
        return handled > 0 ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(next);
    }

    private Task RecordAsync(bool ok, string? error, TimeSpan next, CancellationToken ct) =>
        db.InTransactionAsync(AccessScope.System, ActorContext.System, t => t.ExecuteAsync("""
            UPDATE lic.control_state SET last_checkin_at = now(), last_checkin_ok_at = CASE WHEN @ok THEN now() ELSE last_checkin_ok_at END,
                   last_error = @error, next_checkin_at = now() + make_interval(secs => @secs), updated_at = now()
            """, new { ok, error = error is null ? null : error[..Math.Min(error.Length, 500)], secs = (int)next.TotalSeconds }), ct);

    /// <summary>What Nextdab Admin shows (protocol part 2). Never the shop's customers, sales or stock.</summary>
    public async Task<JsonObject> ReportAsync(CancellationToken ct)
    {
        var s = await licence.StatusAsync(ct);
        var r = await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => t.QuerySingleOrDefaultAsync<ReportRow>("""
            SELECT b.legal_name_en AS Company, b.address_en AS Address, b.phone AS Phone, b.email AS Email, b.cr_number AS CrNo, b.vat_number AS Vatin,
                   (SELECT count(*) FROM core.branch WHERE status = 'active') AS Branches,
                   (SELECT count(*) FROM core.server WHERE role = 'branch' AND status = 'active') AS BranchServers,
                   core.active_terminal_count() AS Terminals,
                   (SELECT count(*) FROM iam."user") AS Users,
                   (SELECT count(*) FROM iam."user" WHERE status = 'active') AS UsersActive,
                   (SELECT count(DISTINCT user_id) FROM iam.session WHERE created_at > now() - interval '30 days') AS Logins30d,
                   (SELECT u.display_name_en FROM iam."user" u JOIN iam.user_branch_role ubr ON ubr.user_id = u.id JOIN iam.role ro ON ro.id = ubr.role_id
                     WHERE ro.code = 'owner' ORDER BY u.created_at LIMIT 1) AS AdminName,
                   (SELECT max(finished_at) FROM ops.backup_run WHERE status = 'succeeded' AND kind IN ('logical','base')) AS LastBackup,
                   (SELECT status FROM ops.backup_run WHERE kind IN ('logical','base') ORDER BY started_at DESC LIMIT 1) AS BackupStatus
              FROM core.business b
            """), ct);
        var states = await db.InTransactionAsync(AccessScope.System, ActorContext.System, async t =>
            (await t.QueryAsync<(string Code, bool Enabled)>("SELECT module_code, enabled FROM core.module_setting WHERE branch_id IS NULL")).ToDictionary(x => x.Code, x => x.Enabled), ct);
        var off = await licence.ModulesOffAsync(ct);
        var lic = new JsonObject
        {
            ["status"] = s.State switch { LicenceState.None => "unlicensed", LicenceState.Expiring => "active", LicenceState.Grace => "grace", _ => s.State.ToString().ToLowerInvariant() },
            ["licensed_until"] = s.Terms?.ValidUntil.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["days_left"] = s.DaysLeft,
            ["kind"] = s.Terms?.Kind,
            ["licence_id"] = s.Terms?.Id,
            ["branches_allowed"] = s.BranchesAllowed,
            ["terminals_allowed"] = s.TerminalsAllowed,
        };
        return new JsonObject
        {
            ["product"] = LicenceService.Product,
            ["version"] = AppInfo.Version,
            ["time"] = clock.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ["role"] = server.Role,
            ["company"] = r?.Company, ["address"] = r?.Address, ["phone"] = r?.Phone, ["email"] = r?.Email,
            ["cr_no"] = r?.CrNo, ["vatin"] = r?.Vatin, ["currency"] = "OMR", ["admin_name"] = r?.AdminName,
            ["license"] = lic,
            ["counts"] = new JsonObject
            {
                ["branches"] = r?.Branches, ["branch_servers"] = r?.BranchServers, ["terminals"] = r?.Terminals,
                ["users"] = r?.Users, ["users_active"] = r?.UsersActive, ["logins_30d"] = r?.Logins30d,
            },
            ["modules"] = new JsonArray(ModuleCatalog.All.Select(m => (JsonNode)new JsonObject
            {
                ["key"] = m.Code, ["name"] = m.NameEn, ["enabled"] = m.AlwaysOn || (states.TryGetValue(m.Code, out var e) && e && !off.Contains(m.Code)),
            }).ToArray()),
            ["modules_off"] = new JsonArray(off.Select(o => (JsonNode)o).ToArray()),
            ["suspended"] = s.State == LicenceState.Suspended,
            ["backup"] = new JsonObject { ["last"] = r?.LastBackup?.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture), ["status"] = r?.BackupStatus ?? "none" },
            ["pc"] = new JsonObject { ["name"] = Environment.MachineName, ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription },
            // The internet address for the WhatsApp webhook (no token): what Nextdab Admin needs to make and watch it.
            ["web"] = server.Role == "branch" ? null : await web.ReportAsync(ct),
        };
    }

    private sealed record ReportRow(string Company, string? Address, string? Phone, string? Email, string? CrNo, string? Vatin, long Branches, long BranchServers,
        long Terminals, long Users, long UsersActive, long Logins30d, string? AdminName, DateTime? LastBackup, string? BackupStatus);
}

/// <summary>While Nextdab has switched KITE off, only signing in, the licence, backups and health keep working (24.5).</summary>
public sealed class SuspensionMiddleware(RequestDelegate next)
{
    private static readonly string[] Allowed =
        ["/api/v1/auth", "/api/v1/licence", "/api/v1/backups", "/api/v1/setup", "/api/v1/servers", "/health", "/sync"];

    public async Task InvokeAsync(HttpContext ctx, LicenceService licence)
    {
        var path = ctx.Request.Path;
        if (path.StartsWithSegments("/api") && !Allowed.Any(a => path.StartsWithSegments(a)))
        {
            var s = await licence.StatusAsync(ctx.RequestAborted);
            if (s.State == LicenceState.Suspended)
                throw new KiteException(ErrorCodes.Suspended, s.SuspendMessage ?? "KITE is switched off by Nextdab. Please contact Nextdab.", 403);
        }
        await next(ctx);
    }
}

public static class LicenceEndpoints
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/licence");

        // Every signed-in user: the state for banners, and Nextdab messages meant for everybody.
        g.MapGet("/status", async (HttpContext http, KiteDb db, LicenceService licence, CancellationToken ct) =>
        {
            var s = await licence.StatusAsync(ct);
            var admin = http.User.Has(Permissions.LicenceManage);
            var msgs = await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => t.QueryAsync<MessageDto>("""
                SELECT id, title, text, title_ar AS TitleAr, text_ar AS TextAr, level, audience, until, received_at AS ReceivedAt
                  FROM lic.message WHERE cleared_at IS NULL AND (until IS NULL OR until >= current_date) AND (audience = 'all' OR @admin)
                 ORDER BY received_at DESC LIMIT 20
                """, new { admin }), ct);
            return Results.Ok(new
            {
                state = s.State.ToString().ToLowerInvariant(), s.DaysLeft, endsOn = s.EndsOn, validUntil = s.Terms?.ValidUntil, kind = s.Terms?.Kind,
                s.CanTransact, s.SuspendMessage, s.SuspendMessageAr, messages = msgs,
            });
        }).RequireSignedIn();

        g.MapGet("", async (KiteDb db, ServerContext server, LicenceService licence, LicenceKeys keys, CancellationToken ct) =>
        {
            var s = await licence.StatusAsync(ct);
            var use = await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => t.QuerySingleOrDefaultAsync<(long Branches, long Terminals)>(
                "SELECT (SELECT count(*) FROM core.branch WHERE status = 'active'), core.active_terminal_count()"), ct);
            var conn = await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => t.QuerySingleOrDefaultAsync<ConnDto>("""
                SELECT last_checkin_at AS LastCheckinAt, last_checkin_ok_at AS LastCheckinOkAt, last_error AS LastError, next_checkin_at AS NextCheckinAt,
                       modules_off AS ModulesOff FROM lic.control_state
                """), ct);
            var history = await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => t.QueryAsync<HistoryDto>(
                "SELECT id, kind, customer, valid_until AS ValidUntil, source, imported_at AS ImportedAt, is_current AS IsCurrent FROM lic.licence ORDER BY imported_at DESC LIMIT 20"), ct);
            return Results.Ok(new
            {
                state = s.State.ToString().ToLowerInvariant(), terms = s.Terms, s.DaysLeft, endsOn = s.EndsOn, s.BranchesAllowed, s.TerminalsAllowed, s.CanTransact,
                s.SuspendMessage, s.SuspendMessageAr, branchesUsed = use.Branches, terminalsUsed = use.Terminals,
                installationId = await licence.LicensedInstallationIdAsync(ct), role = server.Role, keyConfigured = keys.IsConfigured,
                connection = server.IsHeadOffice ? conn : null, history,
            });
        }).RequirePermission(Permissions.LicenceManage);

        g.MapPost("/import", async (ImportLicenceRequest req, HttpContext http, LicenceService licence, CancellationToken ct) =>
        {
            var terms = await licence.ImportAsync(req.FileText, "import", http.ActorOf(), ct);
            return Results.Ok(new { terms.Id, terms.Kind, terms.ValidUntil });
        }).RequirePermission(Permissions.LicenceManage);

        g.MapPost("/checkin", (ServerContext server, NextdabCheckinService checkin) =>
        {
            if (!server.IsHeadOffice) throw new KiteException(ErrorCodes.NotHeadOffice, "Branch servers receive the licence from head office.", 409);
            checkin.CheckInSoon();
            return Results.Accepted();
        }).RequirePermission(Permissions.LicenceManage);
    }

    private sealed record MessageDto(string Id, string? Title, string? Text, string? TitleAr, string? TextAr, string Level, string Audience, DateTime? Until, DateTime ReceivedAt);
    private sealed class ConnDto   // a class, not a positional record: see LicenceRow
    {
        public DateTime? LastCheckinAt { get; init; }
        public DateTime? LastCheckinOkAt { get; init; }
        public string? LastError { get; init; }
        public DateTime? NextCheckinAt { get; init; }
        public string[] ModulesOff { get; init; } = [];
    }
    private sealed record HistoryDto(string Id, string Kind, string Customer, DateTime ValidUntil, string Source, DateTime ImportedAt, bool IsCurrent);
}
