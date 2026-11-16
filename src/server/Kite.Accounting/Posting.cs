using System.Text.Json;
using System.Text.Json.Serialization;
using Kite.Platform;

namespace Kite.Accounting;

/// <summary>
/// The figures a completed business document hands to the posting engine (blueprint 17.1). A figure is either one
/// amount (<see cref="Scalar"/>) or amounts by a key such as tender ("cash", "card") or product type
/// ("accessory", "device") (<see cref="Keyed"/>). Amounts are already rounded to the currency precision.
/// </summary>
public sealed class PostingFacts
{
    private readonly Dictionary<string, decimal> _scalars = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, decimal>> _keyed = new(StringComparer.Ordinal);

    public PostingFacts Set(string name, decimal amount) { _scalars[name] = amount; return this; }

    public PostingFacts Add(string name, string key, decimal amount)
    {
        if (!_keyed.TryGetValue(name, out var d)) _keyed[name] = d = new Dictionary<string, decimal>(StringComparer.Ordinal);
        d[key] = d.GetValueOrDefault(key) + amount;
        return this;
    }

    public bool TryScalar(string name, out decimal amount) => _scalars.TryGetValue(name, out amount);
    public bool TryKeyed(string name, out IReadOnlyDictionary<string, decimal> amounts)
    {
        var ok = _keyed.TryGetValue(name, out var d);
        amounts = d ?? (IReadOnlyDictionary<string, decimal>)new Dictionary<string, decimal>();
        return ok;
    }

    public IReadOnlyDictionary<string, decimal> Scalars => _scalars;
    public IReadOnlyDictionary<string, Dictionary<string, decimal>> KeyedFigures => _keyed;
}

/// <summary>Where a rule line posts: one account code, or a code chosen by the key of a keyed figure.</summary>
public sealed record AccountSelector(
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("by")] string? By,
    [property: JsonPropertyName("map")] IReadOnlyDictionary<string, string>? Map,
    [property: JsonPropertyName("default")] string? Default);

public sealed record PostingRuleLine(
    [property: JsonPropertyName("side")] string Side,                 // "debit" | "credit"
    [property: JsonPropertyName("account")] AccountSelector Account,
    [property: JsonPropertyName("amount")] string Amount,             // the figure's name
    [property: JsonPropertyName("memo")] string? Memo = null);

/// <summary>A posting rule held as data (acct.posting_rule.definition): versioned, effective-dated, synced from the main server.</summary>
public sealed record PostingRuleDefinition(
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("lines")] IReadOnlyList<PostingRuleLine> Lines)
{
    private static readonly JsonSerializerOptions Options = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static PostingRuleDefinition Parse(string json)
    {
        var d = JsonSerializer.Deserialize<PostingRuleDefinition>(json, Options)
            ?? throw new KiteException(ErrorCodes.Validation, "Empty posting rule.");
        d.Check();
        return d;
    }

    /// <summary>Static checks a rule must pass before it is stored or used.</summary>
    public void Check()
    {
        if (Lines is null || Lines.Count < 2) throw new KiteException(ErrorCodes.Validation, "A posting rule needs at least two lines.");
        foreach (var l in Lines)
        {
            if (l.Side is not ("debit" or "credit")) throw new KiteException(ErrorCodes.Validation, $"Rule line side must be debit or credit, not '{l.Side}'.");
            if (string.IsNullOrWhiteSpace(l.Amount)) throw new KiteException(ErrorCodes.Validation, "Rule line needs an amount.");
            var a = l.Account ?? throw new KiteException(ErrorCodes.Validation, "Rule line needs an account.");
            if ((a.Code is null) == (a.By is null))
                throw new KiteException(ErrorCodes.Validation, "A rule account is either a code or a selector by key, not both.");
            if (a.By is not null && (a.Map is null || a.Map.Count == 0) && a.Default is null)
                throw new KiteException(ErrorCodes.Validation, $"Selector by {a.By} needs a map or a default account.");
        }
    }
}

/// <summary>
/// Builds the journal for a document from a rule and the document's figures (blueprint 17.1). A negative amount
/// posts on the other side, so one rule covers a cash over and a cash short. Lines with zero amounts are skipped; a
/// rule that yields no lines yields no journal (for example the cost journal of a sale of services only).
/// </summary>
public static class PostingEngine
{
    public static JournalDraft? Build(PostingRuleDefinition rule, PostingFacts facts, DateOnly date, string sourceDocType, Guid sourceDocId,
        string postingKind, Currency currency, Guid? branchId = null)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(facts);
        var draft = new JournalDraft(date, sourceDocType, sourceDocId, postingKind, currency);
        // Same account and side are merged so a journal stays readable (one VAT line, one cash line).
        var merged = new Dictionary<(string Account, bool Debit, string? Memo), decimal>();
        foreach (var line in rule.Lines)
        {
            foreach (var (account, amount) in Expand(line, facts))
            {
                if (amount == 0m) continue;
                var debit = (line.Side == "debit") == (amount > 0m);
                var key = (account, debit, line.Memo);
                merged[key] = merged.GetValueOrDefault(key) + Math.Abs(amount);
            }
        }
        foreach (var ((account, debit, memo), amount) in merged.Where(m => m.Value != 0m).OrderBy(m => m.Key.Debit ? 0 : 1).ThenBy(m => m.Key.Account, StringComparer.Ordinal))
        {
            if (debit) draft.Debit(account, amount, branchId, memo);
            else draft.Credit(account, amount, branchId, memo);
        }
        if (draft.Lines.Count == 0) return null;
        // A balanced rule with a missing figure would silently drop value; refuse instead.
        draft.Validate();
        return draft;
    }

    /// <summary>The (account, signed amount) pairs one rule line produces.</summary>
    private static IEnumerable<(string Account, decimal Amount)> Expand(PostingRuleLine line, PostingFacts facts)
    {
        var sel = line.Account;
        if (facts.TryKeyed(line.Amount, out var keyed))
        {
            if (sel.Code is not null)
            {
                yield return (sel.Code, keyed.Values.Sum());
                yield break;
            }
            foreach (var (k, v) in keyed.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                if (v == 0m) continue;
                string? code = null;
                if (sel.Map is not null && sel.Map.TryGetValue(k, out var mapped)) code = mapped;
                code ??= sel.Default ?? throw new KiteException(ErrorCodes.Validation,
                    $"Posting rule has no account for {sel.By} '{k}' (figure {line.Amount}).");
                yield return (code, v);
            }
            yield break;
        }
        if (facts.TryScalar(line.Amount, out var scalar))
        {
            if (sel.Code is not null) { yield return (sel.Code, scalar); yield break; }
            yield return (sel.Default ?? throw new KiteException(ErrorCodes.Validation,
                $"Figure {line.Amount} is a single amount, but the rule selects the account by {sel.By} with no default."), scalar);
            yield break;
        }
        // A figure the document does not have contributes nothing (e.g. no card tenders in a cash sale).
    }
}

/// <summary>The posting kinds and default rules (blueprint 17.3), stored per business and editable as data.</summary>
public static class PostingRuleTemplates
{
    public const string Sale = "pos.sale";
    public const string SaleCost = "pos.sale_cost";
    public const string CreditNote = "pos.credit_note";
    public const string CreditNoteCost = "pos.credit_note_cost";
    public const string ShiftVariance = "pos.shift_variance";
    public const string CashPayOut = "pos.cash_pay_out";
    public const string StockAdjustment = "inv.adjustment";

    private static readonly IReadOnlyDictionary<string, string> Revenue = new Dictionary<string, string>
    { ["device"] = "4100", ["accessory"] = "4120", ["consumable"] = "4120", ["spare_part"] = "4130", ["service"] = "4200" };
    private static readonly IReadOnlyDictionary<string, string> Cost = new Dictionary<string, string>
    { ["device"] = "5100", ["accessory"] = "5120", ["consumable"] = "5120", ["spare_part"] = "5200" };
    private static readonly IReadOnlyDictionary<string, string> Inventory = new Dictionary<string, string>
    { ["device"] = "1400", ["accessory"] = "1420", ["consumable"] = "1420", ["spare_part"] = "1430" };
    private static readonly IReadOnlyDictionary<string, string> Tenders = new Dictionary<string, string>
    { ["cash"] = "1100", ["card"] = "1150", ["bank_transfer"] = "1200" };

    private static AccountSelector Code(string c) => new(c, null, null, null);
    private static AccountSelector By(string by, IReadOnlyDictionary<string, string> map) => new(null, by, map, null);

    public static readonly IReadOnlyList<(string EventType, PostingRuleDefinition Definition)> All =
    [
        (Sale, new("Sale: tenders received; revenue by product type and VAT output; cash rounding to over/short", [
            new("debit", By("tender", Tenders), "tenders"),
            new("credit", By("product_type", Revenue), "net_by_product_type"),
            new("credit", Code("2200"), "tax"),
            new("credit", Code("6400"), "cash_rounding"),
        ])),
        (SaleCost, new("Cost of a sale: cost of sales by product type against inventory", [
            new("debit", By("product_type", Cost), "cost_by_product_type"),
            new("credit", By("product_type", Inventory), "cost_by_product_type"),
        ])),
        (CreditNote, new("Credit note: sales returns and VAT output reversed against the refund", [
            new("debit", Code("4900"), "net_by_product_type"),
            new("debit", Code("2200"), "tax"),
            new("credit", By("tender", Tenders), "tenders"),
            new("debit", Code("6400"), "cash_rounding"),
        ])),
        (CreditNoteCost, new("Return to stock: inventory against cost of sales", [
            new("debit", By("product_type", Inventory), "cost_by_product_type"),
            new("credit", By("product_type", Cost), "cost_by_product_type"),
        ])),
        (ShiftVariance, new("Cash over (positive) or short (negative) at shift close", [
            new("debit", Code("1100"), "variance"),
            new("credit", Code("6400"), "variance"),
        ])),
        (CashPayOut, new("Cash paid out of the drawer: general expenses (vouchers come with the Expenses module)", [
            new("debit", Code("6900"), "amount"),
            new("credit", Code("1100"), "amount"),
        ])),
        (StockAdjustment, new("Stock adjustment, count and opening stock", [
            new("debit", By("product_type", Inventory), "gain_by_product_type"),
            new("credit", Code("6700"), "gain_by_product_type"),
            new("debit", Code("6700"), "loss_by_product_type"),
            new("credit", By("product_type", Inventory), "loss_by_product_type"),
            new("debit", By("product_type", Inventory), "opening_by_product_type"),
            new("credit", Code("3900"), "opening_by_product_type"),
        ])),
    ];
}
