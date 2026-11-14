using Kite.Host.Infrastructure;
using Kite.Persistence;
using Kite.Platform;

namespace Kite.Host.Features;

public static class HealthEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowEveryone();

        app.MapGet("/health/ready", async (KiteDb db, ServerContext server, CancellationToken ct) =>
        {
            var schema = await db.InTransactionAsync(AccessScope.System, ActorContext.System,
                t => t.ExecuteScalarAsync<int?>("SELECT max(version) FROM meta.schema_info"), ct);
            return Results.Ok(new { status = "ready", configured = server.IsConfigured, role = server.Role, schemaVersion = schema });
        }).AllowEveryone();
    }
}
