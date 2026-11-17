namespace Kite.Sales;

/// <summary>The result of matching one invoice line to its receipt and order (16.2): "ok", "qty" (more than is open) or "price" (outside tolerance).</summary>
public sealed record MatchResult(string Status, decimal Reference, decimal PriceDifference, decimal LineDifference);

/// <summary>
/// Purchasing arithmetic (blueprint 16.2, 16.3), shared by the server and the web app (@kite/pos purchase.ts) and checked against
/// tests/vectors/purchase-math-vectors.json, which an independent Python implementation writes. Amounts are in baisa (3 decimals),
/// rounded half away from zero; prices are before VAT.
/// </summary>
public static class PurchaseMath
{
    public static decimal Round3(decimal x) => decimal.Round(x, 3, MidpointRounding.AwayFromZero);

    /// <summary>Quantity times price, to the baisa.</summary>
    public static decimal LineNet(decimal qty, decimal unitPrice) => Round3(qty * unitPrice);

    /// <summary>VAT on an amount before VAT, to the baisa.</summary>
    public static decimal LineTax(decimal net, decimal ratePercent) => Round3(net * ratePercent / 100m);

    /// <summary>
    /// An amount spread over weights by cumulative rounding: share k = round(amount x (w1..wk) / total) - round(amount x (w1..wk-1) / total),
    /// so the shares add up to the amount exactly. With no weight at all it is spread evenly.
    /// </summary>
    public static decimal[] Spread(decimal amount, IReadOnlyList<decimal> weights)
    {
        if (weights.Count == 0) return [];
        if (weights.Any(w => w < 0)) throw new ArgumentOutOfRangeException(nameof(weights));
        var total = weights.Sum();
        var w = total == 0 ? weights.Select(_ => 1m).ToArray() : weights.ToArray();
        if (total == 0) total = w.Length;
        var result = new decimal[w.Length];
        decimal cum = 0, before = 0;
        for (var k = 0; k < w.Length; k++)
        {
            cum += w[k];
            var upto = Round3(amount * cum / total);
            result[k] = upto - before;
            before = upto;
        }
        return result;
    }

    /// <summary>Landed costs (freight, customs...) added to receipt lines: each charge spread by the lines' value or quantity.</summary>
    public static decimal[] SpreadCharges(IReadOnlyList<(decimal Value, decimal Qty)> lines, IReadOnlyList<(decimal Amount, string Basis)> charges)
    {
        var shares = new decimal[lines.Count];
        foreach (var (amount, basis) in charges)
        {
            var s = Spread(amount, basis == "qty" ? lines.Select(l => l.Qty).ToArray() : lines.Select(l => l.Value).ToArray());
            for (var k = 0; k < shares.Length; k++) shares[k] += s[k];
        }
        return shares;
    }

    /// <summary>
    /// Three-way match of an invoice line (16.2): no more than the receipt still has open, and a price within tolerance of the order's
    /// (the receipt's when there was no order). Within tolerance means the unit price differs by no more than <paramref name="tolerancePct"/>
    /// of the reference, or the whole line differs by no more than <paramref name="toleranceAmount"/>.
    /// </summary>
    public static MatchResult Match(decimal invoiceQty, decimal invoicePrice, decimal openQty, decimal receiptPrice, decimal? orderPrice,
        decimal tolerancePct, decimal toleranceAmount)
    {
        var reference = orderPrice ?? receiptPrice;
        var diff = invoicePrice - reference;
        var lineDiff = LineNet(invoiceQty, invoicePrice) - LineNet(invoiceQty, reference);
        if (invoiceQty > openQty) return new MatchResult("qty", reference, diff, lineDiff);
        var withinPct = Math.Abs(diff) <= Round3(reference * tolerancePct / 100m);
        var withinAmount = Math.Abs(lineDiff) <= toleranceAmount;
        return new MatchResult(withinPct || withinAmount ? "ok" : "price", reference, diff, lineDiff);
    }

    /// <summary>What an invoice clears from goods received not invoiced: all that is open when it invoices all that is open, else its share.</summary>
    public static decimal Clearing(decimal invoiceQty, decimal openQty, decimal openValue) =>
        openQty <= 0 ? 0m : invoiceQty >= openQty ? openValue : Round3(openValue * invoiceQty / openQty);

    /// <summary>
    /// A price difference split (16.2): the share of the invoiced quantity still on hand corrects the stock's cost, the rest goes to
    /// purchase price variance.
    /// </summary>
    public static (decimal Inventory, decimal Ppv) SplitVariance(decimal variance, decimal invoiceQty, decimal onHandQty)
    {
        if (invoiceQty <= 0) return (0m, variance);
        var share = Math.Clamp(onHandQty, 0m, invoiceQty);
        var inv = Round3(variance * share / invoiceQty);
        return (inv, variance - inv);
    }

    /// <summary>An amount split evenly over n parts to the baisa, the spare baisa on the first parts (signed amounts keep their sign).</summary>
    public static decimal[] SplitEven(decimal amount, int n)
    {
        if (n < 1) return [];
        var baisa = (long)decimal.Round(Math.Abs(amount) * 1000m, 0);
        var sign = amount < 0 ? -1 : 1;
        var each = baisa / n;
        var spare = baisa % n;
        return Enumerable.Range(0, n).Select(k => sign * (each + (k < spare ? 1 : 0)) / 1000m).ToArray();
    }
}
