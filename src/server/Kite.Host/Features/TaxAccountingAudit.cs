using Kite.Host.Infrastructure;
using Kite.Persistence;
using Kite.Platform;

namespace Kite.Host.Features;

public sealed record TaxRateDto(decimal RatePercent, DateTime EffectiveFrom, DateTime? EffectiveTo);
public sealed record TaxCodeDto(Guid Id, string Code, string NameEn, string NameAr, string Kind, string Status, bool NeedsAccountantReview,
    DateTime? ReviewedAt, IReadOnlyList<TaxRateDto> Rates);

/// <summary>Tax codes with accountant review (blueprint 17.4, 26.5).</summary>
public static class TaxEndpoints
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/tax");

        g.MapGet("/codes", async (KiteDb db, ServerContext server, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(AccessScope.System, ActorContext.System, async t =>
            {
                var codes = await t.QueryAsync<(Guid Id, string Code, string NameEn, string NameAr, string Kind, string Status, bool Review, DateTime? ReviewedAt)>(
                    "SELECT id, code, name_en, name_ar, kind, status, needs_accountant_review, reviewed_at FROM tax.tax_code WHERE business_id = @b ORDER BY code",
                    new { b = server.BusinessId });
                var rates = (await t.QueryAsync<(Guid CodeId, decimal Rate, DateTime From, DateTime? To)>(
                    "SELECT tax_code_id, rate_percent, effective_from, effective_to FROM tax.tax_rate ORDER BY effective_from")).ToLookup(r => r.CodeId);
                return codes.Select(c => new TaxCodeDto(c.Id, c.Code, c.NameEn, c.NameAr, c.Kind, c.Status, c.Review, c.ReviewedAt,
                    rates[c.Id].Select(r => new TaxRateDto(r.Rate, r.From, r.To)).ToArray())).ToArray();
            }, ct)))
            .RequirePermission(Permissions.SettingsView);

        g.MapPost("/codes/{id:guid}/review", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            await db.InTransactionAsync(AccessScope.System, http.ActorOf(), async t =>
            {
                var n = await t.ExecuteAsync("""
                    UPDATE tax.tax_code SET needs_accountant_review = false, reviewed_by = @actor, reviewed_at = now(), updated_by = @actor
                     WHERE id = @id AND needs_accountant_review
                    """, new { id, actor = http.User.UserIdOf() });
                if (n == 0) throw new KiteException(ErrorCodes.NotFound, "Tax code not found or already reviewed.", 404);
                await t.AuditAsync(new AuditEntry("tax.code_reviewed", "tax_code", id, Reason: "Confirmed by accountant"));
                await t.PublishAsync("tax.code_reviewed", "tax_code", id, 2, Json.Obj(new { reviewed = true }));
            }, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.TaxReview);
    }
}

public sealed record AccountDto(Guid Id, string Code, string NameEn, string NameAr, string Type, Guid? ParentId, bool IsPostable, bool IsControl, string Status);
public sealed record PeriodDto(Guid Id, int FiscalYear, short PeriodNo, DateTime StartsOn, DateTime EndsOn, string Status);

/// <summary>Accounting screens (module acct_ui). Posting itself runs regardless of the switch (blueprint 2.3 rule 5).</summary>
public static class AccountingEndpoints
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/accounting");

        g.MapGet("/accounts", async (KiteDb db, ServerContext server, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => t.QueryAsync<AccountDto>("""
                SELECT id, code, name_en AS NameEn, name_ar AS NameAr, type, parent_id AS ParentId, is_postable AS IsPostable,
                       is_control AS IsControl, status FROM acct.account WHERE business_id = @b ORDER BY code
                """, new { b = server.BusinessId }), ct)))
            .RequirePermission(Permissions.SettingsView).RequireModule("acct_ui");

    }
}

public sealed record AuditRowDto(long Seq, DateTime OccurredAt, Guid? UserId, string? Username, Guid? BranchId, string Action,
    string? EntityType, Guid? EntityId, string? Reason);

/// <summary>Audit log viewer and chain verification (blueprint 18.6).</summary>
public static class AuditEndpoints
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/audit");

        g.MapGet("", async (HttpContext http, KiteDb db, long? before, int? limit, CancellationToken ct) =>
        {
            var take = Math.Clamp(limit ?? 50, 1, 200);
            return Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.QueryAsync<AuditRowDto>("""
                SELECT a.seq, a.occurred_at AS OccurredAt, a.user_id AS UserId, u.username, a.branch_id AS BranchId, a.action,
                       a.entity_type AS EntityType, a.entity_id AS EntityId, a.reason
                  FROM audit.audit_log a LEFT JOIN iam."user" u ON u.id = a.user_id
                 WHERE (@before::bigint IS NULL OR a.seq < @before)
                 ORDER BY a.seq DESC LIMIT @take
                """, new { before, take }), ct));
        }).RequirePermission(Permissions.AuditView);

        g.MapPost("/verify", async (HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            var result = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var r = await t.QuerySingleOrDefaultAsync<(bool Ok, long RowsChecked, long? FirstBrokenSeq)>(
                    "SELECT ok, rows_checked, first_broken_seq FROM audit.verify_chain()");
                await t.AuditAsync(new AuditEntry("audit.verified", Reason: r.Ok ? "chain intact" : $"chain broken at seq {r.FirstBrokenSeq}"));
                return r;
            }, ct);
            return Results.Ok(new { ok = result.Ok, rowsChecked = result.RowsChecked, firstBrokenSeq = result.FirstBrokenSeq });
        }).RequirePermission(Permissions.AuditVerify);
    }
}
