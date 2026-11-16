using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kite.Sales;
using Xunit;
using Xunit.Abstractions;
using static Kite.IntegrationTests.PosKit;
using static Kite.IntegrationTests.RepairKit;

namespace Kite.IntegrationTests;

/// <summary>
/// Phase 9 exit gate: "Parts costing and commissions exact; duplicate generation impossible". At several branches at once,
/// with a scarce part at one unit cost: workers approve more repairs than there are parts (the rest wait with requests), tills
/// race to sell the reserved stock while arrivals are given out oldest first by several fillers at once; the jobs are fitted,
/// some with outside work, some shared between two technicians, delivered and paid; the generator runs many times at once;
/// invoices are corrected at the till (some re-invoiced, some refunded, one correction sent from every till at once); a serial
/// part is fitted by two workers at once; commissions are approved and paid with racing payouts. Afterwards every job's
/// commission, every account and every drawer match a model kept by the test, and nothing was generated twice.
/// </summary>
[Collection("server")]
public sealed class Phase9ExitGateTests(KiteServerFixture fx, ITestOutputHelper output)
{
    private readonly PosKit kit = new(fx);
    private DeviceKit Dev => new(fx, kit);

    private static int Env(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;
    private static decimal Vat(decimal gross) => Math.Round(gross * 5m / 105m, 3, MidpointRounding.AwayFromZero);
    private static decimal Net(decimal gross) => gross - Vat(gross);
    private const decimal PartCost = 8m;

    private sealed record Till(int No, Guid Branch, Guid Terminal, HttpClient Pos);
    private sealed record Techs(Guid Labour, Guid Profit, Guid Late);

    private sealed class Job
    {
        public Guid Id, Branch, Technician, Sale;
        public decimal LabourGross, PartGross, Outside;
        public bool Shared, Waited;
        public int Version = 1;
        public decimal? CorrectedLabour;   // null: not corrected; 0: refunded with nothing charged again
    }

    private sealed class Model
    {
        private readonly Lock _gate = new();
        public decimal Parts, Wip, Cost, Outside, Payables, Cash;
        public readonly Dictionary<int, decimal> Drawer = [];
        public readonly ConcurrentDictionary<string, int> Counts = new();
        public void Do(Action a) { lock (_gate) a(); }
        public void Count(string what, int n = 1) => Counts.AddOrUpdate(what, n, (_, v) => v + n);
    }

    private async Task<Guid> BranchAsync(HttpClient owner, string code)
    {
        var r = await Ok(await owner.PostAsJsonAsync("/api/v1/branches", new { code, nameEn = "Branch " + code, nameAr = "فرع " + code }), HttpStatusCode.Created);
        return r.GetProperty("id").GetGuid();
    }

    private async Task<Till> TillAsync(HttpClient owner, int no, Guid branch)
    {
        var code = "N" + Suffix();
        var key = NewDeviceKey();
        var r = await Ok(await owner.PostAsJsonAsync("/api/v1/terminals", new { branchId = branch, code, name = "Gate till " + code, kind = "browser", deviceKey = key }), HttpStatusCode.Created);
        var id = r.GetProperty("id").GetGuid();
        await Ok(await owner.PostAsJsonAsync($"/api/v1/terminals/{id}/approve", new { emergencyEnabled = false }));
        var pos = await kit.PosAsync(KiteServerFixture.OwnerUsername, KiteServerFixture.OwnerPassword, new Terminal(id, code, key));
        await OpenShiftAsync(pos, 500m);
        return new Till(no, branch, id, pos);
    }

    private async Task<Guid> TechnicianAsync(HttpClient owner, Guid branch, string prefix) =>
        (await Ok(await owner.PostAsJsonAsync("/api/v1/users", new
        {
            username = $"{prefix}_{Suffix().ToLowerInvariant()}", displayNameEn = prefix + " " + Suffix(), displayNameAr = (string?)null, email = (string?)null, phone = (string?)null,
            temporaryPassword = "Temp-Pass-2026", preferredLanguage = "en", defaultBranchId = branch,
            roles = new[] { new { roleId = await kit.RoleIdAsync(owner, "technician"), branchId = (Guid?)branch } },
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

    private static object Rule(string name, string basis, Guid technician, decimal? amount = null, decimal? rate = null, decimal? min = null, decimal? max = null) => new
    {
        nameEn = name, nameAr = "قاعدة " + name, basis, amount, rate, minAmount = min, maxAmount = max, technicianId = technician,
        effectiveFrom = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(4)).AddDays(-7).ToString("yyyy-MM-dd"),
    };

    /// <summary>What each technician should hold on a job's standing invoice, by the rules of this gate.</summary>
    private static Dictionary<Guid, decimal> Expected(Job j, Techs t)
    {
        if (j.CorrectedLabour == 0m) return [];
        var labour = Net(j.CorrectedLabour ?? j.LabourGross);
        var b = new CommissionBasis(labour, Net(j.PartGross), PartCost, j.Outside);
        decimal Of(Guid tech, decimal share) =>
            tech == t.Labour ? CommissionMath.Compute("labour", null, 10m, null, null, b, share).Amount
            : tech == t.Profit ? CommissionMath.Compute("profit", null, 20m, 0.5m, 30m, b, share).Amount
            : CommissionMath.Compute("fixed", 1.5m, null, null, null, b, share).Amount;
        return j.Shared ? new() { [t.Labour] = Of(t.Labour, 50m), [t.Profit] = Of(t.Profit, 50m) } : new() { [j.Technician] = Of(j.Technician, 100m) };
    }

    [PgFact]
    public async Task Parts_and_commissions_at_several_branches_at_once_are_exact_and_never_generated_twice()
    {
        var branchCount = Env("KITE_P9_BRANCHES", 2);
        var tillsPerBranch = Env("KITE_P9_TILLS", 2);
        var jobsPerTill = Env("KITE_P9_JOBS", 5);
        using var owner = await kit.OwnerAsync();
        await Phase9Tests.EnableAllAsync(owner);
        await kit.SetPolicyAsync(owner, cashRounding: 0m);
        var cs = await Ok(await owner.GetAsync("/api/v1/commissions/settings"));
        await Ok(await owner.PutAsJsonAsync("/api/v1/commissions/settings", new
        {
            generateOn = "delivered", approvalBy = "branch", clawbackDays = 0, clawbackPercent = 100m, version = cs.GetProperty("version").GetInt32(),
        }), HttpStatusCode.NoContent);
        var category = await CategoryAsync(owner, "CHARGING");
        var labour = await ServiceAsync(owner);
        var part = await kit.ProductAsync(owner, "Gate port " + Suffix(), 21m, type: "spare_part");
        var demandPerBranch = tillsPerBranch * jobsPerTill;
        var initial = demandPerBranch / 2;   // half the jobs find their part; the rest wait

        var branches = new List<Guid>();
        var techs = new Dictionary<Guid, Techs>();
        var tills = new List<Till>();
        for (var i = 0; i < branchCount; i++)
        {
            var b = await BranchAsync(owner, "P" + Suffix()[..5]);
            branches.Add(b);
            await Ok(await owner.PostAsJsonAsync("/api/v1/inventory/adjustments", new
            {
                branchId = b, kind = "opening", note = "gate parts", lines = new[] { new { variantId = part.VariantId, qty = (decimal)initial, unitCost = PartCost, reasonCode = "opening" } },
            }), HttpStatusCode.Created);
            var t = new Techs(await TechnicianAsync(owner, b, "lab"), await TechnicianAsync(owner, b, "pro"), await TechnicianAsync(owner, b, "late"));
            techs[b] = t;
            await Ok(await owner.PostAsJsonAsync("/api/v1/commissions/rules", Rule("Gate labour", "labour", t.Labour, rate: 10m)));
            await Ok(await owner.PostAsJsonAsync("/api/v1/commissions/rules", Rule("Gate profit", "profit", t.Profit, rate: 20m, min: 0.5m, max: 30m)));
            for (var w = 0; w < tillsPerBranch; w++) tills.Add(await TillAsync(owner, tills.Count, b));
        }
        var m = new Model();
        foreach (var t in tills) m.Drawer[t.No] = 500m;
        // The model counts what moves after this snapshot (the opening stock is before it).
        var gl = await Dev.GlSnapshotAsync("1430", "1460", "5200", "5210", "2110", "6300", "2400", "1100");
        var jobs = new ConcurrentBag<Job>();
        var clock = Stopwatch.StartNew();

        // ---------------------------------------------------------------- A. more repairs approved than there are parts
        await Task.WhenAll(tills.Select(async t =>
        {
            var rnd = new Random(9000 + t.No);
            var tt = techs[t.Branch];
            for (var k = 0; k < jobsPerTill; k++)
            {
                var j = new Job
                {
                    Branch = t.Branch, LabourGross = new[] { 10m, 15m, 21m }[rnd.Next(3)], PartGross = new[] { 21m, 30m }[rnd.Next(2)],
                    Outside = rnd.Next(3) == 0 ? 4.5m : 0m, Shared = k % 4 == 1,
                };
                j.Technician = k % 4 == 3 ? tt.Late : k % 2 == 0 ? tt.Labour : tt.Profit;
                if (j.Shared) j.Technician = tt.Labour;
                var c = await TradeInKit.CustomerAsync(t.Pos, "G9 " + Suffix());
                j.Id = Id(await IntakeAsync(t.Pos, c, category, preAuthorised: 500m, technicianId: j.Technician));
                await MoveAsync(t.Pos, j.Id, "inspection_pending", "under_diagnosis");
                await EstimateAsync(t.Pos, j.Id, Labour(labour, j.LabourGross), Part(part.VariantId, 1m, j.PartGross));
                var d = await MoveAsync(t.Pos, j.Id, "in_progress");
                j.Waited = d.GetProperty("job").GetProperty("status").GetString() == "waiting_parts";
                if (j.Shared)
                    await Ok(await t.Pos.PutAsJsonAsync($"/api/v1/repairs/{j.Id}/shares",
                        new { shares = new[] { new { technicianId = tt.Labour, share = 50m }, new { technicianId = tt.Profit, share = 50m } } }), HttpStatusCode.NoContent);
                jobs.Add(j);
            }
        }));
        foreach (var b in branches)
        {
            Assert.Equal((decimal)initial, await fx.QueryDbAsync<decimal>($"SELECT reserved FROM inv.stock_balance WHERE branch_id = '{b}' AND variant_id = '{part.VariantId}'"));
            Assert.Equal(demandPerBranch - initial, jobs.Count(j => j.Branch == b && j.Waited));
            Assert.Equal((decimal)(demandPerBranch - initial),
                await fx.QueryDbAsync<decimal>($"SELECT coalesce(sum(qty), 0) FROM repair.part_request WHERE branch_id = '{b}' AND variant_id = '{part.VariantId}'"));
        }
        m.Count("jobs approved", jobs.Count);
        m.Count("jobs waiting for parts", jobs.Count(j => j.Waited));

        // ---------------------------------------------------------------- B. arrivals given out oldest first while tills try to sell them
        // A few more arrive than the jobs wait for: the tills may sell those, never what is reserved or waited for.
        var extra = tillsPerBranch;
        foreach (var b in branches)
            await Ok(await owner.PostAsJsonAsync("/api/v1/inventory/adjustments", new
            {
                branchId = b, kind = "opening", note = "arrived", lines = new[] { new { variantId = part.VariantId, qty = (decimal)(demandPerBranch - initial + extra), unitCost = PartCost, reasonCode = "opening" } },
            }), HttpStatusCode.Created);
        m.Do(() => m.Parts += branchCount * (demandPerBranch - initial + extra) * PartCost);
        var sold = new ConcurrentDictionary<Guid, int>();
        var fills = Task.WhenAll(Enumerable.Range(0, 4).Select(async _ => (await Ok(await owner.PostAsync("/api/v1/repairs/parts-needed/fill", null))).GetProperty("moved").GetInt32()));
        var sales = Task.WhenAll(tills.Select(async t =>
        {
            for (var k = 0; k < extra; k++)
            {
                var r = await t.Pos.PostAsJsonAsync($"/api/v1/sales/{Guid.CreateVersion7()}/complete", new { lines = new[] { Line(part, 1m) }, payments = new[] { Cash(21m) } });
                if (r.IsSuccessStatusCode)
                {
                    sold.AddOrUpdate(t.Branch, 1, (_, v) => v + 1);
                    // A spare part sold over the counter goes to the same cost account as one fitted in a repair (5200).
                    m.Do(() => { m.Drawer[t.No] += 21m; m.Cash += 21m; m.Parts -= PartCost; m.Cost += PartCost; });
                }
                else await Problem(r, HttpStatusCode.Conflict, "INV-001");
            }
        }));
        var moved = (await fills).Sum();
        await sales;
        moved += (await Ok(await owner.PostAsync("/api/v1/repairs/parts-needed/fill", null))).GetProperty("moved").GetInt32();
        // Every waiting job got its part and moved on once; the tills sold exactly the spare ones, and then nothing more.
        Assert.Equal(jobs.Count(j => j.Waited), moved);
        Assert.Equal(0L, await fx.QueryDbAsync<long>($"SELECT count(*) FROM repair.part_request WHERE variant_id = '{part.VariantId}' AND qty > 0"));
        foreach (var b in branches) Assert.Equal(extra, sold.GetValueOrDefault(b));
        foreach (var j in jobs.Where(j => j.Waited))
            Assert.Equal(1L, await fx.QueryDbAsync<long>($"SELECT count(*) FROM repair.status_history WHERE job_id = '{j.Id}' AND note = 'Parts arrived'"));
        await Problem(await tills[0].Pos.PostAsJsonAsync($"/api/v1/sales/{Guid.CreateVersion7()}/complete", new { lines = new[] { Line(part, 1m) }, payments = new[] { Cash(21m) } }),
            HttpStatusCode.Conflict, "INV-001");
        m.Count("parts sold at the till", sold.Values.Sum());

        // ---------------------------------------------------------------- C. fitted, outside work, delivered and paid
        await Task.WhenAll(tills.Select(async t =>
        {
            foreach (var j in jobs.Where(x => x.Branch == t.Branch).OrderBy(x => x.Id).Where((_, i) => i % tillsPerBranch == t.No % tillsPerBranch))
            {
                await FitAsync(t.Pos, j.Id, part, 1m);
                m.Do(() => { m.Parts -= PartCost; m.Cost += PartCost; });
                if (j.Outside > 0)
                {
                    await Ok(await t.Pos.PostAsJsonAsync($"/api/v1/repairs/{j.Id}/outside-work", new { vendor = "Gate Lab", amount = j.Outside }));
                    m.Do(() => { m.Outside += j.Outside; m.Payables -= j.Outside; });
                }
                await MoveAsync(t.Pos, j.Id, "testing", "completed", "ready");
                var gross = j.LabourGross + j.PartGross;
                j.Sale = (await DeliverAsync(t.Pos, j.Id, [Cash(gross)])).GetProperty("saleId").GetGuid();
                m.Do(() => { m.Drawer[t.No] += gross; m.Cash += gross; });
            }
        }));
        Assert.Equal(0m, await fx.QueryDbAsync<decimal>($"SELECT coalesce(sum(reserved), 0) FROM inv.stock_balance WHERE variant_id = '{part.VariantId}'"));
        m.Count("jobs delivered", jobs.Count);

        // ---------------------------------------------------------------- D. the generator, many times at once
        foreach (var b in branches) await Ok(await owner.PostAsJsonAsync("/api/v1/commissions/rules", Rule("Gate late", "fixed", techs[b].Late, amount: 1.5m)));
        var runs = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
            (await Ok(await owner.PostAsJsonAsync("/api/v1/commissions/generate", new { }))).GetProperty("written").GetInt32()));
        Assert.Equal(jobs.Count(j => j.Technician == techs[j.Branch].Late && !j.Shared), runs.Sum());
        Assert.Equal(0, (await Ok(await owner.PostAsJsonAsync("/api/v1/commissions/generate", new { }))).GetProperty("written").GetInt32());
        m.Count("generator runs at once", runs.Length);

        // ---------------------------------------------------------------- E. invoices corrected at the till
        var toCorrect = jobs.OrderBy(j => j.Id).Where((_, i) => i % 3 == 0).ToList();
        await Task.WhenAll(toCorrect.Select(async (j, i) =>
        {
            var t = tills.First(x => x.Branch == j.Branch);
            var gross = j.LabourGross + j.PartGross;
            if (i % 2 == 0)
            {
                // Re-invoiced with 5.000 less labour (paid in cash), the old invoice refunded in cash.
                var newLabour = j.LabourGross - 5m;
                await Ok(await t.Pos.PostAsJsonAsync($"/api/v1/repairs/{j.Id}/correct", new
                {
                    id = Guid.CreateVersion7(), reason = "Labour overcharged", refunds = new[] { Cash(gross) },
                    lines = new object[] { Labour(labour, newLabour), Part(part.VariantId, 1m, j.PartGross) }, payments = new[] { Cash(newLabour + j.PartGross) },
                }));
                j.CorrectedLabour = newLabour; j.Version = 2;
                m.Do(() => { m.Drawer[t.No] -= 5m; m.Cash -= 5m; });
                m.Count("invoices corrected and re-invoiced");
            }
            else
            {
                await Ok(await t.Pos.PostAsJsonAsync($"/api/v1/repairs/{j.Id}/correct", new
                {
                    id = Guid.CreateVersion7(), reason = "Goodwill", refunds = new[] { new { method = "store_credit", amount = gross } },
                }));
                j.CorrectedLabour = 0m;
                m.Count("invoices credited to store credit");
            }
        }));
        // One correction sent from every till of its branch at once (the same request): it happens once.
        var raced = jobs.OrderBy(j => j.Id).First(j => j.CorrectedLabour is null);
        var raceId = Guid.CreateVersion7();
        var raceCorrected = Guid.CreateVersion7();
        var raceTills = tills.Where(t => t.Branch == raced.Branch).ToList();
        var raceResults = await Task.WhenAll(raceTills.Select(t => t.Pos.PostAsJsonAsync($"/api/v1/repairs/{raced.Id}/correct", new
        {
            id = raceId, correctedId = raceCorrected, reason = "Part price wrong", refunds = new[] { Cash(raced.LabourGross + raced.PartGross) },
            lines = new object[] { Labour(labour, raced.LabourGross), Part(part.VariantId, 1m, raced.PartGross - 1m) }, payments = new[] { Cash(raced.LabourGross + raced.PartGross - 1m) },
        })));
        Assert.All(raceResults, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.All(await Task.WhenAll(raceResults.Select(Json)), r => Assert.Equal(raceCorrected, r.GetProperty("correctedId").GetGuid()));
        Assert.Equal(1L, await fx.QueryDbAsync<long>($"SELECT count(*) FROM repair.invoice_correction WHERE job_id = '{raced.Id}'"));
        var winner = await fx.QueryDbAsync<Guid>($"SELECT terminal_id FROM sales.sale WHERE id = '{raceId}'");
        var raceTill = raceTills.Single(t => t.Terminal == winner);
        raced.PartGross -= 1m; raced.Version = 2; raced.CorrectedLabour = raced.LabourGross;
        m.Do(() => { m.Drawer[raceTill.No] -= 1m; m.Cash -= 1m; });
        m.Count("corrections raced from every till", raceTills.Count);

        // ---------------------------------------------------------------- F. a serial part fitted by two workers at once
        var board = await kit.ProductAsync(owner, "Gate board " + Suffix(), 60m, type: "spare_part", tracking: "serial");
        var serial = "GB" + Suffix();
        var b0 = branches[0];
        await Ok(await owner.PostAsJsonAsync("/api/v1/inventory/adjustments", new
        {
            branchId = b0, kind = "opening", note = "board",
            lines = new[] { new { variantId = board.VariantId, qty = 1m, reasonCode = "opening", units = new[] { new { serial, unitCost = 30m } } } },
        }), HttpStatusCode.Created);
        m.Do(() => m.Parts += 30m);
        var boardJobs = new List<Guid>();
        var bt = tills.Where(t => t.Branch == b0).ToList();
        foreach (var t in bt.Take(2))
        {
            var c = await TradeInKit.CustomerAsync(t.Pos, "G9 board " + Suffix());
            var id = Id(await IntakeAsync(t.Pos, c, category, preAuthorised: 500m));
            await MoveAsync(t.Pos, id, "inspection_pending", "under_diagnosis");
            await EstimateAsync(t.Pos, id, Labour(labour, 10m), Part(board.VariantId, 1m, 60m));
            await MoveAsync(t.Pos, id, "in_progress");
            boardJobs.Add(id);
        }
        var fits = await Task.WhenAll(boardJobs.Select((id, i) => bt[i % bt.Count].Pos.PostAsJsonAsync($"/api/v1/repairs/{id}/parts", new { variantId = board.VariantId, qty = 1m, serial })));
        Assert.Equal(1, fits.Count(r => r.IsSuccessStatusCode));
        Assert.All(fits.Where(r => !r.IsSuccessStatusCode), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Equal(1L, await fx.QueryDbAsync<long>($"""
            SELECT count(*) FROM repair.repair_line l JOIN device.imei_registry r ON r.device_unit_id = l.device_unit_id
             WHERE r.value = '{serial}' AND l.kind = 'part' AND l.qty > 0
            """));
        m.Do(() => { m.Parts -= 30m; m.Wip += 30m; });
        m.Count("serial fits raced", fits.Length);

        // ---------------------------------------------------------------- the commissions against the model
        var accruedTotal = 0m;
        foreach (var j in jobs)
        {
            var rows = (await Ok(await owner.GetAsync($"/api/v1/commissions?jobId={j.Id}"))).EnumerateArray().ToArray();
            var standing = rows.Where(r => r.GetProperty("kind").GetString() == "repair" && r.GetProperty("status").GetString() != "reversed")
                .ToDictionary(r => r.GetProperty("technicianId").GetGuid(), r => r.GetProperty("amount").GetDecimal());
            var expected = Expected(j, techs[j.Branch]);
            Assert.True(expected.Count == standing.Count && expected.All(e => standing.TryGetValue(e.Key, out var a) && a == e.Value),
                $"job {j.Id}: expected {string.Join(",", expected.Select(e => $"{e.Key}={e.Value}"))}; standing {string.Join(",", standing.Select(e => $"{e.Key}={e.Value}"))}");
            // Each correction reversed what stood before it, exactly.
            foreach (var r in rows.Where(r => r.GetProperty("status").GetString() == "reversed" && r.GetProperty("kind").GetString() == "repair"))
                Assert.Contains(rows, x => x.GetProperty("kind").GetString() == "reversal" && x.GetProperty("reversesId").GetGuid() == r.GetProperty("id").GetGuid()
                                         && x.GetProperty("amount").GetDecimal() == -r.GetProperty("amount").GetDecimal());
            // What a job's accruals add up to (reversals included) is what stands on it.
            Assert.Equal(expected.Values.Sum(), rows.Sum(r => r.GetProperty("amount").GetDecimal()));
            accruedTotal += expected.Values.Sum();
        }
        Assert.Equal(0L, await fx.QueryDbAsync<long>("""
            SELECT count(*) FROM (SELECT job_id, technician_id, kind, kind_version FROM comm.accrual WHERE kind <> 'reversal'
                                   GROUP BY 1, 2, 3, 4 HAVING count(*) > 1) x
            """));

        // ---------------------------------------------------------------- G. approval and racing payouts
        var paidTotal = 0m;
        foreach (var b in branches)
            foreach (var tech in new[] { techs[b].Labour, techs[b].Profit, techs[b].Late })
            {
                var open = (await Ok(await owner.GetAsync($"/api/v1/commissions?technicianId={tech}&status=accrued"))).EnumerateArray()
                    .Select(a => (Id: a.GetProperty("id").GetGuid(), Amount: a.GetProperty("amount").GetDecimal())).ToArray();
                if (open.Length == 0) continue;
                await Ok(await owner.PostAsJsonAsync("/api/v1/commissions/approve", new { ids = open.Select(o => o.Id) }));
                var total = open.Sum(o => o.Amount);
                if (total <= 0) continue;
                var racers = tills.Where(t => t.Branch == b).Select(_ => owner.PostAsJsonAsync("/api/v1/commissions/payouts",
                    new { branchId = b, technicianId = tech, accrualIds = open.Select(o => o.Id), method = "cash" })).ToArray();
                var results = await Task.WhenAll(racers.Append(owner.PostAsJsonAsync("/api/v1/commissions/payouts",
                    new { branchId = b, technicianId = tech, accrualIds = open.Select(o => o.Id), method = "payroll" })));
                Assert.Equal(1, results.Count(r => r.IsSuccessStatusCode));
                foreach (var r in results.Where(r => !r.IsSuccessStatusCode)) await Problem(r, HttpStatusCode.Conflict, "COM-002");
                var won = await Json(results.Single(r => r.IsSuccessStatusCode));
                Assert.Equal(total, won.GetProperty("total").GetDecimal());
                paidTotal += total;
                if (await fx.QueryDbAsync<string>($"SELECT method FROM comm.payout WHERE id = '{won.GetProperty("id").GetGuid()}'") == "cash") m.Do(() => m.Cash -= total);
                m.Count("payouts raced");
            }
        Assert.Equal(0L, await fx.QueryDbAsync<long>("SELECT count(*) FROM (SELECT accrual_id FROM comm.payout_line GROUP BY 1 HAVING count(*) > 1) x"));
        var elapsed = clock.Elapsed;

        // ---------------------------------------------------------------- the accounts against the model
        async Task<decimal> Moved(string code) => await Dev.GlAsync(code) - gl[code];
        var payroll = await fx.QueryDbAsync<decimal>($"SELECT coalesce(sum(total), 0) FROM comm.payout WHERE method = 'payroll' AND technician_id IN ({string.Join(",", techs.Values.SelectMany(t => new[] { t.Labour, t.Profit, t.Late }).Select(x => $"'{x}'"))})");
        var table = new (string Account, decimal Expected, decimal Moved)[]
        {
            ("1430 Spare parts", m.Parts, await Moved("1430")),
            ("1460 Repair work in progress", m.Wip, await Moved("1460")),
            ("5200 Cost of repair parts", m.Cost, await Moved("5200")),
            ("5210 Cost of outside repair work", m.Outside, await Moved("5210")),
            ("2110 Payables", m.Payables, await Moved("2110")),
            ("6300 Commission expense", accruedTotal, await Moved("6300")),
            ("2400 Commissions payable", -(accruedTotal - paidTotal), await Moved("2400")),
            ("1100 Cash", m.Cash, await Moved("1100")),
        };
        var counts = string.Join(", ", m.Counts.OrderBy(x => x.Key).Select(x => $"{x.Value} {x.Key}"));
        output.WriteLine($"{branchCount} branches x {tillsPerBranch} tills x {jobsPerTill} jobs in {elapsed.TotalSeconds:0}s: {counts}");
        foreach (var (a, e, mv) in table) output.WriteLine($"  {a}: expected {e}, moved {mv}");
        if (Environment.GetEnvironmentVariable("KITE_P9_REPORT") is { Length: > 0 } report)
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
            {
                branches = branchCount, tillsPerBranch, jobsPerTill, seconds = (int)elapsed.TotalSeconds, payroll,
                counts = m.Counts.OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Value),
                accounts = table.ToDictionary(x => x.Account, x => new { expected = x.Expected, actual = x.Moved }),
            }, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var (account, expected, mv) in table) Assert.True(expected == mv, $"{account}: expected {expected}, moved {mv}");

        // The ledgers agree with the accounts as a whole, and every drawer with its tills' takings.
        Assert.Equal(await fx.QueryDbAsync<decimal>("SELECT coalesce((SELECT sum(total) FROM comm.payout), 0) - coalesce((SELECT sum(amount) FROM comm.accrual), 0)"),
            await Dev.GlAsync("2400"));
        Assert.Equal(await fx.QueryDbAsync<decimal>("SELECT coalesce(sum(repair.wip(id)), 0) FROM repair.repair_job"), await Dev.GlAsync("1460"));
        Assert.Equal(0L, await fx.QueryDbAsync<long>("SELECT count(*) FROM inv.stock_balance b WHERE b.reserved <> coalesce((SELECT sum(r.qty) FROM inv.reservation r WHERE r.warehouse_id = b.warehouse_id AND r.variant_id = b.variant_id), 0)"));
        Assert.Equal(0L, await fx.QueryDbAsync<long>("SELECT count(*) FROM comm.accrual WHERE journal_id IS NULL AND amount <> 0"));
        foreach (var t in tills)
            Assert.Equal(m.Drawer[t.No], (await Ok(await t.Pos.GetAsync("/api/v1/pos/shifts/current"))).GetProperty("figures").GetProperty("expectedCash").GetDecimal());
        await DeviceKit.AssertConsistentAsync(owner);
    }
}
