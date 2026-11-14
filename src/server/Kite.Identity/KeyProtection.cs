using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Kite.Identity;

/// <summary>
/// Protects key material at rest (blueprint 18.1, 18.4). On Windows servers keys are wrapped with DPAPI in
/// machine scope, so a copied key file is useless on another PC. Elsewhere (development, CI on Linux) the
/// key is stored with owner-only file permissions and reported as unprotected.
/// </summary>
public interface IKeyProtector
{
    /// <summary>"dpapi-machine" or "file-permissions".</summary>
    string Kind { get; }
    byte[] Protect(byte[] plaintext, string purpose);
    byte[] Unprotect(byte[] protectedData, string purpose);
}

[SupportedOSPlatform("windows")]
public sealed class DpapiMachineKeyProtector : IKeyProtector
{
    public string Kind => "dpapi-machine";

    public byte[] Protect(byte[] plaintext, string purpose) =>
        ProtectedData.Protect(plaintext, Entropy(purpose), DataProtectionScope.LocalMachine);

    public byte[] Unprotect(byte[] protectedData, string purpose) =>
        ProtectedData.Unprotect(protectedData, Entropy(purpose), DataProtectionScope.LocalMachine);

    private static byte[] Entropy(string purpose) => Encoding.UTF8.GetBytes("KITE MOBILE ERP|" + purpose);
}

/// <summary>No wrapping; the file is readable only by the service account (Linux/macOS development and CI).</summary>
public sealed class FilePermissionKeyProtector : IKeyProtector
{
    public string Kind => "file-permissions";
    public byte[] Protect(byte[] plaintext, string purpose) => (byte[])plaintext.Clone();
    public byte[] Unprotect(byte[] protectedData, string purpose) => (byte[])protectedData.Clone();
}

public static class KeyProtectors
{
    public static IKeyProtector ForThisMachine() =>
        OperatingSystem.IsWindows() ? new DpapiMachineKeyProtector() : new FilePermissionKeyProtector();
}

/// <summary>
/// A secret kept in one file: header <c>KITEKEY1|kind|purpose\n</c> then the protected bytes.
/// A plain 32-byte key file from Phase 2 is upgraded in place the first time it is loaded.
/// </summary>
public static class ProtectedKeyFile
{
    private const string Magic = "KITEKEY1";

    public static byte[] LoadOrCreate(string path, IKeyProtector protector, string purpose, int size = 32) =>
        TryLoad(path, protector, purpose) ?? Save(path, protector, purpose, RandomNumberGenerator.GetBytes(size));

    public static byte[]? TryLoad(string path, IKeyProtector protector, string purpose)
    {
        if (!File.Exists(path)) return null;
        var bytes = File.ReadAllBytes(path);
        var nl = Array.IndexOf(bytes, (byte)'\n');
        if (nl > 0 && nl < 200 && Encoding.ASCII.GetString(bytes, 0, nl).StartsWith(Magic + "|", StringComparison.Ordinal))
        {
            var parts = Encoding.ASCII.GetString(bytes, 0, nl).Split('|');
            if (parts.Length != 3 || parts[2] != purpose)
                throw new CryptographicException($"Key file {path} is for '{(parts.Length == 3 ? parts[2] : "?")}', not '{purpose}'.");
            var body = bytes[(nl + 1)..];
            if (parts[1] == protector.Kind) return protector.Unprotect(body, purpose);
            if (parts[1] == "file-permissions")
            {
                // Moved from an unprotected store (for example a restored backup) to a protected one: wrap it now.
                var key = body;
                Save(path, protector, purpose, key);
                return key;
            }
            throw new CryptographicException($"Key file {path} is protected with {parts[1]}, which this machine cannot open.");
        }
        if (bytes.Length is 32 or 64)
        {
            // Phase 2 format: raw key bytes. Upgrade (on Windows this wraps it with DPAPI).
            Save(path, protector, purpose, bytes);
            return bytes;
        }
        throw new CryptographicException($"Key file {path} is not a KITE key file.");
    }

    public static byte[] Save(string path, IKeyProtector protector, string purpose, byte[] key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var header = Encoding.ASCII.GetBytes($"{Magic}|{protector.Kind}|{purpose}\n");
        var body = protector.Protect(key, purpose);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(header);
            fs.Write(body);
            fs.Flush(true);
        }
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, path, overwrite: true);
        return key;
    }
}
