using Kite.Identity;

namespace Kite.Host.Infrastructure;

public sealed class SyncSettings
{
    /// <summary>TLS port for branch registration and heartbeats on the main/combined server; 0 = off.</summary>
    public int Port { get; set; } = 5443;
    /// <summary>Extra names or addresses branches use to reach this server (comma-separated).</summary>
    public string Hosts { get; set; } = "";
    /// <summary>Branch servers: seconds between heartbeats (blueprint 4.4).</summary>
    public int HeartbeatSeconds { get; set; } = 30;
}

public sealed class LicensingSettings
{
    /// <summary>Nextdab's control server (the same address Kite RMS and Kite REMS use).</summary>
    public string ControlUrl { get; set; } = "https://control.nextdab.com";
    /// <summary>Development only: a test public key instead of the embedded Nextdab key.</summary>
    public string? DevPublicKey { get; set; }
    public bool CheckinEnabled { get; set; } = true;
}

public sealed class BackupSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Second physical disk on production servers (blueprint 19.1).</summary>
    public string Folder { get; set; } = "backups";
    /// <summary>NAS or rotated USB drive for the nightly off-machine copy; empty = none.</summary>
    public string OffsiteFolder { get; set; } = "";
    /// <summary>Folder with pg_dump, pg_restore, pg_basebackup, pg_verifybackup; empty = on PATH.</summary>
    public string PgBin { get; set; } = "";
    public string LogicalAt { get; set; } = "01:30";
    public string BaseAt { get; set; } = "02:00";
    public bool BaseBackups { get; set; } = true;
    public DayOfWeek VerifyDay { get; set; } = DayOfWeek.Friday;
    public string VerifyAt { get; set; } = "03:00";
    /// <summary>Alert when no successful backup is newer than this (blueprint 19.3).</summary>
    public int AlertAfterHours { get; set; } = 26;
}

/// <summary>Key files in the data folder, protected for this machine (DPAPI on Windows).</summary>
public sealed class KeyStore(string dataDir, IKeyProtector protector)
{
    public IKeyProtector Protector => protector;
    public string DataDir => dataDir;
    public string PathOf(string name) => Path.Combine(dataDir, name);
    public byte[] LoadOrCreate(string name, string purpose, int size = 32) => ProtectedKeyFile.LoadOrCreate(PathOf(name), protector, purpose, size);
    public byte[]? TryLoad(string name, string purpose) => ProtectedKeyFile.TryLoad(PathOf(name), protector, purpose);
    public void Save(string name, string purpose, byte[] key) => ProtectedKeyFile.Save(PathOf(name), protector, purpose, key);
}

/// <summary>
/// The Nextdab public key that licences and control commands are checked with (blueprint 24.5). It is
/// compiled in, so a customer cannot swap it. Nextdab Admin 5.14 makes the KITE MOBILE ERP key pair on the
/// Nextdab PC (the private key never leaves it, sealed with the owner password) and shows the public key
/// under Settings; that value replaces <see cref="EmbeddedHex"/>. Until then no licence can be imported.
/// </summary>
public sealed class LicenceKeys
{
    public const string EmbeddedHex = "0000000000000000000000000000000000000000000000000000000000000000";

    public LicenceKeys(byte[] publicKey) => PublicKey = publicKey;

    public byte[] PublicKey { get; }
    public bool IsConfigured => PublicKey.Length == 32 && PublicKey.Any(b => b != 0);
    public string Hex => Convert.ToHexString(PublicKey).ToLowerInvariant();

    public static LicenceKeys Create(LicensingSettings settings, bool isDevelopment) =>
        new(isDevelopment && !string.IsNullOrWhiteSpace(settings.DevPublicKey)
            ? Convert.FromHexString(settings.DevPublicKey.Trim())
            : Convert.FromHexString(EmbeddedHex));
}
