using System.Security.Claims;
using System.Text.Json;
using Kite.Accounting;
using Kite.Persistence;
using Kite.Platform;
using Kite.Sales;

namespace Kite.Host.Infrastructure;

public sealed class CommissionSettings
{
    public string GenerateOn { get; set; } = "delivered";
    public string ApprovalBy { get; set; } = "branch";
    public int ClawbackDays { get; set; }
    public decimal ClawbackPercent { get; set; } = 100m;
    public int Version { get; set; }
}

public sealed class CommissionRuleRow
{
    public Guid Id { get; set; }
    public Guid RuleKey { get; set; }
    public int Version { get; set; }
    public string NameEn { get; set; } = "";
    public string NameAr { get; set; } = "";
    public string Basis { get; set; } = "";
    public decimal? Amount { get; set; }
    public decimal? Rate { get; set; }
    public decimal? MinAmount { get; set; }
    public decimal? MaxAmount { get; set; }
    public Guid? TechnicianId { get; set; }
    public Guid? BranchId { get; set; }
    public Guid? CategoryId { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public string Status { get; set; } = "";
}

public sealed class AccrualRow
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }
    public Guid BranchId { get; set; }
    public Guid JobId { get; set; }
    public Guid TechnicianId { get; set; }
    public string Kind { get; set; } = "";
    public int KindVersion { get; set; }
    public decimal Amount { get; set; }
    public string Status { get; set; } = "";
    public Guid? ReversesId { get; set; }
    public bool HasReversal { get; set; }
}

/// <summary>
/// Technician commissions (blueprint 15). Generation writes the accrual first - its unique key on (job, technician, kind,
/// version) is what makes a second accrual impossible, however often or concurrently the generator runs - and then its
/// journal. An accrual is never changed: it is approved, paid in one payout line, or reversed by its exact opposite.
/// </summary>
public static class Commissions
{
    public const string RuleSelect = """
        SELECT id, rule_key AS RuleKey, version, name_en AS NameEn, name_ar AS NameAr, basis, amount, rate, min_amount AS MinAmount, max_amount AS MaxAmount,
               technician_id AS TechnicianId, branch_id AS BranchId, category_id AS CategoryId, effective_from AS EffectiveFrom, effective_to AS EffectiveTo, status
          FROM comm.rule
        """;
    private const string AccrualSelect = """
        SELECT a.id, a.business_id AS BusinessId, a.branch_id AS BranchId, a.job_id AS JobId, a.technician_id AS TechnicianId, a.kind, a.kind_version AS KindVersion,
               a.amount, a.status, a.reverses_id AS ReversesId, EXISTS (SELECT 1 FROM comm.accrual r WHERE r.reverses_id = a.id) AS HasReversal
          FROM comm.accrual a
        """;

    public static async Task<CommissionSettings> SettingsAsync(DbTx t) =>
        await t.QuerySingleOrDefaultAsync<CommissionSettings>("""
            SELECT generate_on AS GenerateOn, approval_by AS ApprovalBy, clawback_days AS ClawbackDays, clawback_percent AS ClawbackPercent, version FROM comm.settings LIMIT 1
            """) ?? new CommissionSettings();

    /// <summary>Whether technician commissions are switched on (and licensed, and not switched off by Nextdab).</summary>
    public static Task<bool> ModuleOnAsync(DbTx t) =>
        t.ExecuteScalarAsync<bool>("""
            SELECT coalesce((SELECT enabled FROM core.module_setting WHERE module_code = 'comm' AND branch_id IS NULL LIMIT 1), false)
               AND NOT coalesce('comm'::text = ANY((SELECT modules_off FROM lic.control_state LIMIT 1)::text[]), false)
               AND coalesce('comm'::text = ANY((SELECT modules FROM lic.licence WHERE is_current LIMIT 1)::text[]), true)
            """);

    /// <summary>The version of a job's invoice that stands: 1 for the delivery invoice, one more for each corrected invoice.</summary>
    public static Task<int> InvoiceVersionAsync(DbTx t, Guid jobId) =>
        t.ExecuteScalarAsync<int>("SELECT 1 + count(*)::int FROM repair.invoice_correction WHERE job_id = @jobId AND corrected_sale_id IS NOT NULL", new { jobId });

    /// <summary>What the job's invoice charged for labour and parts (excluding VAT, after discounts) and what the job cost.</summary>
    public static async Task<CommissionBasis> BasisAsync(DbTx t, Guid jobId, Guid saleId)
    {
        var (labour, parts) = await t.QuerySingleOrDefaultAsync<(decimal, decimal)>("""
            SELECT coalesce(sum(net_amount) FILTER (WHERE repair_kind = 'labour'), 0), coalesce(sum(net_amount) FILTER (WHERE repair_kind = 'part'), 0)
              FROM sales.sale_line WHERE sale_id = @saleId
            """, new { saleId });
        var (partsCost, outside) = await t.QuerySingleOrDefaultAsync<(decimal, decimal)>("SELECT repair.parts_cost(@jobId), repair.outside_cost(@jobId)", new { jobId });
        return new CommissionBasis(labour, parts, partsCost, outside);
    }

    /// <summary>
    /// Generates the commissions of a delivered repair's current invoice (15.2): for each technician's share, the most
    /// specific rule in force on the invoice date. Returns how many accruals this call wrote (0 when they exist already).
    /// </summary>
    public static async Task<int> GenerateAsync(DbTx t, Guid jobId, Guid? actor)
    {
        if (!await ModuleOnAsync(t)) return 0;
        var j = await RepairCore.LoadAsync(t, jobId, forUpdate: true);
        if (j.Status != "delivered" || j.Outcome != "repaired" || j.IsWarrantyClaim) return 0;
        var saleId = await t.ExecuteScalarAsync<Guid?>("SELECT repair.current_invoice(@jobId)", new { jobId });
        if (saleId is not { } sale) return 0;
        var settings = await SettingsAsync(t);
        var invoice = await t.QuerySingleOrDefaultAsync<(string Number, DateTime Date, decimal Unpaid)>("""
            SELECT s.number, s.sale_date, s.gross_total + s.cash_rounding - coalesce((SELECT sum(p.amount) FROM sales.payment p WHERE p.sale_id = s.id), 0)
              FROM sales.sale s WHERE s.id = @sale
            """, new { sale });
        if (settings.GenerateOn == "paid" && invoice.Unpaid > 0) return 0;
        var version = await InvoiceVersionAsync(t, jobId);
        var basis = await BasisAsync(t, jobId, sale);
        var shares = (await t.QueryAsync<(Guid Technician, decimal Share)>("SELECT technician_id, share FROM comm.job_share WHERE job_id = @jobId ORDER BY technician_id",
            new { jobId })).ToList();
        if (shares.Count == 0 && j.TechnicianId is { } only) shares.Add((only, 100m));
        if (shares.Count == 0) return 0;
        if (shares.Sum(s => s.Share) != 100m)
            throw new KiteException(ErrorCodes.CommissionShares, $"The technicians' shares of job {j.Number} add up to {shares.Sum(s => s.Share):0.####}%, not 100%.", 409);
        var date = DateOnly.FromDateTime(invoice.Date);
        var written = 0;
        foreach (var (tech, share) in shares)
        {
            var ruleId = await t.ExecuteScalarAsync<Guid?>("SELECT comm.resolve_rule(@BusinessId, @tech, @BranchId, @CategoryId, @date::date)",
                new { j.BusinessId, tech, j.BranchId, j.CategoryId, date = date.ToDateTime(TimeOnly.MinValue) });
            if (ruleId is null) continue;
            var rule = await t.QuerySingleOrDefaultAsync<CommissionRuleRow>(RuleSelect + " WHERE id = @ruleId", new { ruleId });
            var r = CommissionMath.Compute(rule!.Basis, rule.Amount, rule.Rate, rule.MinAmount, rule.MaxAmount, basis, share);
            var trace = new
            {
                job = j.Number, invoice = invoice.Number, invoiceVersion = version, date,
                rule = new { rule.RuleKey, rule.Version, rule.NameEn, rule.Basis, rule.Amount, rule.Rate, min = rule.MinAmount, max = rule.MaxAmount,
                             scope = new { technician = rule.TechnicianId, branch = rule.BranchId, category = rule.CategoryId } },
                basis = new { basis.Labour, basis.PartsRevenue, basis.PartsCost, basis.External, basis.Profit },
                @base = r.Base, r.Raw, r.Clamped, share, r.Amount,
            };
            var id = t.NewId();
            var inserted = await t.ExecuteScalarAsync<Guid?>("""
                INSERT INTO comm.accrual (id, business_id, branch_id, job_id, technician_id, kind, kind_version, amount, share, rule_id, rule_key, rule_version, basis,
                                          basis_amounts, trace, sale_id, accrual_date, created_by)
                VALUES (@id, @BusinessId, @BranchId, @jobId, @tech, 'repair', @version, @amount, @share, @ruleId, @RuleKey, @ruleVersion, @Basis,
                        @basisJson::jsonb, @traceJson::jsonb, @sale, @date, @actor)
                ON CONFLICT (job_id, technician_id, kind, kind_version) WHERE kind <> 'reversal' DO NOTHING
                RETURNING id
                """, new { id, j.BusinessId, j.BranchId, jobId, tech, version, amount = r.Amount, share, ruleId, rule.RuleKey, ruleVersion = rule.Version, rule.Basis,
                           basisJson = JsonSerializer.Serialize(new { basis.Labour, basis.PartsRevenue, basis.PartsCost, basis.External, basis.Profit }, Json.Web),
                           traceJson = JsonSerializer.Serialize(trace, Json.Web), sale, date = date.ToDateTime(TimeOnly.MinValue), actor });
            if (inserted is null) continue;
            await PostAsync(t, j.BusinessId, j.BranchId, id, r.Amount, date, $"Commission on repair {j.Number} ({invoice.Number})");
            await t.AuditAsync(new AuditEntry("comm.accrued", "repair_job", jobId, After: Json.Obj(new { j.Number, technician = tech, version, r.Amount, rule = rule.NameEn }), BranchId: j.BranchId));
            written++;
        }
        return written;
    }

    private static async Task PostAsync(DbTx t, Guid businessId, Guid branchId, Guid accrualId, decimal amount, DateOnly date, string description)
    {
        var journal = await Posting.PostAsync(t, businessId, branchId, PostingRuleTemplates.CommissionAccrual, new PostingFacts().Set("amount", amount), date,
            "comm_accrual", accrualId, description);
        if (journal is not null) await t.ExecuteAsync("UPDATE comm.accrual SET journal_id = @journal WHERE id = @accrualId", new { journal, accrualId });
    }

    /// <summary>
    /// Reverses an accrual by its exact opposite (15.3). Before payout the pair closes; after payout the reversal waits,
    /// approved, to be deducted in the technician's next payout.
    /// </summary>
    public static async Task<Guid> ReverseAsync(DbTx t, Guid accrualId, string reason, Guid? actor, DateOnly date)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new KiteException(ErrorCodes.Validation, "Give the reason for the reversal.", 400, "reason");
        var a = await t.QuerySingleOrDefaultAsync<AccrualRow>(AccrualSelect + " WHERE a.id = @accrualId FOR UPDATE OF a", new { accrualId })
            ?? throw new KiteException(ErrorCodes.NotFound, "That commission was not found in your branches.", 404);
        if (a.Kind == "reversal") throw new KiteException(ErrorCodes.CommissionState, "A reversal is not reversed again.", 409);
        if (a.Status == "reversed" || a.HasReversal) throw new KiteException(ErrorCodes.CommissionState, "That commission was already reversed.", 409);
        var paid = a.Status == "paid";
        var revId = t.NewId();
        await t.ExecuteAsync("""
            INSERT INTO comm.accrual (id, business_id, branch_id, job_id, technician_id, kind, kind_version, amount, reverses_id, basis_amounts, trace, accrual_date,
                                      status, approved_by, approved_at, reversed_by, reversed_at, reason, created_by)
            VALUES (@revId, @BusinessId, @BranchId, @JobId, @TechnicianId, 'reversal', @KindVersion, @neg, @Id, '{}', @trace::jsonb, @date,
                    @status, @approvedBy, @approvedAt, @closedBy, @closedAt, @reason, @actor)
            """, new { revId, a.BusinessId, a.BranchId, a.JobId, a.TechnicianId, a.KindVersion, neg = -a.Amount, a.Id,
                       trace = JsonSerializer.Serialize(new { reverses = a.Id, a.Kind, a.KindVersion, a.Amount, wasPaid = paid, reason = reason.Trim() }, Json.Web),
                       date = date.ToDateTime(TimeOnly.MinValue),
                       // A paid commission's reversal is a deduction already approved by whoever reversed it: it comes off the next payout.
                       status = !paid ? "reversed" : actor is null ? "accrued" : "approved",
                       approvedBy = paid ? actor : null, approvedAt = paid && actor is not null ? t.Clock.UtcNow : (DateTimeOffset?)null,
                       closedBy = paid ? null : actor, closedAt = paid ? (DateTimeOffset?)null : t.Clock.UtcNow, reason = reason.Trim(), actor });
        if (!paid)
            await t.ExecuteAsync("UPDATE comm.accrual SET status = 'reversed', reversed_by = @actor, reversed_at = now(), reason = @reason WHERE id = @Id",
                new { actor, reason = reason.Trim(), a.Id });
        await PostAsync(t, a.BusinessId, a.BranchId, revId, -a.Amount, date, $"Commission reversed: {reason.Trim()}");
        await t.AuditAsync(new AuditEntry("comm.reversed", "comm_accrual", a.Id, After: Json.Obj(new { a.Amount, wasPaid = paid }), Reason: reason.Trim(), BranchId: a.BranchId));
        return revId;
    }

    /// <summary>Reverses every standing commission generated from an invoice (the invoice was credit-noted, 15.3).</summary>
    public static async Task<int> ReverseInvoiceAsync(DbTx t, Guid jobId, Guid saleId, string reason, Guid? actor, DateOnly date)
    {
        var ids = await t.QueryAsync<Guid>("""
            SELECT a.id FROM comm.accrual a
             WHERE a.job_id = @jobId AND a.sale_id = @saleId AND a.kind = 'repair' AND a.status <> 'reversed'
               AND NOT EXISTS (SELECT 1 FROM comm.accrual r WHERE r.reverses_id = a.id)
             ORDER BY a.id
            """, new { jobId, saleId });
        var n = 0;
        foreach (var id in ids) { await ReverseAsync(t, id, reason, actor, date); n++; }
        return n;
    }

    /// <summary>
    /// Rework clawback (15.3): a warranty claim opened within the configured days of the original delivery takes back the
    /// configured share of each technician's standing commission on the original job, once.
    /// </summary>
    public static async Task<int> ClawbackAsync(DbTx t, RepairJob claim, Guid? actor)
    {
        if (claim.WarrantyOfId is not { } originalId || !await ModuleOnAsync(t)) return 0;
        var settings = await SettingsAsync(t);
        if (settings.ClawbackDays == 0) return 0;
        var original = await t.QuerySingleOrDefaultAsync<(string Number, DateTime? DeliveredAt)>("SELECT number, delivered_at FROM repair.repair_job WHERE id = @originalId",
            new { originalId });
        if (original.DeliveredAt is not { } delivered || claim.CreatedAt > delivered.AddDays(settings.ClawbackDays)) return 0;
        var standing = await t.QueryAsync<AccrualRow>(AccrualSelect + """
             WHERE a.job_id = @originalId AND a.kind = 'repair' AND a.status <> 'reversed' AND NOT EXISTS (SELECT 1 FROM comm.accrual r WHERE r.reverses_id = a.id)
               AND a.kind_version = (SELECT max(kind_version) FROM comm.accrual x WHERE x.job_id = a.job_id AND x.kind = 'repair')
             ORDER BY a.technician_id
            """, new { originalId });
        var date = BusinessDay.Today((await BusinessInfo.LoadAsync(t)).TimeZone, t.Clock);
        var n = 0;
        foreach (var a in standing)
        {
            var amount = -Currency.OMR.Round(a.Amount * settings.ClawbackPercent / 100m);
            if (amount == 0) continue;
            var id = t.NewId();
            var inserted = await t.ExecuteScalarAsync<Guid?>("""
                INSERT INTO comm.accrual (id, business_id, branch_id, job_id, technician_id, kind, kind_version, amount, basis_amounts, trace, claim_job_id, accrual_date, created_by)
                VALUES (@id, @BusinessId, @BranchId, @originalId, @TechnicianId, 'clawback', 1, @amount, '{}', @trace::jsonb, @claimId, @date, @actor)
                ON CONFLICT (job_id, technician_id, kind, kind_version) WHERE kind <> 'reversal' DO NOTHING
                RETURNING id
                """, new { id, a.BusinessId, a.BranchId, originalId, a.TechnicianId, amount,
                           trace = JsonSerializer.Serialize(new { original = original.Number, claim = claim.Number, of = a.Id, a.Amount, settings.ClawbackPercent, settings.ClawbackDays }, Json.Web),
                           claimId = claim.Id, date = date.ToDateTime(TimeOnly.MinValue), actor });
            if (inserted is null) continue;
            await PostAsync(t, a.BusinessId, a.BranchId, id, amount, date, $"Rework clawback: warranty claim {claim.Number} on {original.Number}");
            n++;
        }
        return n;
    }

    /// <summary>Approves accrued commissions for payout (15.4): by the branch's manager, or only at head office, as the business set.</summary>
    public static async Task<int> ApproveAsync(DbTx t, ClaimsPrincipal user, IReadOnlyList<Guid> ids, Guid actor)
    {
        var settings = await SettingsAsync(t);
        var n = 0;
        foreach (var id in ids.Distinct().Order())
        {
            var a = await t.QuerySingleOrDefaultAsync<AccrualRow>(AccrualSelect + " WHERE a.id = @id FOR UPDATE OF a", new { id })
                ?? throw new KiteException(ErrorCodes.NotFound, "A commission was not found in your branches.", 404);
            if (settings.ApprovalBy == "head_office" ? !user.HasIn(Permissions.CommApprove, null) : !user.HasIn(Permissions.CommApprove, a.BranchId))
                throw new KiteException(ErrorCodes.Forbidden, settings.ApprovalBy == "head_office"
                    ? "Commissions are approved at head office." : "You cannot approve commissions of this branch.", 403);
            if (a.Status != "accrued") throw new KiteException(ErrorCodes.CommissionState, $"Only an accrued commission is approved; one is {a.Status}.", 409);
            await t.ExecuteAsync("UPDATE comm.accrual SET status = 'approved', approved_by = @actor, approved_at = now() WHERE id = @id", new { actor, id });
            n++;
        }
        if (n > 0) await t.AuditAsync(new AuditEntry("comm.approved", "comm_accrual", null, After: Json.Obj(new { count = n })));
        return n;
    }

    /// <summary>
    /// Pays approved commissions of one technician at one branch (15.4): a payout document whose lines each take one
    /// accrual (an accrual can be in only one line), and its journal against cash, bank or payroll clearing.
    /// </summary>
    public static async Task<(Guid Id, string Number, decimal Total)> PayoutAsync(DbTx t, Guid branchId, Guid technicianId, IReadOnlyList<Guid> ids, string method,
        string? reference, string? note, Guid actor)
    {
        if (method is not ("cash" or "bank" or "payroll")) throw new KiteException(ErrorCodes.Validation, "A payout is in cash, to the bank, or through payroll.", 400, "method");
        if (ids.Count == 0) throw new KiteException(ErrorCodes.Validation, "Choose the commissions to pay.", 400, "accrualIds");
        var rows = new List<AccrualRow>();
        foreach (var id in ids.Distinct().Order())
        {
            var a = await t.QuerySingleOrDefaultAsync<AccrualRow>(AccrualSelect + " WHERE a.id = @id FOR UPDATE OF a", new { id })
                ?? throw new KiteException(ErrorCodes.NotFound, "A commission was not found in your branches.", 404);
            if (a.Status != "approved") throw new KiteException(ErrorCodes.CommissionState, $"Only approved commissions are paid; one is {a.Status}.", 409);
            if (a.TechnicianId != technicianId || a.BranchId != branchId)
                throw new KiteException(ErrorCodes.CommissionPayout, "A payout is for one technician's commissions at one branch.", 400, "accrualIds");
            rows.Add(a);
        }
        var total = rows.Sum(r => r.Amount);
        if (total <= 0) throw new KiteException(ErrorCodes.CommissionPayout, $"These commissions come to {total:0.000}; a payout is above zero (deductions wait for the next one).", 400, "accrualIds");
        var biz = await BusinessInfo.LoadAsync(t);
        var date = BusinessDay.Today(biz.TimeZone, t.Clock);
        var payoutId = t.NewId();
        var number = await DocNumbers.NextAsync(t, biz.Id, branchId, "comm_payout", await FiscalPeriods.FiscalYearOfAsync(t, biz.Id, date), "COM");
        var journal = await Posting.PostAsync(t, biz.Id, branchId, PostingRuleTemplates.CommissionPayout, new PostingFacts().Add("paid_by_method", method, total), date,
            "comm_payout", payoutId, $"Commissions paid: {number}");
        await t.ExecuteAsync("""
            INSERT INTO comm.payout (id, business_id, branch_id, number, technician_id, method, total, reference, note, payout_date, journal_id, created_by)
            VALUES (@payoutId, @b, @branchId, @number, @technicianId, @method, @total, @reference, @note, @date, @journal, @actor)
            """, new { payoutId, b = biz.Id, branchId, number, technicianId, method, total, reference = Clean(reference), note = Clean(note),
                       date = date.ToDateTime(TimeOnly.MinValue), journal, actor });
        foreach (var a in rows)
            await t.ExecuteAsync("INSERT INTO comm.payout_line (id, payout_id, accrual_id, amount) VALUES (@id, @payoutId, @aid, @Amount)",
                new { id = t.NewId(), payoutId, aid = a.Id, a.Amount });
        await t.AuditAsync(new AuditEntry("comm.paid", "comm_payout", payoutId, After: Json.Obj(new { number, technicianId, total, method, lines = rows.Count }), BranchId: branchId));
        return (payoutId, number, total);
    }

    /// <summary>Generates commissions for every delivered repair whose current invoice has none yet (a safe re-run).</summary>
    public static async Task<int> GenerateMissingAsync(KiteDb db, Guid? jobId, CancellationToken ct)
    {
        var jobs = await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => t.QueryAsync<Guid>("""
            SELECT j.id FROM repair.repair_job j
             WHERE j.status = 'delivered' AND j.outcome = 'repaired' AND j.warranty_of_id IS NULL AND (@jobId::uuid IS NULL OR j.id = @jobId)
               AND repair.current_invoice(j.id) IS NOT NULL
               AND NOT EXISTS (SELECT 1 FROM comm.accrual a WHERE a.job_id = j.id AND a.kind = 'repair' AND a.sale_id = repair.current_invoice(j.id))
             ORDER BY j.delivered_at LIMIT 1000
            """, new { jobId }), ct);
        var n = 0;
        foreach (var id in jobs)
            n += await Kite.Host.Features.PosEndpoints.WithRetryAsync(() => db.InTransactionAsync(AccessScope.System, ActorContext.System, t => GenerateAsync(t, id, null), ct));
        return n;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
