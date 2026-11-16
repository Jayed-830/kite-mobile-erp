using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kite.Host.Infrastructure;
using Kite.Identity;
using Kite.Persistence;
using Kite.Platform;

namespace Kite.Host.Features;

public sealed record WhatsAppSettingsRequest(bool Enabled, string Provider, string? PhoneNumberId, string? WabaId, string? DisplayPhone, string? AccessToken,
    string? AppSecret, int DailyCap, int StaleHours, int Version);
public sealed record WhatsAppTemplateRequest(string MetaName, string MetaLang, string Body, string[]? Buttons, int Version);
public sealed record TestChatRequest(string Phone, string Action, string? Text = null, int? Button = null, string? Wamid = null);

/// <summary>
/// The WhatsApp gateway's screens and doors (5.3): its settings on the main server (the token and app secret are write-only), the
/// templates, the test chat, the webhook Meta calls (through the Nextdab internet address), and the sync-port doors through which
/// branch servers hand over their messages and collect what happened to them.
/// </summary>
public static class WhatsAppEndpoints
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/whatsapp");
        var sync = app.MapGroup("/sync/v1").AddEndpointFilter(async (ctx, next) =>
        {
            if (!ctx.HttpContext.Request.IsHttps)
                throw new KiteException(ErrorCodes.NotFound, "Sync requests are accepted only on the TLS sync port.", 404);
            return await next(ctx);
        });

        g.MapGet("/settings", async (HttpContext http, KiteDb db, ServerContext server, WebAddressService web, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(AccessScope.System, http.ActorOf(), async t =>
            {
                var s = await WhatsAppGateway.SettingsAsync(t);
                var w = await WebAddressService.LoadAsync(t);
                var (state, error, since) = web.Status;
                var counts = await t.RowsAsync("""
                    SELECT status, count(*) AS n FROM notif.gateway_message WHERE received_at > now() - interval '7 days' GROUP BY status ORDER BY status
                    """);
                var today = await t.ExecuteScalarAsync<long>("SELECT count(*) FROM notif.gateway_message WHERE sent_at > date_trunc('day', now())");
                var relay = await t.QuerySingleOrDefaultAsync<(DateTime? LastOk, string? Error)>("SELECT last_ok_at, last_error FROM notif.relay_state");
                return new
                {
                    gateway = server.Role != "branch",
                    s?.Enabled, s?.Provider, s?.PhoneNumberId, s?.WabaId, s?.DisplayPhone, hasToken = s?.AccessTokenEnc is not null, hasSecret = s?.AppSecretEnc is not null,
                    s?.VerifyToken, s?.DailyCap, s?.StaleHours, s?.WebhookSeenAt, s?.Version,
                    webhookUrl = w?.Host is { } host && w.Active ? $"https://{host}/wa/webhook" : null,
                    webAddress = new { host = w?.Host, active = w?.Active ?? false, offReason = w?.OffReason, tunnel = w?.Active == true ? state : "off", error, since },
                    counts, sentToday = today, relay = new { relay.LastOk, relay.Error },
                };
            }, ct))).RequirePermission(Permissions.NotifSettings).RequireModule("notif");

        g.MapPut("/settings", async (WhatsAppSettingsRequest req, HttpContext http, KiteDb db, ServerContext server, ISecretProtector secrets, CancellationToken ct) =>
        {
            if (server.Role == "branch") throw new KiteException(ErrorCodes.WhatsAppState, "WhatsApp is set up on the main server.", 409);
            if (req.Provider is not ("meta" or "test")) throw new KiteException(ErrorCodes.Validation, "The provider is Meta Cloud API or the test chat.", 400, "provider");
            if (req.DailyCap is < 1 or > 100000) throw new KiteException(ErrorCodes.Validation, "The daily limit is 1 to 100000 messages.", 400, "dailyCap");
            if (req.StaleHours is < 1 or > 168) throw new KiteException(ErrorCodes.Validation, "Messages are dropped after 1 to 168 hours.", 400, "staleHours");
            string? Digits(string? v, string field)
            {
                if (string.IsNullOrWhiteSpace(v)) return null;
                var d = v.Trim();
                if (d.Length is < 5 or > 30 || !d.All(char.IsAsciiDigit)) throw new KiteException(ErrorCodes.Validation, "Type the number from WhatsApp Manager (digits only).", 400, field);
                return d;
            }
            var phoneId = Digits(req.PhoneNumberId, "phoneNumberId");
            var wabaId = Digits(req.WabaId, "wabaId");
            return Results.Ok(await db.InTransactionAsync(AccessScope.System, http.ActorOf(), async t =>
            {
                var before = await WhatsAppGateway.SettingsAsync(t) ?? throw new KiteException(ErrorCodes.Internal, "WhatsApp settings are missing.", 500);
                if (before.Version != req.Version) throw new KiteException(ErrorCodes.VersionMismatch, "Someone else changed the WhatsApp settings. Reload and try again.", 412);
                var token = string.IsNullOrWhiteSpace(req.AccessToken) ? before.AccessTokenEnc : secrets.Protect(Encoding.UTF8.GetBytes(req.AccessToken.Trim()));
                var secret = string.IsNullOrWhiteSpace(req.AppSecret) ? before.AppSecretEnc : secrets.Protect(Encoding.UTF8.GetBytes(req.AppSecret.Trim()));
                var verify = before.VerifyToken ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
                if (req.Enabled && req.Provider == "meta" && (phoneId is null || token is null || secret is null))
                    throw new KiteException(ErrorCodes.WhatsAppState, "To switch Meta on, give the phone number ID, the access token and the app secret.", 400);
                await t.ExecuteAsync("""
                    UPDATE notif.settings SET enabled = @Enabled, provider = @Provider, phone_number_id = @phoneId, waba_id = @wabaId, display_phone = @display,
                           access_token_enc = @token, app_secret_enc = @secret, verify_token = @verify, daily_cap = @DailyCap, stale_hours = @StaleHours, updated_by = @uid
                    """, new { req.Enabled, req.Provider, phoneId, wabaId, display = string.IsNullOrWhiteSpace(req.DisplayPhone) ? null : req.DisplayPhone.Trim(), token, secret,
                               verify, req.DailyCap, req.StaleHours, uid = http.User.UserIdOf() });
                // Never the token or the secret in the audit trail.
                await t.AuditAsync(new AuditEntry("whatsapp.settings_changed", "whatsapp", null, After: Json.Obj(new
                {
                    req.Enabled, req.Provider, phoneId, wabaId, req.DailyCap, req.StaleHours, tokenChanged = !string.IsNullOrWhiteSpace(req.AccessToken),
                    secretChanged = !string.IsNullOrWhiteSpace(req.AppSecret),
                })));
                return new { ok = true };
            }, ct));
        }).RequirePermission(Permissions.NotifSettings).RequireModule("notif");

        // Connect (as Kite REMS does): checks the token on the phone number, subscribes the app to the WhatsApp account, and points the
        // number's webhook at this server's Nextdab address with our verify token.
        g.MapPost("/connect", async (HttpContext http, KiteDb db, WhatsAppGateway gateway, KiteOptions opts, IHttpClientFactory factory, CancellationToken ct) =>
        {
            var (s, w) = await db.InTransactionAsync(AccessScope.System, http.ActorOf(), async t => (await WhatsAppGateway.SettingsAsync(t), await WebAddressService.LoadAsync(t)), ct);
            if (s is null || s.Provider != "meta" || s.PhoneNumberId is null || s.AccessTokenEnc is null)
                throw new KiteException(ErrorCodes.WhatsAppState, "Save the Meta phone number ID and access token first.", 409);
            var token = gateway.Secret(s.AccessTokenEnc)!;
            var steps = new List<object>();
            using var client = factory.CreateClient("whatsapp");
            async Task<(bool Ok, string Text)> Call(HttpMethod m, string path, object? body)
            {
                using var req = new HttpRequestMessage(m, $"{opts.WhatsApp.GraphBase.TrimEnd('/')}/{path}") { Content = body is null ? null : JsonContent.Create(body) };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                try
                {
                    using var r = await client.SendAsync(req, ct);
                    var text = await r.Content.ReadAsStringAsync(ct);
                    return (r.IsSuccessStatusCode, text.Length > 400 ? text[..400] : text);
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return (false, e.Message); }
            }
            var check = await Call(HttpMethod.Get, $"{s.PhoneNumberId}?fields=display_phone_number,verified_name", null);
            steps.Add(new { step = "token", check.Ok, detail = check.Text });
            string? display = null;
            if (check.Ok) try { display = JsonDocument.Parse(check.Text).RootElement.GetProperty("display_phone_number").GetString(); } catch (Exception) { }
            if (s.WabaId is not null)
            {
                var sub = await Call(HttpMethod.Post, $"{s.WabaId}/subscribed_apps", null);
                steps.Add(new { step = "subscribe", sub.Ok, detail = sub.Text });
            }
            if (w is { Active: true, Host: { } host })
            {
                var hook = await Call(HttpMethod.Post, s.PhoneNumberId, new { webhook_configuration = new { override_callback_uri = $"https://{host}/wa/webhook", verify_token = s.VerifyToken } });
                steps.Add(new { step = "webhook", hook.Ok, detail = hook.Text });
            }
            else steps.Add(new { step = "webhook", Ok = false, detail = "This server has no internet address from Nextdab yet: ask Nextdab to turn it on." });
            if (display is not null)
                await db.InTransactionAsync(AccessScope.System, http.ActorOf(), t => t.ExecuteAsync("UPDATE notif.settings SET display_phone = @display", new { display }), ct);
            return Results.Ok(new { steps, display });
        }).RequirePermission(Permissions.NotifSettings).RequireModule("notif");

        g.MapGet("/templates", async (HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(AccessScope.System, http.ActorOf(), t => t.RowsAsync("""
                SELECT id, code, lang, meta_name AS "metaName", meta_lang AS "metaLang", body, params, buttons, version FROM notif.template ORDER BY code, lang
                """), ct))).RequirePermission(Permissions.NotifSettings).RequireModule("notif");

        g.MapPut("/templates/{id:guid}", async (Guid id, WhatsAppTemplateRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(AccessScope.System, http.ActorOf(), async t =>
            {
                var n = await t.ExecuteAsync("""
                    UPDATE notif.template SET meta_name = @MetaName, meta_lang = @MetaLang, body = @Body, buttons = coalesce(@Buttons, buttons), updated_by = @uid
                     WHERE id = @id AND version = @Version
                    """, new { id, req.MetaName, req.MetaLang, req.Body, req.Buttons, req.Version, uid = http.User.UserIdOf() });
                if (n == 0) throw new KiteException(ErrorCodes.VersionMismatch, "Someone else changed this template. Reload and try again.", 412);
                return new { id };
            }, ct))).RequirePermission(Permissions.NotifSettings).RequireModule("notif");

        // The test chat: what the customer's phone shows, and the customer's side (reply, tap a button, STOP, read, a failure).
        g.MapGet("/test-chat", async (string? phone, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(AccessScope.System, http.ActorOf(), async t => new
            {
                phones = await t.RowsAsync("SELECT phone_e164 AS phone, max(at) AS last FROM notif.test_chat GROUP BY phone_e164 ORDER BY 2 DESC LIMIT 50"),
                messages = phone is null ? [] : await t.RowsAsync("""
                    SELECT c.id, c.direction, c.wamid, c.body, c.buttons, c.at, g.status
                      FROM notif.test_chat c LEFT JOIN notif.gateway_message g ON g.wamid = c.wamid WHERE c.phone_e164 = @phone ORDER BY c.id
                    """, new { phone }),
            }, ct))).RequirePermission(Permissions.NotifSettings).RequireModule("notif");

        g.MapPost("/test-chat", async (TestChatRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(AccessScope.System, http.ActorOf(), async t =>
            {
                var s = await WhatsAppGateway.SettingsAsync(t);
                if (s?.Provider != "test") throw new KiteException(ErrorCodes.WhatsAppState, "The test chat works while the provider is the test chat.", 409);
                var phone = req.Phone.Trim();
                var wamid = "wamid.test.in." + Guid.CreateVersion7().ToString("N");
                object payload;
                var last = await t.QuerySingleOrDefaultAsync<(string? Wamid, Guid Id)>("""
                    SELECT wamid, id FROM notif.gateway_message WHERE phone_e164 = @phone AND wamid IS NOT NULL ORDER BY sent_at DESC LIMIT 1
                    """, new { phone });
                var target = req.Wamid ?? last.Wamid;
                switch (req.Action)
                {
                    case "read" or "delivered" or "failed":
                        if (target is null) throw new KiteException(ErrorCodes.NotFound, "Nothing was sent to that number.", 404);
                        payload = Envelope(new { statuses = new object[] { new { id = target, status = req.Action, recipient_id = phone.TrimStart('+'),
                            errors = req.Action == "failed" ? new object[] { new { code = 131026, title = "Message undeliverable" } } : null } } });
                        break;
                    case "button":
                        // A button is tapped on the latest message that has buttons (an estimate), unless a message is named.
                        var msg = await t.QuerySingleOrDefaultAsync<(Guid Id, string? Wamid)>("""
                            SELECT g.id, g.wamid FROM notif.gateway_message g
                             WHERE (@named::text IS NOT NULL AND g.wamid = @named)
                                OR (@named::text IS NULL AND g.phone_e164 = @phone AND g.wamid IS NOT NULL
                                    AND EXISTS (SELECT 1 FROM notif.template tp WHERE tp.code = g.template AND cardinality(tp.buttons) > 0))
                             ORDER BY g.sent_at DESC LIMIT 1
                            """, new { named = req.Wamid, phone });
                        if (msg.Id == Guid.Empty) throw new KiteException(ErrorCodes.NotFound, "Nothing was sent to that number.", 404);
                        var b = req.Button ?? 0;
                        payload = Envelope(new { messages = new object[] { new { from = phone.TrimStart('+'), id = wamid, type = "button", context = new { id = msg.Wamid },
                            button = new { payload = (b == 0 ? "approve:" : "call:") + msg.Id.ToString("N"), text = b == 0 ? "Approve" : "Call me" } } } });
                        break;
                    case "text" or "stop" or "start":
                        var text = req.Action == "stop" ? "STOP" : req.Action == "start" ? "START" : (req.Text ?? "").Trim();
                        if (text.Length == 0) throw new KiteException(ErrorCodes.Validation, "Type the customer's message.", 400, "text");
                        payload = Envelope(new { messages = new object[] { new { from = phone.TrimStart('+'), id = wamid, type = "text", text = new { body = text } } } });
                        break;
                    default:
                        throw new KiteException(ErrorCodes.Validation, "The customer replies, taps a button, sends STOP or START, reads, or the message fails.", 400, "action");
                }
                var json = JsonSerializer.SerializeToElement(payload);
                if (req.Action is "button" or "text" or "stop" or "start")
                {
                    var shown = req.Action == "button" ? (req.Button ?? 0) == 0 ? "[Approve]" : "[Call me]" : req.Action == "stop" ? "STOP" : req.Action == "start" ? "START" : req.Text!.Trim();
                    await t.ExecuteAsync("INSERT INTO notif.test_chat (phone_e164, direction, wamid, body) VALUES (@phone, 'in', @wamid, @shown)", new { phone, wamid, shown });
                }
                return new { handled = await WhatsAppGateway.ProcessWebhookAsync(t, json) };
            }, ct))).RequirePermission(Permissions.NotifSettings).RequireModule("notif");

        // ---- the webhook (main server; reached through the Nextdab address) ----
        app.MapGet("/wa/ping", (ServerContext server) => server.IsConfigured && server.Role != "branch" ? Results.Text("ok") : Results.NotFound()).AllowEveryone();

        app.MapGet("/wa/webhook", async (HttpContext http, KiteDb db, ServerContext server, CancellationToken ct) =>
        {
            if (!server.IsConfigured || server.Role == "branch") return Results.NotFound();
            var q = http.Request.Query;
            var s = await db.InTransactionAsync(AccessScope.System, ActorContext.System, WhatsAppGateway.SettingsAsync, ct);
            var given = q["hub.verify_token"].ToString();
            if (q["hub.mode"] != "subscribe" || s?.VerifyToken is null
                || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(s.VerifyToken)))
                return Results.StatusCode(403);
            return Results.Text(q["hub.challenge"].ToString(), "text/plain");
        }).AllowEveryone();

        app.MapPost("/wa/webhook", async (HttpContext http, KiteDb db, ServerContext server, WhatsAppGateway gateway, ILoggerFactory logs, CancellationToken ct) =>
        {
            if (!server.IsConfigured || server.Role == "branch") return Results.NotFound();
            using var ms = new MemoryStream();
            await http.Request.Body.CopyToAsync(ms, ct);
            if (ms.Length > 1_000_000) return Results.StatusCode(413);
            var body = ms.ToArray();
            var s = await db.InTransactionAsync(AccessScope.System, ActorContext.System, WhatsAppGateway.SettingsAsync, ct);
            if (!WhatsAppGateway.SignatureOk(body, http.Request.Headers["X-Hub-Signature-256"].ToString(), gateway.Secret(s?.AppSecretEnc)))
            {
                logs.CreateLogger("Kite.WhatsApp").LogWarning("A WhatsApp webhook call had a wrong signature and was refused.");
                return Results.StatusCode(401);
            }
            JsonElement json;
            try { json = JsonDocument.Parse(body).RootElement.Clone(); }
            catch (JsonException) { return Results.BadRequest(); }
            await PosEndpoints.WithRetryAsync(() => db.InTransactionAsync(AccessScope.System, ActorContext.System, t => WhatsAppGateway.ProcessWebhookAsync(t, json), ct));
            return Results.Ok();
        }).AllowEveryone();

        // ---- the sync port: branch servers hand over messages and collect events (mutual TLS) ----
        sync.MapPost("/notif/push", async (NotifPushRequest req, HttpContext http, KiteDb db, ServerContext server, PkiService pki, CancellationToken ct) =>
        {
            var who = await AuthenticateAsync(http, server, pki, ct);
            if (req.Messages is null || req.Messages.Count > 500) throw new KiteException(ErrorCodes.Validation, "Send 1 to 500 messages.", 400);
            if (req.Messages.Any(m => m.BranchId != who.BranchId)) throw new KiteException(ErrorCodes.Forbidden, "A branch server sends its own branch's messages.", 403);
            var n = await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => WhatsAppGateway.AcceptAsync(t, who.ServerId, req.Messages), ct);
            return Results.Ok(new { accepted = n });
        }).AllowEveryone();

        sync.MapPost("/notif/events", async (NotifEventsRequest req, HttpContext http, KiteDb db, ServerContext server, PkiService pki, CancellationToken ct) =>
        {
            var who = await AuthenticateAsync(http, server, pki, ct);
            return Results.Ok(new NotifEventsResponse(await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => WhatsAppGateway.EventsAsync(t, who.ServerId, req.After), ct)));
        }).AllowEveryone();
    }

    private static object Envelope(object value) => new { @object = "whatsapp_business_account", entry = new[] { new { id = "test", changes = new[] { new { field = "messages", value } } } } };

    private static async Task<PkiService.BranchIdentity> AuthenticateAsync(HttpContext http, ServerContext server, PkiService pki, CancellationToken ct)
    {
        if (!server.IsConfigured || !server.IsHeadOffice) throw new KiteException(ErrorCodes.NotHeadOffice, "This is not a main server.", 404);
        return await pki.AuthenticateAsync(http.Connection.ClientCertificate ?? await http.Connection.GetClientCertificateAsync(ct), ct);
    }

    /// <summary>
    /// Through the tunnel (the loopback webhook port, or a request that came via Cloudflare) only the webhook answers; everything else is
    /// as if it did not exist (the Kite REMS rule).
    /// </summary>
    public static IApplicationBuilder UseTunnelGuard(this IApplicationBuilder app, int webhookPort) =>
        app.Use(async (ctx, next) =>
        {
            var viaTunnel = (webhookPort > 0 && ctx.Connection.LocalPort == webhookPort)
                || ctx.Request.Headers.ContainsKey("Cf-Ray") || ctx.Request.Headers.ContainsKey("Cf-Connecting-Ip");
            if (viaTunnel && !(ctx.Request.Path.Equals("/wa/webhook") || ctx.Request.Path.Equals("/wa/ping")))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await next();
        });
}
