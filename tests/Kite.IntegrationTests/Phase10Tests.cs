using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kite.Host.Infrastructure;
using Kite.Sales;
using Xunit;
using static Kite.IntegrationTests.CreditKit;
using static Kite.IntegrationTests.PosKit;

namespace Kite.IntegrationTests;

/// <summary>
/// Phase 10 (blueprint 14, 16.1): sales on account within the limit, with a manager's approval up to the override cap, refused
/// on hold or past the cap; returns on account; instalments; payments at any branch settling the oldest instalments first with
/// interbranch journals; write-offs; statements that equal the ledger and receivables; branch servers that reserve online,
/// push what they sold offline and are put on hold when head office finds a breach; suppliers kept by head office, provisional
/// ones from branches approved or merged, opening balances and the supplier ledger against payables.
/// </summary>
[Collection("server")]
public sealed class Phase10Tests(KiteServerFixture fx)
{
    private readonly PosKit kit = new(fx);
    private CreditKit Credit => new(fx, kit);

    private async Task<decimal> ArByPartyAsync(Guid customer) =>
        await fx.QueryDbAsync<decimal>($"""
            SELECT coalesce(sum(jl.debit - jl.credit), 0) FROM acct.journal_line jl JOIN acct.account a ON a.id = jl.account_id
             WHERE a.code = '1300' AND jl.party_id = '{customer}'
            """);

    private async Task<decimal> LedgerAsync(Guid customer) =>
        await fx.QueryDbAsync<decimal>($"SELECT coalesce(sum(amount), 0) FROM crm.credit_ledger WHERE customer_id = '{customer}' AND balance_type = 'receivable'");

    [PgFact]
    public async Task Sales_on_account_follow_the_limit_with_approvals_holds_and_returns_on_account()
    {
        using var owner = await kit.OwnerAsync();
        await EnableAsync(owner);
        await kit.SetPolicyAsync(owner, cashRounding: 0m);
        var unit = await Credit.UnitServiceAsync(owner);
        var term = await kit.TerminalAsync(owner);
        var cashier = await kit.UserAsync(owner, "credcash", "senior_cashier");
        var manager = await kit.UserAsync(owner, "credmgr", "branch_manager", pin: "4826");
        using var pos = await kit.PosAsync(cashier.Username, cashier.Password, term);
        await OpenShiftAsync(pos, 20m);
        var customer = await TradeInKit.CustomerAsync(pos, "Credit " + Suffix());

        // No account: no credit. Then head office opens one: 100 limit, 30 days.
        await Problem(await SellOnAccountAsync(pos, unit, 10m, customer), HttpStatusCode.Conflict, "CRD-001");
        await AccountAsync(owner, customer, 100m);
        await Problem(await owner.PostAsJsonAsync("/api/v1/credit/accounts", new { customerId = customer, creditLimit = 50m }), HttpStatusCode.Conflict, "GEN-409");

        var first = Guid.CreateVersion7();
        var s1 = await Ok(await SellOnAccountAsync(pos, unit, 60m, customer, id: first));
        Assert.Equal(60m, s1.GetProperty("gross").GetDecimal());
        var d = await DetailAsync(owner, customer);
        Assert.Equal(60m, Fig(d, "exposure"));
        Assert.Equal(40m, Fig(d, "available"));
        var due = d.GetProperty("openItems")[0].GetProperty("dueDate").GetString();
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow.AddHours(4)).AddDays(30).ToString("yyyy-MM-dd"), due);

        // 50 more is 10 over: a manager's approval (a PIN at the till) lets it through; 60 over is past the 50 cap even with one.
        await Problem(await SellOnAccountAsync(pos, unit, 50m, customer), HttpStatusCode.Forbidden, "CRD-004");
        var approval = await ApprovalAsync(pos, manager.Username, "4826");
        await Ok(await SellOnAccountAsync(pos, unit, 50m, customer, approval));
        Assert.Equal(110m, Fig(await DetailAsync(owner, customer), "exposure"));
        await Problem(await SellOnAccountAsync(pos, unit, 50.001m, customer, await ApprovalAsync(pos, manager.Username, "4826")), HttpStatusCode.Conflict, "CRD-003");
        Assert.Equal(1L, await fx.QueryDbAsync<long>($"SELECT count(*) FROM crm.credit_decision WHERE customer_id = '{customer}' AND over_by = 10 AND approved_by = '{manager.Id}'"));

        // Part cash, part on account; the cashier's permission is POS.CREDIT_SALE (a plain cashier does not have it).
        var plain = await kit.UserAsync(owner, "credplain", "cashier");
        using (var p2 = await kit.PosAsync(plain.Username, plain.Password, await kit.TerminalAsync(owner)))
        {
            await OpenShiftAsync(p2, 10m);
            await Problem(await SellOnAccountAsync(p2, unit, 5m, customer), HttpStatusCode.Forbidden, "AUTH-403");
        }

        // A manual hold stops new credit at once; payments still go in; release brings it back.
        await Ok(await owner.PostAsJsonAsync($"/api/v1/credit/accounts/{customer}/hold", new { reason = "Cheque bounced" }), HttpStatusCode.NoContent);
        await Problem(await SellOnAccountAsync(pos, unit, 1m, customer, await ApprovalAsync(pos, manager.Username, "4826")), HttpStatusCode.Conflict, "CRD-002");
        var pay = await Ok(await pos.PostAsJsonAsync("/api/v1/credit/payments", new { id = Guid.CreateVersion7(), customerId = customer, method = "cash", amount = 30m }));
        Assert.Equal(80m, pay.GetProperty("balance").GetDecimal());
        Assert.Equal(30m, pay.GetProperty("allocated").GetDecimal());
        await Ok(await owner.PostAsJsonAsync($"/api/v1/credit/accounts/{customer}/release", new { reason = "Paid in cash" }), HttpStatusCode.NoContent);
        Assert.Equal("active", (await DetailAsync(owner, customer)).GetProperty("account").GetProperty("status").GetString());

        // A return goes back on account at most up to what the sale charged on account.
        var sale = await pos.GetFromJsonAsync<JsonElement>($"/api/v1/sales/{first}");
        var line = sale.GetProperty("lines")[0].GetProperty("id").GetGuid();
        var cashSale = Guid.CreateVersion7();
        await SellAsync(pos, [Line(unit, 5m)], [Cash(5m)], cashSale, customerId: customer);
        var cashLine = (await pos.GetFromJsonAsync<JsonElement>($"/api/v1/sales/{cashSale}")).GetProperty("lines")[0].GetProperty("id").GetGuid();
        await Problem(await pos.PostAsJsonAsync($"/api/v1/sales/{cashSale}/return", new
        {
            id = Guid.CreateVersion7(), lines = new[] { new { lineId = cashLine, qty = 5m } }, refunds = new[] { OnAccount(5m) }, reason = "changed mind",
        }), HttpStatusCode.BadRequest, "CRD-009");      // nothing on that sale was charged on account
        await Ok(await pos.PostAsJsonAsync($"/api/v1/sales/{first}/return", new
        {
            id = Guid.CreateVersion7(), lines = new[] { new { lineId = line, qty = 20m } }, refunds = new[] { OnAccount(20m) }, reason = "wrong item",
        }));
        Assert.Equal(60m, Fig(await DetailAsync(owner, customer), "balance"));

        // The statement is the ledger, and the ledger is receivables (1300) with the customer as the party.
        var st = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/credit/customers/{customer}/statement");
        Assert.Equal(60m, st.GetProperty("closing").GetDecimal());
        Assert.Equal(st.GetProperty("opening").GetDecimal() + st.GetProperty("debits").GetDecimal() - st.GetProperty("credits").GetDecimal(), st.GetProperty("closing").GetDecimal());
        Assert.Equal(60m, await LedgerAsync(customer));
        Assert.Equal(60m, await ArByPartyAsync(customer));
        var pdf = await owner.GetAsync($"/api/v1/credit/customers/{customer}/statement.pdf");
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        Assert.True((await pdf.Content.ReadAsByteArrayAsync()).Length > 1000);

        // The till's view, and the Z figures: on-account sales and the payment on this drawer.
        var av = await pos.GetFromJsonAsync<JsonElement>($"/api/v1/credit/customers/{customer}/available");
        Assert.Equal(40m, av.GetProperty("available").GetDecimal());
        var shift = await pos.GetFromJsonAsync<JsonElement>("/api/v1/pos/shifts/current");
        Assert.Equal(90m, shift.GetProperty("figures").GetProperty("onAccountSales").GetDecimal());   // 60 + 50 less the 20 returned on account
        Assert.Equal(30m, shift.GetProperty("figures").GetProperty("creditReceipts").GetDecimal());
    }

    [PgFact]
    public async Task Instalments_are_paid_at_any_branch_oldest_first_with_interbranch_journals_then_written_off()
    {
        using var owner = await kit.OwnerAsync();
        await EnableAsync(owner);
        await kit.SetPolicyAsync(owner, cashRounding: 0m);
        var unit = await Credit.UnitServiceAsync(owner);
        using var pos = await kit.OwnerPosAsync(await kit.TerminalAsync(owner));
        await OpenShiftAsync(pos, 20m);
        var (branch2, pos2) = await Credit.SecondBranchAsync(owner);
        using var _ = pos2;
        var customer = await TradeInKit.CustomerAsync(pos, "Instalments " + Suffix());
        await AccountAsync(owner, customer, 1000m, 30, (kit.Branch, 600m), (branch2, 400m));

        // 300 in three monthly instalments; 90 more, due in 30 days.
        await Ok(await SellOnAccountAsync(pos, unit, 300m, customer, plan: new { instalments = 3, intervalDays = 30 }));
        await Ok(await SellOnAccountAsync(pos, unit, 90m, customer));
        var items = (await DetailAsync(owner, customer)).GetProperty("openItems").EnumerateArray().Select(i => (i.GetProperty("dueDate").GetString(), i.GetProperty("open").GetDecimal())).ToList();
        Assert.Equal(4, items.Count);
        Assert.Equal([100m, 90m, 100m, 100m], items.Select(i => i.Item2).ToArray());     // ordered by due date

        // 230 paid at the other branch: the two instalments due first (100 + 90) and 40 of the next.
        var receipt = Guid.CreateVersion7();
        var pay = await Ok(await pos2.PostAsJsonAsync("/api/v1/credit/payments", new { id = receipt, customerId = customer, method = "cash", amount = 230m }));
        Assert.Equal(230m, pay.GetProperty("allocated").GetDecimal());
        var left = (await DetailAsync(owner, customer)).GetProperty("openItems").EnumerateArray().Select(i => i.GetProperty("open").GetDecimal()).ToArray();
        Assert.Equal([60m, 100m], left);
        // The receivable moved: the paying branch owes the selling branch (2500), which is owed by it (1310); each branch balances.
        var lines = await fx.QueryDbAsync<string>($"""
            SELECT string_agg(a.code || ':' || CASE WHEN jl.branch_id = '{branch2}' THEN 'B2' ELSE 'B1' END || ':' || (jl.debit - jl.credit)::text, ' ' ORDER BY a.code, jl.branch_id)
              FROM acct.journal j JOIN acct.journal_line jl ON jl.journal_id = j.id JOIN acct.account a ON a.id = jl.account_id
             WHERE j.source_doc_id = '{receipt}' AND j.posting_kind = 'credit.interbranch'
            """);
        Assert.Equal(["1300:B1:-230.000", "1300:B2:230.000", "1310:B1:230.000", "2500:B2:-230.000"], lines.Split(' ').OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Equal(0m, await fx.QueryDbAsync<decimal>($"SELECT coalesce(sum(amount), 0) FROM crm.credit_ledger WHERE customer_id = '{customer}' AND kind = 'transfer'"));
        // Per branch, the ledger equals receivables: B2 took 230 and passed it on (0); B1 is owed 160.
        Assert.Equal(160m, await fx.QueryDbAsync<decimal>($"SELECT sum(amount) FROM crm.credit_ledger WHERE customer_id = '{customer}' AND balance_type = 'receivable' AND branch_id = '{kit.Branch}'"));
        Assert.Equal(0m, await fx.QueryDbAsync<decimal>($"SELECT sum(amount) FROM crm.credit_ledger WHERE customer_id = '{customer}' AND balance_type = 'receivable' AND branch_id = '{branch2}'"));
        var run = await Ok(await owner.PostAsync("/api/v1/credit/reconciliation/run", null));
        Assert.All(run.GetProperty("rows").EnumerateArray(), r => Assert.Equal(0m, r.GetProperty("difference").GetDecimal()));

        // An overpayment waits on account and settles the next sale at once.
        await Ok(await pos.PostAsJsonAsync("/api/v1/credit/payments", new { id = Guid.CreateVersion7(), customerId = customer, method = "bank_transfer", amount = 200m, reference = "TRF-1" }));
        var after = await DetailAsync(owner, customer);
        Assert.Equal(40m, Fig(after, "onAccount"));
        Assert.Equal(-40m, Fig(after, "balance"));
        await Ok(await SellOnAccountAsync(pos, unit, 25m, customer));
        after = await DetailAsync(owner, customer);
        Assert.Equal(15m, Fig(after, "onAccount"));
        Assert.Empty(after.GetProperty("openItems").EnumerateArray());

        // A back-office transfer names its branch; a write-off clears what is left, to bad debts.
        await Ok(await SellOnAccountAsync(pos, unit, 47.5m, customer));
        var wo = await Ok(await owner.PostAsJsonAsync($"/api/v1/credit/customers/{customer}/write-off", new { reason = "Customer left the country" }));
        Assert.Equal(32.5m, wo.GetProperty("total").GetDecimal());          // 47.5 less the 15 paid in advance
        Assert.Equal(0m, await LedgerAsync(customer));
        Assert.Equal(0m, await ArByPartyAsync(customer));
        Assert.Equal(32.5m, await fx.QueryDbAsync<decimal>($"""
            SELECT sum(jl.debit) FROM acct.journal j JOIN acct.journal_line jl ON jl.journal_id = j.id JOIN acct.account a ON a.id = jl.account_id
             WHERE a.code = '6500' AND j.source_doc_type = 'credit_write_off' AND j.source_doc_id = '{wo.GetProperty("id").GetGuid()}'
            """));
        var aging = await owner.GetFromJsonAsync<JsonElement>("/api/v1/credit/reports/aging");
        var row = aging.EnumerateArray().Single(r => r.GetProperty("customerId").GetGuid() == customer);
        Assert.Equal(0m, row.GetProperty("onAccount").GetDecimal());
        Assert.Equal(0m, row.GetProperty("total").GetDecimal());
    }

    [PgFact]
    public async Task Branch_servers_reserve_online_push_offline_sales_and_are_held_when_head_office_finds_a_breach()
    {
        using var owner = await kit.OwnerAsync();
        await EnableAsync(owner);
        await kit.SetPolicyAsync(owner, cashRounding: 0m);
        var unit = await Credit.UnitServiceAsync(owner);
        using var pos = await kit.OwnerPosAsync(await kit.TerminalAsync(owner));
        await OpenShiftAsync(pos, 20m);
        var b1 = await Credit.Central.RegisterBranchAsync(owner);
        var b2 = await Credit.Central.RegisterBranchAsync(owner);
        var customer = await TradeInKit.CustomerAsync(pos, "Offline " + Suffix());
        await AccountAsync(owner, customer, 500m, 30, (b1.BranchId, 300m), (b2.BranchId, 200m));
        await Ok(await SellOnAccountAsync(pos, unit, 100m, customer));          // 100 owed at head office

        // The heartbeat brings the account, this branch's allowance, the exposure and the policy.
        var hb = await Credit.HeartbeatAsync(b1);
        var snap = Snapshot(hb, customer)!.Value;
        Assert.Equal(300m, snap.GetProperty("allowance").GetDecimal());
        Assert.Equal(100m, snap.GetProperty("exposure").GetDecimal());
        Assert.Equal(100m, hb.GetProperty("creditPolicy").GetProperty("offlineSaleCap").GetDecimal());
        var seq = snap.GetProperty("seq").GetInt64();

        // Online: head office reserves; a second branch asking at the same time sees the reservation; the same sale asked again keeps it.
        var saleA = Guid.CreateVersion7();
        var a = await Credit.AuthoriseAsync(b1, customer, saleA, 350m);
        Assert.True(a.GetProperty("ok").GetBoolean());
        Assert.Equal(400m, a.GetProperty("available").GetDecimal());
        Assert.True((await Credit.AuthoriseAsync(b1, customer, saleA, 350m)).GetProperty("ok").GetBoolean());
        var b = await Credit.AuthoriseAsync(b2, customer, Guid.CreateVersion7(), 110m);
        Assert.Equal("over_cap", b.GetProperty("outcome").GetString());              // 50 left: 60 over is past the 50 a manager may approve
        var c = await Credit.AuthoriseAsync(b2, customer, Guid.CreateVersion7(), 80m);
        Assert.Equal("needs_approval", c.GetProperty("outcome").GetString());
        Assert.Equal(50m, c.GetProperty("available").GetDecimal());
        // The branch's charges head office has not seen count too.
        var d = await Credit.AuthoriseAsync(b2, customer, Guid.CreateVersion7(), 10m, unsynced: [new CreditUnsynced(Guid.CreateVersion7(), 45m)]);
        Assert.Equal(5m, d.GetProperty("available").GetDecimal());
        Assert.False(d.GetProperty("ok").GetBoolean());
        // The sale did not go through at the branch: the reservation is released.
        Assert.Equal(HttpStatusCode.NoContent, (HttpStatusCode)(await Credit.Central.SyncAsync(b1.Cert, "/sync/v1/credit/release", new CreditReleaseRequest(saleA))).Status);
        Assert.Equal(100m, await fx.QueryDbAsync<decimal>($"SELECT crm.credit_exposure('{customer}')"));

        // Offline: each branch sells within its own rule — the lower of its allowance and the limit less the last known 100 — and the
        // 100 per-sale cap. Each is right on its own; together they reach 600 against a 500 limit.
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(4));
        List<CreditEntryPush> Offline(CentralKit.Branch br, decimal allowance, int sales)
        {
            var list = new List<CreditEntryPush>();
            var since = 0m;
            for (var k = 0; k < sales; k++)
            {
                var available = CreditMath.AvailableOffline(allowance, 500m, 100m, since, 100m);
                Assert.True(available >= 100m, $"sale {k + 1} at {br.Code} fits offline ({available})");
                list.Add(Charge(br, customer, 100m, "offline", available, 500m, 100m + since, allowance, today));
                since += 100m;
            }
            Assert.Equal(0m, CreditMath.AvailableOffline(allowance, 500m, 100m, since, 100m));   // the allowance is used up
            return list;
        }
        var from1 = Offline(b1, 300m, 3);
        var from2 = Offline(b2, 200m, 2);
        var p1 = await Credit.PushAsync(b1, from1.ToArray());
        Assert.Equal(3, p1.GetProperty("acked").GetArrayLength());
        Assert.Equal("active", Snapshot(p1, customer)!.Value.GetProperty("status").GetString());   // 400 of 500: fine
        var p2 = await Credit.PushAsync(b2, from2.ToArray());
        var held = Snapshot(p2, customer)!.Value;
        Assert.Equal("hold", held.GetProperty("status").GetString());
        Assert.Equal("exposure", held.GetProperty("holdKind").GetString());
        Assert.Equal(600m, held.GetProperty("exposure").GetDecimal());
        Assert.Equal(1L, await fx.QueryDbAsync<long>($"SELECT count(*) FROM crm.credit_breach WHERE customer_id = '{customer}' AND exposure = 600 AND branch_id = '{b2.BranchId}'"));
        // Pushed again (a lost answer): nothing twice.
        Assert.Equal(2, (await Credit.PushAsync(b2, from2.ToArray())).GetProperty("acked").GetArrayLength());
        Assert.Equal(600m, await LedgerAsync(customer));

        // The hold reaches the other branch with its next heartbeat, and head office refuses it online too.
        var hb1 = await Credit.HeartbeatAsync(b1, seq);
        Assert.Equal("hold", Snapshot(hb1, customer)!.Value.GetProperty("status").GetString());
        Assert.Equal("hold", (await Credit.AuthoriseAsync(b1, customer, Guid.CreateVersion7(), 1m, approved: true)).GetProperty("outcome").GetString());
        await Problem(await SellOnAccountAsync(pos, unit, 1m, customer), HttpStatusCode.Conflict, "CRD-002");

        // A payment pushed from a branch brings it under the limit: the automatic hold lifts.
        var p3 = await Credit.PushAsync(b1, Payment(b1, customer, 150m, today));
        Assert.Equal("active", Snapshot(p3, customer)!.Value.GetProperty("status").GetString());
        var overrides = await owner.GetFromJsonAsync<JsonElement>("/api/v1/credit/reports/overrides");
        Assert.True(overrides.GetProperty("breaches").EnumerateArray().Any(r => r.GetProperty("exposure").GetDecimal() == 600m));
        Assert.True(overrides.GetProperty("decisions").EnumerateArray().Count(r => r.GetProperty("mode").GetString() == "offline") >= 5);

        await Credit.Central.DeactivateAsync(owner, b1);
        await Credit.Central.DeactivateAsync(owner, b2);
    }

    [PgFact]
    public async Task Suppliers_are_kept_by_head_office_and_provisional_ones_from_branches_are_approved_or_merged()
    {
        using var owner = await kit.OwnerAsync();
        await EnableAsync(owner);
        var s1 = await Ok(await owner.PostAsJsonAsync("/api/v1/suppliers", new { nameEn = "Gulf Parts " + Suffix(), nameAr = "قطع الخليج", termsDays = 45, phone = "+96824123456" }));
        Assert.Equal("active", s1.GetProperty("status").GetString());
        Assert.StartsWith("S-", s1.GetProperty("number").GetString());
        var id1 = s1.GetProperty("id").GetGuid();
        await Problem(await owner.PostAsJsonAsync("/api/v1/suppliers", new { nameEn = "No Arabic", nameAr = "" }), HttpStatusCode.BadRequest, "VAL-001");

        // An opening balance owed at this branch: opening balance equity against supplier payables, with the supplier as the party.
        await Ok(await owner.PostAsJsonAsync($"/api/v1/suppliers/{id1}/opening", new { branchId = kit.Branch, amount = 250m }));
        Assert.Equal(250m, await fx.QueryDbAsync<decimal>($"""
            SELECT sum(jl.credit - jl.debit) FROM acct.journal_line jl JOIN acct.account a ON a.id = jl.account_id WHERE a.code = '2100' AND jl.party_id = '{id1}'
            """));

        // A branch server that was offline added two suppliers and owed one of them 80; head office approves one and merges the other.
        var b = await Credit.Central.RegisterBranchAsync(owner);
        var p1 = Guid.CreateVersion7();
        var p2 = Guid.CreateVersion7();
        SupplierSnapshot Prov(Guid id, string name) => new(id, $"SP-{b.Code}-2026-{Suffix()}", name, "مورد " + name, null, null, null, null, null, null, 30, "provisional", null, b.BranchId, 1, 0);
        var line = new SupplierEntryPush(Guid.CreateVersion7(), b.BranchId, p2, "opening", 80m, DateOnly.FromDateTime(DateTime.UtcNow), null, $"OB-{b.Code}-1", "opening_balance",
            Guid.CreateVersion7(), null, DateTimeOffset.UtcNow, Guid.CreateVersion7());
        var (status, body) = await Credit.Central.SyncAsync(b.Cert, "/sync/v1/suppliers/push", new SupplierPushRequest([Prov(p1, "Sohar Screens"), Prov(p2, "Gulf Parts Co")], [line]));
        Assert.True(status == 200, body.ToString());
        Assert.Single(body.GetProperty("ackedEntries").EnumerateArray());
        var list = await owner.GetFromJsonAsync<JsonElement>("/api/v1/suppliers?status=provisional");
        Assert.Contains(list.EnumerateArray(), s => s.GetProperty("id").GetGuid() == p1);
        await Ok(await owner.PostAsync($"/api/v1/suppliers/{p1}/approve", null), HttpStatusCode.NoContent);
        await Problem(await owner.PostAsJsonAsync($"/api/v1/suppliers/{p2}/merge", new { intoId = p2 }), HttpStatusCode.BadRequest, "VAL-001");
        await Ok(await owner.PostAsJsonAsync($"/api/v1/suppliers/{p2}/merge", new { intoId = id1, reason = "Same supplier" }), HttpStatusCode.NoContent);
        await Problem(await owner.PostAsJsonAsync($"/api/v1/suppliers/{p2}/opening", new { branchId = kit.Branch, amount = 5m }), HttpStatusCode.Conflict, "SUP-001");

        // The branch learns both outcomes with its heartbeat; the survivor's balance and statement include the merged supplier's lines.
        var hb = await Credit.HeartbeatAsync(b);
        var sent = hb.GetProperty("suppliers").EnumerateArray().ToDictionary(s => s.GetProperty("id").GetGuid(), s => s);
        Assert.Equal("active", sent[p1].GetProperty("status").GetString());
        Assert.Equal("merged", sent[p2].GetProperty("status").GetString());
        Assert.Equal(id1, sent[p2].GetProperty("mergedIntoId").GetGuid());
        var detail = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/suppliers/{id1}");
        Assert.Equal(330m, detail.GetProperty("balance").GetDecimal());
        var st = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/suppliers/{id1}/statement");
        Assert.Equal(330m, st.GetProperty("closing").GetDecimal());
        var aging = await owner.GetFromJsonAsync<JsonElement>("/api/v1/suppliers/reports/aging");
        Assert.Equal(330m, aging.EnumerateArray().Single(r => r.GetProperty("supplierId").GetGuid() == id1).GetProperty("total").GetDecimal());
        // Head office's own lines equal payables; the branch's line is that branch's books, not head office's.
        var run = await Ok(await owner.PostAsync("/api/v1/credit/reconciliation/run", null));
        Assert.All(run.GetProperty("rows").EnumerateArray(), r => Assert.Equal(0m, r.GetProperty("difference").GetDecimal()));
        await Credit.Central.DeactivateAsync(owner, b);
    }
}
