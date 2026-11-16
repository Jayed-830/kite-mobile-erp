using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Kite.Sales;
using Xunit;
using static Kite.IntegrationTests.PosKit;

namespace Kite.IntegrationTests;

/// <summary>
/// Phase 4 against the real server and PostgreSQL: catalog and prices, opening stock, adjustments and counts, shifts,
/// sales with approvals, returns, voids, cash events, the blind close, receipts and the A4 PDF, the print relay,
/// device keys, parked carts and emergency uploads, each ending with the consistency check.
/// </summary>
[Collection("server")]
public sealed class Phase4Tests(KiteServerFixture fx)
{
    private readonly PosKit kit = new(fx);

    private static async Task AssertConsistentAsync(HttpClient owner)
    {
        var check = await owner.GetFromJsonAsync<JsonElement>("/api/v1/inventory/check");
        Assert.True(check.GetProperty("ok").GetBoolean(), "consistency check failed: " + check.GetRawText());
    }

    [PgFact]
    public async Task Catalog_prices_barcodes_and_the_pos_lookup()
    {
        using var owner = await kit.OwnerAsync();
        var item = await kit.ProductAsync(owner, "Screen guard", 3.500m, minPrice: 3.000m, openingQty: 10, unitCost: 1.200m);

        // Duplicate barcode and SKU are refused with clear codes.
        var dup = await owner.PostAsJsonAsync($"/api/v1/catalog/variants/{item.VariantId}/barcodes", new { code = item.Barcode });
        await Problem(dup, HttpStatusCode.Conflict, "GEN-409");

        // A new price from tomorrow closes today's price the day before; today's price still applies today.
        var tomorrow = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(4).AddDays(1));
        await Ok(await owner.PostAsJsonAsync($"/api/v1/catalog/variants/{item.VariantId}/prices", new { price = 3.900m, minPrice = 3.200m, effectiveFrom = tomorrow }), HttpStatusCode.NoContent);
        var prices = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/catalog/variants/{item.VariantId}/prices");
        Assert.Equal(2, prices.GetArrayLength());
        Assert.Equal(tomorrow.ToString("yyyy-MM-dd"), prices[1].GetProperty("effectiveTo").GetString()![..10]);

        var byBarcode = await Ok(await owner.GetAsync($"/api/v1/pos/items/barcode/{item.Barcode}?branchId={kit.Branch}"));
        Assert.Equal(item.VariantId, byBarcode.GetProperty("variantId").GetGuid());
        Assert.Equal(3.500m, byBarcode.GetProperty("listPrice").GetDecimal());
        Assert.Equal(10m, byBarcode.GetProperty("stock").GetDecimal());
        Assert.Equal(5.0m, byBarcode.GetProperty("taxRate").GetDecimal());
        await Problem(await owner.GetAsync($"/api/v1/pos/items/barcode/NOPE-{Suffix()}?branchId={kit.Branch}"), HttpStatusCode.NotFound, "CAT-002");

        // A branch override price wins over the list price.
        await Ok(await owner.PutAsJsonAsync($"/api/v1/catalog/variants/{item.VariantId}/branches/{kit.Branch}", new { overridePrice = 3.250m, overrideMinPrice = 3.000m, reorderLevel = 4m, binLocation = "A1" }), HttpStatusCode.NoContent);
        var search = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/pos/items?branchId={kit.Branch}&q={item.Sku}");
        Assert.Equal(3.250m, search[0].GetProperty("listPrice").GetDecimal());

        // Product type and tracking cannot change once stock has moved; serialized products take stock one scanned unit at a time.
        var product = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/catalog/products/{item.ProductId}");
        var p = product.GetProperty("product");
        var change = await owner.PutAsJsonAsync($"/api/v1/catalog/products/{item.ProductId}", new
        {
            nameEn = "Screen guard", nameAr = "واقي شاشة", productType = "spare_part", taxCodeId = p.GetProperty("taxCodeId").GetGuid(), version = p.GetProperty("version").GetInt32(),
        });
        await Problem(change, HttpStatusCode.BadRequest, "VAL-001");
        var phone = await kit.ProductAsync(owner, "Phone X", 250m, type: "device", tracking: "imei");
        await Problem(await owner.PostAsJsonAsync("/api/v1/inventory/adjustments", new
        {
            branchId = kit.Branch, kind = "opening", lines = new[] { new { variantId = phone.VariantId, qty = 1m, unitCost = 200m, reasonCode = "opening" } },
        }), HttpStatusCode.BadRequest, "DEV-012");
        await AssertConsistentAsync(owner);
    }

    [PgFact]
    public async Task Stock_adjustments_and_counts_keep_the_ledger_and_the_inventory_accounts_equal()
    {
        using var owner = await kit.OwnerAsync();
        var item = await kit.ProductAsync(owner, "Charger 20W", 6.000m, openingQty: 10, unitCost: 2.000m);
        await kit.OpeningAsync(owner, item, 5, 2.600m);   // average becomes (20 + 13) / 15 = 2.2
        var bal = (await owner.GetFromJsonAsync<JsonElement>($"/api/v1/inventory/balances?branchId={kit.Branch}&q={item.Sku}"))[0];
        Assert.Equal(15m, bal.GetProperty("qty").GetDecimal());
        Assert.Equal(33.000m, bal.GetProperty("value").GetDecimal());
        Assert.Equal(2.2m, bal.GetProperty("avgCost").GetDecimal());

        await Ok(await owner.PostAsJsonAsync("/api/v1/inventory/adjustments", new
        {
            branchId = kit.Branch, kind = "adjustment", note = "two broken in the back",
            lines = new[] { new { variantId = item.VariantId, qty = -2m, unitCost = (decimal?)null, reasonCode = "damaged" } },
        }), HttpStatusCode.Created);
        Assert.Equal(13m, await kit.StockAsync(owner, item));

        var count = await Ok(await owner.PostAsJsonAsync("/api/v1/inventory/counts", new { branchId = kit.Branch, note = "monthly count" }), HttpStatusCode.Created);
        var countId = count.GetProperty("id").GetGuid();
        await Ok(await owner.PutAsJsonAsync($"/api/v1/inventory/counts/{countId}/lines", new[] { new { variantId = item.VariantId, countedQty = 12m } }), HttpStatusCode.NoContent);
        var posted = await Ok(await owner.PostAsync($"/api/v1/inventory/counts/{countId}/post", null));
        Assert.Equal(1, posted.GetProperty("changed").GetInt32());
        Assert.Equal(12m, await kit.StockAsync(owner, item));
        await Problem(await owner.PostAsync($"/api/v1/inventory/counts/{countId}/post", null), HttpStatusCode.Conflict, "INV-002");

        var moves = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/inventory/movements?variantId={item.VariantId}");
        Assert.Equal(4, moves.GetArrayLength());
        Assert.Equal("count", moves[0].GetProperty("movementType").GetString());
        Assert.Equal(1L, await fx.QueryDbAsync<long>($"SELECT count(*) FROM acct.journal WHERE source_doc_type = 'stock_count' AND source_doc_id = '{countId}'"));
        await AssertConsistentAsync(owner);
    }

    [PgFact]
    public async Task A_shift_sells_with_approvals_returns_voids_and_closes_blind_with_every_total_reconciled()
    {
        using var owner = await kit.OwnerAsync();
        await kit.SetPolicyAsync(owner, cashRounding: 0.005m);
        var guard = await kit.ProductAsync(owner, "Glass guard", 4.000m, minPrice: 3.500m, openingQty: 20, unitCost: 1.500m);
        var cable = await kit.ProductAsync(owner, "USB-C cable", 2.333m, openingQty: 30, unitCost: 0.800m);
        var service = await kit.ProductAsync(owner, "Setup service", 5.000m, type: "service");
        var term = await kit.TerminalAsync(owner);
        var cashier = await kit.UserAsync(owner, "p4cash", "cashier", pin: "2468");
        var manager = await kit.UserAsync(owner, "p4mgr", "branch_manager", pin: "1357");
        using var pos = await kit.PosAsync(cashier.Username, cashier.Password, term);

        await Problem(await pos.PostAsJsonAsync($"/api/v1/sales/{Guid.CreateVersion7()}/complete", new { lines = new[] { Line(cable, 1) }, payments = new[] { Cash(2.335m) } }),
            HttpStatusCode.Conflict, "POS-001");
        var shift = await OpenShiftAsync(pos, 20.000m);
        await Problem(await pos.PostAsJsonAsync("/api/v1/pos/shifts", new { openingFloat = 5m }), HttpStatusCode.Conflict, "POS-002");

        // 1. Cash sale with cash rounding to 0.005: 2.333 x 3 = 6.999, paid 7.000 (rounding +0.001), tendered 10.
        var saleId = Guid.CreateVersion7();
        var s1 = await SellAsync(pos, [Line(cable, 3)], [Cash(7.000m, 10m)], saleId);
        Assert.Equal(6.999m, s1.GetProperty("gross").GetDecimal());
        Assert.Equal(0.001m, s1.GetProperty("cashRounding").GetDecimal());
        Assert.Equal(3.000m, s1.GetProperty("change").GetDecimal());
        // The same sale again (a retry after a lost answer) returns the same document, not a second sale.
        var again = await SellAsync(pos, [Line(cable, 3)], [Cash(7.000m, 10m)], saleId);
        Assert.Equal(s1.GetProperty("number").GetString(), again.GetProperty("number").GetString());
        Assert.Equal(27m, await kit.StockAsync(owner, cable));
        await Problem(await pos.PostAsJsonAsync($"/api/v1/sales/{Guid.CreateVersion7()}/complete", new { lines = new[] { Line(cable, 1) }, payments = new[] { Cash(2.330m) } }),
            HttpStatusCode.BadRequest, "POS-005");

        // 2. Discount above the cashier's limit (5% / 2.000) needs a manager; the PIN approval is spent once.
        var big = new[] { Line(guard, 2, discount: 1.000m) };     // 12.5% of 8.000
        await Problem(await pos.PostAsJsonAsync($"/api/v1/sales/{Guid.CreateVersion7()}/complete", new { lines = big, payments = new[] { Cash(7.000m) } }),
            HttpStatusCode.Forbidden, "POS-007");
        await Problem(await pos.PostAsJsonAsync("/api/v1/approvals/pin", new { action = "POS.DISCOUNT.ABOVE_LIMIT", approverUsername = manager.Username, pin = "0000" }),
            HttpStatusCode.Forbidden, "POS-008");
        await Problem(await pos.PostAsJsonAsync("/api/v1/approvals/pin", new { action = "POS.DISCOUNT.ABOVE_LIMIT", approverUsername = cashier.Username, pin = "2468" }),
            HttpStatusCode.Forbidden, "POS-008");
        var approval = await Ok(await pos.PostAsJsonAsync("/api/v1/approvals/pin", new { action = "POS.DISCOUNT.ABOVE_LIMIT", approverUsername = manager.Username, pin = "1357", reason = "loyal customer" }));
        var aid = approval.GetProperty("id").GetGuid();
        var s2 = await SellAsync(pos, [Line(guard, 2, discount: 1.000m, approvalId: aid)], [Card(7.000m)]);
        Assert.Equal(7.000m, s2.GetProperty("gross").GetDecimal());
        await Problem(await pos.PostAsJsonAsync($"/api/v1/sales/{Guid.CreateVersion7()}/complete", new { lines = new[] { Line(guard, 2, discount: 1.000m, approvalId: aid) }, payments = new[] { Cash(7.000m) } }),
            HttpStatusCode.BadRequest, "POS-008");

        // 3. Below the minimum price needs POS.PRICE.OVERRIDE (remote approval, decided by the manager on another device).
        var low = new[] { Line(guard, 1, unitPrice: 3.000m) };
        await Problem(await pos.PostAsJsonAsync($"/api/v1/sales/{Guid.CreateVersion7()}/complete", new { lines = low, payments = new[] { Cash(3.000m) } }),
            HttpStatusCode.Forbidden, "POS-006");
        var remote = await Ok(await pos.PostAsJsonAsync("/api/v1/approvals", new { action = "POS.PRICE.OVERRIDE", reason = "match a competitor" }), HttpStatusCode.Created);
        using (var mgr = fx.Client(await fx.TokenAsync(manager.Username, manager.Password)))
        {
            var pending = await mgr.GetFromJsonAsync<JsonElement>("/api/v1/approvals?status=pending");
            Assert.Contains(pending.EnumerateArray(), a => a.GetProperty("id").GetGuid() == remote.GetProperty("id").GetGuid());
            await Ok(await mgr.PostAsJsonAsync($"/api/v1/approvals/{remote.GetProperty("id").GetGuid()}/approve", new { reason = "ok" }));
        }
        var s3 = await SellAsync(pos, [Line(guard, 1, unitPrice: 3.000m, approvalId: remote.GetProperty("id").GetGuid()), Line(service, 1)],
            [Cash(7.000m), new { method = "bank_transfer", amount = 1.000m, reference = "TRX-1" }]);
        Assert.Equal(8.000m, s3.GetProperty("gross").GetDecimal());
        var s4 = await SellAsync(pos, [Line(guard, 1)], [Card(4.000m, "BANK-2")]);
        Assert.Equal(16m, await kit.StockAsync(owner, guard));

        // 4. Return one cable of sale 1 to stock: 6.999 / 3 = 2.333, refunded in cash rounded to 2.335.
        var original = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/sales/{saleId}");
        var cableLine = original.GetProperty("lines")[0].GetProperty("id").GetGuid();
        var ret = await Ok(await pos.PostAsJsonAsync($"/api/v1/sales/{saleId}/return", new
        {
            id = Guid.CreateVersion7(), reason = "wrong length", lines = new[] { new { lineId = cableLine, qty = 1m, restock = true } }, refunds = new[] { Cash(2.335m) },
        }));
        Assert.Equal(2.333m, ret.GetProperty("gross").GetDecimal());
        Assert.Equal(0.002m, ret.GetProperty("cashRounding").GetDecimal());
        Assert.Equal(28m, await kit.StockAsync(owner, cable));
        await Problem(await pos.PostAsJsonAsync($"/api/v1/sales/{saleId}/return", new
        {
            id = Guid.CreateVersion7(), reason = "again", lines = new[] { new { lineId = cableLine, qty = 3m, restock = true } }, refunds = new[] { Cash(7.000m) },
        }), HttpStatusCode.Conflict, "POS-010");

        // 5. Voiding needs POS.SALE.VOID; the void gives the card payment back and restocks.
        var s2Id = s2.GetProperty("id").GetGuid();
        await Problem(await pos.PostAsJsonAsync($"/api/v1/sales/{s2Id}/void", new { id = Guid.CreateVersion7(), reason = "customer changed mind" }), HttpStatusCode.Forbidden, "AUTH-403");
        var voidApproval = await Ok(await pos.PostAsJsonAsync("/api/v1/approvals/pin", new { action = "POS.SALE.VOID", approverUsername = manager.Username, pin = "1357" }));
        var voided = await Ok(await pos.PostAsJsonAsync($"/api/v1/sales/{s2Id}/void", new { id = Guid.CreateVersion7(), reason = "customer changed mind", approvalId = voidApproval.GetProperty("id").GetGuid() }));
        Assert.Equal(7.000m, voided.GetProperty("gross").GetDecimal());
        Assert.Equal(18m, await kit.StockAsync(owner, guard));
        await Problem(await pos.PostAsJsonAsync($"/api/v1/sales/{s2Id}/void", new { id = Guid.CreateVersion7(), reason = "twice" }), HttpStatusCode.Conflict, "POS-013");

        // 6. Cash events: pay-in and drop by the cashier; a pay-out and a no-sale need a manager.
        await Ok(await pos.PostAsJsonAsync("/api/v1/pos/shifts/current/cash-events", new { kind = "pay_in", amount = 5.000m, reason = "change from the safe" }));
        await Ok(await pos.PostAsJsonAsync("/api/v1/pos/shifts/current/cash-events", new { kind = "drop", amount = 10.000m, reason = "to the safe" }));
        await Problem(await pos.PostAsJsonAsync("/api/v1/pos/shifts/current/cash-events", new { kind = "pay_out", amount = 2.000m, reason = "tea and water" }), HttpStatusCode.Forbidden, "AUTH-403");
        var payOutApproval = await Ok(await pos.PostAsJsonAsync("/api/v1/approvals/pin", new { action = "POS.CASH.PAY_OUT", approverUsername = manager.Username, pin = "1357" }));
        await Ok(await pos.PostAsJsonAsync("/api/v1/pos/shifts/current/cash-events", new { kind = "pay_out", amount = 2.000m, reason = "tea and water", approvalId = payOutApproval.GetProperty("id").GetGuid() }));
        await Problem(await pos.PostAsJsonAsync("/api/v1/pos/shifts/current/cash-events", new { kind = "drop", amount = 500m, reason = "too much" }), HttpStatusCode.BadRequest, "VAL-001");

        // 7. Blind close: the cashier does not see the expected cash.
        var current = await pos.GetFromJsonAsync<JsonElement>("/api/v1/pos/shifts/current");
        Assert.True(current.GetProperty("blind").GetBoolean());
        Assert.Equal(0m, current.GetProperty("figures").GetProperty("expectedCash").GetDecimal());
        var shiftId = shift.GetProperty("id").GetGuid();
        var x = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/pos/shifts/{shiftId}");
        Assert.Equal("X", x.GetProperty("report").GetString());
        // 20 float + cash (7.000 + 7.000 - 2.335) + 5 pay-in - 2 pay-out - 10 drop
        Assert.Equal(24.665m, x.GetProperty("figures").GetProperty("expectedCash").GetDecimal());

        // 8. Two baisa short of 24.665 is within the threshold; 2.000 short needs a reason and a manager.
        object Close(string? reason, Guid? approvalId, bool settle) => new
        {
            countedCash = 22.665m, varianceReason = reason, approvalId,
            cardSettlements = settle ? new[] { new { cardTerminal = "BANK-2", batchTotal = 4.000m, reason = (string?)null } } : null,
        };
        await Problem(await pos.PostAsJsonAsync("/api/v1/pos/shifts/current/close", Close(null, null, true)), HttpStatusCode.Conflict, "POS-014");
        await Problem(await pos.PostAsJsonAsync("/api/v1/pos/shifts/current/close", Close("counted twice", null, true)), HttpStatusCode.Conflict, "POS-014");
        var accept = (await Ok(await pos.PostAsJsonAsync("/api/v1/approvals/pin", new { action = "POS.SHIFT.ACCEPT_VARIANCE", approverUsername = manager.Username, pin = "1357" }))).GetProperty("id").GetGuid();
        await Problem(await pos.PostAsJsonAsync("/api/v1/pos/shifts/current/close", Close("counted twice", accept, false)), HttpStatusCode.BadRequest, "VAL-001");
        var closed = await Ok(await pos.PostAsJsonAsync("/api/v1/pos/shifts/current/close", Close("counted twice", accept, true)));
        Assert.Equal(-2.000m, closed.GetProperty("variance").GetDecimal());
        Assert.Equal(manager.Id, closed.GetProperty("varianceAcceptedBy").GetGuid());
        var z = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/pos/shifts/{shiftId}");
        Assert.Equal("Z", z.GetProperty("report").GetString());
        Assert.Equal(4, z.GetProperty("figures").GetProperty("salesCount").GetInt32());
        Assert.Equal(2, z.GetProperty("figures").GetProperty("creditNotesCount").GetInt32());
        Assert.Equal(1, z.GetProperty("cardSettlements").GetArrayLength());

        // 9. Journals: rounding to over/short, the variance, the pay-out; receipts carry the QR.
        Assert.Equal(0.001m, await fx.QueryDbAsync<decimal>($"""
            SELECT l.credit FROM acct.journal_line l JOIN acct.journal j ON j.id = l.journal_id JOIN acct.account a ON a.id = l.account_id
             WHERE j.source_doc_id = '{saleId}' AND j.posting_kind = 'pos.sale' AND a.code = '6400'
            """));
        Assert.Equal(2.000m, await fx.QueryDbAsync<decimal>($"""
            SELECT l.debit FROM acct.journal_line l JOIN acct.journal j ON j.id = l.journal_id JOIN acct.account a ON a.id = l.account_id
             WHERE j.source_doc_id = '{shiftId}' AND a.code = '6400'
            """));
        var receipt = await Ok(await pos.GetAsync($"/api/v1/sales/{saleId}/receipt?reprint=true"));
        Assert.True(receipt.GetProperty("isCopy").GetBoolean());
        Assert.Equal(7.000m, receipt.GetProperty("total").GetDecimal());
        var qr = PosMath.ReadQr(receipt.GetProperty("qrPayload").GetString()!);
        Assert.Equal("Al Noor Mobiles LLC", qr[1]);
        Assert.Equal("OM1100223344", qr[2]);
        Assert.Equal("6.999", qr[4]);
        Assert.Equal("0.333", qr[5]);
        var pdf = await pos.GetByteArrayAsync($"/api/v1/sales/{saleId}/invoice.pdf");
        Assert.True(pdf.Length > 1000 && pdf[0] == '%' && pdf[1] == 'P' && pdf[2] == 'D' && pdf[3] == 'F');
        await AssertConsistentAsync(owner);
    }

    [PgFact]
    public async Task Customers_tax_invoices_parked_carts_and_terminal_device_keys()
    {
        using var owner = await kit.OwnerAsync();
        await kit.SetPolicyAsync(owner);
        var item = await kit.ProductAsync(owner, "Power bank", 12.000m, openingQty: 5, unitCost: 7.000m);
        var term = await kit.TerminalAsync(owner);

        // The device key is required on that terminal.
        using (var anon = fx.Client())
        {
            await Problem(await anon.PostAsJsonAsync("/api/v1/auth/login", new { username = KiteServerFixture.OwnerUsername, password = KiteServerFixture.OwnerPassword, terminalId = term.Id }),
                HttpStatusCode.Forbidden, "POS-017");
            await Problem(await anon.PostAsJsonAsync("/api/v1/auth/login", new { username = KiteServerFixture.OwnerUsername, password = KiteServerFixture.OwnerPassword, terminalId = term.Id, deviceKey = NewDeviceKey() }),
                HttpStatusCode.Forbidden, "POS-017");
        }
        using var pos = await kit.OwnerPosAsync(term);
        await OpenShiftAsync(pos, 0m);

        var customer = await Ok(await pos.PostAsJsonAsync("/api/v1/customers", new { nameEn = "Gulf Trading LLC", nameAr = "الخليج للتجارة", kind = "company", phone = "9123 4567", vatNumber = "OM1199887766" }), HttpStatusCode.Created);
        Assert.Equal("+96891234567", customer.GetProperty("phoneE164").GetString());
        Assert.StartsWith("C-", customer.GetProperty("number").GetString());
        var found = await owner.GetFromJsonAsync<JsonElement>("/api/v1/customers?q=91234567");
        Assert.Contains(found.EnumerateArray(), c => c.GetProperty("id").GetGuid() == customer.GetProperty("id").GetGuid());

        await Problem(await pos.PostAsJsonAsync($"/api/v1/sales/{Guid.CreateVersion7()}/complete", new { docType = "tax_invoice", lines = new[] { Line(item, 1) }, payments = new[] { Cash(12m) } }),
            HttpStatusCode.BadRequest, "POS-016");
        var inv = await SellAsync(pos, [Line(item, 1)], [new { method = "bank_transfer", amount = 12.000m, reference = "TT-55" }], docType: "tax_invoice", customerId: customer.GetProperty("id").GetGuid());
        var receipt = await pos.GetFromJsonAsync<JsonElement>($"/api/v1/sales/{inv.GetProperty("id").GetGuid()}/receipt");
        Assert.Equal("Tax invoice", receipt.GetProperty("titleEn").GetString());
        Assert.Equal("OM1199887766", receipt.GetProperty("customerVat").GetString());

        // Changed prices are caught when the terminal sends the total it showed.
        await Problem(await pos.PostAsJsonAsync($"/api/v1/sales/{Guid.CreateVersion7()}/complete", new { lines = new[] { Line(item, 1) }, payments = new[] { Cash(12m) }, expectedTotal = 11.500m }),
            HttpStatusCode.Conflict, "POS-015");
        // Blocked negative stock: 4 left.
        await Problem(await pos.PostAsJsonAsync($"/api/v1/sales/{Guid.CreateVersion7()}/complete", new { lines = new[] { Line(item, 5) }, payments = new[] { Cash(60m) } }),
            HttpStatusCode.Conflict, "INV-001");

        // Park a cart at this terminal and recall it at another terminal of the branch.
        var cartId = Guid.CreateVersion7();
        await Ok(await pos.PostAsJsonAsync("/api/v1/sales/parked", new { id = cartId, label = "Ahmed - back in 5 min", cart = new { lines = new[] { new { variantId = item.VariantId, qty = 2 } } } }), HttpStatusCode.NoContent);
        var term2 = await kit.TerminalAsync(owner);
        using var pos2 = await kit.OwnerPosAsync(term2);
        var parked = await pos2.GetFromJsonAsync<JsonElement>("/api/v1/sales/parked");
        Assert.Contains(parked.EnumerateArray(), p => p.GetProperty("id").GetGuid() == cartId);
        var cart = await Ok(await pos2.PostAsync($"/api/v1/sales/parked/{cartId}/recall", null));
        Assert.Equal(2, cart.GetProperty("lines")[0].GetProperty("qty").GetInt32());
        await Problem(await pos2.PostAsync($"/api/v1/sales/parked/{cartId}/recall", null), HttpStatusCode.NotFound, "GEN-404");

        // A reinstalled terminal: the manager binds the new key, the old key stops working.
        var newKey = NewDeviceKey();
        await Ok(await owner.PostAsJsonAsync($"/api/v1/terminals/{term.Id}/device-key", new { deviceKey = newKey }), HttpStatusCode.NoContent);
        using (var anon = fx.Client())
        {
            await Problem(await anon.PostAsJsonAsync("/api/v1/auth/login", new { username = KiteServerFixture.OwnerUsername, password = KiteServerFixture.OwnerPassword, terminalId = term.Id, deviceKey = term.DeviceKey }),
                HttpStatusCode.Forbidden, "POS-017");
            await Ok(await anon.PostAsJsonAsync("/api/v1/auth/login", new { username = KiteServerFixture.OwnerUsername, password = KiteServerFixture.OwnerPassword, terminalId = term.Id, deviceKey = newKey }));
        }
        await AssertConsistentAsync(owner);
    }

    [PgFact]
    public async Task The_print_relay_sends_receipts_and_the_drawer_kick_to_a_network_printer()
    {
        using var owner = await kit.OwnerAsync();
        await kit.SetPolicyAsync(owner);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var printer = await Ok(await owner.PostAsJsonAsync("/api/v1/printers", new { branchId = kit.Branch, name = "Front " + Suffix(), host = "127.0.0.1", port, paperWidthMm = 80 }), HttpStatusCode.Created);
        var printerId = printer.GetProperty("id").GetGuid();
        var term = await kit.TerminalAsync(owner);
        using var pos = await kit.OwnerPosAsync(term);

        async Task<byte[]> ReceiveAsync()
        {
            using var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
            using var ms = new MemoryStream();
            await client.GetStream().CopyToAsync(ms).WaitAsync(TimeSpan.FromSeconds(10));
            return ms.ToArray();
        }

        var receiving = ReceiveAsync();
        var data = new byte[] { 0x1B, 0x40, (byte)'K', (byte)'I', (byte)'T', (byte)'E', 0x0A, 0x1D, 0x56, 0x42, 0x00 };
        await Ok(await pos.PostAsJsonAsync($"/api/v1/printers/{printerId}/jobs", new { kind = "receipt", data = Convert.ToBase64String(data) }));
        Assert.Equal(data, await receiving);

        // The drawer opens only during the user's own shift.
        await Problem(await pos.PostAsJsonAsync($"/api/v1/printers/{printerId}/jobs", new { kind = "drawer" }), HttpStatusCode.Conflict, "POS-001");
        await OpenShiftAsync(pos, 0m);
        receiving = ReceiveAsync();
        await Ok(await pos.PostAsJsonAsync($"/api/v1/printers/{printerId}/jobs", new { kind = "drawer" }));
        Assert.Equal(new byte[] { 0x1B, 0x70, 0x00, 0x19, 0xFA }, await receiving);

        // Printers outside the shop network are refused, and an unreachable printer gives a clear error.
        var outside = await Ok(await owner.PostAsJsonAsync("/api/v1/printers", new { branchId = kit.Branch, name = "Outside " + Suffix(), host = "8.8.8.8", port = 9100 }), HttpStatusCode.Created);
        await Problem(await pos.PostAsJsonAsync($"/api/v1/printers/{outside.GetProperty("id").GetGuid()}/jobs", new { kind = "test" }), HttpStatusCode.BadRequest, "VAL-001");
        listener.Stop();
        await Problem(await pos.PostAsJsonAsync($"/api/v1/printers/{printerId}/jobs", new { kind = "test" }), HttpStatusCode.BadGateway, "PRN-001");
    }

    [PgFact]
    public async Task Emergency_records_upload_once_in_order_and_conflicts_are_kept()
    {
        using var owner = await kit.OwnerAsync();
        await kit.SetPolicyAsync(owner);
        var item = await kit.ProductAsync(owner, "Earphones", 3.150m, openingQty: 1, unitCost: 1.000m);
        var browser = await kit.TerminalAsync(owner);
        using (var bpos = await kit.OwnerPosAsync(browser))
            await Problem(await bpos.GetAsync("/api/v1/pos/emergency/snapshot"), HttpStatusCode.Forbidden, "POS-019");

        var term = await kit.TerminalAsync(owner, kind: "windows", emergency: true);
        var cashier = await kit.UserAsync(owner, "p4em", "cashier", pin: "8642");
        using var pos = await kit.PosAsync(cashier.Username, cashier.Password, term);
        var snap = await Ok(await pos.GetAsync("/api/v1/pos/emergency/snapshot"));
        Assert.Contains(snap.GetProperty("items").EnumerateArray(), i => i.GetProperty("variantId").GetGuid() == item.VariantId);
        Assert.Contains(snap.GetProperty("users").EnumerateArray(), u => u.GetProperty("username").GetString() == cashier.Username && u.GetProperty("pinHash").GetString()!.Length > 20);
        var prefix = snap.GetProperty("numberPrefix").GetString()!;
        Assert.Equal($"EM-B01-{term.Code}-", prefix);

        // Offline: open an emergency shift, sell 2 (only 1 in stock: allowed, flagged), pay out, close.
        var shiftId = Guid.CreateVersion7();
        var saleId = Guid.CreateVersion7();
        var at = DateTimeOffset.UtcNow.AddMinutes(-30);
        var line = new { variantId = item.VariantId, qty = 2m, unitPrice = 3.150m, listPrice = 3.150m, discount = 0m, net = 6.000m, tax = 0.300m, gross = 6.300m, taxRate = 5m,
                         taxCodeId = await kit.TaxCodeAsync(owner), productType = "accessory", descriptionEn = "Earphones", descriptionAr = "سماعات" };
        object Rec(long seq, string kind, object payload, int minutes) => new { id = Guid.CreateVersion7(), seq, kind, recordedAt = at.AddMinutes(minutes), payload };
        var records = new[]
        {
            Rec(1, "shift_open", new { shiftId, number = $"EMS-{term.Code}-000001", cashierId = cashier.Id, openingFloat = 10.000m }, 0),
            Rec(2, "sale", new { saleId, shiftId, cashierId = cashier.Id, number = prefix + "000001", lines = new[] { line },
                                 payments = new[] { new { method = "cash", amount = 6.300m, tendered = 10.000m } }, cashRounding = 0m }, 5),
            Rec(3, "sale", new { saleId = Guid.CreateVersion7(), shiftId, cashierId = cashier.Id, number = prefix + "000002", lines = new[] { line },
                                 payments = new[] { new { method = "cash", amount = 5.000m } }, cashRounding = 0m }, 6),     // does not add up: kept as a conflict
            Rec(4, "cash_event", new { eventId = Guid.CreateVersion7(), shiftId, kind = "pay_out", amount = 1.000m, reason = "bread", createdBy = cashier.Id }, 10),
            Rec(5, "shift_close", new { shiftId, closedBy = cashier.Id, countedCash = 15.300m }, 20),
        };
        var up = await Ok(await pos.PostAsJsonAsync("/api/v1/pos/emergency/records", new { records }));
        var results = up.GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal(["applied", "applied", "conflict", "applied", "applied"], results.Select(r => r.GetProperty("result").GetString()).ToArray());
        Assert.Contains("stock", results[1].GetProperty("detail").GetString());

        // Uploading again (a retry) applies nothing twice.
        var again = await Ok(await pos.PostAsJsonAsync("/api/v1/pos/emergency/records", new { records }));
        Assert.All(again.GetProperty("results").EnumerateArray(), r => Assert.True(r.GetProperty("duplicate").GetBoolean()));
        Assert.Equal(1L, await fx.QueryDbAsync<long>($"SELECT count(*) FROM sales.sale WHERE id = '{saleId}' AND is_emergency AND number = '{prefix}000001'"));
        Assert.Equal(-1m, await kit.StockAsync(owner, item));
        var exceptions = await owner.GetFromJsonAsync<JsonElement>("/api/v1/inventory/exceptions?status=open");
        Assert.Contains(exceptions.EnumerateArray(), e => e.GetProperty("sourceNumber").GetString() == prefix + "000001");
        Assert.Equal("closed", await fx.QueryDbAsync<string>($"SELECT status FROM sales.shift WHERE id = '{shiftId}'"));
        Assert.Equal(15.300m, await fx.QueryDbAsync<decimal>($"SELECT expected_cash FROM sales.shift WHERE id = '{shiftId}'"));
        var conflicts = await owner.GetFromJsonAsync<JsonElement>("/api/v1/pos/emergency/records?result=conflict");
        Assert.Contains(conflicts.EnumerateArray(), c => c.GetProperty("terminalCode").GetString() == term.Code && c.GetProperty("seq").GetInt64() == 3);
        await AssertConsistentAsync(owner);
    }
}
