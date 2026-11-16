using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
using static Kite.IntegrationTests.DeviceKit;
using static Kite.IntegrationTests.PosKit;
using static Kite.IntegrationTests.TradeInKit;

namespace Kite.IntegrationTests;

/// <summary>
/// Phase 7 against the real server and PostgreSQL: trade-ins taken in at the till, the four ways they are settled (credit on a
/// sale with the rest paid, credit on a sale with the excess left as store credit or cash, store credit only, cash buy-back),
/// the journals each posts, store credit spent at the till, profit-margin VAT on resale, reconnecting a phone the business sold,
/// approvals, consent, refusals and voids.
/// </summary>
[Collection("server")]
public sealed class Phase7Tests(KiteServerFixture fx)
{
    private readonly PosKit kit = new(fx);
    private DeviceKit Dev => new(fx, kit);
    private TradeInKit Ti => new(fx);

    private static decimal D(JsonElement e, string name) => e.GetProperty(name).GetDecimal();

    [PgFact]
    public async Task All_four_settlement_types_post_the_expected_journals_and_a_margin_resale_shows_no_VAT()
    {
        using var owner = await kit.OwnerAsync();
        await EnableAsync(owner);
        await MarginAsync(owner, true);
        await kit.SetPolicyAsync(owner, cashRounding: 0m);
        var phone = await Dev.PhoneAsync(owner, "Trade phone " + Suffix(), 150m);
        var cable = await kit.ProductAsync(owner, "USB-C cable " + Suffix(), 20m, openingQty: 10, unitCost: 8m);
        var gradeA = await GradeAsync(owner, "A");
        await GuideAsync(owner, phone, gradeA, 120m);
        var stock = await Dev.ReceiveAsync(owner, phone, (110m, "brand_new", "in_stock", null, null), (110m, "brand_new", "in_stock", null, null));
        var term = await kit.TerminalAsync(owner);
        using var pos = await kit.OwnerPosAsync(term);
        await OpenShiftAsync(pos, 300m);
        var customer = await CustomerAsync(pos, "Salim " + Suffix());
        var gl = await Dev.GlSnapshotAsync("1410", "2320", "2310", "1100", "1150", "2200");
        async Task<decimal> Moved(string code) => await Dev.GlAsync(code) - gl[code];

        // 1. Applied to a sale, the customer pays the rest. Accepting puts the phone in used stock against trade-in clearing.
        var t1 = await TakeInAsync(pos, customer, phone, gradeA, 90m);
        var t1Id = t1.GetProperty("id").GetGuid();
        Assert.Equal("accepted", t1.GetProperty("status").GetString());
        Assert.Equal(90m, D(t1, "outstanding"));
        Assert.Equal(120m, D(t1, "guideValue"));
        Assert.Equal(120m, D(t1, "offeredValue"));
        Assert.True(t1.GetProperty("marginScheme").GetBoolean());
        Assert.StartsWith("SI-", t1.GetProperty("selfInvoiceNumber").GetString());
        Assert.Equal(90m, await Moved("1410"));
        Assert.Equal(-90m, await Moved("2320"));
        var unit1 = t1.GetProperty("deviceUnitId").GetGuid();
        Assert.Equal("under_inspection", await StatusAsync(owner, unit1));
        Assert.Equal(90m, await CostAsync(owner, unit1));
        var s1 = await SellAsync(pos, [UnitLine(phone, stock[0].Id)], [TradeIn(t1Id, 90m), Cash(60m)], customerId: customer);
        Assert.Equal(150m, D(s1, "gross"));
        t1 = await TradeInAsync(pos, t1Id);
        Assert.Equal("settled", t1.GetProperty("status").GetString());
        Assert.Equal(0m, D(t1, "outstanding"));
        Assert.Equal(0m, await Moved("2320"));

        // 2. Applied to a sale with the excess left over: part as store credit, the rest in cash.
        var t2 = await TakeInAsync(pos, customer, phone, gradeA, 120m);
        var t2Id = t2.GetProperty("id").GetGuid();
        await SellAsync(pos, [Line(cable, 1)], [TradeIn(t2Id, 20m)], customerId: customer);
        Assert.Equal(100m, D(await TradeInAsync(pos, t2Id), "outstanding"));
        await Ok(await pos.PostAsJsonAsync($"/api/v1/tradeins/{t2Id}/settle", new { kind = "store_credit", amount = 60m }));
        Assert.Equal(60m, await BalanceAsync(pos, customer));
        await Problem(await pos.PostAsJsonAsync($"/api/v1/tradeins/{t2Id}/settle", new { kind = "cash", amount = 40.0005m }), HttpStatusCode.BadRequest, "VAL-001");
        await Problem(await pos.PostAsJsonAsync($"/api/v1/tradeins/{t2Id}/settle", new { kind = "cash", amount = 41m }), HttpStatusCode.Conflict, "TRD-005");
        t2 = (await Ok(await pos.PostAsJsonAsync($"/api/v1/tradeins/{t2Id}/settle", new { kind = "cash" }))).GetProperty("tradeIn");
        Assert.Equal("settled", t2.GetProperty("status").GetString());
        await Problem(await pos.PostAsJsonAsync($"/api/v1/tradeins/{t2Id}/settle", new { kind = "cash", amount = 1m }), HttpStatusCode.Conflict, "TRD-001");

        // 3. Store credit only, settled as it is accepted.
        var t3 = await TakeInAsync(pos, customer, phone, gradeA, 50m, settle: new { kind = "store_credit" });
        Assert.Equal("settled", t3.GetProperty("status").GetString());
        Assert.Equal(110m, await BalanceAsync(pos, customer));
        Assert.Equal(-110m, await Moved("2310"));

        // 4. Cash buy-back, paid from the drawer as it is accepted.
        var t4 = await TakeInAsync(pos, customer, phone, gradeA, 45m, settle: new { kind = "cash" });
        Assert.Equal("settled", t4.GetProperty("status").GetString());
        Assert.Equal("buy_back", await Ti.DbAsync<string>($"SELECT source FROM device.acquisition WHERE source_doc_id = '{t4.GetProperty("id").GetGuid()}'"));

        // Store credit spends at the till, never past the balance.
        await Problem(await pos.PostAsJsonAsync($"/api/v1/sales/{Guid.CreateVersion7()}/complete", new
        {
            lines = new[] { UnitLine(phone, stock[1].Id) }, payments = new[] { StoreCredit(111m), Card(39m) }, customerId = customer,
        }), HttpStatusCode.Conflict, "TRD-006");
        await Problem(await pos.PostAsJsonAsync($"/api/v1/sales/{Guid.CreateVersion7()}/complete", new
        {
            lines = new[] { UnitLine(phone, stock[1].Id) }, payments = new[] { StoreCredit(110m), Card(40m) },
        }), HttpStatusCode.BadRequest, "POS-016");
        await SellAsync(pos, [UnitLine(phone, stock[1].Id)], [StoreCredit(110m), Card(40m)], customerId: customer);
        Assert.Equal(0m, await BalanceAsync(pos, customer));

        // The journals of all four, against the expected table: used stock in at the agreed values (90+120+50+45), clearing back
        // to zero, store credit raised (60+50) and spent (110), cash in from the first sale (60) and out for two buy-backs (40+45),
        // output VAT on the three standard-rated sales (prices include 5% VAT).
        Assert.Equal(305m, await Moved("1410"));
        Assert.Equal(0m, await Moved("2320"));
        Assert.Equal(0m, await Moved("2310"));
        Assert.Equal(60m - 40m - 45m, await Moved("1100"));
        Assert.Equal(40m, await Moved("1150"));
        Assert.Equal(-(7.143m + 0.952m + 7.143m), await Moved("2200"));
        var f = await Ti.FiguresAsync(pos);
        Assert.Equal(110m, D(f, "tradeInCredit"));
        Assert.Equal(110m, D(f, "storeCreditPayments"));
        Assert.Equal(85m, D(f, "tradeInPayouts"));
        Assert.Equal(300m + 60m - 85m, D(f, "expectedCash"));

        // The first phone, inspected and back on sale, sells under the profit margin scheme: VAT on (130 - 90) only, inside the price.
        await Ok(await InspectAsync(owner, unit1, AllPass(), "restock", "used", gradeA, price: 130m));
        var till = await Ok(await pos.GetAsync($"/api/v1/pos/units?variantId={phone.VariantId}"));
        var offered = till.EnumerateArray().Single(u => u.GetProperty("unitId").GetGuid() == unit1);
        Assert.Equal(90m, D(offered, "marginCost"));
        var vat = await Dev.GlAsync("2200");
        var s5 = await SellAsync(pos, [UnitLine(phone, unit1)], [Cash(130m)], customerId: customer);
        Assert.Equal(130m, D(s5, "gross"));
        var s5Id = s5.GetProperty("id").GetGuid();
        Assert.Equal("margin|90.000|1.905|128.095", await Ti.DbAsync<string>(
            $"SELECT tax_scheme || '|' || margin_purchase_price || '|' || tax_amount || '|' || net_amount FROM sales.sale_line WHERE sale_id = '{s5Id}'"));
        Assert.Equal(-1.905m, await Dev.GlAsync("2200") - vat);
        var receipt = await Ok(await pos.GetAsync($"/api/v1/sales/{s5Id}/receipt"));
        Assert.True(receipt.GetProperty("lines")[0].GetProperty("margin").GetBoolean());
        Assert.Equal(0m, D(receipt.GetProperty("lines")[0], "tax"));
        Assert.Equal(0m, D(receipt, "tax"));
        Assert.Equal(130m, D(receipt, "marginGross"));
        Assert.Empty(receipt.GetProperty("taxBreakdown").EnumerateArray());
        Assert.Contains("profit margin scheme", receipt.GetProperty("marginNoteEn").GetString());

        // The trade-in documents: the self-invoice for a margin purchase, as a PDF.
        var doc = await pos.GetAsync($"/api/v1/tradeins/{t1Id}/document");
        Assert.Equal(HttpStatusCode.OK, doc.StatusCode);
        Assert.Equal("application/pdf", doc.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString((await doc.Content.ReadAsByteArrayAsync())[..4]));
        await AssertConsistentAsync(owner);
    }

    [PgFact]
    public async Task A_phone_the_business_sold_reconnects_and_the_rules_hold_at_every_step()
    {
        using var owner = await kit.OwnerAsync();
        await EnableAsync(owner);
        await MarginAsync(owner, true);
        await kit.SetPolicyAsync(owner, cashRounding: 0m);
        var phone = await Dev.PhoneAsync(owner, "Reconnect phone " + Suffix(), 200m);
        var gradeA = await GradeAsync(owner, "A");
        await GuideAsync(owner, phone, gradeA, 80m);
        var stock = await Dev.ReceiveAsync(owner, phone, (150m, "brand_new", "in_stock", null, null), (150m, "brand_new", "in_stock", null, null));
        var term = await kit.TerminalAsync(owner);
        using var pos = await kit.OwnerPosAsync(term);
        await OpenShiftAsync(pos, 200m);
        var customer = await CustomerAsync(pos, "Aisha " + Suffix());
        var other = await CustomerAsync(pos, "Khalid " + Suffix());

        // A phone still in stock, or on the blocked list, is not taken in.
        await Problem(await CreateRawAsync(pos, customer, phone, stock[1].Imei), HttpStatusCode.Conflict, "TRD-004");
        var stolen = Imei();
        await Ok(await owner.PostAsJsonAsync("/api/v1/devices/blocked", new { value = stolen, reason = "Reported stolen", reference = "ROP 22/119" }), HttpStatusCode.Created);
        await Problem(await CreateRawAsync(pos, customer, phone, stolen), HttpStatusCode.Conflict, "DEV-014");

        // Sold, then traded back: the same device record comes back under inspection at the agreed value.
        await SellAsync(pos, [UnitLine(phone, stock[0].Id)], [Cash(200m)], customerId: customer);
        var back = await TakeInAsync(pos, customer, phone, gradeA, 75m, imei: stock[0].Imei);
        Assert.Equal(stock[0].Id, back.GetProperty("deviceUnitId").GetGuid());
        Assert.True(back.GetProperty("reconnected").GetBoolean());
        Assert.Equal("under_inspection", await StatusAsync(owner, stock[0].Id));
        Assert.Equal(75m, await CostAsync(owner, stock[0].Id));
        await AssertConsistentAsync(owner);

        // Consent, cloud lock and civil ID are checked at acceptance; nothing is saved when one is missing.
        var open = await OpenAsync(pos, customer, phone, gradeA, 60m, cloudLockRemoved: false);
        await Problem(await AcceptRawAsync(pos, open), HttpStatusCode.Conflict, "TRD-004");
        open = await OpenAsync(pos, customer, phone, gradeA, 60m);
        await Problem(await AcceptRawAsync(pos, open, consent: new { method = "on_screen", name = " " }), HttpStatusCode.BadRequest, "TRD-003");
        await Problem(await AcceptRawAsync(pos, open, consent: new { method = "paper" }), HttpStatusCode.BadRequest, "TRD-003");
        Assert.Equal("open", (await TradeInAsync(pos, open.GetProperty("id").GetGuid())).GetProperty("status").GetString());
        var noId = await OpenAsync(pos, customer, phone, gradeA, 60m, civilId: null);
        await Problem(await AcceptRawAsync(pos, noId), HttpStatusCode.BadRequest, "TRD-007");
        var accepted = (await Ok(await AcceptRawAsync(pos, open, consent: new { method = "paper", reference = "TI-FORM-0042" }))).GetProperty("tradeIn");
        Assert.Equal("paper", accepted.GetProperty("consentMethod").GetString());
        Assert.Equal("•••• 5678", accepted.GetProperty("civilId").GetString());
        var full = await Ok(await owner.PostAsync($"/api/v1/tradeins/{accepted.GetProperty("id").GetGuid()}/civil-id", null));
        Assert.Equal("12345678", full.GetProperty("civilId").GetString());

        // Trade-in credit belongs to that customer, up to what is left; a void gives it back to be used again.
        var aid = accepted.GetProperty("id").GetGuid();
        await Problem(await pos.PostAsJsonAsync($"/api/v1/sales/{Guid.CreateVersion7()}/complete", new
        {
            lines = new[] { UnitLine(phone, stock[1].Id) }, payments = new[] { TradeIn(aid, 60m), Cash(140m) }, customerId = other,
        }), HttpStatusCode.Conflict, "TRD-005");
        await Problem(await pos.PostAsJsonAsync($"/api/v1/sales/{Guid.CreateVersion7()}/complete", new
        {
            lines = new[] { UnitLine(phone, stock[1].Id) }, payments = new[] { TradeIn(aid, 61m), Cash(139m) }, customerId = customer,
        }), HttpStatusCode.Conflict, "TRD-005");
        var sale = await SellAsync(pos, [UnitLine(phone, stock[1].Id)], [TradeIn(aid, 60m), Cash(140m)], customerId: customer);
        Assert.Equal("settled", (await TradeInAsync(pos, aid)).GetProperty("status").GetString());
        await Ok(await pos.PostAsJsonAsync($"/api/v1/sales/{sale.GetProperty("id").GetGuid()}/void", new { id = Guid.CreateVersion7(), reason = "customer changed mind" }));
        var after = await TradeInAsync(pos, aid);
        Assert.Equal("accepted", after.GetProperty("status").GetString());
        Assert.Equal(60m, D(after, "outstanding"));
        // Trade-in clearing always equals what accepted trade-ins still owe.
        Assert.Equal(-await Ti.DbAsync<decimal>("SELECT coalesce(sum(tradein.outstanding(id)), 0) FROM tradein.tradein WHERE status = 'accepted'"), await Dev.GlAsync("2320"));

        // A seller registered for VAT is not a margin-scheme purchase: no self-invoice.
        var business = await CustomerAsync(pos, "Dealer " + Suffix(), vat: "OM1100223344");
        var dealer = await TakeInAsync(pos, business, phone, gradeA, 70m, settle: new { kind = "store_credit" });
        Assert.False(dealer.GetProperty("marginScheme").GetBoolean());
        Assert.Equal(JsonValueKind.Null, dealer.GetProperty("selfInvoiceNumber").ValueKind);

        // A cashier agrees up to the staff limit and the guide (+10%); above it a manager's PIN approves, once.
        var manager = await kit.UserAsync(owner, "mgr", "branch_manager", pin: "1357");
        var cashier = await kit.UserAsync(owner, "csh", "cashier", pin: "2468");
        var term2 = await kit.TerminalAsync(owner);
        using var till = await kit.PosAsync(cashier.Username, cashier.Password, term2);
        await OpenShiftAsync(till, 50m);
        var high = await OpenAsync(till, customer, phone, gradeA, 60m);   // above the staff limit of 50
        Assert.True(high.GetProperty("needsApproval").GetBoolean());
        await Problem(await AcceptRawAsync(till, high), HttpStatusCode.Forbidden, "TRD-002");
        var approval = await Ok(await till.PostAsJsonAsync("/api/v1/approvals/pin", new { action = "TRADEIN.APPROVE", approverUsername = manager.Username, pin = "1357", reason = "good condition" }));
        var ok = (await Ok(await AcceptRawAsync(till, high, approvalId: approval.GetProperty("id").GetGuid()))).GetProperty("tradeIn");
        Assert.Equal(approval.GetProperty("id").GetGuid(), ok.GetProperty("approvalId").GetGuid());
        var again = await OpenAsync(till, customer, phone, gradeA, 60m);
        await Problem(await AcceptRawAsync(till, again, approvalId: approval.GetProperty("id").GetGuid()), HttpStatusCode.BadRequest, "POS-008");
        // Store credit and cash payouts are the manager's too.
        await Problem(await till.PostAsJsonAsync($"/api/v1/tradeins/{ok.GetProperty("id").GetGuid()}/settle", new { kind = "store_credit" }), HttpStatusCode.Forbidden, "AUTH-403");
        await Problem(await till.PostAsJsonAsync($"/api/v1/tradeins/{ok.GetProperty("id").GetGuid()}/settle", new { kind = "cash" }), HttpStatusCode.Forbidden, "AUTH-403");
        await Problem(await till.PostAsync($"/api/v1/tradeins/{ok.GetProperty("id").GetGuid()}/civil-id", null), HttpStatusCode.Forbidden, "AUTH-403");

        // Rejected needs a reason, and a rejected trade-in no longer changes.
        var declined = await OpenAsync(pos, customer, phone, gradeA, 30m);
        var did = declined.GetProperty("id").GetGuid();
        await Problem(await pos.PostAsJsonAsync($"/api/v1/tradeins/{did}/reject", new { reason = "" }), HttpStatusCode.BadRequest, "VAL-001");
        await Ok(await pos.PostAsJsonAsync($"/api/v1/tradeins/{did}/reject", new { reason = "Customer declined the offer" }));
        await Problem(await pos.PutAsJsonAsync($"/api/v1/tradeins/{did}", new { version = declined.GetProperty("version").GetInt32() + 1, agreedValue = 35m }), HttpStatusCode.Conflict, "TRD-001");
        await AssertConsistentAsync(owner);
    }

    [PgFact]
    public async Task Valuation_follows_the_guide_and_deductions_and_the_margin_scheme_follows_its_approval()
    {
        using var owner = await kit.OwnerAsync();
        await EnableAsync(owner);
        var phone = await Dev.PhoneAsync(owner, "Guide phone " + Suffix(), 250m);
        var gradeB = await GradeAsync(owner, "B");
        var term = await kit.TerminalAsync(owner);
        using var pos = await kit.OwnerPosAsync(term);
        await OpenShiftAsync(pos, 100m);
        var customer = await CustomerAsync(pos, "Noor " + Suffix());

        // No guide value: the offer is unknown and percentage deductions cannot be worked out.
        var created = await Ok(await CreateRawAsync(pos, customer, phone, Imei()), HttpStatusCode.Created);
        var ti = created.GetProperty("detail").GetProperty("tradeIn");
        var id = ti.GetProperty("id").GetGuid();
        await Problem(await pos.PutAsJsonAsync($"/api/v1/tradeins/{id}", new { version = ti.GetProperty("version").GetInt32(), gradeId = gradeB, deductions = new[] { "SCREEN" } }),
            HttpStatusCode.BadRequest, "TRD-008");
        await GuideAsync(owner, phone, gradeB, 100m);
        // Screen 30% of 100 and no box 3.000: offer 67.000.
        var valued = (await Ok(await pos.PutAsJsonAsync($"/api/v1/tradeins/{id}", new
        {
            version = ti.GetProperty("version").GetInt32(), gradeId = gradeB, deductions = new[] { "SCREEN", "NO_BOX" }, agreedValue = 67m,
        }))).GetProperty("tradeIn");
        Assert.Equal(100m, D(valued, "guideValue"));
        Assert.Equal(33m, D(valued, "deductionsTotal"));
        Assert.Equal(67m, D(valued, "offeredValue"));
        var full = await Ok(await pos.GetAsync($"/api/v1/tradeins/{id}"));
        Assert.Equal(2, full.GetProperty("issues").GetArrayLength());
        Assert.Contains(full.GetProperty("form").GetProperty("grades").EnumerateArray(), g => g.GetProperty("id").GetGuid() == gradeB && D(g, "guideValue") == 100m);

        // While the margin scheme is not approved, trade-ins are ordinary purchases and resales carry standard VAT.
        await MarginAsync(owner, false);
        var code = await Ok(await owner.GetAsync("/api/v1/tax/margin-scheme"));
        Assert.Equal("inactive", code.GetProperty("taxCodeStatus").GetString());
        var plain = (await Ok(await pos.PutAsJsonAsync($"/api/v1/tradeins/{id}", new
        {
            version = valued.GetProperty("version").GetInt32(), condition = "used", inspection = AllPass(), cloudLockRemoved = true,
        }))).GetProperty("tradeIn");
        var accepted = (await Ok(await AcceptRawAsync(pos, plain, settle: new { kind = "store_credit" }))).GetProperty("tradeIn");
        Assert.False(accepted.GetProperty("marginScheme").GetBoolean());
        await Problem(await owner.PutAsJsonAsync("/api/v1/tax/margin-scheme", new { approved = true, reference = "", approvedOn = (DateOnly?)null, version = code.GetProperty("version").GetInt32() }),
            HttpStatusCode.BadRequest, "VAL-001");
        await MarginAsync(owner, true);
        Assert.Equal("active", (await Ok(await owner.GetAsync("/api/v1/tax/margin-scheme"))).GetProperty("taxCodeStatus").GetString());
        await Ok(await InspectAsync(owner, accepted.GetProperty("deviceUnitId").GetGuid(), AllPass(), "restock", "used", gradeB, price: 90m));
        var sold = await SellAsync(pos, [UnitLine(phone, accepted.GetProperty("deviceUnitId").GetGuid())], [Cash(90m)]);
        Assert.Equal("standard", await Ti.DbAsync<string>($"SELECT tax_scheme FROM sales.sale_line WHERE sale_id = '{sold.GetProperty("id").GetGuid()}'"));

        // The settings and the report.
        var settings = await Ok(await owner.GetAsync("/api/v1/tradein/settings"));
        await Problem(await owner.PutAsJsonAsync("/api/v1/tradein/settings", new
        {
            staffLimit = 80m, overGuidePercent = 10m, requireCivilId = true, declarationEn = "x", declarationAr = " ", version = settings.GetProperty("version").GetInt32(),
        }), HttpStatusCode.BadRequest, "VAL-001");
        var report = await Ok(await owner.GetAsync("/api/v1/tradein/report"));
        Assert.Contains(report.EnumerateArray(), r => r.GetProperty("number").GetString() == accepted.GetProperty("number").GetString()
            && D(r, "agreedValue") == 67m && r.GetProperty("saleNumber").GetString() == sold.GetProperty("number").GetString());
    }
}
