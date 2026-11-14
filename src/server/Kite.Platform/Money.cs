using System.Globalization;

namespace Kite.Platform;

public enum RoundingMode
{
    /// <summary>0.0005 rounds to 0.001 (common commercial rounding).</summary>
    HalfAwayFromZero,
    /// <summary>Banker's rounding: 0.0005 rounds to 0.000, 0.0015 to 0.002.</summary>
    HalfEven,
}

/// <summary>Currency and its minor-unit precision. OMR has 3 decimals (baisa).</summary>
public sealed record Currency(string Code, int Decimals)
{
    public static readonly Currency OMR = new("OMR", 3);

    public decimal Round(decimal amount, RoundingMode mode = RoundingMode.HalfAwayFromZero) =>
        Math.Round(amount, Decimals, mode == RoundingMode.HalfEven ? MidpointRounding.ToEven : MidpointRounding.AwayFromZero);
}

/// <summary>
/// Exact money (blueprint 1.1 decision 7). Always decimal, never floating point; amounts are
/// kept at the currency's precision; mixing currencies throws.
/// </summary>
public readonly record struct Money : IComparable<Money>
{
    public Money(decimal amount, Currency currency, RoundingMode mode = RoundingMode.HalfAwayFromZero)
    {
        ArgumentNullException.ThrowIfNull(currency);
        Currency = currency;
        Amount = currency.Round(amount, mode);
    }

    public decimal Amount { get; }
    public Currency Currency { get; }

    public static Money Zero(Currency currency) => new(0m, currency);
    public static Money Omr(decimal amount) => new(amount, Currency.OMR);

    public bool IsZero => Amount == 0m;
    public bool IsNegative => Amount < 0m;

    public static Money operator +(Money a, Money b) { Same(a, b); return new(a.Amount + b.Amount, a.Currency); }
    public static Money operator -(Money a, Money b) { Same(a, b); return new(a.Amount - b.Amount, a.Currency); }
    public static Money operator -(Money a) => new(-a.Amount, a.Currency);

    public Money Multiply(decimal factor, RoundingMode mode = RoundingMode.HalfAwayFromZero) =>
        new(Amount * factor, Currency, mode);

    /// <summary>
    /// Splits the amount by weights so the parts always add up exactly to the original
    /// (no lost or invented baisa). Used for shared commissions and landed costs.
    /// </summary>
    public IReadOnlyList<Money> Allocate(IReadOnlyList<decimal> weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        if (weights.Count == 0) throw new ArgumentException("At least one weight is required.", nameof(weights));
        if (weights.Any(w => w < 0)) throw new ArgumentException("Weights cannot be negative.", nameof(weights));
        var total = weights.Sum();
        if (total == 0) throw new ArgumentException("Weights must not all be zero.", nameof(weights));

        var unit = 1m / Pow10(Currency.Decimals);
        var parts = new decimal[weights.Count];
        decimal allocated = 0;
        for (var i = 0; i < weights.Count; i++)
        {
            // Truncate toward zero so the remainder has the same sign as the amount.
            parts[i] = Math.Truncate(Amount * weights[i] / total / unit) * unit;
            allocated += parts[i];
        }
        var remainder = Amount - allocated;
        var step = remainder >= 0 ? unit : -unit;
        // Hand out the remainder one minor unit at a time, largest weights first.
        var order = Enumerable.Range(0, weights.Count).OrderByDescending(i => weights[i]).ThenBy(i => i).ToArray();
        var k = 0;
        while (remainder != 0)
        {
            var i = order[k % order.Length];
            if (weights[i] > 0) { parts[i] += step; remainder -= step; }
            k++;
        }
        var currency = Currency;
        return parts.Select(p => new Money(p, currency)).ToArray();
    }

    public int CompareTo(Money other) { Same(this, other); return Amount.CompareTo(other.Amount); }

    public static bool operator <(Money a, Money b) => a.CompareTo(b) < 0;
    public static bool operator >(Money a, Money b) => a.CompareTo(b) > 0;
    public static bool operator <=(Money a, Money b) => a.CompareTo(b) <= 0;
    public static bool operator >=(Money a, Money b) => a.CompareTo(b) >= 0;

    /// <summary>Invariant text with full precision, e.g. "12.500".</summary>
    public string ToInvariantString() => Amount.ToString("F" + Currency.Decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    public override string ToString() => $"{ToInvariantString()} {Currency.Code}";

    /// <summary>Parses the API's money string ("12.500"). Rejects more decimals than the currency allows.</summary>
    public static Money Parse(string text, Currency currency)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(currency);
        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
            throw new FormatException($"'{text}' is not a valid amount.");
        if (decimal.Round(value, currency.Decimals) != value)
            throw new FormatException($"'{text}' has more than {currency.Decimals} decimals.");
        return new Money(value, currency);
    }

    private static decimal Pow10(int n)
    {
        decimal r = 1;
        for (var i = 0; i < n; i++) r *= 10;
        return r;
    }

    private static void Same(Money a, Money b)
    {
        if (a.Currency.Code != b.Currency.Code)
            throw new InvalidOperationException($"Cannot combine {a.Currency.Code} and {b.Currency.Code}.");
    }
}
