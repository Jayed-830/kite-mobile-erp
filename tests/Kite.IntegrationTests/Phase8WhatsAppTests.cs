using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kite.Host.Features;
using Kite.Host.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Kite.IntegrationTests.PosKit;
using static Kite.IntegrationTests.RepairKit;

namespace Kite.IntegrationTests;

/// <summary>
/// Phase 8 WhatsApp gateway against the real server and PostgreSQL: consent at intake, the outbox relayed to the gateway and sent with
/// the test chat, delivery statuses back, the customer's Approve button becoming the estimate's approval with the message ID, STOP
/// withdrawing consent, the rules that block, drop or hold a message, typed replies only within 24 hours, Meta's webhook signature and
/// verification, the tunnel guard, a branch server's messages through the sync port, and the Nextdab "web" command.
/// </summary>
[Collection("server")]
public sealed class Phase8WhatsAppTests(KiteServerFixture fx)
{
    private readonly PosKit kit = new(fx);
    private NotifRelay Relay => fx.Factory.Services.GetRequiredService<NotifRelay>();
    private WhatsAppGateway Gateway => fx.Factory.Services.GetRequiredService<WhatsAppGateway>();

    private async Task CycleAsync()
    {
        await Relay.RunOnceAsync(CancellationToken.None);
        await Gateway.SendDueAsync(CancellationToken.None);
        await Relay.RunOnceAsync(CancellationToken.None);
    }

    private static async Task SettingsAsync(HttpClient owner, bool enabled = true, string provider = "test", int dailyCap = 250, int staleHours = 24,
        string? appSecret = null, string? phoneId = null, string? token = null)
    {
        var s = await Ok(await owner.GetAsync("/api/v1/whatsapp/settings"));
        await Ok(await owner.PutAsJsonAsync("/api/v1/whatsapp/settings", new
        {
            enabled, provider, phoneNumberId = phoneId ?? s.GetProperty("phoneNumberId").GetString(), wabaId = (string?)null, displayPhone = (string?)null,
            accessToken = token, appSecret, dailyCap, staleHours, version = s.GetProperty("version").GetInt32(),
        }));
    }

    private async Task<(HttpClient Owner, HttpClient Pos, Guid Customer, string Phone, Guid Category, Guid Labour)> ArrangeAsync()
    {
        var owner = await kit.OwnerAsync();
        await EnableAsync(owner, whatsapp: true);
        await SettingsAsync(owner);
        var term = await kit.TerminalAsync(owner);
        var pos = await kit.OwnerPosAsync(term);
        await OpenShiftAsync(pos, 100m);
        var customer = await TradeInKit.CustomerAsync(pos, "Huda " + Suffix());
        var phone = await fx.QueryDbAsync<string>($"SELECT phone_e164 FROM crm.customer WHERE id = '{customer}'");
        return (owner, pos, customer, phone, await CategoryAsync(owner), await ServiceAsync(owner));
    }

    private async Task<string> OutboundStatusAsync(Guid job, string template) =>
        await fx.QueryDbAsync<string>($"SELECT status FROM notif.outbound_message WHERE source_doc_id IN ('{job}', (SELECT id FROM repair.estimate WHERE job_id = '{job}' ORDER BY estimate_no DESC LIMIT 1)) AND template = '{template}' ORDER BY created_at DESC LIMIT 1");

    [PgFact]
    public async Task Messages_go_out_through_the_gateway_and_an_Approve_tap_approves_the_estimate()
    {
        var (owner, pos, customer, phone, category, labour) = await ArrangeAsync();
        using var _o = owner; using var _p = pos;

        // Consent at intake: recorded on the customer and in the consent log; the received message is queued.
        var id = Id(await IntakeAsync(pos, customer, category, preAuthorised: 5m, whatsapp: true));
        Assert.True(await fx.QueryDbAsync<bool>($"SELECT whatsapp_opt_in FROM crm.customer WHERE id = '{customer}'"));
        Assert.Equal("intake", await fx.QueryDbAsync<string>($"SELECT source FROM notif.consent WHERE customer_id = '{customer}' AND action = 'opt_in'"));
        Assert.Equal("queued", await OutboundStatusAsync(id, "repair_received"));
        await CycleAsync();
        Assert.Equal("delivered", await OutboundStatusAsync(id, "repair_received"));
        var chat = await Ok(await owner.GetAsync($"/api/v1/whatsapp/test-chat?phone={Uri.EscapeDataString(phone)}"));
        var first = chat.GetProperty("messages")[0].GetProperty("body").GetString()!;
        Assert.Contains("job number R-B01-", first.Replace("Job number", "job number"), StringComparison.Ordinal);
        Assert.DoesNotContain("{{", first, StringComparison.Ordinal);

        // The estimate above the pre-authorised amount goes out with Approve and Call me.
        await MoveAsync(pos, id, "inspection_pending", "under_diagnosis");
        var detail = await EstimateAsync(pos, id, Labour(labour, 25m));
        var estimate = CurrentEstimate(detail);
        await SendAsync(pos, id, estimate);
        await CycleAsync();
        chat = await Ok(await owner.GetAsync($"/api/v1/whatsapp/test-chat?phone={Uri.EscapeDataString(phone)}"));
        var last = chat.GetProperty("messages").EnumerateArray().Last();
        Assert.Equal(2, last.GetProperty("buttons").GetArrayLength());
        Assert.Contains("OMR 25.000", last.GetProperty("body").GetString(), StringComparison.Ordinal);

        // The customer reads it, then taps Approve: the main server checks the tap answers that message; the job's server records the
        // approval with the WhatsApp message ID. A second tap changes nothing.
        await Ok(await owner.PostAsJsonAsync("/api/v1/whatsapp/test-chat", new { phone, action = "read" }));
        await Ok(await owner.PostAsJsonAsync("/api/v1/whatsapp/test-chat", new { phone, action = "button", button = 0 }));
        await CycleAsync();
        detail = await DetailAsync(pos, id);
        var e = detail.GetProperty("estimates")[0];
        Assert.Equal("approved", e.GetProperty("status").GetString());
        Assert.Equal("whatsapp", e.GetProperty("approvalMethod").GetString());
        Assert.StartsWith("wamid.test.in.", e.GetProperty("approvalRef").GetString());
        Assert.Equal("read", await OutboundStatusAsync(id, "repair_estimate"));
        await Ok(await owner.PostAsJsonAsync("/api/v1/whatsapp/test-chat", new { phone, action = "button", button = 0 }));
        await CycleAsync();
        var messages = await Ok(await pos.GetAsync($"/api/v1/repairs/{id}/messages"));
        Assert.Contains(messages.GetProperty("messages").EnumerateArray(), m => m.GetProperty("direction").GetString() == "in"
            && m.GetProperty("handled").GetString() == "approved estimate version 1");
        Assert.Contains(messages.GetProperty("messages").EnumerateArray(), m => m.GetProperty("direction").GetString() == "in"
            && m.GetProperty("handled").GetString() == "already approved");
        await MoveAsync(pos, id, "in_progress", "testing", "completed", "ready");
        await CycleAsync();
        Assert.Equal("delivered", await OutboundStatusAsync(id, "repair_ready"));

        // Typed replies only within 24 hours of the customer's last message: the customer writes, staff can answer.
        await Ok(await owner.PostAsJsonAsync("/api/v1/whatsapp/test-chat", new { phone, action = "text", text = "What time do you close?" }));
        await CycleAsync();
        messages = await Ok(await pos.PostAsJsonAsync($"/api/v1/repairs/{id}/messages", new { text = "We are open until 10 pm." }));
        Assert.True(messages.GetProperty("canReply").GetBoolean());
        await CycleAsync();
        Assert.Equal("delivered", await fx.QueryDbAsync<string>($"SELECT status FROM notif.outbound_message WHERE customer_id = '{customer}' AND template IS NULL"));
    }

    [PgFact]
    public async Task STOP_withdraws_consent_and_the_gateway_blocks_drops_and_holds_messages_by_its_rules()
    {
        var (owner, pos, customer, phone, category, labour) = await ArrangeAsync();
        using var _o = owner; using var _p = pos;
        var id = Id(await IntakeAsync(pos, customer, category, whatsapp: true));
        await CycleAsync();

        // No typed reply before the customer has written.
        await Problem(await pos.PostAsJsonAsync($"/api/v1/repairs/{id}/messages", new { text = "Hello" }), HttpStatusCode.Conflict, "WA-002");

        // STOP: the number is suppressed at the gateway; the customer's consent is withdrawn and logged at the job's server.
        await Ok(await owner.PostAsJsonAsync("/api/v1/whatsapp/test-chat", new { phone, action = "stop" }));
        await CycleAsync();
        Assert.False(await fx.QueryDbAsync<bool>($"SELECT whatsapp_opt_in FROM crm.customer WHERE id = '{customer}'"));
        Assert.Equal("customer_reply", await fx.QueryDbAsync<string>($"SELECT source FROM notif.consent WHERE customer_id = '{customer}' AND action = 'opt_out'"));
        Assert.Equal(1L, await fx.QueryDbAsync<long>($"SELECT count(*) FROM notif.suppression WHERE phone_e164 = '{phone}'"));
        // Nothing more is queued for the customer; a message already queued at the gateway is blocked.
        await MoveAsync(pos, id, "cancelled", "ready");
        Assert.Equal(0L, await fx.QueryDbAsync<long>($"SELECT count(*) FROM notif.outbound_message WHERE source_doc_id = '{id}' AND template = 'repair_ready'"));
        await fx.QueryDbAsync<int>($"""
            INSERT INTO notif.gateway_message (id, origin_server_id, business_id, branch_id, phone_e164, lang, template, params, opt_in_at, expires_at)
            SELECT gen_random_uuid(), (SELECT id FROM core.server WHERE is_self), id, (SELECT id FROM core.branch LIMIT 1), '{phone}', 'en', 'repair_ready', '["a","b","c"]',
                   now() - interval '1 day', now() + interval '1 hour' FROM core.business RETURNING 1
            """);
        await Gateway.SendDueAsync(CancellationToken.None);
        Assert.Equal("The customer sent STOP.", await fx.QueryDbAsync<string>($"SELECT status_reason FROM notif.gateway_message WHERE phone_e164 = '{phone}' AND status = 'blocked'"));

        // Stale: dropped. Without consent: blocked. Over the daily limit: held for later.
        async Task<Guid> Queue(string p, string? optIn, string expires)
        {
            var mid = Guid.NewGuid();
            await fx.QueryDbAsync<int>($"""
                INSERT INTO notif.gateway_message (id, origin_server_id, business_id, branch_id, phone_e164, lang, template, params, opt_in_at, expires_at)
                SELECT '{mid}', (SELECT id FROM core.server WHERE is_self), id, (SELECT id FROM core.branch LIMIT 1), '{p}', 'en', 'repair_ready', '["a","b","c"]',
                       {optIn ?? "NULL"}, {expires} FROM core.business RETURNING 1
                """);
            return mid;
        }
        var stale = await Queue("+96890000001", "now()", "now() - interval '1 minute'");
        var noConsent = await Queue("+96890000002", null, "now() + interval '1 hour'");
        await Gateway.SendDueAsync(CancellationToken.None);
        Assert.Equal("dropped", await fx.QueryDbAsync<string>($"SELECT status FROM notif.gateway_message WHERE id = '{stale}'"));
        Assert.Equal("blocked", await fx.QueryDbAsync<string>($"SELECT status FROM notif.gateway_message WHERE id = '{noConsent}'"));
        var sentToday = await fx.QueryDbAsync<long>("""
            SELECT count(*) FROM notif.gateway_message
             WHERE sent_at >= (date_trunc('day', now() AT TIME ZONE (SELECT time_zone FROM core.business)) AT TIME ZONE (SELECT time_zone FROM core.business))
            """);
        await SettingsAsync(owner, dailyCap: (int)sentToday);
        var held = await Queue("+96890000003", "now()", "now() + interval '1 hour'");
        await Gateway.SendDueAsync(CancellationToken.None);
        Assert.Equal("queued", await fx.QueryDbAsync<string>($"SELECT status FROM notif.gateway_message WHERE id = '{held}'"));
        await SettingsAsync(owner, dailyCap: 250);
        await fx.QueryDbAsync<int>($"UPDATE notif.gateway_message SET next_attempt_at = now() WHERE id = '{held}' RETURNING 1");
        await Gateway.SendDueAsync(CancellationToken.None);
        Assert.Equal("delivered", await fx.QueryDbAsync<string>($"SELECT status FROM notif.gateway_message WHERE id = '{held}'"));

        // Switched off: messages are blocked with the reason.
        await SettingsAsync(owner, enabled: false);
        var off = await Queue("+96890000004", "now()", "now() + interval '1 hour'");
        await Gateway.SendDueAsync(CancellationToken.None);
        Assert.Equal("WhatsApp is switched off in KITE.", await fx.QueryDbAsync<string>($"SELECT status_reason FROM notif.gateway_message WHERE id = '{off}'"));
        await SettingsAsync(owner, enabled: true);
    }

    [PgFact]
    public async Task The_webhook_checks_Meta_signature_and_verification_and_only_it_is_reachable_through_the_tunnel()
    {
        using var owner = await kit.OwnerAsync();
        await EnableAsync(owner, whatsapp: true);
        await SettingsAsync(owner, appSecret: "test-app-secret-0123456789");
        var s = await Ok(await owner.GetAsync("/api/v1/whatsapp/settings"));
        var verify = s.GetProperty("verifyToken").GetString()!;
        Assert.True(s.GetProperty("hasSecret").GetBoolean());
        Assert.False(s.TryGetProperty("appSecret", out _));
        using var anon = fx.Client();

        // Verification (GET): the challenge comes back only with our verify token.
        var ok = await anon.GetAsync($"/wa/webhook?hub.mode=subscribe&hub.verify_token={verify}&hub.challenge=12345");
        Assert.Equal("12345", await ok.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await anon.GetAsync("/wa/webhook?hub.mode=subscribe&hub.verify_token=wrong&hub.challenge=1")).StatusCode);

        // Deliveries (POST): refused without Meta's signature; handled once with it.
        var body = Encoding.UTF8.GetBytes("""{"object":"whatsapp_business_account","entry":[{"id":"1","changes":[{"field":"messages","value":{"statuses":[{"id":"wamid.unknown","status":"delivered"}]}}]}]}""");
        HttpRequestMessage Post(string? sig)
        {
            var r = new HttpRequestMessage(HttpMethod.Post, "/wa/webhook") { Content = new ByteArrayContent(body) };
            r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            if (sig is not null) r.Headers.Add("X-Hub-Signature-256", sig);
            return r;
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.SendAsync(Post(null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.SendAsync(Post("sha256=" + new string('0', 64)))).StatusCode);
        var good = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("test-app-secret-0123456789"), body)).ToLowerInvariant();
        Assert.Equal(HttpStatusCode.OK, (await anon.SendAsync(Post(good))).StatusCode);
        Assert.Equal(1L, await fx.QueryDbAsync<long>("SELECT count(*) FROM notif.webhook_seen WHERE key = 'st:wamid.unknown:delivered'"));
        Assert.Equal(HttpStatusCode.OK, (await anon.SendAsync(Post(good))).StatusCode);   // Meta's retry: seen, not handled again

        // Through Cloudflare (the tunnel), only the webhook answers.
        var viaTunnel = new HttpRequestMessage(HttpMethod.Get, "/api/v1/setup/status");
        viaTunnel.Headers.Add("Cf-Ray", "8f2a-MCT");
        Assert.Equal(HttpStatusCode.NotFound, (await anon.SendAsync(viaTunnel)).StatusCode);
        var ping = new HttpRequestMessage(HttpMethod.Get, "/wa/ping");
        ping.Headers.Add("Cf-Connecting-Ip", "203.0.113.9");
        Assert.Equal("ok", await (await anon.SendAsync(ping)).Content.ReadAsStringAsync());
        var spa = new HttpRequestMessage(HttpMethod.Get, "/");
        spa.Headers.Add("Cf-Ray", "8f2a-MCT");
        Assert.Equal(HttpStatusCode.NotFound, (await anon.SendAsync(spa)).StatusCode);
    }

    [PgFact]
    public async Task A_branch_server_hands_its_messages_to_the_gateway_and_collects_their_events_through_the_sync_port()
    {
        using var owner = await kit.OwnerAsync();
        await EnableAsync(owner, whatsapp: true);
        await SettingsAsync(owner);
        var central = new CentralKit(fx);
        var b = await central.RegisterBranchAsync(owner);
        var other = await central.RegisterBranchAsync(owner);
        var biz = await fx.QueryDbAsync<Guid>("SELECT id FROM core.business");
        var mid = Guid.CreateVersion7();
        var phone = "+9689" + RandomNumberGenerator.GetInt32(1000000, 9999999);
        var msg = new RelayedMessage(mid, biz, b.BranchId, phone, "ar", "repair_ready", """["سالم","R-C-1","5.000 ر.ع."]""", null, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddHours(5));
        // A branch sends only its own branch's messages.
        var (bad, _) = await central.SyncAsync(other.Cert, "/sync/v1/notif/push", new NotifPushRequest([msg]));
        Assert.Equal(403, bad);
        var (st, body) = await central.SyncAsync(b.Cert, "/sync/v1/notif/push", new NotifPushRequest([msg]));
        Assert.Equal(200, st);
        Assert.Equal(1, body.GetProperty("accepted").GetInt32());
        Assert.Equal(0, (await central.SyncAsync(b.Cert, "/sync/v1/notif/push", new NotifPushRequest([msg]))).Body.GetProperty("accepted").GetInt32());   // once
        await Gateway.SendDueAsync(CancellationToken.None);
        await Ok(await owner.PostAsJsonAsync("/api/v1/whatsapp/test-chat", new { phone, action = "text", text = "شكرا" }));
        var (est, events) = await central.SyncAsync(b.Cert, "/sync/v1/notif/events", new NotifEventsRequest(0));
        Assert.Equal(200, est);
        var kinds = events.GetProperty("events").EnumerateArray().Select(x => x.GetProperty("kind").GetString() + ":" + (x.GetProperty("status").GetString() ?? "")).ToArray();
        Assert.Equal(["status:sent", "status:delivered", "text:"], kinds);
        Assert.Empty((await central.SyncAsync(other.Cert, "/sync/v1/notif/events", new NotifEventsRequest(0))).Body.GetProperty("events").EnumerateArray());
        var chat = await Ok(await owner.GetAsync($"/api/v1/whatsapp/test-chat?phone={Uri.EscapeDataString(phone)}"));
        Assert.Contains("جاهز للاستلام", chat.GetProperty("messages")[0].GetProperty("body").GetString(), StringComparison.Ordinal);
    }

    [PgFact]
    public async Task The_Nextdab_web_command_gives_the_main_server_its_address_and_keeps_the_token_only_encrypted()
    {
        using var owner = await kit.OwnerAsync();
        var licence = fx.Factory.Services.GetRequiredService<LicenceService>();
        var checkin = fx.Factory.Services.GetRequiredService<NextdabCheckinService>();
        var seq = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        const string token = "eyJhIjoiMTIzNDU2Nzg5MCIsInQiOiJ0dW5uZWwtaWQiLCJzIjoic2VjcmV0In0=";
        var sha = new string('a', 64);
        var web = fx.MakeCommand("web", new { host = "alnoor.nextdab.com", token, port = 8791,
            tool_url = "https://control.nextdab.com/dl/tools/cloudflared-windows-amd64.exe", tool_sha256 = sha, tool_size = 65_000_000 }, seq);
        var ack = await licence.HandleCommandAsync(web, CancellationToken.None);
        var onWindows = OperatingSystem.IsWindows();
        Assert.Equal(onWindows, ack!["ok"]!.GetValue<bool>());
        if (!onWindows)
        {
            // This server is not on Windows: a Windows cloudflared is refused with the reason (Nextdab Admin sends the one for the report's "os").
            Assert.Contains("Windows", ack["msg"]!.GetValue<string>(), StringComparison.Ordinal);
            web = fx.MakeCommand("web", new { host = "alnoor.nextdab.com", token, port = 8791,
                tool_url = "https://control.nextdab.com/dl/tools/cloudflared-linux-amd64", tool_sha256 = sha, tool_size = 40_000_000 }, seq + 1);
            ack = await licence.HandleCommandAsync(web, CancellationToken.None);
            Assert.True(ack!["ok"]!.GetValue<bool>(), ack["msg"]!.GetValue<string>());
        }
        // The token is kept only encrypted; the stored command leaves it out.
        var id = web["id"]!.GetValue<string>();
        Assert.DoesNotContain(token, await fx.QueryDbAsync<string>($"SELECT body::text FROM lic.command WHERE id = '{id}'"), StringComparison.Ordinal);
        Assert.Equal("alnoor.nextdab.com", await fx.QueryDbAsync<string>("SELECT host FROM lic.web_state"));
        Assert.True(await fx.QueryDbAsync<bool>($"SELECT position(convert_to('{token}', 'UTF8') IN token_enc) = 0 FROM lic.web_state"));
        var report = await checkin.ReportAsync(CancellationToken.None);
        Assert.Equal("alnoor.nextdab.com", report["web"]!["host"]!.GetValue<string>());
        Assert.True(report["web"]!["on"]!.GetValue<bool>());
        Assert.DoesNotContain(token, report.ToJsonString(), StringComparison.Ordinal);
        var s = await Ok(await owner.GetAsync("/api/v1/whatsapp/settings"));
        Assert.Equal("https://alnoor.nextdab.com/wa/webhook", s.GetProperty("webhookUrl").GetString());

        // A bad command is refused; "off" stops it and forgets the token.
        var bad = fx.MakeCommand("web", new { host = "not a host", token, tool_url = "http://x/y", tool_sha256 = "zz", tool_size = 5 }, seq + 2);
        Assert.False((await licence.HandleCommandAsync(bad, CancellationToken.None))!["ok"]!.GetValue<bool>());
        Assert.True((await licence.HandleCommandAsync(fx.MakeCommand("web", new { off = true }, seq + 3), CancellationToken.None))!["ok"]!.GetValue<bool>());
        Assert.True(await fx.QueryDbAsync<bool>("SELECT NOT active AND token_enc IS NULL FROM lic.web_state"));
        report = await checkin.ReportAsync(CancellationToken.None);
        Assert.True(report["web"]!["off"]!.GetValue<bool>());
    }
}
