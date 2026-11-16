using Kite.Platform;

namespace Kite.Sales;

/// <summary>What happens to a job's deposit at delivery (13.5): how much the invoice applies, what the customer still pays,
/// and what goes back to them.</summary>
public sealed record DeliverySplit(decimal Applied, decimal Due, decimal Excess, decimal AppliedTax, decimal ExcessTax);

/// <summary>
/// Repair arithmetic with no I/O (blueprint 13.3, 13.5, 13.7). The same rules are in @kite/pos (repair.ts); both are checked
/// against tests/vectors/repair-math-vectors.json.
/// </summary>
public static class RepairMath
{
    private static readonly Currency Omr = Currency.OMR;

    /// <summary>Labour charged by the hour: the hourly rate for the minutes, in baisa, half away from zero.</summary>
    public static decimal LabourPrice(int minutes, decimal hourlyRate)
    {
        if (minutes is < 1 or > 6000) throw new KiteException(ErrorCodes.Validation, "Labour is 1 to 6000 minutes.", 400, "minutes");
        if (hourlyRate < 0) throw new KiteException(ErrorCodes.Validation, "The hourly rate cannot be negative.", 400, "hourlyRate");
        return Omr.Round(hourlyRate * minutes / 60m);
    }

    /// <summary>The VAT inside a deposit when deposits are a VAT point (13.7): amount x rate / (100 + rate).</summary>
    public static decimal DepositTax(decimal amount, decimal ratePercent) =>
        amount <= 0 || ratePercent <= 0 ? 0m : Omr.Round(amount * ratePercent / (100m + ratePercent));

    /// <summary>
    /// The share of the VAT held in a job's deposits that goes with part of them: proportional, and all of it when the part
    /// is the whole balance, so applying and refunding the whole balance always takes back exactly what was declared.
    /// </summary>
    public static decimal TaxShare(decimal heldTax, decimal balance, decimal part)
    {
        if (part < 0 || part > balance) throw new KiteException(ErrorCodes.Validation, "More than the deposit held.", 400, "amount");
        if (heldTax == 0 || part == 0) return 0m;
        return part == balance ? heldTax : Omr.Round(heldTax * part / balance);
    }

    /// <summary>
    /// At delivery the invoice takes the deposit up to its total; the customer pays the rest; a deposit larger than the
    /// invoice gives the excess back. The deposit's VAT (deposits as a VAT point) splits between the two.
    /// </summary>
    public static DeliverySplit Split(decimal invoiceGross, decimal depositBalance, decimal depositTax)
    {
        if (invoiceGross < 0) throw new KiteException(ErrorCodes.Validation, "The invoice cannot be negative.", 400, "gross");
        if (depositBalance < 0) throw new KiteException(ErrorCodes.Validation, "The deposit cannot be negative.", 400, "deposit");
        var applied = Math.Min(invoiceGross, depositBalance);
        var excess = depositBalance - applied;
        var excessTax = TaxShare(depositTax, depositBalance, excess);
        return new DeliverySplit(applied, invoiceGross - applied, excess, depositTax - excessTax, excessTax);
    }

    /// <summary>Spreads the parts cost of a job over its invoice's part lines by their net amounts (the last takes the rest).</summary>
    public static decimal[] AllocateCost(decimal cost, IReadOnlyList<decimal> weights)
    {
        if (weights.Count == 0) return [];
        var total = weights.Sum();
        var result = new decimal[weights.Count];
        if (total <= 0) { result[^1] = cost; return result; }
        var left = cost;
        for (var i = 0; i < weights.Count - 1; i++)
        {
            result[i] = Omr.Round(cost * weights[i] / total);
            left -= result[i];
        }
        result[^1] = left;
        return result;
    }

    /// <summary>The part of a fitted line's cost a return of <paramref name="qty"/> takes back: proportional, the last return takes what is left.</summary>
    public static decimal ReturnCost(decimal fittedQty, decimal fittedCost, decimal returnedQty, decimal returnedCost, decimal qty)
    {
        if (qty <= 0 || returnedQty + qty > fittedQty) throw new KiteException(ErrorCodes.Validation, "More would come back than was fitted.", 400, "qty");
        return returnedQty + qty == fittedQty ? fittedCost - returnedCost : Omr.Round(fittedCost * qty / fittedQty);
    }
}
