using System.Globalization;
using System.Text.RegularExpressions;
using Kite.Platform;

namespace Kite.Devices;

/// <summary>A checklist item as an inspection uses it (12.4).</summary>
public sealed record ChecklistItem(string Code, string LabelEn, string LabelAr, string Kind, bool Required, int? MinPercent, string OnFail);

/// <summary>One answered item, as kept on the inspection.</summary>
public sealed record InspectionAnswer(string Code, string LabelEn, string LabelAr, string Kind, bool Required, int? MinPercent, string OnFail,
    string? Value, bool? Passed);

/// <summary>What a checklist says about a device: complete or not, which items failed, and the suggested decision.</summary>
public sealed record InspectionResult(bool Complete, IReadOnlyList<string> Missing, IReadOnlyList<string> Failed, string SuggestedOutcome,
    string SuggestedCondition, int? BatteryHealth, IReadOnlyList<InspectionAnswer> Answers);

/// <summary>A scanned or typed code, recognised as an IMEI or a serial number (11.2).</summary>
public sealed record ScannedCode(string Kind, string Value);

/// <summary>
/// Device rules with no I/O (blueprint 11.2, 12.1, 12.2, 12.4). The same rules are in @kite/pos (devices.ts) and both
/// must reproduce tests/vectors/device-math-vectors.json, written by an independent Python implementation.
/// </summary>
public static partial class DeviceMath
{
    public static readonly string[] Conditions = ["brand_new", "used", "refurbished", "damaged", "for_parts"];
    public static readonly string[] Outcomes = ["restock", "refurbish", "for_parts", "supplier_return"];
    private static readonly Currency Omr = Currency.OMR;

    [GeneratedRegex("^[A-Z0-9][A-Z0-9-]{3,39}$")]
    private static partial Regex SerialPattern();

    /// <summary>15 digits, the last one the Luhn check digit of the first 14.</summary>
    public static bool ImeiValid(string? s)
    {
        if (s is null || s.Length != 15) return false;
        var sum = 0;
        for (var i = 0; i < 15; i++)
        {
            var c = s[i];
            if (c is < '0' or > '9') return false;
            var d = c - '0';
            if (i % 2 == 1) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
        }
        return sum % 10 == 0;
    }

    /// <summary>
    /// Recognises a code from the scanner or keyboard: 15 digits are an IMEI and must pass the check digit; 14 or 16
    /// digits are a mistyped IMEI; anything else of 4 to 40 letters, digits and dashes is a serial number (upper case).
    /// </summary>
    public static ScannedCode Classify(string? raw)
    {
        var s = (raw ?? "").Trim().Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        if (s.Length > 0 && s.All(char.IsAsciiDigit))
        {
            if (s.Length == 15) return new(ImeiValid(s) ? "imei" : "invalid_imei", s);
            if (s.Length is 14 or 16) return new("invalid_imei", s);
        }
        return SerialPattern().IsMatch(s) ? new("serial", s) : new("unknown", s);
    }

    /// <summary>Internal labour (12.2): minutes at the hourly rate, rounded to the baisa, half away from zero.</summary>
    public static decimal LabourCost(int minutes, decimal ratePerHour) => Omr.Round(minutes * ratePerHour / 60m);

    /// <summary>The grade's suggested price (12.1): the new price times the grade's percentage, to the nearest step.</summary>
    public static decimal? SuggestedPrice(decimal? newPrice, decimal? gradePercent, decimal step)
    {
        if (newPrice is not { } p || gradePercent is not { } g || step <= 0) return null;
        return Omr.Round(decimal.Round(p * g / 100m / step, 0, MidpointRounding.AwayFromZero) * step);
    }

    /// <summary>
    /// The price a unit sells at unless the cashier changes it: its own selling price; else, new, the item's price;
    /// else the grade's suggestion. A used unit with neither a price nor a grade has no price until one is set.
    /// </summary>
    public static decimal? UnitListPrice(decimal? sellingPrice, string condition, decimal? variantPrice, decimal? gradePercent, decimal step) =>
        sellingPrice ?? (condition == "brand_new" ? variantPrice : SuggestedPrice(variantPrice, gradePercent, step));

    /// <summary>The conditions an inspection may give a unit, for an outcome, from its current condition.</summary>
    public static IReadOnlyList<string> AllowedConditions(string outcome, string current) => outcome switch
    {
        "restock" => [.. (current == "brand_new" ? new[] { "brand_new" } : []), .. (current == "refurbished" ? new[] { "refurbished" } : []), "used", "damaged"],
        "refurbish" => ["used", "damaged"],
        "for_parts" => ["for_parts"],
        "supplier_return" => [.. (current == "brand_new" ? new[] { "brand_new" } : []), .. (current == "refurbished" ? new[] { "refurbished" } : []), "used", "damaged"],
        _ => [],
    };

    /// <summary>
    /// Reads a checklist (12.4). Pass/fail items take "pass" or "fail" (a non-required item may be left out); percent
    /// items take a whole number 0-100 and fail under their minimum; text items are notes. Required items left out
    /// make the inspection incomplete. Suggestion: any failure marked for parts → for parts; else any failure marked
    /// refurbish → refurbish (as used); else restock, damaged if a failure is marked damaged, otherwise as it was.
    /// </summary>
    public static InspectionResult Evaluate(IReadOnlyList<ChecklistItem> items, IReadOnlyDictionary<string, string?> answers, string currentCondition)
    {
        var missing = new List<string>();
        var failed = new List<string>();
        var kept = new List<InspectionAnswer>();
        int? battery = null;
        var worst = 0;   // 0 none, 1 damaged, 2 refurbish, 3 for parts
        foreach (var item in items)
        {
            answers.TryGetValue(item.Code, out var raw);
            var value = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
            bool? passed = null;
            switch (item.Kind)
            {
                case "pass_fail":
                    if (value is null) { if (item.Required) missing.Add(item.Code); break; }
                    value = value.ToLowerInvariant();
                    if (value is not ("pass" or "fail")) throw new KiteException(ErrorCodes.Validation, $"{item.LabelEn}: answer pass or fail.", 400, $"answers.{item.Code}");
                    passed = value == "pass";
                    break;
                case "percent":
                    if (value is null) { if (item.Required) missing.Add(item.Code); break; }
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var pct) || pct > 100)
                        throw new KiteException(ErrorCodes.Validation, $"{item.LabelEn}: give a whole number from 0 to 100.", 400, $"answers.{item.Code}");
                    value = pct.ToString(CultureInfo.InvariantCulture);
                    passed = pct >= (item.MinPercent ?? 0);
                    if (item.Code == "battery") battery = pct;
                    break;
                default:
                    break;   // text: a note, never pass or fail
            }
            if (passed == false)
            {
                failed.Add(item.Code);
                worst = Math.Max(worst, item.OnFail switch { "for_parts" => 3, "refurbish" => 2, _ => 1 });
            }
            kept.Add(new InspectionAnswer(item.Code, item.LabelEn, item.LabelAr, item.Kind, item.Required, item.MinPercent, item.OnFail, value, passed));
        }
        var (outcome, condition) = worst switch
        {
            3 => ("for_parts", "for_parts"),
            2 => ("refurbish", "used"),
            1 => ("restock", "damaged"),
            _ => ("restock", currentCondition is "for_parts" ? "used" : currentCondition),
        };
        return new InspectionResult(missing.Count == 0, missing, failed, outcome, condition, battery, kept);
    }
}
