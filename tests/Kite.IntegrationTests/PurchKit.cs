using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Kite.IntegrationTests.PosKit;

namespace Kite.IntegrationTests;

/// <summary>Helpers for the Phase 11 tests: suppliers, orders, receipts, invoices and the general ledger.</summary>
public sealed class PurchKit(KiteServerFixture fx, PosKit kit)
{
    public DeviceKit Devices => new(fx, kit);

    public static async Task EnableAsync(HttpClient owner) =>
        await Ok(await owner.PutAsJsonAsync("/api/v1/settings/modules", new
        {
            changes = new Dictionary<string, bool> { ["purch"] = true, ["exp"] = true, ["acct_ui"] = true, ["device"] = true },
        }));

    public static async Task<Guid> SupplierAsync(HttpClient owner, string name = "Gulf Mobiles", int terms = 30) =>
        (await Ok(await owner.PostAsJsonAsync("/api/v1/suppliers", new { nameEn = $"{name} {Suffix()}", nameAr = "مورد " + name, termsDays = terms }))).GetProperty("id").GetGuid();

    public async Task<Item> AccessoryAsync(HttpClient owner, string name = "Case") => await kit.ProductAsync(owner, $"{name} {Suffix()}", 9m);
    public async Task<Item> PhoneAsync(HttpClient owner, string name = "Phone") => await Devices.PhoneAsync(owner, $"{name} {Suffix()}", 300m);

    public static object OrderLine(Item i, decimal qty, decimal price, Guid? deliver = null) => new { variantId = i.VariantId, qty, unitPrice = price, deliverBranchId = deliver };

    public static async Task<(Guid Id, JsonElement Detail)> OrderAsync(HttpClient c, Guid branch, Guid supplier, params object[] lines)
    {
        var id = Guid.CreateVersion7();
        await Ok(await c.PostAsJsonAsync("/api/v1/purchasing/orders", new { id, branchId = branch, supplierId = supplier, lines }));
        return (id, await Ok(await c.GetAsync($"/api/v1/purchasing/orders/{id}")));
    }

    public static Guid PoLine(JsonElement order, int index) => order.GetProperty("lines")[index].GetProperty("id").GetGuid();

    public static object[] Units(int n, string condition = "brand_new") =>
        Enumerable.Range(0, n).Select(_ => (object)new { imei1 = DeviceKit.Imei(), condition, sellingPrice = 300m }).ToArray();

    public static async Task<HttpResponseMessage> ReceiveRawAsync(HttpClient c, Guid branch, Guid supplier, Guid? po, object[] lines, object[]? charges = null, Guid? id = null,
        DateOnly? date = null) =>
        await c.PostAsJsonAsync("/api/v1/purchasing/receipts", new { id = id ?? Guid.CreateVersion7(), branchId = branch, supplierId = supplier, poId = po, lines, charges, receiptDate = date });

    public static async Task<JsonElement> ReceiveAsync(HttpClient c, Guid branch, Guid supplier, Guid? po, object[] lines, object[]? charges = null, DateOnly? date = null) =>
        await Ok(await ReceiveRawAsync(c, branch, supplier, po, lines, charges, date: date));

    public static Guid ReceiptLine(JsonElement receipt, int index) => receipt.GetProperty("lines")[index].GetProperty("id").GetGuid();

    public static object Goods(Guid receiptLine, decimal qty, decimal price) => new { kind = "receipt", receiptLineId = receiptLine, qty, unitPrice = price };
    public static object Charge(Guid chargeId, decimal amount) => new { kind = "charge", receiptChargeId = chargeId, qty = 1m, unitPrice = amount };

    public static async Task<HttpResponseMessage> InvoiceRawAsync(HttpClient c, Guid branch, Guid supplier, string number, object[] lines, Guid? id = null, DateOnly? date = null) =>
        await c.PostAsJsonAsync("/api/v1/purchasing/invoices", new
        {
            id = id ?? Guid.CreateVersion7(), branchId = branch, supplierId = supplier, supplierInvoiceNo = number, invoiceDate = date ?? Today, lines,
        });

    public static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(4));

    public static async Task<JsonElement> PostInvoiceAsync(HttpClient c, Guid id, string? reason = null) =>
        await Ok(await c.PostAsJsonAsync($"/api/v1/purchasing/invoices/{id}/post", new { overrideReason = reason }));

    /// <summary>Debits less credits on accounts, at one branch or all of them, optionally only for one party.</summary>
    public async Task<decimal> GlAsync(string codes, Guid? branch = null, Guid? party = null) => await fx.QueryDbAsync<decimal>($"""
        SELECT coalesce(sum(l.debit - l.credit), 0) FROM acct.journal_line l JOIN acct.account a ON a.id = l.account_id
         WHERE a.code IN ({string.Join(",", codes.Split(',').Select(c => $"'{c.Trim()}'"))})
           {(branch is { } b ? $"AND l.branch_id = '{b}'" : "")} {(party is { } p ? $"AND l.party_id = '{p}'" : "")}
        """);

    public async Task<decimal> SupplierBalanceAsync(Guid supplier) =>
        await fx.QueryDbAsync<decimal>($"SELECT coalesce(sum(amount), 0) FROM purch.supplier_ledger WHERE supplier_id = '{supplier}'");

    public async Task<decimal> GrniOpenAsync(Guid? branch = null) =>
        await fx.QueryDbAsync<decimal>($"SELECT coalesce(sum(amount), 0) FROM purch.grni_entry {(branch is { } b ? $"WHERE branch_id = '{b}'" : "")}");

    /// <summary>Runs the daily ledger check and returns its rows that do not agree.</summary>
    public static async Task<List<string>> ReconcileAsync(HttpClient owner)
    {
        var r = await Ok(await owner.PostAsync("/api/v1/accounting/reconciliation/run", null));
        return r.GetProperty("rows").EnumerateArray().Where(x => x.GetProperty("difference").GetDecimal() != 0)
            .Select(x => $"{x.GetProperty("ledger").GetString()} {x.GetProperty("branchCode")}: subledger {x.GetProperty("subledger")} control {x.GetProperty("control")}")
            .ToList();
    }

    public static async Task<HttpResponseMessage> ExpenseRawAsync(HttpClient c, Guid branch, Guid account, decimal net, DateOnly? date = null, string paidBy = "cash", Guid? taxCode = null,
        Guid? id = null) =>
        await c.PostAsJsonAsync("/api/v1/expenses", new
        {
            id = id ?? Guid.CreateVersion7(), branchId = branch, accountId = account, description = "Test expense " + Suffix(), netAmount = net, taxCodeId = taxCode, paidBy,
            expenseDate = date, reference = paidBy == "cash" ? null : "TRF-" + Suffix(),
        });

    public static async Task<Guid> AccountIdAsync(HttpClient owner, string code) =>
        (await owner.GetFromJsonAsync<JsonElement>("/api/v1/accounting/chart")).EnumerateArray().Single(a => a.GetProperty("code").GetString() == code).GetProperty("id").GetGuid();

    public static async Task<JsonElement> PeriodsAsync(HttpClient owner, int year) => await Ok(await owner.GetAsync($"/api/v1/accounting/periods?fiscalYear={year}"));

    /// <summary>Sets a period's status for every branch (or one).</summary>
    public static async Task<HttpResponseMessage> PeriodStatusRawAsync(HttpClient owner, int year, int no, string status, Guid? branch = null)
    {
        var periods = await PeriodsAsync(owner, year);
        var id = periods.GetProperty("periods").EnumerateArray().Single(p => p.GetProperty("periodNo").GetInt32() == no).GetProperty("id").GetGuid();
        return await owner.PostAsJsonAsync($"/api/v1/accounting/periods/{id}/status", new { status, branchId = branch, reason = "test " + status });
    }

    public static async Task PeriodStatusAsync(HttpClient owner, int year, int no, string status, Guid? branch = null) =>
        await Ok(await PeriodStatusRawAsync(owner, year, no, status, branch));

    public static async Task<HttpResponseMessage> ProblemOf(HttpResponseMessage r, HttpStatusCode status, string code)
    {
        await Problem(r, status, code);
        return r;
    }
}
