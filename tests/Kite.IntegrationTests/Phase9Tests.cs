using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
using static Kite.IntegrationTests.PosKit;
using static Kite.IntegrationTests.RepairKit;

namespace Kite.IntegrationTests;

/// <summary>
/// Phase 9 against the real server and PostgreSQL: parts reserved on approval and requested when short, filled oldest first,
/// never sold at the till; serial-tracked parts and outside work costed into the repair; commissions on each basis with the most
/// specific rule, shared jobs, an invoice corrected at the till (reversed and generated again), the generator run repeatedly,
/// rework clawback, approval at the branch or at head office, payouts and reversal of a paid commission - with the books.
/// </summary>
[Collection("server")]
public sealed class Phase9Tests(KiteServerFixture fx)
{
    private readonly PosKit kit = new(fx);
    private DeviceKit Dev => new(fx, kit);

    private static decimal D(JsonElement e, string name) => e.GetProperty(name).GetDecimal();
    private static string Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(4)).ToString("yyyy-MM-dd");
    private static string DaysAgo(int n) => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(4)).AddDays(-n).ToString("yyyy-MM-dd");

    public static async Task EnableAllAsync(HttpClient owner) =>
        await Ok(await owner.PutAsJsonAsync("/api/v1/settings/modules",
            new { changes = new Dictionary<string, bool> { ["device"] = true, ["repair"] = true, ["comm"] = true } }));

    private async Task<(HttpClient Owner, HttpClient Pos, Guid Customer, Guid Labour)> ArrangeAsync(decimal opening = 300m)
    {
        var owner = await kit.OwnerAsync();
        await EnableAllAsync(owner);
        await kit.SetPolicyAsync(owner, cashRounding: 0m);
        var term = await kit.TerminalAsync(owner);
        var pos = await kit.OwnerPosAsync(term);
        await OpenShiftAsync(pos, opening);
        var customer = await TradeInKit.CustomerAsync(pos, "Salim " + Suffix());
        return (owner, pos, customer, await ServiceAsync(owner));
    }

    private static JsonElement PartRow(JsonElement detail, Item part) =>
        detail.GetProperty("parts").EnumerateArray().Single(p => p.GetProperty("variantId").GetGuid() == part.VariantId);

    private static string Status(JsonElement detail) => detail.GetProperty("job").GetProperty("status").GetString()!;

    private async Task<string> UnitStatusAsync(string serial) =>
        await fx.QueryDbAsync<string>($"SELECT u.status FROM device.device_unit u JOIN device.imei_registry r ON r.device_unit_id = u.id WHERE r.kind = 'serial' AND r.value = '{serial}'");

    private async Task<decimal> ReservedAsync(Item part) =>
        await fx.QueryDbAsync<decimal>($"SELECT coalesce(sum(reserved), 0) FROM inv.stock_balance WHERE variant_id = '{part.VariantId}'");

    // ------------------------------------------------------------------------------------------------ parts
    [PgFact]
    public async Task Parts_are_reserved_on_approval_requested_when_short_filled_oldest_first_and_never_sold_at_the_till()
    {
        var (owner, pos, customer, labour) = await ArrangeAsync();
        using var _o = owner; using var _p = pos;
        var category = await CategoryAsync(owner, "BATTERY");
        var battery = await kit.ProductAsync(owner, "Battery " + Suffix(), 18m, type: "spare_part", openingQty: 2, unitCost: 6m);

        async Task<Guid> Diagnosed(decimal qty)
        {
            var id = Id(await IntakeAsync(pos, customer, category, preAuthorised: 500m));
            await MoveAsync(pos, id, "inspection_pending", "under_diagnosis");
            await EstimateAsync(pos, id, Labour(labour, 10m), Part(battery.VariantId, qty, 18m));
            return id;
        }

        // Job A needs both batteries: approved (within what the customer authorised), both are reserved for it.
        var a = await Diagnosed(2m);
        var d = await MoveAsync(pos, a, "in_progress");
        Assert.Equal("in_progress", Status(d));
        Assert.Equal((2m, 0m), (D(PartRow(d, battery), "reserved"), D(PartRow(d, battery), "requested")));
        Assert.Equal(2m, await ReservedAsync(battery));

        // The till cannot sell what is reserved, and nothing is available.
        await Problem(await pos.PostAsJsonAsync($"/api/v1/sales/{Guid.CreateVersion7()}/complete",
            new { lines = new[] { Line(battery, 1m) }, payments = new[] { Cash(18m) } }), HttpStatusCode.Conflict, "INV-001");
        Assert.Equal(0m, await fx.QueryDbAsync<decimal>($"SELECT inv.available(warehouse_id, variant_id) FROM inv.stock_balance WHERE variant_id = '{battery.VariantId}'"));

        // Jobs B, then C, each need one: none is free, so each waits for parts with a request, B's the older.
        var b = await Diagnosed(1m);
        d = await MoveAsync(pos, b, "in_progress");
        Assert.Equal("waiting_parts", Status(d));
        Assert.Equal((0m, 1m), (D(PartRow(d, battery), "reserved"), D(PartRow(d, battery), "requested")));
        Assert.Contains(d.GetProperty("history").EnumerateArray(), h => h.GetProperty("note").GetString()?.StartsWith("Waiting for parts") == true);
        var c = await Diagnosed(1m);
        Assert.Equal("waiting_parts", Status(await MoveAsync(pos, c, "in_progress")));
        var needed = (await Ok(await pos.GetAsync("/api/v1/repairs/parts-needed"))).EnumerateArray()
            .Where(r => r.GetProperty("variantId").GetGuid() == battery.VariantId).Select(r => r.GetProperty("jobId").GetGuid()).ToArray();
        Assert.Equal([b, c], needed);

        // Job A cannot fit more than there is.
        await Problem(await pos.PostAsJsonAsync($"/api/v1/repairs/{a}/parts", new { variantId = battery.VariantId, qty = 3m }), HttpStatusCode.Conflict, "INV-001");

        // One battery arrives: it goes to B (the oldest request), and B moves to in progress by itself; C keeps waiting.
        await kit.OpeningAsync(owner, battery, 1m, 6m);
        Assert.Equal(1, (await Ok(await pos.PostAsync("/api/v1/repairs/parts-needed/fill", null))).GetProperty("moved").GetInt32());
        d = await DetailAsync(pos, b);
        Assert.Equal("in_progress", Status(d));
        Assert.Equal((1m, 0m), (D(PartRow(d, battery), "reserved"), D(PartRow(d, battery), "requested")));
        Assert.Contains(d.GetProperty("history").EnumerateArray(), h => h.GetProperty("note").GetString() == "Parts arrived");
        Assert.Equal("waiting_parts", Status(await DetailAsync(pos, c)));
        Assert.Equal(0, (await Ok(await pos.PostAsync("/api/v1/repairs/parts-needed/fill", null))).GetProperty("moved").GetInt32());
        Assert.Equal(1m, await fx.QueryDbAsync<decimal>($"SELECT filled_total FROM repair.part_request WHERE job_id = '{b}'"));

        // A new estimate version waiting for the customer does not release what A holds.
        await EstimateAsync(pos, a, Labour(labour, 25m), Part(battery.VariantId, 2m, 18m));
        Assert.Equal(3m, await ReservedAsync(battery));

        // C is cancelled: its request closes. A fits its two, B its one: nothing is reserved or requested any more.
        await MoveAsync(pos, c, "cancelled");
        Assert.Equal(0m, await fx.QueryDbAsync<decimal>($"SELECT qty FROM repair.part_request WHERE job_id = '{c}'"));
        Assert.True(await fx.QueryDbAsync<bool>($"SELECT open_since IS NULL FROM repair.part_request WHERE job_id = '{c}'"));
        await FitAsync(pos, a, battery, 2m);
        await FitAsync(pos, b, battery, 1m);
        Assert.Equal(0m, await ReservedAsync(battery));
        Assert.Equal(0m, await kit.StockAsync(owner, battery));
        d = await DetailAsync(pos, a);
        Assert.Equal((2m, 2m, 0m), (D(PartRow(d, battery), "needed"), D(PartRow(d, battery), "fitted"), D(PartRow(d, battery), "reserved")));

        // A part returned unused goes back to stock and is held for the job again while the estimate still needs it.
        d = await Ok(await pos.PostAsJsonAsync($"/api/v1/repairs/{a}/parts/{LastFit(d)}/return", new { qty = 1m, note = "wrong battery" }));
        Assert.Equal(1m, await kit.StockAsync(owner, battery));
        Assert.Equal(1m, await ReservedAsync(battery));
        // Ending the job releases it.
        await MoveAsync(pos, a, "testing", "completed");
        Assert.Equal(0m, await ReservedAsync(battery));
        await DeviceKit.AssertConsistentAsync(owner);
    }

    [PgFact]
    public async Task Serial_parts_and_outside_work_are_costed_into_the_repair_exactly()
    {
        var (owner, pos, customer, labour) = await ArrangeAsync();
        using var _o = owner; using var _p = pos;
        var category = await CategoryAsync(owner, "BOARD");
        var board = await kit.ProductAsync(owner, "Logic board " + Suffix(), 80m, type: "spare_part", tracking: "serial");
        var serials = Enumerable.Range(0, 2).Select(_ => "LB" + Suffix()).ToArray();
        await Ok(await owner.PostAsJsonAsync("/api/v1/inventory/adjustments", new
        {
            branchId = kit.Branch, kind = "opening", note = "boards",
            lines = new[] { new { variantId = board.VariantId, qty = 2m, reasonCode = "opening", units = new[] { new { serial = serials[0], unitCost = 40m }, new { serial = serials[1], unitCost = 44m } } } },
        }), HttpStatusCode.Created);
        var gl = await Dev.GlSnapshotAsync("1430", "1460", "5200", "5210", "2110");
        async Task<decimal> Moved(string code) => await Dev.GlAsync(code) - gl[code];

        var id = Id(await IntakeAsync(pos, customer, category, preAuthorised: 500m));
        await MoveAsync(pos, id, "inspection_pending", "under_diagnosis");
        await EstimateAsync(pos, id, Labour(labour, 20m), Part(board.VariantId, 1m, 90m));
        await MoveAsync(pos, id, "in_progress");

        // A serial part is scanned by its serial; without one, or with an unknown one, it is refused.
        await Problem(await pos.PostAsJsonAsync($"/api/v1/repairs/{id}/parts", new { variantId = board.VariantId, qty = 1m }), HttpStatusCode.BadRequest, "VAL-001");
        await Problem(await pos.PostAsJsonAsync($"/api/v1/repairs/{id}/parts", new { variantId = board.VariantId, qty = 1m, serial = "LB000000" }), HttpStatusCode.NotFound, "DEV-003");
        var d = await Ok(await pos.PostAsJsonAsync($"/api/v1/repairs/{id}/parts", new { variantId = board.VariantId, qty = 1m, serial = serials[1] }));
        var fitted = d.GetProperty("work").EnumerateArray().Single(w => w.GetProperty("kind").GetString() == "part");
        Assert.Equal(serials[1], fitted.GetProperty("serial").GetString());
        Assert.Equal(44m, D(d.GetProperty("money"), "wip"));
        Assert.Equal("fitted", await UnitStatusAsync(serials[1]));
        // The fitted unit is not in stock: fitting it again is refused.
        await Problem(await pos.PostAsJsonAsync($"/api/v1/repairs/{id}/parts", new { variantId = board.VariantId, qty = 1m, serial = serials[1] }), HttpStatusCode.Conflict, "DEV-004");

        // Returned unused, it is in stock again as itself, at its own cost; then the other board is fitted.
        d = await Ok(await pos.PostAsJsonAsync($"/api/v1/repairs/{id}/parts/{fitted.GetProperty("id").GetGuid()}/return", new { qty = 1m, note = "faulty" }));
        Assert.Equal("in_stock", await UnitStatusAsync(serials[1]));
        Assert.Equal(0m, D(d.GetProperty("money"), "wip"));
        d = await Ok(await pos.PostAsJsonAsync($"/api/v1/repairs/{id}/parts", new { variantId = board.VariantId, qty = 1m, serial = serials[0] }));
        Assert.Equal(40m, D(d.GetProperty("money"), "wip"));

        // Outside work at cost: into work in progress, owed to the supplier; a mistaken entry is reversed in full, once.
        d = await Ok(await pos.PostAsJsonAsync($"/api/v1/repairs/{id}/outside-work", new { vendor = "Ruwi Board Lab", amount = 12.5m, reference = "RBL-77" }));
        var wrong = d.GetProperty("work").EnumerateArray().Single(w => w.GetProperty("kind").GetString() == "external").GetProperty("id").GetGuid();
        Assert.Equal(52.5m, D(d.GetProperty("money"), "wip"));
        await Problem(await pos.PostAsJsonAsync($"/api/v1/repairs/{id}/outside-work", new { vendor = "", amount = 5m }), HttpStatusCode.BadRequest, "VAL-001");
        await Ok(await pos.PostAsJsonAsync($"/api/v1/repairs/{id}/outside-work/{wrong}/reverse", new { reason = "entered on the wrong job" }));
        await Problem(await pos.PostAsJsonAsync($"/api/v1/repairs/{id}/outside-work/{wrong}/reverse", new { reason = "again" }), HttpStatusCode.Conflict, "REP-002");
        d = await Ok(await pos.PostAsJsonAsync($"/api/v1/repairs/{id}/outside-work", new { vendor = "Ruwi Board Lab", amount = 7.25m, reference = "RBL-78" }));
        Assert.Equal(47.25m, D(d.GetProperty("money"), "wip"));
        Assert.Equal(47.25m, await Moved("1460"));
        Assert.Equal(-7.25m, await Moved("2110"));

        // Delivered: the part to the cost of repairs, the outside work to its own cost; nothing is left in progress.
        await MoveAsync(pos, id, "testing", "completed", "ready");
        await DeliverAsync(pos, id, [Cash(110m)]);
        Assert.Equal(0m, await Moved("1460"));
        Assert.Equal(-40m, await Moved("1430"));
        Assert.Equal(40m, await Moved("5200"));
        Assert.Equal(7.25m, await Moved("5210"));
        Assert.Equal(40m, await fx.QueryDbAsync<decimal>($"SELECT repair.parts_cost('{id}')"));
        Assert.Equal(7.25m, await fx.QueryDbAsync<decimal>($"SELECT repair.outside_cost('{id}')"));
        Assert.Equal(await fx.QueryDbAsync<decimal>("SELECT coalesce(sum(repair.wip(id)), 0) FROM repair.repair_job"), await Dev.GlAsync("1460"));
        await DeviceKit.AssertConsistentAsync(owner);
    }

    // ------------------------------------------------------------------------------------------------ commissions
    private static object Rule(string name, string basis, decimal? amount = null, decimal? rate = null, decimal? min = null, decimal? max = null, Guid? technicianId = null,
        Guid? branchId = null, Guid? categoryId = null, string? from = null) =>
        new { nameEn = name, nameAr = "قاعدة " + name, basis, amount, rate, minAmount = min, maxAmount = max, technicianId, branchId, categoryId, effectiveFrom = from ?? DaysAgo(30) };

    private async Task<(Guid Job, Guid Sale)> RepairedAsync(HttpClient pos, Guid customer, Guid category, Guid labour, Item part, Guid technician,
        decimal outside = 0m, (Guid Tech, decimal Share)[]? shares = null)
    {
        var id = Id(await IntakeAsync(pos, customer, category, preAuthorised: 500m, technicianId: technician));
        await MoveAsync(pos, id, "inspection_pending", "under_diagnosis");
        await EstimateAsync(pos, id, Labour(labour, 15m), Part(part.VariantId, 1m, 21m));
        await MoveAsync(pos, id, "in_progress");
        await FitAsync(pos, id, part, 1m);
        if (outside > 0) await Ok(await pos.PostAsJsonAsync($"/api/v1/repairs/{id}/outside-work", new { vendor = "Muscat Micro", amount = outside }));
        if (shares is not null)
            await Ok(await pos.PutAsJsonAsync($"/api/v1/repairs/{id}/shares", new { shares = shares.Select(s => new { technicianId = s.Tech, share = s.Share }) }), HttpStatusCode.NoContent);
        await MoveAsync(pos, id, "testing", "completed", "ready");
        var done = await DeliverAsync(pos, id, [Cash(36m)]);
        return (id, done.GetProperty("saleId").GetGuid());
    }

    private static async Task<JsonElement[]> AccrualsAsync(HttpClient c, Guid job) =>
        (await Ok(await c.GetAsync($"/api/v1/commissions?jobId={job}"))).EnumerateArray().ToArray();

    [PgFact]
    public async Task Commissions_follow_each_basis_shared_jobs_corrections_the_generator_clawback_approval_and_payout()
    {
        var (owner, pos, customer, labour) = await ArrangeAsync();
        using var _o = owner; using var _p = pos;
        var screen = await CategoryAsync(owner, "SCREEN");
        var batteryCat = await CategoryAsync(owner, "BATTERY");
        var part = await kit.ProductAsync(owner, "Comm part " + Suffix(), 21m, type: "spare_part", openingQty: 20, unitCost: 12m);
        var tl = (await kit.UserAsync(owner, "tl", "technician")).Id;   // 10% of labour
        var tp = (await kit.UserAsync(owner, "tp", "technician")).Id;   // 20% of profit; 25% on screens
        var tf = (await kit.UserAsync(owner, "tf", "technician")).Id;   // 2.500 a job
        var tx = (await kit.UserAsync(owner, "tx", "technician")).Id;   // no rule until later
        var gl = await Dev.GlSnapshotAsync("6300", "2400", "1100", "4200", "4210", "2200");
        async Task<decimal> Moved(string code) => await Dev.GlAsync(code) - gl[code];

        // Rules: refused when they do not fit together; one per scope at a time.
        await Problem(await owner.PostAsJsonAsync("/api/v1/commissions/rules", Rule("bad", "labour", rate: 10m, min: 5m, max: 2m, technicianId: tl)), HttpStatusCode.BadRequest, "COM-001");
        await Problem(await owner.PostAsJsonAsync("/api/v1/commissions/rules", Rule("bad", "fixed", technicianId: tl)), HttpStatusCode.BadRequest, "COM-001");
        await Problem(await owner.PostAsJsonAsync("/api/v1/commissions/rules", Rule("bad", "fixed", amount: 1m, technicianId: tl, branchId: kit.Branch)), HttpStatusCode.BadRequest, "COM-001");
        var labourRule = await Ok(await owner.PostAsJsonAsync("/api/v1/commissions/rules", Rule("TL labour", "labour", rate: 10m, technicianId: tl)));
        await Problem(await owner.PostAsJsonAsync("/api/v1/commissions/rules", Rule("TL again", "fixed", amount: 1m, technicianId: tl)), HttpStatusCode.Conflict, "COM-001");
        await Ok(await owner.PostAsJsonAsync("/api/v1/commissions/rules", Rule("TP profit", "profit", rate: 20m, min: 1m, max: 50m, technicianId: tp)));
        var screenRule = await Ok(await owner.PostAsJsonAsync("/api/v1/commissions/rules", Rule("TP screens", "profit", rate: 25m, technicianId: tp, categoryId: screen)));
        await Ok(await owner.PostAsJsonAsync("/api/v1/commissions/rules", Rule("TF fixed", "fixed", amount: 2.5m, technicianId: tf)));

        // Each basis, the most specific rule winning: labour 15.000 and a part 21.000 (VAT included), the part cost 12.000.
        var j1 = await RepairedAsync(pos, customer, screen, labour, part, tl);
        var j2 = await RepairedAsync(pos, customer, screen, labour, part, tp, outside: 5m);
        var j3 = await RepairedAsync(pos, customer, batteryCat, labour, part, tp);
        var j4 = await RepairedAsync(pos, customer, screen, labour, part, tf);
        var a1 = Assert.Single(await AccrualsAsync(owner, j1.Job));
        Assert.Equal(1.429m, D(a1, "amount"));                     // 10% of 14.286
        var a2 = Assert.Single(await AccrualsAsync(owner, j2.Job));
        Assert.Equal(4.322m, D(a2, "amount"));                     // 25% of (14.286 + 20.000 - 12.000 - 5.000)
        Assert.Equal(screenRule.GetProperty("ruleKey").GetGuid(), a2.GetProperty("ruleKey").GetGuid());
        Assert.Equal(17.286m, a2.GetProperty("trace").GetProperty("basis").GetProperty("profit").GetDecimal());
        Assert.Equal(4.457m, D(Assert.Single(await AccrualsAsync(owner, j3.Job)), "amount"));   // 20% of 22.286
        Assert.Equal(2.5m, D(Assert.Single(await AccrualsAsync(owner, j4.Job)), "amount"));

        // A shared job: shares make 100%, each technician paid by their own rule.
        var shareJob = Id(await IntakeAsync(pos, customer, screen, preAuthorised: 500m, technicianId: tl));
        await Problem(await pos.PutAsJsonAsync($"/api/v1/repairs/{shareJob}/shares", new { shares = new[] { new { technicianId = tl, share = 59m }, new { technicianId = tf, share = 40m } } }),
            HttpStatusCode.BadRequest, "COM-003");
        await MoveAsync(pos, shareJob, "cancelled");
        var j5 = await RepairedAsync(pos, customer, screen, labour, part, tl, shares: [(tl, 60m), (tf, 40m)]);
        var a5 = await AccrualsAsync(owner, j5.Job);
        Assert.Equal(0.857m, D(a5.Single(a => a.GetProperty("technicianId").GetGuid() == tl), "amount"));   // 1.429 x 60%
        Assert.Equal(1m, D(a5.Single(a => a.GetProperty("technicianId").GetGuid() == tf), "amount"));       // 2.500 x 40%
        await Problem(await pos.PutAsJsonAsync($"/api/v1/repairs/{j5.Job}/shares", new { shares = new[] { new { technicianId = tl, share = 100m } } }),
            HttpStatusCode.Conflict, "COM-002");

        // The generator: a job delivered before its technician had a rule gets one accrual, however often and however
        // concurrently the generator runs.
        var j6 = await RepairedAsync(pos, customer, screen, labour, part, tx);
        Assert.Empty(await AccrualsAsync(owner, j6.Job));
        await Ok(await owner.PostAsJsonAsync("/api/v1/commissions/rules", Rule("TX fixed", "fixed", amount: 1.25m, technicianId: tx)));
        var runs = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
            (await Ok(await owner.PostAsJsonAsync("/api/v1/commissions/generate", new { jobId = j6.Job }))).GetProperty("written").GetInt32()));
        Assert.Equal(1, runs.Sum());
        Assert.Equal(0, (await Ok(await owner.PostAsJsonAsync("/api/v1/commissions/generate", new { }))).GetProperty("written").GetInt32());
        Assert.Equal(1.25m, D(Assert.Single(await AccrualsAsync(owner, j6.Job)), "amount"));
        Assert.Equal(1L, await fx.QueryDbAsync<long>($"SELECT count(*) FROM comm.accrual WHERE job_id = '{j6.Job}'"));

        // A repair invoice is corrected at the till: credited in full (cash back from the drawer), a corrected invoice with
        // less labour paid there; the commission is reversed and generated again from the corrected invoice.
        var books = await Dev.GlSnapshotAsync("1100", "4200", "4210", "4900", "2200");
        await Problem(await pos.PostAsJsonAsync($"/api/v1/sales/{j1.Sale}/void", new { id = Guid.CreateVersion7(), reason = "x", refunds = new[] { Cash(36m) } }),
            HttpStatusCode.Conflict, "REP-002");
        var correctionId = Guid.CreateVersion7();
        object Correction(Guid cid) => new
        {
            id = cid, reason = "Labour charged at the wrong rate", refunds = new[] { Cash(36m) }, expectedCredit = 36m,
            lines = new object[] { Labour(labour, 10m), Part(part.VariantId, 1m, 21m) }, payments = new[] { Cash(31m) },
        };
        var corrected = await Ok(await pos.PostAsJsonAsync($"/api/v1/repairs/{j1.Job}/correct", Correction(correctionId)));
        Assert.Equal(36m, D(corrected, "credited"));
        Assert.Equal(31m, D(corrected, "correctedGross"));
        Assert.StartsWith("CN-", corrected.GetProperty("creditNoteNumber").GetString());
        // The same request again changes nothing.
        var again = await Ok(await pos.PostAsJsonAsync($"/api/v1/repairs/{j1.Job}/correct", Correction(correctionId)));
        Assert.Equal(corrected.GetProperty("correctedId").GetGuid(), again.GetProperty("correctedId").GetGuid());
        Assert.Equal(-5m, await Dev.GlAsync("1100") - books["1100"]);
        // The credit note goes to sales returns (34.286 net, 1.714 VAT); the corrected invoice is new income (9.524 + 20.000, 1.476 VAT).
        Assert.Equal(34.286m, await Dev.GlAsync("4900") - books["4900"]);
        Assert.Equal(-9.524m, await Dev.GlAsync("4200") - books["4200"]);
        Assert.Equal(-20m, await Dev.GlAsync("4210") - books["4210"]);
        Assert.Equal(0.238m, await Dev.GlAsync("2200") - books["2200"]);
        var c1 = await AccrualsAsync(owner, j1.Job);
        Assert.Equal(3, c1.Length);
        Assert.Equal("reversed", c1.Single(a => a.GetProperty("id").GetGuid() == a1.GetProperty("id").GetGuid()).GetProperty("status").GetString());
        Assert.Equal(-1.429m, D(c1.Single(a => a.GetProperty("kind").GetString() == "reversal"), "amount"));
        var v2 = c1.Single(a => a.GetProperty("kind").GetString() == "repair" && a.GetProperty("kindVersion").GetInt32() == 2);
        Assert.Equal(0.952m, D(v2, "amount"));                                    // 10% of 9.524
        Assert.Equal(corrected.GetProperty("correctedNumber").GetString(), v2.GetProperty("invoiceNumber").GetString());
        // The generator does not bring the credited invoice's commission back.
        Assert.Equal(0, (await Ok(await owner.PostAsJsonAsync("/api/v1/commissions/generate", new { jobId = j1.Job }))).GetProperty("written").GetInt32());

        // Credited again with nothing charged (the money to store credit): the commission goes, and no invoice stands.
        await Ok(await pos.PostAsJsonAsync($"/api/v1/repairs/{j3.Job}/correct", new
        {
            id = Guid.CreateVersion7(), reason = "Goodwill: repair not charged", refunds = new[] { new { method = "store_credit", amount = 36m } },
        }));
        var c3 = await AccrualsAsync(owner, j3.Job);
        Assert.Equal(0m, c3.Sum(a => D(a, "amount")));
        await Problem(await pos.PostAsJsonAsync($"/api/v1/repairs/{j3.Job}/correct", new { id = Guid.CreateVersion7(), reason = "again", refunds = new[] { Cash(1m) } }),
            HttpStatusCode.Conflict, "REP-008");

        // Rework: a warranty claim within 30 days takes back half of the original technician's commission, once.
        var s = await Ok(await owner.GetAsync("/api/v1/commissions/settings"));
        await Ok(await owner.PutAsJsonAsync("/api/v1/commissions/settings", new
        {
            generateOn = "delivered", approvalBy = "branch", clawbackDays = 30, clawbackPercent = 50m, version = s.GetProperty("version").GetInt32(),
        }), HttpStatusCode.NoContent);
        // (The first claim is opened by mistake, cancelled and the device handed back; the second takes nothing more.)
        foreach (var n in new[] { 1, 2 })
        {
            var claim = (await Ok(await pos.PostAsJsonAsync($"/api/v1/repairs/{j4.Job}/warranty-claim", new
            {
                problem = "Screen flickers again", passcodeMode = "customer_present", termsName = "Salim Al Hinai", termsSignature = Png,
            }), HttpStatusCode.Created)).GetProperty("job");
            if (n == 1)
            {
                await MoveAsync(pos, Id(claim), "cancelled", "ready");
                await DeliverAsync(pos, Id(claim));
            }
        }
        var c4 = await AccrualsAsync(owner, j4.Job);
        Assert.Equal(-1.25m, D(c4.Single(a => a.GetProperty("kind").GetString() == "clawback"), "amount"));
        Assert.Equal(2, c4.Length);

        // Approval: at head office only, when the business says so; a branch manager is refused there, allowed at the branch.
        var bm = await kit.UserAsync(owner, "bm", "branch_manager");
        using var manager = fx.Client(await fx.TokenAsync(bm.Username, bm.Password));
        s = await Ok(await owner.GetAsync("/api/v1/commissions/settings"));
        await Ok(await owner.PutAsJsonAsync("/api/v1/commissions/settings", new
        {
            generateOn = "delivered", approvalBy = "head_office", clawbackDays = 30, clawbackPercent = 50m, version = s.GetProperty("version").GetInt32(),
        }), HttpStatusCode.NoContent);
        var tlOpen = (await Ok(await owner.GetAsync($"/api/v1/commissions?technicianId={tl}&status=accrued"))).EnumerateArray().Select(a => a.GetProperty("id").GetGuid()).ToArray();
        Assert.Equal(2, tlOpen.Length);                                          // the corrected job's 0.952 and the shared job's 0.857
        await Problem(await manager.PostAsJsonAsync("/api/v1/commissions/approve", new { ids = tlOpen }), HttpStatusCode.Forbidden, "AUTH-403");
        Assert.Equal(1, (await Ok(await owner.PostAsJsonAsync("/api/v1/commissions/approve", new { ids = tlOpen.Take(1) }))).GetProperty("approved").GetInt32());
        s = await Ok(await owner.GetAsync("/api/v1/commissions/settings"));
        await Ok(await owner.PutAsJsonAsync("/api/v1/commissions/settings", new
        {
            generateOn = "delivered", approvalBy = "branch", clawbackDays = 30, clawbackPercent = 50m, version = s.GetProperty("version").GetInt32(),
        }), HttpStatusCode.NoContent);
        Assert.Equal(1, (await Ok(await manager.PostAsJsonAsync("/api/v1/commissions/approve", new { ids = tlOpen.Skip(1) }))).GetProperty("approved").GetInt32());
        await Problem(await manager.PostAsJsonAsync("/api/v1/commissions/approve", new { ids = tlOpen }), HttpStatusCode.Conflict, "COM-002");

        // Payout in cash: one line per accrual; paying them again is refused.
        var paid = await Ok(await owner.PostAsJsonAsync("/api/v1/commissions/payouts", new { branchId = kit.Branch, technicianId = tl, accrualIds = tlOpen, method = "cash", reference = "Nov" }));
        Assert.Equal(1.809m, D(paid, "total"));
        Assert.StartsWith("COM-B01-", paid.GetProperty("number").GetString());
        await Problem(await owner.PostAsJsonAsync("/api/v1/commissions/payouts", new { branchId = kit.Branch, technicianId = tl, accrualIds = tlOpen, method = "bank" }),
            HttpStatusCode.Conflict, "COM-002");
        await Problem(await owner.PostAsJsonAsync("/api/v1/commissions/payouts", new { branchId = kit.Branch, technicianId = tf, accrualIds = tlOpen, method = "bank" }),
            HttpStatusCode.Conflict, "COM-002");

        // A paid commission reversed: an approved deduction waiting for the next payout, which cannot be below zero.
        var shared = tlOpen[1];
        var rev = (await Ok(await owner.PostAsJsonAsync($"/api/v1/commissions/{shared}/reverse", new { reason = "Shared job: the screen was refitted by another technician" })))
            .GetProperty("reversalId").GetGuid();
        await Problem(await owner.PostAsJsonAsync($"/api/v1/commissions/{shared}/reverse", new { reason = "twice" }), HttpStatusCode.Conflict, "COM-002");
        Assert.Equal("approved", await fx.QueryDbAsync<string>($"SELECT status FROM comm.accrual WHERE id = '{rev}'"));
        await Problem(await owner.PostAsJsonAsync("/api/v1/commissions/payouts", new { branchId = kit.Branch, technicianId = tl, accrualIds = new[] { rev }, method = "cash" }),
            HttpStatusCode.BadRequest, "COM-004");

        // The statement: what was payable at the start and the end of the period.
        var st = await Ok(await owner.GetAsync($"/api/v1/commissions/statement?technicianId={tl}&from={Today}&to={Today}"));
        Assert.Equal(0m, D(st, "opening"));
        Assert.Equal(-0.857m, D(st, "closing"));                                   // 1.429 - 1.429 + 0.952 + 0.857 - 1.809 - 0.857
        Assert.Single(st.GetProperty("payouts").EnumerateArray());
        var usage = (await Ok(await owner.GetAsync("/api/v1/commissions/rule-usage"))).EnumerateArray()
            .Single(r => r.GetProperty("ruleKey").GetGuid() == labourRule.GetProperty("ruleKey").GetGuid());
        Assert.Equal(3L, usage.GetProperty("accruals").GetInt64());                  // j1, j1 corrected, j5
        var perf = (await Ok(await owner.GetAsync($"/api/v1/commissions/performance?from={Today}&to={Today}"))).EnumerateArray()
            .Single(r => r.GetProperty("technicianId").GetGuid() == tf);
        Assert.Equal(2L, perf.GetProperty("rework").GetInt64());                   // two claims on TF's job

        // The books: commission expense is exactly what was accrued; what is owed is accrued less paid, all over the ledger.
        var accrued = await fx.QueryDbAsync<decimal>($"SELECT coalesce(sum(amount), 0) FROM comm.accrual WHERE technician_id IN ('{tl}','{tp}','{tf}','{tx}')");
        Assert.Equal(1.429m - 1.429m + 0.952m + 4.322m + 4.457m - 4.457m + 2.5m - 1.25m + 0.857m + 1m + 1.25m - 0.857m, accrued);
        Assert.Equal(accrued, await Moved("6300"));
        Assert.Equal(-(accrued - 1.809m), await Moved("2400"));
        Assert.Equal(await fx.QueryDbAsync<decimal>("SELECT coalesce((SELECT sum(total) FROM comm.payout), 0) - coalesce((SELECT sum(amount) FROM comm.accrual), 0)"),
            await Dev.GlAsync("2400"));
        Assert.Equal(0L, await fx.QueryDbAsync<long>("SELECT count(*) FROM comm.accrual WHERE journal_id IS NULL AND amount <> 0"));
    }
}
