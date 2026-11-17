using System.Net.Http.Json;
using System.Text.Json;
using Kite.Host.Features;
using Kite.Persistence;
using Kite.Platform;

namespace Kite.Host.Infrastructure;

/// <summary>
/// A branch server's side of customer credit (14.2, 14.5): asks head office before a credit sale while it can reach it
/// (head office reserves the amount), pushes the receivable rows it wrote, and applies head office's copies of accounts,
/// allowances, holds and the policy. On a main or combined server everything is local and this does nothing.
/// </summary>
public sealed class CreditLink(KiteDb db, ServerContext server, BranchLinkService link, ILogger<CreditLink> log)
{
    public bool IsBranch => server.Role == "branch";

    /// <summary>Head office's answer, or null when it did not answer within <paramref name="timeoutMs"/> (the sale is then decided offline).</summary>
    public async Task<CreditAuthoriseResponse?> AuthoriseAsync(CreditAuthoriseRequest req, int timeoutMs)
    {
        if (!IsBranch || !link.HeadOfficeLikelyReachable) return null;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
        try
        {
            var c = await link.MainClientAsync(TimeSpan.FromMilliseconds(timeoutMs), cts.Token);
            if (c is null) return null;
            using var http = c.Value.Http;
            var r = await http.PostAsJsonAsync(c.Value.SyncUrl + "/sync/v1/credit/authorise", req, cts.Token);
            return r.IsSuccessStatusCode ? await r.Content.ReadFromJsonAsync<CreditAuthoriseResponse>(cts.Token) : null;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException or OperationCanceledException)
        {
            log.LogInformation("Head office did not answer the credit check in {Ms} ms: {Error}", timeoutMs, e.Message);
            return null;
        }
    }

    /// <summary>Tells head office a sale it reserved credit for did not go through (best effort: the reservation also expires).</summary>
    public async Task ReleaseAsync(Guid saleId)
    {
        if (!IsBranch) return;
        try
        {
            var c = await link.MainClientAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            if (c is null) return;
            using var http = c.Value.Http;
            await http.PostAsJsonAsync(c.Value.SyncUrl + "/sync/v1/credit/release", new CreditReleaseRequest(saleId));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { log.LogInformation("Releasing a credit reservation failed: {Error}", e.Message); }
    }

    /// <summary>The stream number of head office's account copies applied here.</summary>
    public async Task<(long CreditSeq, long SupplierSeq)> SeqsAsync(CancellationToken ct) =>
        await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => t.QuerySingleOrDefaultAsync<(long, long)>(
            "SELECT coalesce((SELECT credit_seq FROM crm.credit_link LIMIT 1), 0), coalesce((SELECT supplier_seq FROM crm.credit_link LIMIT 1), 0)"), ct);

    /// <summary>
    /// Pushes the receivable rows head office has not confirmed (charges, payments, credit notes written here, offline or not),
    /// and applies the fresh copies of the accounts they touched. Returns how many head office confirmed.
    /// </summary>
    public async Task<int> PushAsync(CancellationToken ct)
    {
        if (!IsBranch) return 0;
        var done = 0;
        for (var batch = 0; batch < 10; batch++)
        {
            var pending = await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => CreditCore.PendingPushAsync(t), ct);
            if (pending.Count == 0) return done;
            var c = await link.MainClientAsync(TimeSpan.FromSeconds(30), ct);
            if (c is null) return done;
            CreditPushResponse? answer = null;
            string? error = null;
            using (var http = c.Value.Http)
            {
                try
                {
                    var r = await http.PostAsJsonAsync(c.Value.SyncUrl + "/sync/v1/credit/push", new CreditPushRequest(pending), ct);
                    if (r.IsSuccessStatusCode) answer = await r.Content.ReadFromJsonAsync<CreditPushResponse>(ct);
                    else error = $"head office answered {(int)r.StatusCode}: {await r.Content.ReadAsStringAsync(ct)}";
                }
                catch (Exception e) when (e is HttpRequestException or JsonException || (e is TaskCanceledException && !ct.IsCancellationRequested)) { error = e.Message; }
            }
            await db.InTransactionAsync(AccessScope.System, ActorContext.System, async t =>
            {
                await t.ExecuteAsync("""
                    INSERT INTO crm.credit_link (business_id, last_push_at, last_push_ok_at, last_push_error)
                    VALUES ((SELECT id FROM core.business LIMIT 1), now(), CASE WHEN @ok THEN now() END, @error)
                    ON CONFLICT (business_id) DO UPDATE SET last_push_at = now(), last_push_ok_at = CASE WHEN @ok THEN now() ELSE crm.credit_link.last_push_ok_at END,
                           last_push_error = @error, updated_at = now()
                    """, new { ok = answer is not null, error = error ?? (answer?.Errors.Count > 0 ? string.Join("; ", answer.Errors) : null) });
                if (answer is null) return 0;
                foreach (var id in answer.Acked)
                    await t.ExecuteAsync("INSERT INTO crm.credit_entry_ack (entry_id) VALUES (@id) ON CONFLICT DO NOTHING", new { id });
                await CreditCore.ApplySnapshotsAsync(t, null, answer.Snapshots, server.BranchId!.Value, advanceSeq: false);
                return 0;
            }, ct);
            if (answer is null)
            {
                log.LogWarning("Pushing credit to head office failed: {Error}", error);
                return done;
            }
            done += answer.Acked.Count;
            if (answer.Acked.Count == 0) return done;   // nothing more head office will take now (its errors are recorded)
        }
        return done;
    }

    /// <summary>Pushes provisional suppliers added here and supplier ledger lines head office has not confirmed (16.1).</summary>
    public async Task<int> PushSuppliersAsync(CancellationToken ct)
    {
        if (!IsBranch) return 0;
        var pending = await db.InTransactionAsync(AccessScope.System, ActorContext.System, t => SupplierCore.PendingPushAsync(t), ct);
        if (pending.Suppliers.Count == 0 && pending.Entries.Count == 0) return 0;
        var c = await link.MainClientAsync(TimeSpan.FromSeconds(30), ct);
        if (c is null) return 0;
        SupplierPushResponse? answer;
        using (var http = c.Value.Http)
        {
            try
            {
                var r = await http.PostAsJsonAsync(c.Value.SyncUrl + "/sync/v1/suppliers/push", pending, ct);
                if (!r.IsSuccessStatusCode) { log.LogWarning("Pushing suppliers: head office answered {Status}", (int)r.StatusCode); return 0; }
                answer = await r.Content.ReadFromJsonAsync<SupplierPushResponse>(ct);
            }
            catch (Exception e) when (e is HttpRequestException or JsonException || (e is TaskCanceledException && !ct.IsCancellationRequested))
            { log.LogWarning("Pushing suppliers to head office failed: {Error}", e.Message); return 0; }
        }
        if (answer is null) return 0;
        await db.InTransactionAsync(AccessScope.System, ActorContext.System, async t =>
        {
            foreach (var s in pending.Suppliers.Where(s => answer.Suppliers.Any(a => a.Id == s.Id)))
                await t.ExecuteAsync("""
                    INSERT INTO purch.supplier_push (supplier_id, version) VALUES (@Id, @Version)
                    ON CONFLICT (supplier_id) DO UPDATE SET version = EXCLUDED.version, acked_at = now()
                    """, new { s.Id, s.Version });
            foreach (var id in answer.AckedEntries)
                await t.ExecuteAsync("INSERT INTO purch.supplier_entry_ack (entry_id) VALUES (@id) ON CONFLICT DO NOTHING", new { id });
            // Head office's copy wins where it already approved or merged the supplier.
            await SupplierCore.ApplySnapshotsAsync(t, answer.Suppliers.Where(a => a.Status != "provisional").ToList(), advanceSeq: false);
            return 0;
        }, ct);
        return answer.Suppliers.Count + answer.AckedEntries.Count;
    }

    public async Task ApplySuppliersAsync(List<SupplierSnapshot>? list, CancellationToken ct)
    {
        if (!IsBranch || list is null || list.Count == 0) return;
        await db.InTransactionAsync(AccessScope.System, ActorContext.System, async t =>
        {
            await SupplierCore.ApplySnapshotsAsync(t, list, advanceSeq: true);
            return 0;
        }, ct);
    }

    /// <summary>Applies what the heartbeat brought: the policy and the accounts changed since the last stream number.</summary>
    public async Task ApplyHeartbeatAsync(CreditPolicy? policy, List<CreditSnapshot>? snapshots, CancellationToken ct)
    {
        if (!IsBranch || (policy is null && (snapshots is null || snapshots.Count == 0))) return;
        await db.InTransactionAsync(AccessScope.System, ActorContext.System, async t =>
        {
            await CreditCore.ApplySnapshotsAsync(t, policy, snapshots ?? [], server.BranchId!.Value, advanceSeq: true);
            return 0;
        }, ct);
    }
}
