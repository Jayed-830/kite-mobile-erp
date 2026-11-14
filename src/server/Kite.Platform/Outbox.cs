using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Kite.Platform;

/// <summary>
/// Sync event envelope, schema v1 (blueprint 7.1). origin_seq and the payload hash are
/// assigned by the database when the event is written to sync.outbox.
/// </summary>
public sealed record EventEnvelope(
    Guid EventId,
    string EventType,
    int SchemaVersion,
    string AggregateType,
    Guid AggregateId,
    int AggregateVersion,
    Guid BusinessId,
    Guid? OriginBranchId,
    Guid OriginServerId,
    Guid? OriginDeviceId,
    Guid? ActorUserId,
    DateTimeOffset OccurredAt,
    Guid? CausationId,
    Guid? CorrelationId,
    JsonObject Payload)
{
    private static readonly Regex TypePattern = new(@"^[a-z][a-z0-9_]*\.[a-z][a-z0-9_.]*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static bool IsValidEventType(string eventType) => TypePattern.IsMatch(eventType);

    public void Validate()
    {
        if (!IsValidEventType(EventType)) throw new KiteException(ErrorCodes.Validation, $"Invalid event type '{EventType}'.");
        if (SchemaVersion < 1) throw new KiteException(ErrorCodes.Validation, "Schema version must be 1 or higher.");
        if (AggregateVersion < 1) throw new KiteException(ErrorCodes.Validation, "Aggregate version must be 1 or higher.");
        if (EventId == Guid.Empty || AggregateId == Guid.Empty || BusinessId == Guid.Empty || OriginServerId == Guid.Empty)
            throw new KiteException(ErrorCodes.Validation, "Event identifiers must not be empty.");
    }
}

/// <summary>Who is acting and on which branches (blueprint 18.3). Applied to the database as RLS settings.</summary>
public sealed record AccessScope(bool AllBranches, IReadOnlyList<Guid> BranchIds)
{
    public static readonly AccessScope System = new(true, []);
    public static readonly AccessScope None = new(false, []);

    public static AccessScope ForBranches(IEnumerable<Guid> ids) => new(false, ids.Distinct().ToArray());

    public bool CanSee(Guid? branchId) => AllBranches || branchId is null || BranchIds.Contains(branchId.Value);

    /// <summary>PostgreSQL array literal for kite.branch_ids, e.g. {uuid,uuid}.</summary>
    public string ToPgArray() => "{" + string.Join(',', BranchIds.Select(b => b.ToString("D"))) + "}";
}

/// <summary>An entry for the hash-chained audit log; the database assigns seq and hashes.</summary>
public sealed record AuditEntry(
    string Action,
    string? EntityType = null,
    Guid? EntityId = null,
    JsonNode? Before = null,
    JsonNode? After = null,
    string? Reason = null,
    Guid? BranchId = null);
