using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
using static Kite.IntegrationTests.DeviceKit;
using static Kite.IntegrationTests.PosKit;

namespace Kite.IntegrationTests;

/// <summary>
/// Phase 6 against the real server and PostgreSQL, the server acting as head office and the tests acting as branch servers
/// over mutual TLS: the verification points of 11.5 (receiving, sale, look-up), the central registry with its conflicts and
/// their resolution (correcting a mistyped IMEI, reversing a receipt), the blocked list reaching branch servers, counting
/// devices by IMEI, and emergency sales of phones settled by a manager.
/// </summary>
[Collection("server")]
public sealed class Phase6Tests(KiteServerFixture fx)
{
    private readonly PosKit kit = new(fx);
    private DeviceKit Dev => new(fx, kit);
    private CentralKit Central => new(fx);

    private async Task<Guid[]> ReceiveImeisAsync(HttpClient owner, Item phone, decimal cost, string entry, params string[] imeis)
    {
        var r = await owner.PostAsJsonAsync("/api/v1/inventory/adjustments", new
        {
            branchId = kit.Branch, kind = "opening", note = "phones",
            lines = new[] { new { variantId = phone.VariantId, qty = (decimal)imeis.Length, reasonCode = "opening", units = imeis.Select(i => new { imei1 = i, unitCost = cost, entry }).ToArray() } },
        });
        await Ok(r, HttpStatusCode.Created);
        var ids = new List<Guid>();
        foreach (var i in imeis) ids.Add((await Ok(await owner.GetAsync($"/api/v1/devices/lookup/{i}"))).GetProperty("id").GetGuid());
        return ids.ToArray();
    }

    private static async Task<HttpResponseMessage> ReceiveRawAsync(HttpClient owner, Guid branch, Item phone, params string[] imeis) =>
        await owner.PostAsJsonAsync("/api/v1/inventory/adjustments", new
        {
            branchId = branch, kind = "opening",
            lines = new[] { new { variantId = phone.VariantId, qty = (decimal)imeis.Length, reasonCode = "opening", units = imeis.Select(i => new { imei1 = i, unitCost = 50m }).ToArray() } },
        });

    private static async Task<JsonElement> HoldsAsync(HttpClient owner, Guid unit) => (await DetailAsync(owner, unit)).GetProperty("holds");

    [PgFact]
    public async Task Receiving_checks_the_blocked_list_the_serial_format_and_who_holds_the_IMEI_anywhere()
    {
        using var owner = await kit.OwnerAsync();
        var phone = await Dev.PhoneAsync(owner, "iPhone 15 128GB", 360m);
        var (blocked, held, typed, fresh) = (Imei(), Imei(), Imei(), Imei());

        // The blocked list refuses the device at the door.
        await Ok(await owner.PostAsJsonAsync("/api/v1/devices/blocked", new { value = blocked, reason = "Reported stolen", reference = "ROP 22/118" }), HttpStatusCode.Created);
        await Problem(await ReceiveRawAsync(owner, kit.Branch, phone, blocked), HttpStatusCode.Conflict, "DEV-014");

        // An IMEI a branch server already registered is refused here, naming that branch.
        var b = await Central.RegisterBranchAsync(owner);
        await Central.RegisterAsync(b, CentralKit.Unit(Guid.CreateVersion7(), "in_stock", held));
        var refusal = await ReceiveRawAsync(owner, kit.Branch, phone, held);
        await Problem(refusal, HttpStatusCode.Conflict, "DEV-007");
        Assert.Contains(b.Code, (await Json(await ReceiveRawAsync(owner, kit.Branch, phone, held))).GetProperty("title").GetString());

        // Typing instead of scanning is logged; the same IMEI twice in the business is refused with where it is.
        var typedUnit = (await ReceiveImeisAsync(owner, phone, 300m, "manual", typed))[0];
        Assert.Equal(1L, await fx.QueryDbAsync<long>($"SELECT count(*) FROM audit.audit_log WHERE action = 'device.identifier_typed' AND entity_id = '{typedUnit}'"));
        var again = await ReceiveRawAsync(owner, kit.Branch, phone, typed);
        await Problem(again, HttpStatusCode.Conflict, "DEV-007");
        Assert.Contains("in stock at B01", (await Json(await ReceiveRawAsync(owner, kit.Branch, phone, typed))).GetProperty("title").GetString());

        // Serial numbers follow their brand's format.
        var brand = await Ok(await owner.PostAsJsonAsync("/api/v1/catalog/brands", new { nameEn = "Lenovo " + Suffix(), nameAr = "لينوفو" }), HttpStatusCode.Created);
        var brandId = brand.GetProperty("id").GetGuid();
        await Problem(await owner.PutAsJsonAsync($"/api/v1/devices/serial-formats/{brandId}", new { pattern = "^[A-Z" }), HttpStatusCode.BadRequest, "VAL-001");
        await Ok(await owner.PutAsJsonAsync($"/api/v1/devices/serial-formats/{brandId}", new { pattern = "^[A-Z]{2}[0-9]{6}$" }), HttpStatusCode.NoContent);
        var tablet = await Ok(await owner.PostAsJsonAsync("/api/v1/catalog/products", new
        {
            nameEn = "Tab M10", nameAr = "تاب", productType = "device", tracking = "serial", brandId, taxCodeId = await kit.TaxCodeAsync(owner), defaultWarrantyDays = 365,
            variants = new[] { new { sku = "TAB-" + Suffix(), barcodes = new[] { "629" + Random.Shared.Next(100000000, 999999999) }, price = 120m } },
        }), HttpStatusCode.Created);
        var tabletVariant = tablet.GetProperty("variants")[0].GetProperty("variant").GetProperty("id").GetGuid();
        object Serial(string s) => new
        {
            branchId = kit.Branch, kind = "opening",
            lines = new[] { new { variantId = tabletVariant, qty = 1m, reasonCode = "opening", units = new[] { new { serial = s, unitCost = 80m } } } },
        };
        await Problem(await owner.PostAsJsonAsync("/api/v1/inventory/adjustments", Serial("HX12345")), HttpStatusCode.BadRequest, "DEV-017");
        await Ok(await owner.PostAsJsonAsync("/api/v1/inventory/adjustments", Serial("HX" + Random.Shared.Next(100000, 999999))), HttpStatusCode.Created);

        // While scanning: every problem before the document is posted, and the model the IMEI suggests.
        await Ok(await owner.PostAsJsonAsync("/api/v1/devices/tac/import", new { csv = $"tac,brand,model\n{fresh[..8]},Samsung,Galaxy S24\nnot,a,row" }));
        var check = await Ok(await owner.PostAsJsonAsync("/api/v1/devices/identifiers/check", new
        {
            variantId = phone.VariantId, codes = new[] { blocked[..14] + (char)('0' + (blocked[14] - '0' + 1) % 10), blocked, held, typed, fresh },
        }));
        var problems = check.EnumerateArray().Select(c => c.GetProperty("problem").ValueKind == JsonValueKind.Null ? null : c.GetProperty("problem").GetProperty("code").GetString()).ToArray();
        Assert.Equal(["DEV-001", "DEV-014", "DEV-007", "DEV-007", null], problems);
        Assert.Contains("Samsung Galaxy S24, not iPhone 15", check[4].GetProperty("hint").GetString());
        Assert.True(check[4].GetProperty("hintMismatch").GetBoolean());

        // The business-wide look-up (for trade-ins and repairs): the branch server's device and the blocked list.
        var look = await Ok(await owner.GetAsync($"/api/v1/devices/history-lookup/{held}"));
        Assert.Contains(look.GetProperty("headOffice").GetProperty("holders").EnumerateArray(), h => h.GetProperty("branchCode").GetString() == b.Code);
        Assert.Equal("Reported stolen (ROP 22/118)", (await Ok(await owner.GetAsync($"/api/v1/devices/history-lookup/{blocked}"))).GetProperty("blocked").GetString());
        await Central.DeactivateAsync(owner, b);
        await AssertConsistentAsync(owner);
    }

    [PgFact]
    public async Task A_duplicate_found_at_head_office_holds_both_devices_and_correcting_the_mistyped_IMEI_releases_them()
    {
        using var owner = await kit.OwnerAsync();
        var phone = await Dev.PhoneAsync(owner, "Galaxy S23 256GB", 280m);
        var (shared, right) = (Imei(), Imei());
        var mine = (await ReceiveImeisAsync(owner, phone, 210m, "scan", shared))[0];
        var b = await Central.RegisterBranchAsync(owner);

        // The branch received the same IMEI while offline: both devices go on hold, and the branch is told so.
        var theirs = Guid.CreateVersion7();
        var reg = await Central.RegisterAsync(b, CentralKit.Unit(theirs, "in_stock", shared));
        Assert.Equal("conflict", CentralKit.State(reg, theirs));
        Assert.Equal("B01", reg.GetProperty("results")[0].GetProperty("conflicts")[0].GetProperty("others")[0].GetProperty("branchCode").GetString());
        Assert.Equal("quarantined", await StatusAsync(owner, mine));
        Assert.Contains((await HoldsAsync(owner, mine)).EnumerateArray(), h => h.GetProperty("reason").GetString() == "imei_conflict");
        var hb = await Central.HeartbeatAsync(b);
        var hold = hb.GetProperty("imeiInstructions").EnumerateArray().Single(i => i.GetProperty("unitId").GetGuid() == theirs);
        Assert.Equal("hold", hold.GetProperty("kind").GetString());
        var seq = hold.GetProperty("seq").GetInt64();
        var conflicts = await Ok(await owner.GetAsync("/api/v1/devices/conflicts?status=open"));
        var conflict = conflicts.EnumerateArray().Single(c => c.GetProperty("value").GetString() == shared);
        Assert.Equal(2, conflict.GetProperty("sides").GetArrayLength());
        // A third receipt anywhere is still refused while the conflict is open.
        await Problem(await ReceiveRawAsync(owner, kit.Branch, phone, shared), HttpStatusCode.Conflict, "DEV-007");
        Assert.Equal("held", (await Central.CheckAsync(b, shared))[0].GetProperty("state").GetString());

        // A manager cannot simply release it; the identifier is corrected instead (a typing error at head office).
        await Problem(await owner.PostAsJsonAsync($"/api/v1/devices/{mine}/release", new { reason = "looks fine" }), HttpStatusCode.Conflict, "DEV-018");
        await Problem(await owner.PostAsJsonAsync($"/api/v1/devices/{mine}/identifiers", new { imei1 = shared, reason = "same" }), HttpStatusCode.BadRequest, "VAL-001");
        await Ok(await owner.PostAsJsonAsync($"/api/v1/devices/{mine}/identifiers", new { imei1 = right, reason = "last digits swapped when typed" }));
        Assert.Equal("in_stock", await StatusAsync(owner, mine));
        var detail = await DetailAsync(owner, mine);
        Assert.Equal(right, detail.GetProperty("unit").GetProperty("imei1").GetString());
        Assert.Contains(detail.GetProperty("identifiers").EnumerateArray(), i => i.GetProperty("value").GetString() == shared && i.GetProperty("status").GetString() == "retired");
        Assert.Equal("identifier_corrected", detail.GetProperty("events").EnumerateArray().Last(e => e.GetProperty("eventType").GetString() != "released").GetProperty("eventType").GetString());
        var release = (await Central.HeartbeatAsync(b, seq)).GetProperty("imeiInstructions").EnumerateArray().Single(i => i.GetProperty("unitId").GetGuid() == theirs);
        Assert.Equal("release", release.GetProperty("kind").GetString());
        Assert.Empty((await Central.HeartbeatAsync(b, release.GetProperty("seq").GetInt64())).GetProperty("imeiInstructions").EnumerateArray());
        Assert.Equal("resolved", (await Ok(await owner.GetAsync("/api/v1/devices/conflicts"))).EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == conflict.GetProperty("id").GetGuid())
            .GetProperty("status").GetString());
        // The old IMEI now belongs to the branch's device; pushing the same state again changes nothing.
        Assert.Equal("verified", CentralKit.State(await Central.RegisterAsync(b, CentralKit.Unit(theirs, "in_stock", shared)), theirs));
        Assert.Equal(1L, await fx.QueryDbAsync<long>($"SELECT count(*) FROM device.central_registry WHERE value = '{shared}' AND status = 'active'"));
        await Central.DeactivateAsync(owner, b);
        await AssertConsistentAsync(owner);
    }

    [PgFact]
    public async Task A_receipt_made_in_error_is_reversed_at_cost_and_a_branch_reversal_releases_head_office_too()
    {
        using var owner = await kit.OwnerAsync();
        var phone = await Dev.PhoneAsync(owner, "Pixel 9 128GB", 310m);
        var (v1, v2) = (Imei(), Imei());
        var units = await ReceiveImeisAsync(owner, phone, 240m, "scan", v1, v2);
        var b = await Central.RegisterBranchAsync(owner);
        var (t1, t2) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await Central.RegisterAsync(b, CentralKit.Unit(t1, "in_stock", v1), CentralKit.Unit(t2, "in_stock", v2));
        Assert.Equal("quarantined", await StatusAsync(owner, units[0]));

        // Head office's device was received in error: its receipt is reversed at its cost, against opening equity.
        var gl = await Dev.GlSnapshotAsync("1400", "3900");
        await Problem(await owner.PostAsJsonAsync($"/api/v1/devices/{units[0]}/reverse-receipt", new { reason = " " }), HttpStatusCode.BadRequest, "VAL-001");
        var rev = await Ok(await owner.PostAsJsonAsync($"/api/v1/devices/{units[0]}/reverse-receipt", new { reason = "Counted twice: the phone is at the branch" }));
        Assert.Equal(240.000m, rev.GetProperty("cost").GetDecimal());
        Assert.Equal("reversed", await StatusAsync(owner, units[0]));
        Assert.Equal(gl["1400"] - 240.000m, await Dev.GlAsync("1400"));
        Assert.Equal(gl["3900"] + 240.000m, await Dev.GlAsync("3900"));      // equity debited back
        Assert.Equal(1m, await kit.StockAsync(owner, phone));
        Assert.Equal("verified", CentralKit.State(await Central.RegisterAsync(b, CentralKit.Unit(t1, "in_stock", v1)), t1));

        // The branch reverses its own receipt of the second IMEI: head office's device is released.
        Assert.Equal("quarantined", await StatusAsync(owner, units[1]));
        Assert.Equal("verified", CentralKit.State(await Central.RegisterAsync(b, CentralKit.Unit(t2, "reversed")), t2));
        Assert.Equal("in_stock", await StatusAsync(owner, units[1]));
        // Reversing is only for a device in a conflict.
        await Problem(await owner.PostAsJsonAsync($"/api/v1/devices/{units[1]}/reverse-receipt", new { reason = "no" }), HttpStatusCode.Conflict, "DEV-018");
        await Central.DeactivateAsync(owner, b);
        await AssertConsistentAsync(owner);
    }

    [PgFact]
    public async Task The_blocked_list_holds_devices_in_stock_and_reaches_branch_servers_with_the_heartbeat()
    {
        using var owner = await kit.OwnerAsync();
        var phone = await Dev.PhoneAsync(owner, "Redmi 13C", 45m);
        var x = Imei();
        var unit = (await ReceiveImeisAsync(owner, phone, 30m, "scan", x))[0];
        var b = await Central.RegisterBranchAsync(owner);
        var start = (await Central.HeartbeatAsync(b)).GetProperty("blocked").EnumerateArray().Select(e => e.GetProperty("seq").GetInt64()).DefaultIfEmpty(0).Max();

        var entry = await Ok(await owner.PostAsJsonAsync("/api/v1/devices/blocked", new { value = x, reason = "Owner reported it lost" }), HttpStatusCode.Created);
        Assert.Equal("quarantined", await StatusAsync(owner, unit));
        await Problem(await owner.PostAsJsonAsync($"/api/v1/devices/{unit}/release", new { reason = "?" }), HttpStatusCode.Conflict, "DEV-018");
        var sent = (await Central.HeartbeatAsync(b, 0, start)).GetProperty("blocked").EnumerateArray().Single(e => e.GetProperty("value").GetString() == x);
        Assert.Equal("active", sent.GetProperty("status").GetString());
        Assert.Equal("blocked", (await Central.CheckAsync(b, x))[0].GetProperty("state").GetString());

        await Ok(await owner.PostAsJsonAsync($"/api/v1/devices/blocked/{entry.GetProperty("id").GetGuid()}/remove", new { reason = "Owner found the receipt" }), HttpStatusCode.NoContent);
        Assert.Equal("in_stock", await StatusAsync(owner, unit));
        var removed = (await Central.HeartbeatAsync(b, 0, sent.GetProperty("seq").GetInt64())).GetProperty("blocked").EnumerateArray().Single(e => e.GetProperty("value").GetString() == x);
        Assert.Equal("removed", removed.GetProperty("status").GetString());
        await Central.DeactivateAsync(owner, b);
    }

    [PgFact]
    public async Task At_the_till_the_device_must_match_the_model_chosen_and_the_strict_setting_keeps_unconfirmed_devices_off_sale()
    {
        using var owner = await kit.OwnerAsync();
        var a = await Dev.PhoneAsync(owner, "Nokia G42", 70m);
        var c = await Dev.PhoneAsync(owner, "Nokia C32", 50m);
        var imei = Imei();
        var unit = (await ReceiveImeisAsync(owner, c, 35m, "scan", imei))[0];
        var term = await kit.TerminalAsync(owner);
        using var pos = await kit.OwnerPosAsync(term);
        var wrong = await pos.GetAsync($"/api/v1/pos/units/code/{imei}?variantId={a.VariantId}");
        await Problem(wrong, HttpStatusCode.Conflict, "DEV-016");
        Assert.Contains("not the Nokia G42", (await Json(await pos.GetAsync($"/api/v1/pos/units/code/{imei}?variantId={a.VariantId}"))).GetProperty("title").GetString());
        await Ok(await pos.GetAsync($"/api/v1/pos/units/code/{imei}?variantId={c.VariantId}"));

        // A device received while head office was away waits for its check; with the strict setting it is not sold until then.
        await fx.QueryDbAsync<int>($"UPDATE device.central_check SET state = 'pending' WHERE device_unit_id = '{unit}' RETURNING 1");
        var settings = await Ok(await owner.GetAsync("/api/v1/devices/settings"));
        object Settings(bool strict, int version) => new
        {
            labourRatePerHour = settings.GetProperty("labourRatePerHour").GetDecimal(), reservationMinutes = settings.GetProperty("reservationMinutes").GetInt32(),
            parkedReservationMinutes = settings.GetProperty("parkedReservationMinutes").GetInt32(), suggestedPriceStep = settings.GetProperty("suggestedPriceStep").GetDecimal(),
            version, strictCentralCheck = strict,
        };
        await Ok(await pos.GetAsync($"/api/v1/pos/units/code/{imei}"));     // not strict: sellable
        await Ok(await owner.PutAsJsonAsync("/api/v1/devices/settings", Settings(true, settings.GetProperty("version").GetInt32())), HttpStatusCode.NoContent);
        try
        {
            await Problem(await pos.GetAsync($"/api/v1/pos/units/code/{imei}"), HttpStatusCode.Conflict, "DEV-015");
            var listed = await Ok(await pos.GetAsync($"/api/v1/pos/units?variantId={c.VariantId}"));
            Assert.Equal("pending", listed.EnumerateArray().Single(u => u.GetProperty("unitId").GetGuid() == unit).GetProperty("centralState").GetString());
        }
        finally
        {
            var now = await Ok(await owner.GetAsync("/api/v1/devices/settings"));
            await Ok(await owner.PutAsJsonAsync("/api/v1/devices/settings", Settings(false, now.GetProperty("version").GetInt32())), HttpStatusCode.NoContent);
            await fx.QueryDbAsync<int>($"UPDATE device.central_check SET state = 'verified' WHERE device_unit_id = '{unit}' RETURNING 1");
        }
    }

    [PgFact]
    public async Task Counting_devices_by_IMEI_finds_the_missing_and_the_extra_and_writes_off_the_missing()
    {
        using var owner = await kit.OwnerAsync();
        await kit.SetPolicyAsync(owner);
        var phone = await Dev.PhoneAsync(owner, "Moto G54", 80m);
        var ims = new[] { Imei(), Imei(), Imei(), Imei() };
        var units = await ReceiveImeisAsync(owner, phone, 55m, "scan", ims);
        var term = await kit.TerminalAsync(owner);
        using var pos = await kit.OwnerPosAsync(term);
        await OpenShiftAsync(pos, 10m);
        await SellAsync(pos, [UnitLine(phone, units[3])], [Card(80m)]);

        var count = await Ok(await owner.PostAsJsonAsync("/api/v1/devices/counts", new { branchId = kit.Branch, variantId = phone.VariantId }), HttpStatusCode.Created);
        var id = count.GetProperty("id").GetGuid();
        await Problem(await owner.PostAsJsonAsync("/api/v1/devices/counts", new { branchId = kit.Branch, variantId = phone.VariantId }), HttpStatusCode.Conflict, "DEV-019");
        async Task<string> ScanAsync(string code) => (await Ok(await owner.PostAsJsonAsync($"/api/v1/devices/counts/{id}/scans", new { code }))).GetProperty("result").GetString()!;
        Assert.Equal("here", await ScanAsync(ims[0]));
        Assert.Equal("here", await ScanAsync(ims[1]));
        Assert.Equal("not_in_stock", await ScanAsync(ims[3]));   // sold, yet on the shelf
        Assert.Equal("unknown", await ScanAsync(Imei()));
        await Problem(await owner.PostAsJsonAsync($"/api/v1/devices/counts/{id}/scans", new { code = ims[0] }), HttpStatusCode.Conflict, "DEV-019");
        var detail = await Ok(await owner.GetAsync($"/api/v1/devices/counts/{id}"));
        Assert.Equal(2, detail.GetProperty("found").GetInt32());
        Assert.Equal([units[2]], detail.GetProperty("missing").EnumerateArray().Select(m => m.GetProperty("id").GetGuid()).ToArray());
        Assert.Equal(2, detail.GetProperty("extra").GetArrayLength());

        var gl = await Dev.GlSnapshotAsync("6700");
        await Problem(await owner.PostAsJsonAsync($"/api/v1/devices/counts/{id}/post", new { writeOffMissing = true }), HttpStatusCode.BadRequest, "VAL-001");
        var posted = await Ok(await owner.PostAsJsonAsync($"/api/v1/devices/counts/{id}/post", new { writeOffMissing = true, reason = "Not found on the shelves" }));
        Assert.Equal(1, posted.GetProperty("writtenOff").GetArrayLength());
        Assert.Equal("written_off", await StatusAsync(owner, units[2]));
        Assert.Equal(gl["6700"] + 55.000m, await Dev.GlAsync("6700"));
        await Problem(await owner.PostAsJsonAsync($"/api/v1/devices/counts/{id}/scans", new { code = ims[1] }), HttpStatusCode.Conflict, "DEV-019");   // posted: closed
        await AssertConsistentAsync(owner);
    }

    [PgFact]
    public async Task Emergency_mode_sells_phones_from_the_snapshot_and_a_phone_sold_meanwhile_is_settled_by_a_manager()
    {
        using var owner = await kit.OwnerAsync();
        await kit.SetPolicyAsync(owner);
        var phone = await Dev.PhoneAsync(owner, "Galaxy A35 128GB", 105m);
        var units = await Dev.ReceiveAsync(owner, phone, (75.000m, "brand_new", "in_stock", null, null), (76.000m, "brand_new", "in_stock", null, null),
            (77.000m, "brand_new", "in_stock", null, null));
        var term = await kit.TerminalAsync(owner, kind: "windows", emergency: true);
        var cashier = await kit.UserAsync(owner, "p6em", "cashier", pin: "8642");
        using var pos = await kit.PosAsync(cashier.Username, cashier.Password, term);
        var snap = await Ok(await pos.GetAsync("/api/v1/pos/emergency/snapshot"));
        Assert.Contains(snap.GetProperty("items").EnumerateArray(), i => i.GetProperty("variantId").GetGuid() == phone.VariantId);
        var snapUnits = snap.GetProperty("units").EnumerateArray().Where(u => u.GetProperty("variantId").GetGuid() == phone.VariantId).ToArray();
        Assert.Equal(3, snapUnits.Length);
        Assert.Equal(105.000m, snapUnits[0].GetProperty("listPrice").GetDecimal());
        var prefix = snap.GetProperty("numberPrefix").GetString()!;

        // While the till is offline, another till sells the second phone online.
        var online = await kit.TerminalAsync(owner);
        using var pos2 = await kit.OwnerPosAsync(online);
        await OpenShiftAsync(pos2, 10m);
        await SellAsync(pos2, [UnitLine(phone, units[1].Id)], [Card(105m)]);

        var shiftId = Guid.CreateVersion7();
        var taxCode = await kit.TaxCodeAsync(owner);
        var at = DateTimeOffset.UtcNow.AddMinutes(-30);
        object Line(Guid? unit) => new
        {
            variantId = phone.VariantId, qty = 1m, unitPrice = 105.000m, listPrice = 105.000m, discount = 0m, net = 100.000m, tax = 5.000m, gross = 105.000m, taxRate = 5m,
            taxCodeId = taxCode, productType = "device", descriptionEn = "Galaxy A35 128GB", descriptionAr = "جالاكسي", warrantyDays = 365, deviceUnitId = unit,
        };
        object Rec(long seq, string kind, object payload) => new { id = Guid.CreateVersion7(), seq, kind, recordedAt = at.AddMinutes(seq), payload };
        var conflictSale = Guid.CreateVersion7();
        var records = new[]
        {
            Rec(1, "shift_open", new { shiftId, number = $"EMS-{term.Code}-000001", cashierId = cashier.Id, openingFloat = 10.000m }),
            Rec(2, "sale", new { saleId = Guid.CreateVersion7(), shiftId, cashierId = cashier.Id, number = prefix + "000001", lines = new[] { Line(units[0].Id) },
                                 payments = new[] { new { method = "cash", amount = 105.000m } }, cashRounding = 0m }),
            Rec(3, "sale", new { saleId = Guid.CreateVersion7(), shiftId, cashierId = cashier.Id, number = prefix + "000002", lines = new[] { Line(null) },
                                 payments = new[] { new { method = "card", amount = 105.000m } }, cashRounding = 0m }),
            Rec(4, "sale", new { saleId = conflictSale, shiftId, cashierId = cashier.Id, number = prefix + "000003", lines = new[] { Line(units[1].Id) },
                                 payments = new[] { new { method = "cash", amount = 105.000m } }, cashRounding = 0m }),
            Rec(5, "shift_close", new { shiftId, closedBy = cashier.Id, countedCash = 220.000m }),   // 10 float + two cash sales in the drawer
        };
        var gl = await Dev.GlSnapshotAsync("6400", "1100");
        var up = await Ok(await pos.PostAsJsonAsync("/api/v1/pos/emergency/records", new { records }));
        var results = up.GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal(["applied", "applied", "conflict", "conflict", "applied"], results.Select(r => r.GetProperty("result").GetString()).ToArray());
        Assert.Equal("sold", await StatusAsync(owner, units[0].Id));
        Assert.Contains("IMEI", results[2].GetProperty("detail").GetString());
        Assert.Contains("kept for a manager", results[3].GetProperty("detail").GetString());
        Assert.Equal(gl["6400"] - 105.000m, await Dev.GlAsync("6400"));   // the drawer was 105 over at close

        // The manager settles it with the phone the customer actually took: the third one.
        var list = await Ok(await owner.GetAsync("/api/v1/pos/emergency/records?result=conflict&devices=true"));
        var rec = list.EnumerateArray().Single(r => r.GetProperty("number").GetString() == prefix + "000003");
        await Problem(await owner.PostAsJsonAsync($"/api/v1/pos/emergency/records/{rec.GetProperty("id").GetGuid()}/settle", new { action = "apply" }),
            HttpStatusCode.Conflict, "DEV-004");                                                       // still the sold phone: no
        var settled = await Ok(await owner.PostAsJsonAsync($"/api/v1/pos/emergency/records/{rec.GetProperty("id").GetGuid()}/settle",
            new { action = "apply", substitutions = new[] { new { line = 0, deviceUnitId = units[2].Id } }, note = "Customer took the third phone" }));
        Assert.Equal(conflictSale, settled.GetProperty("saleId").GetGuid());
        Assert.Equal("sold", await StatusAsync(owner, units[2].Id));
        Assert.Equal(gl["6400"], await Dev.GlAsync("6400"));            // the overage is this sale: taken back
        Assert.Equal(gl["1100"] + 210.000m, await Dev.GlAsync("1100"));  // both cash sales, once each
        await Problem(await owner.PostAsJsonAsync($"/api/v1/pos/emergency/records/{rec.GetProperty("id").GetGuid()}/settle", new { action = "dismiss", note = "x" }),
            HttpStatusCode.Conflict, "GEN-409");
        var all = await Ok(await owner.GetAsync("/api/v1/pos/emergency/records?result=conflict"));
        Assert.DoesNotContain(list.EnumerateArray(), r => r.GetProperty("number").GetString() == prefix + "000002");   // no phone on it: not a device conflict
        var noUnit = all.EnumerateArray().Single(r => r.GetProperty("number").GetString() == prefix + "000002");
        await Ok(await owner.PostAsJsonAsync($"/api/v1/pos/emergency/records/{noUnit.GetProperty("id").GetGuid()}/settle", new { action = "dismiss", note = "Card payment reversed at the bank" }));
        await AssertConsistentAsync(owner);
    }
}
