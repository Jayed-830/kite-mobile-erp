using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kite.Host.Infrastructure;
using Xunit;
using static Kite.IntegrationTests.PosKit;

namespace Kite.IntegrationTests;

/// <summary>Phase 10 helpers: credit accounts, sales on account, a second branch with its till, and a branch server's credit traffic.</summary>
public sealed class CreditKit(KiteServerFixture fx, PosKit kit)
{
    public CentralKit Central => new(fx);

    public static async Task EnableAsync(HttpClient owner) =>
        await Ok(await owner.PutAsJsonAsync("/api/v1/settings/modules", new { changes = new Dictionary<string, bool> { ["credit"] = true, ["purch"] = true } }));

    public static object OnAccount(decimal amount) => new { method = "credit", amount };

    public static async Task<Guid> AccountAsync(HttpClient owner, Guid customer, decimal limit, int terms = 30, params (Guid Branch, decimal Amount)[] allowances)
    {
        await Ok(await owner.PostAsJsonAsync("/api/v1/credit/accounts", new
        {
            customerId = customer, creditLimit = limit, termsDays = terms, allowances = allowances.Select(a => new { branchId = a.Branch, amount = a.Amount }).ToArray(),
        }));
        return customer;
    }

    /// <summary>Sells <paramref name="amount"/> of a 1.000 service (prices include tax), all of it on account unless other payments are given.</summary>
    public static async Task<HttpResponseMessage> SellOnAccountAsync(HttpClient pos, Item unit, decimal amount, Guid customer, Guid? approval = null,
        object? plan = null, Guid? id = null, decimal onAccount = -1m, object[]? extraPayments = null) =>
        await pos.PostAsJsonAsync($"/api/v1/sales/{id ?? Guid.CreateVersion7()}/complete", new
        {
            lines = new[] { Line(unit, amount) },
            payments = new[] { OnAccount(onAccount < 0 ? amount : onAccount) }.Concat(extraPayments ?? []).ToArray(), customerId = customer, creditApprovalId = approval,
            creditPlan = plan,
        });

    /// <summary>A service priced 1.000, so a quantity is an amount.</summary>
    public async Task<Item> UnitServiceAsync(HttpClient owner) => await kit.ProductAsync(owner, "Credit service " + Suffix(), 1m, type: "service");

    public static async Task<JsonElement> DetailAsync(HttpClient owner, Guid customer) =>
        await owner.GetFromJsonAsync<JsonElement>($"/api/v1/credit/accounts/{customer}");

    public static decimal Fig(JsonElement detail, string name) => detail.GetProperty("figures").GetProperty(name).GetDecimal();

    public static async Task<Guid> ApprovalAsync(HttpClient pos, string manager, string pin, string reason = "good customer") =>
        (await Ok(await pos.PostAsJsonAsync("/api/v1/approvals/pin", new { action = "CREDIT.OVERRIDE", approverUsername = manager, pin, reason }))).GetProperty("id").GetGuid();

    /// <summary>A second branch on this server with an approved till, the owner signed in on it with a shift open.</summary>
    public async Task<(Guid Branch, HttpClient Pos)> SecondBranchAsync(HttpClient owner, decimal opening = 50m)
    {
        var code = "B" + Suffix()[..5];
        var branch = (await Ok(await owner.PostAsJsonAsync("/api/v1/branches", new { code, nameEn = "Branch " + code, nameAr = "فرع " + code }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        var tcode = "T" + Suffix();
        var key = NewDeviceKey();
        var t = await Ok(await owner.PostAsJsonAsync("/api/v1/terminals", new { branchId = branch, code = tcode, name = "Till " + tcode, kind = "browser", deviceKey = key }), HttpStatusCode.Created);
        var tid = t.GetProperty("id").GetGuid();
        await Ok(await owner.PostAsJsonAsync($"/api/v1/terminals/{tid}/approve", new { emergencyEnabled = false }));
        var pos = await kit.OwnerPosAsync(new Terminal(tid, tcode, key));
        await OpenShiftAsync(pos, opening);
        return (branch, pos);
    }

    // ---------------------------------------------------------------- a branch server's traffic (the test plays the branch server)
    public async Task<JsonElement> HeartbeatAsync(CentralKit.Branch b, long creditSeq = 0, long supplierSeq = 0)
    {
        var schema = await fx.QueryDbAsync<int>("SELECT max(version) FROM meta.schema_info");
        var (status, body) = await Central.SyncAsync(b.Cert, "/sync/v1/heartbeat",
            new Kite.Host.Features.HeartbeatRequest("0.10.0", schema, DateTimeOffset.UtcNow, 0, 0, null, null, null, 0, 0, 0, 0, 0, creditSeq, supplierSeq));
        Assert.True(status == 200, $"heartbeat: {status} {body}");
        return body;
    }

    /// <summary>The customer's account in a heartbeat answer ("credit") or a push answer ("snapshots").</summary>
    public static JsonElement? Snapshot(JsonElement answer, Guid customer) =>
        answer.GetProperty(answer.TryGetProperty("credit", out _) ? "credit" : "snapshots").EnumerateArray().Where(s => s.GetProperty("customerId").GetGuid() == customer).Select(s => (JsonElement?)s).LastOrDefault();

    public async Task<JsonElement> AuthoriseAsync(CentralKit.Branch b, Guid customer, Guid sale, decimal amount, bool approved = false, List<CreditUnsynced>? unsynced = null)
    {
        var (status, body) = await Central.SyncAsync(b.Cert, "/sync/v1/credit/authorise", new CreditAuthoriseRequest(customer, b.BranchId, sale, amount, approved, unsynced));
        Assert.True(status == 200, $"authorise: {status} {body}");
        return body;
    }

    /// <summary>A charge as a branch server writes it on a credit sale, with one instalment and its decision.</summary>
    public static CreditEntryPush Charge(CentralKit.Branch b, Guid customer, decimal amount, string mode, decimal available, decimal limit, decimal exposure,
        decimal? allowance, DateOnly date, decimal overBy = 0m, Guid? approvedBy = null)
    {
        var id = Guid.CreateVersion7();
        var sale = Guid.CreateVersion7();
        return new CreditEntryPush(id, b.BranchId, customer, "charge", amount, $"INV-{b.Code}-{Suffix()}", date, "sale", sale, "Sale offline", DateTimeOffset.UtcNow,
            Guid.CreateVersion7(), [new CreditItemPush(1, date.AddDays(30), amount, Guid.CreateVersion7())],
            new CreditDecisionPush(Guid.CreateVersion7(), mode, amount, available, limit, exposure, allowance, overBy, 0, null, approvedBy, DateTimeOffset.UtcNow, Guid.CreateVersion7()));
    }

    public static CreditEntryPush Payment(CentralKit.Branch b, Guid customer, decimal amount, DateOnly date) =>
        new(Guid.CreateVersion7(), b.BranchId, customer, "payment", -amount, $"RC-{b.Code}-{Suffix()}", date, "customer_receipt", Guid.CreateVersion7(), "Payment (cash)",
            DateTimeOffset.UtcNow, Guid.CreateVersion7(), [], null);

    public async Task<JsonElement> PushAsync(CentralKit.Branch b, params CreditEntryPush[] entries)
    {
        var (status, body) = await Central.SyncAsync(b.Cert, "/sync/v1/credit/push", new CreditPushRequest(entries.ToList()));
        Assert.True(status == 200, $"push: {status} {body}");
        return body;
    }
}
