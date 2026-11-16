using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
using static Kite.IntegrationTests.DeviceKit;
using static Kite.IntegrationTests.PosKit;

namespace Kite.IntegrationTests;

/// <summary>
/// Phase 5 against the real server and PostgreSQL: phones received by IMEI with their own cost, inspections that decide
/// what happens to a device, refurbishment costs flowing into that one device and the accounts, selling a unit at its
/// own cost, returns to inspection, voids, damaged devices, reclassification, write-offs and the history page.
/// </summary>
[Collection("server")]
public sealed class Phase5Tests(KiteServerFixture fx)
{
    private readonly PosKit kit = new(fx);
    private DeviceKit Dev => new(fx, kit);

    [PgFact]
    public async Task Phones_come_in_by_imei_each_with_its_own_cost_and_identity()
    {
        using var owner = await kit.OwnerAsync();
        var before = await Dev.GlSnapshotAsync("1400", "1410");
        var phone = await Dev.PhoneAsync(owner, "Galaxy A55 128GB", 160m, 150m);
        var units = await Dev.ReceiveAsync(owner, phone, (120.000m, "brand_new", "in_stock", null, null), (121.500m, "brand_new", "in_stock", null, null),
            (70.000m, "used", "under_inspection", null, null));
        Assert.Equal(2m + 1m, await kit.StockAsync(owner, phone));
        Assert.Equal(120.000m, await CostAsync(owner, units[0].Id));
        Assert.Equal(121.500m, await CostAsync(owner, units[1].Id));
        Assert.Equal(before["1400"] + 241.500m, await Dev.GlAsync("1400"));   // new phones in new-device inventory
        Assert.Equal(before["1410"] + 70.000m, await Dev.GlAsync("1410"));    // a used phone in used-device inventory
        var detail = await DetailAsync(owner, units[2].Id);
        Assert.Equal("under_inspection", detail.GetProperty("unit").GetProperty("status").GetString());
        Assert.Equal(units[2].Imei, detail.GetProperty("unit").GetProperty("imei1").GetString());
        Assert.Equal("received", detail.GetProperty("events")[0].GetProperty("eventType").GetString());

        // Refusals: a wrong check digit, too few IMEIs for the quantity, an IMEI that already belongs to a device. Nothing is saved.
        var bad = units[0].Imei[..14] + (char)('0' + (units[0].Imei[14] - '0' + 1) % 10);
        async Task<HttpResponseMessage> Opening(object[] list, decimal qty) => await owner.PostAsJsonAsync("/api/v1/inventory/adjustments", new
        {
            branchId = kit.Branch, kind = "opening", lines = new[] { new { variantId = phone.VariantId, qty, reasonCode = "opening", units = list } },
        });
        await Problem(await Opening([new { imei1 = bad, unitCost = 100m }], 1), HttpStatusCode.BadRequest, "DEV-001");
        await Problem(await Opening([new { imei1 = Imei(), unitCost = 100m }], 2), HttpStatusCode.BadRequest, "DEV-012");
        await Problem(await Opening([new { imei1 = Imei(), unitCost = 100m }, new { imei1 = units[1].Imei, unitCost = 100m }], 2), HttpStatusCode.Conflict, "DEV-007");
        await Problem(await Opening([new { imei1 = Imei(), unitCost = 100m, condition = "for_parts" }], 1), HttpStatusCode.BadRequest, "VAL-001");
        Assert.Equal(3m, await kit.StockAsync(owner, phone));
        // Devices leave stock by sale or write-off, and are counted by IMEI, not by quantity.
        await Problem(await owner.PostAsJsonAsync("/api/v1/inventory/adjustments", new
        {
            branchId = kit.Branch, kind = "adjustment", lines = new[] { new { variantId = phone.VariantId, qty = -1m, reasonCode = "lost" } },
        }), HttpStatusCode.BadRequest, "VAL-001");
        var count = await Ok(await owner.PostAsJsonAsync("/api/v1/inventory/counts", new { branchId = kit.Branch }), HttpStatusCode.Created);
        await Problem(await owner.PutAsJsonAsync($"/api/v1/inventory/counts/{count.GetProperty("id").GetGuid()}/lines", new[] { new { variantId = phone.VariantId, countedQty = 3m } }),
            HttpStatusCode.BadRequest, "VAL-001");
        await Ok(await owner.PostAsync($"/api/v1/inventory/counts/{count.GetProperty("id").GetGuid()}/cancel", null), HttpStatusCode.NoContent);
        await AssertConsistentAsync(owner);
    }

    [PgFact]
    public async Task Inspection_and_refurbishment_costs_flow_into_the_one_device_and_the_accounts()
    {
        using var owner = await kit.OwnerAsync();
        await EnableRefurbAsync(owner);
        var settings = await owner.GetFromJsonAsync<JsonElement>("/api/v1/devices/settings");
        await Ok(await owner.PutAsJsonAsync("/api/v1/devices/settings", new
        {
            labourRatePerHour = 4.000m, reservationMinutes = 15, parkedReservationMinutes = 240, suggestedPriceStep = 0.500m, version = settings.GetProperty("version").GetInt32(),
        }), HttpStatusCode.NoContent);
        var phone = await Dev.PhoneAsync(owner, "iPhone 13 128GB", 220m);
        var battery = await kit.ProductAsync(owner, "iPhone 13 battery", 15m, type: "spare_part", openingQty: 5, unitCost: 6.500m);
        var unit = (await Dev.ReceiveAsync(owner, phone, (120.000m, "used", "under_inspection", null, null)))[0];
        var gradeA = await GradeAsync(owner, "A");
        var gl = await Dev.GlSnapshotAsync("1410", "1430", "6210", "2110");

        // The checklist: incomplete is refused; deciding against its suggestion needs a reason.
        var answers = AllPass(battery: 71);
        answers.Remove("water");
        await Problem(await InspectAsync(owner, unit.Id, answers, "refurbish", "used"), HttpStatusCode.BadRequest, "DEV-011");
        answers = AllPass(battery: 71);   // battery under 80%: the checklist suggests refurbishment
        await Problem(await InspectAsync(owner, unit.Id, answers, "restock", "used"), HttpStatusCode.BadRequest, "DEV-011");
        await Problem(await InspectAsync(owner, unit.Id, answers, "refurbish", "brand_new"), HttpStatusCode.BadRequest, "VAL-001");
        var inspection = await Ok(await InspectAsync(owner, unit.Id, answers, "refurbish", "used"));
        Assert.Equal("refurbish", inspection.GetProperty("suggestedOutcome").GetString());
        Assert.StartsWith("RF-", inspection.GetProperty("refurbOrderNumber").GetString());
        var order = inspection.GetProperty("refurbOrderId").GetGuid();
        Assert.Equal("refurbishing", await StatusAsync(owner, unit.Id));

        // Costs: a part at its stock cost, 45 minutes of labour at 4.000 an hour, outside work; one mistake taken back.
        Assert.Equal(6.500m, (await AddCostAsync(owner, order, new { kind = "part", variantId = battery.VariantId, qty = 1m })).GetProperty("amount").GetDecimal());
        Assert.Equal(3.000m, (await AddCostAsync(owner, order, new { kind = "labour", minutes = 45 })).GetProperty("amount").GetDecimal());
        var wrong = await AddCostAsync(owner, order, new { kind = "external", vendor = "Screen Lab Ruwi", reference = "SL-1182", amount = 8.250m });
        await Ok(await owner.PostAsJsonAsync($"/api/v1/refurbishments/{order}/costs/{wrong.GetProperty("id").GetGuid()}/reverse", new { reason = "invoice was for another phone" }));
        await Problem(await owner.PostAsJsonAsync($"/api/v1/refurbishments/{order}/costs/{wrong.GetProperty("id").GetGuid()}/reverse", new { reason = "twice" }), HttpStatusCode.Conflict, "GEN-409");
        await AddCostAsync(owner, order, new { kind = "external", vendor = "Screen Lab Ruwi", reference = "SL-1190", amount = 7.750m });
        await Problem(await owner.PostAsJsonAsync($"/api/v1/refurbishments/{order}/costs", new { kind = "part", variantId = battery.VariantId, qty = 9m }), HttpStatusCode.Conflict, "INV-001");

        Assert.Equal(120.000m + 6.500m + 3.000m + 7.750m, await CostAsync(owner, unit.Id));
        Assert.Equal(4m, await kit.StockAsync(owner, battery));
        Assert.Equal(gl["1410"] + 17.250m, await Dev.GlAsync("1410"));   // the device's inventory value grew by its costs
        Assert.Equal(gl["1430"] - 6.500m, await Dev.GlAsync("1430"));    // the part left spare-parts inventory
        Assert.Equal(gl["6210"] - 3.000m, await Dev.GlAsync("6210"));    // labour absorbed (credit)
        Assert.Equal(gl["2110"] - 7.750m, await Dev.GlAsync("2110"));    // outside work received, not yet invoiced (credit)

        await Problem(await owner.PostAsJsonAsync($"/api/v1/refurbishments/{order}/complete", new { condition = "used", gradeId = gradeA }), HttpStatusCode.BadRequest, "VAL-001");
        await CompleteAsync(owner, order, "refurbished", gradeA);
        await Problem(await owner.PostAsJsonAsync($"/api/v1/refurbishments/{order}/costs", new { kind = "labour", minutes = 10 }), HttpStatusCode.Conflict, "DEV-010");
        var d = await DetailAsync(owner, unit.Id);
        Assert.Equal("in_stock", d.GetProperty("unit").GetProperty("status").GetString());
        Assert.Equal("refurbished", d.GetProperty("unit").GetProperty("condition").GetString());
        Assert.Equal("A", d.GetProperty("unit").GetProperty("gradeCode").GetString());
        var cost = d.GetProperty("cost");
        Assert.Equal((120.000m, 6.500m, 3.000m, 7.750m, 137.250m), (cost.GetProperty("acquisition").GetDecimal(), cost.GetProperty("parts").GetDecimal(),
            cost.GetProperty("labour").GetDecimal(), cost.GetProperty("external").GetDecimal(), cost.GetProperty("total").GetDecimal()));
        Assert.Equal(187.000m, d.GetProperty("price").GetProperty("listPrice").GetDecimal());   // grade A: 85% of 220.000, to the 0.500 step
        Assert.Equal(["received", "inspected", "cost_added", "cost_added", "cost_added", "cost_added", "cost_added", "refurb_completed"],
            d.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("eventType").GetString()).ToArray());
        await AssertConsistentAsync(owner);
    }

    [PgFact]
    public async Task A_unit_sells_at_its_own_cost_and_comes_back_to_inspection_never_straight_to_the_shelf()
    {
        using var owner = await kit.OwnerAsync();
        await EnableRefurbAsync(owner);
        await kit.SetPolicyAsync(owner, cashRounding: 0m);
        var phone = await Dev.PhoneAsync(owner, "Pixel 8 128GB", 300m, 280m);
        var gradeB = await GradeAsync(owner, "B");
        var units = await Dev.ReceiveAsync(owner, phone, (250.000m, "brand_new", "in_stock", null, null), (252.000m, "brand_new", "in_stock", null, null),
            (90.000m, "used", "in_stock", 130.000m, gradeB), (60.000m, "damaged", "in_stock", 80.000m, null));
        var (newOne, newTwo, used, damaged) = (units[0], units[1], units[2], units[3]);
        var t1 = await kit.TerminalAsync(owner);
        var t2 = await kit.TerminalAsync(owner);
        var cashier = await kit.UserAsync(owner, "p5cash", "cashier", "2468");
        var manager = await kit.UserAsync(owner, "p5mgr", "branch_manager", "1357");
        using var pos = await kit.PosAsync(cashier.Username, cashier.Password, t1);
        using var pos2 = await kit.OwnerPosAsync(t2);
        await OpenShiftAsync(pos, 50m);
        await OpenShiftAsync(pos2, 0m);

        // The till finds the unit by IMEI, with its own price and condition; a cart holds it for that till only.
        var found = await Ok(await pos.GetAsync($"/api/v1/pos/units/code/{used.Imei}"));
        Assert.Equal((130.000m, "used", "B"), (found.GetProperty("listPrice").GetDecimal(), found.GetProperty("condition").GetString(), found.GetProperty("gradeCode").GetString()));
        Assert.Contains("Used B", found.GetProperty("descriptionEn").GetString());
        var cart = Guid.CreateVersion7();
        await Ok(await pos.PostAsJsonAsync($"/api/v1/pos/units/{used.Id}/reserve", new { cartId = cart }));
        await Problem(await pos2.PostAsJsonAsync($"/api/v1/pos/units/{used.Id}/reserve", new { cartId = Guid.CreateVersion7() }), HttpStatusCode.Conflict, "DEV-005");
        await Problem(await pos2.PostAsJsonAsync($"/api/v1/sales/{Guid.CreateVersion7()}/complete", new { lines = new[] { UnitLine(phone, used.Id) }, payments = new[] { Card(130m) } }),
            HttpStatusCode.Conflict, "DEV-005");
        await Problem(await pos.PostAsJsonAsync($"/api/v1/sales/{Guid.CreateVersion7()}/complete", new { lines = new[] { Line(phone, 1) }, payments = new[] { Card(300m) } }),
            HttpStatusCode.BadRequest, "DEV-002");   // a phone line without its unit

        var gl = await Dev.GlSnapshotAsync("1400", "1410", "4100", "4110", "5100", "5110");
        var sale = await SellAsync(pos, [UnitLine(phone, used.Id), UnitLine(phone, newOne.Id)], [Card(430.000m)], id: cart);
        var saleId = sale.GetProperty("id").GetGuid();
        Assert.Equal(gl["1410"] - 90.000m, await Dev.GlAsync("1410"));
        Assert.Equal(gl["1400"] - 250.000m, await Dev.GlAsync("1400"));
        Assert.Equal(gl["5110"] + 90.000m, await Dev.GlAsync("5110"));    // cost of a used device = that device's own cost
        Assert.Equal(gl["5100"] + 250.000m, await Dev.GlAsync("5100"));
        Assert.Equal(gl["4110"] - 123.810m, await Dev.GlAsync("4110"));   // used-device sales: 130.000 incl. 5% VAT
        Assert.Equal(gl["4100"] - 285.714m, await Dev.GlAsync("4100"));
        Assert.Equal(33.810m, await fx.QueryDbAsync<decimal>($"SELECT profit FROM sales.sale_line WHERE device_unit_id = '{used.Id}'"));
        Assert.Equal("sold", await StatusAsync(owner, used.Id));
        await Problem(await pos.GetAsync($"/api/v1/pos/units/code/{used.Imei}"), HttpStatusCode.Conflict, "DEV-004");
        var receipt = await Ok(await pos.GetAsync($"/api/v1/sales/{saleId}/receipt"));
        Assert.Contains(receipt.GetProperty("lines").EnumerateArray(), l => l.GetProperty("serial").GetString() == "IMEI " + used.Imei);

        // A return must scan the very device; it goes back to inspection at the same cost, not on sale.
        var loaded = await Ok(await pos.GetAsync($"/api/v1/sales/{saleId}"));
        var usedLine = loaded.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("deviceUnitId").GetGuid() == used.Id).GetProperty("id").GetGuid();
        var newLine = loaded.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("deviceUnitId").GetGuid() == newOne.Id).GetProperty("id").GetGuid();
        object Ret(Guid line, string? code, decimal refund) => new { id = Guid.CreateVersion7(), reason = "changed mind", lines = new[] { new { lineId = line, qty = 1m, code } }, refunds = new[] { Card(refund) } };
        await Problem(await pos.PostAsJsonAsync($"/api/v1/sales/{saleId}/return", Ret(usedLine, null, 130m)), HttpStatusCode.BadRequest, "DEV-009");
        await Problem(await pos.PostAsJsonAsync($"/api/v1/sales/{saleId}/return", Ret(usedLine, newOne.Imei, 130m)), HttpStatusCode.BadRequest, "DEV-009");
        await Ok(await pos.PostAsJsonAsync($"/api/v1/sales/{saleId}/return", Ret(usedLine, used.Imei, 130m)));
        Assert.Equal("returned_inspection", await StatusAsync(owner, used.Id));
        Assert.Equal(90.000m, await CostAsync(owner, used.Id));
        Assert.Equal(gl["1410"], await Dev.GlAsync("1410"));
        await Problem(await pos.GetAsync($"/api/v1/pos/units/code/{used.Imei}"), HttpStatusCode.Conflict, "DEV-004");
        await Ok(await InspectAsync(owner, used.Id, AllPass(), "restock", "used", gradeB));
        Assert.Equal("in_stock", await StatusAsync(owner, used.Id));

        // A brand-new phone that comes back opened is restocked as used: its cost moves to used-device inventory.
        await Ok(await pos.PostAsJsonAsync($"/api/v1/sales/{saleId}/return", Ret(newLine, newOne.Imei, 300m)));
        var before = await Dev.GlSnapshotAsync("1400", "1410");
        await Ok(await InspectAsync(owner, newOne.Id, AllPass(), "restock", "used", gradeB, note: "box opened"));
        Assert.Equal(before["1400"] - 250.000m, await Dev.GlAsync("1400"));
        Assert.Equal(before["1410"] + 250.000m, await Dev.GlAsync("1410"));

        // A void puts the unit straight back in stock: it never left the shop.
        var s2 = await SellAsync(pos2, [UnitLine(phone, newTwo.Id)], [Card(300m)]);
        await Ok(await pos2.PostAsJsonAsync($"/api/v1/sales/{s2.GetProperty("id").GetGuid()}/void", new { id = Guid.CreateVersion7(), reason = "wrong phone" }));
        Assert.Equal("in_stock", await StatusAsync(owner, newTwo.Id));

        // A damaged device sells only with a manager's approval; its description tells the customer.
        await Problem(await pos.PostAsJsonAsync($"/api/v1/sales/{Guid.CreateVersion7()}/complete", new { lines = new[] { UnitLine(phone, damaged.Id) }, payments = new[] { Card(80m) } }),
            HttpStatusCode.Forbidden, "DEV-008");
        var approval = await Ok(await pos.PostAsJsonAsync("/api/v1/approvals/pin", new { action = "DEVICE.SELL_DAMAGED", approverUsername = manager.Username, pin = "1357" }));
        var s3 = await SellAsync(pos, [UnitLine(phone, damaged.Id, damagedApproval: approval.GetProperty("id").GetGuid())], [Card(80m)]);
        var s3Lines = await Ok(await pos.GetAsync($"/api/v1/sales/{s3.GetProperty("id").GetGuid()}"));
        Assert.Contains("Damaged", s3Lines.GetProperty("lines")[0].GetProperty("descriptionEn").GetString());

        // A write-off takes the unit out at its cost, to stock write-offs.
        var w = await Dev.GlSnapshotAsync("6700", "1410");
        await Ok(await owner.PostAsJsonAsync($"/api/v1/devices/{newOne.Id}/write-off", new { reason = "screen smashed in storage" }));
        Assert.Equal(w["6700"] + 250.000m, await Dev.GlAsync("6700"));
        Assert.Equal(w["1410"] - 250.000m, await Dev.GlAsync("1410"));
        await Problem(await owner.PostAsJsonAsync($"/api/v1/devices/{newOne.Id}/write-off", new { reason = "again" }), HttpStatusCode.Conflict, "DEV-006");

        // The history page: every step, with the profit of each sale.
        var history = await DetailAsync(owner, used.Id);
        Assert.Equal(["received", "sold", "returned", "inspected"], history.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("eventType").GetString()).ToArray());
        Assert.Equal(33.810m, history.GetProperty("sales")[0].GetProperty("profit").GetDecimal());
        await AssertConsistentAsync(owner);
    }

    [PgFact]
    public async Task Two_tills_racing_for_the_same_phone_sell_it_exactly_once()
    {
        using var owner = await kit.OwnerAsync();
        await kit.SetPolicyAsync(owner, cashRounding: 0m);
        var phone = await Dev.PhoneAsync(owner, "Redmi Note 13", 90m);
        var unit = (await Dev.ReceiveAsync(owner, phone, (70.000m, "brand_new", "in_stock", null, null)))[0];
        var tills = new List<HttpClient>();
        for (var i = 0; i < 6; i++)
        {
            var term = await kit.TerminalAsync(owner);
            var c = await kit.OwnerPosAsync(term);
            await OpenShiftAsync(c, 0m);
            tills.Add(c);
        }
        var results = await Task.WhenAll(tills.Select(c => c.PostAsJsonAsync($"/api/v1/sales/{Guid.CreateVersion7()}/complete",
            new { lines = new[] { UnitLine(phone, unit.Id) }, payments = new[] { Card(90m) } })));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Equal(1, await fx.QueryDbAsync<int>($"SELECT count(*)::int FROM sales.sale_line WHERE device_unit_id = '{unit.Id}'"));
        foreach (var c in tills) c.Dispose();
        await AssertConsistentAsync(owner);
    }
}
