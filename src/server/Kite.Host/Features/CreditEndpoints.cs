using System.Globalization;
using Kite.Accounting;
using Kite.Host.Infrastructure;
using Kite.Persistence;
using Kite.Platform;
using Kite.Sales;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Kite.Host.Features;

public sealed record CreditPolicyRequest(int DefaultTermsDays, int BlockOverdueDays, int HoldOverdueDays, decimal OfflineSaleCap, decimal OverrideCap, bool AllowOvercommit,
    int OnlineTimeoutMs, int AuthorisationMinutes, int MaxInstalments, int RemindBeforeDays, int RemindOverdueEvery, int Version);
public sealed record AllowanceInput(Guid BranchId, decimal Amount);
public sealed record CreditAccountRequest(Guid CustomerId, decimal CreditLimit, int? TermsDays, decimal? OfflineSaleCap, string? Note, List<AllowanceInput>? Allowances,
    string? Status = null, int? Version = null);
public sealed record CreditHoldRequest(string Reason);
public sealed record AllocationInput(Guid ItemId, decimal Amount);
public sealed record CustomerPaymentRequest(Guid Id, Guid CustomerId, string Method, decimal Amount, Guid? BranchId = null, string? CardTerminal = null,
    string? ApprovalCode = null, string? Reference = null, string? Note = null, List<AllocationInput>? Allocations = null);
public sealed record ManualAllocationRequest(Guid CreditEntryId, List<AllocationInput> Allocations);
public sealed record WriteOffRequest(string Reason, List<Guid>? ItemIds = null, decimal? Amount = null);
public sealed record CustomerOpeningRequest(Guid BranchId, decimal Amount, DateOnly? Date = null, string? Note = null);

/// <summary>Customer credit (blueprint 14): policy, accounts and allowances, payments, holds, write-offs, statements and reports.</summary>
public static class CreditEndpoints
{
    private static string M(decimal v) => v.ToString("0.000", CultureInfo.InvariantCulture);
    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static void HeadOffice(DbTx t, string what)
    {
        if (!t.Server.IsHeadOffice) throw new KiteException(ErrorCodes.NotHeadOffice, $"{what} at head office; branch servers get the result with the next sync.", 409);
    }

    private static Guid OwnerBranch(DbTx t) => t.Server.BranchId ?? throw new KiteException(ErrorCodes.Internal, "This server has no branch of its own.", 500);

    private const string Today = "(now() AT TIME ZONE (SELECT time_zone FROM core.business LIMIT 1))::date";

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/credit");

        // ---------------------------------------------------------------- policy (14.2, 14.3, 14.5)
        g.MapGet("/policy", async (HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), CreditCore.PolicyAsync, ct)))
            .RequirePermission(Permissions.CreditView).RequireModule("credit");
        g.MapPut("/policy", async (CreditPolicyRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            if (req.DefaultTermsDays is < 0 or > 365) throw new KiteException(ErrorCodes.Validation, "Terms are 0 to 365 days.", 400, "defaultTermsDays");
            if (req.BlockOverdueDays is < 0 or > 3650 || req.HoldOverdueDays is < 0 or > 3650) throw new KiteException(ErrorCodes.Validation, "Overdue days are 0 to 3650.", 400, "blockOverdueDays");
            if (req.OfflineSaleCap < 0 || req.OverrideCap < 0) throw new KiteException(ErrorCodes.Validation, "Caps are zero or more.", 400, "offlineSaleCap");
            if (req.OnlineTimeoutMs is < 500 or > 10000) throw new KiteException(ErrorCodes.Validation, "Head office has 500 to 10000 ms to answer.", 400, "onlineTimeoutMs");
            if (req.AuthorisationMinutes is < 1 or > 1440) throw new KiteException(ErrorCodes.Validation, "A reservation lasts 1 to 1440 minutes.", 400, "authorisationMinutes");
            if (req.MaxInstalments is < 1 or > 60) throw new KiteException(ErrorCodes.Validation, "1 to 60 instalments.", 400, "maxInstalments");
            if (req.RemindBeforeDays is < 0 or > 60 || req.RemindOverdueEvery is < 0 or > 60) throw new KiteException(ErrorCodes.Validation, "Reminder days are 0 to 60.", 400, "remindBeforeDays");
            await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                HeadOffice(t, "The credit policy is set");
                var before = await CreditCore.PolicyAsync(t);
                var n = await t.ExecuteAsync("""
                    UPDATE crm.credit_policy SET default_terms_days = @DefaultTermsDays, block_overdue_days = @BlockOverdueDays, hold_overdue_days = @HoldOverdueDays,
                           offline_sale_cap = @OfflineSaleCap, override_cap = @OverrideCap, allow_overcommit = @AllowOvercommit, online_timeout_ms = @OnlineTimeoutMs,
                           authorisation_minutes = @AuthorisationMinutes, max_instalments = @MaxInstalments, remind_before_days = @RemindBeforeDays,
                           remind_overdue_every = @RemindOverdueEvery, updated_by = @uid
                     WHERE version = @Version
                    """, new { req.DefaultTermsDays, req.BlockOverdueDays, req.HoldOverdueDays, req.OfflineSaleCap, req.OverrideCap, req.AllowOvercommit, req.OnlineTimeoutMs,
                               req.AuthorisationMinutes, req.MaxInstalments, req.RemindBeforeDays, req.RemindOverdueEvery, req.Version, uid = http.User.UserIdOf() });
                if (n == 0) throw new KiteException(ErrorCodes.VersionMismatch, "Someone else changed the credit policy. Reload and try again.", 412, "version");
                await t.AuditAsync(new AuditEntry("credit.policy_changed", "credit_policy", null, Json.Obj(before), Json.Obj(req)));
                await t.PublishAsync("crm.credit_policy_changed", "credit_policy", (await t.ExecuteScalarAsync<Guid>("SELECT id FROM core.business LIMIT 1")), req.Version + 1, Json.Obj(req));
                return 0;
            }, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.CreditManage).RequireModule("credit");

        // ---------------------------------------------------------------- accounts (14.1)
        g.MapGet("/accounts", async (string? q, string? status, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.RowsAsync($"""
                SELECT x.*, CASE WHEN x."creditLimit" > 0 THEN round(x.exposure / x."creditLimit" * 100, 1) END AS utilisation,
                       greatest(x."creditLimit" - x.exposure, 0) AS available,
                       CASE WHEN x."oldestDue" IS NULL THEN 0 ELSE greatest({Today} - x."oldestDue", 0) END AS "overdueDays"
                  FROM (SELECT a.id, a.customer_id AS "customerId", c.number AS "customerNumber", c.name_en AS "nameEn", c.name_ar AS "nameAr", c.phone_e164 AS phone,
                               a.credit_limit AS "creditLimit", a.terms_days AS "termsDays", a.status, a.hold_kind AS "holdKind", a.hold_reason AS "holdReason",
                               CASE WHEN @ho THEN crm.credit_exposure(a.customer_id) ELSE a.exposure END AS exposure,
                               CASE WHEN @ho THEN (crm.receivable_status(a.customer_id)).oldest_due ELSE a.oldest_due END AS "oldestDue",
                               a.exposure_as_of AS "asOf", a.version
                          FROM crm.credit_account a JOIN crm.customer c ON c.id = a.customer_id
                         WHERE (@q::text IS NULL OR c.name_en ILIKE '%' || @q || '%' OR coalesce(c.name_ar, '') ILIKE '%' || @q || '%' OR c.number ILIKE '%' || @q || '%'
                                OR coalesce(c.phone_e164, '') LIKE '%' || @q || '%')
                           AND (@status::text IS NULL OR a.status = @status)) x
                 ORDER BY x."nameEn" LIMIT 500
                """, new { q = Clean(q), status = Clean(status), ho = t.Server.IsHeadOffice }), ct)))
            .RequirePermission(Permissions.CreditView).RequireModule("credit");

        g.MapGet("/accounts/{customerId:guid}", async (Guid customerId, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => DetailAsync(t, customerId), ct)))
            .RequirePermission(Permissions.CreditView).RequireModule("credit");

        g.MapPost("/accounts", async (CreditAccountRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                HeadOffice(t, "Credit accounts are opened");
                var policy = await CreditCore.PolicyAsync(t);
                Check(req, policy);
                if (!await t.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM crm.customer WHERE id = @CustomerId AND status = 'active')", new { req.CustomerId }))
                    throw new KiteException(ErrorCodes.Validation, "That customer does not exist or is inactive.", 400, "customerId");
                await CreditCore.LockAsync(t, req.CustomerId);
                if (await CreditCore.AccountAsync(t, req.CustomerId) is not null) throw new KiteException(ErrorCodes.Conflict, "The customer already has a credit account.", 409, "customerId");
                var id = t.NewId();
                var uid = http.User.UserIdOf();
                await t.ExecuteAsync("""
                    INSERT INTO crm.credit_account (id, business_id, customer_id, credit_limit, terms_days, offline_sale_cap, note, approved_by, created_by, origin_server_id)
                    VALUES (@id, (SELECT id FROM core.business LIMIT 1), @CustomerId, @CreditLimit, @terms, @OfflineSaleCap, @note, @uid, @uid, (SELECT id FROM core.server WHERE is_self))
                    """, new { id, req.CustomerId, req.CreditLimit, terms = req.TermsDays ?? policy.DefaultTermsDays, req.OfflineSaleCap, note = Clean(req.Note), uid });
                await SetAllowancesAsync(t, id, req.Allowances, uid);
                await t.AuditAsync(new AuditEntry("credit.account_opened", "credit_account", id, After: Json.Obj(req)));
                await t.PublishAsync("crm.credit_account_opened", "credit_account", id, 1, Json.Obj(new { req.CustomerId, req.CreditLimit, req.TermsDays }));
                return new { id };
            }, ct))).RequirePermission(Permissions.CreditManage).RequireModule("credit");

        g.MapPut("/accounts/{customerId:guid}", async (Guid customerId, CreditAccountRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                HeadOffice(t, "Credit limits and terms are changed");
                var policy = await CreditCore.PolicyAsync(t);
                Check(req, policy);
                if (req.Status is not (null or "active" or "closed")) throw new KiteException(ErrorCodes.Validation, "An account is active or closed (holds have their own actions).", 400, "status");
                await CreditCore.LockAsync(t, customerId);
                var acc = await CreditCore.AccountAsync(t, customerId) ?? throw new KiteException(ErrorCodes.NotFound, "That customer has no credit account.", 404);
                if (req.Version != acc.Version) throw new KiteException(ErrorCodes.VersionMismatch, "Someone else changed this account. Reload and try again.", 412, "version");
                var status = req.Status is null || acc.Status == "hold" ? acc.Status : req.Status;
                var uid = http.User.UserIdOf();
                // Allowances first: a lower limit must not be refused because the old allowances are still there.
                await SetAllowancesAsync(t, acc.Id, req.Allowances, uid);
                await t.ExecuteAsync("""
                    UPDATE crm.credit_account SET credit_limit = @CreditLimit, terms_days = @terms, offline_sale_cap = @OfflineSaleCap, note = @note, status = @status,
                           approved_by = CASE WHEN credit_limit <> @CreditLimit THEN @uid ELSE approved_by END, updated_by = @uid
                     WHERE id = @id
                    """, new { id = acc.Id, req.CreditLimit, terms = req.TermsDays ?? acc.TermsDays, req.OfflineSaleCap, note = Clean(req.Note), status, uid });
                await t.AuditAsync(new AuditEntry("credit.account_changed", "credit_account", acc.Id,
                    Json.Obj(new { acc.CreditLimit, acc.TermsDays, acc.OfflineSaleCap, acc.Status }), Json.Obj(req)));
                await t.PublishAsync("crm.credit_account_changed", "credit_account", acc.Id, acc.Version + 1, Json.Obj(new { customerId, req.CreditLimit, req.TermsDays, status }));
                return 0;
            }, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.CreditManage).RequireModule("credit");

        g.MapPost("/accounts/{customerId:guid}/hold", async (Guid customerId, CreditHoldRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            var reason = Clean(req.Reason) ?? throw new KiteException(ErrorCodes.Validation, "Give the reason for the hold.", 400, "reason");
            await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                HeadOffice(t, "Accounts are put on hold");
                await CreditCore.LockAsync(t, customerId);
                var acc = await CreditCore.AccountAsync(t, customerId) ?? throw new KiteException(ErrorCodes.NotFound, "That customer has no credit account.", 404);
                if (acc.Status != "active") throw new KiteException(ErrorCodes.CreditHold, $"The account is {acc.Status}.", 409);
                await CreditCore.HoldAsync(t, acc, "manual", reason, await CreditCore.ExposureAsync(t, customerId));
                return 0;
            }, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.CreditManage).RequireModule("credit");

        g.MapPost("/accounts/{customerId:guid}/release", async (Guid customerId, CreditHoldRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            var reason = Clean(req.Reason) ?? throw new KiteException(ErrorCodes.Validation, "Give the reason for the release.", 400, "reason");
            await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                HeadOffice(t, "Holds are released");
                await CreditCore.LockAsync(t, customerId);
                var acc = await CreditCore.AccountAsync(t, customerId) ?? throw new KiteException(ErrorCodes.NotFound, "That customer has no credit account.", 404);
                if (acc.Status != "hold") throw new KiteException(ErrorCodes.CreditHold, "The account is not on hold.", 409);
                await CreditCore.ReleaseAsync(t, acc, reason, await CreditCore.ExposureAsync(t, customerId));
                return 0;
            }, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.CreditManage).RequireModule("credit");

        // What the till shows before a sale on account: the account, the figures and the instalment limits.
        g.MapGet("/customers/{customerId:guid}/available", async (Guid customerId, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var acc = await CreditCore.AccountAsync(t, customerId);
                var policy = await CreditCore.PolicyAsync(t);
                if (acc is null) return (object)new { hasAccount = false };
                var biz = await BusinessInfo.LoadAsync(t);
                var today = BusinessDay.Today(biz.TimeZone, t.Clock);
                decimal exposure;
                DateOnly? oldest;
                if (t.Server.IsHeadOffice)
                {
                    exposure = await CreditCore.ExposureAsync(t, customerId);
                    var st = await CreditCore.StatusAsync(t, customerId);
                    oldest = st.OldestDue is { } d ? DateOnly.FromDateTime(d) : null;
                }
                else
                {
                    exposure = acc.Exposure;
                    oldest = acc.OldestDue is { } d ? DateOnly.FromDateTime(d) : null;
                }
                var overdue = CreditMath.OverdueDays(oldest, today);
                return new
                {
                    hasAccount = true, acc.Status, acc.HoldReason, limit = acc.CreditLimit, exposure, available = CreditMath.AvailableOnline(acc.CreditLimit, exposure, 0m),
                    overdueDays = overdue, blocked = overdue > policy.BlockOverdueDays, policy.BlockOverdueDays, policy.OverrideCap, acc.TermsDays, policy.MaxInstalments,
                    offlineSaleCap = acc.OfflineSaleCap ?? policy.OfflineSaleCap, mode = t.Server.IsHeadOffice ? "head_office" : "branch_copy",
                    asOf = t.Server.IsHeadOffice ? (DateTime?)null : acc.ExposureAsOf, acc.CustomerNumber, acc.CustomerName, acc.CustomerNameAr,
                };
            }, ct))).RequirePermission(Permissions.PosSell).RequireModule("credit");

        // ---------------------------------------------------------------- payments and allocation (14.4)
        g.MapPost("/payments", async (CustomerPaymentRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => ReceiveAsync(t, http, req), ct)))
            .RequireSignedIn().RequireModule("credit");   // CREDIT.RECEIVE at the till's branch, or at the branch named for a transfer

        g.MapPost("/customers/{customerId:guid}/allocate", async (Guid customerId, ManualAllocationRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                HeadOffice(t, "Payments are allocated by hand");
                if (req.Allocations is not { Count: > 0 }) throw new KiteException(ErrorCodes.Validation, "Choose the instalments to settle.", 400, "allocations");
                await CreditCore.LockAsync(t, customerId);
                var biz = await BusinessInfo.LoadAsync(t);
                var n = await CreditCore.AllocateAsync(t, biz.Id, customerId, OwnerBranch(t), BusinessDay.Today(biz.TimeZone, t.Clock), "credit_allocation", t.NewId(),
                    req.Allocations.Select(a => (req.CreditEntryId, a.ItemId, a.Amount)).ToList());
                await t.AuditAsync(new AuditEntry("credit.allocated", "customer", customerId, After: Json.Obj(req)));
                return new { allocations = n };
            }, ct))).RequirePermission(Permissions.CreditManage).RequireModule("credit");

        // ---------------------------------------------------------------- write-off and opening balances (14.5, 14.6)
        g.MapPost("/customers/{customerId:guid}/write-off", async (Guid customerId, WriteOffRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => WriteOffAsync(t, http, customerId, req), ct)))
            .RequirePermission(Permissions.CreditWriteOff).RequireModule("credit");

        g.MapPost("/customers/{customerId:guid}/opening", async (Guid customerId, CustomerOpeningRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => OpeningAsync(t, http, customerId, req), ct)))
            .RequirePermission(Permissions.CreditManage).RequireModule("credit");

        // ---------------------------------------------------------------- statements (14.5)
        g.MapGet("/customers/{customerId:guid}/statement", async (Guid customerId, DateOnly? from, DateOnly? to, Guid? branchId, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => StatementAsync(t, http, customerId, from, to, branchId), ct)))
            .RequirePermission(Permissions.CreditView).RequireModule("credit");
        g.MapGet("/customers/{customerId:guid}/statement.pdf", async (Guid customerId, DateOnly? from, DateOnly? to, Guid? branchId, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            var (s, biz) = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t => (await StatementAsync(t, http, customerId, from, to, branchId), await BusinessInfo.LoadAsync(t)), ct);
            return Results.File(StatementPdf(s, biz), "application/pdf", $"statement-{s.Customer.Number}-{s.From:yyyyMMdd}-{s.To:yyyyMMdd}.pdf");
        }).RequirePermission(Permissions.CreditView).RequireModule("credit");

        // ---------------------------------------------------------------- reports (14.5) and the ledger check (17)
        g.MapGet("/reports/aging", async (HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.WithAllBranchesAsync(() => t.RowsAsync($"""
                WITH items AS (
                  SELECT i.customer_id, {Today} - i.due_date AS days,
                         i.amount - coalesce((SELECT sum(a.amount) FROM crm.receivable_allocation a WHERE a.item_id = i.id), 0) AS open
                    FROM crm.receivable_item i
                ), credits AS (
                  SELECT l.customer_id, -l.amount - coalesce((SELECT sum(a.amount) FROM crm.receivable_allocation a WHERE a.credit_entry_id = l.id), 0) AS left_over
                    FROM crm.credit_ledger l WHERE l.balance_type = 'receivable' AND l.amount < 0 AND l.kind <> 'transfer'
                ), per AS (
                  SELECT customer_id,
                         coalesce(sum(open) FILTER (WHERE days <= 0), 0) AS current, coalesce(sum(open) FILTER (WHERE days BETWEEN 1 AND 30), 0) AS d1_30,
                         coalesce(sum(open) FILTER (WHERE days BETWEEN 31 AND 60), 0) AS d31_60, coalesce(sum(open) FILTER (WHERE days BETWEEN 61 AND 90), 0) AS d61_90,
                         coalesce(sum(open) FILTER (WHERE days > 90), 0) AS d90plus
                    FROM items WHERE open > 0 GROUP BY customer_id
                )
                SELECT c.id AS "customerId", c.number AS "customerNumber", c.name_en AS "nameEn", c.name_ar AS "nameAr", a.status, a.credit_limit AS "creditLimit",
                       coalesce(p.current, 0) AS current, coalesce(p.d1_30, 0) AS "d1_30", coalesce(p.d31_60, 0) AS "d31_60", coalesce(p.d61_90, 0) AS "d61_90",
                       coalesce(p.d90plus, 0) AS "d90plus", coalesce((SELECT sum(left_over) FROM credits x WHERE x.customer_id = c.id AND x.left_over > 0), 0) AS "onAccount",
                       (SELECT coalesce(sum(amount), 0) FROM crm.credit_ledger WHERE customer_id = c.id AND balance_type = 'receivable') AS total
                  FROM crm.customer c JOIN crm.credit_account a ON a.customer_id = c.id LEFT JOIN per p ON p.customer_id = c.id
                 WHERE p.customer_id IS NOT NULL OR EXISTS (SELECT 1 FROM crm.credit_ledger l WHERE l.customer_id = c.id AND l.balance_type = 'receivable')
                 ORDER BY coalesce(p.d90plus, 0) + coalesce(p.d61_90, 0) DESC, c.name_en
                """)), ct))).RequirePermission(Permissions.CreditView).RequireModule("credit");

        g.MapGet("/reports/exposure", async (HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.WithAllBranchesAsync(() => t.RowsAsync($"""
                SELECT b.id AS "branchId", b.code, b.name_en AS "nameEn", b.name_ar AS "nameAr",
                       coalesce((SELECT sum(l.amount) FROM crm.credit_ledger l WHERE l.branch_id = b.id AND l.balance_type = 'receivable'), 0) AS balance,
                       coalesce((SELECT sum(i.amount - coalesce((SELECT sum(a.amount) FROM crm.receivable_allocation a WHERE a.item_id = i.id), 0))
                                   FROM crm.receivable_item i WHERE i.branch_id = b.id AND i.due_date < {Today}), 0) AS overdue,
                       (SELECT count(DISTINCT l.customer_id) FROM crm.credit_ledger l WHERE l.branch_id = b.id AND l.balance_type = 'receivable') AS customers,
                       coalesce((SELECT sum(l.amount) FROM crm.credit_ledger l WHERE l.branch_id = b.id AND l.kind = 'charge' AND l.doc_date >= date_trunc('month', {Today})), 0) AS "chargedThisMonth",
                       coalesce((SELECT -sum(l.amount) FROM crm.credit_ledger l WHERE l.branch_id = b.id AND l.kind = 'payment' AND l.doc_date >= date_trunc('month', {Today})), 0) AS "receivedThisMonth",
                       coalesce((SELECT sum(w.amount) FROM crm.credit_allowance w WHERE w.branch_id = b.id), 0) AS allowances
                  FROM core.branch b WHERE b.status = 'active' ORDER BY b.code
                """)), ct))).RequirePermission(Permissions.CreditView).RequireModule("credit");

        g.MapGet("/reports/overrides", async (DateOnly? from, DateOnly? to, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t => new
            {
                decisions = await t.WithAllBranchesAsync(() => t.RowsAsync("""
                    SELECT d.id, d.created_at AS "createdAt", d.mode, d.amount, d.available, d.over_by AS "overBy", d.overdue_days AS "overdueDays", d.credit_limit AS "creditLimit",
                           d.exposure, d.allowance, b.code AS "branchCode", c.number AS "customerNumber", c.name_en AS "nameEn", c.name_ar AS "nameAr", l.number AS "invoiceNumber",
                           u.display_name_en AS "approvedBy", u.display_name_ar AS "approvedByAr"
                      FROM crm.credit_decision d JOIN core.branch b ON b.id = d.branch_id JOIN crm.customer c ON c.id = d.customer_id
                      JOIN crm.credit_ledger l ON l.id = d.entry_id LEFT JOIN iam."user" u ON u.id = d.approved_by
                     WHERE (d.approved_by IS NOT NULL OR d.mode = 'offline') AND d.created_at::date BETWEEN @f AND @to
                     ORDER BY d.created_at DESC LIMIT 1000
                    """, new { f = (from ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-90))).ToDateTime(TimeOnly.MinValue), to = (to ?? DateOnly.FromDateTime(DateTime.UtcNow)).ToDateTime(TimeOnly.MinValue) })),
                breaches = await t.WithAllBranchesAsync(() => t.RowsAsync("""
                    SELECT r.id, r.detected_at AS "detectedAt", r.exposure, r.credit_limit AS "creditLimit", r.entries, b.code AS "branchCode", c.number AS "customerNumber",
                           c.name_en AS "nameEn", c.name_ar AS "nameAr"
                      FROM crm.credit_breach r JOIN crm.customer c ON c.id = r.customer_id LEFT JOIN core.branch b ON b.id = r.branch_id
                     ORDER BY r.detected_at DESC LIMIT 500
                    """)),
            }, ct))).RequirePermission(Permissions.CreditView).RequireModule("credit");

        g.MapGet("/reconciliation", async (HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.WithAllBranchesAsync(() => t.RowsAsync("""
                SELECT s.ledger, s.branch_id AS "branchId", b.code AS "branchCode", s.as_of AS "asOf", s.subledger, s.control, s.difference, s.checked_at AS "checkedAt"
                  FROM acct.subledger_check s LEFT JOIN core.branch b ON b.id = s.branch_id
                 WHERE s.run_id = (SELECT run_id FROM acct.subledger_check ORDER BY checked_at DESC LIMIT 1)
                 ORDER BY s.ledger, b.code
                """)), ct))).RequirePermission(Permissions.CreditView);
        g.MapPost("/reconciliation/run", async (HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(AccessScope.System, http.ActorOf(), t => Reconciliation.RunAsync(t), ct)))
            .RequirePermission(Permissions.CreditView);
    }

    // ------------------------------------------------------------------ sync port: branch servers (14.2, 14.5, 16.1)
    public static void MapSync(RouteGroupBuilder sync)
    {
        sync.MapPost("/credit/authorise", async (CreditAuthoriseRequest req, HttpContext http, KiteDb db, ServerContext server, PkiService pki, CancellationToken ct) =>
        {
            var who = await Who(http, server, pki, ct);
            if (req.BranchId != who.BranchId) throw new KiteException(ErrorCodes.Forbidden, "A branch server asks for its own branch.", 403);
            return Results.Ok(await db.InTransactionAsync(AccessScope.System, ActorContext.System, async t =>
                await CreditCore.AuthoriseAsync(t, who, req, (await BusinessInfo.LoadAsync(t)).TimeZone), ct));
        }).AllowEveryone();
        sync.MapPost("/credit/release", async (CreditReleaseRequest req, HttpContext http, KiteDb db, ServerContext server, PkiService pki, CancellationToken ct) =>
        {
            var who = await Who(http, server, pki, ct);
            await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => t.ExecuteAsync(
                "UPDATE crm.credit_authorisation SET released_at = now() WHERE server_id = @s AND sale_id = @SaleId AND consumed_at IS NULL AND released_at IS NULL",
                new { s = who.ServerId, req.SaleId }), ct);
            return Results.NoContent();
        }).AllowEveryone();
        sync.MapPost("/credit/push", async (CreditPushRequest req, HttpContext http, KiteDb db, ServerContext server, PkiService pki, CancellationToken ct) =>
        {
            var who = await Who(http, server, pki, ct);
            return Results.Ok(await db.InTransactionAsync(AccessScope.System, ActorContext.System, async t =>
                await CreditCore.AcceptPushAsync(t, who, req, (await BusinessInfo.LoadAsync(t)).TimeZone), ct));
        }).AllowEveryone();
        sync.MapPost("/suppliers/push", async (SupplierPushRequest req, HttpContext http, KiteDb db, ServerContext server, PkiService pki, CancellationToken ct) =>
        {
            var who = await Who(http, server, pki, ct);
            return Results.Ok(await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => SupplierCore.AcceptPushAsync(t, who, req), ct));
        }).AllowEveryone();
    }

    private static async Task<PkiService.BranchIdentity> Who(HttpContext http, ServerContext server, PkiService pki, CancellationToken ct)
    {
        if (!server.IsConfigured || !server.IsHeadOffice) throw new KiteException(ErrorCodes.NotHeadOffice, "This is not a main server.", 404);
        return await pki.AuthenticateAsync(http.Connection.ClientCertificate ?? await http.Connection.GetClientCertificateAsync(ct), ct);
    }

    // ------------------------------------------------------------------ helpers
    private static void Check(CreditAccountRequest req, CreditPolicy policy)
    {
        if (req.CreditLimit < 0 || req.CreditLimit != decimal.Round(req.CreditLimit, 3)) throw new KiteException(ErrorCodes.Validation, "The limit is zero or more, with at most 3 decimals.", 400, "creditLimit");
        if (req.TermsDays is < 0 or > 365) throw new KiteException(ErrorCodes.Validation, "Terms are 0 to 365 days.", 400, "termsDays");
        if (req.OfflineSaleCap is < 0) throw new KiteException(ErrorCodes.Validation, "The offline cap is zero or more.", 400, "offlineSaleCap");
        foreach (var (a, i) in (req.Allowances ?? []).Select((a, i) => (a, i)))
            if (a.Amount < 0 || a.Amount != decimal.Round(a.Amount, 3)) throw new KiteException(ErrorCodes.Validation, "An allowance is zero or more.", 400, $"allowances[{i}].amount");
        if ((req.Allowances ?? []).GroupBy(a => a.BranchId).Any(x => x.Count() > 1)) throw new KiteException(ErrorCodes.Validation, "A branch appears twice.", 400, "allowances");
        if (!policy.AllowOvercommit && (req.Allowances ?? []).Sum(a => a.Amount) > req.CreditLimit)
            throw new KiteException(ErrorCodes.CreditAllowance, $"Branch allowances add up to {M((req.Allowances ?? []).Sum(a => a.Amount))}, more than the limit of {M(req.CreditLimit)}.", 409, "allowances");
    }

    private static async Task SetAllowancesAsync(DbTx t, Guid accountId, List<AllowanceInput>? list, Guid uid)
    {
        if (list is null) return;
        await t.ExecuteAsync("UPDATE crm.credit_allowance SET amount = 0, updated_by = @uid WHERE account_id = @accountId AND NOT (branch_id = ANY(@keep)) AND amount <> 0",
            new { accountId, uid, keep = list.Select(a => a.BranchId).ToArray() });
        foreach (var a in list.OrderBy(a => a.BranchId))
        {
            if (!await t.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM core.branch WHERE id = @BranchId)", new { a.BranchId }))
                throw new KiteException(ErrorCodes.Validation, "That branch does not exist.", 400, "allowances");
            await t.ExecuteAsync("""
                INSERT INTO crm.credit_allowance (id, business_id, account_id, branch_id, amount, updated_by)
                VALUES (@id, (SELECT id FROM core.business LIMIT 1), @accountId, @BranchId, @Amount, @uid)
                ON CONFLICT (account_id, branch_id) DO UPDATE SET amount = EXCLUDED.amount, updated_by = EXCLUDED.updated_by
                 WHERE crm.credit_allowance.amount <> EXCLUDED.amount
                """, new { id = t.NewId(), accountId, a.BranchId, a.Amount, uid });
        }
    }

    private static async Task<object> DetailAsync(DbTx t, Guid customerId)
    {
        var acc = await CreditCore.AccountAsync(t, customerId) ?? throw new KiteException(ErrorCodes.NotFound, "That customer has no credit account.", 404);
        var policy = await CreditCore.PolicyAsync(t);
        var biz = await BusinessInfo.LoadAsync(t);
        var today = BusinessDay.Today(biz.TimeZone, t.Clock);
        var ho = t.Server.IsHeadOffice;
        var status = await CreditCore.StatusAsync(t, customerId);
        var exposure = ho ? await CreditCore.ExposureAsync(t, customerId) : acc.Exposure;
        var oldest = ho ? status.OldestDue : acc.OldestDue;
        var overdue = CreditMath.OverdueDays(oldest is { } d ? DateOnly.FromDateTime(d) : null, today);
        return await t.WithAllBranchesAsync(async () => (object)new
        {
            account = acc, policy,
            figures = new
            {
                exposure, balance = status.Balance, openItems = status.OpenItems, onAccount = status.Unallocated, oldestDue = oldest, overdueDays = overdue,
                available = CreditMath.AvailableOnline(acc.CreditLimit, exposure, 0m), blocked = overdue > policy.BlockOverdueDays, headOffice = ho, asOf = ho ? null : acc.ExposureAsOf,
            },
            allowances = await t.RowsAsync("""
                SELECT b.id AS "branchId", b.code, b.name_en AS "nameEn", b.name_ar AS "nameAr", coalesce(w.amount, 0) AS amount
                  FROM core.branch b LEFT JOIN crm.credit_allowance w ON w.branch_id = b.id AND w.account_id = @a
                 WHERE b.status = 'active' ORDER BY b.code
                """, new { a = acc.Id }),
            openItems = await t.RowsAsync($"""
                SELECT i.id, l.number, b.code AS "branchCode", i.seq, (SELECT count(*) FROM crm.receivable_item x WHERE x.entry_id = i.entry_id) AS "of", i.due_date::text AS "dueDate", i.amount,
                       i.amount - coalesce((SELECT sum(a.amount) FROM crm.receivable_allocation a WHERE a.item_id = i.id), 0) AS open,
                       greatest({Today} - i.due_date, 0) AS "overdueDays"
                  FROM crm.receivable_item i JOIN crm.credit_ledger l ON l.id = i.entry_id JOIN core.branch b ON b.id = i.branch_id
                 WHERE i.customer_id = @customerId
                   AND i.amount > coalesce((SELECT sum(a.amount) FROM crm.receivable_allocation a WHERE a.item_id = i.id), 0)
                 ORDER BY i.due_date, l.doc_date, i.seq
                """, new { customerId }),
            credits = await t.RowsAsync("""
                SELECT l.id, l.number, l.kind, b.code AS "branchCode", l.doc_date::text AS "docDate", -l.amount AS amount,
                       -l.amount - coalesce((SELECT sum(a.amount) FROM crm.receivable_allocation a WHERE a.credit_entry_id = l.id), 0) AS "leftOver"
                  FROM crm.credit_ledger l JOIN core.branch b ON b.id = l.branch_id
                 WHERE l.customer_id = @customerId AND l.balance_type = 'receivable' AND l.amount < 0 AND l.kind <> 'transfer'
                   AND -l.amount > coalesce((SELECT sum(a.amount) FROM crm.receivable_allocation a WHERE a.credit_entry_id = l.id), 0)
                 ORDER BY l.doc_date, l.created_at
                """, new { customerId }),
            ledger = await t.RowsAsync("""
                SELECT l.id, l.doc_date::text AS "docDate", l.number, l.kind, b.code AS "branchCode", l.amount, l.note, l.created_at AS "createdAt"
                  FROM crm.credit_ledger l JOIN core.branch b ON b.id = l.branch_id
                 WHERE l.customer_id = @customerId AND l.balance_type = 'receivable'
                 ORDER BY l.doc_date DESC, l.created_at DESC LIMIT 100
                """, new { customerId }),
            holds = await t.RowsAsync("""
                SELECT h.action, h.kind, h.reason, h.exposure, h.credit_limit AS "creditLimit", h.created_at AS "createdAt", u.display_name_en AS "byName", u.display_name_ar AS "byNameAr"
                  FROM crm.credit_hold_event h LEFT JOIN iam."user" u ON u.id = h.created_by WHERE h.account_id = @a ORDER BY h.created_at DESC LIMIT 50
                """, new { a = acc.Id }),
            breaches = await t.RowsAsync("""
                SELECT r.detected_at AS "detectedAt", r.exposure, r.credit_limit AS "creditLimit", r.entries, b.code AS "branchCode"
                  FROM crm.credit_breach r LEFT JOIN core.branch b ON b.id = r.branch_id WHERE r.account_id = @a ORDER BY r.detected_at DESC LIMIT 20
                """, new { a = acc.Id }),
        });
    }

    private static async Task<object> ReceiveAsync(DbTx t, HttpContext http, CustomerPaymentRequest req)
    {
        var done = await t.QuerySingleOrDefaultAsync<ReceiptRow>(ReceiptRow.Select + " WHERE r.id = @Id", new { req.Id });
        if (done is not null) return done;   // the same request again (a retry after a lost answer)
        if (req.Method is not ("cash" or "card" or "bank_transfer")) throw new KiteException(ErrorCodes.Validation, "A payment is in cash, by card or by bank transfer.", 400, "method");
        if (req.Amount <= 0 || req.Amount != decimal.Round(req.Amount, 3)) throw new KiteException(ErrorCodes.Validation, "The amount is above zero, with at most 3 decimals.", 400, "amount");
        if (req.Method == "card" && (Clean(req.CardTerminal) is null || Clean(req.ApprovalCode) is null))
            throw new KiteException(ErrorCodes.Validation, "A card payment needs the bank terminal and the approval code from its slip.", 400, "cardTerminal");
        if (req.Method == "bank_transfer" && Clean(req.Reference) is null) throw new KiteException(ErrorCodes.Validation, "A bank transfer needs its reference.", 400, "reference");
        if (!await CreditCore.ModuleOnAsync(t)) throw new KiteException(ErrorCodes.ModuleDisabled, "Customer credit is switched off.", 409);

        // At the till (cash, card, or a transfer taken there) the payment goes on the drawer; a transfer at the office names its branch.
        Guid branchId;
        Guid? shiftId = null;
        if (req.BranchId is null || req.Method != "bank_transfer")
        {
            var term = await PosTerminals.CurrentAsync(t, http);
            http.User.Demand(Permissions.CreditReceive, term.BranchId);
            shiftId = (await PosEndpoints.OpenShiftForUpdateAsync(t, term, http)).Id;
            branchId = term.BranchId;
        }
        else
        {
            branchId = req.BranchId.Value;
            http.User.Demand(Permissions.CreditReceive, branchId);
        }
        await CreditCore.LockAsync(t, req.CustomerId);
        var acc = await CreditCore.AccountAsync(t, req.CustomerId)
            ?? throw new KiteException(ErrorCodes.CreditNoAccount, "This customer has no credit account here.", 409, "customerId");
        if (req.Allocations is { Count: > 0 } && !t.Server.IsHeadOffice)
            throw new KiteException(ErrorCodes.NotHeadOffice, "A branch server takes the payment; head office settles invoices with it (oldest first, or by hand there).", 409, "allocations");
        var biz = await BusinessInfo.LoadAsync(t);
        var now = t.Clock.UtcNow;
        var date = BusinessDay.DateOf(now, biz.TimeZone);
        var uid = http.User.UserIdOf();
        var number = await DocNumbers.NextAsync(t, biz.Id, branchId, "customer_receipt", await FiscalPeriods.FiscalYearOfAsync(t, biz.Id, date), "RC");
        var entry = t.NewId();
        var methodName = req.Method switch { "cash" => "cash", "card" => "card", _ => "bank transfer" };
        await t.ExecuteAsync("""
            INSERT INTO crm.credit_ledger (id, business_id, branch_id, customer_id, balance_type, kind, amount, number, doc_date, source_doc_type, source_doc_id, note,
                                           created_by, origin_server_id)
            VALUES (@entry, @b, @branchId, @CustomerId, 'receivable', 'payment', @neg, @number, @date::date, 'customer_receipt', @Id, @note, @uid,
                    (SELECT id FROM core.server WHERE is_self))
            """, new { entry, b = biz.Id, branchId, req.CustomerId, neg = -req.Amount, number, date = date.ToDateTime(TimeOnly.MinValue), req.Id,
                       note = $"Payment {number} ({methodName}){(Clean(req.Note) is { } n ? ": " + n : "")}", uid });
        await t.ExecuteAsync("""
            INSERT INTO crm.customer_receipt (id, business_id, branch_id, customer_id, number, method, amount, shift_id, reference, note, entry_id, received_at, created_by, origin_server_id)
            VALUES (@Id, @b, @branchId, @CustomerId, @number, @Method, @Amount, @shiftId, @reference, @note, @entry, @now, @uid, (SELECT id FROM core.server WHERE is_self))
            """, new { req.Id, b = biz.Id, branchId, req.CustomerId, number, req.Method, req.Amount, shiftId, reference = Clean(req.Reference), note = Clean(req.Note), entry, now, uid });
        if (shiftId is not null)
            await t.ExecuteAsync("""
                INSERT INTO sales.payment (id, business_id, branch_id, shift_id, method, amount, card_terminal, approval_code, reference, created_at, customer_receipt_id)
                VALUES (@pid, @b, @branchId, @shiftId, @Method, @Amount, @terminal, @approval, @reference, @now, @Id)
                """, new { pid = t.NewId(), b = biz.Id, branchId, shiftId, req.Method, req.Amount, terminal = Clean(req.CardTerminal), approval = Clean(req.ApprovalCode),
                           reference = Clean(req.Reference), now, req.Id });
        await Posting.PostAsync(t, biz.Id, branchId, PostingRuleTemplates.CreditPayment, new PostingFacts().Add("received_by_method", req.Method, req.Amount), date,
            "customer_receipt", req.Id, $"Customer payment {number}: {acc.CustomerName}", req.CustomerId);
        var allocated = await CreditCore.AllocateAsync(t, biz.Id, req.CustomerId, branchId, date, "customer_receipt", req.Id,
            req.Allocations?.Select(a => (entry, a.ItemId, a.Amount)).ToList());
        if (t.Server.IsHeadOffice) await CreditCore.EvaluateHoldAsync(t, acc, await CreditCore.PolicyAsync(t), biz.TimeZone, afterSync: false);
        await t.AuditAsync(new AuditEntry("credit.payment", "customer_receipt", req.Id, After: Json.Obj(new { number, req.CustomerId, req.Method, req.Amount, allocated }), BranchId: branchId));
        await t.PublishAsync("crm.customer_payment", "customer_receipt", req.Id, 1, Json.Obj(new { number, req.CustomerId, req.Method, req.Amount }), branchId);
        return (await t.QuerySingleOrDefaultAsync<ReceiptRow>(ReceiptRow.Select + " WHERE r.id = @Id", new { req.Id }))!;
    }

    public sealed class ReceiptRow
    {
        public Guid Id { get; set; }
        public string Number { get; set; } = "";
        public string Method { get; set; } = "";
        public decimal Amount { get; set; }
        public Guid CustomerId { get; set; }
        public string BranchCode { get; set; } = "";
        public DateTime ReceivedAt { get; set; }
        public decimal Balance { get; set; }
        public decimal Allocated { get; set; }
        public const string Select = """
            SELECT r.id, r.number, r.method, r.amount, r.customer_id AS CustomerId, b.code AS BranchCode, r.received_at AS ReceivedAt,
                   (SELECT coalesce(sum(amount), 0) FROM crm.credit_ledger WHERE customer_id = r.customer_id AND balance_type = 'receivable') AS Balance,
                   coalesce((SELECT sum(a.amount) FROM crm.receivable_allocation a WHERE a.credit_entry_id = r.entry_id), 0) AS Allocated
              FROM crm.customer_receipt r JOIN core.branch b ON b.id = r.branch_id
            """;
    }

    private static async Task<object> WriteOffAsync(DbTx t, HttpContext http, Guid customerId, WriteOffRequest req)
    {
        HeadOffice(t, "Debts are written off");
        var reason = Clean(req.Reason) ?? throw new KiteException(ErrorCodes.Validation, "Give the reason for the write-off.", 400, "reason");
        if (req.Amount is <= 0) throw new KiteException(ErrorCodes.Validation, "The amount is above zero.", 400, "amount");
        await CreditCore.LockAsync(t, customerId);
        var acc = await CreditCore.AccountAsync(t, customerId) ?? throw new KiteException(ErrorCodes.NotFound, "That customer has no credit account.", 404);
        var biz = await BusinessInfo.LoadAsync(t);
        var date = BusinessDay.Today(biz.TimeZone, t.Clock);
        var owner = OwnerBranch(t);
        var uid = http.User.UserIdOf();
        return await t.WithAllBranchesAsync(async () =>
        {
            var open = (await t.QueryAsync<(Guid Id, Guid BranchId, DateTime Due, decimal Open)>("""
                SELECT i.id, i.branch_id, i.due_date, i.amount - coalesce((SELECT sum(a.amount) FROM crm.receivable_allocation a WHERE a.item_id = i.id), 0)
                  FROM crm.receivable_item i JOIN crm.credit_ledger l ON l.id = i.entry_id WHERE i.customer_id = @customerId ORDER BY i.due_date, l.doc_date, i.seq
                """, new { customerId })).Where(i => i.Open > 0).ToList();
            if (req.ItemIds is { Count: > 0 })
            {
                if (req.ItemIds.Any(id => open.All(o => o.Id != id))) throw new KiteException(ErrorCodes.CreditAllocation, "An instalment chosen is not open.", 409, "itemIds");
                open = open.Where(o => req.ItemIds.Contains(o.Id)).ToList();
            }
            var (picks, left) = CreditMath.AllocateOldestFirst(open.Select(o => new OpenItem(o.Id, DateOnly.FromDateTime(o.Due), o.Open)).ToList(), req.Amount ?? open.Sum(o => o.Open));
            if (picks.Count == 0) throw new KiteException(ErrorCodes.CreditAllocation, "Nothing is open to write off.", 409);
            if (req.Amount is not null && left > 0) throw new KiteException(ErrorCodes.CreditAllocation, $"Only {M(req.Amount.Value - left)} is open on what was chosen.", 409, "amount");
            var id = t.NewId();
            var number = await DocNumbers.NextAsync(t, biz.Id, owner, "credit_write_off", await FiscalPeriods.FiscalYearOfAsync(t, biz.Id, date), "WO");
            var byBranch = picks.GroupBy(p => open.First(o => o.Id == p.ItemId).BranchId).OrderBy(g => g.Key).ToList();
            var manual = new List<(Guid, Guid, decimal)>();
            foreach (var grp in byBranch)
            {
                var entry = t.NewId();
                await t.ExecuteAsync("""
                    INSERT INTO crm.credit_ledger (id, business_id, branch_id, customer_id, balance_type, kind, amount, number, doc_date, source_doc_type, source_doc_id, note,
                                                   created_by, origin_server_id)
                    VALUES (@entry, @b, @branch, @customerId, 'receivable', 'write_off', @neg, @number, @date::date, 'credit_write_off', @id, @note, @uid,
                            (SELECT id FROM core.server WHERE is_self))
                    """, new { entry, b = biz.Id, branch = grp.Key, customerId, neg = -grp.Sum(p => p.Amount), number, date = date.ToDateTime(TimeOnly.MinValue), id,
                               note = $"Written off: {reason}", uid });
                manual.AddRange(grp.Select(p => (entry, p.ItemId, p.Amount)));
            }
            await CreditCore.AllocateAsync(t, biz.Id, customerId, owner, date, "credit_write_off", id, manual);
            var total = picks.Sum(p => p.Amount);
            await Posting.PostManyAsync(t, biz.Id, owner, PostingRuleTemplates.CreditWriteOff,
                byBranch.Select(gr => (PostingRuleTemplates.CreditWriteOff, gr.Key, new PostingFacts().Set("amount", gr.Sum(p => p.Amount)))).ToList(),
                date, "credit_write_off", id, $"Bad debt written off {number}: {acc.CustomerName}", customerId);
            await CreditCore.EvaluateHoldAsync(t, acc, await CreditCore.PolicyAsync(t), biz.TimeZone, afterSync: false);
            await t.AuditAsync(new AuditEntry("credit.write_off", "customer", customerId, After: Json.Obj(new { number, total, reason, items = picks.Count })));
            await t.PublishAsync("crm.credit_write_off", "customer", customerId, 1, Json.Obj(new { number, total, reason }));
            return (object)new { id, number, total };
        });
    }

    private static async Task<object> OpeningAsync(DbTx t, HttpContext http, Guid customerId, CustomerOpeningRequest req)
    {
        HeadOffice(t, "Opening balances are entered");
        if (req.Amount == 0 || req.Amount != decimal.Round(req.Amount, 3)) throw new KiteException(ErrorCodes.Validation, "The amount is not zero, with at most 3 decimals.", 400, "amount");
        await CreditCore.LockAsync(t, customerId);
        var acc = await CreditCore.AccountAsync(t, customerId) ?? throw new KiteException(ErrorCodes.NotFound, "Open a credit account for the customer first.", 404);
        var biz = await BusinessInfo.LoadAsync(t);
        var date = req.Date ?? BusinessDay.Today(biz.TimeZone, t.Clock);
        var uid = http.User.UserIdOf();
        var id = t.NewId();
        return await t.WithAllBranchesAsync(async () =>
        {
            var number = await DocNumbers.NextAsync(t, biz.Id, req.BranchId, "opening_balance", await FiscalPeriods.FiscalYearOfAsync(t, biz.Id, date), "OB");
            await t.ExecuteAsync("""
                INSERT INTO crm.credit_ledger (id, business_id, branch_id, customer_id, balance_type, kind, amount, number, doc_date, source_doc_type, source_doc_id, note,
                                               created_by, origin_server_id)
                VALUES (@id, @b, @BranchId, @customerId, 'receivable', 'opening', @Amount, @number, @date::date, 'opening_balance', @id, @note, @uid,
                        (SELECT id FROM core.server WHERE is_self))
                """, new { id, b = biz.Id, req.BranchId, customerId, req.Amount, number, date = date.ToDateTime(TimeOnly.MinValue), note = Clean(req.Note) ?? "Opening balance", uid });
            if (req.Amount > 0)
                await t.ExecuteAsync("""
                    INSERT INTO crm.receivable_item (id, business_id, branch_id, customer_id, entry_id, seq, due_date, amount, origin_server_id)
                    VALUES (@iid, @b, @BranchId, @customerId, @id, 1, @due::date, @Amount, (SELECT id FROM core.server WHERE is_self))
                    """, new { iid = t.NewId(), b = biz.Id, req.BranchId, customerId, id, due = date.AddDays(acc.TermsDays).ToDateTime(TimeOnly.MinValue), req.Amount });
            await Posting.PostAsync(t, biz.Id, req.BranchId, PostingRuleTemplates.CreditOpening, new PostingFacts().Set("amount", req.Amount), date, "opening_balance", id,
                $"Customer opening balance {number}: {acc.CustomerName}", customerId);
            await CreditCore.AllocateAsync(t, biz.Id, customerId, OwnerBranch(t), date, "opening_balance", id);
            await t.AuditAsync(new AuditEntry("credit.opening_balance", "customer", customerId, After: Json.Obj(new { number, req.Amount, date }), BranchId: req.BranchId));
            return (object)new { id, number };
        });
    }

    // ------------------------------------------------------------------ statements
    public sealed record StatementCustomer(Guid Id, string Number, string NameEn, string? NameAr, string? Phone, string? VatNumber);
    public sealed record StatementLine(DateOnly Date, string Number, string Kind, string BranchCode, string? Note, decimal Debit, decimal Credit, decimal Balance);
    public sealed record Statement(StatementCustomer Customer, DateOnly From, DateOnly To, Guid? BranchId, string? BranchCode, decimal Opening, IReadOnlyList<StatementLine> Lines,
        decimal Debits, decimal Credits, decimal Closing, AgingBuckets? Aging, decimal? CreditLimit, DateTimeOffset GeneratedAt, bool HeadOffice);

    /// <summary>
    /// The customer's statement for any period (14.5): across every branch (transfers between branches leave it out, they net
    /// to nothing) or for one branch (with the transfers that moved its invoices and payments). Closing = opening + lines.
    /// </summary>
    public static async Task<Statement> StatementAsync(DbTx t, HttpContext http, Guid customerId, DateOnly? from, DateOnly? to, Guid? branchId)
    {
        if (branchId is null) http.User.Demand(Permissions.CreditView, null);
        else http.User.Demand(Permissions.CreditView, branchId);
        var biz = await BusinessInfo.LoadAsync(t);
        var today = BusinessDay.Today(biz.TimeZone, t.Clock);
        var end = to ?? today;
        var start = from ?? new DateOnly(end.Year, end.Month, 1).AddMonths(-2);
        if (start > end) throw new KiteException(ErrorCodes.Validation, "The period starts after it ends.", 400, "from");
        return await t.WithAllBranchesAsync(async () =>
        {
            var c = await t.QuerySingleOrDefaultAsync<StatementCustomer>(
                "SELECT id, number, name_en AS NameEn, name_ar AS NameAr, phone_e164 AS Phone, vat_number AS VatNumber FROM crm.customer WHERE id = @customerId", new { customerId })
                ?? throw new KiteException(ErrorCodes.NotFound, "That customer was not found.", 404);
            var branchCode = branchId is null ? null : await t.ExecuteScalarAsync<string>("SELECT code FROM core.branch WHERE id = @branchId", new { branchId });
            var filter = branchId is null ? "AND l.kind <> 'transfer'" : "AND l.branch_id = @branchId";
            var opening = await t.ExecuteScalarAsync<decimal>($"""
                SELECT coalesce(sum(l.amount), 0) FROM crm.credit_ledger l
                 WHERE l.customer_id = @customerId AND l.balance_type = 'receivable' AND l.doc_date < @start {filter}
                """, new { customerId, start = start.ToDateTime(TimeOnly.MinValue), branchId });
            var rows = await t.QueryAsync<(DateTime Date, string Number, string Kind, string Code, string? Note, decimal Amount)>($"""
                SELECT l.doc_date, l.number, l.kind, b.code, l.note, l.amount FROM crm.credit_ledger l JOIN core.branch b ON b.id = l.branch_id
                 WHERE l.customer_id = @customerId AND l.balance_type = 'receivable' AND l.doc_date BETWEEN @start AND @end {filter}
                 ORDER BY l.doc_date, l.created_at, l.id
                """, new { customerId, start = start.ToDateTime(TimeOnly.MinValue), end = end.ToDateTime(TimeOnly.MinValue), branchId });
            var balance = opening;
            var lines = new List<StatementLine>();
            foreach (var r in rows)
            {
                balance += r.Amount;
                lines.Add(new StatementLine(DateOnly.FromDateTime(r.Date), r.Number, r.Kind, r.Code, r.Note, r.Amount > 0 ? r.Amount : 0m, r.Amount < 0 ? -r.Amount : 0m, balance));
            }
            AgingBuckets? aging = null;
            if (branchId is null && end == today)
            {
                var items = (await t.QueryAsync<(DateTime Due, decimal Open)>("""
                    SELECT i.due_date, i.amount - coalesce((SELECT sum(a.amount) FROM crm.receivable_allocation a WHERE a.item_id = i.id), 0)
                      FROM crm.receivable_item i WHERE i.customer_id = @customerId
                    """, new { customerId })).Where(i => i.Open > 0).Select(i => (DateOnly.FromDateTime(i.Due), i.Open)).ToList();
                aging = CreditMath.Aging(items, (await CreditCore.StatusAsync(t, customerId)).Unallocated, today);
            }
            var limit = await t.ExecuteScalarAsync<decimal?>("SELECT credit_limit FROM crm.credit_account WHERE customer_id = @customerId", new { customerId });
            return new Statement(c, start, end, branchId, branchCode, opening, lines, lines.Sum(l => l.Debit), lines.Sum(l => l.Credit), balance, aging, limit,
                t.Clock.UtcNow, t.Server.IsHeadOffice);
        });
    }

    private static readonly string[] Fonts = ["Segoe UI", "Tahoma", "DejaVu Sans", "Noto Sans Arabic"];
    private static readonly Dictionary<string, (string En, string Ar)> Kinds = new()
    {
        ["charge"] = ("Invoice", "فاتورة"), ["payment"] = ("Payment", "دفعة"), ["credit_note"] = ("Credit note", "إشعار دائن"), ["write_off"] = ("Written off", "شطب"),
        ["opening"] = ("Opening balance", "رصيد افتتاحي"), ["transfer"] = ("Between branches", "بين الفروع"),
    };

    public static byte[] StatementPdf(Statement s, BusinessInfo biz) => Document.Create(doc => doc.Page(page =>
    {
        page.Size(PageSizes.A4);
        page.Margin(28);
        page.DefaultTextStyle(x => x.FontSize(8.5f).FontFamily(Fonts));
        page.Header().Column(h =>
        {
            h.Item().Row(r =>
            {
                r.RelativeItem().Column(c =>
                {
                    c.Item().Text(biz.SellerNameEn).Bold().FontSize(12);
                    if (biz.VatNumber is { } vat) c.Item().Text($"VAT {vat}");
                    c.Item().Text("Customer statement").FontSize(14).Bold();
                });
                r.RelativeItem().AlignRight().Column(c =>
                {
                    c.Item().AlignRight().ContentFromRightToLeft().Text(biz.SellerNameAr).Bold().FontSize(12);
                    c.Item().AlignRight().ContentFromRightToLeft().Text("كشف حساب عميل").FontSize(14).Bold();
                });
            });
            h.Item().PaddingTop(6).Row(r =>
            {
                r.RelativeItem().Column(c =>
                {
                    c.Item().Text($"{s.Customer.NameEn} · {s.Customer.Number}").SemiBold();
                    if (s.Customer.Phone is { } ph) c.Item().Text(ph);
                    if (s.Customer.VatNumber is { } cv) c.Item().Text($"VAT {cv}");
                });
                r.RelativeItem().AlignRight().Column(c =>
                {
                    if (s.Customer.NameAr is { } ar) c.Item().AlignRight().ContentFromRightToLeft().Text(ar).SemiBold();
                    c.Item().AlignRight().Text($"{s.From:yyyy-MM-dd} – {s.To:yyyy-MM-dd}");
                    c.Item().AlignRight().Text(s.BranchCode is null ? "All branches / جميع الفروع" : $"Branch {s.BranchCode} / الفرع");
                    if (s.CreditLimit is { } lim) c.Item().AlignRight().Text($"Credit limit / حد الائتمان: OMR {M(lim)}");
                });
            });
        });
        page.Content().PaddingTop(10).Column(col =>
        {
            col.Item().Table(tb =>
            {
                tb.ColumnsDefinition(cd => { cd.ConstantColumn(62); cd.RelativeColumn(2); cd.RelativeColumn(2); cd.ConstantColumn(40); cd.RelativeColumn(1.2f); cd.RelativeColumn(1.2f); cd.RelativeColumn(1.3f); });
                void H(string en, string ar, bool right = false) => tb.Cell().BorderBottom(1).PaddingBottom(2).Element(e => right ? e.AlignRight() : e).Text($"{en}\n{ar}").SemiBold();
                H("Date", "التاريخ"); H("Document", "المستند"); H("Type", "النوع"); H("Branch", "الفرع"); H("Debit", "مدين", true); H("Credit", "دائن", true); H("Balance", "الرصيد", true);
                tb.Cell().ColumnSpan(6).PaddingVertical(2).Text("Opening balance / الرصيد الافتتاحي").Italic();
                tb.Cell().AlignRight().PaddingVertical(2).Text(M(s.Opening)).Italic();
                foreach (var l in s.Lines)
                {
                    var k = Kinds.TryGetValue(l.Kind, out var n) ? n : (l.Kind, l.Kind);
                    tb.Cell().PaddingVertical(1).Text(l.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    tb.Cell().PaddingVertical(1).Text(l.Number);
                    tb.Cell().PaddingVertical(1).Text($"{k.Item1} / {k.Item2}");
                    tb.Cell().PaddingVertical(1).Text(l.BranchCode);
                    tb.Cell().PaddingVertical(1).AlignRight().Text(l.Debit == 0 ? "" : M(l.Debit));
                    tb.Cell().PaddingVertical(1).AlignRight().Text(l.Credit == 0 ? "" : M(l.Credit));
                    tb.Cell().PaddingVertical(1).AlignRight().Text(M(l.Balance));
                }
                tb.Cell().ColumnSpan(4).BorderTop(1).PaddingTop(2).Text("Totals / الإجمالي").SemiBold();
                tb.Cell().BorderTop(1).PaddingTop(2).AlignRight().Text(M(s.Debits)).SemiBold();
                tb.Cell().BorderTop(1).PaddingTop(2).AlignRight().Text(M(s.Credits)).SemiBold();
                tb.Cell().BorderTop(1).PaddingTop(2).AlignRight().Text(M(s.Closing)).Bold();
            });
            col.Item().PaddingTop(8).Text($"Closing balance / الرصيد الختامي: OMR {M(s.Closing)}").Bold().FontSize(10);
            if (s.Aging is { } a)
            {
                col.Item().PaddingTop(10).Text("Ageing (days past due) / أعمار الديون (أيام التأخير)").SemiBold();
                col.Item().Table(tb =>
                {
                    tb.ColumnsDefinition(cd => { for (var i = 0; i < 7; i++) cd.RelativeColumn(); });
                    foreach (var h in new[] { "Not due / غير مستحق", "1–30", "31–60", "61–90", "90+", "On account / على الحساب", "Balance / الرصيد" })
                        tb.Cell().BorderBottom(1).Text(h).SemiBold();
                    foreach (var v in new[] { a.Current, a.D1To30, a.D31To60, a.D61To90, a.D90Plus, -a.OnAccount, a.Total }) tb.Cell().Text(M(v));
                });
            }
            if (!s.HeadOffice) col.Item().PaddingTop(8).Text("Printed at a branch: this branch's records only; head office prints the full statement. / طُبع في فرع: سجلات هذا الفرع فقط.").Italic().FontSize(7.5f);
        });
        page.Footer().Row(r =>
        {
            r.RelativeItem().Text($"{s.Customer.Number} · {BusinessDay.Local(s.GeneratedAt, biz.TimeZone):yyyy-MM-dd HH:mm}").FontSize(7);
            r.RelativeItem().AlignRight().Text(x => { x.CurrentPageNumber().FontSize(7); x.Span(" / ").FontSize(7); x.TotalPages().FontSize(7); });
        });
    })).GeneratePdf();
}
