namespace Kite.Platform;

/// <summary>
/// Permission codes. Must match the iam.permission rows seeded by the migrations (V0009, V0011, V0012, V0014); a unit test
/// compares the two so they cannot drift.
/// </summary>
public static class Permissions
{
    public const string SetupBusinessEdit = "SETUP.BUSINESS.EDIT";
    public const string BranchesManage = "BRANCHES.MANAGE";
    public const string SettingsView = "SETTINGS.VIEW";
    public const string SettingsModulesEdit = "SETTINGS.MODULES.EDIT";
    public const string TerminalsApprove = "TERMINALS.APPROVE";
    public const string UsersView = "USERS.VIEW";
    public const string UsersManage = "USERS.MANAGE";
    public const string RolesManage = "ROLES.MANAGE";
    public const string AuditView = "AUDIT.VIEW";
    public const string AuditVerify = "AUDIT.VERIFY";
    public const string TaxManage = "TAX.MANAGE";
    public const string TaxReview = "TAX.REVIEW";
    public const string AcctCoaManage = "ACCT.COA.MANAGE";
    public const string AcctJournalManual = "ACCT.JOURNAL.MANUAL";
    public const string AcctControlAccountPost = "ACCT.CONTROL_ACCOUNT.POST";
    public const string AcctPeriodClose = "ACCT.PERIOD.CLOSE";
    public const string PosSell = "POS.SELL";
    public const string PosDiscountAboveLimit = "POS.DISCOUNT.ABOVE_LIMIT";
    public const string PosPriceOverride = "POS.PRICE.OVERRIDE";
    public const string PosSaleVoid = "POS.SALE.VOID";
    public const string PosRefund = "POS.REFUND";
    public const string PosReturnNoReceipt = "POS.RETURN.NO_RECEIPT";
    public const string PosCreditSale = "POS.CREDIT_SALE";
    public const string PosDrawerNoSale = "POS.DRAWER.NO_SALE";
    public const string PosReceiptReprint = "POS.RECEIPT.REPRINT";
    public const string PosShiftAcceptVariance = "POS.SHIFT.ACCEPT_VARIANCE";
    public const string PosEmergencyActivate = "POS.EMERGENCY.ACTIVATE";
    public const string DeviceImeiManualEntry = "DEVICE.IMEI.MANUAL_ENTRY";
    public const string RepairCredentialView = "REPAIR.CREDENTIAL.VIEW";
    public const string CreditOverride = "CREDIT.OVERRIDE";
    public const string CreditWriteOff = "CREDIT.WRITE_OFF";
    // Phase 3 (V0011)
    public const string ServersManage = "SERVERS.MANAGE";
    public const string LicenceManage = "LICENCE.MANAGE";
    public const string BackupView = "BACKUP.VIEW";
    public const string BackupManage = "BACKUP.MANAGE";
    // Phase 4 (V0012)
    public const string CatalogView = "CATALOG.VIEW";
    public const string CatalogManage = "CATALOG.MANAGE";
    public const string CatalogPriceManage = "CATALOG.PRICE.MANAGE";
    public const string InvView = "INV.VIEW";
    public const string InvAdjust = "INV.ADJUST";
    public const string InvCount = "INV.COUNT";
    public const string CustomersView = "CUSTOMERS.VIEW";
    public const string CustomersManage = "CUSTOMERS.MANAGE";
    public const string PosShiftOpen = "POS.SHIFT.OPEN";
    public const string PosCashPayOut = "POS.CASH.PAY_OUT";
    public const string PosReportsView = "POS.REPORTS.VIEW";
    public const string PosPolicyManage = "POS.POLICY.MANAGE";
    public const string ApprovalsDecide = "APPROVALS.DECIDE";
    // Phase 5 (V0014)
    public const string DeviceView = "DEVICE.VIEW";
    public const string DeviceInspect = "DEVICE.INSPECT";
    public const string DeviceRefurbish = "DEVICE.REFURBISH";
    public const string DevicePrice = "DEVICE.PRICE";
    public const string DeviceSellDamaged = "DEVICE.SELL_DAMAGED";
    public const string DeviceWriteOff = "DEVICE.WRITE_OFF";
    public const string DeviceHold = "DEVICE.HOLD";
    public const string DeviceSettings = "DEVICE.SETTINGS";
    // Phase 6 (V0015)
    public const string DeviceImeiCorrect = "DEVICE.IMEI.CORRECT";
    public const string DeviceConflicts = "DEVICE.CONFLICTS";
    public const string DeviceBlocklist = "DEVICE.BLOCKLIST";
    public const string DeviceCount = "DEVICE.COUNT";
    // Phase 7 (V0016)
    public const string TradeInCreate = "TRADEIN.CREATE";
    public const string TradeInApprove = "TRADEIN.APPROVE";
    public const string TradeInStoreCredit = "TRADEIN.STORE_CREDIT";
    public const string TradeInPayout = "TRADEIN.PAYOUT";
    public const string TradeInView = "TRADEIN.VIEW";
    public const string TradeInSettings = "TRADEIN.SETTINGS";
    public const string RepairView = "REPAIR.VIEW";
    public const string RepairIntake = "REPAIR.INTAKE";
    public const string RepairWork = "REPAIR.WORK";
    public const string RepairAssign = "REPAIR.ASSIGN";
    public const string RepairCancel = "REPAIR.CANCEL";
    public const string RepairSettings = "REPAIR.SETTINGS";
    public const string NotifSettings = "NOTIF.SETTINGS";
    public const string NotifReply = "NOTIF.REPLY";
    public const string RepairInvoiceCorrect = "REPAIR.INVOICE.CORRECT";
    public const string CommView = "COMM.VIEW";
    public const string CommOwn = "COMM.OWN";
    public const string CommRules = "COMM.RULES";
    public const string CommApprove = "COMM.APPROVE";
    public const string CommPay = "COMM.PAY";

    public static readonly IReadOnlyList<string> All = typeof(Permissions)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!)
        .OrderBy(c => c, StringComparer.Ordinal)
        .ToArray();
}

/// <summary>System role templates created by the setup wizard (blueprint 0.1, 10.5).</summary>
public sealed record RoleTemplate(string Code, string NameEn, string NameAr, IReadOnlyList<string> Permissions,
    decimal DiscountLimitPercent = 0m, decimal DiscountLimitAmount = 0m);

public static class RoleTemplates
{
    public const string OwnerCode = "owner";

    public static readonly IReadOnlyList<RoleTemplate> All =
    [
        new(OwnerCode, "Owner", "المالك", Permissions.All),
        new("branch_manager", "Branch manager", "مدير الفرع",
        [
            Permissions.SettingsView, Permissions.TerminalsApprove, Permissions.UsersView, Permissions.AuditView,
            Permissions.PosSell, Permissions.PosDiscountAboveLimit, Permissions.PosPriceOverride, Permissions.PosSaleVoid,
            Permissions.PosRefund, Permissions.PosReturnNoReceipt, Permissions.PosCreditSale, Permissions.PosDrawerNoSale,
            Permissions.PosReceiptReprint, Permissions.PosShiftAcceptVariance, Permissions.PosEmergencyActivate,
            Permissions.DeviceImeiManualEntry, Permissions.CreditOverride, Permissions.BackupView,
            Permissions.CatalogView, Permissions.CatalogManage, Permissions.CatalogPriceManage, Permissions.InvView, Permissions.InvAdjust,
            Permissions.InvCount, Permissions.CustomersView, Permissions.CustomersManage, Permissions.PosShiftOpen, Permissions.PosCashPayOut,
            Permissions.PosReportsView, Permissions.ApprovalsDecide,
            Permissions.DeviceView, Permissions.DeviceInspect, Permissions.DeviceRefurbish, Permissions.DevicePrice, Permissions.DeviceSellDamaged,
            Permissions.DeviceWriteOff, Permissions.DeviceHold, Permissions.DeviceSettings,
            Permissions.DeviceImeiCorrect, Permissions.DeviceConflicts, Permissions.DeviceBlocklist, Permissions.DeviceCount,
            Permissions.TradeInCreate, Permissions.TradeInApprove, Permissions.TradeInStoreCredit, Permissions.TradeInPayout, Permissions.TradeInView,
            Permissions.TradeInSettings,
            Permissions.RepairView, Permissions.RepairIntake, Permissions.RepairWork, Permissions.RepairAssign, Permissions.RepairCancel,
            Permissions.RepairSettings, Permissions.RepairCredentialView, Permissions.NotifSettings, Permissions.NotifReply,
            Permissions.RepairInvoiceCorrect, Permissions.CommView, Permissions.CommOwn, Permissions.CommApprove,
        ]),
        new("senior_cashier", "Senior cashier", "أمين صندوق أول",
            [Permissions.PosSell, Permissions.PosCreditSale, Permissions.PosDrawerNoSale, Permissions.PosReceiptReprint,
             Permissions.CatalogView, Permissions.InvView, Permissions.CustomersView, Permissions.CustomersManage, Permissions.PosShiftOpen,
             Permissions.PosCashPayOut, Permissions.DeviceView, Permissions.TradeInCreate, Permissions.TradeInView,
             Permissions.RepairView, Permissions.RepairIntake, Permissions.NotifReply], 10m, 10.000m),
        new("cashier", "Cashier", "أمين صندوق",
            [Permissions.PosSell, Permissions.PosReceiptReprint, Permissions.CatalogView, Permissions.CustomersView, Permissions.CustomersManage,
             Permissions.PosShiftOpen, Permissions.TradeInCreate, Permissions.TradeInView, Permissions.RepairView, Permissions.RepairIntake,
             Permissions.NotifReply], 5m, 2.000m),
        new("technician", "Technician", "فني",
            [Permissions.RepairCredentialView, Permissions.DeviceView, Permissions.DeviceInspect, Permissions.DeviceRefurbish, Permissions.CatalogView, Permissions.InvView,
             Permissions.RepairView, Permissions.RepairWork, Permissions.CommOwn]),
        new("inventory_controller", "Inventory controller", "مراقب المخزون",
            [Permissions.DeviceImeiManualEntry, Permissions.CatalogView, Permissions.CatalogManage, Permissions.InvView, Permissions.InvAdjust, Permissions.InvCount,
             Permissions.DeviceView, Permissions.DeviceInspect, Permissions.DeviceCount, Permissions.DeviceImeiCorrect]),
        new("purchasing_officer", "Purchasing officer", "مسؤول المشتريات", [Permissions.DeviceImeiManualEntry]),
        new("accountant", "Accountant", "المحاسب",
        [
            Permissions.SettingsView, Permissions.AuditView, Permissions.AuditVerify, Permissions.TaxManage, Permissions.TaxReview,
            Permissions.AcctCoaManage, Permissions.AcctJournalManual, Permissions.AcctPeriodClose, Permissions.CreditWriteOff,
            Permissions.CatalogView, Permissions.InvView, Permissions.CustomersView, Permissions.PosReportsView, Permissions.DeviceView,
            Permissions.TradeInView, Permissions.RepairView, Permissions.CommView, Permissions.CommPay,
        ]),
    ];
}
