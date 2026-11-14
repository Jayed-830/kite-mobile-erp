using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kite.Identity;

/// <summary>
/// NEXTDAB PROTOCOL 1 canonical JSON: keys sorted, separators "," and ":", UTF-8, no ASCII escaping - byte for
/// byte what Nextdab Admin signs (lib/rems.js canon(), Python json.dumps(sort_keys=True, separators=(",", ":"),
/// ensure_ascii=False)). Only strings, integers, booleans, null, arrays and objects are allowed.
/// </summary>
public static class NextdabCanonicalJson
{
    public static string Write(JsonNode? node)
    {
        var sb = new StringBuilder();
        Append(sb, node);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, JsonNode? node)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject o:
                sb.Append('{');
                var first = true;
                foreach (var kv in o.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    AppendString(sb, kv.Key);
                    sb.Append(':');
                    Append(sb, kv.Value);
                }
                sb.Append('}');
                break;
            case JsonArray a:
                sb.Append('[');
                for (var i = 0; i < a.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Append(sb, a[i]);
                }
                sb.Append(']');
                break;
            case JsonValue v:
                if (v.TryGetValue<string>(out var str)) AppendString(sb, str);
                else if (v.TryGetValue<bool>(out var b)) sb.Append(b ? "true" : "false");
                else if (v.TryGetValue<long>(out var n)) sb.Append(n.ToString(CultureInfo.InvariantCulture));
                else throw new FormatException("Only strings, whole numbers, true/false and null are allowed in signed Nextdab data.");
                break;
        }
    }

    // As JavaScript JSON.stringify: escape quote, backslash and control characters; everything else as is.
    private static void AppendString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>Parses JSON so every node can be re-serialised canonically (numbers kept as JsonElement).</summary>
    public static JsonObject ParseObject(string json) =>
        JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 16 }) as JsonObject
        ?? throw new FormatException("Expected a JSON object.");
}

/// <summary>Ed25519 signatures as Nextdab Admin makes them: over <c>"NEXTDAB1|" + canonical JSON</c>.</summary>
public static class NextdabSignature
{
    public const string Prefix = "NEXTDAB1|";

    public static byte[] Message(JsonObject unsigned) => Encoding.UTF8.GetBytes(Prefix + NextdabCanonicalJson.Write(unsigned));

    public static bool Verify(byte[] publicKey, JsonObject unsigned, string signatureHex)
    {
        if (publicKey.Length != 32 || signatureHex.Length != 128) return false;
        byte[] sig;
        try { sig = Convert.FromHexString(signatureHex); } catch (FormatException) { return false; }
        var msg = Message(unsigned);
        return Org.BouncyCastle.Math.EC.Rfc8032.Ed25519.Verify(sig, 0, publicKey, 0, msg, 0, msg.Length);
    }

    /// <summary>For tests and tools: sign as Nextdab Admin would.</summary>
    public static string Sign(byte[] privateKeySeed, JsonObject unsigned)
    {
        var msg = Message(unsigned);
        var sig = new byte[64];
        Org.BouncyCastle.Math.EC.Rfc8032.Ed25519.Sign(privateKeySeed, 0, msg, 0, msg.Length, sig, 0);
        return Convert.ToHexString(sig).ToLowerInvariant();
    }

    public static byte[] PublicKeyOf(byte[] privateKeySeed)
    {
        var pk = new byte[32];
        Org.BouncyCastle.Math.EC.Rfc8032.Ed25519.GeneratePublicKey(privateKeySeed, 0, pk, 0);
        return pk;
    }
}

internal static class JsonText
{
    public static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

public static class MachineId
{
    /// <summary>16 hex in 4 groups, like the Machine IDs Nextdab Admin knows (NEXTDAB PROTOCOL 1).</summary>
    public static string New()
    {
        var hex = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        return $"{hex[..4]}-{hex[4..8]}-{hex[8..12]}-{hex[12..]}";
    }

    public static string? Normalise(string? s)
    {
        var h = new string((s ?? "").Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        return h.Length == 16 ? $"{h[..4]}-{h[4..8]}-{h[8..12]}-{h[12..]}" : null;
    }
}

/// <summary>The terms of a KITE MOBILE ERP licence (blueprint 24.5), as signed by Nextdab Admin.</summary>
public sealed record LicenceTerms(
    string Id, string InstallationId, string Kind, string Customer, DateOnly ValidUntil, int GraceDays,
    int BranchesAllowed, int TerminalsAllowed, IReadOnlyList<string> Modules, DateTimeOffset IssuedAt);

/// <summary>
/// A licence file (.kmelic): <c>{"type":"KITE MOBILE ERP licence file","version":1,"licence":{...},"sig":"..."}</c>.
/// The signature covers the "licence" object only, so the explanatory fields around it can change.
/// </summary>
public static class LicenceFile
{
    public const string Type = "KITE MOBILE ERP licence file";
    public const string Product = "kite-mobile-erp";
    public static readonly string[] Kinds = ["trial", "subscription"];

    public static LicenceTerms Read(string fileText, byte[] publicKey, string installationId)
    {
        JsonObject file;
        try { file = NextdabCanonicalJson.ParseObject(fileText.Trim().TrimStart('﻿')); }
        catch (Exception e) when (e is JsonException or FormatException) { throw new LicenceException("LIC-010", "This is not a KITE MOBILE ERP licence file."); }
        if (JsonText.Str(file["type"]) != Type || file["licence"] is not JsonObject lic || JsonText.Str(file["sig"]) is not { } sig)
            throw new LicenceException("LIC-010", "This is not a KITE MOBILE ERP licence file.");
        if (!NextdabSignature.Verify(publicKey, lic, sig))
            throw new LicenceException("LIC-011", "The licence signature is not valid. Use the file exactly as Nextdab sent it.");
        LicenceTerms t;
        try
        {
            t = new LicenceTerms(
                Id: Req(lic, "id"),
                InstallationId: MachineId.Normalise(Req(lic, "mid")) ?? throw new FormatException("mid"),
                Kind: Req(lic, "kind"),
                Customer: Req(lic, "customer"),
                ValidUntil: DateOnly.ParseExact(Req(lic, "valid_until"), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                GraceDays: Int(lic, "grace_days"),
                BranchesAllowed: Int(lic, "branches"),
                TerminalsAllowed: Int(lic, "terminals"),
                Modules: (lic["modules"] as JsonArray ?? throw new FormatException("modules")).Select(m => JsonText.Str(m) ?? throw new FormatException("modules")).ToArray(),
                IssuedAt: DateTimeOffset.Parse(Req(lic, "issued"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal));
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or KeyNotFoundException or NullReferenceException)
        {
            throw new LicenceException("LIC-010", "The licence file is incomplete.");
        }
        if (JsonText.Str(lic["product"]) != Product) throw new LicenceException("LIC-012", "This licence is for another Nextdab product.");
        if (t.InstallationId != installationId)
            throw new LicenceException("LIC-013", $"This licence is for installation {t.InstallationId}; this installation is {installationId}.");
        if (!Kinds.Contains(t.Kind) || t.GraceDays is < 0 or > 120 || t.BranchesAllowed is < 1 or > 500 || t.TerminalsAllowed is < 1 or > 5000)
            throw new LicenceException("LIC-010", "The licence file has values KITE does not accept.");
        return t;
    }

    private static string Req(JsonObject o, string k) => JsonText.Str(o[k]) is { Length: > 0 } s ? s : throw new FormatException(k);
    private static int Int(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<int>(out var i) ? i : throw new FormatException(k);
}

public sealed class LicenceException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public enum LicenceState { None, Active, Expiring, Grace, Expired, Suspended }

/// <summary>What the licence allows today. Evaluated locally, so trading never waits for Nextdab (24.5).</summary>
public sealed record LicenceStatus(
    LicenceState State, LicenceTerms? Terms, int? DaysLeft, DateOnly? EndsOn,
    int BranchesAllowed, int TerminalsAllowed, bool CanTransact, string? SuspendMessage, string? SuspendMessageAr)
{
    /// <summary>Without a licence: setup and administration work, one branch and one terminal; no trading.</summary>
    public const int UnlicensedBranches = 1;
    public const int UnlicensedTerminals = 1;
    public const int WarnDays = 14;

    public static LicenceStatus Evaluate(LicenceTerms? terms, bool suspended, string? message, string? messageAr, DateOnly today)
    {
        if (terms is null)
            return new(suspended ? LicenceState.Suspended : LicenceState.None, null, null, null, UnlicensedBranches, UnlicensedTerminals, false, message, messageAr);
        var daysLeft = terms.ValidUntil.DayNumber - today.DayNumber;
        var endsOn = terms.ValidUntil.AddDays(terms.GraceDays);
        var state = suspended ? LicenceState.Suspended
            : today <= terms.ValidUntil ? (daysLeft <= WarnDays ? LicenceState.Expiring : LicenceState.Active)
            : today <= endsOn ? LicenceState.Grace
            : LicenceState.Expired;
        var can = state is LicenceState.Active or LicenceState.Expiring or LicenceState.Grace;
        return new(state, terms, daysLeft, endsOn, terms.BranchesAllowed, terms.TerminalsAllowed, can, message, messageAr);
    }

    public bool ModuleLicensed(string module) => Terms is null || Terms.Modules.Contains(module);
}

/// <summary>A signed command from Nextdab Admin (NEXTDAB PROTOCOL 1, part 3).</summary>
public sealed record NextdabCommand(string Id, string Type, string Mid, long? Seq, DateTimeOffset? Issued, DateTimeOffset? Expires, JsonObject Data, JsonObject Raw)
{
    private static readonly string[] BroadcastTypes = ["message", "message_clear", "update", "checkin"];

    /// <summary>Checks signature, product, Machine ID and expiry. Returns null with a reason when it must be ignored.</summary>
    public static NextdabCommand? Verify(JsonObject cmd, byte[] publicKey, string product, string installationId, DateTimeOffset now, out string reason)
    {
        var clone = (JsonObject)cmd.DeepClone();
        var sig = JsonText.Str(clone["sig"]);
        clone.Remove("sig");
        string? S(string k) => JsonText.Str(clone[k]);
        var id = S("id") ?? "";
        var type = S("type") ?? "";
        if (sig is null || !NextdabSignature.Verify(publicKey, clone, sig)) { reason = "bad signature"; return null; }
        if (S("product") != product) { reason = "another product"; return null; }
        var mid = S("mid") ?? "";
        if (mid == "*" ? !BroadcastTypes.Contains(type) : MachineId.Normalise(mid) != installationId) { reason = "not for this installation"; return null; }
        DateTimeOffset? T(string k) => S(k) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
        var expires = T("expires");
        if (expires is not null && expires < now) { reason = "expired"; return null; }
        long? seq = clone["seq"] is JsonValue sv && sv.TryGetValue<long>(out var q) ? q : null;
        reason = "";
        return new NextdabCommand(id, type, mid, seq, T("issued"), expires, clone["data"] as JsonObject ?? [], cmd);
    }
}
