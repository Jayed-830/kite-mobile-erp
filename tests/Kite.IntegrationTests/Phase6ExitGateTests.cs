using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;
using static Kite.IntegrationTests.DeviceKit;
using static Kite.IntegrationTests.PosKit;

namespace Kite.IntegrationTests;

/// <summary>
/// Phase 6 exit gate (blueprint 25, row 6): "Uniqueness and verification points pass concurrency tests".
///
/// 1. One server: twenty receivers post receipts sharing IMEIs at the same moment, then twenty tills try to sell the same
///    phone at once. Every IMEI ends with exactly one device, every receipt is all or nothing, and exactly one sale succeeds.
/// 2. Head office and several branch servers at once: the branch servers (this test, over mutual TLS) register devices
///    drawn from a shared pool of IMEIs, as they would after working offline, while head office receives some of the same
///    IMEIs, corrects and reverses its own, and branches correct and reverse theirs. Each branch keeps its own record of its
///    devices' holds from head office's answers and heartbeat instructions, exactly as a branch server applies them. At the
///    end the test checks head office against its own record of who claims each IMEI: one holder per IMEI, a conflict open
///    exactly where two devices claim one, every device on hold exactly while it is in an open conflict (on head office and
///    on every branch), and the instruction stream never releasing before holding.
/// Size: KITE_P6_TILLS (20); KITE_P6_BRANCHES (4) x KITE_P6_ROUNDS (4) x KITE_P6_UNITS (6). KITE_P6_REPORT writes a JSON report.
/// </summary>
[Collection("server")]
public sealed class Phase6ExitGateTests(KiteServerFixture fx, ITestOutputHelper output)
{
    private readonly PosKit kit = new(fx);
    private DeviceKit Dev => new(fx, kit);
    private CentralKit Central => new(fx);

    private static int Env(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;

    private Task<HttpResponseMessage> ReceiveAsync(HttpClient owner, Item phone, params string[] imeis) =>
        owner.PostAsJsonAsync("/api/v1/inventory/adjustments", new
        {
            branchId = kit.Branch, kind = "opening",
            lines = new[] { new { variantId = phone.VariantId, qty = (decimal)imeis.Length, reasonCode = "opening", units = imeis.Select(i => new { imei1 = i, unitCost = 100m }).ToArray() } },
        });

    private async Task<string?> CodeOfAsync(HttpResponseMessage r) =>
        r.IsSuccessStatusCode ? null : (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

    [PgFact]
    public async Task Twenty_receivers_and_twenty_tills_racing_for_the_same_IMEIs_leave_one_device_per_IMEI()
    {
        var n = Env("KITE_P6_TILLS", 20);
        using var owner = await kit.OwnerAsync();
        await kit.SetPolicyAsync(owner, cashRounding: 0m);
        var phone = await Dev.PhoneAsync(owner, "Race phone " + Suffix(), 120m);
        var pool = Enumerable.Range(0, 6).Select(_ => Imei()).ToArray();
        var rnd = new Random(20261116);
        var docs = Enumerable.Range(0, n).Select(_ => pool.OrderBy(_ => rnd.Next()).Take(3).Append(Imei()).ToArray()).ToArray();

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = docs.Select(async d => { await gate.Task; return await ReceiveAsync(owner, phone, d); }).ToArray();
        gate.SetResult();
        var responses = await Task.WhenAll(runs);
        var codes = await Task.WhenAll(responses.Select(CodeOfAsync));
        Assert.All(codes, c => Assert.True(c is null or "DEV-007", $"a receipt failed with {c}"));
        var won = docs.Where((d, i) => codes[i] is null).ToArray();
        Assert.NotEmpty(won);
        // No IMEI is in two receipts that went through.
        Assert.Equal(won.SelectMany(d => d).Count(), won.SelectMany(d => d).Distinct().Count());

        // Every receipt is all or nothing: each IMEI of a receipt that went through belongs to a device of that receipt; none of a refused one exists.
        var all = docs.SelectMany(d => d).Distinct().ToArray();
        var json = await fx.QueryDbAsync<string>($"""
            SELECT coalesce(json_agg(json_build_object('value', r.value, 'doc', u.source_doc_id))::text, '[]')
              FROM device.imei_registry r JOIN device.device_unit u ON u.id = r.device_unit_id
             WHERE r.status = 'active' AND r.value IN ({string.Join(',', all.Select(v => $"'{v}'"))})
            """);
        var rows = JsonSerializer.Deserialize<JsonElement>(json).EnumerateArray().Select(e => (Value: e.GetProperty("value").GetString()!, Doc: e.GetProperty("doc").GetGuid())).ToList();
        Assert.Equal(rows.Count, rows.Select(x => x.Value).Distinct().Count());                     // one device per IMEI
        foreach (var d in won) Assert.Single(rows.Where(x => d.Contains(x.Value)).Select(x => x.Doc).Distinct());
        foreach (var d in docs.Where((d, i) => codes[i] is not null)) Assert.DoesNotContain(rows, x => x.Value == d[^1]);
        Assert.Equal(won.Length * 4, rows.Count);
        var central = await fx.QueryDbAsync<long>($"SELECT count(*) FROM device.central_registry WHERE status = 'active' AND value IN ({string.Join(',', all.Select(v => $"'{v}'"))})");
        Assert.Equal(rows.Count, central);

        // Twenty tills scan the same phone at once: exactly one sale; the others are told why.
        var target = await Ok(await owner.GetAsync($"/api/v1/devices/lookup/{won[0][0]}"));
        var unitId = target.GetProperty("id").GetGuid();
        var tills = new List<HttpClient>();
        for (var i = 0; i < n; i++)
        {
            var term = await kit.TerminalAsync(owner);
            var c = await kit.OwnerPosAsync(term);
            await OpenShiftAsync(c, 0m);
            tills.Add(c);
        }
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sells = tills.Select(async c =>
        {
            // As the till does it: scan the IMEI (for the model chosen), lease the phone to this cart, complete the cart.
            var cart = Guid.CreateVersion7();
            await go.Task;
            var scan = await c.GetAsync($"/api/v1/pos/units/code/{won[0][0]}?variantId={phone.VariantId}");
            if (!scan.IsSuccessStatusCode) return scan;
            var lease = await c.PostAsJsonAsync($"/api/v1/pos/units/{unitId}/reserve", new { cartId = cart });
            if (!lease.IsSuccessStatusCode) return lease;
            return await c.PostAsJsonAsync($"/api/v1/sales/{cart}/complete", new { lines = new[] { UnitLine(phone, unitId) }, payments = new[] { Card(120m) } });
        }).ToArray();
        go.SetResult();
        var sold = await Task.WhenAll(sells);
        Assert.Equal(1, sold.Count(r => r.StatusCode == HttpStatusCode.OK));
        var refusals = await Task.WhenAll(sold.Where(r => r.StatusCode != HttpStatusCode.OK).Select(CodeOfAsync));
        Assert.All(refusals, c => Assert.True(c is "DEV-004" or "DEV-005", $"a till was refused with {c}"));
        Assert.Equal(1, await fx.QueryDbAsync<int>($"SELECT count(*)::int FROM sales.sale_line WHERE device_unit_id = '{unitId}'"));
        foreach (var c in tills) c.Dispose();
        await AssertConsistentAsync(owner);
        output.WriteLine($"{n} receivers: {won.Length} receipts went through, {n - won.Length} refused (DEV-007); {n} tills: 1 sale, {refusals.Length} refused.");
    }

    // ------------------------------------------------------------------------------------------ the multi-server model
    private sealed class SimUnit(Guid id, string imei)
    {
        public Guid Id { get; } = id;
        public string Imei { get; set; } = imei;
        public bool Reversed { get; set; }
        public long Stamp { get; set; }
        public HashSet<Guid> Holds { get; } = [];
    }

    private sealed class SimBranch(CentralKit.Branch b)
    {
        public CentralKit.Branch B { get; } = b;
        public List<SimUnit> Units { get; } = [];
        public long Seq { get; set; }
        public object Gate { get; } = new();
        public List<(long Seq, string Kind, Guid Unit, Guid Conflict)> Instructions { get; } = [];
    }

    private sealed class Tally
    {
        public int Pushes, BranchUnits, HoReceived, HoRefused, BranchCorrections, BranchReversals, HoCorrections, HoReversals, HoReversalsTooLate, StaleAnswers, StaleInstructions;
    }

    private async Task PushAsync(SimBranch s, IReadOnlyList<SimUnit> units, Tally tally)
    {
        if (units.Count == 0) return;
        var body = await Central.RegisterAsync(s.B, units.Select(u => u.Reversed ? CentralKit.Unit(u.Id, "reversed") : CentralKit.Unit(u.Id, "in_stock", u.Imei)).ToArray());
        var stamp = body.GetProperty("instructionSeq").GetInt64();
        Interlocked.Increment(ref tally.Pushes);
        lock (s.Gate)
            foreach (var r in body.GetProperty("results").EnumerateArray())
            {
                var u = s.Units.Single(x => x.Id == r.GetProperty("unitId").GetGuid());
                // As a branch server applies an answer (CentralRegistry.ApplyResultAsync): ignored when an instruction about the unit is newer.
                if (stamp < u.Stamp) { Interlocked.Increment(ref tally.StaleAnswers); continue; }
                u.Holds.Clear();
                foreach (var c in r.GetProperty("conflicts").EnumerateArray()) u.Holds.Add(c.GetProperty("conflictId").GetGuid());
                u.Stamp = Math.Max(u.Stamp, stamp);
            }
    }

    private async Task<int> HeartbeatAsync(SimBranch s, Tally tally)
    {
        long from;
        lock (s.Gate) from = s.Seq;
        var hb = await Central.HeartbeatAsync(s.B, from);
        var applied = 0;
        lock (s.Gate)
            foreach (var i in hb.GetProperty("imeiInstructions").EnumerateArray().OrderBy(i => i.GetProperty("seq").GetInt64()))
            {
                var seq = i.GetProperty("seq").GetInt64();
                if (seq <= s.Seq) continue;
                s.Seq = seq;
                applied++;
                var kind = i.GetProperty("kind").GetString()!;
                var unitId = i.GetProperty("unitId").GetGuid();
                var conflict = i.GetProperty("conflictId").GetGuid();
                s.Instructions.Add((seq, kind, unitId, conflict));
                // As a branch server applies an instruction (CentralClient.ApplyFromHeadOfficeAsync): only when newer than the unit's last answer.
                var u = s.Units.SingleOrDefault(x => x.Id == unitId);
                if (u is null || seq <= u.Stamp) { Interlocked.Increment(ref tally.StaleInstructions); continue; }
                if (kind == "hold") u.Holds.Add(conflict); else u.Holds.Remove(conflict);
                u.Stamp = seq;
            }
        return applied;
    }

    [PgFact]
    public async Task Branch_servers_and_head_office_registering_overlapping_IMEIs_at_once_keep_one_holder_per_IMEI_and_settle_every_conflict()
    {
        var (branchCount, rounds, perRound) = (Env("KITE_P6_BRANCHES", 4), Env("KITE_P6_ROUNDS", 4), Env("KITE_P6_UNITS", 6));
        var clock = Stopwatch.StartNew();
        using var owner = await kit.OwnerAsync();
        var phone = await Dev.PhoneAsync(owner, "Pool phone " + Suffix(), 150m);
        var sims = new List<SimBranch>();
        for (var i = 0; i < branchCount; i++) sims.Add(new SimBranch(await Central.RegisterBranchAsync(owner)));
        // About two or three claimants per pool IMEI over the run: plenty of conflicts, and each one can be settled.
        var pool = Enumerable.Range(0, Math.Max(8, branchCount * perRound * rounds * 6 / 25)).Select(_ => Imei()).ToArray();
        var ho = new ConcurrentDictionary<Guid, string>();           // head office's devices claiming an IMEI: unit -> IMEI
        var tally = new Tally();
        var rnd = new Random(20261116);
        var setup = clock.Elapsed;

        // The working rounds, then two rounds where nothing new comes in and sides settle about half of what is open.
        for (var round = 0; round < rounds + 2; round++)
        {
            var settling = round >= rounds;
            var work = new List<Task>();
            // Branch servers register what they received (as after working offline): mostly IMEIs from the shared pool.
            foreach (var s in settling ? [] : sims)
            {
                var fresh = new List<SimUnit>();
                lock (s.Gate)
                {
                    var mine = s.Units.Where(u => !u.Reversed).Select(u => u.Imei).ToHashSet();
                    for (var k = 0; k < perRound; k++)
                    {
                        var imei = rnd.NextDouble() < 0.6 ? pool[rnd.Next(pool.Length)] : Imei();
                        if (!mine.Add(imei)) continue;                        // a branch never holds one IMEI twice (its own unique index)
                        var u = new SimUnit(Guid.CreateVersion7(), imei);
                        s.Units.Add(u);
                        fresh.Add(u);
                    }
                }
                Interlocked.Add(ref tally.BranchUnits, fresh.Count);
                work.Add(PushAsync(s, fresh, tally));
            }
            // Head office receives some pool IMEIs at the same moment, one receipt each.
            var heldHere = ho.Values.ToHashSet();
            foreach (var imei in settling ? [] : pool.Where(p => !heldHere.Contains(p)).OrderBy(_ => rnd.Next()).Take(4))
                work.Add(Task.Run(async () =>
                {
                    var r = await ReceiveAsync(owner, phone, imei);
                    var code = await CodeOfAsync(r);
                    Assert.True(code is null or "DEV-007", $"head office receipt failed with {code}");
                    if (code is null)
                    {
                        ho[(await Ok(await owner.GetAsync($"/api/v1/devices/lookup/{imei}"))).GetProperty("id").GetGuid()] = imei;
                        Interlocked.Increment(ref tally.HoReceived);
                    }
                    else Interlocked.Increment(ref tally.HoRefused);
                }));
            // Conflicts open before this round are resolved by one of their sides, at the same time as the new work.
            var open = JsonSerializer.Deserialize<JsonElement>(await fx.QueryDbAsync<string>("""
                SELECT coalesce(json_agg(json_build_object('id', c.id, 'units', (SELECT json_agg(r.device_unit_id) FROM device.central_registry r WHERE r.conflict_id = c.id AND r.status <> 'retired'))), '[]')::text
                  FROM device.imei_conflict c WHERE c.status = 'open'
                """)).EnumerateArray().ToList();
            foreach (var c in open.OrderBy(_ => rnd.Next()).Take(Math.Max(3, open.Count / (settling ? 2 : 3))))
            {
                var units = c.GetProperty("units").EnumerateArray().Select(x => x.GetGuid()).ToList();
                var pick = units[rnd.Next(units.Count)];
                var correct = rnd.NextDouble() < 0.6;
                if (ho.ContainsKey(pick))
                    work.Add(Task.Run(async () =>
                    {
                        if (correct)
                        {
                            var newImei = Imei();
                            await Ok(await owner.PostAsJsonAsync($"/api/v1/devices/{pick}/identifiers", new { imei1 = newImei, reason = "typed wrong" }));
                            ho[pick] = newImei;
                            Interlocked.Increment(ref tally.HoCorrections);
                        }
                        else
                        {
                            var r = await owner.PostAsJsonAsync($"/api/v1/devices/{pick}/reverse-receipt", new { reason = "received in error" });
                            var code = await CodeOfAsync(r);
                            Assert.True(code is null or "DEV-018", $"reversal failed with {code}");   // DEV-018: its conflict closed meanwhile
                            if (code is null) { ho.TryRemove(pick, out _); Interlocked.Increment(ref tally.HoReversals); }
                            else Interlocked.Increment(ref tally.HoReversalsTooLate);
                        }
                    }));
                else if (sims.FirstOrDefault(s => { lock (s.Gate) return s.Units.Any(u => u.Id == pick); }) is { } owner2)
                {
                    SimUnit u;
                    lock (owner2.Gate)
                    {
                        u = owner2.Units.Single(x => x.Id == pick);
                        if (correct) u.Imei = Imei(); else u.Reversed = true;
                    }
                    if (correct) Interlocked.Increment(ref tally.BranchCorrections); else Interlocked.Increment(ref tally.BranchReversals);
                    work.Add(PushAsync(owner2, [u], tally));
                }
            }
            foreach (var s in sims) work.Add(HeartbeatAsync(s, tally));
            await Task.WhenAll(work);
        }
        // The branch servers' next heartbeats bring every instruction still on its way.
        for (var drain = 0; drain < 5; drain++)
            if ((await Task.WhenAll(sims.Select(s => HeartbeatAsync(s, tally)))).Sum() == 0) break;

        // ---- head office against the test's own record
        var claimants = new Dictionary<string, HashSet<Guid>>();
        void Claim(string imei, Guid unit) { if (!claimants.TryGetValue(imei, out var set)) claimants[imei] = set = []; set.Add(unit); }
        foreach (var s in sims) foreach (var u in s.Units.Where(u => !u.Reversed)) Claim(u.Imei, u.Id);
        foreach (var (unit, imei) in ho) Claim(imei, unit);
        var values = claimants.Keys.Concat(pool).Distinct().ToArray();
        var registry = JsonSerializer.Deserialize<JsonElement>(await fx.QueryDbAsync<string>($"""
            SELECT coalesce(json_agg(json_build_object('value', value, 'unit', device_unit_id, 'status', status, 'conflict', conflict_id))::text, '[]')
              FROM device.central_registry WHERE status <> 'retired' AND value IN ({string.Join(',', values.Select(v => $"'{v}'"))})
            """)).EnumerateArray().Select(e => (Value: e.GetProperty("value").GetString()!, Unit: e.GetProperty("unit").GetGuid(), Status: e.GetProperty("status").GetString()!)).ToList();
        var openConflicts = JsonSerializer.Deserialize<JsonElement>(await fx.QueryDbAsync<string>($"""
            SELECT coalesce(json_agg(json_build_object('id', c.id, 'value', c.value,
                       'units', (SELECT json_agg(r.device_unit_id) FROM device.central_registry r WHERE r.conflict_id = c.id AND r.status <> 'retired')))::text, '[]')
              FROM device.imei_conflict c WHERE c.status = 'open' AND c.value IN ({string.Join(',', values.Select(v => $"'{v}'"))})
            """)).EnumerateArray().ToDictionary(e => e.GetProperty("value").GetString()!, e => (Id: e.GetProperty("id").GetGuid(),
                Units: e.GetProperty("units").EnumerateArray().Select(x => x.GetGuid()).ToHashSet()));
        var problems = new List<string>();
        foreach (var v in values)
        {
            var want = claimants.TryGetValue(v, out var set) ? set : [];
            var have = registry.Where(r => r.Value == v).ToList();
            if (!have.Select(r => r.Unit).ToHashSet().SetEquals(want)) problems.Add($"{v}: head office has {have.Count} devices, the record {want.Count}");
            if (want.Count > 0 && have.Count(r => r.Status == "active") != 1) problems.Add($"{v}: {have.Count(r => r.Status == "active")} active holders");
            if ((want.Count >= 2) != openConflicts.ContainsKey(v)) problems.Add($"{v}: {want.Count} claimants but conflict open = {openConflicts.ContainsKey(v)}");
            if (openConflicts.TryGetValue(v, out var oc) && !oc.Units.SetEquals(want)) problems.Add($"{v}: the open conflict does not list every claimant");
        }
        // Every device, on every server, is on hold exactly for the open conflicts it is in.
        HashSet<Guid> ConflictsOf(Guid unit) => openConflicts.Values.Where(c => c.Units.Contains(unit)).Select(c => c.Id).ToHashSet();
        foreach (var s in sims)
            foreach (var u in s.Units)
                if (!u.Holds.SetEquals(u.Reversed ? [] : ConflictsOf(u.Id))) problems.Add($"branch {s.B.Code} device {u.Id}: holds {u.Holds.Count}, open conflicts {ConflictsOf(u.Id).Count}");
        foreach (var unit in ho.Keys)
        {
            var holds = JsonSerializer.Deserialize<JsonElement>(await fx.QueryDbAsync<string>($"""
                SELECT coalesce(json_agg(conflict_id), '[]')::text FROM device.unit_hold WHERE device_unit_id = '{unit}' AND reason = 'imei_conflict' AND released_at IS NULL
                """)).EnumerateArray().Select(x => x.GetGuid()).ToHashSet();
            var status = await StatusAsync(owner, unit);
            if (!holds.SetEquals(ConflictsOf(unit))) problems.Add($"head office device {unit}: holds {holds.Count}, open conflicts {ConflictsOf(unit).Count}");
            if ((status == "quarantined") != (holds.Count > 0)) problems.Add($"head office device {unit} is {status} with {holds.Count} holds");
        }
        // The instruction stream: per device and conflict, a hold first, then at most one release.
        foreach (var s in sims)
            foreach (var g in s.Instructions.GroupBy(i => (i.Unit, i.Conflict)))
            {
                var kinds = string.Join(",", g.OrderBy(i => i.Seq).Select(i => i.Kind));
                if (kinds is not ("hold" or "hold,release")) problems.Add($"branch {s.B.Code} device {g.Key.Unit}: instructions {kinds}");
            }
        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(30)));
        await AssertConsistentAsync(owner);
        Assert.Equal(0L, await fx.QueryDbAsync<long>("SELECT count(*) FROM device.check_units()"));

        var stats = JsonSerializer.Deserialize<JsonElement>(await fx.QueryDbAsync<string>($"""
            SELECT json_build_object('opened', (SELECT count(*) FROM device.imei_conflict WHERE value IN ({string.Join(',', values.Select(v => $"'{v}'"))})),
                                     'resolved', (SELECT count(*) FROM device.imei_conflict WHERE status = 'resolved' AND value IN ({string.Join(',', values.Select(v => $"'{v}'"))})),
                                     'instructions', (SELECT count(*) FROM device.central_instruction WHERE server_id IN ({string.Join(',', sims.Select(s => $"'{s.B.ServerId}'"))})))::text
            """));
        var report = JsonSerializer.Serialize(new
        {
            branchServers = branchCount, rounds, unitsPerRound = perRound, poolSize = pool.Length, seconds = Math.Round((clock.Elapsed - setup).TotalSeconds, 1),
            branchUnits = tally.BranchUnits, pushes = tally.Pushes, headOfficeReceived = tally.HoReceived, headOfficeRefused = tally.HoRefused,
            conflictsOpened = stats.GetProperty("opened").GetInt64(), conflictsResolved = stats.GetProperty("resolved").GetInt64(), conflictsOpen = openConflicts.Count,
            branchCorrections = tally.BranchCorrections, branchReversals = tally.BranchReversals, headOfficeCorrections = tally.HoCorrections,
            headOfficeReversals = tally.HoReversals, headOfficeReversalsAfterResolution = tally.HoReversalsTooLate,
            instructions = stats.GetProperty("instructions").GetInt64(), staleAnswersIgnored = tally.StaleAnswers, olderInstructionsIgnored = tally.StaleInstructions,
            identifiersChecked = values.Length, problems = problems.Count,
        }, new JsonSerializerOptions { WriteIndented = true });
        output.WriteLine(report);
        if (Environment.GetEnvironmentVariable("KITE_P6_REPORT") is { Length: > 0 } path) await File.WriteAllTextAsync(path, report);
        foreach (var s in sims) await Central.DeactivateAsync(owner, s.B);
    }
}
