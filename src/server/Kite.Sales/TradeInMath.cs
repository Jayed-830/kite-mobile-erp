using Kite.Platform;

namespace Kite.Sales;

/// <summary>A deduction applied to a trade-in: a fixed amount, or a percentage of the guide value.</summary>
public sealed record TradeInDeduction(string Code, string Kind, decimal Value);

/// <summary>A trade-in's valuation (12.4): the guide value, each deduction's amount, their total, and the offer.</summary>
public sealed record TradeInValuation(decimal? Guide, IReadOnlyList<decimal> Amounts, decimal DeductionsTotal, decimal? Offered);

/// <summary>A line sold under the profit margin mechanism (12.3).</summary>
public sealed record MarginLineTax(decimal Net, decimal Tax, decimal Gross, decimal Margin);

/// <summary>
/// Trade-in arithmetic with no I/O, the same rules as @kite/pos (tradein.ts) and an independent Python implementation
/// (tests/vectors/tradein-math-vectors.json). Amounts are OMR with three decimals; rounding is half away from zero.
/// </summary>
public static class TradeInMath
{
    private static readonly Currency Omr = Currency.OMR;

    /// <summary>
    /// Values a trade-in: each percentage deduction is that share of the guide value, rounded to the baisa; the offer is the
    /// guide value less every deduction, never below zero. Without a guide value there is no offer, and only fixed deductions.
    /// </summary>
    public static TradeInValuation Value(decimal? guide, IReadOnlyList<TradeInDeduction> deductions)
    {
        ArgumentNullException.ThrowIfNull(deductions);
        if (guide is < 0 || (guide is { } g0 && g0 != Omr.Round(g0)))
            throw new KiteException(ErrorCodes.Validation, "The guide value is zero or more, in baisa.", 400, "guideValue");
        var amounts = new List<decimal>();
        foreach (var d in deductions)
        {
            if (d.Value < 0) throw new KiteException(ErrorCodes.Validation, $"Deduction {d.Code} cannot be negative.", 400, "deductions");
            var amount = d.Kind switch
            {
                "amount" => d.Value == Omr.Round(d.Value) ? d.Value
                    : throw new KiteException(ErrorCodes.Validation, $"Deduction {d.Code} is in baisa.", 400, "deductions"),
                "percent" when d.Value > 100 => throw new KiteException(ErrorCodes.Validation, $"Deduction {d.Code} is at most 100%.", 400, "deductions"),
                "percent" when guide is null => throw new KiteException(ErrorCodes.TradeInNoValue,
                    $"Deduction {d.Code} is a percentage of the guide value, and this model and grade have no guide value.", 400, "deductions"),
                "percent" => Omr.Round(guide!.Value * d.Value / 100m),
                _ => throw new KiteException(ErrorCodes.Validation, "A deduction is an amount or a percent.", 400, "deductions"),
            };
            amounts.Add(amount);
        }
        var total = amounts.Sum();
        decimal? offered = guide is { } g ? Math.Max(0m, g - total) : null;
        return new TradeInValuation(guide, amounts, total, offered);
    }

    /// <summary>
    /// Whether a staff member without TRADEIN.APPROVE needs a manager for this value (12.4): there is no guide offer to
    /// compare with, the value is above the staff limit, or it is more than the allowed percentage above the offer.
    /// </summary>
    public static bool NeedsApproval(decimal agreed, decimal? offered, decimal staffLimit, decimal overGuidePercent)
    {
        if (agreed < 0 || agreed != Omr.Round(agreed)) throw new KiteException(ErrorCodes.Validation, "The agreed value is zero or more, in baisa.", 400, "agreedValue");
        if (offered is null) return true;
        if (agreed > staffLimit) return true;
        return agreed > Omr.Round(offered.Value * (100m + overGuidePercent) / 100m);
    }

    /// <summary>
    /// VAT on a line under the profit margin mechanism: the line amount (after discount) is what the customer pays, the margin
    /// is that less the purchase price, and the tax inside it is margin × rate / (100 + rate), never below zero.
    /// </summary>
    public static MarginLineTax MarginLine(decimal gross, decimal purchasePrice, decimal ratePercent)
    {
        if (gross < 0 || purchasePrice < 0 || ratePercent < 0) throw new KiteException(ErrorCodes.Validation, "Margin amounts cannot be negative.", 400);
        var margin = gross - purchasePrice;
        var tax = margin > 0 ? Omr.Round(margin * ratePercent / (100m + ratePercent)) : 0m;
        return new MarginLineTax(gross - tax, tax, gross, margin);
    }

    /// <summary>How much of what is left on a trade-in a sale takes (never more than the sale's balance), and what stays.</summary>
    public static (decimal Applied, decimal Left) Apply(decimal outstanding, decimal due)
    {
        if (outstanding < 0 || due < 0) throw new KiteException(ErrorCodes.Validation, "Amounts cannot be negative.", 400);
        var applied = Math.Min(outstanding, due);
        return (applied, outstanding - applied);
    }

    /// <summary>The last four characters of a civil ID, for showing it masked.</summary>
    public static string Last4(string civilId)
    {
        var clean = new string(civilId.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return clean.Length <= 4 ? clean : clean[^4..];
    }
}
