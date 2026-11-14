namespace Kite.Platform;

/// <summary>A switchable module (blueprint 2.2, 2.4).</summary>
public sealed record ModuleDefinition(string Code, string NameEn, string NameAr, bool AlwaysOn, bool DefaultEnabled, IReadOnlyList<string> DependsOn);

public static class ModuleCatalog
{
    public static readonly IReadOnlyList<ModuleDefinition> All =
    [
        new("core",       "Platform",                      "المنصة",                    true,  true,  []),
        new("iam",        "Identity and access",           "الهوية والصلاحيات",          true,  true,  ["core"]),
        new("catalog",    "Catalog",                       "دليل المنتجات",              true,  true,  ["core"]),
        new("inv",        "Inventory",                     "المخزون",                   true,  true,  ["catalog"]),
        new("device",     "Serialized devices and IMEI",   "الأجهزة المتسلسلة وIMEI",    false, true,  ["inv"]),
        new("refurb",     "Condition and refurbishment",   "الحالة والتجديد",            false, false, ["device"]),
        new("sales",      "POS and sales",                 "نقاط البيع والمبيعات",        true,  true,  ["catalog", "inv", "tax"]),
        new("tradein",    "Trade-in",                      "الاستبدال",                 false, false, ["device", "refurb", "sales"]),
        new("repair",     "Repairs",                       "الصيانة",                   false, true,  ["inv"]),
        new("comm",       "Technician commissions",        "عمولات الفنيين",             false, false, ["repair"]),
        new("credit",     "Customer credit",               "ائتمان العملاء",             false, false, ["sales"]),
        new("purch",      "Purchasing and suppliers",      "المشتريات والموردون",         false, true,  ["inv"]),
        new("exp",        "Expenses",                      "المصروفات",                 false, true,  ["core"]),
        new("tax",        "Tax",                           "الضرائب",                   true,  true,  ["core"]),
        new("einv",       "E-invoicing (Fawtara)",         "الفوترة الإلكترونية (فوترة)", false, false, ["sales", "tax"]),
        new("acct_ui",    "Accounting screens",            "شاشات المحاسبة",             false, true,  ["core"]),
        new("rpt",        "Reporting",                     "التقارير",                  true,  true,  ["core"]),
        new("notif",      "WhatsApp notifications",        "إشعارات واتساب",             false, false, ["core"]),
        new("sync",       "Multi-branch sync",             "المزامنة بين الفروع",         false, false, ["core"]),
    ];

    private static readonly Dictionary<string, ModuleDefinition> ByCode = All.ToDictionary(m => m.Code, StringComparer.Ordinal);

    public static ModuleDefinition Get(string code) =>
        ByCode.TryGetValue(code, out var m) ? m : throw new KiteException(ErrorCodes.Validation, $"Unknown module '{code}'.", 400, "code");

    public static bool Exists(string code) => ByCode.ContainsKey(code);

    /// <summary>Default module states for a new business.</summary>
    public static IReadOnlyDictionary<string, bool> Defaults() =>
        All.ToDictionary(m => m.Code, m => m.AlwaysOn || m.DefaultEnabled, StringComparer.Ordinal);

    /// <summary>
    /// Applies requested changes to current states and returns the new states, or throws if a
    /// rule is broken: always-on modules cannot be disabled, a module cannot be enabled while a
    /// dependency is off, and a module cannot be disabled while an enabled module depends on it.
    /// </summary>
    public static IReadOnlyDictionary<string, bool> Apply(IReadOnlyDictionary<string, bool> current, IReadOnlyDictionary<string, bool> changes)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(changes);
        var next = All.ToDictionary(m => m.Code, m => m.AlwaysOn || (current.TryGetValue(m.Code, out var v) ? v : m.DefaultEnabled), StringComparer.Ordinal);
        foreach (var (code, enabled) in changes)
        {
            var def = Get(code);
            if (def.AlwaysOn && !enabled)
                throw new KiteException(ErrorCodes.ModuleAlwaysOn, $"{def.NameEn} is always on and cannot be switched off.", 400, code);
            next[code] = enabled || def.AlwaysOn;
        }
        foreach (var def in All.Where(m => next[m.Code]))
        {
            var missing = def.DependsOn.Where(d => !next[d]).ToArray();
            if (missing.Length > 0)
                throw new KiteException(ErrorCodes.ModuleDependency,
                    $"{def.NameEn} needs {string.Join(", ", missing.Select(d => Get(d).NameEn))} switched on first.", 400, def.Code);
        }
        return next;
    }
}
