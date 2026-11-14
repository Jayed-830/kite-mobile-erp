using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace Kite.Identity;

public interface IPasswordHasher
{
    string Hash(string secret);
    bool Verify(string secret, string encoded);
    bool NeedsRehash(string encoded);
}

/// <summary>Argon2id cost settings. Tuned so one hash takes roughly 250 ms on server hardware (blueprint 18.2).</summary>
public sealed record Argon2Options(int MemoryKiB = 65536, int Iterations = 3, int Parallelism = 2, int SaltBytes = 16, int HashBytes = 32)
{
    /// <summary>Cheap settings for unit tests only.</summary>
    public static readonly Argon2Options ForTests = new(MemoryKiB: 1024, Iterations: 1, Parallelism: 1);
}

/// <summary>
/// Argon2id hashing stored in PHC string format:
/// $argon2id$v=19$m=65536,t=3,p=2$&lt;salt b64&gt;$&lt;hash b64&gt;
/// </summary>
public sealed class Argon2PasswordHasher(Argon2Options options) : IPasswordHasher
{
    public Argon2PasswordHasher() : this(new Argon2Options()) { }

    public string Hash(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var salt = RandomNumberGenerator.GetBytes(options.SaltBytes);
        var hash = Compute(secret, salt, options.MemoryKiB, options.Iterations, options.Parallelism, options.HashBytes);
        return string.Create(CultureInfo.InvariantCulture,
            $"$argon2id$v=19$m={options.MemoryKiB},t={options.Iterations},p={options.Parallelism}${B64(salt)}${B64(hash)}");
    }

    public bool Verify(string secret, string encoded)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (!TryParse(encoded, out var m, out var t, out var p, out var salt, out var expected)) return false;
        var actual = Compute(secret, salt, m, t, p, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public bool NeedsRehash(string encoded) =>
        !TryParse(encoded, out var m, out var t, out var p, out _, out _)
        || m != options.MemoryKiB || t != options.Iterations || p != options.Parallelism;

    private static byte[] Compute(string secret, byte[] salt, int memoryKiB, int iterations, int parallelism, int length)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(secret))
        {
            Salt = salt,
            MemorySize = memoryKiB,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon.GetBytes(length);
    }

    private static bool TryParse(string encoded, out int m, out int t, out int p, out byte[] salt, out byte[] hash)
    {
        m = t = p = 0; salt = hash = [];
        if (string.IsNullOrEmpty(encoded)) return false;
        var parts = encoded.Split('$');
        // ["", "argon2id", "v=19", "m=..,t=..,p=..", salt, hash]
        if (parts.Length != 6 || parts[1] != "argon2id" || parts[2] != "v=19") return false;
        foreach (var kv in parts[3].Split(','))
        {
            var pair = kv.Split('=');
            if (pair.Length != 2 || !int.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out var v)) return false;
            switch (pair[0]) { case "m": m = v; break; case "t": t = v; break; case "p": p = v; break; default: return false; }
        }
        try { salt = FromB64(parts[4]); hash = FromB64(parts[5]); }
        catch (FormatException) { return false; }
        return m > 0 && t > 0 && p > 0 && salt.Length >= 8 && hash.Length >= 16;
    }

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=');

    private static byte[] FromB64(string s) => Convert.FromBase64String(s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '='));
}
