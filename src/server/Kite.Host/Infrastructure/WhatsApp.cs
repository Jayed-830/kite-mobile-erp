using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kite.Host.Features;
using Kite.Identity;
using Kite.Persistence;
using Kite.Platform;

namespace Kite.Host.Infrastructure;

/// <summary>The WhatsApp gateway's own settings (5.3): the loopback port the webhook is served on for the tunnel, and the Graph API address.</summary>
public sealed class WhatsAppSettings
{
    public bool Background { get; set; } = true;
    public int WebhookPort { get; set; } = 8791;
    public string GraphBase { get; set; } = "https://graph.facebook.com/v21.0";
    public int IntervalSeconds { get; set; } = 4;
}

/// <summary>A message handed from the server that queued it to the main server's gateway.</summary>
public sealed record RelayedMessage(Guid Id, Guid BusinessId, Guid BranchId, string Phone, string Lang, string? Template, string Params, string? BodyText,
    DateTime? OptInAt, DateTime ExpiresAt);

/// <summary>What happened to a message, or what a customer sent, for the server it belongs to (ordered by Seq).</summary>
public sealed record GatewayEventDto(long Seq, Guid? MessageId, string Kind, string? Status, string? Reason, string? Phone, string? Body, string? Wamid, DateTime At);

public sealed record NotifPushRequest(List<RelayedMessage> Messages);
public sealed record NotifEventsRequest(long After);
public sealed record NotifEventsResponse(List<GatewayEventDto> Events);

public sealed record WaSend(Guid MessageId, string Phone, string? TemplateName, string? TemplateLang, IReadOnlyList<string> Params, IReadOnlyList<string> Buttons,
    string? Text, string Rendered);
public sealed record WaResult(bool Ok, string? Wamid, string? Error, bool Retry);

public interface IWhatsAppProvider
{
    Task<WaResult> SendAsync(WaSend m, CancellationToken ct);
}

/// <summary>
/// Meta's WhatsApp Cloud API (the owner's choice, as Kite REMS uses it): templates with quick-reply buttons whose payload names our
/// message, and free-text replies inside the 24-hour window. The access token never leaves the main server.
/// </summary>
public sealed class MetaCloudProvider(HttpClient http, string graphBase, string phoneNumberId, string accessToken) : IWhatsAppProvider
{
    public async Task<WaResult> SendAsync(WaSend m, CancellationToken ct)
    {
        object body;
        if (m.TemplateName is null)
            body = new { messaging_product = "whatsapp", to = m.Phone.TrimStart('+'), type = "text", text = new { body = m.Text, preview_url = false } };
        else
        {
            var components = new List<object>();
            if (m.Params.Count > 0)
                components.Add(new { type = "body", parameters = m.Params.Select(p => new { type = "text", text = p }).ToArray() });
            for (var i = 0; i < m.Buttons.Count; i++)
                components.Add(new
                {
                    type = "button", sub_type = "quick_reply", index = i.ToString(CultureInfo.InvariantCulture),
                    parameters = new[] { new { type = "payload", payload = (i == 0 ? "approve:" : "call:") + m.MessageId.ToString("N") } },
                });
            body = new { messaging_product = "whatsapp", to = m.Phone.TrimStart('+'), type = "template",
                         template = new { name = m.TemplateName, language = new { code = m.TemplateLang }, components } };
        }
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{graphBase.TrimEnd('/')}/{phoneNumberId}/messages") { Content = JsonContent.Create(body) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        try
        {
            using var r = await http.SendAsync(req, ct);
            var text = await r.Content.ReadAsStringAsync(ct);
            if (r.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(text);
                var wamid = doc.RootElement.TryGetProperty("messages", out var ms) && ms.GetArrayLength() > 0 ? ms[0].GetProperty("id").GetString() : null;
                return wamid is null ? new WaResult(false, null, "WhatsApp did not return a message ID.", true) : new WaResult(true, wamid, null, false);
            }
            var error = text;
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("error", out var e))
                    error = $"{(e.TryGetProperty("code", out var c) ? c.ToString() : "")} {(e.TryGetProperty("message", out var msg) ? msg.GetString() : "")}".Trim();
            }
            catch (JsonException) { }
            var retry = (int)r.StatusCode >= 500 || (int)r.StatusCode == 429;
            return new WaResult(false, null, error.Length > 300 ? error[..300] : error, retry);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new WaResult(false, null, "WhatsApp could not be reached: " + e.Message, true);
        }
    }
}

/// <summary>The test chat (provider "test"): what the customer's phone would show, kept on the main server for demos and tests.</summary>
public sealed class TestChatProvider(KiteDb db) : IWhatsAppProvider
{
    public async Task<WaResult> SendAsync(WaSend m, CancellationToken ct)
    {
        var wamid = "wamid.test." + Guid.CreateVersion7().ToString("N");
        await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => t.ExecuteAsync("""
            INSERT INTO notif.test_chat (phone_e164, direction, wamid, body, buttons) VALUES (@Phone, 'out', @wamid, @Rendered, @buttons)
            """, new { m.Phone, wamid, m.Rendered, buttons = m.Buttons.ToArray() }), ct);
        return new WaResult(true, wamid, null, false);
    }
}

/// <summary>
/// The WhatsApp gateway on the main server (5.3): accepts messages from every server, sends them within the daily cap and the
/// rules (consent, STOP, stale, the 24-hour window), receives the webhook (statuses, replies, button taps, STOP) and keeps an
/// ordered stream of events for each server.
/// </summary>
public sealed class WhatsAppGateway(KiteDb db, ServerContext server, ISecretProtector secrets, KiteOptions opts, IHttpClientFactory http)
{
    public bool IsGateway => server.IsConfigured && server.Role != "branch";

    public sealed class GatewaySettings
    {
        public Guid BusinessId { get; set; }
        public bool Enabled { get; set; }
        public string Provider { get; set; } = "test";
        public string? PhoneNumberId { get; set; }
        public string? WabaId { get; set; }
        public string? DisplayPhone { get; set; }
        public byte[]? AccessTokenEnc { get; set; }
        public byte[]? AppSecretEnc { get; set; }
        public string? VerifyToken { get; set; }
        public int DailyCap { get; set; }
        public int StaleHours { get; set; }
        public DateTime? WebhookSeenAt { get; set; }
        public int Version { get; set; }
    }

    public const string SettingsSelect = """
        SELECT business_id AS BusinessId, enabled, provider, phone_number_id AS PhoneNumberId, waba_id AS WabaId, display_phone AS DisplayPhone,
               access_token_enc AS AccessTokenEnc, app_secret_enc AS AppSecretEnc, verify_token AS VerifyToken, daily_cap AS DailyCap,
               stale_hours AS StaleHours, webhook_seen_at AS WebhookSeenAt, version
          FROM notif.settings
        """;

    public static Task<GatewaySettings?> SettingsAsync(DbTx t) => t.QuerySingleOrDefaultAsync<GatewaySettings>(SettingsSelect + " LIMIT 1");

    public string? Secret(byte[]? enc) => enc is null ? null : Encoding.UTF8.GetString(secrets.Unprotect(enc));

    public IWhatsAppProvider ProviderFor(GatewaySettings s) => s.Provider == "meta"
        ? new MetaCloudProvider(http.CreateClient("whatsapp"), opts.WhatsApp.GraphBase, s.PhoneNumberId!, Secret(s.AccessTokenEnc)!)
        : new TestChatProvider(db);

    /// <summary>Takes messages into the gateway queue (once each); a newer consent with a message lifts an earlier STOP.</summary>
    public static async Task<int> AcceptAsync(DbTx t, Guid originServer, IReadOnlyList<RelayedMessage> messages)
    {
        var n = 0;
        foreach (var m in messages)
        {
            if (m.Template is null == (m.BodyText is null)) continue;
            n += await t.ExecuteAsync("""
                INSERT INTO notif.gateway_message (id, origin_server_id, business_id, branch_id, phone_e164, lang, template, params, body_text, opt_in_at, expires_at)
                VALUES (@Id, @originServer, @BusinessId, @BranchId, @Phone, @Lang, @Template, @Params::jsonb, @BodyText, @OptInAt, @ExpiresAt)
                ON CONFLICT (id) DO NOTHING
                """, new { m.Id, originServer, m.BusinessId, m.BranchId, m.Phone, m.Lang, m.Template, m.Params, m.BodyText, m.OptInAt, m.ExpiresAt });
            if (m.OptInAt is { } consent)
                await t.ExecuteAsync("DELETE FROM notif.suppression WHERE phone_e164 = @Phone AND since < @consent", new { m.Phone, consent });
        }
        return n;
    }

    public static async Task<List<GatewayEventDto>> EventsAsync(DbTx t, Guid originServer, long after) =>
        (await t.QueryAsync<GatewayEventDto>("""
            SELECT seq, message_id AS MessageId, kind, status, reason, phone_e164 AS Phone, body, wamid, at FROM notif.gateway_event
             WHERE origin_server_id = @originServer AND seq > @after ORDER BY seq LIMIT 500
            """, new { originServer, after })).ToList();

    private sealed class Queued
    {
        public Guid Id { get; set; }
        public Guid OriginServerId { get; set; }
        public Guid BusinessId { get; set; }
        public string Phone { get; set; } = "";
        public string Lang { get; set; } = "en";
        public string? Template { get; set; }
        public string Params { get; set; } = "[]";
        public string? BodyText { get; set; }
        public DateTime? OptInAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public DateTime ReceivedAt { get; set; }
        public int Attempts { get; set; }
    }

    /// <summary>Sends what is due. Returns how many were sent. Safe to run on several threads: each message is leased before it is sent.</summary>
    public async Task<int> SendDueAsync(CancellationToken ct)
    {
        if (!IsGateway) return 0;
        var (settings, batch) = await db.InTransactionAsync(AccessScope.System, ActorContext.System, async t =>
        {
            var s = await SettingsAsync(t);
            var rows = (await t.QueryAsync<Queued>("""
                UPDATE notif.gateway_message g SET next_attempt_at = now() + interval '2 minutes'
                 WHERE g.id IN (SELECT id FROM notif.gateway_message WHERE status = 'queued' AND next_attempt_at <= now()
                                 ORDER BY received_at LIMIT 25 FOR UPDATE SKIP LOCKED)
                RETURNING g.id, g.origin_server_id AS OriginServerId, g.business_id AS BusinessId, g.phone_e164 AS Phone, g.lang, g.template,
                          g.params::text AS Params, g.body_text AS BodyText, g.opt_in_at AS OptInAt, g.expires_at AS ExpiresAt, g.received_at AS ReceivedAt, g.attempts
                """)).ToList();
            return (s, rows);
        }, ct);
        if (batch.Count == 0 || settings is null) return 0;
        var sent = 0;
        IWhatsAppProvider? provider = null;
        foreach (var m in batch.OrderBy(x => x.ReceivedAt))
        {
            var verdict = await db.InTransactionAsync(AccessScope.System, ActorContext.System, async t =>
            {
                var now = t.Clock.UtcNow.UtcDateTime;
                if (!settings.Enabled) return ("blocked", "WhatsApp is switched off in KITE.");
                if (settings.Provider == "meta" && (settings.PhoneNumberId is null || settings.AccessTokenEnc is null)) return ("blocked", "WhatsApp is not connected yet.");
                if (m.ExpiresAt < now || m.ReceivedAt.AddHours(settings.StaleHours) < now) return ("dropped", "Too old to send.");
                if (await t.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM notif.suppression WHERE phone_e164 = @Phone)", new { m.Phone }))
                    return ("blocked", "The customer sent STOP.");
                if (m.Template is not null && m.OptInAt is null) return ("blocked", "The customer did not agree to WhatsApp messages.");
                if (m.Template is null && !await t.ExecuteScalarAsync<bool>("""
                        SELECT EXISTS (SELECT 1 FROM notif.gateway_event WHERE phone_e164 = @Phone AND kind IN ('text','button','start') AND at > now() - interval '24 hours')
                        """, new { m.Phone }))
                    return ("blocked", "Outside the 24-hour window for typed replies.");
                var tz = await t.ExecuteScalarAsync<string>("SELECT time_zone FROM core.business LIMIT 1") ?? "Asia/Muscat";
                var dayStart = TimeZoneInfo.ConvertTimeToUtc(BusinessDay.Local(DateTimeOffset.UtcNow, tz).Date, BusinessDay.Zone(tz));
                var today = await t.ExecuteScalarAsync<int>("SELECT count(*)::int FROM notif.gateway_message WHERE sent_at >= @dayStart", new { dayStart });
                if (today >= settings.DailyCap) return ("cap", "");
                return ("send", "");
            }, ct);
            if (verdict.Item1 is "blocked" or "dropped")
            {
                await FinishAsync(m.Id, m.OriginServerId, verdict.Item1, verdict.Item2, null, false, ct);
                continue;
            }
            if (verdict.Item1 == "cap")
            {
                await db.InTransactionAsync(AccessScope.System, ActorContext.System,
                    t => t.ExecuteAsync("UPDATE notif.gateway_message SET next_attempt_at = now() + interval '15 minutes', status_reason = 'Daily limit reached; waiting.' WHERE id = @Id", new { m.Id }), ct);
                continue;
            }
            var send = await PrepareAsync(m, ct);
            if (send is null)
            {
                await FinishAsync(m.Id, m.OriginServerId, "failed", "No WhatsApp template for that message.", null, false, ct);
                continue;
            }
            provider ??= ProviderFor(settings);
            var r = await provider.SendAsync(send, ct);
            if (r.Ok)
            {
                await FinishAsync(m.Id, m.OriginServerId, "sent", null, r.Wamid, false, ct);
                sent++;
                // The test chat has no phone to report back; it shows the message as delivered at once.
                if (settings.Provider == "test")
                    await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => ApplyStatusAsync(t, r.Wamid!, "delivered", null), ct);
            }
            else if (r.Retry && m.Attempts < 4)
            {
                await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => t.ExecuteAsync("""
                    UPDATE notif.gateway_message SET attempts = attempts + 1, status_reason = @Error,
                           next_attempt_at = now() + make_interval(secs => 30 * power(2, attempts)::int) WHERE id = @Id
                    """, new { r.Error, m.Id }), ct);
            }
            else await FinishAsync(m.Id, m.OriginServerId, "failed", r.Error, null, true, ct);
        }
        return sent;
    }

    private async Task<WaSend?> PrepareAsync(Queued m, CancellationToken ct)
    {
        var args = JsonSerializer.Deserialize<List<string>>(m.Params) ?? [];
        if (m.Template is null) return new WaSend(m.Id, m.Phone, null, null, [], [], m.BodyText, m.BodyText!);
        var tpl = await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => t.QuerySingleOrDefaultAsync<(string MetaName, string MetaLang, string Body, string[] Buttons)>("""
            SELECT meta_name, meta_lang, body, buttons FROM notif.template WHERE code = @Template ORDER BY (lang = @Lang) DESC LIMIT 1
            """, new { m.Template, m.Lang }), ct);
        if (tpl.MetaName is null) return null;
        var rendered = tpl.Body;
        for (var i = 0; i < args.Count; i++) rendered = rendered.Replace("{{" + (i + 1).ToString(CultureInfo.InvariantCulture) + "}}", args[i], StringComparison.Ordinal);
        return new WaSend(m.Id, m.Phone, tpl.MetaName, tpl.MetaLang, args, tpl.Buttons, null, rendered);
    }

    private Task FinishAsync(Guid id, Guid origin, string status, string? reason, string? wamid, bool countAttempt, CancellationToken ct) =>
        db.InTransactionAsync(AccessScope.System, ActorContext.System, async t =>
        {
            await t.ExecuteAsync("""
                UPDATE notif.gateway_message SET status = @status, status_reason = @reason, wamid = coalesce(@wamid, wamid), status_at = now(),
                       sent_at = CASE WHEN @status = 'sent' THEN now() ELSE sent_at END, attempts = attempts + CASE WHEN @countAttempt THEN 1 ELSE 0 END
                 WHERE id = @id
                """, new { status, reason, wamid, countAttempt, id });
            await t.ExecuteAsync("""
                INSERT INTO notif.gateway_event (origin_server_id, message_id, kind, status, reason, wamid) VALUES (@origin, @id, 'status', @status, @reason, @wamid)
                """, new { origin, id, status, reason, wamid });
            return true;
        }, ct);

    private static readonly string[] Order = ["queued", "sent", "delivered", "read"];

    /// <summary>A delivery status from WhatsApp: it only moves forward (a late "delivered" never undoes "read"); a failure is final.</summary>
    public static async Task ApplyStatusAsync(DbTx t, string wamid, string status, string? reason)
    {
        if (status is not ("sent" or "delivered" or "read" or "failed")) return;
        var m = await t.QuerySingleOrDefaultAsync<(Guid Id, Guid Origin, string Status)>(
            "SELECT id, origin_server_id, status FROM notif.gateway_message WHERE wamid = @wamid FOR UPDATE", new { wamid });
        if (m.Id == Guid.Empty) return;
        var forward = status == "failed" ? m.Status is "queued" or "sent" : Array.IndexOf(Order, status) > Array.IndexOf(Order, m.Status);
        if (!forward) return;
        await t.ExecuteAsync("UPDATE notif.gateway_message SET status = @status, status_reason = @reason, status_at = now() WHERE id = @Id", new { status, reason, m.Id });
        await t.ExecuteAsync("""
            INSERT INTO notif.gateway_event (origin_server_id, message_id, kind, status, reason, wamid) VALUES (@Origin, @Id, 'status', @status, @reason, @wamid)
            """, new { m.Origin, m.Id, status, reason, wamid });
    }

    /// <summary>Checks Meta's signature on a webhook delivery: HMAC-SHA256 of the body with the app secret.</summary>
    public static bool SignatureOk(byte[] body, string? header, string? appSecret)
    {
        if (string.IsNullOrEmpty(appSecret) || string.IsNullOrEmpty(header) || !header.StartsWith("sha256=", StringComparison.Ordinal)) return false;
        byte[] given;
        try { given = Convert.FromHexString(header[7..]); }
        catch (FormatException) { return false; }
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), body);
        return CryptographicOperations.FixedTimeEquals(given, expected);
    }

    private static readonly string[] StopWords = ["STOP", "UNSUBSCRIBE", "STOP ALL", "إيقاف", "ايقاف", "الغاء", "إلغاء", "توقف"];
    private static readonly string[] StartWords = ["START", "ابدأ", "ابدا"];

    /// <summary>
    /// One webhook delivery from WhatsApp (statuses and messages). Each status and message is handled once (Meta retries). A reply is
    /// routed to the server whose message the customer last received; a button tap is checked against the message it answers.
    /// </summary>
    public static async Task<int> ProcessWebhookAsync(DbTx t, JsonElement body)
    {
        var handled = 0;
        if (!body.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array) return 0;
        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("changes", out var changes)) continue;
            foreach (var change in changes.EnumerateArray())
            {
                if (!change.TryGetProperty("value", out var value)) continue;
                if (value.TryGetProperty("statuses", out var statuses))
                    foreach (var st in statuses.EnumerateArray())
                    {
                        var wamid = Str(st, "id");
                        var status = Str(st, "status");
                        if (wamid is null || status is null || !await SeenOnceAsync(t, $"st:{wamid}:{status}")) continue;
                        string? reason = null;
                        if (st.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array && errs.GetArrayLength() > 0)
                        {
                            var e = errs[0];
                            reason = $"{(e.TryGetProperty("code", out var c) ? c.ToString() : "")} {Str(e, "title") ?? Str(e, "message")}".Trim();
                        }
                        await ApplyStatusAsync(t, wamid, status, reason);
                        handled++;
                    }
                if (value.TryGetProperty("messages", out var messages))
                    foreach (var msg in messages.EnumerateArray())
                    {
                        var wamid = Str(msg, "id");
                        var from = Str(msg, "from");
                        if (wamid is null || from is null || !await SeenOnceAsync(t, "msg:" + wamid)) continue;
                        var phone = "+" + new string(from.Where(char.IsAsciiDigit).ToArray());
                        var type = Str(msg, "type");
                        string kind; string? text = null; Guid? answered = null;
                        if (type == "button" && msg.TryGetProperty("button", out var b))
                        {
                            (kind, text, answered) = ("button", ButtonOf(Str(b, "payload")), MessageOf(Str(b, "payload")));
                        }
                        else if (type == "interactive" && msg.TryGetProperty("interactive", out var ia) && ia.TryGetProperty("button_reply", out var br))
                        {
                            (kind, text, answered) = ("button", ButtonOf(Str(br, "id")), MessageOf(Str(br, "id")));
                        }
                        else
                        {
                            text = type == "text" && msg.TryGetProperty("text", out var tx) ? Str(tx, "body") : $"({type ?? "message"})";
                            var word = (text ?? "").Trim().ToUpperInvariant();
                            kind = StopWords.Contains(word) ? "stop" : StartWords.Contains(word) ? "start" : "text";
                        }
                        // The message it answers: the one the button names (it must have gone to this phone and be the one replied to), or the
                        // latest one sent to this phone.
                        (Guid Id, Guid Origin, string Phone, string? Wamid) target = default;
                        if (answered is { } aid)
                        {
                            target = await t.QuerySingleOrDefaultAsync<(Guid, Guid, string, string?)>(
                                "SELECT id, origin_server_id, phone_e164, wamid FROM notif.gateway_message WHERE id = @aid", new { aid });
                            var context = msg.TryGetProperty("context", out var ctx) ? Str(ctx, "id") : null;
                            if (target.Id == Guid.Empty || target.Phone != phone || (context is not null && target.Wamid is not null && context != target.Wamid))
                            { kind = "text"; target = default; }
                        }
                        if (target.Id == Guid.Empty)
                            target = await t.QuerySingleOrDefaultAsync<(Guid, Guid, string, string?)>("""
                                SELECT id, origin_server_id, phone_e164, wamid FROM notif.gateway_message WHERE phone_e164 = @phone AND status <> 'queued'
                                 ORDER BY coalesce(sent_at, received_at) DESC LIMIT 1
                                """, new { phone });
                        if (kind == "stop")
                            await t.ExecuteAsync("""
                                INSERT INTO notif.suppression (phone_e164, since, source) VALUES (@phone, now(), 'customer') ON CONFLICT (phone_e164) DO UPDATE SET since = now()
                                """, new { phone });
                        if (kind == "start") await t.ExecuteAsync("DELETE FROM notif.suppression WHERE phone_e164 = @phone", new { phone });
                        if (target.Id == Guid.Empty) continue;   // nothing of ours was sent to this number
                        await t.ExecuteAsync("""
                            INSERT INTO notif.gateway_event (origin_server_id, message_id, kind, phone_e164, body, wamid) VALUES (@Origin, @Id, @kind, @phone, @text, @wamid)
                            """, new { target.Origin, target.Id, kind, phone, text = text is { Length: > 1000 } ? text[..1000] : text, wamid });
                        handled++;
                    }
            }
        }
        if (handled > 0) await t.ExecuteAsync("UPDATE notif.settings SET webhook_seen_at = now()");
        return handled;
    }

    private static string? ButtonOf(string? payload) => payload?.Split(':')[0] is "approve" ? "approve" : payload?.Split(':')[0] is "call" ? "call" : null;
    private static Guid? MessageOf(string? payload) => payload is not null && payload.Split(':') is [_, var g] && Guid.TryParseExact(g, "N", out var id) ? id : null;

    private static async Task<bool> SeenOnceAsync(DbTx t, string key) =>
        await t.ExecuteAsync("INSERT INTO notif.webhook_seen (key) VALUES (@key) ON CONFLICT DO NOTHING", new { key }) == 1;

    private static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>
/// Every server's side (5.3): hands its queued messages to the main server's gateway (its own gateway on a main or combined server)
/// and applies the main server's events in order: delivery statuses, replies, button taps (an Approve becomes the estimate's approval
/// with the message ID) and STOP (the customer's consent is withdrawn).
/// </summary>
public sealed class NotifRelay(KiteDb db, ServerContext server, BranchLinkService link, ILogger<NotifRelay> log)
{
    public async Task<(int Pushed, int Applied)> RunOnceAsync(CancellationToken ct)
    {
        if (!server.IsConfigured) return (0, 0);
        var self = server.ServerId!.Value;
        var pushed = 0;
        for (var round = 0; round < 10; round++)
        {
            var batch = await db.InTransactionAsync(AccessScope.System, ActorContext.System, async t => (await t.QueryAsync<RelayedMessage>("""
                SELECT id, business_id AS BusinessId, branch_id AS BranchId, phone_e164 AS Phone, lang, template, params::text AS Params, body_text AS BodyText,
                       opt_in_at AS OptInAt, expires_at AS ExpiresAt
                  FROM notif.outbound_message WHERE status = 'queued' ORDER BY created_at LIMIT 100
                """)).ToList(), ct);
            if (batch.Count == 0) break;
            var ok = server.Role == "branch" ? await PushAsync(batch, ct)
                : await db.InTransactionAsync(AccessScope.System, ActorContext.System, async t => { await WhatsAppGateway.AcceptAsync(t, self, batch); return true; }, ct);
            await db.InTransactionAsync(AccessScope.System, ActorContext.System, async t =>
            {
                if (ok)
                    await t.ExecuteAsync("""
                        UPDATE notif.outbound_message SET status = 'relayed', relayed_at = now(), status_at = now() WHERE id = ANY(@ids) AND status = 'queued'
                        """, new { ids = batch.Select(b => b.Id).ToArray() });
                await t.ExecuteAsync("UPDATE notif.relay_state SET last_push_at = now(), last_ok_at = CASE WHEN @ok THEN now() ELSE last_ok_at END", new { ok });
                return true;
            }, ct);
            if (!ok) break;
            pushed += batch.Count;
        }

        var applied = 0;
        for (var round = 0; round < 10; round++)
        {
            var after = await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => t.ExecuteScalarAsync<long>("SELECT events_seq FROM notif.relay_state"), ct);
            var events = server.Role == "branch" ? await PullAsync(after, ct)
                : await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => WhatsAppGateway.EventsAsync(t, self, after), ct);
            if (events is null || events.Count == 0) break;
            foreach (var e in events)
            {
                await PosEndpoints.WithRetryAsync(() => db.InTransactionAsync(AccessScope.System, ActorContext.System, async t =>
                {
                    var current = await t.ExecuteScalarAsync<long>("SELECT events_seq FROM notif.relay_state FOR UPDATE");
                    if (current >= e.Seq) return false;   // another worker applied it
                    await ApplyAsync(t, e);
                    await t.ExecuteAsync("UPDATE notif.relay_state SET events_seq = @Seq, last_pull_at = now(), last_error = NULL", new { e.Seq });
                    return true;
                }, ct));
                applied++;
            }
        }
        return (pushed, applied);
    }

    private async Task<bool> PushAsync(List<RelayedMessage> batch, CancellationToken ct)
    {
        var c = await link.MainClientAsync(TimeSpan.FromSeconds(15), ct);
        if (c is null) return false;
        using var http = c.Value.Http;
        try
        {
            var r = await http.PostAsJsonAsync(c.Value.SyncUrl + "/sync/v1/notif/push", new NotifPushRequest(batch), ct);
            if (r.IsSuccessStatusCode) return true;
            await NoteErrorAsync($"head office answered {(int)r.StatusCode}", ct);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException || (e is TaskCanceledException && !ct.IsCancellationRequested)) { await NoteErrorAsync(e.Message, ct); }
        return false;
    }

    private async Task<List<GatewayEventDto>?> PullAsync(long after, CancellationToken ct)
    {
        var c = await link.MainClientAsync(TimeSpan.FromSeconds(15), ct);
        if (c is null) return null;
        using var http = c.Value.Http;
        try
        {
            var r = await http.PostAsJsonAsync(c.Value.SyncUrl + "/sync/v1/notif/events", new NotifEventsRequest(after), ct);
            if (r.IsSuccessStatusCode) return (await r.Content.ReadFromJsonAsync<NotifEventsResponse>(ct))?.Events;
            await NoteErrorAsync($"head office answered {(int)r.StatusCode}", ct);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException || (e is TaskCanceledException && !ct.IsCancellationRequested)) { await NoteErrorAsync(e.Message, ct); }
        return null;
    }

    private Task NoteErrorAsync(string error, CancellationToken ct)
    {
        log.LogInformation("WhatsApp relay: {Error}", error);
        return db.InTransactionAsync(AccessScope.System, ActorContext.System,
            t => t.ExecuteAsync("UPDATE notif.relay_state SET last_error = @error", new { error = error.Length > 300 ? error[..300] : error }), ct);
    }

    private static readonly string[] Rank = ["queued", "relayed", "sent", "delivered", "read"];

    /// <summary>Applies one event from the gateway to this server's records.</summary>
    public static async Task ApplyAsync(DbTx t, GatewayEventDto e)
    {
        var m = e.MessageId is { } mid ? await t.QuerySingleOrDefaultAsync<OutboundRef>("""
            SELECT o.id, o.business_id AS BusinessId, o.branch_id AS BranchId, o.customer_id AS CustomerId, o.phone_e164 AS Phone, o.status,
                   o.source_doc_type AS SourceType, o.source_doc_id AS SourceId
              FROM notif.outbound_message o WHERE o.id = @mid
            """, new { mid }) : null;
        if (m is null) return;
        if (e.Kind == "status")
        {
            var status = e.Status ?? "";
            var forward = status is "failed" or "dropped" or "blocked" ? m.Status is "queued" or "relayed" or "sent"
                : Array.IndexOf(Rank, status) > Array.IndexOf(Rank, m.Status);
            if (!forward) return;
            await t.ExecuteAsync("""
                UPDATE notif.outbound_message SET status = @status, status_reason = @Reason, wamid = coalesce(@Wamid, wamid), status_at = @At WHERE id = @Id
                """, new { status, e.Reason, e.Wamid, e.At, m.Id });
            return;
        }
        string? handled = null;
        var phone = e.Phone ?? m.Phone;
        var customer = m.CustomerId;
        if (e.Kind == "button" && e.Body == "approve") handled = await ApproveFromWhatsAppAsync(t, m, e);
        else if (e.Kind == "button" && e.Body == "call") handled = "call requested";
        else if (e.Kind is "stop" or "start" && customer is { } c)
        {
            var optIn = e.Kind == "start";
            var changed = await t.ExecuteAsync("""
                UPDATE crm.customer SET whatsapp_opt_in = @optIn, whatsapp_opt_in_at = CASE WHEN @optIn THEN now() ELSE whatsapp_opt_in_at END
                 WHERE id = @c AND phone_e164 = @phone AND whatsapp_opt_in <> @optIn
                """, new { optIn, c, phone });
            if (changed > 0)
                await t.ExecuteAsync("""
                    INSERT INTO notif.consent (id, business_id, branch_id, customer_id, phone_e164, action, source, note, at)
                    VALUES (gen_random_uuid(), @BusinessId, @BranchId, @c, @phone, @action, 'customer_reply', @Body, @At)
                    """, new { m.BusinessId, m.BranchId, c, phone, action = optIn ? "opt_in" : "opt_out", e.Body, e.At });
            handled = optIn ? "opted in" : "opted out";
        }
        await t.ExecuteAsync("""
            INSERT INTO notif.inbound_message (id, business_id, branch_id, customer_id, phone_e164, kind, body, reply_to_id, wamid, received_at, handled)
            VALUES (gen_random_uuid(), @BusinessId, @BranchId, @CustomerId, @phone, @Kind, @Body, @Id, @wamid, @At, @handled)
            ON CONFLICT (wamid) DO NOTHING
            """, new { m.BusinessId, m.BranchId, m.CustomerId, phone, e.Kind, e.Body, m.Id, wamid = e.Wamid ?? $"event:{e.Seq}", e.At, handled });
    }

    /// <summary>The customer tapped Approve on the estimate message (13.3): recorded with the message ID, if it is still the current estimate.</summary>
    private static async Task<string> ApproveFromWhatsAppAsync(DbTx t, OutboundRef m, GatewayEventDto e)
    {
        if (m.SourceType != "repair_estimate" || m.SourceId is not { } estimateId) return "approval ignored: not an estimate";
        var jobId = await t.ExecuteScalarAsync<Guid?>("SELECT job_id FROM repair.estimate WHERE id = @estimateId", new { estimateId });
        if (jobId is null) return "approval ignored: estimate not found";
        var j = await RepairCore.LoadAsync(t, jobId.Value, forUpdate: true);
        var current = await RepairCore.CurrentEstimateAsync(t, j.Id);
        if (current is null || current.Id != estimateId) return "approval ignored: a newer estimate was written";
        if (current.Status == "approved") return "already approved";
        if (current.Status is not ("sent" or "draft")) return $"approval ignored: the estimate is {current.Status}";
        if (j.Ended) return "approval ignored: the job has ended";
        await RepairCore.ApproveAsync(t, j, current, "whatsapp", j.CustomerName, e.Wamid ?? $"event:{e.Seq}", null, e.At);
        return $"approved estimate version {current.EstimateNo}";
    }

    private sealed class OutboundRef
    {
        public Guid Id { get; set; }
        public Guid BusinessId { get; set; }
        public Guid BranchId { get; set; }
        public Guid? CustomerId { get; set; }
        public string Phone { get; set; } = "";
        public string Status { get; set; } = "";
        public string? SourceType { get; set; }
        public Guid? SourceId { get; set; }
    }
}

/// <summary>
/// Runs the relay on every server and the gateway's sender on the main server; also the repair reminders for devices not collected
/// (13.6) and the deletion of passcodes kept too long (13.1).
/// </summary>
public sealed class WhatsAppWorker(NotifRelay relay, WhatsAppGateway gateway, RepairHousekeeping housekeeping, KiteOptions opts, ILogger<WhatsAppWorker> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!opts.WhatsApp.Background) return;
        var lastHousekeeping = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await relay.RunOnceAsync(stoppingToken);
                if (await gateway.SendDueAsync(stoppingToken) > 0) await relay.RunOnceAsync(stoppingToken);
                if (DateTime.UtcNow - lastHousekeeping > TimeSpan.FromMinutes(30))
                {
                    await housekeeping.RunOnceAsync(stoppingToken);
                    lastHousekeeping = DateTime.UtcNow;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "WhatsApp worker"); }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, opts.WhatsApp.IntervalSeconds)), stoppingToken); } catch (OperationCanceledException) { }
        }
    }
}

/// <summary>Reminders for devices ready but not collected at the configured ages (13.6; disposal is never automatic), and passcodes
/// deleted after the configured number of days (13.1).</summary>
public sealed class RepairHousekeeping(KiteDb db, ServerContext server)
{
    public async Task<(int Reminded, int Purged)> RunOnceAsync(CancellationToken ct)
    {
        if (!server.IsConfigured) return (0, 0);
        return await db.InTransactionAsync(AccessScope.System, ActorContext.System, async t =>
        {
            var settings = await t.QuerySingleOrDefaultAsync<RepairSettings>("""
                SELECT passcode_keep_days AS PasscodeKeepDays, uncollected_days AS UncollectedDays FROM repair.settings LIMIT 1
                """);
            if (settings is null) return (0, 0);
            var purged = await t.ExecuteAsync("""
                UPDATE repair.repair_job SET passcode_enc = NULL, passcode_purged_at = now()
                 WHERE passcode_enc IS NOT NULL AND created_at < now() - make_interval(days => @days)
                """, new { days = settings.PasscodeKeepDays });
            var reminded = 0;
            var due = (await t.QueryAsync<(Guid Id, int Days, int Reminded)>("""
                SELECT j.id, floor(extract(epoch FROM now() - h.at) / 86400)::int, j.reminded_days
                  FROM repair.repair_job j JOIN LATERAL (SELECT max(at) AS at FROM repair.status_history WHERE job_id = j.id AND to_status = 'ready') h ON true
                 WHERE j.status = 'ready'
                """)).ToList();
            foreach (var (id, days, already) in due)
            {
                var step = settings.UncollectedDays.Where(d => d <= days && d > already).DefaultIfEmpty(0).Max();
                if (step == 0) continue;
                var j = await RepairCore.LoadAsync(t, id, forUpdate: true);
                await t.ExecuteAsync("UPDATE repair.repair_job SET reminded_days = @step WHERE id = @id", new { step, id });
                await RepairCore.NotifyAsync(t, j, "repair_uncollected", [RepairCore.FirstName(j), j.Number, days.ToString(CultureInfo.InvariantCulture)],
                    "repair_job", j.Id, null);
                await t.AuditAsync(new AuditEntry("repair.uncollected_reminder", "repair_job", id, After: Json.Obj(new { j.Number, days, step }), BranchId: j.BranchId));
                reminded++;
            }
            return (reminded, purged);
        }, ct);
    }
}
