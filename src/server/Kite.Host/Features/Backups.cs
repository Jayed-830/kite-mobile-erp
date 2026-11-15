using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Kite.Host.Infrastructure;
using Kite.Identity;
using Kite.Persistence;
using Kite.Platform;
using Npgsql;

namespace Kite.Host.Features;

public sealed record RunBackupRequest(string Kind);
public sealed record RecoveryKeyRequest(string Password);

/// <summary>
/// The backup agent (blueprint 19): a nightly logical dump and base backup, both AES-256 encrypted with the
/// installation's backup key, copied off the machine when a folder is set, kept by the retention rules, and a
/// weekly restore of the latest dump into a scratch database with integrity checks. Every run is recorded.
/// </summary>
public sealed class BackupService(KiteDb db, ServerContext server, KeyStore keys, KiteOptions opts, IConfiguration config, IClock clock,
    ILogger<BackupService> log) : BackgroundService
{
    private const string KeyFile = "backup.key", KeyPurpose = "backup";
    private readonly Channel<(string Kind, Guid? UserId)> _requests = Channel.CreateBounded<(string, Guid?)>(8);
    private volatile string? _running;

    public string? Running => _running;
    public bool Configured => !string.IsNullOrWhiteSpace(config.GetConnectionString("Backup"));
    public string Folder => Path.GetFullPath(Path.IsPathRooted(opts.Backup.Folder) ? opts.Backup.Folder : Path.Combine(keys.DataDir, opts.Backup.Folder));
    public byte[] BackupKey => keys.LoadOrCreate(KeyFile, KeyPurpose);

    public bool Request(string kind, Guid? userId) => _requests.Writer.TryWrite((kind, userId));

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var tick = CancellationTokenSource.CreateLinkedTokenSource(stop);
                tick.CancelAfter(TimeSpan.FromMinutes(1));
                (string Kind, Guid? UserId)? manual = null;
                try { manual = await _requests.Reader.ReadAsync(tick.Token); }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested) { }
                if (!opts.Backup.Enabled || !server.IsConfigured || !Configured) continue;
                if (manual is { } m) await RunAsync(m.Kind, "manual", m.UserId, stop);
                else foreach (var kind in await DueAsync(stop)) await RunAsync(kind, "schedule", null, stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception e) { log.LogError(e, "Backup agent error"); }
        }
    }

    /// <summary>Scheduled kinds whose time today has passed with no scheduled run since then (survives restarts).</summary>
    private async Task<IReadOnlyList<string>> DueAsync(CancellationToken ct)
    {
        var tz = SetupEndpoints.BusinessTimeZone();
        var local = TimeZoneInfo.ConvertTime(clock.UtcNow, tz);
        var due = new List<string>();
        async Task Check(string kind, string at, bool weeklyOnly = false)
        {
            if (weeklyOnly && local.DayOfWeek != opts.Backup.VerifyDay) return;
            if (!TimeOnly.TryParseExact(at, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) return;
            var scheduledLocal = local.Date + t.ToTimeSpan();
            if (local.DateTime < scheduledLocal) return;
            var scheduledUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(scheduledLocal, DateTimeKind.Unspecified), tz);
            var ran = await db.InTransactionAsync(AccessScope.System, ActorContext.System, tx => tx.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM ops.backup_run WHERE kind = @kind AND trigger = 'schedule' AND started_at >= @scheduledUtc",
                new { kind, scheduledUtc }), ct);
            if (ran == 0) due.Add(kind);
        }
        await Check("logical", opts.Backup.LogicalAt);
        if (opts.Backup.BaseBackups) await Check("base", opts.Backup.BaseAt);
        await Check("verify", opts.Backup.VerifyAt, weeklyOnly: true);
        return due;
    }

    // ------------------------------------------------------------------------------------------ runs
    public async Task<Guid> RunAsync(string kind, string trigger, Guid? userId, CancellationToken ct)
    {
        if (kind is not ("logical" or "base" or "verify")) throw new KiteException(ErrorCodes.Validation, "Kind must be logical, base or verify.", 400, "kind");
        if (!Configured) throw new KiteException(ErrorCodes.BackupNotConfigured, "Backups are not configured: ConnectionStrings:Backup (the kite_backup login) is missing.", 409);
        if (_running is not null) throw new KiteException(ErrorCodes.BackupBusy, $"A {_running} backup is running. Try again when it has finished.", 409);
        _running = kind;
        var id = Ids.New();
        var started = clock.UtcNow;
        await db.InTransactionAsync(AccessScope.System, new ActorContext(userId, null, null, null), t => t.ExecuteAsync(
            "INSERT INTO ops.backup_run (id, kind, status, trigger, started_at, triggered_by) VALUES (@id, @kind, 'running', @trigger, @started, @userId)",
            new { id, kind, trigger, started, userId }), ct);
        try
        {
            var result = kind switch
            {
                "logical" => await LogicalAsync(started, ct),
                "base" => await BaseAsync(started, ct),
                _ => await VerifyAsync(ct),
            };
            await FinishAsync(id, "succeeded", result.File, result.Size, result.Sha256, result.Checks, null, result.SourceRun, ct);
            if (kind != "verify" && result.File is not null)
            {
                await OffsiteAsync(kind, result.File, id, ct);
                ApplyRetention(kind);
            }
            log.LogInformation("Backup {Kind} succeeded: {File}", kind, result.File);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogError("Backup {Kind} failed: {Error}", kind, e.Message);
            await FinishAsync(id, "failed", null, null, null, (e as VerifyFailedException)?.Checks, e.Message, null, CancellationToken.None);
        }
        finally { _running = null; }
        return id;
    }

    private sealed record RunResult(string? File, long? Size, string? Sha256, JsonObjectish? Checks, Guid? SourceRun);
    private sealed class JsonObjectish(Dictionary<string, object?> values) { public Dictionary<string, object?> Values { get; } = values; }
    private sealed class VerifyFailedException(string message, JsonObjectish checks) : Exception(message) { public JsonObjectish Checks { get; } = checks; }

    private async Task FinishAsync(Guid id, string status, string? file, long? size, string? sha, JsonObjectish? checks, string? error, Guid? source, CancellationToken ct)
    {
        await db.InTransactionAsync(AccessScope.System, ActorContext.System, async t =>
        {
            await t.ExecuteAsync("""
                UPDATE ops.backup_run SET status = @status, finished_at = @now, file_name = @file, size_bytes = @size, sha256 = @sha,
                       checks = @checks::jsonb, error = @error WHERE id = @id
                """, new { id, status, now = clock.UtcNow, file, size, sha, checks = checks is null ? null : JsonSerializer.Serialize(checks.Values),
                           error = error is null ? null : error[..Math.Min(error.Length, 2000)] });
            if (source is not null)
                await t.ExecuteAsync("UPDATE ops.backup_run SET checks = coalesce(checks, '{}'::jsonb) || jsonb_build_object('source_run', @source::text) WHERE id = @id", new { id, source });
            if (status == "failed")
                await t.AuditAsync(new AuditEntry("backup.failed", "backup_run", id, After: Json.Obj(new { error })));
        }, ct);
    }

    private NpgsqlConnectionStringBuilder Cs() => new(config.GetConnectionString("Backup"));

    private string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
    private string Prefix => "kite-" + (server.InstallationId ?? server.ServerId?.ToString("N")[..8] ?? "server").Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
    private string Work { get { var w = Path.Combine(Folder, "work"); Directory.CreateDirectory(w); return w; } }

    private async Task<RunResult> LogicalAsync(DateTimeOffset started, CancellationToken ct)
    {
        var cs = Cs();
        var dump = Path.Combine(Work, $"{Prefix}-logical-{Stamp(started)}.dump");
        try
        {
            await ToolAsync("pg_dump", ["-h", cs.Host!, "-p", cs.Port.ToString(CultureInfo.InvariantCulture), "-U", cs.Username!, "-d", cs.Database!,
                "-Fc", "-Z", "6", "--no-password", "-f", dump], cs.Password, TimeSpan.FromHours(2), ct);
            return await EncryptIntoAsync(dump, "logical", started, ct);
        }
        finally { TryDelete(dump); }
    }

    private async Task<RunResult> BaseAsync(DateTimeOffset started, CancellationToken ct)
    {
        var cs = Cs();
        var dir = Path.Combine(Work, $"{Prefix}-base-{Stamp(started)}");
        var tar = dir + ".tar";
        try
        {
            await ToolAsync("pg_basebackup", ["-h", cs.Host!, "-p", cs.Port.ToString(CultureInfo.InvariantCulture), "-U", cs.Username!, "--no-password",
                "-D", dir, "-Fp", "-X", "stream", "-c", "fast", "--manifest-checksums=SHA256"], cs.Password, TimeSpan.FromHours(4), ct);
            await ToolAsync("pg_verifybackup", ["--quiet", dir], null, TimeSpan.FromHours(1), ct);
            await TarFile.CreateFromDirectoryAsync(dir, tar, includeBaseDirectory: false, ct);
            return await EncryptIntoAsync(tar, "base", started, ct);
        }
        finally { TryDelete(tar); TryDeleteDir(dir); }
    }

    private async Task<RunResult> EncryptIntoAsync(string plainFile, string kind, DateTimeOffset started, CancellationToken ct)
    {
        var outDir = Path.Combine(Folder, kind);
        Directory.CreateDirectory(outDir);
        var name = $"{Prefix}-{kind}-{Stamp(started)}.kbak";
        var target = Path.Combine(outDir, name);
        var tmp = target + ".part";
        await using (var input = File.OpenRead(plainFile))
        await using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
        {
            await BackupCrypto.EncryptAsync(input, output, BackupKey, ct);
            await output.FlushAsync(ct);
        }
        File.Move(tmp, target, overwrite: false);
        var (size, sha) = await HashAsync(target, ct);
        return new RunResult(Path.Combine(kind, name), size, sha, null, null);
    }

    private static async Task<(long Size, string Sha)> HashAsync(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        var h = await SHA256.HashDataAsync(fs, ct);
        return (fs.Length, Convert.ToHexString(h).ToLowerInvariant());
    }

    /// <summary>Weekly: restore the newest dump into a scratch database and check it (blueprint 19.3).</summary>
    private async Task<RunResult> VerifyAsync(CancellationToken ct)
    {
        var src = await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => t.QuerySingleOrDefaultAsync<(Guid Id, string File, string Sha256)>(
            "SELECT id, file_name, sha256 FROM ops.backup_run WHERE kind = 'logical' AND status = 'succeeded' ORDER BY started_at DESC LIMIT 1"), ct);
        if (src.Id == Guid.Empty) throw new KiteException(ErrorCodes.BackupFailed, "There is no logical backup to verify yet. Run a backup first.", 409);
        var path = Path.Combine(Folder, src.File);
        if (!File.Exists(path)) throw new KiteException(ErrorCodes.BackupFailed, $"The backup file {src.File} is missing from the backup folder.", 409);
        var (_, sha) = await HashAsync(path, ct);
        if (sha != src.Sha256) throw new KiteException(ErrorCodes.BackupFailed, $"The backup file {src.File} changed since it was made (SHA-256 differs).", 409);

        var cs = Cs();
        var dump = Path.Combine(Work, "verify-" + Guid.NewGuid().ToString("N")[..8] + ".dump");
        var scratch = "kite_verify_" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var checks = new Dictionary<string, object?> { ["source_file"] = src.File };
        try
        {
            await using (var input = File.OpenRead(path))
            await using (var output = File.Create(dump))
                await BackupCrypto.DecryptAsync(input, output, BackupKey, ct);
            await using (var conn = new NpgsqlConnection(cs.ConnectionString))
            {
                await conn.OpenAsync(ct);
                await using var create = new NpgsqlCommand($"CREATE DATABASE \"{scratch}\"", conn);
                await create.ExecuteNonQueryAsync(ct);
            }
            await ToolAsync("pg_restore", ["-h", cs.Host!, "-p", cs.Port.ToString(CultureInfo.InvariantCulture), "-U", cs.Username!, "--no-password",
                "-d", scratch, "--no-owner", "--no-privileges", "--exit-on-error", dump], cs.Password, TimeSpan.FromHours(2), ct);
            checks["restored"] = true;

            var scs = new NpgsqlConnectionStringBuilder(cs.ConnectionString) { Database = scratch, Pooling = false };
            await using (var conn = new NpgsqlConnection(scs.ConnectionString))
            {
                await conn.OpenAsync(ct);
                async Task<T> Q<T>(string sql) { await using var c = new NpgsqlCommand(sql, conn); return (T)(await c.ExecuteScalarAsync(ct))!; }
                var unbalanced = await Q<long>("SELECT count(*) FROM (SELECT j.id FROM acct.journal j JOIN acct.journal_line l ON l.journal_id = j.id GROUP BY j.id HAVING sum(l.debit) <> sum(l.credit)) x");
                var outboxOk = await Q<bool>("SELECT count(*) = 0 OR (min(origin_seq) = 1 AND max(origin_seq) = count(*)) FROM sync.outbox");
                var chainOk = await Q<bool>("SELECT ok FROM audit.verify_chain()");
                var chainRows = await Q<long>("SELECT rows_checked FROM audit.verify_chain()");
                checks["journals_balanced"] = unbalanced == 0;
                checks["unbalanced_journals"] = unbalanced;
                checks["outbox_gap_free"] = outboxOk;
                checks["audit_chain_intact"] = chainOk;
                checks["audit_rows_checked"] = chainRows;
                checks["stock_matches_movements"] = "not applicable until stock arrives (Phase 4)";
                foreach (var table in new[] { "core.business", "core.branch", "iam.\"user\"", "acct.journal", "sync.outbox", "audit.audit_log" })
                    checks["rows " + table.Replace("\"", "", StringComparison.Ordinal)] = await Q<long>($"SELECT count(*) FROM {table}");
            }
            if (!(bool)checks["journals_balanced"]! || !(bool)checks["outbox_gap_free"]! || !(bool)checks["audit_chain_intact"]!)
                throw new VerifyFailedException("The restored backup failed an integrity check.", new JsonObjectish(checks));
            return new RunResult(null, null, null, new JsonObjectish(checks), src.Id);
        }
        finally
        {
            TryDelete(dump);
            try
            {
                NpgsqlConnection.ClearAllPools();
                await using var conn = new NpgsqlConnection(cs.ConnectionString);
                await conn.OpenAsync(CancellationToken.None);
                await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{scratch}\" WITH (FORCE)", conn);
                await drop.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch (Exception e) { log.LogWarning("Could not drop scratch database {Db}: {Error}", scratch, e.Message); }
        }
    }

    private async Task OffsiteAsync(string kind, string relFile, Guid sourceRun, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(opts.Backup.OffsiteFolder)) return;
        var id = Ids.New();
        var started = clock.UtcNow;
        await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => t.ExecuteAsync(
            "INSERT INTO ops.backup_run (id, kind, status, trigger, started_at, source_run_id) VALUES (@id, 'offsite', 'running', 'schedule', @started, @sourceRun)",
            new { id, started, sourceRun }), ct);
        try
        {
            var dest = Path.Combine(opts.Backup.OffsiteFolder, relFile);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(Path.Combine(Folder, relFile), dest + ".part", overwrite: true);
            File.Move(dest + ".part", dest, overwrite: true);
            var (size, sha) = await HashAsync(dest, ct);
            await FinishAsync(id, "succeeded", relFile, size, sha, null, null, null, ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await FinishAsync(id, "failed", relFile, null, null, null, "Off-machine copy failed: " + e.Message, null, CancellationToken.None);
        }
    }

    /// <summary>Daily 30 days, weekly 12 weeks, monthly 12 months, yearly 10 years (19.2); base backups 7 days.</summary>
    public void ApplyRetention(string kind)
    {
        foreach (var root in new[] { Folder, opts.Backup.OffsiteFolder }.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            var dir = Path.Combine(root, kind);
            if (!Directory.Exists(dir)) continue;
            var files = Directory.GetFiles(dir, "*.kbak").Select(f => (Path: f, At: StampOf(f))).Where(f => f.At is not null)
                .Select(f => (f.Path, At: f.At!.Value)).OrderByDescending(f => f.At).ToList();
            var keep = Keep(files.Select(f => f.At).ToList(), kind, clock.UtcNow.UtcDateTime);
            foreach (var f in files.Where(f => !keep.Contains(f.At))) TryDelete(f.Path);
        }
    }

    public static HashSet<DateTime> Keep(IReadOnlyList<DateTime> stamps, string kind, DateTime now)
    {
        var keep = new HashSet<DateTime>();
        if (stamps.Count == 0) return keep;
        keep.Add(stamps.Max()); // never delete the newest backup
        if (kind == "base")
        {
            foreach (var s in stamps.Where(s => now - s <= TimeSpan.FromDays(7))) keep.Add(s);
            return keep;
        }
        var ordered = stamps.OrderBy(s => s).ToList();
        void Bucket(Func<DateTime, string> key, TimeSpan window)
        {
            foreach (var g in ordered.Where(s => now - s <= window).GroupBy(key)) keep.Add(g.Max());
        }
        Bucket(s => s.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), TimeSpan.FromDays(30));
        Bucket(s => ISOWeek.GetYear(s) + "-" + ISOWeek.GetWeekOfYear(s), TimeSpan.FromDays(7 * 12));
        Bucket(s => s.ToString("yyyy-MM", CultureInfo.InvariantCulture), TimeSpan.FromDays(366));
        Bucket(s => s.Year.ToString(CultureInfo.InvariantCulture), TimeSpan.FromDays(3660));
        return keep;
    }

    private static DateTime? StampOf(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.Length >= 15 && DateTime.TryParseExact(name[^15..], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
    }

    // ------------------------------------------------------------------------------------------ status
    public async Task<object> StatusAsync(CancellationToken ct)
    {
        var runs = await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => t.QueryAsync<RunDto>("""
            SELECT id, kind, status, trigger, started_at AS StartedAt, finished_at AS FinishedAt, file_name AS FileName, size_bytes AS SizeBytes,
                   sha256, checks::text AS Checks, error FROM ops.backup_run ORDER BY started_at DESC LIMIT 50
            """), ct);
        var list = runs.ToList();
        DateTime? LastOk(string k) => list.Where(r => r.Kind == k && r.Status == "succeeded").Select(r => r.FinishedAt).FirstOrDefault();
        var newest = new[] { LastOk("logical"), LastOk("base") }.Where(d => d is not null).Select(d => d!.Value).DefaultIfEmpty().Max();
        var alert = newest == default || clock.UtcNow.UtcDateTime - newest > TimeSpan.FromHours(opts.Backup.AlertAfterHours);
        object? wal = null;
        try
        {
            wal = await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => t.QuerySingleOrDefaultAsync<WalDto>("""
                SELECT current_setting('archive_mode') AS Mode, archived_count AS Archived, last_archived_time AS LastArchivedAt,
                       failed_count AS Failed, last_failed_time AS LastFailedAt FROM pg_stat_archiver
                """), ct);
        }
        catch (PostgresException) { /* pg_stat_archiver needs pg_monitor on some setups */ }
        var shown = await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => t.ExecuteScalarAsync<DateTime?>(
            "SELECT max(occurred_at) FROM audit.audit_log WHERE action = 'backup.recovery_key_shown'"), ct);
        return new
        {
            configured = Configured, enabled = opts.Backup.Enabled, running = _running, folder = Folder, offsiteFolder = opts.Backup.OffsiteFolder,
            schedule = new { opts.Backup.LogicalAt, opts.Backup.BaseAt, opts.Backup.BaseBackups, verifyDay = opts.Backup.VerifyDay.ToString(), opts.Backup.VerifyAt },
            lastLogicalOk = LastOk("logical"), lastBaseOk = LastOk("base"), lastVerifyOk = LastOk("verify"), lastOffsiteOk = LastOk("offsite"),
            alert, alertAfterHours = opts.Backup.AlertAfterHours, wal, keyProtection = keys.Protector.Kind, recoveryKeyShownAt = shown,
            runs = list.Select(r => new { r.Id, r.Kind, r.Status, r.Trigger, r.StartedAt, r.FinishedAt, r.FileName, r.SizeBytes, r.Sha256, checks = r.Checks is null ? (JsonElement?)null : JsonDocument.Parse(r.Checks).RootElement, r.Error }),
        };
    }

    private sealed record RunDto(Guid Id, string Kind, string Status, string Trigger, DateTime StartedAt, DateTime? FinishedAt, string? FileName, long? SizeBytes,
        string? Sha256, string? Checks, string? Error);
    private sealed record WalDto(string Mode, long Archived, DateTime? LastArchivedAt, long Failed, DateTime? LastFailedAt);

    // ------------------------------------------------------------------------------------------ tools
    private async Task ToolAsync(string tool, string[] args, string? password, TimeSpan timeout, CancellationToken ct)
    {
        var exe = string.IsNullOrWhiteSpace(opts.Backup.PgBin) ? tool : Path.Combine(opts.Backup.PgBin, OperatingSystem.IsWindows() ? tool + ".exe" : tool);
        var psi = new ProcessStartInfo(exe) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (password is not null) psi.Environment["PGPASSWORD"] = password;
        psi.Environment["PGCONNECT_TIMEOUT"] = "15";
        using var p = new Process { StartInfo = psi };
        try { p.Start(); }
        catch (System.ComponentModel.Win32Exception e)
        { throw new KiteException(ErrorCodes.BackupNotConfigured, $"{tool} was not found ({e.Message}). Set Kite:Backup:PgBin to the PostgreSQL bin folder.", 409); }
        var stderr = p.StandardError.ReadToEndAsync(ct);
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } throw new TimeoutException($"{tool} took longer than {timeout}."); }
        var err = (await stderr).Trim();
        await stdout;
        if (p.ExitCode != 0) throw new InvalidOperationException($"{tool} failed (exit {p.ExitCode}): {(err.Length > 800 ? err[..800] : err)}");
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    private static void TryDeleteDir(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}

public static class BackupEndpoints
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/backups");
        g.MapGet("", async (BackupService backups, CancellationToken ct) => Results.Ok(await backups.StatusAsync(ct)))
            .RequirePermission(Permissions.BackupView);

        g.MapPost("/run", async (RunBackupRequest req, HttpContext http, BackupService backups, KiteDb db, CancellationToken ct) =>
        {
            if (req.Kind is not ("logical" or "base" or "verify")) throw new KiteException(ErrorCodes.Validation, "Kind must be logical, base or verify.", 400, "kind");
            if (!backups.Configured) throw new KiteException(ErrorCodes.BackupNotConfigured, "Backups are not configured: ConnectionStrings:Backup (the kite_backup login) is missing.", 409);
            // A request while another backup runs waits its turn (the agent runs one at a time).
            if (!backups.Request(req.Kind, http.User.UserIdOf())) throw new KiteException(ErrorCodes.BackupBusy, "Several backups are already waiting. Try again when they have finished.", 409);
            await db.InTransactionAsync(AccessScope.System, http.ActorOf(), t => t.AuditAsync(new AuditEntry("backup.requested", "backup_run", null, After: Json.Obj(new { req.Kind }))), ct);
            return Results.Accepted();
        }).RequirePermission(Permissions.BackupManage);

        // The printed recovery key: needed to restore on another PC. Shown only after the password again; audited.
        g.MapPost("/recovery-key", async (RecoveryKeyRequest req, HttpContext http, BackupService backups, KiteDb db, IPasswordHasher hasher, CancellationToken ct) =>
        {
            await Reauth.RequirePasswordAsync(db, hasher, http, req.Password, ct);
            var key = BackupCrypto.FormatRecoveryKey(backups.BackupKey);
            await db.InTransactionAsync(AccessScope.System, http.ActorOf(), t => t.AuditAsync(new AuditEntry("backup.recovery_key_shown", "backup_key", null)), ct);
            return Results.Ok(new { recoveryKey = key, keyId = Convert.ToHexString(BackupCrypto.KeyId(backups.BackupKey)).ToLowerInvariant() });
        }).RequirePermission(Permissions.BackupManage);
    }
}

/// <summary>
/// Command line restore helper (runbook 19.5): <c>Kite.Host backup-decrypt --in file.kbak --out file.dump --recovery-key XXXX-...</c>.
/// Works without a database or the server's data folder, so a backup can be opened on any PC.
/// </summary>
public static class BackupCli
{
    public static async Task<int?> TryRunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] != "backup-decrypt") return null;
        string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var input = Arg("--in"); var output = Arg("--out"); var key = Arg("--recovery-key") ?? Environment.GetEnvironmentVariable("KITE_RECOVERY_KEY");
        if (input is null || output is null || key is null)
        {
            Console.Error.WriteLine("Usage: Kite.Host backup-decrypt --in <file.kbak> --out <file> --recovery-key <key>  (or KITE_RECOVERY_KEY)");
            return 2;
        }
        try
        {
            await using var i = File.OpenRead(input);
            await using var o = File.Create(output);
            await BackupCrypto.DecryptAsync(i, o, BackupCrypto.ParseRecoveryKey(key));
            Console.WriteLine($"Decrypted {input} -> {output}");
            return 0;
        }
        catch (Exception e) when (e is CryptographicException or IOException)
        {
            Console.Error.WriteLine("Could not decrypt: " + e.Message);
            return 1;
        }
    }
}
