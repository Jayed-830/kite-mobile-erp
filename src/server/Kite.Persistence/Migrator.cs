using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

namespace Kite.Persistence;

public sealed record MigrationScript(int Version, string Name, string Sql, string Checksum);

/// <summary>
/// Applies the versioned SQL migrations embedded from db/migrations (blueprint 9.6):
/// forward-only, one transaction per script, recorded with a checksum in meta.schema_info.
/// An applied script whose content changed is refused, so history cannot be rewritten.
/// Runs as kite_migrator, never as the application role.
/// </summary>
public sealed partial class Migrator(string migratorConnectionString)
{
    [GeneratedRegex(@"^V(?<v>\d{4})__(?<n>[a-z0-9_]+)\.sql$", RegexOptions.CultureInvariant)]
    private static partial Regex FileName();

    public static IReadOnlyList<MigrationScript> LoadScripts()
    {
        var asm = typeof(Migrator).Assembly;
        var scripts = new List<MigrationScript>();
        foreach (var res in asm.GetManifestResourceNames().Where(r => r.StartsWith("migrations/", StringComparison.Ordinal)))
        {
            var file = res["migrations/".Length..];
            var m = FileName().Match(file);
            if (!m.Success) throw new InvalidOperationException($"Badly named migration '{file}'.");
            var sql = ReadResource(asm, res);
            scripts.Add(new MigrationScript(int.Parse(m.Groups["v"].Value, CultureInfo.InvariantCulture), m.Groups["n"].Value, sql, Checksum(sql)));
        }
        var ordered = scripts.OrderBy(s => s.Version).ToList();
        for (var i = 0; i < ordered.Count; i++)
            if (ordered[i].Version != i + 1)
                throw new InvalidOperationException($"Migrations must be numbered without gaps; expected V{i + 1:D4}.");
        return ordered;
    }

    public static string BootstrapSql() => ReadResource(typeof(Migrator).Assembly, "bootstrap/00_bootstrap.sql");

    /// <summary>Line endings are normalised so Windows and Linux checkouts give the same checksum.</summary>
    public static string Checksum(string sql) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql.Replace("\r\n", "\n", StringComparison.Ordinal))));

    /// <summary>Applies pending migrations and returns the names applied.</summary>
    public async Task<IReadOnlyList<string>> MigrateAsync(CancellationToken ct = default)
    {
        var scripts = LoadScripts();
        await using var conn = new NpgsqlConnection(migratorConnectionString);
        await conn.OpenAsync(ct);

        // One migrator at a time per database.
        await Exec(conn, null, "SELECT pg_advisory_lock(hashtextextended('kite.migrator', 0))", ct);
        try
        {
            await Exec(conn, null, """
                CREATE SCHEMA IF NOT EXISTS meta;
                CREATE TABLE IF NOT EXISTS meta.schema_info (
                  version    integer PRIMARY KEY,
                  name       text NOT NULL,
                  checksum   text NOT NULL,
                  applied_at timestamptz NOT NULL DEFAULT now(),
                  applied_by text NOT NULL DEFAULT current_user);
                GRANT USAGE ON SCHEMA meta TO kite_app, kite_ro;
                GRANT SELECT ON meta.schema_info TO kite_app, kite_ro;
                """, ct);

            var applied = new Dictionary<int, string>();
            await using (var cmd = new NpgsqlCommand("SELECT version, checksum FROM meta.schema_info", conn))
            await using (var rd = await cmd.ExecuteReaderAsync(ct))
                while (await rd.ReadAsync(ct)) applied[rd.GetInt32(0)] = rd.GetString(1);

            foreach (var (version, checksum) in applied)
            {
                var script = scripts.FirstOrDefault(s => s.Version == version)
                    ?? throw new InvalidOperationException($"Database has migration V{version:D4}, which this build does not know. Upgrade the software.");
                if (script.Checksum != checksum)
                    throw new InvalidOperationException($"Migration V{version:D4} ({script.Name}) was changed after it was applied. Refusing to continue.");
            }

            var done = new List<string>();
            foreach (var script in scripts.Where(s => !applied.ContainsKey(s.Version)))
            {
                await using var tx = await conn.BeginTransactionAsync(ct);
                await Exec(conn, tx, script.Sql, ct);
                await using (var rec = new NpgsqlCommand("INSERT INTO meta.schema_info (version, name, checksum) VALUES (@v, @n, @c)", conn, tx))
                {
                    rec.Parameters.AddWithValue("v", script.Version);
                    rec.Parameters.AddWithValue("n", script.Name);
                    rec.Parameters.AddWithValue("c", script.Checksum);
                    await rec.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
                done.Add($"V{script.Version:D4}__{script.Name}");
            }
            return done;
        }
        finally
        {
            await Exec(conn, null, "SELECT pg_advisory_unlock(hashtextextended('kite.migrator', 0))", CancellationToken.None);
        }
    }

    private static async Task Exec(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx) { CommandTimeout = 300 };
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string ReadResource(Assembly asm, string name)
    {
        using var s = asm.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"Missing resource {name}.");
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }
}
