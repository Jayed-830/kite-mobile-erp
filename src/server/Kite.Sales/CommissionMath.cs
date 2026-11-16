using Kite.Platform;

namespace Kite.Sales;

/// <summary>What a repair earned, excluding VAT, and what it cost (blueprint 15.1).</summary>
public sealed record CommissionBasis(decimal Labour, decimal PartsRevenue, decimal PartsCost, decimal External)
{
    /// <summary>Repair profit: labour and parts revenue, less parts cost and outside work.</summary>
    public decimal Profit => Labour + PartsRevenue - PartsCost - External;
}

public sealed record CommissionResult(decimal Base, decimal Raw, decimal Clamped, decimal Amount);

/// <summary>
/// Commission arithmetic (blueprint 15.1, 15.2), the same as tests/vectors/generators/commission_vectors.py and
/// @kite/pos commission.ts: a fixed amount, or a rate of labour or of repair profit (never of a loss), then the rule's
/// minimum and maximum per job, then the technician's share; each step rounded half away from zero to the baisa.
/// </summary>
public static class CommissionMath
{
    private static readonly Currency Omr = Currency.OMR;

    public static CommissionResult Compute(string basis, decimal? fixedAmount, decimal? ratePercent, decimal? min, decimal? max, CommissionBasis b, decimal sharePercent)
    {
        ArgumentNullException.ThrowIfNull(b);
        if (sharePercent is <= 0 or > 100) throw new KiteException(ErrorCodes.CommissionShares, "A share is above 0% and at most 100%.", 400, "share");
        if (min is { } mn && max is { } mx && mn > mx) throw new KiteException(ErrorCodes.CommissionRule, "The minimum is above the maximum.", 400, "min");
        decimal baseAmount, raw;
        switch (basis)
        {
            case "fixed":
                if (fixedAmount is not { } f || f < 0) throw new KiteException(ErrorCodes.CommissionRule, "A fixed commission has an amount of zero or more.", 400, "amount");
                baseAmount = 0m;
                raw = f;
                break;
            case "labour" or "profit":
                if (ratePercent is not { } r || r is < 0 or > 100) throw new KiteException(ErrorCodes.CommissionRule, "A rate is 0% to 100%.", 400, "rate");
                baseAmount = basis == "labour" ? b.Labour : b.Profit;
                raw = Omr.Round(r * Math.Max(baseAmount, 0m) / 100m);
                break;
            default:
                throw new KiteException(ErrorCodes.CommissionRule, "A rule pays a fixed amount, a share of labour, or a share of repair profit.", 400, "basis");
        }
        var clamped = raw;
        if (min is { } lo && clamped < lo) clamped = lo;
        if (max is { } hi && clamped > hi) clamped = hi;
        return new CommissionResult(baseAmount, raw, clamped, Omr.Round(clamped * sharePercent / 100m));
    }
}
