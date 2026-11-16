using System.Text.Json;
using Kite.Host.Infrastructure;
using Kite.Persistence;
using Kite.Platform;
using Kite.Sales;

namespace Kite.Host.Features;

public sealed record CommissionSettingsRequest(string GenerateOn, string ApprovalBy, int ClawbackDays, decimal ClawbackPercent, int Version);
public sealed record CommissionRuleRequest(string NameEn, string NameAr, string Basis, decimal? Amount, decimal? Rate, decimal? MinAmount, decimal? MaxAmount,
    Guid? TechnicianId, Guid? BranchId, Guid? CategoryId, DateOnly EffectiveFrom, DateOnly? EffectiveTo = null);
public sealed record CommissionRuleEndRequest(DateOnly EffectiveTo);
public sealed record JobSharesRequest(List<JobShareInput> Shares);
public sealed record JobShareInput(Guid TechnicianId, decimal Share);
public sealed record AccrualIdsRequest(List<Guid> Ids);
public sealed record AccrualReverseRequest(string Reason);
public sealed record PayoutRequest(Guid BranchId, Guid TechnicianId, List<Guid> AccrualIds, string Method, string? Reference = null, string? Note = null);
public sealed record GenerateRequest(Guid? JobId = null);

public static class CommissionEndpoints
{
    private const string AccrualList = """
        SELECT a.id, a.job_id AS "jobId", j.number AS "jobNumber", a.branch_id AS "branchId", b.code AS "branchCode", a.technician_id AS "technicianId",
               u.display_name_en AS "technicianName", a.kind, a.kind_version AS "kindVersion", a.amount, a.share, a.basis, a.basis_amounts AS "basisAmounts",
               a.trace, a.rule_key AS "ruleKey", a.rule_version AS "ruleVersion", r.name_en AS "ruleNameEn", r.name_ar AS "ruleNameAr", s.number AS "invoiceNumber",
               a.accrual_date AS "accrualDate", a.status, a.reverses_id AS "reversesId", a.reason, ap.display_name_en AS "approvedBy", a.approved_at AS "approvedAt",
               p.number AS "payoutNumber", a.created_at AS "createdAt"
          FROM comm.accrual a JOIN repair.repair_job j ON j.id = a.job_id JOIN core.branch b ON b.id = a.branch_id JOIN iam."user" u ON u.id = a.technician_id
          LEFT JOIN comm.rule r ON r.id = a.rule_id LEFT JOIN sales.sale s ON s.id = a.sale_id LEFT JOIN iam."user" ap ON ap.id = a.approved_by
          LEFT JOIN comm.payout p ON p.id = a.payout_id
        """;

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/commissions");

        // ---------------------------------------------------------------- settings (15.2, 15.3, 15.4)
        g.MapGet("/settings", async (HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), Commissions.SettingsAsync, ct)))
            .RequirePermission(Permissions.CommView).RequireModule("comm");
        g.MapPut("/settings", async (CommissionSettingsRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            if (req.GenerateOn is not ("delivered" or "paid")) throw new KiteException(ErrorCodes.Validation, "Commissions are generated at delivery or when the invoice is paid.", 400, "generateOn");
            if (req.ApprovalBy is not ("branch" or "head_office")) throw new KiteException(ErrorCodes.Validation, "Commissions are approved at the branch or at head office.", 400, "approvalBy");
            if (req.ClawbackDays is < 0 or > 365) throw new KiteException(ErrorCodes.Validation, "The rework window is 0 to 365 days (0: no clawback).", 400, "clawbackDays");
            if (req.ClawbackPercent is <= 0 or > 100) throw new KiteException(ErrorCodes.Validation, "The clawback is above 0% and at most 100%.", 400, "clawbackPercent");
            await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var before = await Commissions.SettingsAsync(t);
                var n = await t.ExecuteAsync("""
                    UPDATE comm.settings SET generate_on = @GenerateOn, approval_by = @ApprovalBy, clawback_days = @ClawbackDays, clawback_percent = @ClawbackPercent,
                           version = version + 1, updated_at = now(), updated_by = @uid
                     WHERE version = @Version
                    """, new { req.GenerateOn, req.ApprovalBy, req.ClawbackDays, req.ClawbackPercent, req.Version, uid = http.User.UserIdOf() });
                if (n == 0) throw new KiteException(ErrorCodes.VersionMismatch, "Someone else changed the commission settings. Reload and try again.", 412, "version");
                await t.AuditAsync(new AuditEntry("comm.settings_changed", "comm_settings", null, Json.Obj(before), Json.Obj(req)));
                return 0;
            }, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.CommRules).RequireModule("comm");

        // ---------------------------------------------------------------- rules (15.1): every version, the newest first
        g.MapGet("/rules", async (HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.RowsAsync("""
                SELECT r.id, r.rule_key AS "ruleKey", r.version, r.name_en AS "nameEn", r.name_ar AS "nameAr", r.basis, r.amount, r.rate, r.min_amount AS "minAmount",
                       r.max_amount AS "maxAmount", r.technician_id AS "technicianId", u.display_name_en AS "technicianName", r.branch_id AS "branchId", b.code AS "branchCode",
                       r.category_id AS "categoryId", c.name_en AS "categoryEn", c.name_ar AS "categoryAr", r.effective_from AS "effectiveFrom", r.effective_to AS "effectiveTo",
                       r.status, comm.scope_rank(r.technician_id, r.branch_id, r.category_id) AS rank,
                       r.version = (SELECT max(x.version) FROM comm.rule x WHERE x.rule_key = r.rule_key) AS latest,
                       (SELECT count(*) FROM comm.accrual a WHERE a.rule_id = r.id) AS used, r.created_at AS "createdAt"
                  FROM comm.rule r LEFT JOIN iam."user" u ON u.id = r.technician_id LEFT JOIN core.branch b ON b.id = r.branch_id LEFT JOIN repair.category c ON c.id = r.category_id
                 ORDER BY latest DESC, rank, r.name_en, r.version DESC
                """), ct))).RequirePermission(Permissions.CommView).RequireModule("comm");

        g.MapPost("/rules", async (CommissionRuleRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t =>
                WriteRuleAsync(t, Guid.CreateVersion7(), 1, req, http.User.UserIdOf()), ct)))
            .RequirePermission(Permissions.CommRules).RequireModule("comm");

        // A change is the next version from a date; the version before ends the day before (or is withdrawn if it had not begun).
        g.MapPut("/rules/{key:guid}", async (Guid key, CommissionRuleRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var latest = await t.QuerySingleOrDefaultAsync<CommissionRuleRow>(Commissions.RuleSelect + " WHERE rule_key = @key ORDER BY version DESC LIMIT 1 FOR UPDATE", new { key })
                    ?? throw new KiteException(ErrorCodes.NotFound, "That rule was not found.", 404);
                var from = req.EffectiveFrom.ToDateTime(TimeOnly.MinValue);
                if (latest.Status == "active")
                {
                    if (latest.EffectiveFrom >= from)
                        await t.ExecuteAsync("UPDATE comm.rule SET status = 'inactive' WHERE id = @Id", new { latest.Id });
                    else if (latest.EffectiveTo is null || latest.EffectiveTo >= from)
                        await t.ExecuteAsync("UPDATE comm.rule SET effective_to = @to WHERE id = @Id", new { to = from.AddDays(-1), latest.Id });
                }
                return await WriteRuleAsync(t, key, latest.Version + 1, req, http.User.UserIdOf());
            }, ct))).RequirePermission(Permissions.CommRules).RequireModule("comm");

        g.MapPost("/rules/{key:guid}/end", async (Guid key, CommissionRuleEndRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var latest = await t.QuerySingleOrDefaultAsync<CommissionRuleRow>(Commissions.RuleSelect + " WHERE rule_key = @key ORDER BY version DESC LIMIT 1 FOR UPDATE", new { key })
                    ?? throw new KiteException(ErrorCodes.NotFound, "That rule was not found.", 404);
                var to = req.EffectiveTo.ToDateTime(TimeOnly.MinValue);
                if (to < latest.EffectiveFrom) await t.ExecuteAsync("UPDATE comm.rule SET status = 'inactive' WHERE id = @Id", new { latest.Id });
                else await t.ExecuteAsync("UPDATE comm.rule SET effective_to = @to WHERE id = @Id", new { to, latest.Id });
                await t.AuditAsync(new AuditEntry("comm.rule_ended", "comm_rule", latest.Id, After: Json.Obj(new { latest.NameEn, req.EffectiveTo })));
                return 0;
            }, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.CommRules).RequireModule("comm");

        // ---------------------------------------------------------------- accruals, approval, reversal (15.2-15.4)
        g.MapGet("", async (string? status, Guid? technicianId, Guid? branchId, Guid? jobId, DateOnly? from, DateOnly? to, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t => Parsed(await t.RowsAsync(AccrualList + """
                 WHERE (@status::text IS NULL OR a.status = @status) AND (@technicianId::uuid IS NULL OR a.technician_id = @technicianId)
                   AND (@branchId::uuid IS NULL OR a.branch_id = @branchId) AND (@jobId::uuid IS NULL OR a.job_id = @jobId)
                   AND (@from::date IS NULL OR a.accrual_date >= @from) AND (@to::date IS NULL OR a.accrual_date <= @to)
                 ORDER BY a.accrual_date DESC, j.number, a.kind_version, a.created_at LIMIT 2000
                """, new { status = string.IsNullOrWhiteSpace(status) ? null : status, technicianId, branchId, jobId, from = D(from), to = D(to) })), ct)))
            .RequirePermission(Permissions.CommView).RequireModule("comm");

        // A technician's own commissions.
        g.MapGet("/mine", async (DateOnly? from, DateOnly? to, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(AccessScope.System, http.ActorOf(), async t => Parsed(await t.RowsAsync(AccrualList + """
                 WHERE a.technician_id = @me AND (@from::date IS NULL OR a.accrual_date >= @from) AND (@to::date IS NULL OR a.accrual_date <= @to)
                 ORDER BY a.accrual_date DESC, a.created_at LIMIT 2000
                """, new { me = http.User.UserIdOf(), from = D(from), to = D(to) })), ct)))
            .RequirePermission(Permissions.CommOwn).RequireModule("comm");

        g.MapPost("/approve", async (AccrualIdsRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(new { approved = await PosEndpoints.WithRetryAsync(() => db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(),
                t => Commissions.ApproveAsync(t, http.User, req.Ids ?? [], http.User.UserIdOf()), ct)) }))
            .RequirePermission(Permissions.CommApprove).RequireModule("comm");

        g.MapPost("/{id:guid}/reverse", async (Guid id, AccrualReverseRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(new { reversalId = await PosEndpoints.WithRetryAsync(() => db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var branch = await t.ExecuteScalarAsync<Guid?>("SELECT branch_id FROM comm.accrual WHERE id = @id", new { id })
                    ?? throw new KiteException(ErrorCodes.NotFound, "That commission was not found in your branches.", 404);
                http.User.Demand(Permissions.CommApprove, branch);
                var biz = await BusinessInfo.LoadAsync(t);
                return await Commissions.ReverseAsync(t, id, req.Reason, http.User.UserIdOf(), BusinessDay.Today(biz.TimeZone, t.Clock));
            }, ct)) })).RequirePermission(Permissions.CommApprove).RequireModule("comm");

        // Runs the generator for delivered repairs that have no commission yet (safe to run any number of times).
        g.MapPost("/generate", async (GenerateRequest? req, KiteDb db, CancellationToken ct) =>
            Results.Ok(new { written = await Commissions.GenerateMissingAsync(db, req?.JobId, ct) }))
            .RequirePermission(Permissions.CommApprove).RequireModule("comm");

        // ---------------------------------------------------------------- payouts (15.4)
        g.MapGet("/payable", async (Guid? branchId, Guid? technicianId, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.RowsAsync("""
                SELECT a.branch_id AS "branchId", b.code AS "branchCode", a.technician_id AS "technicianId", u.display_name_en AS "technicianName",
                       count(*) AS count, sum(a.amount) AS total, min(a.accrual_date) AS "oldest"
                  FROM comm.accrual a JOIN core.branch b ON b.id = a.branch_id JOIN iam."user" u ON u.id = a.technician_id
                 WHERE a.status = 'approved' AND (@branchId::uuid IS NULL OR a.branch_id = @branchId) AND (@technicianId::uuid IS NULL OR a.technician_id = @technicianId)
                 GROUP BY a.branch_id, b.code, a.technician_id, u.display_name_en ORDER BY b.code, u.display_name_en
                """, new { branchId, technicianId }), ct))).RequirePermission(Permissions.CommView).RequireModule("comm");

        g.MapPost("/payouts", async (PayoutRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            var r = await PosEndpoints.WithRetryAsync(() => db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t =>
            {
                http.User.Demand(Permissions.CommPay, req.BranchId);
                return Commissions.PayoutAsync(t, req.BranchId, req.TechnicianId, req.AccrualIds ?? [], req.Method, req.Reference, req.Note, http.User.UserIdOf());
            }, ct));
            return Results.Ok(new { id = r.Id, number = r.Number, total = r.Total });
        }).RequirePermission(Permissions.CommPay).RequireModule("comm");

        g.MapGet("/payouts", async (Guid? technicianId, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.RowsAsync("""
                SELECT p.id, p.number, p.branch_id AS "branchId", b.code AS "branchCode", p.technician_id AS "technicianId", u.display_name_en AS "technicianName",
                       p.method, p.total, p.reference, p.note, p.payout_date AS "payoutDate", c.display_name_en AS "createdBy",
                       (SELECT count(*) FROM comm.payout_line l WHERE l.payout_id = p.id) AS lines
                  FROM comm.payout p JOIN core.branch b ON b.id = p.branch_id JOIN iam."user" u ON u.id = p.technician_id JOIN iam."user" c ON c.id = p.created_by
                 WHERE @technicianId::uuid IS NULL OR p.technician_id = @technicianId
                 ORDER BY p.created_at DESC LIMIT 500
                """, new { technicianId }), ct))).RequirePermission(Permissions.CommView).RequireModule("comm");

        // ---------------------------------------------------------------- reports (15.5)
        // A technician's statement for a period: what was payable at the start, each accrual, each payout, and what is payable at the end.
        g.MapGet("/statement", async (Guid technicianId, DateOnly from, DateOnly to, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t => new
            {
                technician = await t.ExecuteScalarAsync<string>("SELECT display_name_en FROM iam.\"user\" WHERE id = @technicianId", new { technicianId }),
                from, to,
                opening = await t.ExecuteScalarAsync<decimal>("""
                    SELECT coalesce((SELECT sum(amount) FROM comm.accrual WHERE technician_id = @technicianId AND accrual_date < @from), 0)
                         - coalesce((SELECT sum(total) FROM comm.payout WHERE technician_id = @technicianId AND payout_date < @from), 0)
                    """, new { technicianId, from = D(from) }),
                accruals = Parsed(await t.RowsAsync(AccrualList + " WHERE a.technician_id = @technicianId AND a.accrual_date BETWEEN @from AND @to ORDER BY a.accrual_date, a.created_at",
                    new { technicianId, from = D(from), to = D(to) })),
                payouts = await t.RowsAsync("""
                    SELECT p.id, p.number, p.method, p.total, p.payout_date AS "payoutDate", b.code AS "branchCode" FROM comm.payout p JOIN core.branch b ON b.id = p.branch_id
                     WHERE p.technician_id = @technicianId AND p.payout_date BETWEEN @from AND @to ORDER BY p.payout_date, p.number
                    """, new { technicianId, from = D(from), to = D(to) }),
                closing = await t.ExecuteScalarAsync<decimal>("""
                    SELECT coalesce((SELECT sum(amount) FROM comm.accrual WHERE technician_id = @technicianId AND accrual_date <= @to), 0)
                         - coalesce((SELECT sum(total) FROM comm.payout WHERE technician_id = @technicianId AND payout_date <= @to), 0)
                    """, new { technicianId, to = D(to) }),
            }, ct))).RequirePermission(Permissions.CommView).RequireModule("comm");

        // Technician performance: jobs, revenue, turnaround, rework and commission earned in a period (days in the business time zone).
        g.MapGet("/performance", async (DateOnly from, DateOnly to, Guid? branchId, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.RowsAsync("""
                WITH jobs AS (
                       SELECT j.id, j.technician_id, j.created_at, j.delivered_at, j.warranty_of_id,
                              (SELECT coalesce(sum(l.net_amount), 0) FROM sales.sale_line l WHERE l.sale_id = repair.current_invoice(j.id)) AS revenue,
                              (SELECT max(h.at) FROM repair.status_history h WHERE h.job_id = j.id AND h.to_status = 'completed') AS completed_at
                         FROM repair.repair_job j
                        WHERE j.status = 'delivered' AND j.outcome = 'repaired' AND j.technician_id IS NOT NULL
                          AND (j.delivered_at AT TIME ZONE (SELECT time_zone FROM core.business LIMIT 1))::date BETWEEN @from::date AND @to::date AND (@branchId::uuid IS NULL OR j.branch_id = @branchId))
                SELECT u.id AS "technicianId", u.display_name_en AS "technicianName",
                       count(*) FILTER (WHERE jobs.warranty_of_id IS NULL) AS jobs,
                       coalesce(sum(jobs.revenue), 0) AS revenue,
                       round(avg(extract(epoch FROM coalesce(jobs.completed_at, jobs.delivered_at) - jobs.created_at) / 3600)::numeric, 1) AS "avgTurnaroundHours",
                       (SELECT count(*) FROM repair.repair_job c JOIN repair.repair_job o ON o.id = c.warranty_of_id
                         WHERE o.technician_id = u.id AND (c.created_at AT TIME ZONE (SELECT time_zone FROM core.business LIMIT 1))::date BETWEEN @from::date AND @to::date) AS rework,
                       (SELECT coalesce(sum(a.amount), 0) FROM comm.accrual a WHERE a.technician_id = u.id AND a.accrual_date BETWEEN @from AND @to) AS commission
                  FROM jobs JOIN iam."user" u ON u.id = jobs.technician_id
                 GROUP BY u.id, u.display_name_en ORDER BY u.display_name_en
                """, new { from = D(from), to = D(to), branchId }), ct))).RequirePermission(Permissions.CommView).RequireModule("comm");

        // Which rule versions produced which commissions.
        g.MapGet("/rule-usage", async (DateOnly? from, DateOnly? to, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.RowsAsync("""
                SELECT r.rule_key AS "ruleKey", r.version, r.name_en AS "nameEn", r.name_ar AS "nameAr", r.basis, r.rate, r.amount, count(a.id) AS accruals,
                       coalesce(sum(a.amount), 0) AS total, min(a.accrual_date) AS first, max(a.accrual_date) AS last
                  FROM comm.rule r JOIN comm.accrual a ON a.rule_id = r.id
                 WHERE (@from::date IS NULL OR a.accrual_date >= @from) AND (@to::date IS NULL OR a.accrual_date <= @to)
                 GROUP BY r.rule_key, r.version, r.name_en, r.name_ar, r.basis, r.rate, r.amount ORDER BY r.name_en, r.version
                """, new { from = D(from), to = D(to) }), ct))).RequirePermission(Permissions.CommView).RequireModule("comm");

        // ---------------------------------------------------------------- a job's technician shares (15.2)
        app.MapGet("/api/v1/repairs/{id:guid}/shares", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.RowsAsync("""
                SELECT s.technician_id AS "technicianId", u.display_name_en AS "technicianName", s.share FROM comm.job_share s JOIN iam."user" u ON u.id = s.technician_id
                 WHERE s.job_id = @id ORDER BY s.share DESC, u.display_name_en
                """, new { id }), ct))).RequirePermission(Permissions.RepairView).RequireModule("repair");

        app.MapPut("/api/v1/repairs/{id:guid}/shares", async (Guid id, JobSharesRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            await PosEndpoints.WithRetryAsync(() => db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var j = await RepairCore.LoadAsync(t, id, forUpdate: true);
                http.User.Demand(Permissions.RepairAssign, j.BranchId);
                var shares = req.Shares ?? [];
                if (shares.Select(s => s.TechnicianId).Distinct().Count() != shares.Count)
                    throw new KiteException(ErrorCodes.CommissionShares, "A technician appears twice.", 400, "shares");
                if (shares.Any(s => s.Share <= 0 || s.Share > 100 || s.Share != decimal.Round(s.Share, 4)))
                    throw new KiteException(ErrorCodes.CommissionShares, "Each share is above 0% and at most 100%, with up to 4 decimals.", 400, "shares");
                if (shares.Count > 0 && shares.Sum(s => s.Share) != 100m)
                    throw new KiteException(ErrorCodes.CommissionShares, $"The shares add up to {shares.Sum(s => s.Share):0.####}%; they must make 100%.", 400, "shares");
                if (await t.ExecuteScalarAsync<bool>("""
                    SELECT EXISTS (SELECT 1 FROM comm.accrual a WHERE a.job_id = @id AND a.kind = 'repair' AND a.status <> 'reversed'
                                     AND NOT EXISTS (SELECT 1 FROM comm.accrual r WHERE r.reverses_id = a.id))
                    """, new { id }))
                    throw new KiteException(ErrorCodes.CommissionState, "The job's commissions were generated; correct the invoice to change who shares them.", 409);
                foreach (var s in shares)
                    if (!await t.ExecuteScalarAsync<bool>("""
                        SELECT EXISTS (SELECT 1 FROM iam."user" u JOIN iam.user_branch_role ur ON ur.user_id = u.id JOIN iam.role_permission rp ON rp.role_id = ur.role_id
                                        WHERE u.id = @TechnicianId AND u.status = 'active' AND rp.permission_code = 'REPAIR.WORK'
                                          AND (ur.branch_id IS NULL OR ur.branch_id = @BranchId))
                        """, new { s.TechnicianId, j.BranchId }))
                        throw new KiteException(ErrorCodes.CommissionShares, "Shares go to technicians who work at this branch.", 400, "shares");
                var before = await t.RowsAsync("SELECT technician_id AS \"technicianId\", share FROM comm.job_share WHERE job_id = @id", new { id });
                await t.ExecuteAsync("DELETE FROM comm.job_share WHERE job_id = @id", new { id });
                foreach (var s in shares)
                    await t.ExecuteAsync("""
                        INSERT INTO comm.job_share (job_id, technician_id, business_id, branch_id, share, set_by) VALUES (@id, @TechnicianId, @BusinessId, @BranchId, @Share, @uid)
                        """, new { id, s.TechnicianId, j.BusinessId, j.BranchId, s.Share, uid = http.User.UserIdOf() });
                await t.AuditAsync(new AuditEntry("repair.shares_set", "repair_job", id, Json.Obj(new { shares = before }), Json.Obj(new { shares }), BranchId: j.BranchId));
                return 0;
            }, ct));
            return Results.NoContent();
        }).RequirePermission(Permissions.RepairAssign).RequireModule("repair");
    }

    private static DateTime? D(DateOnly? d) => d?.ToDateTime(TimeOnly.MinValue);

    /// <summary>The trace and basis figures go out as JSON objects, not as text.</summary>
    private static IReadOnlyList<Dictionary<string, object?>> Parsed(IReadOnlyList<Dictionary<string, object?>> rows)
    {
        foreach (var r in rows)
            foreach (var k in new[] { "trace", "basisAmounts" })
                if (r.TryGetValue(k, out var v) && v is string text) r[k] = JsonDocument.Parse(text).RootElement.Clone();
        return rows;
    }

    private static async Task<object> WriteRuleAsync(DbTx t, Guid key, int version, CommissionRuleRequest req, Guid uid)
    {
        if (string.IsNullOrWhiteSpace(req.NameEn) || string.IsNullOrWhiteSpace(req.NameAr)) throw new KiteException(ErrorCodes.Validation, "Name the rule in English and Arabic.", 400, "nameEn");
        // The arithmetic checks the basis, amount, rate, minimum and maximum the same way it will use them.
        CommissionMath.Compute(req.Basis, req.Basis == "fixed" ? req.Amount : null, req.Basis == "fixed" ? null : req.Rate, req.MinAmount, req.MaxAmount,
            new CommissionBasis(0, 0, 0, 0), 100m);
        if (req.TechnicianId is not null && req.BranchId is not null)
            throw new KiteException(ErrorCodes.CommissionRule, "A technician's rule applies in every branch: choose a technician or a branch, not both.", 400, "branchId");
        foreach (var (v, f) in new[] { (req.Amount, "amount"), (req.MinAmount, "minAmount"), (req.MaxAmount, "maxAmount") })
            if (v is { } x && (x < 0 || x != decimal.Round(x, 3))) throw new KiteException(ErrorCodes.Validation, "Amounts are zero or more, in baisa.", 400, f);
        if (req.Rate is { } rr && rr != decimal.Round(rr, 4)) throw new KiteException(ErrorCodes.Validation, "A rate has at most 4 decimals.", 400, "rate");
        var id = t.NewId();
        var businessId = (await BusinessInfo.LoadAsync(t)).Id;
        await t.ExecuteAsync("""
            INSERT INTO comm.rule (id, business_id, rule_key, version, name_en, name_ar, basis, amount, rate, min_amount, max_amount, technician_id, branch_id, category_id,
                                   effective_from, effective_to, created_by)
            VALUES (@id, @businessId, @key, @version, @nameEn, @nameAr, @Basis, @amount, @rate, @MinAmount, @MaxAmount, @TechnicianId, @BranchId, @CategoryId,
                    @from, @to, @uid)
            """, new { id, businessId, key, version, nameEn = req.NameEn.Trim(), nameAr = req.NameAr.Trim(), req.Basis,
                       amount = req.Basis == "fixed" ? req.Amount : null, rate = req.Basis == "fixed" ? null : req.Rate, req.MinAmount, req.MaxAmount,
                       req.TechnicianId, req.BranchId, req.CategoryId, from = req.EffectiveFrom.ToDateTime(TimeOnly.MinValue),
                       to = req.EffectiveTo?.ToDateTime(TimeOnly.MinValue), uid });
        await t.AuditAsync(new AuditEntry("comm.rule_written", "comm_rule", id, After: Json.Obj(new { req.NameEn, version, req.Basis, req.Rate, req.Amount, req.EffectiveFrom })));
        return new { id, ruleKey = key, version };
    }
}
