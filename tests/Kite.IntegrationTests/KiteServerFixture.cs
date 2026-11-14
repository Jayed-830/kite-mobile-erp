using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kite.Host.Infrastructure;
using Kite.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Kite.IntegrationTests;

/// <summary>
/// Runs only when KITE_TEST_PG holds a PostgreSQL superuser connection string, e.g.
/// "Host=localhost;Username=postgres;Password=postgres". CI provides it.
/// </summary>
public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KITE_TEST_PG")))
            Skip = "KITE_TEST_PG not set; integration tests need PostgreSQL.";
    }
}

[CollectionDefinition("server")]
public sealed class ServerCollection : ICollectionFixture<KiteServerFixture>;

/// <summary>
/// Creates a fresh database (bootstrap as superuser, migrations by the server as kite_migrator),
/// starts the real server in memory and completes the setup wizard once.
/// </summary>
public sealed class KiteServerFixture : IAsyncLifetime
{
    public const string OwnerUsername = "owner";
    public const string OwnerPassword = "Owner-Pass-2026!";
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _dbName = "kite_it_" + Guid.NewGuid().ToString("N")[..12];
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "kite-it-" + Guid.NewGuid().ToString("N"));
    private string? _superCs;

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public bool Enabled { get; private set; }
    public JsonElement SetupResult { get; private set; }

    public async Task InitializeAsync()
    {
        _superCs = Environment.GetEnvironmentVariable("KITE_TEST_PG");
        if (string.IsNullOrWhiteSpace(_superCs)) return;
        Enabled = true;

        await using (var admin = new NpgsqlConnection(_superCs))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{_dbName}\"", admin);
            await create.ExecuteNonQueryAsync();
        }
        var dbCs = new NpgsqlConnectionStringBuilder(_superCs) { Database = _dbName }.ConnectionString;
        await using (var conn = new NpgsqlConnection(dbCs))
        {
            await conn.OpenAsync();
            await using var boot = new NpgsqlCommand(Migrator.BootstrapSql(), conn);
            await boot.ExecuteNonQueryAsync();
            await using var pw = new NpgsqlCommand("ALTER ROLE kite_migrator PASSWORD 'migrator-test-pw'; ALTER ROLE kite_app PASSWORD 'app-test-pw';", conn);
            await pw.ExecuteNonQueryAsync();
        }

        var basis = new NpgsqlConnectionStringBuilder(_superCs) { Database = _dbName };
        var appCs = new NpgsqlConnectionStringBuilder(basis.ConnectionString) { Username = "kite_app", Password = "app-test-pw" }.ConnectionString;
        var migCs = new NpgsqlConnectionStringBuilder(basis.ConnectionString) { Username = "kite_migrator", Password = "migrator-test-pw" }.ConnectionString;

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:App", appCs);
            b.UseSetting("ConnectionStrings:Migrator", migCs);
            b.UseSetting("Kite:DataDir", _dataDir);
            b.UseSetting("Kite:Argon2:MemoryKiB", "1024");
            b.UseSetting("Kite:Argon2:Iterations", "1");
            b.UseSetting("Kite:Argon2:Parallelism", "1");
        });

        using var client = Factory.CreateClient();
        var code = Factory.Services.GetRequiredService<SetupCodeStore>().GetOrCreate();
        var response = await client.PostAsJsonAsync("/api/v1/setup", SetupBody(code));
        response.EnsureSuccessStatusCode();
        SetupResult = await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static object SetupBody(string code) => new
    {
        setupCode = code,
        business = new
        {
            legalNameEn = "Al Noor Mobiles LLC",
            legalNameAr = "شركة النور للهواتف",
            vatNumber = "OM1100223344",
            crNumber = "1234567",
            fiscalYearStartMonth = 1,
            pricesIncludeTax = true,
        },
        headOffice = new { code = "B01", nameEn = "Muscat - Ruwi", nameAr = "مسقط - روي" },
        owner = new { username = OwnerUsername, displayNameEn = "Owner", displayNameAr = "المالك", password = OwnerPassword, preferredLanguage = "en" },
        serverRole = "combined",
    };

    public HttpClient Client(string? token = null)
    {
        var c = Factory.CreateClient();
        if (token is not null) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    public async Task<JsonElement> LoginAsync(string username, string password, Guid? terminalId = null)
    {
        using var c = Client();
        var r = await c.PostAsJsonAsync("/api/v1/auth/login", new { username, password, terminalId });
        Assert.True(r.IsSuccessStatusCode, $"login {username} failed: {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<string> TokenAsync(string username, string password) =>
        (await LoginAsync(username, password)).GetProperty("accessToken").GetString()!;

    public Task<string> OwnerTokenAsync() => TokenAsync(OwnerUsername, OwnerPassword);

    public async Task<T> QueryDbAsync<T>(string sql)
    {
        var cs = new NpgsqlConnectionStringBuilder(_superCs) { Database = _dbName }.ConnectionString;
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    public async Task DisposeAsync()
    {
        if (!Enabled) return;
        await Factory.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(_superCs);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }
}
