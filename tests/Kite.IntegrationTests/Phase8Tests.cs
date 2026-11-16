using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
using static Kite.IntegrationTests.PosKit;
using static Kite.IntegrationTests.RepairKit;

namespace Kite.IntegrationTests;

/// <summary>
/// Phase 8 against the real server and PostgreSQL: a repair from intake to collection with the journals it posts (parts into
/// work-in-progress and out as the cost of the repair, the deposit held and applied, the repair invoice); estimates approved by
/// signature, phone and within the pre-authorised amount, extra work needing approval again; a cancelled job with a diagnosis fee,
/// a part written off and the deposit's excess given back; a warranty claim at no charge; deposits as a VAT point; the passcode;
/// and every transition through the API.
/// </summary>
[Collection("server")]
public sealed class Phase8Tests(KiteServerFixture fx)
{
    private readonly PosKit kit = new(fx);
    private DeviceKit Dev => new(fx, kit);

    private static decimal D(JsonElement e, string name) => e.GetProperty(name).GetDecimal();

    private async Task<(HttpClient Owner, HttpClient Pos, Guid Customer, Guid Category, Guid Labour, Item Screen)> ArrangeAsync(decimal opening = 300m)
    {
        var owner = await kit.OwnerAsync();
        await EnableAsync(owner);
        await kit.SetPolicyAsync(owner, cashRounding: 0m);
        var screen = await kit.ProductAsync(owner, "iPhone 13 screen " + Suffix(), 35m, type: "spare_part", openingQty: 10, unitCost: 12m);
        var term = await kit.TerminalAsync(owner);
        var pos = await kit.OwnerPosAsync(term);
        await OpenShiftAsync(pos, opening);
        var customer = await TradeInKit.CustomerAsync(pos, "Salim " + Suffix());
        return (owner, pos, customer, await CategoryAsync(owner), await ServiceAsync(owner), screen);
    }

    [PgFact]
    public async Task A_repair_from_intake_to_collection_posts_parts_deposit_and_invoice_correctly()
    {
        var (owner, pos, customer, category, labour, screen) = await ArrangeAsync();
        using var _o = owner; using var _p = pos;
        var gl = await Dev.GlSnapshotAsync("1430", "1460", "5200", "4200", "4210", "2300", "2200", "1100");
        async Task<decimal> Moved(string code) => await Dev.GlAsync(code) - gl[code];

        // Intake: the job number, the history's first row, the signed terms and the photo with its hash.
        var job = await IntakeAsync(pos, customer, category, preAuthorised: 40m, passcodeMode: "provided", passcode: "482913");
        var id = Id(job);
        Assert.StartsWith("R-B01-", job.GetProperty("number").GetString());
        Assert.Equal("received", job.GetProperty("status").GetString());
        Assert.True(job.GetProperty("passcodeHeld").GetBoolean());
        var detail = await DetailAsync(pos, id);
        Assert.Equal(2, detail.GetProperty("photos").GetArrayLength());
        Assert.All(detail.GetProperty("photos").EnumerateArray(), p => Assert.Matches("^[0-9a-f]{64}$", p.GetProperty("sha256").GetString()));
        Assert.Single(detail.GetProperty("history").EnumerateArray());

        // The passcode: shown with the permission, each view logged; the job card never prints it.
        var pc = await Ok(await pos.PostAsync($"/api/v1/repairs/{id}/passcode", null));
        Assert.Equal("482913", pc.GetProperty("passcode").GetString());
        Assert.Equal(1L, await fx.QueryDbAsync<long>($"SELECT count(*) FROM repair.credential_view WHERE job_id = '{id}'"));
        var card = await pos.GetAsync($"/api/v1/repairs/{id}/job-card");
        Assert.Equal(HttpStatusCode.OK, card.StatusCode);
        Assert.Equal("application/pdf", card.Content.Headers.ContentType?.MediaType);

        // A deposit of 10.000 in cash: held in customer deposits, in the drawer.
        var dep = await DepositAsync(pos, id, 10m);
        Assert.StartsWith("DEP-B01-", dep.GetProperty("number").GetString());
        Assert.Equal(-10m, await Moved("2300"));
        Assert.Equal(10m, await Moved("1100"));

        // Diagnosis, an estimate within the 40.000 pre-authorised: the work goes ahead without asking the customer again.
        await MoveAsync(pos, id, "inspection_pending", "under_diagnosis");
        detail = await EstimateAsync(pos, id, Labour(labour, 15m), Part(screen.VariantId, 1m, 21m));
        Assert.Equal(36m, D(detail.GetProperty("estimates")[0], "total"));
        detail = await MoveAsync(pos, id, "in_progress");
        Assert.Equal("approved", detail.GetProperty("estimates")[0].GetProperty("status").GetString());
        Assert.Equal("pre_authorised", detail.GetProperty("estimates")[0].GetProperty("approvalMethod").GetString());

        // Two screens fitted (one broke on fitting and comes back unused): 24.000 in, 12.000 back; stock 8 then 9.
        detail = await FitAsync(pos, id, screen, 2m);
        Assert.Equal(24m, D(detail.GetProperty("money"), "wip"));
        Assert.Equal(24m, await Moved("1460"));
        Assert.Equal(-24m, await Moved("1430"));
        detail = await Ok(await pos.PostAsJsonAsync($"/api/v1/repairs/{id}/parts/{LastFit(detail)}/return", new { qty = 1m, note = "not needed" }));
        Assert.Equal(12m, D(detail.GetProperty("money"), "wip"));
        Assert.Equal(9m, await kit.StockAsync(owner, screen));
        await Ok(await pos.PostAsJsonAsync($"/api/v1/repairs/{id}/labour", new { minutes = 45 }));

        detail = await MoveAsync(pos, id, "testing", "completed", "ready");
        Assert.Equal("ready", detail.GetProperty("job").GetProperty("status").GetString());
        Assert.Equal(26m, D(detail.GetProperty("money"), "due"));

        // Collection: the QR must be this job's; the invoice applies the deposit; the rest is paid in cash.
        await Problem(await DeliverRawAsync(pos, id, "KITE-R|R-B01-2026-999999|000000000000", [Cash(26m)]), HttpStatusCode.Conflict, "REP-004");
        var done = await DeliverAsync(pos, id, [Cash(26m)]);
        Assert.Equal(36m, D(done, "gross"));
        Assert.Equal(10m, D(done, "applied"));
        Assert.StartsWith("INV-", done.GetProperty("saleNumber").GetString());

        // The books: parts out of WIP into cost of repair parts; labour and parts income; VAT; deposits back to zero.
        Assert.Equal(0m, await Moved("1460"));
        Assert.Equal(-12m, await Moved("1430"));
        Assert.Equal(12m, await Moved("5200"));
        Assert.Equal(-14.286m, await Moved("4200"));   // 15.000 incl. 5% VAT
        Assert.Equal(-20m, await Moved("4210"));       // 21.000 incl. 5% VAT
        Assert.Equal(-1.714m, await Moved("2200"));
        Assert.Equal(0m, await Moved("2300"));
        Assert.Equal(36m, await Moved("1100"));

        detail = await DetailAsync(pos, id);
        var j = detail.GetProperty("job");
        Assert.Equal("delivered", j.GetProperty("status").GetString());
        Assert.False(j.GetProperty("passcodeHeld").GetBoolean());
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow.AddHours(4)).AddDays(90).ToString("yyyy-MM-dd"), j.GetProperty("warrantyUntil").GetString()![..10]);
        Assert.Equal(8, detail.GetProperty("history").GetArrayLength());
        await Problem(await pos.PostAsync($"/api/v1/repairs/{id}/passcode", null), HttpStatusCode.NotFound, "REP-006");

        // The invoice is a tax invoice that knows its job, is not voided at the till, and the drawer counts the deposit.
        var saleId = done.GetProperty("saleId").GetGuid();
        Assert.Equal("tax_invoice", await fx.QueryDbAsync<string>($"SELECT doc_type FROM sales.sale WHERE id = '{saleId}' AND repair_job_id = '{id}'"));
        await Problem(await pos.PostAsJsonAsync($"/api/v1/sales/{saleId}/void", new { id = Guid.CreateVersion7(), reason = "x", refunds = new[] { Cash(26m) } }),
            HttpStatusCode.Conflict, "REP-002");
        var figures = (await Ok(await pos.GetAsync("/api/v1/pos/shifts/current"))).GetProperty("figures");
        Assert.Equal(10m, D(figures, "repairDeposits"));
        Assert.Equal(10m, D(figures, "repairDepositsApplied"));
        Assert.Equal(300m + 36m, D(figures, "expectedCash"));
        Assert.Equal(0L, await fx.QueryDbAsync<long>("SELECT count(*) FROM repair.deposit_mismatches()"));
        foreach (var path in new[] { "delivery-receipt", $"deposits/{dep.GetProperty("id").GetGuid()}/receipt", "tag" })
            Assert.Equal(HttpStatusCode.OK, (await pos.GetAsync($"/api/v1/repairs/{id}/{path}")).StatusCode);
    }

    [PgFact]
    public async Task Estimates_above_the_pre_authorised_amount_wait_for_the_customer_and_extra_work_needs_approval_again()
    {
        var (owner, pos, customer, category, labour, screen) = await ArrangeAsync();
        using var _o = owner; using var _p = pos;
        var id = Id(await IntakeAsync(pos, customer, category, preAuthorised: 5m));
        await MoveAsync(pos, id, "inspection_pending", "under_diagnosis");
        var detail = await EstimateAsync(pos, id, Labour(labour, 10m), Part(screen.VariantId, 1m, 30m));

        // 40.000 is above the 5.000 pre-authorised: the work cannot start; staff cannot approve it on their own.
        await Problem(await MoveRawAsync(pos, id, "in_progress"), HttpStatusCode.Conflict, "REP-005");
        var v1 = CurrentEstimate(detail);
        await Problem(await ApproveRawAsync(pos, id, v1, "pre_authorised"), HttpStatusCode.Conflict, "REP-002");

        // Sent: the job waits for approval; the customer approves by phone and the repair starts.
        detail = await SendAsync(pos, id, v1);
        Assert.Equal("waiting_approval", detail.GetProperty("job").GetProperty("status").GetString());
        detail = await Ok(await ApproveRawAsync(pos, id, v1, "phone", then: "in_progress"));
        Assert.Equal("in_progress", detail.GetProperty("job").GetProperty("status").GetString());
        Assert.Equal("phone", detail.GetProperty("estimates")[0].GetProperty("approvalMethod").GetString());
        Assert.Equal("+96892345678", detail.GetProperty("estimates")[0].GetProperty("approvalRef").GetString());

        // Extra work found: version 2 at 55.000 sends the job back to waiting for approval; it is approved by signature.
        detail = await EstimateAsync(pos, id, Labour(labour, 25m), Part(screen.VariantId, 1m, 30m));
        await Problem(await MoveRawAsync(pos, id, "testing"), HttpStatusCode.Conflict, "REP-005");
        var v2 = CurrentEstimate(detail);
        detail = await SendAsync(pos, id, v2);
        Assert.Equal("waiting_approval", detail.GetProperty("job").GetProperty("status").GetString());
        detail = await Ok(await ApproveRawAsync(pos, id, v2, "signature", then: "in_progress"));
        Assert.Equal(2, detail.GetProperty("estimates")[0].GetProperty("estimateNo").GetInt32());
        Assert.Equal("approved", detail.GetProperty("estimates")[1].GetProperty("status").GetString());
        Assert.Equal(55m, D(detail.GetProperty("job"), "ceiling"));
        Assert.Equal(3, await fx.QueryDbAsync<int>($"SELECT count(*)::int FROM repair.photo WHERE job_id = '{id}'"));

        // A third version within the 55.000 the customer approved is approved by the technician.
        detail = await EstimateAsync(pos, id, Labour(labour, 20m), Part(screen.VariantId, 1m, 30m));
        detail = await Ok(await ApproveRawAsync(pos, id, CurrentEstimate(detail), "pre_authorised"));
        Assert.Equal("approved", detail.GetProperty("estimates")[0].GetProperty("status").GetString());
        await MoveAsync(pos, id, "testing");
        var history = detail.GetProperty("history").EnumerateArray().Select(h => h.GetProperty("to").GetString()).ToArray();
        Assert.Equal(["received", "inspection_pending", "under_diagnosis", "waiting_approval", "in_progress", "waiting_approval", "in_progress"], history);
    }

    [PgFact]
    public async Task A_cancelled_job_charges_the_diagnosis_fee_writes_off_its_part_and_gives_the_rest_of_the_deposit_back()
    {
        var (owner, pos, customer, category, labour, screen) = await ArrangeAsync();
        using var _o = owner; using var _p = pos;
        var setup = await SetupAsync(owner);
        var s = setup.GetProperty("settings");
        await Ok(await owner.PutAsJsonAsync("/api/v1/repair-setup/settings", new
        {
            preAuthorised = 5m, warrantyDays = 90, passcodeKeepDays = 30, uncollectedDays = new[] { 30, 60, 90 }, diagnosisFee = 3m, hourlyRate = 10m,
            depositVatPoint = false, notifyStatuses = new[] { "received", "waiting_approval", "ready" }, termsEn = s.GetProperty("termsEn").GetString(),
            termsAr = s.GetProperty("termsAr").GetString(), version = s.GetProperty("version").GetInt32(),
        }));
        var gl = await Dev.GlSnapshotAsync("1430", "1460", "6700", "4200", "2300", "2200", "1100");
        async Task<decimal> Moved(string code) => await Dev.GlAsync(code) - gl[code];

        var id = Id(await IntakeAsync(pos, customer, category, preAuthorised: 50m));
        await DepositAsync(pos, id, 20m);
        await MoveAsync(pos, id, "inspection_pending", "under_diagnosis");
        await EstimateAsync(pos, id, Labour(labour, 10m), Part(screen.VariantId, 1m, 30m));
        await MoveAsync(pos, id, "in_progress");
        var detail = await FitAsync(pos, id, screen, 1m);

        // The customer wants the device back after all: the job goes to waiting for approval with a new estimate, then is cancelled.
        await EstimateAsync(pos, id, Labour(labour, 60m));
        detail = await SendAsync(pos, id, CurrentEstimate(await DetailAsync(pos, id)));
        detail = await MoveAsync(pos, id, "cancelled", "ready");
        Assert.Equal("cancelled", detail.GetProperty("job").GetProperty("outcome").GetString());

        // Not collected while the fitted screen is in work-in-progress; it is written off (damaged on removal).
        await Problem(await DeliverRawAsync(pos, id, detail.GetProperty("job").GetProperty("qr").GetString()!, [], fee: 3m, excessTo: "cash"), HttpStatusCode.Conflict, "REP-002");
        detail = await Ok(await pos.PostAsJsonAsync($"/api/v1/repairs/{id}/parts/{LastFit(detail)}/write-off", new { reason = "Damaged on removal" }));
        Assert.Equal(0m, D(detail.GetProperty("money"), "wip"));
        Assert.Equal(12m, await Moved("6700"));

        // Collection: 3.000 diagnosis fee from the 20.000 deposit; 17.000 back in cash.
        var q = await DeliveryAsync(pos, id);
        Assert.Equal(3m, D(q, "gross"));
        Assert.Equal(17m, D(q, "excess"));
        var done = await DeliverAsync(pos, id, [], check: "verified", excessTo: "cash");
        Assert.Equal(3m, D(done, "applied"));
        Assert.Equal(17m, D(done, "excess"));
        Assert.StartsWith("DEP-", done.GetProperty("refundNumber").GetString());
        Assert.Equal(0m, await Moved("1460"));
        Assert.Equal(-12m, await Moved("1430"));
        Assert.Equal(-2.857m, await Moved("4200"));
        Assert.Equal(-0.143m, await Moved("2200"));
        Assert.Equal(0m, await Moved("2300"));
        Assert.Equal(3m, await Moved("1100"));
        detail = await DetailAsync(pos, id);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("job").GetProperty("warrantyUntil").ValueKind);
    }

    [PgFact]
    public async Task A_warranty_claim_is_at_no_charge_and_its_parts_go_to_warranty_expense()
    {
        var (owner, pos, customer, category, labour, screen) = await ArrangeAsync();
        using var _o = owner; using var _p = pos;
        var id = Id(await IntakeAsync(pos, customer, category, preAuthorised: 50m));
        await MoveAsync(pos, id, "inspection_pending", "under_diagnosis");
        await EstimateAsync(pos, id, Labour(labour, 10m));
        await MoveAsync(pos, id, "in_progress", "testing", "completed", "ready");
        await DeliverAsync(pos, id, [Cash(10m)]);

        var gl = await Dev.GlSnapshotAsync("1460", "6600", "5200", "1100");
        async Task<decimal> Moved(string code) => await Dev.GlAsync(code) - gl[code];
        var claim = (await Ok(await pos.PostAsJsonAsync($"/api/v1/repairs/{id}/warranty-claim", new
        {
            problem = "Screen flickers again", passcodeMode = "customer_present", termsName = "Salim Al Hinai", termsSignature = Png,
        }), HttpStatusCode.Created)).GetProperty("job");
        var wid = Id(claim);
        Assert.Equal(id, claim.GetProperty("warrantyOfId").GetGuid());
        var detail = await DetailAsync(pos, wid);
        Assert.Equal("warranty", detail.GetProperty("estimates")[0].GetProperty("approvalMethod").GetString());
        await Problem(await pos.PostAsJsonAsync($"/api/v1/repairs/{wid}/estimates", new { lines = new[] { Labour(labour, 5m) } }), HttpStatusCode.Conflict, "REP-005");
        await MoveAsync(pos, wid, "inspection_pending", "under_diagnosis", "in_progress");
        await FitAsync(pos, wid, screen, 1m);
        await MoveAsync(pos, wid, "testing", "completed", "ready");
        Assert.Equal(0m, D((await DeliveryAsync(pos, wid)), "gross"));
        var done = await DeliverAsync(pos, wid);
        Assert.Equal(JsonValueKind.Null, done.GetProperty("saleNumber").ValueKind);
        Assert.Equal(12m, await Moved("6600"));
        Assert.Equal(0m, await Moved("5200"));
        Assert.Equal(0m, await Moved("1460"));
        Assert.Equal(0m, await Moved("1100"));
        // A delivered job is fixed; a warranty claim needs a repaired job still in warranty.
        await Problem(await MoveRawAsync(pos, id, "ready"), HttpStatusCode.Conflict, "REP-001");
    }

    [PgFact]
    public async Task Deposits_as_a_VAT_point_declare_their_VAT_when_taken_and_take_it_back_when_applied()
    {
        var (owner, pos, customer, category, labour, _) = await ArrangeAsync();
        using var _o = owner; using var _p = pos;
        var s = (await SetupAsync(owner)).GetProperty("settings");
        await Ok(await owner.PutAsJsonAsync("/api/v1/repair-setup/settings", new
        {
            preAuthorised = 5m, warrantyDays = 90, passcodeKeepDays = 30, uncollectedDays = new[] { 30, 60, 90 }, diagnosisFee = 0m, hourlyRate = 10m,
            depositVatPoint = true, notifyStatuses = new[] { "received", "waiting_approval", "ready" }, termsEn = s.GetProperty("termsEn").GetString(),
            termsAr = s.GetProperty("termsAr").GetString(), version = s.GetProperty("version").GetInt32(),
        }));
        try
        {
            var gl = await Dev.GlSnapshotAsync("2300", "2200", "4200", "1100");
            async Task<decimal> Moved(string code) => await Dev.GlAsync(code) - gl[code];
            var id = Id(await IntakeAsync(pos, customer, category, preAuthorised: 50m));
            var dep = await DepositAsync(pos, id, 21m);
            Assert.Equal(1m, D(dep, "tax"));
            Assert.Equal(-20m, await Moved("2300"));
            Assert.Equal(-1m, await Moved("2200"));
            await MoveAsync(pos, id, "inspection_pending", "under_diagnosis");
            await EstimateAsync(pos, id, Labour(labour, 42m));
            await MoveAsync(pos, id, "in_progress", "testing", "completed", "ready");
            await DeliverAsync(pos, id, [Cash(21m)]);
            // VAT output ends at the invoice's 2.000: 1.000 at the deposit plus the remaining 1.000 at the invoice.
            Assert.Equal(-2m, await Moved("2200"));
            Assert.Equal(0m, await Moved("2300"));
            Assert.Equal(-40m, await Moved("4200"));
            Assert.Equal(42m, await Moved("1100"));
        }
        finally
        {
            var s2 = (await SetupAsync(owner)).GetProperty("settings");
            await Ok(await owner.PutAsJsonAsync("/api/v1/repair-setup/settings", new
            {
                preAuthorised = 5m, warrantyDays = 90, passcodeKeepDays = 30, uncollectedDays = new[] { 30, 60, 90 }, diagnosisFee = 0m, hourlyRate = 10m,
                depositVatPoint = false, notifyStatuses = new[] { "received", "waiting_approval", "ready" }, termsEn = s2.GetProperty("termsEn").GetString(),
                termsAr = s2.GetProperty("termsAr").GetString(), version = s2.GetProperty("version").GetInt32(),
            }));
        }
    }

    [PgFact]
    public async Task Every_allowed_transition_passes_and_every_other_is_refused_through_the_API()
    {
        var (owner, pos, customer, category, labour, _) = await ArrangeAsync();
        using var _o = owner; using var _p = pos;
        int allowed = 0, refused = 0;
        foreach (var from in Statuses.Where(x => x != "delivered"))
            foreach (var to in Statuses.Where(x => x != from))
            {
                var c2 = await TradeInKit.CustomerAsync(pos, "T " + Suffix());
                var id = Id(await IntakeAsync(pos, c2, category, preAuthorised: 50m));
                await ToStatusAsync(pos, id, from, labour);
                // What an allowed move needs besides the transition: extra work is a new estimate; leaving approval needs the approval.
                if (from == "in_progress" && to == "waiting_approval") await EstimateAsync(pos, id, Labour(labour, 90m));
                if (from == "waiting_approval" && to is "in_progress" or "waiting_parts")
                    await Ok(await ApproveRawAsync(pos, id, CurrentEstimate(await DetailAsync(pos, id)), "phone"));
                HttpResponseMessage r;
                if (to == "delivered")
                {
                    var q = await DeliveryAsync(pos, id);
                    var due = q.GetProperty("due").GetDecimal();
                    r = await DeliverRawAsync(pos, id, q.GetProperty("qr").GetString()!, due > 0 ? [Cash(due)] : []);
                }
                else r = await MoveRawAsync(pos, id, to);
                if (Allowed[from].Contains(to))
                {
                    Assert.True(r.IsSuccessStatusCode, $"{from} -> {to}: {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
                    allowed++;
                }
                else
                {
                    Assert.True(r.StatusCode is HttpStatusCode.Conflict, $"{from} -> {to} should be refused: {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
                    refused++;
                }
            }
        Assert.Equal(22, allowed);
        Assert.Equal(99, refused);   // the 11 from Delivered are refused in the database test and by the exit gate
    }

    /// <summary>Drives a new job to a status along allowed transitions, with what each step needs.</summary>
    public static async Task ToStatusAsync(HttpClient c, Guid id, string status, Guid labour)
    {
        if (status == "received") return;
        if (status is "inspection_pending" or "cancelled") { await MoveAsync(c, id, PathTo[status]); return; }
        await MoveAsync(c, id, "inspection_pending", "under_diagnosis");
        await EstimateAsync(c, id, Labour(labour, 5m));
        switch (status)
        {
            case "under_diagnosis": return;
            case "unrepairable": await MoveAsync(c, id, "unrepairable"); return;
            case "waiting_approval":
            case "waiting_parts":
                await EstimateAsync(c, id, Labour(labour, 80m));
                await MoveAsync(c, id, "waiting_approval");
                if (status == "waiting_parts")
                {
                    await Ok(await ApproveRawAsync(c, id, CurrentEstimate(await DetailAsync(c, id)), "phone"));
                    await MoveAsync(c, id, "waiting_parts");
                }
                return;
            default:
                await MoveAsync(c, id, PathTo[status].Skip(2).ToArray());
                return;
        }
    }
}
