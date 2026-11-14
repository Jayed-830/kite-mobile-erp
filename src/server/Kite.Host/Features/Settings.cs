using Kite.Host.Infrastructure;
using Kite.Persistence;
using Kite.Platform;

namespace Kite.Host.Features;

public sealed record BusinessDto(
    Guid Id, string LegalNameEn, string LegalNameAr, string? TradeNameEn, string? TradeNameAr, string? CrNumber, string? VatNumber,
    string CountryCode, string BaseCurrency, short CurrencyDecimals, string TimeZone, short FiscalYearStartMonth, bool PricesIncludeTax,
    string? AddressEn, string? AddressAr, string? Phone, string? Email, int Version);

public sealed record UpdateBusinessRequest(
    string LegalNameEn, string LegalNameAr, string? TradeNameEn, string? TradeNameAr, string? CrNumber, string? VatNumber,
    string? AddressEn, string? AddressAr, string? Phone, string? Email, bool PricesIncludeTax, int Version);

/// <summary>Locked: null, "nextdab" (switched off by Nextdab) or "licence" (not in the licence).</summary>
public sealed record ModuleDto(string Code, string NameEn, string NameAr, bool AlwaysOn, bool Enabled, IReadOnlyList<string> DependsOn, string? Locked = null);
public sealed record UpdateModulesRequest(Dictionary<string, bool> Changes);

/// <summary>Business profile, branches and module switches (blueprint 2.4, 19).</summary>
public static class SettingsEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/branches", async (HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            var scope = http.User.ScopeOf();
            var all = await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => t.QueryAsync<BranchDto>(
                "SELECT id, code, name_en AS NameEn, name_ar AS NameAr, is_head_office AS IsHeadOffice, status FROM core.branch ORDER BY code"), ct);
            return Results.Ok(all.Where(b => scope.CanSee(b.Id)));
        }).RequireSignedIn();

        var g = app.MapGroup("/api/v1/settings");

        g.MapGet("/business", async (KiteDb db, ServerContext server, CancellationToken ct) =>
            Results.Ok(await LoadBusiness(db, server, ct)))
            .RequirePermission(Permissions.SettingsView);

        g.MapPut("/business", async (UpdateBusinessRequest req, HttpContext http, KiteDb db, ServerContext server, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.LegalNameEn) || string.IsNullOrWhiteSpace(req.LegalNameAr))
                throw new KiteException(ErrorCodes.Validation, "Legal names in English and Arabic are required.", 400, "legalNameEn");
            await db.InTransactionAsync(AccessScope.System, http.ActorOf(), async t =>
            {
                var before = await t.QuerySingleOrDefaultAsync<BusinessDto>(BusinessSql, new { b = server.BusinessId })
                    ?? throw new KiteException(ErrorCodes.NotFound, "Business not found.", 404);
                if (before.Version != req.Version)
                    throw new KiteException(ErrorCodes.VersionMismatch, "Someone else changed the business details. Reload and try again.", 412);
                await t.ExecuteAsync("""
                    UPDATE core.business SET legal_name_en = @LegalNameEn, legal_name_ar = @LegalNameAr, trade_name_en = @TradeNameEn,
                           trade_name_ar = @TradeNameAr, cr_number = @CrNumber, vat_number = @VatNumber, address_en = @AddressEn,
                           address_ar = @AddressAr, phone = @Phone, email = @Email, prices_include_tax = @PricesIncludeTax, updated_by = @actor
                     WHERE id = @b
                    """, new { b = server.BusinessId, req.LegalNameEn, req.LegalNameAr, req.TradeNameEn, req.TradeNameAr, req.CrNumber, req.VatNumber,
                               req.AddressEn, req.AddressAr, req.Phone, req.Email, req.PricesIncludeTax, actor = http.User.UserIdOf() });
                var after = Json.Obj(req);
                await t.AuditAsync(new AuditEntry("business.updated", "business", server.BusinessId, Before: Json.Obj(before), After: after));
                await t.PublishAsync("core.business_updated", "business", server.BusinessId!.Value, before.Version + 1, after);
            }, ct);
            return Results.Ok(await LoadBusiness(db, server, ct));
        }).RequirePermission(Permissions.SetupBusinessEdit);

        g.MapGet("/modules", async (ModuleStateCache cache, CancellationToken ct) => Results.Ok(await ModuleList(cache, ct)))
            .RequirePermission(Permissions.SettingsView);

        g.MapPut("/modules", async (UpdateModulesRequest req, HttpContext http, KiteDb db, ServerContext server, ModuleStateCache cache, CancellationToken ct) =>
        {
            cache.Invalidate();
            var current = await cache.GetConfiguredAsync(ct);
            var next = ModuleCatalog.Apply(current, req.Changes ?? []);
            var restrictions = await cache.RestrictionsAsync(ct);
            foreach (var (code, on) in next)
                if (on && !current[code] && restrictions.LockOf(code) is { } lockedBy)
                    throw new KiteException(ErrorCodes.ModuleNotLicensed, lockedBy == "nextdab"
                        ? $"The {ModuleCatalog.Get(code).NameEn} module was switched off by Nextdab."
                        : $"The licence does not include the {ModuleCatalog.Get(code).NameEn} module. Ask Nextdab to add it.", 403, code);
            var changed = next.Where(kv => current[kv.Key] != kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            if (changed.Count > 0)
            {
                await db.InTransactionAsync(AccessScope.System, http.ActorOf(), async t =>
                {
                    foreach (var (code, enabled) in changed)
                        await t.ExecuteAsync("""
                            INSERT INTO core.module_setting (id, business_id, branch_id, module_code, enabled, created_by, updated_by)
                            VALUES (@id, @b, NULL, @code, @enabled, @actor, @actor)
                            ON CONFLICT ON CONSTRAINT module_setting_scope_unique
                            DO UPDATE SET enabled = EXCLUDED.enabled, updated_by = EXCLUDED.updated_by
                            """, new { id = Ids.New(), b = server.BusinessId, code, enabled, actor = http.User.UserIdOf() });
                    var payload = Json.Obj(new { changes = changed });
                    await t.AuditAsync(new AuditEntry("settings.modules_changed", "business", server.BusinessId,
                        Before: Json.Obj(changed.ToDictionary(kv => kv.Key, kv => current[kv.Key])), After: payload));
                    var version = await t.ExecuteScalarAsync<long>("SELECT count(*) + 1 FROM sync.outbox WHERE event_type = 'core.modules_configured'");
                    await t.PublishAsync("core.modules_configured", "business", server.BusinessId!.Value, (int)version, payload);
                }, ct);
            }
            cache.Invalidate();
            return Results.Ok(await ModuleList(cache, ct));
        }).RequirePermission(Permissions.SettingsModulesEdit);
    }

    private static async Task<IEnumerable<ModuleDto>> ModuleList(ModuleStateCache cache, CancellationToken ct)
    {
        var states = await cache.GetAsync(ct);
        var restrictions = await cache.RestrictionsAsync(ct);
        return ModuleCatalog.All.Select(m => new ModuleDto(m.Code, m.NameEn, m.NameAr, m.AlwaysOn, states[m.Code], m.DependsOn, m.AlwaysOn ? null : restrictions.LockOf(m.Code)));
    }

    private const string BusinessSql = """
        SELECT id, legal_name_en AS LegalNameEn, legal_name_ar AS LegalNameAr, trade_name_en AS TradeNameEn, trade_name_ar AS TradeNameAr,
               cr_number AS CrNumber, vat_number AS VatNumber, country_code AS CountryCode, base_currency AS BaseCurrency,
               currency_decimals AS CurrencyDecimals, time_zone AS TimeZone, fiscal_year_start_month AS FiscalYearStartMonth,
               prices_include_tax AS PricesIncludeTax, address_en AS AddressEn, address_ar AS AddressAr, phone, email, version
          FROM core.business WHERE id = @b
        """;

    private static async Task<BusinessDto> LoadBusiness(KiteDb db, ServerContext server, CancellationToken ct) =>
        await db.InTransactionAsync(AccessScope.System, ActorContext.System,
            t => t.QuerySingleOrDefaultAsync<BusinessDto>(BusinessSql, new { b = server.BusinessId }), ct)
        ?? throw new KiteException(ErrorCodes.SetupRequired, "Set up the business first.", 409);
}
