namespace Kite.Sales;

/// <summary>What the till decides about a credit sale (14.3).</summary>
public enum CreditOutcome { Ok, NeedsApproval, Approved, OverCap, Hold, Closed }

public sealed record CreditDecisionResult(CreditOutcome Outcome, decimal OverBy)
{
    /// <summary>The sale may be written: within the available credit, or approved within the override cap.</summary>
    public bool Allowed => Outcome is CreditOutcome.Ok or CreditOutcome.Approved;
}

/// <summary>One instalment of a credit sale: its number, due date and amount.</summary>
public sealed record Instalment(int Seq, DateOnly Due, decimal Amount);

/// <summary>An open item to allocate to: its id, due date and what is still open on it.</summary>
public sealed record OpenItem(Guid Id, DateOnly Due, decimal Open);

public sealed record ItemAllocation(Guid ItemId, decimal Amount);

public sealed record AgingBuckets(decimal Current, decimal D1To30, decimal D31To60, decimal D61To90, decimal D90Plus, decimal OnAccount, decimal Total);

/// <summary>
/// Customer credit arithmetic (blueprint 14.2 to 14.5), checked against the vectors of an independent Python implementation
/// (tests/vectors/generators/credit_vectors.py). Amounts are OMR with three decimals.
/// </summary>
public static class CreditMath
{
    /// <summary>Online (14.2): the limit less head office's exposure and the branch's charges head office has not seen yet.</summary>
    public static decimal AvailableOnline(decimal limit, decimal exposure, decimal unsynced) => Math.Max(0m, limit - exposure - unsynced);

    /// <summary>
    /// Offline or when head office did not answer in time (14.2): the lower of the branch's allowance and the limit less the
    /// last known exposure, each less the charges at this branch since the last sync; never more than the per-sale cap.
    /// </summary>
    public static decimal AvailableOffline(decimal allowance, decimal limit, decimal lastExposure, decimal sinceSync, decimal cap) =>
        Math.Max(0m, Math.Min(Math.Min(allowance - sinceSync, limit - lastExposure - sinceSync), cap));

    /// <summary>
    /// The decision (14.3): no credit on hold or closed; over the available credit, or with an invoice overdue past the policy,
    /// needs CREDIT.OVERRIDE; over by more than the override cap is refused even with it.
    /// </summary>
    public static CreditDecisionResult Decide(decimal amount, decimal available, decimal overrideCap, int overdueDays, int blockDays, string status, bool hasApproval)
    {
        if (status is "hold") return new(CreditOutcome.Hold, 0m);
        if (status is "closed") return new(CreditOutcome.Closed, 0m);
        var over = Math.Max(0m, amount - available);
        if (over > overrideCap) return new(CreditOutcome.OverCap, over);
        if (over > 0 || overdueDays > blockDays) return new(hasApproval ? CreditOutcome.Approved : CreditOutcome.NeedsApproval, over);
        return new(CreditOutcome.Ok, over);
    }

    /// <summary>Days the oldest unpaid instalment is past due (0 when nothing is overdue).</summary>
    public static int OverdueDays(DateOnly? oldestDue, DateOnly today) =>
        oldestDue is { } d && today > d ? today.DayNumber - d.DayNumber : 0;

    /// <summary>Equal instalments to the baisa, the spare baisa on the first ones; the first due after the terms, then at the interval.</summary>
    public static IReadOnlyList<Instalment> Instalments(decimal total, int count, DateOnly saleDate, int termsDays, int intervalDays)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        var baisa = (long)decimal.Round(total * 1000m, 0);
        var each = baisa / count;
        var spare = baisa % count;
        return Enumerable.Range(0, count)
            .Select(k => new Instalment(k + 1, saleDate.AddDays(termsDays + k * intervalDays), (each + (k < spare ? 1 : 0)) / 1000m))
            .ToArray();
    }

    /// <summary>Oldest due date first (ties in the order given), never past an item's open amount. Returns what was not used.</summary>
    public static (IReadOnlyList<ItemAllocation> Allocations, decimal Left) AllocateOldestFirst(IReadOnlyList<OpenItem> items, decimal amount)
    {
        var left = amount;
        var result = new List<ItemAllocation>();
        foreach (var it in items.Select((x, i) => (x, i)).OrderBy(p => p.x.Due).ThenBy(p => p.i).Select(p => p.x))
        {
            if (left <= 0) break;
            if (it.Open <= 0) continue;
            var take = Math.Min(left, it.Open);
            result.Add(new ItemAllocation(it.Id, take));
            left -= take;
        }
        return (result, left);
    }

    /// <summary>Open items by days past due (14.5), what is paid on account, and the balance.</summary>
    public static AgingBuckets Aging(IEnumerable<(DateOnly Due, decimal Open)> items, decimal unallocated, DateOnly asOf)
    {
        decimal cur = 0, a = 0, b = 0, c = 0, d = 0;
        foreach (var (due, open) in items)
        {
            var days = asOf.DayNumber - due.DayNumber;
            if (days <= 0) cur += open;
            else if (days <= 30) a += open;
            else if (days <= 60) b += open;
            else if (days <= 90) c += open;
            else d += open;
        }
        return new AgingBuckets(cur, a, b, c, d, unallocated, cur + a + b + c + d - unallocated);
    }
}
