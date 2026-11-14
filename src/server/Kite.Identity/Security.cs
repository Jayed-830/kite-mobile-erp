using System.Security.Cryptography;
using Kite.Platform;

namespace Kite.Identity;

/// <summary>Opaque bearer tokens. Only the SHA-256 hash is stored (iam.session).</summary>
public static class Tokens
{
    public static string New() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static byte[] Hash(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(token));
    }

    public static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed record SessionPolicy(TimeSpan AccessLifetime, TimeSpan RefreshLifetime, TimeSpan PosIdleTimeout)
{
    public static readonly SessionPolicy Default = new(TimeSpan.FromMinutes(15), TimeSpan.FromHours(12), TimeSpan.FromMinutes(10));
}

/// <summary>
/// Progressive lockout (blueprint 18.2): after 5 failures the account locks for 15 minutes;
/// every further failure while at or above the threshold doubles the lock, capped at 24 hours.
/// </summary>
public sealed record LockoutPolicy(int Threshold = 5, int BaseMinutes = 15, int MaxMinutes = 1440)
{
    public TimeSpan? LockFor(int failedCount)
    {
        if (failedCount < Threshold) return null;
        var doublings = Math.Min(failedCount - Threshold, 10);
        var minutes = Math.Min(BaseMinutes * (1 << doublings), MaxMinutes);
        return TimeSpan.FromMinutes(minutes);
    }
}

public static class PasswordPolicy
{
    public const int MinLength = 10;

    public static void Check(string password, string username)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length < MinLength)
            throw new KiteException(ErrorCodes.PasswordPolicy, $"Password must be at least {MinLength} characters.", 400, "password");
        if (password.Length > 200)
            throw new KiteException(ErrorCodes.PasswordPolicy, "Password is too long.", 400, "password");
        if (!string.IsNullOrEmpty(username) && password.Contains(username, StringComparison.OrdinalIgnoreCase))
            throw new KiteException(ErrorCodes.PasswordPolicy, "Password must not contain the username.", 400, "password");
        if (password.Distinct().Count() < 4)
            throw new KiteException(ErrorCodes.PasswordPolicy, "Password is too simple.", 400, "password");
    }

    public static void CheckPin(string pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        if (pin.Length is < 4 or > 8 || !pin.All(char.IsAsciiDigit))
            throw new KiteException(ErrorCodes.PasswordPolicy, "PIN must be 4 to 8 digits.", 400, "pin");
        if (pin.Distinct().Count() == 1)
            throw new KiteException(ErrorCodes.PasswordPolicy, "PIN must not repeat a single digit.", 400, "pin");
    }
}

/// <summary>Encrypts small secrets (TOTP keys) with AES-256-GCM (blueprint 18.4).</summary>
public interface ISecretProtector
{
    byte[] Protect(ReadOnlySpan<byte> plaintext);
    byte[] Unprotect(ReadOnlySpan<byte> protectedData);
}

public sealed class AesGcmSecretProtector : ISecretProtector
{
    private const byte FormatVersion = 1;
    private readonly byte[] _key;

    public AesGcmSecretProtector(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 32) throw new ArgumentException("Key must be 32 bytes.", nameof(key));
        _key = key;
    }

    // Layout: version(1) | nonce(12) | tag(16) | ciphertext
    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        var output = new byte[1 + 12 + 16 + plaintext.Length];
        output[0] = FormatVersion;
        var nonce = output.AsSpan(1, 12);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plaintext, output.AsSpan(29), output.AsSpan(13, 16));
        return output;
    }

    public byte[] Unprotect(ReadOnlySpan<byte> protectedData)
    {
        if (protectedData.Length < 29 || protectedData[0] != FormatVersion)
            throw new CryptographicException("Unrecognised protected data.");
        var plain = new byte[protectedData.Length - 29];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(protectedData.Slice(1, 12), protectedData[29..], protectedData.Slice(13, 16), plain);
        return plain;
    }
}

/// <summary>
/// Loads or creates the 32-byte data-protection key in the server's data folder. On Windows the
/// Phase 3 installer wraps this key with DPAPI; here the file is created with owner-only access.
/// </summary>
public static class KeyFile
{
    public static byte[] LoadOrCreate(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (File.Exists(path))
        {
            var existing = File.ReadAllBytes(path);
            if (existing.Length != 32) throw new CryptographicException($"Key file {path} is not 32 bytes.");
            return existing;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var key = RandomNumberGenerator.GetBytes(32);
        using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            fs.Write(key);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return key;
    }
}
