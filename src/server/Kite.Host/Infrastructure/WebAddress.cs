using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kite.Identity;
using Kite.Persistence;
using Kite.Platform;

namespace Kite.Host.Infrastructure;

/// <summary>
/// The main server's internet address, made by Nextdab Admin (as for Kite RMS, Kite REMS and MOBILE STORE ERP): a signed "web"
/// command brings https://&lt;name&gt;.&lt;domain&gt;, the tunnel token and where to get cloudflared with its SHA-256 and size. The token is
/// kept only here, encrypted; cloudflared gets it in the TUNNEL_TOKEN environment variable. The tunnel's only route is this
/// server's loopback webhook port, on which only the WhatsApp webhook answers. "web {off:true}" stops it.
/// </summary>
public sealed partial class WebAddressService(KiteDb db, ISecretProtector secrets, KiteOptions opts, IHttpClientFactory http, ILogger<WebAddressService> log)
    : BackgroundService
{
    private readonly object _gate = new();
    private Process? _proc;
    private string _state = "off";
    private string? _error;
    private DateTime? _connectedSince;
    private readonly SemaphoreSlim _wake = new(0);

    public sealed class WebRow
    {
        public string? Host { get; set; }
        public byte[]? TokenEnc { get; set; }
        public int? Port { get; set; }
        public string? ToolUrl { get; set; }
        public string? ToolSha256 { get; set; }
        public long? ToolSize { get; set; }
        public bool Active { get; set; }
        public string? OffReason { get; set; }
        public string? CommandId { get; set; }
        public DateTime? ReceivedAt { get; set; }
    }

    public static string Platform => (OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "darwin" : "other")
        + "-" + (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "amd64");

    private string ToolPath => Path.Combine(Path.GetFullPath(opts.DataDir), "tools", OperatingSystem.IsWindows() ? "cloudflared.exe" : "cloudflared");

    public static Task<WebRow?> LoadAsync(DbTx t) => t.QuerySingleOrDefaultAsync<WebRow>("""
        SELECT host, token_enc AS TokenEnc, port, tool_url AS ToolUrl, tool_sha256 AS ToolSha256, tool_size AS ToolSize, active, off_reason AS OffReason,
               command_id AS CommandId, received_at AS ReceivedAt FROM lic.web_state LIMIT 1
        """);

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$")]
    private static partial Regex HostPattern();

    /// <summary>Carries out a verified "web" command. The token is encrypted before it is stored; the command's body is stored without it.</summary>
    public async Task<(bool Ok, string Msg)> ApplyAsync(string commandId, JsonObject data, CancellationToken ct)
    {
        if (data["off"] is JsonValue off && off.TryGetValue<bool>(out var isOff) && isOff)
        {
            await db.InTransactionAsync(AccessScope.System, ActorContext.System, async t =>
            {
                await t.ExecuteAsync("""
                    INSERT INTO lic.web_state (id, active, off_reason, command_id, received_at, token_enc) VALUES (true, false, 'turned off by Nextdab', @commandId, now(), NULL)
                    ON CONFLICT (id) DO UPDATE SET active = false, off_reason = 'turned off by Nextdab', token_enc = NULL, command_id = @commandId,
                           received_at = now(), updated_at = now()
                    """, new { commandId });
                await t.AuditAsync(new AuditEntry("web.address_off", "licence", null, After: Json.Obj(new { commandId })));
            }, ct);
            Stop("off");
            return (true, "internet address turned off");
        }
        string S(string k) => (data[k] as JsonValue)?.TryGetValue<string>(out var v) == true ? v.Trim() : "";
        var host = S("host").ToLowerInvariant();
        var token = S("token");
        var toolUrl = S("tool_url");
        var sha = S("tool_sha256").ToLowerInvariant();
        long size = data["tool_size"] is JsonValue sv && sv.TryGetValue<long>(out var sz) ? sz : 0;
        int port = data["port"] is JsonValue pv && pv.TryGetValue<int>(out var p) ? p : 0;
        if (!HostPattern().IsMatch(host) || host.Length > 200) return (false, "the address is not a host name");
        if (token.Length is < 20 or > 4000) return (false, "the tunnel token is missing");
        if (!Uri.TryCreate(toolUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps) return (false, "cloudflared must come from an https address");
        if (sha.Length != 64 || !sha.All(Uri.IsHexDigit)) return (false, "cloudflared's SHA-256 is missing");
        if (size is < 1_000_000 or > 300_000_000) return (false, "cloudflared's size is missing");
        if (!OperatingSystem.IsWindows() && toolUrl.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return (false, $"this server runs on {Platform}; the cloudflared sent is for Windows");
        var enc = secrets.Protect(Encoding.UTF8.GetBytes(token));
        await db.InTransactionAsync(AccessScope.System, ActorContext.System, async t =>
        {
            await t.ExecuteAsync("""
                INSERT INTO lic.web_state (id, host, token_enc, port, tool_url, tool_sha256, tool_size, active, off_reason, command_id, received_at)
                VALUES (true, @host, @enc, @port, @toolUrl, @sha, @size, true, NULL, @commandId, now())
                ON CONFLICT (id) DO UPDATE SET host = @host, token_enc = @enc, port = @port, tool_url = @toolUrl, tool_sha256 = @sha, tool_size = @size,
                       active = true, off_reason = NULL, command_id = @commandId, received_at = now(), updated_at = now()
                """, new { host, enc, port = port == 0 ? (int?)null : port, toolUrl, sha, size, commandId });
            await t.AuditAsync(new AuditEntry("web.address_set", "licence", null, After: Json.Obj(new { host, port, commandId })));
        }, ct);
        Stop("starting");
        _wake.Release();
        var note = port != 0 && port != opts.WhatsApp.WebhookPort ? $" (Nextdab routes it to port {port}; this server's webhook port is {opts.WhatsApp.WebhookPort})" : "";
        return (true, $"https://{host} received{note}");
    }

    /// <summary>What the check-in report says about the address (never the token).</summary>
    public async Task<JsonObject> ReportAsync(CancellationToken ct)
    {
        var w = await db.InTransactionAsync(AccessScope.System, ActorContext.System, LoadAsync, ct);
        lock (_gate)
            return new JsonObject
            {
                ["host"] = w?.Host,
                ["on"] = w?.Active ?? false,
                ["port"] = opts.WhatsApp.WebhookPort,
                ["path"] = "/wa/webhook",
                ["os"] = Platform,
                ["off"] = w is { Active: false, OffReason: not null },
                ["off_reason"] = w?.OffReason,
                ["tunnel"] = w?.Active == true ? _state : "off",
                ["tunnel_error"] = _error ?? (w?.Active == true ? WebhookListener.Problem : null),
                ["listening"] = WebhookListener.Port > 0,
                ["connected_since"] = _connectedSince?.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            };
    }

    public (string State, string? Error, DateTime? Since) Status
    {
        get { lock (_gate) return (_state, _error, _connectedSince); }
    }

    private void Stop(string next)
    {
        lock (_gate)
        {
            try { if (_proc is { HasExited: false }) _proc.Kill(true); } catch (InvalidOperationException) { }
            _proc = null;
            _state = next;
            _error = null;
            _connectedSince = null;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(5);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var w = await db.InTransactionAsync(AccessScope.System, ActorContext.System, LoadAsync, stoppingToken);
                bool running;
                lock (_gate) running = _proc is { HasExited: false };
                if (w is { Active: true, TokenEnc: not null } && WebhookListener.Port > 0 && !running)
                {
                    await StartAsync(w, stoppingToken);
                    delay = TimeSpan.FromSeconds(30);
                }
                else if ((w is null || !w.Active) && running) Stop("off");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                lock (_gate) { _state = "error"; _error = e.Message; }
                log.LogWarning(e, "Internet address");
                delay = TimeSpan.FromMinutes(1);
            }
            try { await _wake.WaitAsync(delay, stoppingToken); } catch (OperationCanceledException) { }
        }
        Stop("off");
    }

    private async Task StartAsync(WebRow w, CancellationToken ct)
    {
        var exe = ToolPath;
        if (!File.Exists(exe) || !await HashOkAsync(exe, w.ToolSha256!, ct))
        {
            lock (_gate) { _state = "downloading"; _error = null; }
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
            using var client = http.CreateClient("nextdab");
            client.Timeout = TimeSpan.FromMinutes(5);
            var bytes = await client.GetByteArrayAsync(w.ToolUrl, ct);
            if (bytes.LongLength != w.ToolSize) throw new InvalidOperationException($"cloudflared is {bytes.LongLength} bytes, not {w.ToolSize}; not used");
            var got = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (got != w.ToolSha256) throw new InvalidOperationException("cloudflared's SHA-256 does not match the signed command; not used");
            await File.WriteAllBytesAsync(exe + ".part", bytes, ct);
            File.Move(exe + ".part", exe, true);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var psi = new ProcessStartInfo(exe, "tunnel --no-autoupdate run")
        {
            UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true,
        };
        psi.Environment["TUNNEL_TOKEN"] = Encoding.UTF8.GetString(secrets.Unprotect(w.TokenEnc!));
        lock (_gate) { _state = "starting"; _error = null; }
        var p = Process.Start(psi) ?? throw new InvalidOperationException("cloudflared did not start");
        p.EnableRaisingEvents = true;
        void Line(string? line)
        {
            if (line is null) return;
            lock (_gate)
            {
                if (_proc != p) return;
                if (line.Contains("Registered tunnel connection", StringComparison.Ordinal)) { _state = "connected"; _error = null; _connectedSince ??= DateTime.UtcNow; }
                else if (line.Contains(" ERR ", StringComparison.Ordinal)) _error = line.Length > 300 ? line[..300] : line;
            }
        }
        p.ErrorDataReceived += (_, e) => Line(e.Data);
        p.OutputDataReceived += (_, e) => Line(e.Data);
        p.Exited += (_, _) =>
        {
            lock (_gate) if (_proc == p) { _proc = null; _state = "error"; _error ??= "cloudflared stopped; it starts again shortly"; _connectedSince = null; }
            _wake.Release();
        };
        p.BeginErrorReadLine();
        p.BeginOutputReadLine();
        lock (_gate) _proc = p;
        log.LogInformation("Internet address: cloudflared started for https://{Host}", w.Host);
    }

    private static async Task<bool> HashOkAsync(string path, string sha, CancellationToken ct)
    {
        await using var f = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(f, ct)).ToLowerInvariant() == sha;
    }
}
