using System.Security.Cryptography;
using System.Text;

namespace Kite.Identity;

/// <summary>
/// Time-based one-time passwords (RFC 6238, HMAC-SHA1, 30-second steps, 6 digits), compatible
/// with common authenticator apps. Works offline because it needs only the shared secret and time.
/// </summary>
public static class Totp
{
    public const int StepSeconds = 30;
    public const int Digits = 6;

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    public static string Code(byte[] secret, DateTimeOffset at) => CodeForStep(secret, at.ToUnixTimeSeconds() / StepSeconds);

    /// <summary>Accepts the current step and one step either side to allow for clock drift.</summary>
    public static bool Verify(byte[] secret, string code, DateTimeOffset at, int window = 1) =>
        MatchStep(secret, code, at, window) is not null;

    /// <summary>
    /// The time step the code belongs to (current step, or one either side for clock drift), or null if it matches none.
    /// Callers store the step and refuse it next time, so a code cannot be used twice.
    /// Every candidate is compared in constant time.
    /// </summary>
    public static long? MatchStep(byte[] secret, string code, DateTimeOffset at, int window = 1)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (string.IsNullOrEmpty(code) || code.Length != Digits || !code.All(char.IsAsciiDigit)) return null;
        var step = at.ToUnixTimeSeconds() / StepSeconds;
        long? matched = null;
        for (var i = -window; i <= window; i++)
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(CodeForStep(secret, step + i)), Encoding.ASCII.GetBytes(code)))
                matched = step + i;
        return matched;
    }

    public static string CodeForStep(byte[] secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        for (var i = 7; i >= 0; i--) { counter[i] = (byte)(step & 0xff); step >>= 8; }
#pragma warning disable CA5350 // RFC 6238 authenticator apps use HMAC-SHA1; this is not used for integrity of stored data.
        var hash = HMACSHA1.HashData(secret, counter);
#pragma warning restore CA5350
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>otpauth:// URI for QR enrolment.</summary>
    public static string EnrolmentUri(byte[] secret, string accountName, string issuer = "KITE MOBILE ERP") =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(accountName)}?secret={Base32.Encode(secret)}&issuer={Uri.EscapeDataString(issuer)}&digits={Digits}&period={StepSeconds}";
}

/// <summary>RFC 4648 base32 without padding, as used by authenticator apps.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b; bits += 8;
            while (bits >= 5) { sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[] Decode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var clean = text.TrimEnd('=').Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        var output = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in clean)
        {
            var v = Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (v < 0) throw new FormatException($"Invalid base32 character '{c}'.");
            buffer = (buffer << 5) | v; bits += 5;
            if (bits >= 8) { output.Add((byte)((buffer >> (bits - 8)) & 0xff)); bits -= 8; }
        }
        return [.. output];
    }
}
