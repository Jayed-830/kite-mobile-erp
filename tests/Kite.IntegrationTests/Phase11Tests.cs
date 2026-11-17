using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
using static Kite.IntegrationTests.PosKit;
using static Kite.IntegrationTests.PurchKit;

namespace Kite.IntegrationTests;

/// <summary>
/// Phase 11 (blueprint 16, 17): orders approved within each role's limit; goods received against them with landed costs and one IMEI per
/// phone, never more than ordered; supplier invoices matched three ways, a price difference outside tolerance only with an override,
/// the difference split between stock on hand and purchase price variance; returns and debit notes; payments oldest first, head office
/// paying a branch's invoice through the interbranch accounts; manual journals with a second approver, control accounts and reversal;
/// periods closed by branch with late documents moving on; VAT returns from closed months; statements that balance; the ledger check.
/// </summary>
[Collection("server")]
public sealed class Phase11Tests(KiteServerFixture fx)
{
    private readonly PosKit kit = new(fx);
    private PurchKit P => new(fx, kit);

    [PgFact]
    public async Task Orders_receipts_and_invoices_follow_the_limits_the_order_and_the_three_way_match()
    {
        using var owner = await kit.OwnerAsync();
        await EnableAsync(owner);
        var supplier = await SupplierAsync(owner);
        var cases = await P.AccessoryAsync(owner);
        var phone = await P.PhoneAsync(owner);
        var manager = await kit.UserAsync(owner, "p11mgr", "branch_manager");
        var officer = await kit.UserAsync(owner, "p11buy", "purchasing_officer");
        using var mgr = fx.Client(await fx.TokenAsync(manager.Username, manager.Password));
        using var buyer = fx.Client(await fx.TokenAsync(officer.Username, officer.Password));

        // 10 cases at 50 and 2 phones at 200: 900 + 45 VAT. The branch manager (limit 500) orders but cannot approve it; the officer (2000) can.
        var (po, order) = await OrderAsync(mgr, kit.Branch, supplier, OrderLine(cases, 10, 50m), OrderLine(phone, 2, 200m));
        Assert.Equal(945m, order.GetProperty("order").GetProperty("grossTotal").GetDecimal());
        Assert.False(order.GetProperty("approval").GetProperty("canApprove").GetBoolean());
        await Problem(await mgr.PostAsync($"/api/v1/purchasing/orders/{po}/approve", null), HttpStatusCode.Forbidden, "PUR-006");
        // Goods are not received against a draft.
        await Problem(await ReceiveRawAsync(owner, kit.Branch, supplier, po, [new { poLineId = PoLine(order, 0), qty = 1m }]), HttpStatusCode.Conflict, "PUR-001");
        await Ok(await buyer.PostAsync($"/api/v1/purchasing/orders/{po}/approve", null));
        await Ok(await buyer.PostAsync($"/api/v1/purchasing/orders/{po}/send", null));
        var pdf = await buyer.GetAsync($"/api/v1/purchasing/orders/{po}/pdf");
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        // An approved order does not change.
        await Problem(await owner.PutAsJsonAsync($"/api/v1/purchasing/orders/{po}", new { branchId = kit.Branch, supplierId = supplier, lines = new[] { OrderLine(cases, 1, 1m) } }),
            HttpStatusCode.Conflict, "PUR-001");

        // First delivery: 6 cases, both phones (one IMEI each), and 12 freight spread by value (300 : 400).
        var grniBefore = await P.GlAsync("2110");
        var stockBefore = await P.GlAsync("1400,1420");
        await Problem(await ReceiveRawAsync(owner, kit.Branch, supplier, po, [new { poLineId = PoLine(order, 1), qty = 2m, units = Units(1) }]),
            HttpStatusCode.BadRequest, "DEV-012");
        var gr = await ReceiveAsync(owner, kit.Branch, supplier, po,
            [new { poLineId = PoLine(order, 0), qty = 6m }, new { poLineId = PoLine(order, 1), qty = 2m, units = Units(2) }],
            [new { kind = "freight", description = "DHL", amount = 12m, basis = "value" }]);
        Assert.Equal(700m, gr.GetProperty("receipt").GetProperty("goodsValue").GetDecimal());
        Assert.Equal(5.143m, gr.GetProperty("lines")[0].GetProperty("chargeShare").GetDecimal());
        Assert.Equal(6.857m, gr.GetProperty("lines")[1].GetProperty("chargeShare").GetDecimal());
        Assert.Equal(-712m, await P.GlAsync("2110") - grniBefore);
        Assert.Equal(712m, await P.GlAsync("1400,1420") - stockBefore);
        var units = gr.GetProperty("lines")[1].GetProperty("units").EnumerateArray().Select(u => u.GetProperty("id").GetGuid()).ToArray();
        Assert.Equal([203.429m, 203.428m], gr.GetProperty("lines")[1].GetProperty("units").EnumerateArray().Select(u => u.GetProperty("cost").GetDecimal()).ToArray());
        Assert.Equal("partly_received", (await Ok(await owner.GetAsync($"/api/v1/purchasing/orders/{po}"))).GetProperty("order").GetProperty("status").GetString());
        // Only 4 cases are still to come; the phones are all in.
        await Problem(await ReceiveRawAsync(owner, kit.Branch, supplier, po, [new { poLineId = PoLine(order, 0), qty = 5m }]), HttpStatusCode.Conflict, "PUR-002");

        // The invoice: cases at 51 (1.000 over, within 2%), phones at 210 (10 over each: outside), the freight 12.500 (within the 1.000 amount).
        var open = await Ok(await owner.GetAsync($"/api/v1/purchasing/suppliers/{supplier}/open"));
        Assert.Equal(2, open.GetProperty("receiptLines").EnumerateArray().Count(l => l.GetProperty("receiptId").GetGuid() == gr.GetProperty("receipt").GetProperty("id").GetGuid()));
        var chargeId = gr.GetProperty("charges")[0].GetProperty("id").GetGuid();
        var inv = Guid.CreateVersion7();
        var draft = await Ok(await InvoiceRawAsync(buyer, kit.Branch, supplier, "INV-" + Suffix(),
            [Goods(ReceiptLine(gr, 0), 6, 51m), Goods(ReceiptLine(gr, 1), 2, 210m), Charge(chargeId, 12.5m)], inv));
        Assert.Equal("needs_approval", draft.GetProperty("invoice").GetProperty("status").GetString());
        Assert.Equal(["ok", "price", "ok"], draft.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("matchStatus").GetString()!).ToArray());
        // The officer cannot post outside tolerance; the owner can, with a reason.
        await Problem(await buyer.PostAsJsonAsync($"/api/v1/purchasing/invoices/{inv}/post", new { }), HttpStatusCode.Conflict, "PUR-004");
        await Problem(await owner.PostAsJsonAsync($"/api/v1/purchasing/invoices/{inv}/post", new { }), HttpStatusCode.BadRequest, "VAL-001");
        var ppvBefore = await P.GlAsync("5300");
        var posted = await PostInvoiceAsync(owner, inv, "Supplier raised the phone price; agreed by phone");
        var h = posted.GetProperty("invoice");
        Assert.Equal("posted", h.GetProperty("status").GetString());
        Assert.Equal(738.5m, h.GetProperty("netTotal").GetDecimal());     // 306 + 420 + 12.5
        Assert.Equal(36.925m, h.GetProperty("taxTotal").GetDecimal());
        // All cases and both phones are still on hand: their differences go to stock (6 and 20); the freight's 0.500 to purchase price variance.
        var lines = posted.GetProperty("lines").EnumerateArray().ToArray();
        Assert.Equal((6m, 0m), (lines[0].GetProperty("varianceInventory").GetDecimal(), lines[0].GetProperty("variancePpv").GetDecimal()));
        Assert.Equal((20m, 0m), (lines[1].GetProperty("varianceInventory").GetDecimal(), lines[1].GetProperty("variancePpv").GetDecimal()));
        Assert.Equal((0m, 0.5m), (lines[2].GetProperty("varianceInventory").GetDecimal(), lines[2].GetProperty("variancePpv").GetDecimal()));
        Assert.Equal(0.5m, await P.GlAsync("5300") - ppvBefore);
        Assert.Equal(-712m + 712m, await P.GlAsync("2110") - grniBefore);          // what was received is cleared
        Assert.Equal(738m, await P.GlAsync("1400,1420") - stockBefore);           // 712 + 26
        Assert.Equal(213.429m, await DeviceKit.CostAsync(owner, units[0]));
        Assert.Equal(213.428m, await DeviceKit.CostAsync(owner, units[1]));
        Assert.Equal(-775.425m, await P.GlAsync("2100", party: supplier));
        Assert.Equal(775.425m, await P.SupplierBalanceAsync(supplier));
        await DeviceKit.AssertConsistentAsync(owner);

        // The same supplier invoice number is not booked twice (spaces and case ignored); nothing is left to match anyway.
        await Problem(await InvoiceRawAsync(owner, kit.Branch, supplier, " " + h.GetProperty("supplierInvoiceNo").GetString()!.ToLowerInvariant() + " ", [Goods(ReceiptLine(gr, 0), 1, 51m)]),
            HttpStatusCode.Conflict, "PUR-008");
        await Problem(await InvoiceRawAsync(owner, kit.Branch, supplier, "INV-" + Suffix(), [Goods(ReceiptLine(gr, 0), 1, 51m)]), HttpStatusCode.Conflict, "PUR-002");
        // A posted invoice is not cancelled or changed.
        await Problem(await owner.PostAsync($"/api/v1/purchasing/invoices/{inv}/cancel", null), HttpStatusCode.Conflict, "PUR-003");

        // The rest of the cases come and the order is received in full; invoiced at 49.500 (within tolerance), the 2.000 less goes to the stock on hand.
        var gr2 = await ReceiveAsync(owner, kit.Branch, supplier, po, [new { poLineId = PoLine(order, 0), qty = 4m }]);
        Assert.Equal("received", (await Ok(await owner.GetAsync($"/api/v1/purchasing/orders/{po}"))).GetProperty("order").GetProperty("status").GetString());
        var inv2 = Guid.CreateVersion7();
        var d2 = await Ok(await InvoiceRawAsync(owner, kit.Branch, supplier, "INV-" + Suffix(), [Goods(ReceiptLine(gr2, 0), 4, 49.5m)], inv2));
        Assert.Equal("matched", d2.GetProperty("invoice").GetProperty("status").GetString());
        var p2 = await PostInvoiceAsync(owner, inv2);
        Assert.Equal(-2m, p2.GetProperty("lines")[0].GetProperty("varianceInventory").GetDecimal());
        Assert.Equal(await P.GrniOpenAsync(kit.Branch), -await P.GlAsync("2110", kit.Branch));   // the goods-received ledger is the 2110 balance

        // The last price paid, for the next order.
        var ph = await Ok(await owner.GetAsync($"/api/v1/purchasing/price-history/{cases.VariantId}"));
        Assert.Equal(50m, ph.GetProperty("last").GetProperty("unitPrice").GetDecimal());
    }

    [PgFact]
    public async Task Returns_debit_notes_and_payments_settle_the_supplier_with_head_office_paying_a_branch()
    {
        using var owner = await kit.OwnerAsync();
        await EnableAsync(owner);
        var credit = new CreditKit(fx, kit);
        var (branch2, pos2) = await credit.SecondBranchAsync(owner);
        using var _ = pos2;
        var supplier = await SupplierAsync(owner, "Muscat Parts", terms: 15);
        var cases = await P.AccessoryAsync(owner, "Charger");
        var phone = await P.PhoneAsync(owner, "Handset");

        // Head office receives 10 chargers at 10 and 2 phones at 100 without an order; branch 2 receives 5 chargers at 10.
        var gr = await ReceiveAsync(owner, kit.Branch, supplier, null,
            [new { variantId = cases.VariantId, qty = 10m, unitPrice = 10m }, new { variantId = phone.VariantId, qty = 2m, unitPrice = 100m, units = Units(2) }]);
        var gr2 = await ReceiveAsync(owner, branch2, supplier, null, [new { variantId = cases.VariantId, qty = 5m, unitPrice = 10m }]);
        var phoneUnits = gr.GetProperty("lines")[1].GetProperty("units").EnumerateArray().Select(u => u.GetProperty("id").GetGuid()).ToArray();

        // Before any invoice: 3 chargers and a phone go back out of goods received not invoiced; no debit note, nothing owed changes.
        var ret1 = await Ok(await owner.PostAsJsonAsync("/api/v1/purchasing/returns", new
        {
            id = Guid.CreateVersion7(), branchId = kit.Branch, supplierId = supplier, reason = "Damaged in transit",
            lines = new object[] { new { receiptLineId = ReceiptLine(gr, 0), qty = 3m }, new { receiptLineId = ReceiptLine(gr, 1), qty = 1m, deviceUnitIds = new[] { phoneUnits[0] } } },
        }));
        Assert.Equal(130m, ret1.GetProperty("supplierReturn").GetProperty("netTotal").GetDecimal());
        Assert.Equal(0m, ret1.GetProperty("supplierReturn").GetProperty("taxTotal").GetDecimal());
        Assert.Equal(0m, await P.SupplierBalanceAsync(supplier));
        Assert.Equal("supplier_returned", await DeviceKit.StatusAsync(owner, phoneUnits[0]));
        // Only 7 chargers are open to invoice now.
        await Problem(await InvoiceRawAsync(owner, kit.Branch, supplier, "MP-" + Suffix(), [Goods(ReceiptLine(gr, 0), 8, 10m)]), HttpStatusCode.Conflict, "PUR-002");

        // Head office books one invoice for its own goods and branch 2's: branch 2 clears its goods received and owes head office.
        var inv = Guid.CreateVersion7();
        await Ok(await InvoiceRawAsync(owner, kit.Branch, supplier, "MP-" + Suffix(),
            [Goods(ReceiptLine(gr, 0), 7, 10m), Goods(ReceiptLine(gr, 1), 1, 100m), Goods(ReceiptLine(gr2, 0), 5, 10m)], inv));
        var due2Before = await P.GlAsync("2500", branch2);
        var due1Before = await P.GlAsync("1310", kit.Branch);
        await PostInvoiceAsync(owner, inv);
        Assert.Equal(-50m, await P.GlAsync("2500", branch2) - due2Before);
        Assert.Equal(50m, await P.GlAsync("1310", kit.Branch) - due1Before);
        Assert.Equal(0m, await P.GrniOpenAsync(branch2));
        Assert.Equal(0m, await P.GlAsync("2110", branch2));
        Assert.Equal(231m, await P.SupplierBalanceAsync(supplier));   // (70 + 100 + 50) + 5% VAT

        // After the invoice, 2 chargers go back on a debit note at the invoice price with VAT: 21 off what is owed, set against the invoice.
        var ret2 = await Ok(await owner.PostAsJsonAsync("/api/v1/purchasing/returns", new
        {
            id = Guid.CreateVersion7(), branchId = kit.Branch, supplierId = supplier, reason = "Faulty",
            lines = new object[] { new { receiptLineId = ReceiptLine(gr, 0), qty = 2m } },
        }));
        Assert.Equal(1m, ret2.GetProperty("supplierReturn").GetProperty("taxTotal").GetDecimal());
        Assert.Equal(210m, await P.SupplierBalanceAsync(supplier));
        var items = (await Ok(await owner.GetAsync($"/api/v1/purchasing/suppliers/{supplier}/open-items"))).EnumerateArray().ToArray();
        Assert.Single(items);
        Assert.Equal(210m, items[0].GetProperty("open").GetDecimal());
        // More than was received, counting what went back, does not go back.
        await Problem(await owner.PostAsJsonAsync("/api/v1/purchasing/returns", new
        {
            id = Guid.CreateVersion7(), branchId = kit.Branch, supplierId = supplier, reason = "Too many",
            lines = new object[] { new { receiptLineId = ReceiptLine(gr, 0), qty = 6m } },
        }), HttpStatusCode.Conflict, "PUR-002");

        // Branch 2 receives and is invoiced on its own: 40 + 2 VAT. Head office pays 100 by transfer, part of it on branch 2's invoice.
        var gr3 = await ReceiveAsync(owner, branch2, supplier, null, [new { variantId = cases.VariantId, qty = 4m, unitPrice = 10m }]);
        var inv3 = Guid.CreateVersion7();
        await Ok(await InvoiceRawAsync(owner, branch2, supplier, "MP-" + Suffix(), [Goods(ReceiptLine(gr3, 0), 4, 10m)], inv3));
        await PostInvoiceAsync(owner, inv3);
        var open = (await Ok(await owner.GetAsync($"/api/v1/purchasing/suppliers/{supplier}/open-items"))).EnumerateArray().ToArray();
        var b2Entry = open.Single(o => o.GetProperty("branchId").GetGuid() == branch2).GetProperty("id").GetGuid();
        var hoEntry = open.Single(o => o.GetProperty("branchId").GetGuid() == kit.Branch).GetProperty("id").GetGuid();
        await Problem(await owner.PostAsJsonAsync("/api/v1/purchasing/payments", new
        {
            id = Guid.CreateVersion7(), branchId = kit.Branch, supplierId = supplier, method = "bank_transfer", amount = 100m,
        }), HttpStatusCode.BadRequest, "VAL-001");      // a transfer needs its reference
        var due2 = await P.GlAsync("2500", branch2);
        var bank = await P.GlAsync("1200", kit.Branch);
        var pay = await Ok(await owner.PostAsJsonAsync("/api/v1/purchasing/payments", new
        {
            id = Guid.CreateVersion7(), branchId = kit.Branch, supplierId = supplier, method = "bank_transfer", amount = 100m, reference = "TRF-" + Suffix(),
            allocations = new[] { new { entryId = b2Entry, amount = 42m }, new { entryId = hoEntry, amount = 50m } },
        }));
        Assert.Equal(2, pay.GetProperty("allocations").GetArrayLength());
        Assert.Equal(-100m, await P.GlAsync("1200", kit.Branch) - bank);
        Assert.Equal(-42m, await P.GlAsync("2500", branch2) - due2);        // head office paid branch 2's invoice: branch 2 owes head office 42 more
        Assert.Equal(152m, await P.SupplierBalanceAsync(supplier));         // 210 + 42 - 100
        Assert.Equal(-152m, await P.GlAsync("2100", party: supplier));
        // The 8 not allocated is an advance at head office; when the next head office invoice is posted it settles the oldest open invoice there.
        var left = (await Ok(await owner.GetAsync($"/api/v1/purchasing/suppliers/{supplier}/open-items"))).EnumerateArray().Sum(o => o.GetProperty("open").GetDecimal());
        Assert.Equal(160m, left);
        var gr4 = await ReceiveAsync(owner, kit.Branch, supplier, null, [new { variantId = cases.VariantId, qty = 1m, unitPrice = 10m }]);
        var inv4 = Guid.CreateVersion7();
        await Ok(await InvoiceRawAsync(owner, kit.Branch, supplier, "MP-" + Suffix(), [Goods(ReceiptLine(gr4, 0), 1, 10m)], inv4));
        await PostInvoiceAsync(owner, inv4);
        Assert.Equal(0m, (await Ok(await owner.GetAsync($"/api/v1/purchasing/invoices/{inv4}"))).GetProperty("invoice").GetProperty("paid").GetDecimal());
        Assert.Equal([152m, 10.5m], (await Ok(await owner.GetAsync($"/api/v1/purchasing/suppliers/{supplier}/open-items"))).EnumerateArray()
            .Select(o => o.GetProperty("open").GetDecimal()).ToArray());
        // The supplier's ageing shows what is open after allocations.
        var aging = (await Ok(await owner.GetAsync("/api/v1/suppliers/reports/aging"))).EnumerateArray().Single(a => a.GetProperty("supplierId").GetGuid() == supplier);
        Assert.Equal(162.5m, aging.GetProperty("total").GetDecimal());   // 152 + 10.5
        Assert.Empty(await ReconcileAsync(owner));
    }

    [PgFact]
    public async Task Manual_journals_need_a_second_approver_above_the_threshold_and_control_accounts_need_their_permission()
    {
        using var owner = await kit.OwnerAsync();
        await EnableAsync(owner);
        var acc = await kit.UserAsync(owner, "p11acc", "accountant");
        using var accountant = fx.Client(await fx.TokenAsync(acc.Username, acc.Password));
        var roleCode = "bookkeeper_" + Suffix().ToLowerInvariant();
        await Ok(await owner.PostAsJsonAsync("/api/v1/roles", new { code = roleCode, nameEn = "Bookkeeper", nameAr = "مسك الدفاتر", permissions = new[] { "ACCT.VIEW", "ACCT.JOURNAL.MANUAL" } }),
            HttpStatusCode.Created);
        var bk = await kit.UserAsync(owner, "p11bk", roleCode);
        using var bookkeeper = fx.Client(await fx.TokenAsync(bk.Username, bk.Password));
        var rent = await AccountIdAsync(owner, "6100");
        var bankAcc = await AccountIdAsync(owner, "1200");
        var payables = await AccountIdAsync(owner, "2100");

        // Unbalanced: refused. Within the 1000 threshold: posted at once.
        var j1 = Guid.CreateVersion7();
        await Ok(await bookkeeper.PostAsJsonAsync("/api/v1/accounting/manual-journals", new
        {
            id = j1, branchId = kit.Branch, journalDate = Today, description = "Rent accrual",
            lines = new object[] { new { accountId = rent, branchId = kit.Branch, debit = 300m, credit = 0m }, new { accountId = bankAcc, branchId = kit.Branch, debit = 0m, credit = 299m } },
        }));
        await Problem(await bookkeeper.PostAsync($"/api/v1/accounting/manual-journals/{j1}/submit", null), HttpStatusCode.BadRequest, "ACC-002");
        await Ok(await bookkeeper.PutAsJsonAsync($"/api/v1/accounting/manual-journals/{j1}", new
        {
            id = j1, branchId = kit.Branch, journalDate = Today, description = "Rent",
            lines = new object[] { new { accountId = rent, branchId = kit.Branch, debit = 300m, credit = 0m }, new { accountId = bankAcc, branchId = kit.Branch, debit = 0m, credit = 300m } },
        }));
        var m1 = await Ok(await bookkeeper.PostAsync($"/api/v1/accounting/manual-journals/{j1}/submit", null));
        Assert.Equal("posted", m1.GetProperty("journal").GetProperty("status").GetString());

        // A control account (supplier payables) by hand: not for the bookkeeper.
        var j2 = Guid.CreateVersion7();
        await Ok(await bookkeeper.PostAsJsonAsync("/api/v1/accounting/manual-journals", new
        {
            id = j2, branchId = kit.Branch, journalDate = Today, description = "Payables fix",
            lines = new object[] { new { accountId = payables, branchId = kit.Branch, debit = 10m, credit = 0m }, new { accountId = bankAcc, branchId = kit.Branch, debit = 0m, credit = 10m } },
        }));
        await Problem(await bookkeeper.PostAsync($"/api/v1/accounting/manual-journals/{j2}/submit", null), HttpStatusCode.Forbidden, "ACC-004");

        // Above 1000: waits for someone else to approve; the accountant who entered it cannot.
        var j3 = Guid.CreateVersion7();
        await Ok(await accountant.PostAsJsonAsync("/api/v1/accounting/manual-journals", new
        {
            id = j3, branchId = kit.Branch, journalDate = Today, description = "Owner capital",
            lines = new object[] { new { accountId = bankAcc, branchId = kit.Branch, debit = 5000m, credit = 0m }, new { accountId = await AccountIdAsync(owner, "3100"), branchId = kit.Branch, debit = 0m, credit = 5000m } },
        }));
        Assert.Equal("pending_approval", (await Ok(await accountant.PostAsync($"/api/v1/accounting/manual-journals/{j3}/submit", null))).GetProperty("journal").GetProperty("status").GetString());
        await Problem(await accountant.PostAsync($"/api/v1/accounting/manual-journals/{j3}/approve", null), HttpStatusCode.Forbidden, "ACC-005");
        var bankBefore = await P.GlAsync("1200", kit.Branch);
        var m3 = await Ok(await owner.PostAsync($"/api/v1/accounting/manual-journals/{j3}/approve", null));
        Assert.Equal("posted", m3.GetProperty("journal").GetProperty("status").GetString());
        Assert.Equal(5000m, await P.GlAsync("1200", kit.Branch) - bankBefore);
        // Corrected by its reversal, never edited.
        await Problem(await accountant.PutAsJsonAsync($"/api/v1/accounting/manual-journals/{j3}", new
        {
            id = j3, branchId = kit.Branch, journalDate = Today, description = "x",
            lines = new object[] { new { accountId = bankAcc, branchId = kit.Branch, debit = 1m, credit = 0m }, new { accountId = rent, branchId = kit.Branch, debit = 0m, credit = 1m } },
        }), HttpStatusCode.Conflict, "ACC-003");
        var r3 = await Ok(await accountant.PostAsJsonAsync($"/api/v1/accounting/manual-journals/{j3}/reverse", new { reason = "Entered in the wrong branch" }));
        Assert.NotNull(r3.GetProperty("journal").GetProperty("reversalNumber").GetString());
        Assert.Equal(0m, await P.GlAsync("1200", kit.Branch) - bankBefore);
        await Problem(await accountant.PostAsJsonAsync($"/api/v1/accounting/manual-journals/{j3}/reverse", new { reason = "again" }), HttpStatusCode.Conflict, "ACC-003");
        var jr = await Ok(await owner.GetAsync($"/api/v1/accounting/journals/{m3.GetProperty("journal").GetProperty("journalId").GetGuid()}"));
        Assert.Equal(r3.GetProperty("journal").GetProperty("reversalNumber").GetString(), jr.GetProperty("journal").GetProperty("reversedByNumber").GetString());
    }

    [PgFact]
    public async Task Periods_close_by_branch_late_documents_move_on_and_vat_returns_file_from_closed_months()
    {
        using var owner = await kit.OwnerAsync();
        await EnableAsync(owner);
        var credit = new CreditKit(fx, kit);
        var (branch2, pos2) = await credit.SecondBranchAsync(owner);
        using var _ = pos2;
        var vatStd = await kit.TaxCodeAsync(owner);
        var general = await AccountIdAsync(owner, "6900");
        var fixedAssets = await AccountIdAsync(owner, "1600");
        // 2024 is a year no other test posts in.
        await Ok(await ExpenseRawAsync(owner, kit.Branch, general, 100m, new DateOnly(2024, 1, 10), taxCode: vatStd));
        await Ok(await ExpenseRawAsync(owner, branch2, general, 40m, new DateOnly(2024, 2, 5), taxCode: vatStd, paidBy: "bank_transfer"));
        await Ok(await ExpenseRawAsync(owner, kit.Branch, fixedAssets, 1000m, new DateOnly(2024, 3, 3), taxCode: vatStd, paidBy: "bank_transfer"));

        // January closes at head office's branch only: a January expense there moves to February; at branch 2 it stays in January.
        await PeriodStatusAsync(owner, 2024, 1, "closed", kit.Branch);
        var e1 = await Ok(await ExpenseRawAsync(owner, kit.Branch, general, 10m, new DateOnly(2024, 1, 20), taxCode: vatStd));
        var e2 = await Ok(await ExpenseRawAsync(owner, branch2, general, 10m, new DateOnly(2024, 1, 20), taxCode: vatStd));
        Assert.Equal("2024-02-01", await fx.QueryDbAsync<string>($"SELECT j.journal_date::text FROM acct.journal j JOIN exp.expense e ON e.journal_id = j.id WHERE e.id = '{e1.GetProperty("id").GetGuid()}'"));
        Assert.Equal("2024-01-20", await fx.QueryDbAsync<string>($"SELECT j.journal_date::text FROM acct.journal j JOIN exp.expense e ON e.journal_id = j.id WHERE e.id = '{e2.GetProperty("id").GetGuid()}'"));
        var periods = await PeriodsAsync(owner, 2024);
        var jan = periods.GetProperty("periods")[0];
        Assert.Contains("\"closed\"", jan.GetProperty("branches").GetString());
        Assert.Equal("open", jan.GetProperty("businessStatus").GetString());

        // The VAT return for the first quarter: not filed while a month is open somewhere.
        await Ok(await owner.PostAsync("/api/v1/accounting/vat/boxes/review", null), HttpStatusCode.NoContent);
        var vr = await Ok(await owner.PostAsJsonAsync("/api/v1/accounting/vat/returns", new { periodFrom = new DateOnly(2024, 1, 1), periodTo = new DateOnly(2024, 3, 31) }));
        var vrId = vr.GetProperty("vatReturn").GetProperty("id").GetGuid();
        var boxes = vr.GetProperty("vatReturn").GetProperty("boxes");
        Assert.Equal(8m, boxes.GetProperty("6a").GetProperty("vat").GetDecimal());      // (100 + 40 + 10 + 10) * 5%: both late expenses' journals fall in the quarter
        Assert.Equal(50m, boxes.GetProperty("6c").GetProperty("vat").GetDecimal());     // the fixed asset
        Assert.Equal(-58m, boxes.GetProperty("7").GetProperty("vat").GetDecimal());     // a refund position
        Assert.True(vr.GetProperty("agrees").GetBoolean(), vr.GetProperty("check").GetRawText());
        await Problem(await owner.PostAsJsonAsync($"/api/v1/accounting/vat/returns/{vrId}/file", new { }), HttpStatusCode.Conflict, "ACC-007");
        foreach (var m in new[] { 1, 2, 3 }) await PeriodStatusAsync(owner, 2024, m, "soft_closed");
        var input = await P.GlAsync("1500");
        var filed = await Ok(await owner.PostAsJsonAsync($"/api/v1/accounting/vat/returns/{vrId}/file", new { filingReference = "OTA-" + Suffix() }));
        Assert.Equal("filed", filed.GetProperty("vatReturn").GetProperty("status").GetString());
        Assert.Equal(-58m, await P.GlAsync("1500") - input);                   // VAT input of the quarter moved to VAT payable, branch by branch
        Assert.Equal(-58m, await fx.QueryDbAsync<decimal>($"""
            SELECT coalesce(sum(l.credit - l.debit), 0) FROM acct.journal_line l JOIN acct.account a ON a.id = l.account_id JOIN acct.journal j ON j.id = l.journal_id
             WHERE a.code = '2210' AND j.source_doc_id = '{vrId}'
            """));
        // A filed return keeps its figures: its months do not reopen, and late documents for them move on.
        await Problem(await PeriodStatusRawAsync(owner, 2024, 2, "open"), HttpStatusCode.Conflict, "ACC-006");
        var late = await Ok(await ExpenseRawAsync(owner, branch2, general, 20m, new DateOnly(2024, 2, 14), taxCode: vatStd));
        Assert.Equal("2024-04-01", await fx.QueryDbAsync<string>($"SELECT j.journal_date::text FROM acct.journal j JOIN exp.expense e ON e.journal_id = j.id WHERE e.id = '{late.GetProperty("id").GetGuid()}'"));
        // The accountant may still post into a soft-closed month by hand.
        var adj = Guid.CreateVersion7();
        await Ok(await owner.PostAsJsonAsync("/api/v1/accounting/manual-journals", new
        {
            id = adj, branchId = kit.Branch, journalDate = new DateOnly(2024, 3, 31), description = "Accrued electricity", intoSoftClosed = true,
            lines = new object[] { new { accountId = general, branchId = kit.Branch, debit = 15m, credit = 0m }, new { accountId = await AccountIdAsync(owner, "1100"), branchId = kit.Branch, debit = 0m, credit = 15m } },
        }));
        var mj = await Ok(await owner.PostAsync($"/api/v1/accounting/manual-journals/{adj}/submit", null));
        Assert.Equal("2024-03-31", (await Ok(await owner.GetAsync($"/api/v1/accounting/journals/{mj.GetProperty("journal").GetProperty("journalId").GetGuid()}")))
            .GetProperty("journal").GetProperty("journalDate").GetString());
        // A refund position: the payment posts the other way.
        var paid = await Ok(await owner.PostAsJsonAsync($"/api/v1/accounting/vat/returns/{vrId}/pay", new { reference = "REFUND-1" }));
        Assert.NotNull(paid.GetProperty("vatReturn").GetProperty("paymentNumber").GetString());

        // Statements for the quarter balance; the year closes into retained earnings once every period is closed.
        var tb = await Ok(await owner.GetAsync("/api/v1/accounting/reports/trial-balance?from=2024-01-01&to=2024-12-31"));
        Assert.True(tb.GetProperty("balanced").GetBoolean());
        var pl = await Ok(await owner.GetAsync("/api/v1/accounting/reports/profit-and-loss?from=2024-01-01&to=2024-12-31"));
        Assert.Equal(-(100m + 40m + 10m + 10m + 20m + 15m), pl.GetProperty("netProfit").GetDecimal());
        await Problem(await owner.PostAsJsonAsync("/api/v1/accounting/year-end", new { fiscalYear = 2024 }), HttpStatusCode.Conflict, "ACC-008");
        foreach (var m in Enumerable.Range(1, 12)) await PeriodStatusAsync(owner, 2024, m, "closed");
        var re = await P.GlAsync("3300");
        var ye = await Ok(await owner.PostAsJsonAsync("/api/v1/accounting/year-end", new { fiscalYear = 2024 }));
        Assert.Equal(-195m, ye.GetProperty("profit").GetDecimal());
        Assert.Equal(195m, await P.GlAsync("3300") - re);
        await Problem(await PeriodStatusRawAsync(owner, 2024, 6, "open"), HttpStatusCode.Conflict, "ACC-008");
        await Problem(await owner.PostAsJsonAsync("/api/v1/accounting/year-end", new { fiscalYear = 2024 }), HttpStatusCode.Conflict, "ACC-008");
        var pl2 = await Ok(await owner.GetAsync("/api/v1/accounting/reports/profit-and-loss?from=2024-01-01&to=2024-12-31"));
        Assert.Equal(-195m, pl2.GetProperty("netProfit").GetDecimal());   // the close is not part of the year's result
    }
}
