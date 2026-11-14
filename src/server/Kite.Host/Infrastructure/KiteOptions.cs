using System.Security.Cryptography;

namespace Kite.Host.Infrastructure;

public sealed class KiteOptions
{
    public string DataDir { get; set; } = "data";
    public bool MigrateOnStartup { get; set; } = true;
    public Argon2Settings Argon2 { get; set; } = new();
    public SyncSettings Sync { get; set; } = new();
    public LicensingSettings Licensing { get; set; } = new();
    public BackupSettings Backup { get; set; } = new();
}

public sealed class Argon2Settings
{
    public int MemoryKiB { get; set; } = 65536;
    public int Iterations { get; set; } = 3;
    public int Parallelism { get; set; } = 2;
}

/// <summary>
/// One-time setup code (blueprint 24.3). Until the business is set up, the setup endpoint
/// needs this code, which only someone with access to the server PC can read, so a stranger
/// on the shop network cannot claim a fresh installation.
/// </summary>
public sealed class SetupCodeStore(string path)
{
    private readonly object _gate = new();

    public string GetOrCreate()
    {
        lock (_gate)
        {
            if (File.Exists(path)) return File.ReadAllText(path).Trim();
            const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var code = string.Create(8, 0, (span, _) =>
            {
                for (var i = 0; i < span.Length; i++) span[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
            });
            File.WriteAllText(path, code);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return code;
        }
    }

    public bool Matches(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        var expected = GetOrCreate();
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(expected),
            System.Text.Encoding.ASCII.GetBytes(candidate.Trim().ToUpperInvariant()));
    }

    public void Consume()
    {
        lock (_gate) { if (File.Exists(path)) File.Delete(path); }
    }
}
