using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Kite.Platform;
using Npgsql;

namespace Kite.Persistence;

/// <summary>Who is acting, for provenance, audit and outbox columns.</summary>
public sealed record ActorContext(Guid? UserId, Guid? BranchId, Guid? TerminalId, string? Ip)
{
    public static readonly ActorContext System = new(null, null, null, null);
}

/// <summary>Identity of this server installation (core.server where is_self).</summary>
public sealed class ServerContext
{
    public Guid? ServerId { get; private set; }
    public Guid? BusinessId { get; private set; }
    public Guid? BranchId { get; private set; }
    public string Role { get; private set; } = "combined";
    /// <summary>The Machine ID Nextdab knows this installation by (blueprint 24.5).</summary>
    public string? InstallationId { get; private set; }
    public bool IsConfigured => ServerId is not null && BusinessId is not null;
    /// <summary>Main or combined: holds the business CA, talks to Nextdab, receives branch heartbeats.</summary>
    public bool IsHeadOffice => Role is "combined" or "main";

    public void Set(Guid serverId, Guid businessId, Guid? branchId, string role, string? installationId = null)
    {
        ServerId = serverId; BusinessId = businessId; BranchId = branchId; Role = role; InstallationId = installationId;
    }

    /// <summary>Used if a setup transaction fails after Set was called.</summary>
    public void Reset()
    {
        ServerId = null; BusinessId = null; BranchId = null; Role = "combined"; InstallationId = null;
    }

    public async Task LoadAsync(NpgsqlDataSource dataSource, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<(Guid Id, Guid? BusinessId, Guid? BranchId, string Role, string? InstallationId)>(
            new CommandDefinition("SELECT id, business_id, branch_id, role, installation_id FROM core.server WHERE is_self", cancellationToken: ct));
        if (row.Id != Guid.Empty && row.BusinessId is not null)
            Set(row.Id, row.BusinessId.Value, row.BranchId, row.Role, row.InstallationId);
    }
}

/// <summary>
/// Unit of work over one PostgreSQL transaction. Applies the caller's branch scope to the
/// database (row-level security) and offers audit and outbox writes inside the same
/// transaction, so a business change, its audit row and its sync event commit together.
/// </summary>
public sealed class KiteDb(NpgsqlDataSource dataSource, ServerContext server, IIdGenerator ids, IClock clock)
{
    public NpgsqlDataSource DataSource => dataSource;

    public async Task<T> InTransactionAsync<T>(AccessScope scope, ActorContext actor, Func<DbTx, Task<T>> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await ApplyScope(conn, tx, scope, ct);
        var result = await work(new DbTx(conn, tx, actor, server, ids, clock, ct));
        await tx.CommitAsync(ct);
        return result;
    }

    public Task InTransactionAsync(AccessScope scope, ActorContext actor, Func<DbTx, Task> work, CancellationToken ct = default) =>
        InTransactionAsync<bool>(scope, actor, async t => { await work(t); return true; }, ct);

    private static async Task ApplyScope(NpgsqlConnection conn, NpgsqlTransaction tx, AccessScope scope, CancellationToken ct)
    {
        await conn.ExecuteAsync(new CommandDefinition(
            "SELECT set_config('kite.all_branches', @all, true), set_config('kite.branch_ids', @ids, true)",
            new { all = scope.AllBranches ? "on" : "", ids = scope.AllBranches || scope.BranchIds.Count == 0 ? "" : scope.ToPgArray() },
            tx, cancellationToken: ct));
    }
}

public sealed class DbTx(NpgsqlConnection conn, NpgsqlTransaction tx, ActorContext actor, ServerContext server, IIdGenerator ids, IClock clock, CancellationToken ct)
{
    public NpgsqlConnection Connection => conn;
    public NpgsqlTransaction Transaction => tx;
    public ActorContext Actor => actor;
    public CancellationToken Cancellation => ct;
    public IClock Clock => clock;
    public Guid NewId() => ids.NewId();

    public Task<int> ExecuteAsync(string sql, object? param = null) =>
        conn.ExecuteAsync(new CommandDefinition(sql, param, tx, cancellationToken: ct));

    public Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null) =>
        conn.QueryAsync<T>(new CommandDefinition(sql, param, tx, cancellationToken: ct));

    /// <summary>Rows as name/value dictionaries, for read-only lists returned as JSON (column aliases are the JSON names).</summary>
    public async Task<IReadOnlyList<Dictionary<string, object?>>> RowsAsync(string sql, object? param = null) =>
        (await conn.QueryAsync(new CommandDefinition(sql, param, tx, cancellationToken: ct)))
            .Select(r => new Dictionary<string, object?>((IDictionary<string, object?>)r, StringComparer.Ordinal)).ToList();

    public Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? param = null) =>
        conn.QuerySingleOrDefaultAsync<T>(new CommandDefinition(sql, param, tx, cancellationToken: ct));

    public Task<T> ExecuteScalarAsync<T>(string sql, object? param = null) =>
        conn.ExecuteScalarAsync<T>(new CommandDefinition(sql, param, tx, cancellationToken: ct))!;

    /// <summary>
    /// Runs business-wide bookkeeping (rows with no branch, such as the customer number series or fiscal periods) with
    /// all-branches scope inside a branch-scoped transaction, then restores the caller's scope. Server code only.
    /// </summary>
    public async Task<T> WithAllBranchesAsync<T>(Func<Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        var previous = await ExecuteScalarAsync<string?>("SELECT current_setting('kite.all_branches', true)");
        if (previous == "on") return await work();
        await ExecuteAsync("SELECT set_config('kite.all_branches', 'on', true)");
        var result = await work();
        await ExecuteAsync("SELECT set_config('kite.all_branches', '', true)");
        return result;
    }

    /// <summary>Appends to the hash-chained audit log (the database computes seq and hashes).</summary>
    public Task AuditAsync(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return ExecuteAsync("""
            INSERT INTO audit.audit_log (id, business_id, branch_id, user_id, terminal_id, action, entity_type, entity_id,
                                         before, after, reason, ip, origin_server_id)
            VALUES (@id, @business, @branch, @user, @terminal, @action, @entityType, @entityId,
                    @before::jsonb, @after::jsonb, @reason, @ip::inet, @server)
            """,
            new
            {
                id = ids.NewId(),
                business = server.BusinessId,
                branch = entry.BranchId ?? actor.BranchId,
                user = actor.UserId,
                terminal = actor.TerminalId,
                action = entry.Action,
                entityType = entry.EntityType,
                entityId = entry.EntityId,
                before = entry.Before?.ToJsonString(),
                after = entry.After?.ToJsonString(),
                reason = entry.Reason,
                ip = actor.Ip,
                server = server.ServerId,
            });
    }

    /// <summary>
    /// Writes a sync event to the transactional outbox (blueprint 7.2). Requires a configured
    /// server; during setup the server row is created first in the same transaction.
    /// </summary>
    public async Task<Guid> PublishAsync(string eventType, string aggregateType, Guid aggregateId, int aggregateVersion, JsonObject payload, Guid? branchId = null)
    {
        var envelope = new EventEnvelope(
            ids.NewId(), eventType, 1, aggregateType, aggregateId, aggregateVersion,
            server.BusinessId ?? throw new InvalidOperationException("Business is not configured."),
            branchId ?? actor.BranchId,
            server.ServerId ?? throw new InvalidOperationException("Server is not configured."),
            actor.TerminalId, actor.UserId, clock.UtcNow, null, null, payload);
        envelope.Validate();
        await ExecuteAsync("""
            INSERT INTO sync.outbox (event_id, event_type, schema_version, aggregate_type, aggregate_id, aggregate_version,
                                     business_id, origin_branch_id, origin_server_id, origin_device_id, actor_user_id,
                                     occurred_at, causation_id, correlation_id, payload)
            VALUES (@EventId, @EventType, @SchemaVersion, @AggregateType, @AggregateId, @AggregateVersion,
                    @BusinessId, @OriginBranchId, @OriginServerId, @OriginDeviceId, @ActorUserId,
                    @OccurredAt, @CausationId, @CorrelationId, @payload::jsonb)
            """,
            new
            {
                envelope.EventId, envelope.EventType, envelope.SchemaVersion, envelope.AggregateType, envelope.AggregateId,
                envelope.AggregateVersion, envelope.BusinessId, envelope.OriginBranchId, envelope.OriginServerId,
                envelope.OriginDeviceId, envelope.ActorUserId, envelope.OccurredAt, envelope.CausationId, envelope.CorrelationId,
                payload = envelope.Payload.ToJsonString(),
            });
        return envelope.EventId;
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>Serialises any object to a JsonObject for audit and outbox payloads.</summary>
    public static JsonObject Obj(object value) =>
        JsonSerializer.SerializeToNode(value, Web)?.AsObject() ?? [];
}
