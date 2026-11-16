using System.Globalization;
using System.Text;
using Kite.Platform;
using Kite.Tax;

namespace Kite.Sales;

/// <summary>What the catalog says about one sellable variant on the sale date, in one branch.</summary>
public sealed record PricedItem(
    Guid VariantId, string Sku, string ProductType, string Tracking, string DescriptionEn, string DescriptionAr,
    Guid TaxCodeId, string TaxKind, decimal TaxRate, decimal? ListPrice, decimal? MinPrice, int WarrantyDays, bool Active);

/// <summary>A cart line as the terminal sends it. <see cref="UnitPrice"/> is null when the list price is used. A serialized
/// item names its device unit (11.5) and is sold one at a time.</summary>
public sealed record CartLine(Guid VariantId, decimal Qty, decimal? UnitPrice = null, decimal Discount = 0m, Guid? ApprovalId = null, Guid? DeviceUnitId = null);

/// <summary>A cashier's discount limit (10.2): within <see cref="Percent"/> of the line and <see cref="Amount"/>, whichever is lower.</summary>
public sealed record DiscountLimit(decimal Percent, decimal Amount, bool Unlimited)
{
    public static readonly DiscountLimit None = new(0m, 0m, false);
    public static readonly DiscountLimit Any = new(100m, decimal.MaxValue, true);

    public decimal AllowedOn(decimal lineAmount, Currency currency) =>
        Unlimited ? lineAmount : Math.Min(currency.Round(lineAmount * Percent / 100m), Amount);
}

public sealed record PricedLine(
    int LineNo, PricedItem Item, decimal Qty, decimal ListPrice, decimal UnitPrice, decimal Discount,
    decimal Net, decimal Tax, decimal Gross, bool NeedsPriceOverride, bool NeedsDiscountApproval, Guid? ApprovalId);

public sealed record SaleTotals(decimal Net, decimal Discount, decimal Tax, decimal Gross);

public sealed record TenderInput(string Method, decimal Amount, decimal? Tendered = null, string? CardTerminal = null,
    string? ApprovalCode = null, string? Reference = null);

public sealed record CheckedTender(string Method, decimal Amount, decimal? Tendered, decimal? Change, string? CardTerminal,
    string? ApprovalCode, string? Reference);

public sealed record CheckedPayments(IReadOnlyList<CheckedTender> Tenders, decimal CashRounding, decimal Paid);

/// <summary>Amounts of an original sale line, and how much of it earlier credit notes already returned.</summary>
public sealed record ReturnableLine(decimal Qty, decimal Net, decimal Tax, decimal Gross, decimal Discount,
    decimal ReturnedQty, decimal ReturnedNet, decimal ReturnedTax, decimal ReturnedGross, decimal ReturnedDiscount);

public sealed record ReturnAmounts(decimal Net, decimal Tax, decimal Gross, decimal Discount);

public static class PosMath
{
    public static readonly string[] Methods = ["cash", "card", "bank_transfer"];
    private static readonly Currency Omr = Currency.OMR;

    /// <summary>Prices one cart line (10.2): list price unless overridden, discount checked against the cashier's limit,
    /// tax per line. Approval needs are reported, not enforced: the caller holds the permissions and approvals.</summary>
    public static PricedLine PriceLine(int lineNo, CartLine line, PricedItem item, bool pricesIncludeTax, DiscountLimit limit)
    {
        if (!item.Active) throw new KiteException(ErrorCodes.ItemInactive, $"{item.Sku} is inactive and cannot be sold.", 400, $"lines[{lineNo - 1}]");
        if (item.Tracking != "none" && line.DeviceUnitId is null)
            throw new KiteException(ErrorCodes.DeviceUnitRequired, $"{item.Sku} is sold by IMEI or serial number: scan the device.", 400, $"lines[{lineNo - 1}]");
        if (item.Tracking == "none" && line.DeviceUnitId is not null)
            throw new KiteException(ErrorCodes.Validation, $"{item.Sku} is not serialized; it has no device units.", 400, $"lines[{lineNo - 1}].deviceUnitId");
        if (line.DeviceUnitId is not null && line.Qty != 1)
            throw new KiteException(ErrorCodes.Validation, "A device is sold one at a time.", 400, $"lines[{lineNo - 1}].qty");
        if (item.TaxKind == "margin")
            throw new KiteException(ErrorCodes.Validation, $"{item.Sku} uses the margin VAT scheme, which comes with trade-ins (Phase 7).", 400, $"lines[{lineNo - 1}]");
        if (item.ListPrice is null && line.UnitPrice is null)
            throw new KiteException(ErrorCodes.NoPrice, $"{item.Sku} has no price on this date.", 400, $"lines[{lineNo - 1}]");
        if (line.Qty <= 0 || line.Qty != decimal.Round(line.Qty, 3))
            throw new KiteException(ErrorCodes.Validation, "Quantity must be above zero with at most 3 decimals.", 400, $"lines[{lineNo - 1}].qty");
        if (line.Discount < 0 || line.Discount != Omr.Round(line.Discount))
            throw new KiteException(ErrorCodes.Validation, "Discount must be zero or more with at most 3 decimals.", 400, $"lines[{lineNo - 1}].discount");
        if (line.UnitPrice is < 0 || (line.UnitPrice is { } up && up != Omr.Round(up)))
            throw new KiteException(ErrorCodes.Validation, "Price must be zero or more with at most 3 decimals.", 400, $"lines[{lineNo - 1}].unitPrice");

        var list = item.ListPrice ?? line.UnitPrice!.Value;
        var unit = line.UnitPrice ?? list;
        var lineAmount = Omr.Round(unit * line.Qty);
        if (line.Discount > lineAmount)
            throw new KiteException(ErrorCodes.Validation, "The discount is larger than the line amount.", 400, $"lines[{lineNo - 1}].discount");
        var tax = TaxEngine.ComputeLine(unit, line.Qty, line.Discount, item.TaxRate, pricesIncludeTax, Omr);

        var priceChanged = item.ListPrice is null || unit != list;
        var effectiveUnit = (lineAmount - line.Discount) / line.Qty;
        var belowMinimum = item.MinPrice is { } min && effectiveUnit < min;
        var aboveLimit = line.Discount > limit.AllowedOn(lineAmount, Omr);
        return new PricedLine(lineNo, item, line.Qty, list, unit, line.Discount, tax.Net.Amount, tax.Tax.Amount, tax.Gross.Amount,
            priceChanged || belowMinimum, aboveLimit, line.ApprovalId);
    }

    /// <summary>Invoice-level rounding (policy "invoice"): tax is worked out once per rate on the lines' total and spread
    /// back over the lines, so the invoice tax can differ by a few baisa from the sum of per-line rounding.</summary>
    public static IReadOnlyList<PricedLine> ApplyInvoiceRounding(IReadOnlyList<PricedLine> lines, bool pricesIncludeTax)
    {
        var result = lines.ToArray();
        foreach (var group in lines.Select((l, i) => (l, i)).GroupBy(x => x.l.Item.TaxRate))
        {
            var rate = group.Key;
            var items = group.ToArray();
            var baseTotal = items.Sum(x => pricesIncludeTax ? x.l.Gross : x.l.Net);
            var tax = pricesIncludeTax ? Omr.Round(baseTotal * rate / (100m + rate)) : Omr.Round(baseTotal * rate / 100m);
            if (baseTotal == 0) continue;
            var shares = new Money(tax, Omr).Allocate(items.Select(x => pricesIncludeTax ? x.l.Gross : x.l.Net).ToArray());
            for (var k = 0; k < items.Length; k++)
            {
                var (l, i) = items[k];
                var t = shares[k].Amount;
                result[i] = pricesIncludeTax ? l with { Tax = t, Net = l.Gross - t } : l with { Tax = t, Gross = l.Net + t };
            }
        }
        return result;
    }

    public static SaleTotals Totals(IReadOnlyList<PricedLine> lines) =>
        new(lines.Sum(l => l.Net), lines.Sum(l => l.Discount), lines.Sum(l => l.Tax), lines.Sum(l => l.Gross));

    /// <summary>Rounds a cash amount to the policy step (0 = no rounding), half away from zero.</summary>
    public static decimal RoundCash(decimal amount, decimal step) =>
        step <= 0 ? amount : Omr.Round(decimal.Round(amount / step, 0, MidpointRounding.AwayFromZero) * step);

    /// <summary>
    /// Checks the tenders of a sale (positive amounts) or of a refund (pass <paramref name="refund"/> and positive
    /// amounts; they are stored negative). Cash is rounded to the policy step: the rounding is what the customer pays
    /// (or gets) on top of the gross total. At most one cash tender; cards need the bank terminal and approval code
    /// (standalone terminals, 10.4); bank transfers need a reference.
    /// </summary>
    public static CheckedPayments CheckTenders(IReadOnlyList<TenderInput> tenders, decimal gross, decimal cashStep, bool refund = false)
    {
        // Nothing to pay: a free sale, or one that cash rounding brings to zero (e.g. 0.002 with a 0.005 step).
        if (tenders.Count == 0 && RoundCash(gross, cashStep) == 0) return new CheckedPayments([], -gross, 0m);
        if (tenders.Count == 0) throw new KiteException(ErrorCodes.PaymentMismatch, "Add a payment.", 400, "payments");
        if (tenders.Count(t => t.Method == "cash") > 1) throw new KiteException(ErrorCodes.PaymentMismatch, "Use one cash payment.", 400, "payments");
        var list = new List<CheckedTender>();
        for (var i = 0; i < tenders.Count; i++)
        {
            var t = tenders[i];
            var f = $"payments[{i}]";
            if (!Methods.Contains(t.Method)) throw new KiteException(ErrorCodes.Validation, "Payment method must be cash, card or bank_transfer.", 400, f + ".method");
            if (t.Amount <= 0 || t.Amount != Omr.Round(t.Amount)) throw new KiteException(ErrorCodes.Validation, "Each payment must be above zero with at most 3 decimals.", 400, f + ".amount");
            if (t.Method == "card" && !refund && (string.IsNullOrWhiteSpace(t.CardTerminal) || string.IsNullOrWhiteSpace(t.ApprovalCode)))
                throw new KiteException(ErrorCodes.Validation, "A card payment needs the bank terminal and the approval code from its slip.", 400, f);
            if (t.Method == "bank_transfer" && string.IsNullOrWhiteSpace(t.Reference))
                throw new KiteException(ErrorCodes.Validation, "A bank transfer needs its reference.", 400, f + ".reference");
            if (t.Method != "cash" && t.Tendered is not null) throw new KiteException(ErrorCodes.Validation, "Only cash has an amount tendered.", 400, f + ".tendered");
            if (t.Tendered is { } td && (refund || td < t.Amount || td != Omr.Round(td)))
                throw new KiteException(ErrorCodes.Validation, "Cash tendered must cover the cash amount.", 400, f + ".tendered");
            list.Add(new CheckedTender(t.Method, t.Amount, t.Tendered, t.Tendered is { } tt ? tt - t.Amount : null,
                Clean(t.CardTerminal), Clean(t.ApprovalCode), Clean(t.Reference)));
        }

        var nonCash = list.Where(t => t.Method != "cash").Sum(t => t.Amount);
        var cash = list.FirstOrDefault(t => t.Method == "cash");
        if (nonCash > gross) throw new KiteException(ErrorCodes.PaymentMismatch, "Card and transfer payments are more than the total.", 400, "payments");
        decimal rounding = 0m;
        if (cash is null)
        {
            if (nonCash != gross) throw new KiteException(ErrorCodes.PaymentMismatch, $"Payments {nonCash:0.000} do not match the total {gross:0.000}.", 400, "payments");
        }
        else
        {
            var due = gross - nonCash;
            var rounded = RoundCash(due, cashStep);
            rounding = rounded - due;
            if (cash.Amount != rounded)
                throw new KiteException(ErrorCodes.PaymentMismatch, $"Cash should be {rounded:0.000} (total {gross:0.000}{(rounding == 0 ? "" : $", rounded by {rounding:+0.000;-0.000}")}).", 400, "payments");
        }
        return new CheckedPayments(list, rounding, gross + rounding);
    }

    /// <summary>The share of an original line that a return takes: proportional, with the last return taking whatever
    /// is left so that a fully returned line nets to exactly zero.</summary>
    public static ReturnAmounts ApportionReturn(ReturnableLine o, decimal qty)
    {
        if (qty <= 0) throw new KiteException(ErrorCodes.Validation, "Return quantity must be above zero.", 400, "qty");
        var left = o.Qty - o.ReturnedQty;
        if (qty > left) throw new KiteException(ErrorCodes.ReturnTooMany, $"Only {left:0.###} of this line can still be returned.", 409, "qty");
        if (qty == left)
            return new(o.Net - o.ReturnedNet, o.Tax - o.ReturnedTax, o.Gross - o.ReturnedGross, o.Discount - o.ReturnedDiscount);
        decimal Share(decimal v) => Omr.Round(v * qty / o.Qty);
        var tax = Share(o.Tax);
        var gross = Share(o.Gross);
        return new(gross - tax, tax, gross, Share(o.Discount));
    }

    /// <summary>Cash the drawer should hold (10.4).</summary>
    public static decimal ExpectedCash(decimal openingFloat, decimal cashPayments, decimal payIns, decimal payOuts, decimal drops) =>
        openingFloat + cashPayments + payIns - payOuts - drops;

    /// <summary>Totals a denomination count ("20" → 3 pieces) against the denominations the policy lists.</summary>
    public static decimal CountTotal(IReadOnlyDictionary<string, int>? count, IReadOnlyCollection<decimal> denominations)
    {
        if (count is null) return 0m;
        var total = 0m;
        foreach (var (k, pieces) in count)
        {
            if (!decimal.TryParse(k, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) || !denominations.Contains(d))
                throw new KiteException(ErrorCodes.Validation, $"{k} is not one of the denominations in the POS policy.", 400, "count");
            if (pieces < 0) throw new KiteException(ErrorCodes.Validation, "Pieces cannot be negative.", 400, "count");
            total += d * pieces;
        }
        return Omr.Round(total);
    }

    /// <summary>
    /// The receipt QR payload: TLV (tag, length, UTF-8 value) in base64 with 1 seller name, 2 VAT number,
    /// 3 time (ISO 8601 UTC), 4 total with VAT, 5 VAT total, 6 invoice UUID. The Fawtara format is confirmed in Phase 9.
    /// </summary>
    public static string QrPayload(string seller, string? vatNumber, DateTimeOffset at, decimal total, decimal vat, Guid uuid)
    {
        using var ms = new MemoryStream();
        void Tag(byte tag, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length > 255) bytes = bytes[..255];
            ms.WriteByte(tag);
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes);
        }
        Tag(1, seller);
        Tag(2, vatNumber ?? "");
        Tag(3, at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        Tag(4, total.ToString("0.000", CultureInfo.InvariantCulture));
        Tag(5, vat.ToString("0.000", CultureInfo.InvariantCulture));
        Tag(6, uuid.ToString());
        return Convert.ToBase64String(ms.ToArray());
    }

    public static IReadOnlyDictionary<byte, string> ReadQr(string payload)
    {
        var bytes = Convert.FromBase64String(payload);
        var d = new Dictionary<byte, string>();
        for (var i = 0; i + 1 < bytes.Length;)
        {
            var tag = bytes[i];
            var len = bytes[i + 1];
            d[tag] = Encoding.UTF8.GetString(bytes, i + 2, len);
            i += 2 + len;
        }
        return d;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
