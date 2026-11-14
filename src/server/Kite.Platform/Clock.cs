using System.Globalization;

namespace Kite.Platform;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>Clock whose time is set by tests.</summary>
public sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;

    public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
}

/// <summary>
/// Hybrid logical clock timestamp (blueprint 8.1 rule 3): physical milliseconds, a logical
/// counter, and the node that produced it. Totally ordered, so field-level last-writer-wins
/// is deterministic across servers even when wall clocks drift.
/// </summary>
public readonly record struct HlcTimestamp(long WallMs, int Counter, string NodeId) : IComparable<HlcTimestamp>
{
    public int CompareTo(HlcTimestamp other)
    {
        var c = WallMs.CompareTo(other.WallMs);
        if (c != 0) return c;
        c = Counter.CompareTo(other.Counter);
        return c != 0 ? c : string.CompareOrdinal(NodeId, other.NodeId);
    }

    public static bool operator <(HlcTimestamp a, HlcTimestamp b) => a.CompareTo(b) < 0;
    public static bool operator >(HlcTimestamp a, HlcTimestamp b) => a.CompareTo(b) > 0;
    public static bool operator <=(HlcTimestamp a, HlcTimestamp b) => a.CompareTo(b) <= 0;
    public static bool operator >=(HlcTimestamp a, HlcTimestamp b) => a.CompareTo(b) >= 0;

    /// <summary>Sortable text form, e.g. 0001763140000000-00003-srv1.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{WallMs:D16}-{Counter:D5}-{NodeId}");

    public static HlcTimestamp Parse(string text)
    {
        var parts = text.Split('-', 3);
        if (parts.Length != 3) throw new FormatException($"Not an HLC timestamp: {text}");
        return new HlcTimestamp(
            long.Parse(parts[0], CultureInfo.InvariantCulture),
            int.Parse(parts[1], CultureInfo.InvariantCulture),
            parts[2]);
    }
}

public sealed class HybridLogicalClock
{
    private readonly IClock _clock;
    private readonly object _gate = new();
    private long _lastWall;
    private int _counter;

    public HybridLogicalClock(IClock clock, string nodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        if (nodeId.Contains('-', StringComparison.Ordinal))
            throw new ArgumentException("Node id must not contain '-'.", nameof(nodeId));
        _clock = clock;
        NodeId = nodeId;
    }

    public string NodeId { get; }

    /// <summary>Timestamp for a local event.</summary>
    public HlcTimestamp Now()
    {
        lock (_gate)
        {
            var physical = _clock.UtcNow.ToUnixTimeMilliseconds();
            if (physical > _lastWall)
            {
                _lastWall = physical;
                _counter = 0;
            }
            else
            {
                _counter++;
            }
            return new HlcTimestamp(_lastWall, _counter, NodeId);
        }
    }

    /// <summary>Merges a timestamp received from another node and returns the receive timestamp.</summary>
    public HlcTimestamp Receive(HlcTimestamp remote)
    {
        lock (_gate)
        {
            var physical = _clock.UtcNow.ToUnixTimeMilliseconds();
            var maxWall = Math.Max(Math.Max(_lastWall, remote.WallMs), physical);
            if (maxWall == _lastWall && maxWall == remote.WallMs)
                _counter = Math.Max(_counter, remote.Counter) + 1;
            else if (maxWall == _lastWall)
                _counter++;
            else if (maxWall == remote.WallMs)
                _counter = remote.Counter + 1;
            else
                _counter = 0;
            _lastWall = maxWall;
            return new HlcTimestamp(_lastWall, _counter, NodeId);
        }
    }
}
