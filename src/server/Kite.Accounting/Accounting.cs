using Kite.Platform;

namespace Kite.Accounting;

public enum AccountType { Asset, Liability, Equity, Revenue, Expense }

public sealed record AccountTemplate(string Code, string NameEn, string NameAr, AccountType Type, string? ParentCode, bool IsPostable, bool IsControl);

/// <summary>
/// Default chart of accounts for an Omani mobile retail and repair business (blueprint 17.2).
/// Editable after setup. Branch is a dimension on journal lines, not separate accounts.
/// </summary>
public static class ChartTemplate
{
    public static readonly IReadOnlyList<AccountTemplate> Accounts =
    [
        new("1000", "Assets", "الأصول", AccountType.Asset, null, false, false),
        new("1100", "Cash on hand", "النقدية في الصندوق", AccountType.Asset, "1000", true, false),
        new("1150", "Card clearing", "تسوية مدفوعات البطاقات", AccountType.Asset, "1000", true, false),
        new("1200", "Bank", "البنك", AccountType.Asset, "1000", true, false),
        new("1300", "Accounts receivable", "الذمم المدينة", AccountType.Asset, "1000", true, true),
        new("1310", "Interbranch due from", "مستحق من الفروع", AccountType.Asset, "1000", true, true),
        new("1400", "Inventory: new devices", "المخزون: أجهزة جديدة", AccountType.Asset, "1000", true, true),
        new("1410", "Inventory: used devices", "المخزون: أجهزة مستعملة", AccountType.Asset, "1000", true, true),
        new("1420", "Inventory: accessories", "المخزون: الإكسسوارات", AccountType.Asset, "1000", true, true),
        new("1430", "Inventory: spare parts", "المخزون: قطع الغيار", AccountType.Asset, "1000", true, true),
        new("1450", "Goods in transit", "بضاعة في الطريق", AccountType.Asset, "1000", true, true),
        new("1460", "Repair work in progress", "أعمال صيانة قيد التنفيذ", AccountType.Asset, "1000", true, true),
        new("1500", "VAT input", "ضريبة المدخلات", AccountType.Asset, "1000", true, true),
        new("2000", "Liabilities", "الالتزامات", AccountType.Liability, null, false, false),
        new("2100", "Supplier payables", "الذمم الدائنة للموردين", AccountType.Liability, "2000", true, true),
        new("2110", "Goods received not invoiced", "بضاعة مستلمة غير مفوترة", AccountType.Liability, "2000", true, true),
        new("2200", "VAT output", "ضريبة المخرجات", AccountType.Liability, "2000", true, true),
        new("2210", "VAT payable", "ضريبة القيمة المضافة المستحقة", AccountType.Liability, "2000", true, true),
        new("2300", "Customer deposits", "دفعات العملاء المقدمة", AccountType.Liability, "2000", true, true),
        new("2310", "Customer store credit", "رصيد العملاء لدى المتجر", AccountType.Liability, "2000", true, true),
        new("2320", "Trade-in clearing", "تسوية الاستبدال", AccountType.Liability, "2000", true, true),
        new("2400", "Commissions payable", "العمولات المستحقة", AccountType.Liability, "2000", true, true),
        new("2500", "Interbranch due to", "مستحق للفروع", AccountType.Liability, "2000", true, true),
        new("3000", "Equity", "حقوق الملكية", AccountType.Equity, null, false, false),
        new("3100", "Capital", "رأس المال", AccountType.Equity, "3000", true, false),
        new("3200", "Owner drawings", "مسحوبات المالك", AccountType.Equity, "3000", true, false),
        new("3300", "Retained earnings", "الأرباح المحتجزة", AccountType.Equity, "3000", true, false),
        new("3400", "Current year result", "نتيجة السنة الحالية", AccountType.Equity, "3000", true, false),
        new("3900", "Opening balance equity", "حقوق ملكية الأرصدة الافتتاحية", AccountType.Equity, "3000", true, false),
        new("4000", "Revenue", "الإيرادات", AccountType.Revenue, null, false, false),
        new("4100", "Sales: new devices", "المبيعات: أجهزة جديدة", AccountType.Revenue, "4000", true, false),
        new("4110", "Sales: used devices", "المبيعات: أجهزة مستعملة", AccountType.Revenue, "4000", true, false),
        new("4120", "Sales: accessories", "المبيعات: الإكسسوارات", AccountType.Revenue, "4000", true, false),
        new("4130", "Sales: spare parts", "المبيعات: قطع الغيار", AccountType.Revenue, "4000", true, false),
        new("4200", "Repair labour income", "إيرادات أجور الصيانة", AccountType.Revenue, "4000", true, false),
        new("4210", "Repair parts income", "إيرادات قطع الصيانة", AccountType.Revenue, "4000", true, false),
        new("4900", "Sales returns and discounts", "مردودات وخصومات المبيعات", AccountType.Revenue, "4000", true, false),
        new("5000", "Cost of sales", "تكلفة المبيعات", AccountType.Expense, null, false, false),
        new("5100", "Cost: new devices", "التكلفة: أجهزة جديدة", AccountType.Expense, "5000", true, false),
        new("5110", "Cost: used devices", "التكلفة: أجهزة مستعملة", AccountType.Expense, "5000", true, false),
        new("5120", "Cost: accessories", "التكلفة: الإكسسوارات", AccountType.Expense, "5000", true, false),
        new("5200", "Cost: repair parts", "التكلفة: قطع الصيانة", AccountType.Expense, "5000", true, false),
        new("6000", "Expenses", "المصروفات", AccountType.Expense, null, false, false),
        new("6100", "Rent", "الإيجار", AccountType.Expense, "6000", true, false),
        new("6200", "Salaries", "الرواتب", AccountType.Expense, "6000", true, false),
        new("6300", "Commission expense", "مصروف العمولات", AccountType.Expense, "6000", true, false),
        new("6400", "Cash over and short", "فروقات النقدية", AccountType.Expense, "6000", true, false),
        new("6500", "Bad debts", "الديون المعدومة", AccountType.Expense, "6000", true, false),
        new("6600", "Warranty expense", "مصروف الضمان", AccountType.Expense, "6000", true, false),
        new("6700", "Stock write-offs and shrinkage", "شطب المخزون والعجز", AccountType.Expense, "6000", true, false),
        new("6800", "Card fees", "رسوم البطاقات", AccountType.Expense, "6000", true, false),
        new("6900", "General expenses", "مصروفات عامة", AccountType.Expense, "6000", true, false),
    ];
}

public sealed record JournalLineDraft(string AccountCode, decimal Debit, decimal Credit, Guid? BranchId = null, string? Memo = null);

/// <summary>
/// Builds a journal and checks the same rules the database enforces (blueprint 9.4), so
/// errors are reported clearly before reaching the database.
/// </summary>
public sealed class JournalDraft
{
    private readonly List<JournalLineDraft> _lines = [];

    public JournalDraft(DateOnly date, string sourceDocType, Guid sourceDocId, string postingKind, Currency currency)
    {
        Date = date;
        SourceDocType = sourceDocType;
        SourceDocId = sourceDocId;
        PostingKind = postingKind;
        Currency = currency;
    }

    public DateOnly Date { get; }
    public string SourceDocType { get; }
    public Guid SourceDocId { get; }
    public string PostingKind { get; }
    public Currency Currency { get; }
    public IReadOnlyList<JournalLineDraft> Lines => _lines;

    public JournalDraft Debit(string account, decimal amount, Guid? branch = null, string? memo = null) =>
        Add(new JournalLineDraft(account, Currency.Round(amount), 0m, branch, memo));

    public JournalDraft Credit(string account, decimal amount, Guid? branch = null, string? memo = null) =>
        Add(new JournalLineDraft(account, 0m, Currency.Round(amount), branch, memo));

    private JournalDraft Add(JournalLineDraft line)
    {
        if (line.Debit < 0 || line.Credit < 0)
            throw new KiteException(ErrorCodes.Validation, "Journal amounts cannot be negative; swap debit and credit instead.");
        if (line.Debit == 0 && line.Credit == 0)
            throw new KiteException(ErrorCodes.Validation, "A journal line needs an amount.");
        _lines.Add(line);
        return this;
    }

    public decimal TotalDebit => _lines.Sum(l => l.Debit);
    public decimal TotalCredit => _lines.Sum(l => l.Credit);

    public void Validate()
    {
        if (_lines.Count < 2)
            throw new KiteException(ErrorCodes.Validation, "A journal needs at least two lines.");
        if (TotalDebit != TotalCredit)
            throw new KiteException(ErrorCodes.Validation, $"Journal does not balance: debit {TotalDebit}, credit {TotalCredit}.");
    }

    /// <summary>The reversal that mirrors this journal exactly (blueprint 17.6).</summary>
    public JournalDraft Reverse(DateOnly date)
    {
        var r = new JournalDraft(date, SourceDocType, SourceDocId, PostingKind + ".reversal", Currency);
        foreach (var l in _lines)
            r._lines.Add(l with { Debit = l.Credit, Credit = l.Debit });
        return r;
    }
}

public sealed record FiscalPeriodPlan(int FiscalYear, int PeriodNo, DateOnly StartsOn, DateOnly EndsOn);

public static class FiscalCalendar
{
    /// <summary>
    /// Twelve monthly periods for the fiscal year that contains <paramref name="date"/>.
    /// The fiscal year is named after the calendar year in which it starts.
    /// </summary>
    public static IReadOnlyList<FiscalPeriodPlan> YearContaining(DateOnly date, int startMonth)
    {
        if (startMonth is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(startMonth));
        var startYear = date.Month >= startMonth ? date.Year : date.Year - 1;
        var start = new DateOnly(startYear, startMonth, 1);
        return Enumerable.Range(0, 12)
            .Select(i =>
            {
                var s = start.AddMonths(i);
                return new FiscalPeriodPlan(startYear, i + 1, s, s.AddMonths(1).AddDays(-1));
            })
            .ToArray();
    }
}
