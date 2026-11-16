using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Kite.IntegrationTests.PosKit;

namespace Kite.IntegrationTests;

/// <summary>Phase 8 test data and steps through the API: repair jobs from intake to collection, estimates, parts, deposits.</summary>
public sealed class RepairKit(KiteServerFixture fx)
{
    /// <summary>A 1x1 PNG, used for signatures and photos.</summary>
    public const string Png = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    public static readonly string[] Statuses =
    [
        "received", "inspection_pending", "under_diagnosis", "waiting_approval", "waiting_parts", "in_progress", "testing",
        "completed", "ready", "delivered", "cancelled", "unrepairable",
    ];

    public static readonly Dictionary<string, string[]> Allowed = new()
    {
        ["received"] = ["inspection_pending", "cancelled"],
        ["inspection_pending"] = ["under_diagnosis", "cancelled"],
        ["under_diagnosis"] = ["waiting_approval", "in_progress", "unrepairable"],
        ["waiting_approval"] = ["waiting_parts", "in_progress", "cancelled"],
        ["waiting_parts"] = ["in_progress", "cancelled"],
        ["in_progress"] = ["testing", "waiting_approval", "waiting_parts", "unrepairable"],
        ["testing"] = ["completed", "in_progress"],
        ["completed"] = ["ready"],
        ["ready"] = ["delivered"],
        ["delivered"] = [],
        ["cancelled"] = ["ready"],
        ["unrepairable"] = ["ready"],
    };

    /// <summary>The path from Received to each status along allowed transitions (Delivered goes through collection).</summary>
    public static readonly Dictionary<string, string[]> PathTo = new()
    {
        ["received"] = [],
        ["inspection_pending"] = ["inspection_pending"],
        ["under_diagnosis"] = ["inspection_pending", "under_diagnosis"],
        ["waiting_approval"] = ["inspection_pending", "under_diagnosis", "waiting_approval"],
        ["waiting_parts"] = ["inspection_pending", "under_diagnosis", "waiting_approval", "waiting_parts"],
        ["in_progress"] = ["inspection_pending", "under_diagnosis", "in_progress"],
        ["testing"] = ["inspection_pending", "under_diagnosis", "in_progress", "testing"],
        ["completed"] = ["inspection_pending", "under_diagnosis", "in_progress", "testing", "completed"],
        ["ready"] = ["inspection_pending", "under_diagnosis", "in_progress", "testing", "completed", "ready"],
        ["cancelled"] = ["cancelled"],
        ["unrepairable"] = ["inspection_pending", "under_diagnosis", "unrepairable"],
    };

    public static async Task EnableAsync(HttpClient owner, bool whatsapp = true) =>
        await Ok(await owner.PutAsJsonAsync("/api/v1/settings/modules",
            new { changes = new Dictionary<string, bool> { ["device"] = true, ["repair"] = true, ["notif"] = whatsapp } }));

    public static async Task<JsonElement> SetupAsync(HttpClient c) => await Ok(await c.GetAsync("/api/v1/repair-setup"));

    public static async Task<Guid> CategoryAsync(HttpClient c, string code = "SCREEN") =>
        (await SetupAsync(c)).GetProperty("categories").EnumerateArray().Single(x => x.GetProperty("code").GetString() == code).GetProperty("id").GetGuid();

    public static async Task<Guid> ServiceAsync(HttpClient c, string sku = "REPAIR-LABOUR") =>
        (await SetupAsync(c)).GetProperty("services").EnumerateArray().Single(x => x.GetProperty("sku").GetString() == sku).GetProperty("id").GetGuid();

    public static object Device(string? imei = null) => new { imei1 = imei ?? DeviceKit.Imei(), brand = "Apple", model = "iPhone 13" };

    public static Task<HttpResponseMessage> IntakeRawAsync(HttpClient c, Guid customer, Guid category, object? device = null, decimal? preAuthorised = null,
        bool whatsapp = false, string passcodeMode = "none", string? passcode = null, Guid? branchId = null, Guid? technicianId = null) =>
        c.PostAsJsonAsync("/api/v1/repairs", new
        {
            customerId = customer, categoryId = category, device = device ?? Device(), problem = "Cracked screen after a drop", faultCodes = new[] { "SCR_CRACKED" },
            condition = new Dictionary<string, string> { ["screen"] = "fault", ["body"] = "ok", ["liquid"] = "ok" }, accessories = new[] { "sim_tray", "case" },
            passcodeMode, passcode, preAuthorised, termsName = "Salim Al Hinai", termsSignature = Png, notifyWhatsapp = whatsapp, lang = "en",
            photos = new[] { new { stage = "intake", dataUrl = Png, caption = "front" } }, branchId, technicianId,
        });

    public static async Task<JsonElement> IntakeAsync(HttpClient c, Guid customer, Guid category, object? device = null, decimal? preAuthorised = null,
        bool whatsapp = false, string passcodeMode = "none", string? passcode = null, Guid? branchId = null, Guid? technicianId = null) =>
        (await Ok(await IntakeRawAsync(c, customer, category, device, preAuthorised, whatsapp, passcodeMode, passcode, branchId, technicianId), HttpStatusCode.Created))
            .GetProperty("job");

    public static async Task<JsonElement> DetailAsync(HttpClient c, Guid job) => await Ok(await c.GetAsync($"/api/v1/repairs/{job}"));
    public static Guid Id(JsonElement job) => job.GetProperty("id").GetGuid();

    public static Task<HttpResponseMessage> MoveRawAsync(HttpClient c, Guid job, string to, string? note = null) =>
        c.PostAsJsonAsync($"/api/v1/repairs/{job}/status", new { to, note });

    public static async Task<JsonElement> MoveAsync(HttpClient c, Guid job, params string[] path)
    {
        JsonElement d = default;
        foreach (var to in path) d = await Ok(await MoveRawAsync(c, job, to, $"to {to}"));
        return d;
    }

    public static object Labour(Guid service, decimal price) => new { kind = "labour", variantId = service, qty = 1m, unitPrice = price };
    public static object Part(Guid variant, decimal qty, decimal? price = null) => new { kind = "part", variantId = variant, qty, unitPrice = price };

    public static async Task<JsonElement> EstimateAsync(HttpClient c, Guid job, params object[] lines) =>
        await Ok(await c.PostAsJsonAsync($"/api/v1/repairs/{job}/estimates", new { lines }));

    public static Guid CurrentEstimate(JsonElement detail) => detail.GetProperty("estimates")[0].GetProperty("id").GetGuid();

    public static async Task<JsonElement> SendAsync(HttpClient c, Guid job, Guid estimate) =>
        await Ok(await c.PostAsync($"/api/v1/repairs/{job}/estimates/{estimate}/send", null));

    public static Task<HttpResponseMessage> ApproveRawAsync(HttpClient c, Guid job, Guid estimate, string method, string? then = null) =>
        c.PostAsJsonAsync($"/api/v1/repairs/{job}/estimates/{estimate}/approve", new
        {
            method, approverName = method is "signature" or "phone" ? "Salim Al Hinai" : null, phone = method == "phone" ? "+96892345678" : null,
            signature = method == "signature" ? Png : null, then,
        });

    public static async Task<JsonElement> FitAsync(HttpClient c, Guid job, Item part, decimal qty) =>
        await Ok(await c.PostAsJsonAsync($"/api/v1/repairs/{job}/parts", new { variantId = part.VariantId, qty }));

    public static Guid LastFit(JsonElement detail) =>
        detail.GetProperty("work").EnumerateArray().Last(w => w.GetProperty("kind").GetString() == "part" && w.GetProperty("qty").GetDecimal() > 0).GetProperty("id").GetGuid();

    public static async Task<JsonElement> DepositAsync(HttpClient pos, Guid job, decimal amount, string method = "cash") =>
        await Ok(await pos.PostAsJsonAsync($"/api/v1/repairs/{job}/deposits", method == "card"
            ? new { amount, method, cardTerminal = "BANK-1", approvalCode = "A" + Suffix() } as object
            : new { amount, method }));

    public static async Task<JsonElement> DeliveryAsync(HttpClient c, Guid job, decimal? fee = null) =>
        await Ok(await c.GetAsync($"/api/v1/repairs/{job}/delivery{(fee is { } f ? $"?diagnosisFee={f.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : "")}"));

    public static Task<HttpResponseMessage> DeliverRawAsync(HttpClient pos, Guid job, string qr, object[]? payments = null, string check = "qr", decimal? fee = null,
        string? excessTo = null) =>
        pos.PostAsJsonAsync($"/api/v1/repairs/{job}/deliver", new
        {
            check, qrCode = check == "qr" ? qr : null, collectedBy = "Salim Al Hinai", signature = Png, payments = payments ?? [], diagnosisFee = fee, excessTo,
        });

    public static async Task<JsonElement> DeliverAsync(HttpClient pos, Guid job, object[]? payments = null, string check = "qr", decimal? fee = null, string? excessTo = null)
    {
        var d = await DeliveryAsync(pos, job);
        return await Ok(await DeliverRawAsync(pos, job, d.GetProperty("qr").GetString()!, payments, check, fee, excessTo));
    }

    public async Task<T> DbAsync<T>(string sql) => await fx.QueryDbAsync<T>(sql);
}
