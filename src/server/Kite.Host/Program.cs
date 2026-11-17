using Kite.Host.Features;
using Kite.Host.Infrastructure;
using Kite.Identity;
using Kite.Persistence;
using Kite.Platform;
using System.Net;
using System.Security.Authentication;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Npgsql;

// Restore helper that needs no server: Kite.Host backup-decrypt --in X --out Y --recovery-key Z
if (await BackupCli.TryRunAsync(args) is { } cliExit) return cliExit;

var builder = WebApplication.CreateBuilder(args);

var kite = builder.Configuration.GetSection("Kite").Get<KiteOptions>() ?? new KiteOptions();
var appCs = builder.Configuration.GetConnectionString("App")
    ?? throw new InvalidOperationException("ConnectionStrings:App is required.");
var migratorCs = builder.Configuration.GetConnectionString("Migrator");
var dataDir = Path.GetFullPath(kite.DataDir);
Directory.CreateDirectory(dataDir);

builder.Services.AddSingleton(kite);
builder.Services.AddSingleton(NpgsqlDataSource.Create(appCs));
builder.Services.AddSingleton<IClock>(SystemClock.Instance);
builder.Services.AddSingleton<IIdGenerator>(UuidV7Generator.Instance);
builder.Services.AddSingleton<ServerContext>();
builder.Services.AddSingleton<KiteDb>();
builder.Services.AddSingleton<ModuleStateCache>();
builder.Services.AddSingleton<IPasswordHasher>(new Argon2PasswordHasher(new Argon2Options(kite.Argon2.MemoryKiB, kite.Argon2.Iterations, kite.Argon2.Parallelism)));
// Keys at rest are wrapped with DPAPI (machine scope) on Windows servers (blueprint 18.4); the Phase 2
// plain key file is upgraded in place the first time it is loaded.
var keyStore = new KeyStore(dataDir, KeyProtectors.ForThisMachine());
builder.Services.AddSingleton(keyStore);
builder.Services.AddSingleton<ISecretProtector>(new AesGcmSecretProtector(keyStore.LoadOrCreate("secrets.key", "secrets")));
builder.Services.AddSingleton(LicenceKeys.Create(kite.Licensing, builder.Environment.IsDevelopment()));
builder.Services.AddSingleton<PkiService>();
builder.Services.AddSingleton<LicenceService>();
builder.Services.AddSingleton<BranchLinkService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BranchLinkService>());
builder.Services.AddSingleton<CentralClient>();
builder.Services.AddSingleton<NextdabCheckinService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NextdabCheckinService>());
builder.Services.AddSingleton<BackupService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BackupService>());
builder.Services.AddHttpClient("nextdab", c => c.Timeout = TimeSpan.FromSeconds(30));
// Phase 8: the main server's internet address (Nextdab "web" command), the WhatsApp gateway and relay, repair reminders.
builder.Services.AddSingleton<WebAddressService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WebAddressService>());
builder.Services.AddSingleton<WhatsAppGateway>();
builder.Services.AddSingleton<NotifRelay>();
builder.Services.AddSingleton<RepairHousekeeping>();
builder.Services.AddHostedService<PartsWorker>();
builder.Services.AddHostedService<WhatsAppWorker>();
builder.Services.AddHttpClient("whatsapp", c => c.Timeout = TimeSpan.FromSeconds(20));
// Phase 10: customer credit and suppliers between branch servers and head office; holds, reminders and ledger checks.
builder.Services.AddSingleton<CreditLink>();
builder.Services.AddHostedService<CreditWorker>();

// Kestrel: the web/API addresses from configuration ("urls"), plus the TLS sync port on which branch servers
// register and send heartbeats with their client certificates (blueprint 18.1, 18.4).
var webhookOpen = WebhookListener.Reserve(kite.WhatsApp.WebhookPort);
builder.WebHost.ConfigureKestrel(o =>
{
    var urls = (builder.Configuration["urls"] ?? "http://localhost:5000").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    foreach (var url in urls)
    {
        var a = BindingAddress.Parse(url);
        if (a.Host is "localhost") o.ListenLocalhost(a.Port);
        else if (a.Host is "*" or "+" or "0.0.0.0" or "[::]") o.ListenAnyIP(a.Port);
        else if (IPAddress.TryParse(a.Host.Trim('[', ']'), out var ip)) o.Listen(ip, a.Port);
        else o.ListenAnyIP(a.Port);
    }
    // The WhatsApp webhook port: loopback only, for the Nextdab tunnel (only /wa/webhook answers on it).
    if (webhookOpen) o.Listen(IPAddress.Loopback, WebhookListener.Port);
    if (kite.Sync.Port > 0)
        o.ListenAnyIP(kite.Sync.Port, lo => lo.UseHttps(new HttpsConnectionAdapterOptions
        {
            ServerCertificateSelector = (_, _) => o.ApplicationServices.GetRequiredService<PkiService>().SyncTlsCertificate,
            ClientCertificateMode = ClientCertificateMode.AllowCertificate,
            ClientCertificateValidation = (_, _, _) => true, // checked against the business CA and revocation records per request
            SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        }));
});
builder.Services.AddSingleton(new SetupCodeStore(Path.Combine(dataDir, "setup-code.txt")));
builder.Services.AddSingleton(SessionPolicy.Default);
builder.Services.AddSingleton(new LockoutPolicy());

builder.Services.AddAuthentication(SessionAuthHandler.SchemeName)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, SessionAuthHandler>(SessionAuthHandler.SchemeName, null);
builder.Services.AddAuthorization();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);

var app = builder.Build();

app.UseKiteErrors();
app.UseTunnelGuard(WebhookListener.Port);
if (WebhookListener.Problem is { } webhookProblem) app.Logger.LogWarning("{Problem}", webhookProblem);
app.UseMiddleware<SuspensionMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

HealthEndpoints.Map(app);
SetupEndpoints.Map(app);
AuthEndpoints.Map(app);
UserEndpoints.Map(app);
RoleEndpoints.Map(app);
SettingsEndpoints.Map(app);
TerminalEndpoints.Map(app);
TaxEndpoints.Map(app);
AccountingEndpoints.Map(app);
AuditEndpoints.Map(app);
ServerEndpoints.Map(app);
LicenceEndpoints.Map(app);
BackupEndpoints.Map(app);
CatalogEndpoints.Map(app);
InventoryEndpoints.Map(app);
PosEndpoints.Map(app);
ReceiptEndpoints.Map(app);
EmergencyEndpoints.Map(app);
DeviceEndpoints.Map(app);
DeviceIdentity.Map(app);
DeviceCounts.Map(app);
TradeInEndpoints.Map(app);
RefurbishmentEndpoints.Map(app);
RepairEndpoints.Map(app);
PosEndpoints.MapRepairCorrection(app);
CommissionEndpoints.Map(app);
WhatsAppEndpoints.Map(app);
CreditEndpoints.Map(app);
SupplierEndpoints.Map(app);
PurchaseEndpoints.Map(app);
AccountingDeskEndpoints.Map(app);
ExpenseEndpoints.Map(app);

// The web app (built into wwwroot) handles every non-API route; unknown API routes are 404.
app.MapFallback(async ctx =>
{
    if (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/health") || ctx.Request.Path.StartsWithSegments("/sync")
        || ctx.Request.Path.StartsWithSegments("/wa"))
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    var index = app.Environment.WebRootFileProvider.GetFileInfo("index.html");
    if (!index.Exists)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        await ctx.Response.WriteAsync("The KITE web app is not installed on this server.");
        return;
    }
    ctx.Response.ContentType = "text/html; charset=utf-8";
    await ctx.Response.SendFileAsync(index);
}).WithMetadata(KiteAccess.Anonymous);

// Startup: migrate as kite_migrator, then load this server's identity.
if (kite.MigrateOnStartup)
{
    if (string.IsNullOrWhiteSpace(migratorCs))
        throw new InvalidOperationException("ConnectionStrings:Migrator is required when MigrateOnStartup is true.");
    var applied = await new Migrator(migratorCs).MigrateAsync();
    foreach (var name in applied) app.Logger.LogInformation("Applied migration {Migration}", name);
}
var server = app.Services.GetRequiredService<ServerContext>();
await server.LoadAsync(app.Services.GetRequiredService<NpgsqlDataSource>());
await Phase3Startup.RunAsync(app);
await Phase4Startup.RunAsync(app);
if (!server.IsConfigured)
{
    var code = app.Services.GetRequiredService<SetupCodeStore>().GetOrCreate();
    app.Logger.LogWarning("KITE is not set up yet. Open the web app and enter setup code {SetupCode} (also saved in {Path}).",
        code, Path.Combine(dataDir, "setup-code.txt"));
}

await app.RunAsync();
return 0;

public partial class Program;
