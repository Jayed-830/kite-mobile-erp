using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace Kite.IntegrationTests;

/// <summary>Builds Phase 4 test data through the API: licence, products with opening stock, terminals, users with PINs, POS sessions.</summary>
public sealed class PosKit(KiteServerFixture fx)
{
    public sealed record Item(Guid ProductId, Guid VariantId, string Sku, string Barcode, string Type);
    public sealed record Terminal(Guid Id, string Code, string DeviceKey);

    public Guid Branch => fx.SetupResult.GetProperty("branchId").GetGuid();

    public static string Suffix() => Convert.ToHexString(RandomNumberGenerator.GetBytes(3));
    public static string NewDeviceKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    public static async Task<JsonElement> Ok(HttpResponseMessage r, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == expected, $"{r.RequestMessage?.Method} {r.RequestMessage?.RequestUri?.PathAndQuery}: expected {(int)expected} but got {(int)r.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    public static async Task Problem(HttpResponseMessage r, HttpStatusCode status, string code)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == status, $"{r.RequestMessage?.RequestUri?.PathAndQuery}: expected {(int)status} {code} but got {(int)r.StatusCode}: {body}");
        Assert.Contains($"\"code\":\"{code}\"", body, StringComparison.Ordinal);
    }

    public async Task<HttpClient> OwnerAsync()
    {
        var c = fx.Client(await fx.OwnerTokenAsync());
        await Ok(await c.PostAsJsonAsync("/api/v1/licence/import", new { fileText = fx.MakeLicence(KiteServerFixture.FutureDate(365), branches: 20, terminals: 200) }));
        return c;
    }

    public async Task<Guid> TaxCodeAsync(HttpClient owner, string code = "VAT-STD")
    {
        var codes = await owner.GetFromJsonAsync<JsonElement>("/api/v1/tax/codes");
        return codes.EnumerateArray().Single(c => c.GetProperty("code").GetString() == code).GetProperty("id").GetGuid();
    }

    public async Task<Item> ProductAsync(HttpClient owner, string name, decimal price, decimal? minPrice = null, string type = "accessory", string tracking = "none",
        decimal openingQty = 0, decimal unitCost = 0, string taxCode = "VAT-STD")
    {
        var sku = $"P4-{Suffix()}";
        var barcode = $"629{RandomNumberGenerator.GetInt32(100000000, 999999999)}";
        var r = await owner.PostAsJsonAsync("/api/v1/catalog/products", new
        {
            nameEn = name, nameAr = "منتج " + name, productType = type, tracking, taxCodeId = await TaxCodeAsync(owner, taxCode), defaultWarrantyDays = 30,
            variants = new[] { new { sku, barcodes = new[] { barcode }, price, minPrice } },
        });
        var p = await Ok(r, HttpStatusCode.Created);
        var item = new Item(p.GetProperty("product").GetProperty("id").GetGuid(), p.GetProperty("variants")[0].GetProperty("variant").GetProperty("id").GetGuid(), sku, barcode, type);
        if (openingQty > 0) await OpeningAsync(owner, item, openingQty, unitCost);
        return item;
    }

    public async Task OpeningAsync(HttpClient owner, Item item, decimal qty, decimal unitCost) =>
        await Ok(await owner.PostAsJsonAsync("/api/v1/inventory/adjustments", new
        {
            branchId = Branch, kind = "opening", note = "test opening stock",
            lines = new[] { new { variantId = item.VariantId, qty, unitCost, reasonCode = "opening" } },
        }), HttpStatusCode.Created);

    public async Task<Terminal> TerminalAsync(HttpClient owner, string kind = "browser", bool emergency = false)
    {
        var code = "T" + Suffix();
        var key = NewDeviceKey();
        var r = await Ok(await owner.PostAsJsonAsync("/api/v1/terminals", new { branchId = Branch, code, name = "Till " + code, kind, deviceKey = key }), HttpStatusCode.Created);
        var id = r.GetProperty("id").GetGuid();
        await Ok(await owner.PostAsJsonAsync($"/api/v1/terminals/{id}/approve", new { emergencyEnabled = emergency }));
        return new Terminal(id, code, key);
    }

    public async Task<Guid> RoleIdAsync(HttpClient owner, string code)
    {
        var roles = await owner.GetFromJsonAsync<JsonElement>("/api/v1/roles");
        return roles.EnumerateArray().Single(r => r.GetProperty("code").GetString() == code).GetProperty("id").GetGuid();
    }

    /// <summary>A user with a real password (and a PIN when given), for one branch.</summary>
    public async Task<(Guid Id, string Username, string Password)> UserAsync(HttpClient owner, string prefix, string role, string? pin = null)
    {
        var username = $"{prefix}_{Suffix().ToLowerInvariant()}";
        var roleId = await RoleIdAsync(owner, role);
        var created = await Ok(await owner.PostAsJsonAsync("/api/v1/users", new
        {
            username, displayNameEn = username, displayNameAr = (string?)null, email = (string?)null, phone = (string?)null,
            temporaryPassword = "Temp-Pass-2026", preferredLanguage = "en", defaultBranchId = Branch, roles = new[] { new { roleId, branchId = (Guid?)Branch } },
        }), HttpStatusCode.Created);
        using var uc = fx.Client(await fx.TokenAsync(username, "Temp-Pass-2026"));
        await Ok(await uc.PostAsJsonAsync("/api/v1/auth/password", new { currentPassword = "Temp-Pass-2026", newPassword = "Real-Pass-2026" }), HttpStatusCode.NoContent);
        if (pin is not null)
        {
            using var uc2 = fx.Client(await fx.TokenAsync(username, "Real-Pass-2026"));
            await Ok(await uc2.PostAsJsonAsync("/api/v1/auth/pin", new { pin, currentPassword = "Real-Pass-2026" }), HttpStatusCode.NoContent);
        }
        return (created.GetProperty("id").GetGuid(), username, "Real-Pass-2026");
    }

    public async Task<HttpClient> PosAsync(string username, string password, Terminal terminal)
    {
        using var anon = fx.Client();
        var r = await Ok(await anon.PostAsJsonAsync("/api/v1/auth/login", new { username, password, terminalId = terminal.Id, deviceKey = terminal.DeviceKey }));
        return fx.Client(r.GetProperty("accessToken").GetString());
    }

    public async Task<HttpClient> OwnerPosAsync(Terminal terminal) => await PosAsync(KiteServerFixture.OwnerUsername, KiteServerFixture.OwnerPassword, terminal);

    public static async Task<JsonElement> OpenShiftAsync(HttpClient pos, decimal opening) =>
        await Ok(await pos.PostAsJsonAsync("/api/v1/pos/shifts", new { openingFloat = opening }), HttpStatusCode.Created);

    public static object Cash(decimal amount, decimal? tendered = null) => new { method = "cash", amount, tendered };
    public static object Card(decimal amount, string terminal = "BANK-1") => new { method = "card", amount, cardTerminal = terminal, approvalCode = "A" + Suffix() };
    public static object Line(Item i, decimal qty, decimal? discount = null, decimal? unitPrice = null, Guid? approvalId = null) =>
        new { variantId = i.VariantId, qty, discount, unitPrice, approvalId };

    public static async Task<JsonElement> SellAsync(HttpClient pos, object[] lines, object[] payments, Guid? id = null, string? docType = null, Guid? customerId = null,
        HttpStatusCode expected = HttpStatusCode.OK) =>
        await Ok(await pos.PostAsJsonAsync($"/api/v1/sales/{id ?? Guid.CreateVersion7()}/complete", new { lines, payments, docType, customerId }), expected);

    public async Task<decimal> StockAsync(HttpClient owner, Item item)
    {
        var rows = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/inventory/balances?branchId={Branch}&q={item.Sku}");
        return rows.EnumerateArray().Where(r => r.GetProperty("variantId").GetGuid() == item.VariantId).Select(r => r.GetProperty("qty").GetDecimal()).SingleOrDefault();
    }

    public async Task SetPolicyAsync(HttpClient owner, decimal cashRounding = 0m, string negativeStock = "block", decimal cashRefundCap = 100m, decimal variance = 1m,
        int returnDays = 14, string rounding = "line")
    {
        var current = await owner.GetFromJsonAsync<JsonElement>("/api/v1/pos/policy");
        await Ok(await owner.PutAsJsonAsync("/api/v1/pos/policy", new
        {
            branchId = (Guid?)null, negativeStock, returnWindowDays = returnDays, cashRefundCap, varianceThreshold = variance, blindClose = true,
            roundingLevel = rounding, cashRounding, denominations = (decimal[]?)null, receiptFooterEn = "Thank you", receiptFooterAr = "شكراً لكم",
            version = current.GetProperty("version").GetInt32() == 0 ? (int?)null : current.GetProperty("version").GetInt32(),
        }));
    }
}
