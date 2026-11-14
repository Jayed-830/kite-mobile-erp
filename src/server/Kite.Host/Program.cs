using Kite.Host.Features;
using Kite.Host.Infrastructure;
using Kite.Identity;
using Kite.Persistence;
using Kite.Platform;
using Npgsql;

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
builder.Services.AddSingleton<ISecretProtector>(new AesGcmSecretProtector(KeyFile.LoadOrCreate(Path.Combine(dataDir, "secrets.key"))));
builder.Services.AddSingleton(new SetupCodeStore(Path.Combine(dataDir, "setup-code.txt")));
builder.Services.AddSingleton(SessionPolicy.Default);
builder.Services.AddSingleton(new LockoutPolicy());

builder.Services.AddAuthentication(SessionAuthHandler.Scheme)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, SessionAuthHandler>(SessionAuthHandler.Scheme, null);
builder.Services.AddAuthorization();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);

var app = builder.Build();

app.UseKiteErrors();
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

// The web app (built into wwwroot) handles every non-API route; unknown API routes are 404.
app.MapFallback(async ctx =>
{
    if (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/health"))
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
if (!server.IsConfigured)
{
    var code = app.Services.GetRequiredService<SetupCodeStore>().GetOrCreate();
    app.Logger.LogWarning("KITE is not set up yet. Open the web app and enter setup code {SetupCode} (also saved in {Path}).",
        code, Path.Combine(dataDir, "setup-code.txt"));
}

await app.RunAsync();

public partial class Program;
