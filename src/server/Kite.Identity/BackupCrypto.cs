using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Kite.Identity;

/// <summary>
/// Backup files (.kbak) are AES-256-GCM encrypted in 1 MiB chunks (blueprint 18.4, 19). Each chunk's nonce is
/// a random prefix plus the chunk number, and the header, chunk number and a last-chunk flag are authenticated,
/// so a file cannot be reordered, truncated or have chunks swapped without detection.
/// Layout: "KITEBAK1" | version(1) | key id(8) | chunk size(4) | nonce prefix(8) | chunks...
/// Chunk: length(4) | ciphertext | tag(16); the length's top bit marks the last chunk.
/// </summary>
public static class BackupCrypto
{
    private static readonly byte[] Magic = "KITEBAK1"u8.ToArray();
    private const byte Version = 1;
    public const int ChunkSize = 1 << 20;
    private const int HeaderSize = 8 + 1 + 8 + 4 + 8;
    private const uint LastFlag = 0x8000_0000;

    public static byte[] KeyId(byte[] key) => SHA256.HashData(key)[..8];

    public static async Task EncryptAsync(Stream input, Stream output, byte[] key, CancellationToken ct = default)
    {
        if (key.Length != 32) throw new ArgumentException("A backup key is 32 bytes.", nameof(key));
        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        header[8] = Version;
        KeyId(key).CopyTo(header, 9);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(17), ChunkSize);
        RandomNumberGenerator.Fill(header.AsSpan(21, 8));
        await output.WriteAsync(header, ct);

        using var gcm = new AesGcm(key, 16);
        var plain = new byte[ChunkSize];
        var next = new byte[ChunkSize];
        var cipher = new byte[ChunkSize];
        var tag = new byte[16];
        var len = await FillAsync(input, plain, ct);
        uint counter = 0;
        while (true)
        {
            var nextLen = len == ChunkSize ? await FillAsync(input, next, ct) : 0;
            var last = nextLen == 0;
            var nonce = Nonce(header, counter);
            gcm.Encrypt(nonce, plain.AsSpan(0, len), cipher.AsSpan(0, len), tag, Aad(header, counter, last));
            var lenField = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(lenField, (uint)len | (last ? LastFlag : 0));
            await output.WriteAsync(lenField, ct);
            await output.WriteAsync(cipher.AsMemory(0, len), ct);
            await output.WriteAsync(tag, ct);
            if (last) break;
            (plain, next) = (next, plain);
            len = nextLen;
            counter = checked(counter + 1);
        }
    }

    public static async Task DecryptAsync(Stream input, Stream output, byte[] key, CancellationToken ct = default)
    {
        var header = new byte[HeaderSize];
        if (await FillAsync(input, header, ct) != HeaderSize || !header.AsSpan(0, 8).SequenceEqual(Magic) || header[8] != Version)
            throw new CryptographicException("This is not a KITE backup file.");
        if (!header.AsSpan(9, 8).SequenceEqual(KeyId(key)))
            throw new CryptographicException("This backup was made with a different backup key. Use the recovery key printed for this installation.");
        var chunk = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(17));
        if (chunk is <= 0 or > 16 << 20) throw new CryptographicException("The backup file header is damaged.");
        using var gcm = new AesGcm(key, 16);
        var cipher = new byte[chunk];
        var plain = new byte[chunk];
        var tag = new byte[16];
        var lenField = new byte[4];
        uint counter = 0;
        while (true)
        {
            if (await FillAsync(input, lenField, ct) != 4) throw new CryptographicException("The backup file is incomplete (cut short).");
            var raw = BinaryPrimitives.ReadUInt32BigEndian(lenField);
            var last = (raw & LastFlag) != 0;
            var len = (int)(raw & ~LastFlag);
            if (len > chunk) throw new CryptographicException("The backup file is damaged.");
            if (await FillAsync(input, cipher.AsMemory(0, len), ct) != len || await FillAsync(input, tag, ct) != 16)
                throw new CryptographicException("The backup file is incomplete (cut short).");
            try { gcm.Decrypt(Nonce(header, counter), cipher.AsSpan(0, len), tag, plain.AsSpan(0, len), Aad(header, counter, last)); }
            catch (AuthenticationTagMismatchException) { throw new CryptographicException($"The backup file is damaged or was changed (part {counter + 1})."); }
            await output.WriteAsync(plain.AsMemory(0, len), ct);
            if (last) break;
            counter = checked(counter + 1);
        }
        if (input.ReadByte() != -1) throw new CryptographicException("The backup file has unexpected data after its end.");
    }

    /// <summary>The printed recovery key: the 32-byte backup key in base32, groups of four (13 groups).</summary>
    public static string FormatRecoveryKey(byte[] key)
    {
        var b32 = Base32.Encode(key);
        return string.Join("-", Enumerable.Range(0, (b32.Length + 3) / 4).Select(i => b32.Substring(i * 4, Math.Min(4, b32.Length - i * 4))));
    }

    public static byte[] ParseRecoveryKey(string text)
    {
        var clean = new string(text.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        byte[] key;
        try { key = Base32.Decode(clean); } catch (FormatException) { throw new CryptographicException("A recovery key has only the letters A-Z and the digits 2-7."); }
        if (key.Length != 32) throw new CryptographicException("A recovery key has 52 letters and digits.");
        return key;
    }

    private static byte[] Nonce(byte[] header, uint counter)
    {
        var n = new byte[12];
        header.AsSpan(21, 8).CopyTo(n);
        BinaryPrimitives.WriteUInt32BigEndian(n.AsSpan(8), counter);
        return n;
    }

    private static byte[] Aad(byte[] header, uint counter, bool last)
    {
        var a = new byte[HeaderSize + 5];
        header.CopyTo(a, 0);
        BinaryPrimitives.WriteUInt32BigEndian(a.AsSpan(HeaderSize), counter);
        a[HeaderSize + 4] = last ? (byte)1 : (byte)0;
        return a;
    }

    private static async Task<int> FillAsync(Stream s, Memory<byte> buf, CancellationToken ct)
    {
        var total = 0;
        while (total < buf.Length)
        {
            var n = await s.ReadAsync(buf[total..], ct);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    private static Task<int> FillAsync(Stream s, byte[] buf, CancellationToken ct) => FillAsync(s, buf.AsMemory(), ct);
}
