using System.Text.RegularExpressions;
using Kite.Devices;
using Kite.Persistence;
using Kite.Platform;

namespace Kite.Host.Infrastructure;

/// <summary>One identifier of a unit as servers exchange it: IMEI 1 or 2, a serial number (with its brand) or an EID.</summary>
public sealed record PushIdentifier(string Kind, short Slot, string Value, string? Brand = null);

/// <summary>A unit as a server reports it to head office: what it is, where it stands and its current identifiers.</summary>
public sealed record CentralUnitPush(Guid UnitId, string Status, string Condition, string DescriptionEn, string DescriptionAr, DateTimeOffset? ReceivedAt,
    List<PushIdentifier> Identifiers);
public sealed record CentralRegisterRequest(List<CentralUnitPush> Units);
public sealed record CentralCheckRequest(List<PushIdentifier> Identifiers, Guid? ExceptUnitId = null);
public sealed record CentralLookupRequest(string Code);

/// <summary>The unit that holds an identifier, as head office knows it.</summary>
public sealed record CentralHolder(Guid ServerId, Guid BranchId, string BranchCode, string BranchNameEn, string BranchNameAr, Guid UnitId, string UnitStatus,
    string Condition, string DescriptionEn, string DescriptionAr, DateTime? ReceivedAt, string RowStatus);
public sealed record CentralCheckResult(string Kind, string Value, string State, CentralHolder? Holder, string? BlockedReason);
public sealed record CentralConflictInfo(Guid ConflictId, string Kind, string Value, List<CentralHolder> Others);
public sealed record CentralUnitResult(Guid UnitId, string State, List<CentralConflictInfo> Conflicts);
/// <summary>InstructionSeq: every instruction to this server up to this number is reflected in the results.</summary>
public sealed record CentralRegisterResponse(List<CentralUnitResult> Results, long InstructionSeq = 0);
public sealed record CentralInstructionDto(long Seq, string Kind, Guid UnitId, Guid ConflictId, string Value, string? Note);
public sealed record BlockedDto(Guid Id, string Kind, string Value, string Reason, string? Reference, string Status, long Seq, DateTime AddedAt, string? AddedByName,
    DateTime? RemovedAt, string? RemovedReason);

/// <summary>
/// The central IMEI registry (blueprint 11.1, 11.4, 8.2), run on the main or combined server. Every identifier the business has
/// handled is a row with its unit, server and branch. The first unit holds an identifier; a later one (received offline at a
/// branch) is stored as a conflict, both units go on hold, and the conflict closes by itself once only one unit still claims
/// the identifier. Work on one identifier is serialised with an advisory lock, taken in a fixed order.
/// </summary>
public static class CentralRegistry
{
    public sealed record Key(string Kind, string Value, string BrandName)
    {
        public string LockKey => Kind == "serial" ? $"s:{BrandName}:{Value}" : $"i:{Value}";
        public static Key Of(PushIdentifier i) => new(i.Kind, i.Value, i.Kind == "serial" ? NormBrand(i.Brand) : "");
    }

    public static string NormBrand(string? brand) => (brand ?? "").Trim().ToLowerInvariant();

    /// <summary>Locks these identifiers on this server for the rest of the transaction (re-entrant), in one order so two documents never deadlock.</summary>
    public static async Task LockAsync(DbTx t, IEnumerable<Key> keys)
    {
        foreach (var k in keys.Select(k => k.LockKey).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            await t.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(@k, 6))", new { k });
    }

    private const string HolderSelect = """
        SELECT r.server_id AS ServerId, r.branch_id AS BranchId, b.code AS BranchCode, b.name_en AS BranchNameEn, b.name_ar AS BranchNameAr,
               r.device_unit_id AS UnitId, coalesce(u.status, r.unit_status) AS UnitStatus, coalesce(u.condition, r.condition) AS Condition,
               r.description_en AS DescriptionEn, r.description_ar AS DescriptionAr, r.received_at AS ReceivedAt, r.status AS RowStatus
          FROM device.central_registry r JOIN core.branch b ON b.id = r.branch_id
          LEFT JOIN device.device_unit u ON u.id = r.device_unit_id AND r.server_id = (SELECT id FROM core.server WHERE is_self)
        """;

    /// <summary>Who holds each identifier anywhere in the business (any unit other than <paramref name="exceptUnit"/>), and the blocked list.</summary>
    public static async Task<List<CentralCheckResult>> CheckAsync(DbTx t, IReadOnlyList<PushIdentifier> ids, Guid? exceptUnit)
    {
        var results = new List<CentralCheckResult>();
        foreach (var i in ids)
        {
            var k = Key.Of(i);
            var holder = await t.QuerySingleOrDefaultAsync<CentralHolder>(HolderSelect + """
                 WHERE r.business_id = (SELECT id FROM core.business LIMIT 1) AND r.status <> 'retired' AND r.value = @Value
                   AND (CASE WHEN r.kind = 'serial' THEN @Kind = 'serial' AND r.brand_name = @BrandName ELSE @Kind <> 'serial' END)
                   AND (@except::uuid IS NULL OR r.device_unit_id <> @except)
                 ORDER BY (r.status = 'active') DESC, r.registered_at LIMIT 1
                """, new { k.Value, k.Kind, k.BrandName, except = exceptUnit });
            var blocked = await BlockedReasonAsync(t, i.Kind, i.Value);
            results.Add(new CentralCheckResult(i.Kind, i.Value, blocked is not null ? "blocked" : holder is not null ? "held" : "free", holder, blocked));
        }
        return results;
    }

    public static Task<string?> BlockedReasonAsync(DbTx t, string kind, string value) =>
        t.ExecuteScalarAsync<string?>("""
            SELECT reason || coalesce(' (' || reference || ')', '') FROM device.blocked_imei
             WHERE status = 'active' AND value = @value AND kind = CASE WHEN @kind = 'serial' THEN 'serial' ELSE 'imei' END LIMIT 1
            """, new { kind, value });

    /// <summary>The words for a holder, for messages: "in stock at B02 (iPhone 13 128GB)".</summary>
    public static string Describe(CentralHolder h)
    {
        var status = h.UnitStatus switch
        {
            "sold" => "sold", "in_stock" => "in stock", "quarantined" => "on hold", "written_off" => "written off", "reversed" => "a reversed receipt",
            "supplier_returned" => "returned to the supplier", _ => h.UnitStatus.Replace('_', ' '),
        };
        return $"{status} at {h.BranchCode} ({h.DescriptionEn})";
    }

    /// <summary>
    /// Records a server's unit at head office: its current identifiers and status. New identifiers held by another unit
    /// become conflicts (both units on hold); identifiers the unit no longer has (corrected, or its receipt reversed) are
    /// retired, which can close a conflict and release the other unit. Idempotent: pushing the same state twice changes nothing.
    /// </summary>
    public static async Task<CentralUnitResult> UpsertAsync(DbTx t, Guid businessId, Guid serverId, Guid branchId, CentralUnitPush u)
    {
        var desired = u.Status == "reversed" ? [] : u.Identifiers.Where(i => i.Value.Length > 0).ToList();
        foreach (var i in desired)
        {
            if (i.Kind is not ("imei" or "serial" or "eid") || i.Slot is not (1 or 2))
                throw new KiteException(ErrorCodes.Validation, $"Unit {u.UnitId}: identifiers are IMEI, serial or EID, in slot 1 or 2.", 400);
            if (i.Kind == "imei" && !DeviceMath.ImeiValid(i.Value))
                throw new KiteException(ErrorCodes.ImeiInvalid, $"Unit {u.UnitId}: {i.Value} is not a valid IMEI.", 400);
        }
        var existing = (await t.QueryAsync<(Guid Id, string Kind, short Slot, string Value, string BrandName, string Status)>("""
            SELECT id, kind, slot, value, brand_name, status FROM device.central_registry
             WHERE server_id = @serverId AND device_unit_id = @unit AND status <> 'retired'
            """, new { serverId, unit = u.UnitId })).ToList();
        var keys = desired.Select(Key.Of).Concat(existing.Select(e => new Key(e.Kind, e.Value, e.BrandName))).ToList();
        await LockAsync(t, keys);
        var touched = new List<Key>();

        foreach (var e in existing)
        {
            var still = desired.Any(d => d.Kind == e.Kind && d.Slot == e.Slot && d.Value == e.Value && Key.Of(d).BrandName == e.BrandName);
            if (still) continue;
            await t.ExecuteAsync("UPDATE device.central_registry SET status = 'retired', retired_at = now(), updated_at = now() WHERE id = @id", new { e.Id });
            await t.ExecuteAsync("""
                UPDATE device.imei_conflict_side SET outcome = @outcome, outcome_at = now() WHERE registry_id = @id AND outcome IS NULL
                """, new { e.Id, outcome = u.Status == "reversed" ? "reversed" : "corrected" });
            touched.Add(new Key(e.Kind, e.Value, e.BrandName));
        }
        foreach (var d in desired)
        {
            var k = Key.Of(d);
            var mine = existing.FirstOrDefault(e => e.Kind == d.Kind && e.Slot == d.Slot && e.Value == d.Value && e.BrandName == k.BrandName);
            if (mine.Id != Guid.Empty)
            {
                await t.ExecuteAsync("""
                    UPDATE device.central_registry SET unit_status = @Status, condition = @Condition, description_en = @DescriptionEn,
                           description_ar = @DescriptionAr, branch_id = @branchId, updated_at = now()
                     WHERE id = @id
                    """, new { id = mine.Id, u.Status, u.Condition, u.DescriptionEn, u.DescriptionAr, branchId });
                continue;
            }
            var holders = (await t.QueryAsync<(Guid Id, string UnitStatus, string Status)>("""
                SELECT id, unit_status, status FROM device.central_registry WHERE business_id = @businessId AND status <> 'retired' AND value = @Value
                   AND (CASE WHEN kind = 'serial' THEN @Kind = 'serial' AND brand_name = @BrandName ELSE @Kind <> 'serial' END)
                """, new { businessId, k.Value, k.Kind, k.BrandName })).ToList();
            var taken = holders.Count > 0;
            var rowStatus = "active";
            if (taken && u.Status == "sold")
            {
                // A device the business sold has come back in elsewhere (a trade-in, 11.1): the sold record never displaces it.
                var recorded = await t.ExecuteScalarAsync<bool>("""
                    SELECT EXISTS (SELECT 1 FROM device.central_registry WHERE server_id = @serverId AND device_unit_id = @unit AND value = @Value AND status = 'retired'
                                      AND retired_reason = 'superseded')
                    """, new { serverId, unit = u.UnitId, d.Value });
                if (recorded) continue;
                rowStatus = "retired";
                taken = false;
            }
            else if (taken && holders.All(h => h.UnitStatus == "sold"))
            {
                // Every holder is a device that was sold: this unit is that device coming back in (a trade-in), not a duplicate.
                await t.ExecuteAsync("""
                    UPDATE device.central_registry SET status = 'retired', retired_at = now(), retired_reason = 'superseded', conflict_id = NULL, updated_at = now()
                     WHERE id = ANY(@ids)
                    """, new { ids = holders.Select(h => h.Id).ToArray() });
                taken = false;
            }
            var conflictId = taken ? await OpenConflictAsync(t, businessId, k) : (Guid?)null;
            await t.ExecuteAsync("""
                INSERT INTO device.central_registry (id, business_id, kind, slot, value, brand_name, server_id, branch_id, device_unit_id, description_en,
                                                     description_ar, unit_status, condition, received_at, status, conflict_id, retired_at, retired_reason)
                VALUES (@id, @businessId, @Kind, @Slot, @Value, @BrandName, @serverId, @branchId, @unit, @DescriptionEn, @DescriptionAr, @unitStatus, @Condition,
                        @ReceivedAt, @rowStatus, @conflictId, CASE WHEN @rowStatus = 'retired' THEN now() END, CASE WHEN @rowStatus = 'retired' THEN 'superseded' END)
                """, new { id = t.NewId(), businessId, d.Kind, d.Slot, d.Value, k.BrandName, serverId, branchId, unit = u.UnitId, u.DescriptionEn, u.DescriptionAr,
                           unitStatus = u.Status, u.Condition, ReceivedAt = u.ReceivedAt?.UtcDateTime, rowStatus = taken ? "conflict" : rowStatus, conflictId });
            touched.Add(k);
        }
        foreach (var k in touched.Distinct()) await ReevaluateAsync(t, businessId, k);
        return await UnitStateAsync(t, serverId, u.UnitId);
    }

    private static async Task<Guid> OpenConflictAsync(DbTx t, Guid businessId, Key k)
    {
        var open = await t.ExecuteScalarAsync<Guid?>("""
            SELECT id FROM device.imei_conflict WHERE business_id = @businessId AND kind = @kind AND brand_name = @BrandName AND value = @Value AND status = 'open'
            """, new { businessId, kind = k.Kind == "serial" ? "serial" : "imei", k.BrandName, k.Value });
        if (open is { } id) return id;
        id = t.NewId();
        await t.ExecuteAsync("""
            INSERT INTO device.imei_conflict (id, business_id, kind, value, brand_name) VALUES (@id, @businessId, @kind, @Value, @BrandName)
            """, new { id, businessId, kind = k.Kind == "serial" ? "serial" : "imei", k.Value, k.BrandName });
        return id;
    }

    private sealed record Row(Guid Id, Guid ServerId, Guid BranchId, Guid UnitId, string Status, Guid? ConflictId, string DescriptionEn, string DescriptionAr,
        string UnitStatus, DateTime? ReceivedAt);

    /// <summary>
    /// Settles one identifier: with two or more units claiming it, a conflict is open, the earliest row is active and every
    /// unit is on hold; with one or none, the conflict is resolved and the remaining unit released.
    /// </summary>
    public static async Task ReevaluateAsync(DbTx t, Guid businessId, Key k)
    {
        var rows = (await t.QueryAsync<Row>("""
            SELECT id, server_id AS ServerId, branch_id AS BranchId, device_unit_id AS UnitId, status, conflict_id AS ConflictId,
                   description_en AS DescriptionEn, description_ar AS DescriptionAr, unit_status AS UnitStatus, received_at AS ReceivedAt
              FROM device.central_registry
             WHERE business_id = @businessId AND status <> 'retired' AND value = @Value
               AND (CASE WHEN kind = 'serial' THEN @Kind = 'serial' AND brand_name = @BrandName ELSE @Kind <> 'serial' END)
             ORDER BY registered_at, id
            """, new { businessId, k.Value, k.Kind, k.BrandName })).ToList();
        var open = await t.ExecuteScalarAsync<Guid?>("""
            SELECT id FROM device.imei_conflict WHERE business_id = @businessId AND kind = @kind AND brand_name = @BrandName AND value = @Value AND status = 'open'
            """, new { businessId, kind = k.Kind == "serial" ? "serial" : "imei", k.BrandName, k.Value });

        if (rows.Count <= 1)
        {
            if (open is not { } cid) return;
            var keeper = rows.FirstOrDefault();
            var branch = keeper is null ? null : await t.ExecuteScalarAsync<string>("SELECT code FROM core.branch WHERE id = @id", new { id = keeper.BranchId });
            await t.ExecuteAsync("""
                UPDATE device.imei_conflict SET status = 'resolved', resolved_at = now(), resolution = @resolution WHERE id = @cid
                """, new { cid, resolution = keeper is null ? "No device claims it any more." : $"Only the device at {branch} claims it now." });
            if (keeper is not null)
            {
                await t.ExecuteAsync("UPDATE device.central_registry SET status = 'active', conflict_id = NULL, updated_at = now() WHERE id = @id", new { keeper.Id });
                await t.ExecuteAsync("UPDATE device.imei_conflict_side SET outcome = 'kept', outcome_at = now() WHERE conflict_id = @cid AND registry_id = @id AND outcome IS NULL",
                    new { cid, keeper.Id });
                await InstructAsync(t, businessId, keeper.ServerId, "release", keeper.UnitId, cid, k.Value, "The IMEI conflict is resolved.");
            }
            await t.ExecuteAsync("UPDATE device.central_registry SET conflict_id = NULL WHERE conflict_id = @cid AND status = 'active'", new { cid });
            return;
        }

        var conflictId = open ?? await OpenConflictAsync(t, businessId, k);
        var activeId = rows.FirstOrDefault(r => r.Status == "active")?.Id ?? rows[0].Id;
        foreach (var r in rows)
        {
            await t.ExecuteAsync("""
                UPDATE device.central_registry SET status = @rowStatus, conflict_id = @conflictId, updated_at = now() WHERE id = @rowId
                """, new { rowId = r.Id, rowStatus = r.Id == activeId ? "active" : "conflict", conflictId });
            var joined = await t.ExecuteAsync("""
                INSERT INTO device.imei_conflict_side (id, conflict_id, registry_id, server_id, branch_id, device_unit_id, description_en, description_ar, unit_status, received_at)
                VALUES (@sid, @conflictId, @Id, @ServerId, @BranchId, @UnitId, @DescriptionEn, @DescriptionAr, @UnitStatus, @ReceivedAt)
                ON CONFLICT (conflict_id, registry_id) DO NOTHING
                """, new { sid = t.NewId(), conflictId, r.Id, r.ServerId, r.BranchId, r.UnitId, r.DescriptionEn, r.DescriptionAr, r.UnitStatus, r.ReceivedAt });
            if (joined == 1)
                await InstructAsync(t, businessId, r.ServerId, "hold", r.UnitId, conflictId, k.Value,
                    $"{(k.Kind == "serial" ? "Serial number" : "IMEI")} {k.Value} is also registered to another device.");
        }
    }

    /// <summary>A hold or release for a unit: applied here when the unit is on this server, otherwise queued for its branch server.</summary>
    private static async Task InstructAsync(DbTx t, Guid businessId, Guid serverId, string kind, Guid unitId, Guid conflictId, string value, string note)
    {
        var self = await t.ExecuteScalarAsync<Guid>("SELECT id FROM core.server WHERE is_self");
        if (serverId == self)
        {
            await t.WithAllBranchesAsync(async () =>
            {
                if (kind == "hold") await UnitHolds.HoldAsync(t, unitId, "imei_conflict", conflictId, null, note);
                else await UnitHolds.ReleaseAsync(t, unitId, "imei_conflict", conflictId, note);
                return 0;
            });
            return;
        }
        await t.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(@k, 8))", new { k = InstructionLock });
        await t.ExecuteAsync("""
            INSERT INTO device.central_instruction (business_id, server_id, kind, device_unit_id, conflict_id, value, note)
            VALUES (@businessId, @serverId, @kind, @unitId, @conflictId, @value, @note)
            """, new { businessId, serverId, kind, unitId, conflictId, value, note });
    }

    /// <summary>
    /// Writers of instructions hold this lock until they commit, so once a reader holds it every instruction numbered so far
    /// is committed and later ones are numbered higher: the stamp of an answer is exact.
    /// </summary>
    public const string InstructionLock = "kite:central_instruction";

    /// <summary>The answer for units a server just pushed, stamped with the last instruction to that server it reflects.</summary>
    public static async Task<CentralRegisterResponse> AnswerAsync(DbTx t, Guid serverId, IEnumerable<Guid> unitIds)
    {
        await t.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(@k, 8))", new { k = InstructionLock });
        var stamp = await t.ExecuteScalarAsync<long>("SELECT coalesce(max(seq), 0) FROM device.central_instruction WHERE server_id = @serverId", new { serverId });
        var results = new List<CentralUnitResult>();
        foreach (var id in unitIds) results.Add(await UnitStateAsync(t, serverId, id));
        return new CentralRegisterResponse(results, stamp);
    }

    /// <summary>The unit's standing at head office: verified, or in conflict with the other units claiming its identifiers.</summary>
    public static async Task<CentralUnitResult> UnitStateAsync(DbTx t, Guid serverId, Guid unitId)
    {
        var mine = (await t.QueryAsync<(Guid ConflictId, string Kind, string Value, string BrandName)>("""
            SELECT r.conflict_id, r.kind, r.value, r.brand_name FROM device.central_registry r JOIN device.imei_conflict c ON c.id = r.conflict_id
             WHERE r.server_id = @serverId AND r.device_unit_id = @unitId AND r.status <> 'retired' AND c.status = 'open'
            """, new { serverId, unitId })).ToList();
        var conflicts = new List<CentralConflictInfo>();
        foreach (var c in mine)
        {
            var others = (await t.QueryAsync<CentralHolder>(HolderSelect + """
                 WHERE r.conflict_id = @ConflictId AND r.status <> 'retired' AND NOT (r.server_id = @serverId AND r.device_unit_id = @unitId)
                 ORDER BY r.registered_at
                """, new { c.ConflictId, serverId, unitId })).ToList();
            conflicts.Add(new CentralConflictInfo(c.ConflictId, c.Kind, c.Value, others));
        }
        return new CentralUnitResult(unitId, conflicts.Count == 0 ? "verified" : "conflict", conflicts);
    }

    /// <summary>The identifiers of a unit on this server, as head office records them (serial numbers with their brand's name).</summary>
    public static async Task<CentralUnitPush?> PushOfAsync(DbTx t, Guid unitId)
    {
        var u = await t.QuerySingleOrDefaultAsync<DeviceUnitRow>(DeviceUnits.Select + " WHERE u.id = @unitId", new { unitId });
        if (u is null) return null;
        var brand = await t.ExecuteScalarAsync<string?>("""
            SELECT coalesce(lower(trim(b.name_en)), 'product:' || p.id::text) FROM catalog.variant v JOIN catalog.product p ON p.id = v.product_id
              LEFT JOIN catalog.brand b ON b.id = p.brand_id WHERE v.id = @VariantId
            """, new { u.VariantId });
        var ids = (await t.QueryAsync<(string Kind, short Slot, string Value)>("""
            SELECT kind, slot, value FROM device.imei_registry WHERE device_unit_id = @unitId AND status = 'active' ORDER BY kind, slot
            """, new { unitId })).Select(i => new PushIdentifier(i.Kind, i.Slot, i.Value, i.Kind == "serial" ? brand : null)).ToList();
        return new CentralUnitPush(u.Id, u.Status, u.Condition, u.DescriptionEn, u.DescriptionAr, new DateTimeOffset(DateTime.SpecifyKind(u.ReceivedAt, DateTimeKind.Utc)), ids);
    }

    /// <summary>On head office: brings the central rows of one of its own units up to date (after a receipt, correction or reversal).</summary>
    public static async Task<CentralUnitResult> SyncOwnUnitAsync(DbTx t, Guid unitId)
    {
        var push = await PushOfAsync(t, unitId) ?? throw new KiteException(ErrorCodes.DeviceNotFound, "That device is not on this server.", 404);
        var self = await t.QuerySingleOrDefaultAsync<(Guid Id, Guid BusinessId)>("SELECT id, business_id FROM core.server WHERE is_self");
        var branch = await t.ExecuteScalarAsync<Guid>("SELECT branch_id FROM device.device_unit WHERE id = @unitId", new { unitId });
        var result = await UpsertAsync(t, self.BusinessId, self.Id, branch, push);
        await t.WithAllBranchesAsync(async () =>
        {
            await ApplyResultAsync(t, result, await t.ExecuteScalarAsync<int>("SELECT coalesce(max(seq), 0) FROM device.device_event WHERE device_unit_id = @unitId", new { unitId }));
            return 0;
        });
        return result;
    }

    /// <summary>
    /// What head office says about one unit, applied where the unit is: verified, or a hold for each conflict it is in; holds for
    /// conflicts that closed are released (the unit goes back to where it was when no hold is left).
    /// </summary>
    public static async Task ApplyResultAsync(DbTx t, CentralUnitResult result, int pushedSeq, long? stamp = null)
    {
        // On a branch server, an answer older than an instruction already applied to this unit changes no hold (the instructions are newer).
        var current = await t.ExecuteScalarAsync<long?>("SELECT stamp FROM device.central_check WHERE device_unit_id = @unitId FOR UPDATE", new { unitId = result.UnitId });
        if (stamp is { } s0 && current is { } c0 && s0 < c0)
        {
            await t.ExecuteAsync("UPDATE device.central_check SET pushed_seq = greatest(pushed_seq, @pushedSeq), checked_at = now(), attempts = 0, last_error = NULL, updated_at = now() WHERE device_unit_id = @unitId",
                new { unitId = result.UnitId, pushedSeq });
            return;
        }
        await t.ExecuteAsync("""
            UPDATE device.central_check SET state = CASE WHEN @conflict THEN 'conflict' ELSE 'verified' END, pushed_seq = greatest(pushed_seq, @pushedSeq),
                   stamp = greatest(stamp, @stamp), checked_at = now(), attempts = 0, last_error = NULL, updated_at = now()
             WHERE device_unit_id = @unitId
            """, new { unitId = result.UnitId, conflict = result.State == "conflict", pushedSeq, stamp = stamp ?? 0 });
        foreach (var c in result.Conflicts)
        {
            var other = c.Others.FirstOrDefault();
            await UnitHolds.HoldAsync(t, result.UnitId, "imei_conflict", c.ConflictId, null,
                $"{(c.Kind == "serial" ? "Serial number" : "IMEI")} {c.Value} is also registered" + (other is null ? "." : $": {Describe(other)}."));
        }
        var stillOpen = result.Conflicts.Select(c => c.ConflictId).ToHashSet();
        var openHolds = await t.QueryAsync<Guid>("""
            SELECT conflict_id FROM device.unit_hold WHERE device_unit_id = @unitId AND reason = 'imei_conflict' AND released_at IS NULL
            """, new { unitId = result.UnitId });
        foreach (var cid in openHolds.Where(id => !stillOpen.Contains(id)).ToList())
            await UnitHolds.ReleaseAsync(t, result.UnitId, "imei_conflict", cid, "The IMEI conflict is resolved.");
    }
}

/// <summary>
/// Holds with reasons (11.3): a unit is quarantined while any hold is open (a manager's, an IMEI conflict, the blocked list)
/// and released only when the last one closes.
/// </summary>
public static class UnitHolds
{
    private static readonly string[] Holdable = ["in_stock", "under_inspection", "returned_inspection", "refurbishing", "for_parts", "supplier_return"];

    public sealed record OpenHold(Guid Id, string Reason, Guid? ConflictId, Guid? BlockedId, string? Note, DateTime HeldAt);

    public static async Task<IReadOnlyList<OpenHold>> OpenAsync(DbTx t, Guid unitId) =>
        (await t.QueryAsync<OpenHold>("""
            SELECT id, reason, conflict_id AS ConflictId, blocked_id AS BlockedId, note, held_at AS HeldAt
              FROM device.unit_hold WHERE device_unit_id = @unitId AND released_at IS NULL ORDER BY held_at
            """, new { unitId })).ToList();

    /// <summary>Opens a hold (once per reason and conflict or blocked entry) and quarantines the unit if it can be.</summary>
    public static async Task HoldAsync(DbTx t, Guid unitId, string reason, Guid? conflictId, Guid? blockedId, string note)
    {
        var u = await t.QuerySingleOrDefaultAsync<(Guid Id, Guid BusinessId, Guid BranchId, string Status)>(
            "SELECT id, business_id, branch_id, status FROM device.device_unit WHERE id = @unitId", new { unitId });
        if (u.Id == Guid.Empty) return;
        var already = await t.ExecuteScalarAsync<bool>("""
            SELECT EXISTS (SELECT 1 FROM device.unit_hold WHERE device_unit_id = @unitId AND reason = @reason AND released_at IS NULL
                              AND conflict_id IS NOT DISTINCT FROM @conflictId AND blocked_id IS NOT DISTINCT FROM @blockedId)
            """, new { unitId, reason, conflictId, blockedId });
        if (!already)
            await t.ExecuteAsync("""
                INSERT INTO device.unit_hold (id, business_id, branch_id, device_unit_id, reason, conflict_id, blocked_id, note, held_by)
                VALUES (@id, @BusinessId, @BranchId, @unitId, @reason, @conflictId, @blockedId, @note, @by)
                """, new { id = t.NewId(), u.BusinessId, u.BranchId, unitId, reason, conflictId, blockedId, note, by = t.Actor.UserId });
        if (reason == "imei_conflict")
            await t.ExecuteAsync("UPDATE device.central_check SET state = 'conflict', updated_at = now() WHERE device_unit_id = @unitId", new { unitId });
        if (!already && Holdable.Contains(u.Status))
            await DeviceUnits.ApplyEventAsync(t, unitId, "held", "quarantined", docType: "device_unit", docId: unitId,
                details: new { reason, conflictId, blockedId, note });
    }

    /// <summary>Closes the hold for this reason (and conflict or blocked entry); releases the unit when no hold is left.</summary>
    public static async Task ReleaseAsync(DbTx t, Guid unitId, string reason, Guid? refId, string note)
    {
        var closed = await t.ExecuteAsync("""
            UPDATE device.unit_hold SET released_at = now(), released_by = @by, release_note = @note
             WHERE device_unit_id = @unitId AND reason = @reason AND released_at IS NULL
               AND (@refId::uuid IS NULL OR conflict_id = @refId OR blocked_id = @refId)
            """, new { unitId, reason, refId, note, by = t.Actor.UserId });
        if (reason == "imei_conflict"
            && !await t.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM device.unit_hold WHERE device_unit_id = @unitId AND reason = 'imei_conflict' AND released_at IS NULL)", new { unitId }))
            await t.ExecuteAsync("UPDATE device.central_check SET state = 'verified', checked_at = now(), updated_at = now() WHERE device_unit_id = @unitId AND state = 'conflict'", new { unitId });
        if (closed == 0) return;
        var status = await t.ExecuteScalarAsync<string?>("SELECT status FROM device.device_unit WHERE id = @unitId", new { unitId });
        if (status == "quarantined" && (await OpenAsync(t, unitId)).Count == 0)
            await DeviceUnits.ApplyEventAsync(t, unitId, "released", null, docType: "device_unit", docId: unitId, details: new { reason, note });
    }

    /// <summary>A short sentence for each open hold, for the device page and for refusals.</summary>
    public static string Explain(OpenHold h) => h.Reason switch
    {
        "imei_conflict" => "the same IMEI or serial number is registered to another device (an IMEI conflict at head office)",
        "blocked_imei" => "its IMEI is on the business's blocked list",
        _ => "a manager put it on hold" + (string.IsNullOrWhiteSpace(h.Note) ? "" : $" ({h.Note})"),
    };
}

/// <summary>Serial number formats per brand (11.2): an optional .NET regular expression, checked with a time limit.</summary>
public static class SerialFormats
{
    public static bool Valid(string pattern) { try { _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100)); return true; } catch (ArgumentException) { return false; } }

    public static bool Matches(string pattern, string value)
    {
        try { return Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)); }
        catch (RegexMatchTimeoutException) { return false; }
        catch (ArgumentException) { return true; }   // a broken pattern never blocks receiving; it is refused when saved
    }
}
