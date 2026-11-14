namespace Kite.Platform;

/// <summary>
/// Global identifiers (blueprint 1.1 decision 6): UUIDv7, time-ordered and safe to generate
/// offline on any server or terminal without coordination.
/// </summary>
public interface IIdGenerator
{
    Guid NewId();
}

public sealed class UuidV7Generator : IIdGenerator
{
    public static readonly UuidV7Generator Instance = new();

    public Guid NewId() => Guid.CreateVersion7();
}

public static class Ids
{
    public static Guid New() => Guid.CreateVersion7();

    /// <summary>Version nibble of a GUID as defined by RFC 9562 (7 for UUIDv7).</summary>
    public static int VersionOf(Guid id) => id.Version;
}
