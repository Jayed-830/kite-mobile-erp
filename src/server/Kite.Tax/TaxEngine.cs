using Kite.Platform;

namespace Kite.Tax;

public enum TaxKind { Standard, Zero, Exempt, OutOfScope, Margin }

public sealed record TaxRatePeriod(decimal RatePercent, DateOnly EffectiveFrom, DateOnly? EffectiveTo)
{
    public bool AppliesOn(DateOnly date) => EffectiveFrom <= date && (EffectiveTo is null || date < EffectiveTo.Value);
}

public sealed record TaxCodeDefinition(string Code, string NameEn, string NameAr, TaxKind Kind, bool ActiveByDefault, IReadOnlyList<TaxRatePeriod> Rates)
{
    /// <summary>Rate in force on a date (blueprint 17.4: lines store the rate at the moment of sale).</summary>
    public decimal RateOn(DateOnly date)
    {
        var matches = Rates.Where(r => r.AppliesOn(date)).ToArray();
        return matches.Length switch
        {
            1 => matches[0].RatePercent,
            0 => throw new KiteException(ErrorCodes.Validation, $"Tax code {Code} has no rate on {date:yyyy-MM-dd}."),
            _ => throw new KiteException(ErrorCodes.Validation, $"Tax code {Code} has overlapping rates on {date:yyyy-MM-dd}."),
        };
    }
}

/// <summary>Result of taxing one line, all amounts rounded to the currency precision.</summary>
public sealed record LineTax(Money Net, Money Tax, Money Gross, decimal RatePercent);

public static class TaxEngine
{
    /// <summary>
    /// Taxes a sale line. With tax-inclusive prices (the Oman default), VAT is extracted:
    /// tax = gross × rate / (100 + rate). Rounding is per line (blueprint 17.4).
    /// </summary>
    public static LineTax ComputeLine(decimal unitPrice, decimal quantity, decimal discount, decimal ratePercent,
        bool pricesIncludeTax, Currency currency, RoundingMode mode = RoundingMode.HalfAwayFromZero)
    {
        ArgumentNullException.ThrowIfNull(currency);
        if (quantity <= 0) throw new KiteException(ErrorCodes.Validation, "Quantity must be greater than zero.", 400, "quantity");
        if (unitPrice < 0) throw new KiteException(ErrorCodes.Validation, "Price cannot be negative.", 400, "unitPrice");
        if (discount < 0) throw new KiteException(ErrorCodes.Validation, "Discount cannot be negative.", 400, "discount");
        if (ratePercent < 0) throw new KiteException(ErrorCodes.Validation, "Tax rate cannot be negative.", 400, "rate");

        var lineAmount = currency.Round(unitPrice * quantity, mode) - currency.Round(discount, mode);
        if (lineAmount < 0) throw new KiteException(ErrorCodes.Validation, "Discount is larger than the line amount.", 400, "discount");

        if (pricesIncludeTax)
        {
            var gross = new Money(lineAmount, currency, mode);
            var tax = new Money(gross.Amount * ratePercent / (100m + ratePercent), currency, mode);
            return new LineTax(gross - tax, tax, gross, ratePercent);
        }
        else
        {
            var net = new Money(lineAmount, currency, mode);
            var tax = net.Multiply(ratePercent / 100m, mode);
            return new LineTax(net, tax, net + tax, ratePercent);
        }
    }

    /// <summary>
    /// VAT under the profit margin mechanism for used goods (blueprint 12.3):
    /// tax = margin × rate / (100 + rate), margin = selling price − purchase price, never below zero.
    /// Only used when the business holds Tax Authority approval.
    /// </summary>
    public static Money MarginVat(Money sellingPriceGross, Money purchasePrice, decimal ratePercent, RoundingMode mode = RoundingMode.HalfAwayFromZero)
    {
        var margin = sellingPriceGross - purchasePrice;
        if (margin.Amount <= 0) return Money.Zero(sellingPriceGross.Currency);
        return new Money(margin.Amount * ratePercent / (100m + ratePercent), sellingPriceGross.Currency, mode);
    }
}

/// <summary>
/// Seed tax codes for Oman. Every seeded code is stored with needs_accountant_review = true
/// until the business's accountant confirms it (blueprint 0.5, 17.4).
/// </summary>
public static class OmanTaxTemplate
{
    /// <summary>Oman's standard VAT took effect on 16 April 2021 at 5%.</summary>
    public static readonly DateOnly VatStart = new(2021, 4, 16);

    public static readonly IReadOnlyList<TaxCodeDefinition> Codes =
    [
        new("VAT-STD",    "Standard rate VAT",            "ضريبة القيمة المضافة بالنسبة الأساسية", TaxKind.Standard,   true,  [new(5m, VatStart, null)]),
        new("VAT-ZERO",   "Zero-rated",                   "خاضع لنسبة الصفر",                      TaxKind.Zero,       true,  [new(0m, VatStart, null)]),
        new("VAT-EXEMPT", "Exempt",                       "معفى",                                  TaxKind.Exempt,     true,  [new(0m, VatStart, null)]),
        new("OUT-SCOPE",  "Out of scope",                 "خارج نطاق الضريبة",                      TaxKind.OutOfScope, true,  [new(0m, VatStart, null)]),
        // Active only after the business records Tax Authority approval (blueprint 12.3).
        new("VAT-MARGIN", "VAT on profit margin (used goods)", "ضريبة على هامش الربح (سلع مستعملة)", TaxKind.Margin, false, [new(5m, VatStart, null)]),
    ];
}
