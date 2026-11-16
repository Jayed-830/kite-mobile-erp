using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using static Kite.IntegrationTests.PosKit;

namespace Kite.IntegrationTests;

/// <summary>Phase 5 test data through the API: phones received by IMEI, inspections, refurbishment costs, account balances.</summary>
public sealed class DeviceKit(KiteServerFixture fx, PosKit kit)
{
    public sealed record Unit(Guid Id, string Imei);

    /// <summary>A random IMEI with a correct check digit (TAC 35 so it looks like a phone).</summary>
    public static string Imei()
    {
        var digits = "35" + string.Concat(Enumerable.Range(0, 12).Select(_ => RandomNumberGenerator.GetInt32(0, 10)));
        var sum = 0;
        for (var i = 0; i < 14; i++)
        {
            var d = digits[i] - '0';
            if (i % 2 == 1) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
        }
        return digits + ((10 - sum % 10) % 10);
    }

    public static Dictionary<string, string?> AllPass(int battery = 92) => new()
    {
        ["screen"] = "pass", ["touch"] = "pass", ["body"] = "pass", ["buttons"] = "pass", ["cameras"] = "pass", ["audio"] = "pass", ["charging"] = "pass",
        ["network"] = "pass", ["biometrics"] = "pass", ["water"] = "pass", ["battery"] = battery.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["cloud_lock"] = "pass", ["notes"] = null,
    };

    public static async Task EnableRefurbAsync(HttpClient owner) =>
        await Ok(await owner.PutAsJsonAsync("/api/v1/settings/modules", new { changes = new Dictionary<string, bool> { ["device"] = true, ["refurb"] = true } }));

    public async Task<PosKit.Item> PhoneAsync(HttpClient owner, string name, decimal price, decimal? minPrice = null) =>
        await kit.ProductAsync(owner, name, price, minPrice, type: "device", tracking: "imei");

    /// <summary>Receives units on one opening-stock document; returns them in order.</summary>
    public async Task<Unit[]> ReceiveAsync(HttpClient owner, PosKit.Item phone, params (decimal Cost, string Condition, string Status, decimal? Price, Guid? Grade)[] units)
    {
        var imeis = units.Select(_ => Imei()).ToArray();
        await Ok(await owner.PostAsJsonAsync("/api/v1/inventory/adjustments", new
        {
            branchId = kit.Branch, kind = "opening", note = "phones",
            lines = new[]
            {
                new
                {
                    variantId = phone.VariantId, qty = (decimal)units.Length, reasonCode = "opening",
                    units = units.Select((u, i) => new { imei1 = imeis[i], unitCost = u.Cost, condition = u.Condition, status = u.Status, sellingPrice = u.Price, gradeId = u.Grade }).ToArray(),
                },
            },
        }), HttpStatusCode.Created);
        var result = new List<Unit>();
        foreach (var imei in imeis) result.Add(new Unit((await Ok(await owner.GetAsync($"/api/v1/devices/lookup/{imei}"))).GetProperty("id").GetGuid(), imei));
        return [.. result];
    }

    public static async Task<JsonElement> DetailAsync(HttpClient c, Guid unitId) => await Ok(await c.GetAsync($"/api/v1/devices/{unitId}"));

    public static async Task<decimal> CostAsync(HttpClient c, Guid unitId) => (await DetailAsync(c, unitId)).GetProperty("cost").GetProperty("total").GetDecimal();

    public static async Task<string> StatusAsync(HttpClient c, Guid unitId) => (await DetailAsync(c, unitId)).GetProperty("unit").GetProperty("status").GetString()!;

    public static async Task<Guid> GradeAsync(HttpClient c, string code) =>
        (await c.GetFromJsonAsync<JsonElement>("/api/v1/devices/grades")).EnumerateArray()
            .First(g => g.GetProperty("code").GetString() == code && g.GetProperty("categoryId").ValueKind == JsonValueKind.Null).GetProperty("id").GetGuid();

    public static async Task<HttpResponseMessage> InspectAsync(HttpClient c, Guid unitId, Dictionary<string, string?> answers, string outcome, string condition,
        Guid? grade = null, string? note = null, decimal? price = null, Guid? technician = null) =>
        await c.PostAsJsonAsync($"/api/v1/devices/{unitId}/inspections", new { answers, outcome, condition, gradeId = grade, note, sellingPrice = price, technicianId = technician });

    public static async Task<Guid> OpenOrderAsync(HttpClient c, Guid unitId) =>
        (await c.GetFromJsonAsync<JsonElement>("/api/v1/refurbishments?status=open")).EnumerateArray()
            .Single(o => o.GetProperty("unitId").GetGuid() == unitId).GetProperty("id").GetGuid();

    public static async Task<JsonElement> AddCostAsync(HttpClient c, Guid orderId, object body) => await Ok(await c.PostAsJsonAsync($"/api/v1/refurbishments/{orderId}/costs", body));

    public static async Task<JsonElement> CompleteAsync(HttpClient c, Guid orderId, string condition, Guid? grade, decimal? price = null) =>
        await Ok(await c.PostAsJsonAsync($"/api/v1/refurbishments/{orderId}/complete", new { condition, gradeId = grade, sellingPrice = price, warrantyDays = 90 }));

    /// <summary>Debits minus credits on accounts (comma-separated codes), all branches.</summary>
    public async Task<decimal> GlAsync(string codes) => await fx.QueryDbAsync<decimal>($"""
        SELECT coalesce(sum(l.debit - l.credit), 0) FROM acct.journal_line l JOIN acct.account a ON a.id = l.account_id
         WHERE a.code IN ({string.Join(",", codes.Split(',').Select(c => $"'{c.Trim()}'"))})
        """);

    public async Task<Dictionary<string, decimal>> GlSnapshotAsync(params string[] codes)
    {
        var d = new Dictionary<string, decimal>();
        foreach (var c in codes) d[c] = await GlAsync(c);
        return d;
    }

    public static async Task AssertConsistentAsync(HttpClient owner)
    {
        var check = await owner.GetFromJsonAsync<JsonElement>("/api/v1/inventory/check");
        Xunit.Assert.True(check.GetProperty("ok").GetBoolean(), "consistency check failed: " + check.GetRawText());
    }

    public static object UnitLine(PosKit.Item phone, Guid unitId, decimal? unitPrice = null, Guid? damagedApproval = null) =>
        new { variantId = phone.VariantId, qty = 1m, deviceUnitId = unitId, unitPrice, damagedApprovalId = damagedApproval };
}
