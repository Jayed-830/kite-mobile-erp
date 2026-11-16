using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using static Kite.IntegrationTests.DeviceKit;
using static Kite.IntegrationTests.PosKit;

namespace Kite.IntegrationTests;

/// <summary>Phase 7 test data through the API: the trade-in module, the margin scheme approval, customers, trade-ins taken in at a till.</summary>
public sealed class TradeInKit(KiteServerFixture fx)
{
    public static async Task EnableAsync(HttpClient owner) =>
        await Ok(await owner.PutAsJsonAsync("/api/v1/settings/modules",
            new { changes = new Dictionary<string, bool> { ["device"] = true, ["refurb"] = true, ["tradein"] = true } }));

    /// <summary>Records (or withdraws) the Tax Authority's approval of the profit margin scheme.</summary>
    public static async Task MarginAsync(HttpClient owner, bool approved)
    {
        var m = await owner.GetFromJsonAsync<JsonElement>("/api/v1/tax/margin-scheme");
        await Ok(await owner.PutAsJsonAsync("/api/v1/tax/margin-scheme", new
        {
            approved, reference = approved ? "TA/MS/2026/0142" : null, approvedOn = approved ? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30)) : (DateOnly?)null,
            note = "test", version = m.GetProperty("version").GetInt32(),
        }), HttpStatusCode.NoContent);
    }

    public static async Task<Guid> CustomerAsync(HttpClient c, string name, string? vat = null) =>
        (await Ok(await c.PostAsJsonAsync("/api/v1/customers", new
        {
            nameEn = name, nameAr = "عميل " + name, kind = vat is null ? "individual" : "company", vatNumber = vat,
            phone = "9" + RandomNumberGenerator.GetInt32(1000000, 9999999).ToString(System.Globalization.CultureInfo.InvariantCulture),
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

    public static async Task GuideAsync(HttpClient owner, Item phone, Guid grade, decimal value) =>
        await Ok(await owner.PutAsJsonAsync("/api/v1/tradein/guide", new { rows = new[] { new { variantId = phone.VariantId, gradeId = grade, baseValue = (decimal?)value } } }),
            HttpStatusCode.NoContent);

    public static async Task<HttpResponseMessage> CreateRawAsync(HttpClient pos, Guid customer, Item phone, string imei, string? civilId = "12345678", string? sellerVat = null) =>
        await pos.PostAsJsonAsync("/api/v1/tradeins", new { customerId = customer, device = new { variantId = phone.VariantId, imei1 = imei }, civilId, sellerVatNumber = sellerVat });

    /// <summary>Records the device and inspection and agrees the value; returns the trade-in as it stands (still open).</summary>
    public static async Task<JsonElement> OpenAsync(HttpClient pos, Guid customer, Item phone, Guid grade, decimal agreed, string? imei = null, string? civilId = "12345678",
        string? sellerVat = null, bool cloudLockRemoved = true, string[]? deductions = null)
    {
        var created = await Ok(await CreateRawAsync(pos, customer, phone, imei ?? Imei(), civilId, sellerVat), HttpStatusCode.Created);
        var ti = created.GetProperty("detail").GetProperty("tradeIn");
        return (await Ok(await pos.PutAsJsonAsync($"/api/v1/tradeins/{ti.GetProperty("id").GetGuid()}", new
        {
            version = ti.GetProperty("version").GetInt32(), gradeId = grade, condition = "used", inspection = AllPass(), batteryHealth = 92, cloudLockRemoved,
            deductions = deductions ?? [], agreedValue = agreed,
        }))).GetProperty("tradeIn");
    }

    public static Task<HttpResponseMessage> AcceptRawAsync(HttpClient pos, JsonElement open, object? settle = null, Guid? approvalId = null, object? consent = null) =>
        pos.PostAsJsonAsync($"/api/v1/tradeins/{open.GetProperty("id").GetGuid()}/accept", new
        {
            version = open.GetProperty("version").GetInt32(), agreedValue = open.GetProperty("agreedValue").GetDecimal(),
            consent = consent ?? new { method = "on_screen", name = "Salim Al Harthy" }, settle, approvalId,
        });

    /// <summary>Takes a device in and accepts it, settling it now when <paramref name="settle"/> says how; returns the accepted trade-in.</summary>
    public static async Task<JsonElement> TakeInAsync(HttpClient pos, Guid customer, Item phone, Guid grade, decimal agreed, object? settle = null, string? imei = null,
        string? sellerVat = null) =>
        (await Ok(await AcceptRawAsync(pos, await OpenAsync(pos, customer, phone, grade, agreed, imei, sellerVat: sellerVat), settle))).GetProperty("tradeIn");

    public static object TradeIn(Guid tradeInId, decimal amount) => new { method = "trade_in", amount, tradeInId };
    public static object StoreCredit(decimal amount) => new { method = "store_credit", amount };

    public static async Task<decimal> BalanceAsync(HttpClient c, Guid customer) =>
        (await Ok(await c.GetAsync($"/api/v1/customers/{customer}/store-credit"))).GetProperty("balance").GetDecimal();

    public static async Task<JsonElement> TradeInAsync(HttpClient c, Guid tradeInId) => (await Ok(await c.GetAsync($"/api/v1/tradeins/{tradeInId}"))).GetProperty("tradeIn");

    public async Task<JsonElement> FiguresAsync(HttpClient pos) => (await Ok(await pos.GetAsync("/api/v1/pos/shifts/current"))).GetProperty("figures");

    public async Task<T> DbAsync<T>(string sql) => await fx.QueryDbAsync<T>(sql);
}
