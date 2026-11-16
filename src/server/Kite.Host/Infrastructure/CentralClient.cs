using System.Net.Http.Json;
using System.Text.Json;
using Kite.Host.Features;
using Kite.Persistence;
using Kite.Platform;

namespace Kite.Host.Infrastructure;

public sealed record CentralCheckResponse(List<CentralCheckResult> Results);

/// <summary>What head office knows about an identifier across the business (11.5: trade-in and repair intake look-ups).</summary>
public sealed record CentralLookupResponse(string Kind, string Value, List<CentralHolder> Holders, string? BlockedReason, List<Guid> OpenConflicts);

/// <summary>Head office's side of the device exchange with branch servers: instructions, the blocked list and look-ups.</summary>
public static class CentralSync
{
    public static async Task<List<CentralInstructionDto>> InstructionsForAsync(DbTx t, Guid serverId, long afterSeq) =>
        (await t.QueryAsync<CentralInstructionDto>("""
            SELECT seq, kind, device_unit_id AS UnitId, conflict_id AS ConflictId, value, note FROM device.central_instruction
             WHERE server_id = @serverId AND seq > @afterSeq ORDER BY seq LIMIT 500
            """, new { serverId, afterSeq })).ToList();

    public static async Task<List<BlockedDto>> BlockedSinceAsync(DbTx t, long afterSeq) =>
        (await t.QueryAsync<BlockedDto>("""
            SELECT id, kind, value, reason, reference, status, seq, added_at AS AddedAt, added_by_name AS AddedByName, removed_at AS RemovedAt,
                   removed_reason AS RemovedReason
              FROM device.blocked_imei WHERE seq > @afterSeq ORDER BY seq LIMIT 2000
            """, new { afterSeq })).ToList();

    public static async Task<CentralLookupResponse> LookupAsync(DbTx t, string kind, string value)
    {
        var holders = (await t.QueryAsync<CentralHolder>("""
            SELECT r.server_id AS ServerId, r.branch_id AS BranchId, b.code AS BranchCode, b.name_en AS BranchNameEn, b.name_ar AS BranchNameAr,
                   r.device_unit_id AS UnitId, coalesce(u.status, r.unit_status) AS UnitStatus, coalesce(u.condition, r.condition) AS Condition,
                   r.description_en AS DescriptionEn, r.description_ar AS DescriptionAr, r.received_at AS ReceivedAt, r.status AS RowStatus
              FROM device.central_registry r JOIN core.branch b ON b.id = r.branch_id
              LEFT JOIN device.device_unit u ON u.id = r.device_unit_id AND r.server_id = (SELECT id FROM core.server WHERE is_self)
             WHERE r.value = @value AND (CASE WHEN @kind = 'serial' THEN r.kind = 'serial' ELSE r.kind IN ('imei','eid') END)
             ORDER BY (r.status = 'retired'), r.registered_at
            """, new { kind, value })).ToList();
        var conflicts = (await t.QueryAsync<Guid>("""
            SELECT id FROM device.imei_conflict WHERE value = @value AND status = 'open'
            """, new { value })).ToList();
        return new CentralLookupResponse(kind, value, holders, await CentralRegistry.BlockedReasonAsync(t, kind, value), conflicts);
    }

    /// <summary>The device endpoints of the sync port (head office): online check, registration of units, look-up.</summary>
    public static void Map(RouteGroupBuilder sync)
    {
        sync.MapPost("/imei/check", async (CentralCheckRequest req, HttpContext http, KiteDb db, ServerContext server, PkiService pki, CancellationToken ct) =>
        {
            await AuthenticateAsync(http, server, pki, ct);
            if (req.Identifiers is null || req.Identifiers.Count > 500) throw new KiteException(ErrorCodes.Validation, "Send 1 to 500 identifiers.", 400);
            var results = await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => CentralRegistry.CheckAsync(t, req.Identifiers, req.ExceptUnitId), ct);
            return Results.Ok(new CentralCheckResponse(results));
        }).AllowEveryone();

        sync.MapPost("/imei/register", async (CentralRegisterRequest req, HttpContext http, KiteDb db, ServerContext server, PkiService pki, CancellationToken ct) =>
        {
            var who = await AuthenticateAsync(http, server, pki, ct);
            if (req.Units is null || req.Units.Count > 500) throw new KiteException(ErrorCodes.Validation, "Send 1 to 500 units.", 400);
            foreach (var u in req.Units)
            {
                // One unit per transaction, so one bad unit never holds back the others; retried when two servers touch the same identifiers.
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        await db.InTransactionAsync(AccessScope.System, ActorContext.System,
                            t => CentralRegistry.UpsertAsync(t, server.BusinessId!.Value, who.ServerId, who.BranchId, u), ct);
                        break;
                    }
                    catch (Npgsql.PostgresException e) when (e.SqlState is "40P01" or "40001" or "23505" && attempt < 6)
                    {
                        await Task.Delay(Random.Shared.Next(10, 60) * attempt, ct);
                    }
                }
            }
            // The answer is read after every unit is stored, stamped so the branch can order it against instructions (ADR 34).
            return Results.Ok(await db.InTransactionAsync(AccessScope.System, ActorContext.System,
                t => CentralRegistry.AnswerAsync(t, who.ServerId, req.Units.Select(u => u.UnitId).Distinct()), ct));
        }).AllowEveryone();

        sync.MapPost("/imei/lookup", async (CentralLookupRequest req, HttpContext http, KiteDb db, ServerContext server, PkiService pki, CancellationToken ct) =>
        {
            await AuthenticateAsync(http, server, pki, ct);
            var code = DeviceUnits.Classify(req.Code);
            return Results.Ok(await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => LookupAsync(t, code.Kind, code.Value), ct));
        }).AllowEveryone();
    }

    private static async Task<PkiService.BranchIdentity> AuthenticateAsync(HttpContext http, ServerContext server, PkiService pki, CancellationToken ct)
    {
        if (!server.IsConfigured || !server.IsHeadOffice) throw new KiteException(ErrorCodes.NotHeadOffice, "This is not a main server.", 404);
        return await pki.AuthenticateAsync(http.Connection.ClientCertificate ?? await http.Connection.GetClientCertificateAsync(ct), ct);
    }
}

/// <summary>
/// A branch server's side (11.4): asks head office before a receipt while it can reach it, pushes its units (received
/// offline, corrected, sold, reversed) and applies what head office sends back: holds and releases for IMEI conflicts and
/// changes to the blocked list. On a main or combined server everything is local and this does nothing.
/// </summary>
public sealed class CentralClient(KiteDb db, ServerContext server, BranchLinkService link, ILogger<CentralClient> log)
{
    public bool IsBranch => server.Role == "branch";

    /// <summary>Head office's answer for these identifiers, or null when it cannot be reached (the receipt then goes ahead, pending).</summary>
    public async Task<List<CentralCheckResult>?> CheckAsync(IReadOnlyList<PushIdentifier> ids, Guid? exceptUnit, CancellationToken ct)
    {
        if (!IsBranch || ids.Count == 0 || !link.HeadOfficeLikelyReachable) return null;
        var c = await link.MainClientAsync(TimeSpan.FromSeconds(4), ct);
        if (c is null) return null;
        using var http = c.Value.Http;
        try
        {
            var r = await http.PostAsJsonAsync(c.Value.SyncUrl + "/sync/v1/imei/check", new CentralCheckRequest(ids.ToList(), exceptUnit), ct);
            if (!r.IsSuccessStatusCode) return null;
            return (await r.Content.ReadFromJsonAsync<CentralCheckResponse>(ct))?.Results;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            log.LogInformation("Head office did not answer the IMEI check: {Error}", e.Message);
            return null;
        }
    }

    /// <summary>What head office knows about an identifier, or null when it cannot be reached.</summary>
    public async Task<CentralLookupResponse?> LookupAsync(string code, CancellationToken ct)
    {
        if (!IsBranch) return null;
        var c = await link.MainClientAsync(TimeSpan.FromSeconds(4), ct);
        if (c is null) return null;
        using var http = c.Value.Http;
        try
        {
            var r = await http.PostAsJsonAsync(c.Value.SyncUrl + "/sync/v1/imei/lookup", new CentralLookupRequest(code), ct);
            return r.IsSuccessStatusCode ? await r.Content.ReadFromJsonAsync<CentralLookupResponse>(ct) : null;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException || (e is TaskCanceledException && !ct.IsCancellationRequested)) { return null; }
    }

    public async Task<(long InstructionSeq, long BlockedSeq)> SeqsAsync(CancellationToken ct) =>
        await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => t.QuerySingleOrDefaultAsync<(long, long)>("""
            SELECT coalesce((SELECT instruction_seq FROM device.central_link LIMIT 1), 0), coalesce((SELECT max(seq) FROM device.blocked_imei), 0)
            """), ct);

    /// <summary>
    /// Pushes units to head office: the given ones, or every unit that is pending or changed since its last push. Returns how
    /// many head office accepted; failures leave the units pending and are retried with the next heartbeat.
    /// </summary>
    public async Task<int> PushAsync(IReadOnlyCollection<Guid>? unitIds, CancellationToken ct)
    {
        if (!IsBranch) return 0;
        var done = 0;
        for (var batch = 0; batch < 10; batch++)
        {
            var pushes = await db.InTransactionAsync(AccessScope.System, ActorContext.System, async t =>
            {
                var ids = unitIds is not null ? unitIds.ToList() : (await t.QueryAsync<Guid>("""
                    SELECT c.device_unit_id FROM device.central_check c
                     WHERE c.state = 'pending' OR c.pushed_seq < (SELECT coalesce(max(e.seq), 0) FROM device.device_event e WHERE e.device_unit_id = c.device_unit_id)
                     ORDER BY c.updated_at LIMIT 200
                    """)).ToList();
                var list = new List<(CentralUnitPush Push, int Seq)>();
                foreach (var id in ids)
                    if (await CentralRegistry.PushOfAsync(t, id) is { } p)
                        list.Add((p, await t.ExecuteScalarAsync<int>("SELECT coalesce(max(seq), 0) FROM device.device_event WHERE device_unit_id = @id", new { id })));
                return list;
            }, ct);
            if (pushes.Count == 0) return done;

            var c = await link.MainClientAsync(TimeSpan.FromSeconds(15), ct);
            if (c is null) return done;
            CentralRegisterResponse? answer = null;
            string? error = null;
            using (var http = c.Value.Http)
            {
                try
                {
                    var r = await http.PostAsJsonAsync(c.Value.SyncUrl + "/sync/v1/imei/register", new CentralRegisterRequest(pushes.Select(p => p.Push).ToList()), ct);
                    if (r.IsSuccessStatusCode) answer = await r.Content.ReadFromJsonAsync<CentralRegisterResponse>(ct);
                    else error = $"head office answered {(int)r.StatusCode}: {await r.Content.ReadAsStringAsync(ct)}";
                }
                catch (Exception e) when (e is HttpRequestException or JsonException || (e is TaskCanceledException && !ct.IsCancellationRequested)) { error = e.Message; }
            }
            await db.InTransactionAsync(AccessScope.System, ActorContext.System, async t =>
            {
                await t.ExecuteAsync("""
                    INSERT INTO device.central_link (business_id, last_push_at, last_push_ok_at, last_push_error)
                    VALUES ((SELECT id FROM core.business LIMIT 1), now(), CASE WHEN @ok THEN now() END, @error)
                    ON CONFLICT (business_id) DO UPDATE SET last_push_at = now(), last_push_ok_at = CASE WHEN @ok THEN now() ELSE device.central_link.last_push_ok_at END,
                           last_push_error = @error, updated_at = now()
                    """, new { ok = answer is not null, error });
                if (answer is null)
                {
                    await t.ExecuteAsync("""
                        UPDATE device.central_check SET attempts = attempts + 1, last_error = @error, updated_at = now() WHERE device_unit_id = ANY(@ids)
                        """, new { error, ids = pushes.Select(p => p.Push.UnitId).ToArray() });
                    return;
                }
                foreach (var result in answer.Results)
                {
                    var seq = pushes.First(p => p.Push.UnitId == result.UnitId).Seq;
                    await CentralRegistry.ApplyResultAsync(t, result, seq, answer.InstructionSeq);
                }
            }, ct);
            if (answer is null) { log.LogWarning("Pushing devices to head office failed: {Error}", error); return done; }
            done += answer.Results.Count;
            if (unitIds is not null || pushes.Count < 200) return done;
        }
        return done;
    }

    /// <summary>Applies head office's instructions (in order, once) and its blocked-list changes.</summary>
    public async Task ApplyFromHeadOfficeAsync(IReadOnlyList<CentralInstructionDto> instructions, IReadOnlyList<BlockedDto> blocked, CancellationToken ct)
    {
        if (!IsBranch || (instructions.Count == 0 && blocked.Count == 0)) return;
        await db.InTransactionAsync(AccessScope.System, ActorContext.System, async t =>
        {
            var applied = await t.ExecuteScalarAsync<long>("SELECT coalesce((SELECT instruction_seq FROM device.central_link LIMIT 1), 0)");
            foreach (var i in instructions.Where(i => i.Seq > applied).OrderBy(i => i.Seq))
            {
                applied = i.Seq;
                // An instruction older than head office's last answer about this unit is already reflected in it.
                var stamp = await t.ExecuteScalarAsync<long?>("SELECT stamp FROM device.central_check WHERE device_unit_id = @UnitId FOR UPDATE", new { i.UnitId });
                if (stamp is not { } s0 || i.Seq <= s0) continue;
                if (i.Kind == "hold") await UnitHolds.HoldAsync(t, i.UnitId, "imei_conflict", i.ConflictId, null, i.Note ?? $"IMEI {i.Value} is also registered to another device.");
                else await UnitHolds.ReleaseAsync(t, i.UnitId, "imei_conflict", i.ConflictId, i.Note ?? "The IMEI conflict is resolved.");
                await t.ExecuteAsync("UPDATE device.central_check SET stamp = @Seq, updated_at = now() WHERE device_unit_id = @UnitId", new { i.Seq, i.UnitId });
            }
            await t.ExecuteAsync("""
                INSERT INTO device.central_link (business_id, instruction_seq) VALUES ((SELECT id FROM core.business LIMIT 1), @applied)
                ON CONFLICT (business_id) DO UPDATE SET instruction_seq = greatest(device.central_link.instruction_seq, @applied), updated_at = now()
                """, new { applied });
            foreach (var b in blocked.OrderBy(b => b.Seq))
            {
                await t.ExecuteAsync("""
                    INSERT INTO device.blocked_imei (id, business_id, kind, value, reason, reference, status, added_at, added_by_name, removed_at, removed_reason, seq)
                    VALUES (@Id, (SELECT id FROM core.business LIMIT 1), @Kind, @Value, @Reason, @Reference, @Status, @AddedAt, @AddedByName, @RemovedAt, @RemovedReason, @Seq)
                    ON CONFLICT (id) DO UPDATE SET status = EXCLUDED.status, removed_at = EXCLUDED.removed_at, removed_reason = EXCLUDED.removed_reason, seq = EXCLUDED.seq
                    """, b);
                await BlockedList.ApplyToUnitsAsync(t, b.Id, b.Kind, b.Value, b.Status == "active", b.Reason);
            }
        }, ct);
    }
}

/// <summary>The blocked-IMEI list's effect on units in the business: a listed identifier puts its unit on hold; removing it releases.</summary>
public static class BlockedList
{
    public static async Task ApplyToUnitsAsync(DbTx t, Guid blockedId, string kind, string value, bool active, string reason)
    {
        var units = await t.QueryAsync<Guid>("""
            SELECT r.device_unit_id FROM device.imei_registry r
             WHERE r.status = 'active' AND r.value = @value AND (CASE WHEN @kind = 'serial' THEN r.kind = 'serial' ELSE r.kind IN ('imei','eid') END)
            """, new { kind, value });
        foreach (var u in units)
        {
            if (active) await UnitHolds.HoldAsync(t, u, "blocked_imei", null, blockedId, $"On the blocked list: {reason}");
            else await UnitHolds.ReleaseAsync(t, u, "blocked_imei", blockedId, "Removed from the blocked list.");
        }
    }
}
