using Kite.Persistence;
using Kite.Platform;

namespace Kite.Host.Infrastructure;

public sealed class RepairOptions
{
    /// <summary>Fill parts requests from stock that has arrived, in the background.</summary>
    public bool PartsBackground { get; set; } = true;
    public int PartsIntervalSeconds { get; set; } = 10;
}

/// <summary>
/// Spare parts for repair jobs (blueprint 13.4). A job's approved estimate decides what it needs; what it needs and has
/// not fitted is reserved in the branch's stock, and what the branch does not have is a parts request. Stock is given
/// to requests oldest first: a job reserving now cannot take what an older request is waiting for. Every change takes
/// the job row first and then the stock balance rows (in part order), so these writers never wait on each other in
/// opposite order (ADR 54); the till takes only the balance.
/// </summary>
public static class RepairParts
{
    public static async Task<Guid> WarehouseAsync(DbTx t, Guid branchId) =>
        await t.ExecuteScalarAsync<Guid?>("""
            SELECT id FROM core.warehouse WHERE branch_id = @branchId AND status = 'active' ORDER BY (kind = 'shop') DESC, (code = 'SHOP') DESC, created_at LIMIT 1
            """, new { branchId }) ?? throw new KiteException(ErrorCodes.Validation, "The branch has no warehouse.", 400);

    private sealed class Need
    {
        public Guid VariantId { get; set; }
        public decimal Target { get; set; }
        public decimal Reserved { get; set; }
        public decimal Requested { get; set; }
        public DateTime? OpenSince { get; set; }
    }

    /// <summary>Locks a part's stock balance (creating it if needed) and returns what is on hand and reserved.</summary>
    public static async Task<(decimal Qty, decimal Reserved)> LockBalanceAsync(DbTx t, Guid businessId, Guid branchId, Guid warehouseId, Guid variantId) =>
        await t.QuerySingleOrDefaultAsync<(decimal, decimal)>("SELECT qty, reserved FROM inv.lock_part_balance(@branchId, @warehouseId, @variantId)",
            new { branchId, warehouseId, variantId });

    /// <summary>What older requests of other jobs are still waiting for: stock earmarked for them before this job.</summary>
    public static Task<decimal> OlderDemandAsync(DbTx t, Guid jobId, Guid warehouseId, Guid variantId, DateTime? since) =>
        t.ExecuteScalarAsync<decimal>("""
            SELECT coalesce(sum(qty), 0) FROM repair.part_request
             WHERE warehouse_id = @warehouseId AND variant_id = @variantId AND job_id <> @jobId AND qty > 0 AND open_since < coalesce(@since, now())
            """, new { jobId, warehouseId, variantId, since });

    /// <summary>
    /// Brings a job's reservations and parts requests in line with its approved estimate (the latest approved version: a
    /// new version waiting for the customer does not release what the job already holds): for each part, what the
    /// estimate asks for less what has been fitted (nothing once the job has ended) is reserved as far as free stock
    /// allows, and the rest is requested. Returns the parts still short.
    /// </summary>
    public static async Task<decimal> ReconcileAsync(DbTx t, RepairJob j, Guid? actor, (Guid VariantId, decimal Qty)? fitting = null)
    {
        var warehouse = await WarehouseAsync(t, j.BranchId);
        var ended = j.Outcome is not null || j.Status == "delivered";
        var needs = (await t.QueryAsync<Need>("""
            WITH est AS (
                   SELECT l.variant_id, sum(l.qty) AS qty
                     FROM repair.estimate e JOIN repair.estimate_line l ON l.estimate_id = e.id
                    WHERE e.id = (SELECT x.id FROM repair.estimate x WHERE x.job_id = @job AND x.status = 'approved' ORDER BY x.estimate_no DESC LIMIT 1) AND l.kind = 'part'
                    GROUP BY l.variant_id),
                 fit AS (SELECT variant_id, sum(qty) AS qty FROM repair.repair_line WHERE job_id = @job AND kind = 'part' GROUP BY variant_id),
                 res AS (SELECT variant_id, qty FROM inv.reservation WHERE source_doc_type = 'repair_job' AND source_doc_id = @job AND warehouse_id = @warehouse),
                 req AS (SELECT variant_id, qty, open_since FROM repair.part_request WHERE job_id = @job AND warehouse_id = @warehouse),
                 parts AS (SELECT variant_id FROM est UNION SELECT variant_id FROM res UNION SELECT variant_id FROM req)
            SELECT p.variant_id AS VariantId,
                   CASE WHEN @ended THEN 0 ELSE greatest(coalesce(est.qty, 0) - coalesce(fit.qty, 0), 0) END AS Target,
                   coalesce(res.qty, 0) AS Reserved, coalesce(req.qty, 0) AS Requested, req.open_since AS OpenSince
              FROM parts p LEFT JOIN est USING (variant_id) LEFT JOIN fit USING (variant_id) LEFT JOIN res USING (variant_id) LEFT JOIN req USING (variant_id)
             ORDER BY p.variant_id
            """, new { job = j.Id, warehouse, ended })).ToList();
        // A part being fitted (or returned) in this transaction whose line is not written yet counts as fitted (or back).
        if (fitting is { } f)
            foreach (var n in needs.Where(n => n.VariantId == f.VariantId && !ended))
                n.Target = Math.Max(0m, n.Target - f.Qty);
        var shortTotal = 0m;
        var audits = new List<AuditEntry>();
        foreach (var n in needs)
        {
            if (n.Target == n.Reserved && n.Requested == 0) continue;
            var bal = await LockBalanceAsync(t, j.BusinessId, j.BranchId, warehouse, n.VariantId);
            var older = n.Target > n.Reserved ? await OlderDemandAsync(t, j.Id, warehouse, n.VariantId, n.OpenSince) : 0m;
            var free = Math.Max(0m, bal.Qty - bal.Reserved - older);
            var newReserved = Math.Min(n.Target, n.Reserved + free);
            var newRequested = n.Target - newReserved;
            shortTotal += newRequested;
            if (newReserved != n.Reserved)
                await t.ExecuteAsync("""
                    INSERT INTO inv.reservation (id, business_id, branch_id, warehouse_id, variant_id, source_doc_type, source_doc_id, qty, updated_by)
                    VALUES (@id, @BusinessId, @BranchId, @warehouse, @VariantId, 'repair_job', @job, @qty, @actor)
                    ON CONFLICT (source_doc_type, source_doc_id, warehouse_id, variant_id) DO UPDATE SET qty = EXCLUDED.qty, updated_by = EXCLUDED.updated_by
                    """, new { id = t.NewId(), j.BusinessId, j.BranchId, warehouse, n.VariantId, job = j.Id, qty = newReserved, actor });
            if (newRequested != n.Requested)
            {
                var filled = n.Requested > 0 ? Math.Max(0m, Math.Min(n.Requested, newReserved - n.Reserved)) : 0m;
                await t.ExecuteAsync("""
                    INSERT INTO repair.part_request (id, business_id, branch_id, job_id, warehouse_id, variant_id, qty, requested_total, open_since)
                    VALUES (@id, @BusinessId, @BranchId, @job, @warehouse, @VariantId, @qty, @qty, CASE WHEN @qty > 0 THEN now() END)
                    ON CONFLICT (job_id, warehouse_id, variant_id) DO UPDATE
                       SET requested_total = repair.part_request.requested_total + greatest(EXCLUDED.qty - repair.part_request.qty, 0),
                           filled_total = repair.part_request.filled_total + @filled,
                           open_since = CASE WHEN EXCLUDED.qty > 0 THEN coalesce(repair.part_request.open_since, now()) END,
                           qty = EXCLUDED.qty, updated_at = now()
                    """, new { id = t.NewId(), j.BusinessId, j.BranchId, job = j.Id, warehouse, n.VariantId, qty = newRequested, filled });
            }
            if (newReserved != n.Reserved || newRequested != n.Requested)
                audits.Add(new AuditEntry("repair.parts_reserved", "repair_job", j.Id,
                    Before: Json.Obj(new { variant = n.VariantId, reserved = n.Reserved, requested = n.Requested }),
                    After: Json.Obj(new { variant = n.VariantId, reserved = newReserved, requested = newRequested }), BranchId: j.BranchId));
        }
        foreach (var a in audits) await t.AuditAsync(a);
        return shortTotal;
    }

    /// <summary>
    /// Takes a part for fitting: the job's own reservation first, then free stock not earmarked for older requests. Call
    /// before the stock movement; returns what the balance must still hold for others after the movement.
    /// </summary>
    public static async Task<decimal> TakeForFittingAsync(DbTx t, RepairJob j, Guid warehouseId, Guid variantId, decimal qty, Guid? actor)
    {
        var bal = await LockBalanceAsync(t, j.BusinessId, j.BranchId, warehouseId, variantId);
        var mine = await t.QuerySingleOrDefaultAsync<decimal?>("""
            SELECT qty FROM inv.reservation WHERE source_doc_type = 'repair_job' AND source_doc_id = @job AND warehouse_id = @warehouseId AND variant_id = @variantId
            """, new { job = j.Id, warehouseId, variantId }) ?? 0m;
        var used = Math.Min(mine, qty);
        if (used > 0)
            await t.ExecuteAsync("""
                UPDATE inv.reservation SET qty = qty - @used, updated_by = @actor
                 WHERE source_doc_type = 'repair_job' AND source_doc_id = @job AND warehouse_id = @warehouseId AND variant_id = @variantId
                """, new { used, actor, job = j.Id, warehouseId, variantId });
        var mySince = await t.ExecuteScalarAsync<DateTime?>("SELECT open_since FROM repair.part_request WHERE job_id = @job AND warehouse_id = @warehouseId AND variant_id = @variantId",
            new { job = j.Id, warehouseId, variantId });
        var older = used < qty ? await OlderDemandAsync(t, j.Id, warehouseId, variantId, mySince) : 0m;
        return bal.Reserved - used + older;
    }

    /// <summary>
    /// Fills open parts requests from stock that has arrived, oldest first: each job in its own transaction, which
    /// reconciles it and moves it from waiting for parts to in progress when nothing is short any more.
    /// </summary>
    public static async Task<int> FillAsync(KiteDb db, CancellationToken ct)
    {
        var jobs = await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => t.QueryAsync<Guid>("""
            SELECT job_id FROM (
              SELECT r.job_id, min(r.open_since) AS since
                FROM repair.part_request r JOIN inv.stock_balance b ON b.warehouse_id = r.warehouse_id AND b.variant_id = r.variant_id
               WHERE r.qty > 0 AND b.qty - b.reserved > 0
               GROUP BY r.job_id) x
             ORDER BY since LIMIT 500
            """), ct);
        var moved = 0;
        foreach (var jobId in jobs)
        {
            ct.ThrowIfCancellationRequested();
            moved += await Kite.Host.Features.PosEndpoints.WithRetryAsync(() => db.InTransactionAsync(AccessScope.System, ActorContext.System, async t =>
            {
                var j = await RepairCore.LoadAsync(t, jobId, forUpdate: true);
                var stillShort = await ReconcileAsync(t, j, null);
                if (stillShort == 0 && j.Status == "waiting_parts")
                {
                    await RepairCore.MoveAsync(t, j, "in_progress", "Parts arrived", null);
                    return 1;
                }
                return 0;
            }, ct));
        }
        return moved;
    }

    /// <summary>The job's parts: what its estimate asks for, fitted, reserved and still requested.</summary>
    public static async Task<IReadOnlyList<Dictionary<string, object?>>> SummaryAsync(DbTx t, Guid jobId) =>
        await t.RowsAsync("""
            WITH est AS (
                   SELECT l.variant_id, sum(l.qty) AS qty
                     FROM repair.estimate e JOIN repair.estimate_line l ON l.estimate_id = e.id
                    WHERE e.id = (SELECT x.id FROM repair.estimate x WHERE x.job_id = @jobId AND x.status = 'approved' ORDER BY x.estimate_no DESC LIMIT 1) AND l.kind = 'part'
                    GROUP BY l.variant_id),
                 fit AS (SELECT variant_id, sum(qty) AS qty FROM repair.repair_line WHERE job_id = @jobId AND kind = 'part' GROUP BY variant_id),
                 res AS (SELECT variant_id, sum(qty) AS qty FROM inv.reservation WHERE source_doc_type = 'repair_job' AND source_doc_id = @jobId GROUP BY variant_id),
                 req AS (SELECT variant_id, sum(qty) AS qty, min(open_since) AS since FROM repair.part_request WHERE job_id = @jobId GROUP BY variant_id),
                 parts AS (SELECT variant_id FROM est UNION SELECT variant_id FROM fit UNION SELECT variant_id FROM res UNION SELECT variant_id FROM req)
            SELECT p.variant_id AS "variantId", v.sku, pr.name_en AS "nameEn", pr.name_ar AS "nameAr", pr.tracking,
                   coalesce(est.qty, 0) AS needed, coalesce(fit.qty, 0) AS fitted, coalesce(res.qty, 0) AS reserved, coalesce(req.qty, 0) AS requested,
                   req.since AS "requestedSince"
              FROM parts p JOIN catalog.variant v ON v.id = p.variant_id JOIN catalog.product pr ON pr.id = v.product_id
              LEFT JOIN est USING (variant_id) LEFT JOIN fit USING (variant_id) LEFT JOIN res USING (variant_id) LEFT JOIN req USING (variant_id)
             ORDER BY pr.name_en
            """, new { jobId });
}

/// <summary>Fills parts requests from arrived stock every few seconds (the job then moves on by itself).</summary>
public sealed class PartsWorker(KiteDb db, ServerContext server, KiteOptions opts, ILogger<PartsWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!opts.Repairs.PartsBackground) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (server.IsConfigured) await RepairParts.FillAsync(db, stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning(e, "Filling parts requests failed; trying again shortly");
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(2, opts.Repairs.PartsIntervalSeconds)), stoppingToken);
        }
    }
}
