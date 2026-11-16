using System.Text.Json;
using Kite.Accounting;
using Kite.Host.Infrastructure;
using Kite.Persistence;
using Kite.Platform;
using Kite.Sales;
using Npgsql;

namespace Kite.Host.Features;

public sealed record EmergencyRecordInput(Guid Id, long Seq, string Kind, DateTimeOffset RecordedAt, JsonElement Payload, long? MonotonicMs = null);
public sealed record EmergencyUploadRequest(List<EmergencyRecordInput> Records);

public sealed record EmShiftOpen(Guid ShiftId, string Number, Guid CashierId, decimal OpeningFloat);
public sealed record EmLine(Guid VariantId, decimal Qty, decimal UnitPrice, decimal ListPrice, decimal Discount, decimal Net, decimal Tax, decimal Gross,
    decimal TaxRate, Guid TaxCodeId, string ProductType, string DescriptionEn, string DescriptionAr, int WarrantyDays = 0);
public sealed record EmPayment(string Method, decimal Amount, decimal? Tendered = null, string? CardTerminal = null, string? ApprovalCode = null, string? Reference = null);
public sealed record EmSale(Guid SaleId, Guid ShiftId, Guid CashierId, string Number, List<EmLine> Lines, List<EmPayment> Payments, decimal CashRounding,
    Guid? CustomerId = null, string? Note = null);
public sealed record EmCashEvent(Guid EventId, Guid ShiftId, string Kind, decimal Amount, string Reason, Guid CreatedBy);
public sealed record EmShiftClose(Guid ShiftId, Guid ClosedBy, decimal CountedCash, List<CardSettlementInput>? CardSettlements = null, string? VarianceReason = null);

/// <summary>
/// Emergency mode (6.4, pulled forward): a Windows terminal keeps selling from its encrypted local store while the
/// server cannot be reached, numbering documents from its own series, and uploads the records in order when the
/// server is back. Each record is applied once (terminal + sequence); a record that cannot be applied is kept as a
/// conflict for a manager, never dropped.
/// </summary>
public static class EmergencyEndpoints
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/pos/emergency");

        // What the terminal needs to sell alone: catalog with prices and stock, tax, policy, and who may sign in (PIN hashes).
        g.MapGet("/snapshot", async (HttpContext http, KiteDb db, ServerContext server, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var term = await PosTerminals.CurrentAsync(t, http);
                RequireEmergencyTerminal(term);
                http.User.Demand(Permissions.PosSell, term.BranchId);
                var biz = await BusinessInfo.LoadAsync(t);
                var policy = await PosPolicies.ForBranchAsync(t, biz.Id, term.BranchId);
                var today = BusinessDay.Today(biz.TimeZone, t.Clock);
                var items = await t.QueryAsync<PosItemDto>(CatalogEndpoints.PosItemSql("v.status = 'active' AND p.status = 'active' AND p.tracking = 'none'") + " ORDER BY v.sku",
                    new { q = (string?)null, branchId = term.BranchId, warehouse = term.WarehouseId, d = today.ToDateTime(TimeOnly.MinValue) });
                var barcodes = await t.RowsAsync("""
                    SELECT bc.code, bc.variant_id AS "variantId" FROM catalog.barcode bc JOIN catalog.variant v ON v.id = bc.variant_id
                      JOIN catalog.product p ON p.id = v.product_id WHERE v.status = 'active' AND p.status = 'active' AND p.tracking = 'none'
                    """);
                var users = await t.RowsAsync("""
                    SELECT u.id, u.username, u.display_name_en AS "displayNameEn", u.display_name_ar AS "displayNameAr", u.pin_hash AS "pinHash",
                           array_agg(DISTINCT rp.permission_code) AS permissions,
                           max(r.discount_limit_percent) AS "discountLimitPercent", max(r.discount_limit_amount) AS "discountLimitAmount"
                      FROM iam."user" u JOIN iam.user_branch_role ubr ON ubr.user_id = u.id AND (ubr.branch_id IS NULL OR ubr.branch_id = @branch)
                      JOIN iam.role r ON r.id = ubr.role_id JOIN iam.role_permission rp ON rp.role_id = ubr.role_id
                     WHERE u.status = 'active' AND u.pin_hash IS NOT NULL
                     GROUP BY u.id HAVING bool_or(rp.permission_code = 'POS.SELL')
                    """, new { branch = term.BranchId });
                await t.AuditAsync(new AuditEntry("pos.emergency_snapshot", "terminal", term.Id, After: Json.Obj(new { items = items.Count(), users = users.Count }), BranchId: term.BranchId));
                return new
                {
                    generatedAt = t.Clock.UtcNow, businessDate = today, business = biz, terminal = new { term.Id, term.Code, term.BranchId, term.BranchCode, term.BranchNameEn, term.BranchNameAr },
                    numberPrefix = $"EM-{term.BranchCode}-{term.Code}-", policy, items, barcodes, users,
                };
            }, ct))).RequireSignedIn();

        g.MapPost("/records", async (EmergencyUploadRequest req, HttpContext http, KiteDb db, LicenceService licence, CancellationToken ct) =>
        {
            if (req.Records is null || req.Records.Count == 0) return Results.Ok(new { results = Array.Empty<object>() });
            if (req.Records.Count > 1000) throw new KiteException(ErrorCodes.Validation, "Upload at most 1000 records at a time.", 400, "records");
            var term = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var term = await PosTerminals.CurrentAsync(t, http);
                RequireEmergencyTerminal(term);
                http.User.Demand(Permissions.PosSell, term.BranchId);
                return term;
            }, ct);
            var results = new List<object>();
            foreach (var r in req.Records.OrderBy(r => r.Seq))
            {
                if (r.Seq <= 0) throw new KiteException(ErrorCodes.Validation, "Sequence numbers start at 1.", 400, "records");
                var done = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.QuerySingleOrDefaultAsync<(Guid Id, string Result, string? Detail)>(
                    "SELECT id, result, result_detail FROM sales.emergency_record WHERE terminal_id = @tid AND terminal_seq = @Seq", new { tid = term.Id, r.Seq }), ct);
                if (done.Id != Guid.Empty)
                {
                    results.Add(new { r.Id, r.Seq, result = done.Result, detail = done.Detail, duplicate = true });
                    continue;
                }
                string result = "applied";
                string? detail = null;
                try
                {
                    detail = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
                    {
                        var d = await ApplyAsync(t, http, term, r);
                        await RecordAsync(t, term, r, "applied", d);
                        return d;
                    }, ct);
                }
                catch (Exception e) when (e is KiteException or PostgresException or JsonException or InvalidOperationException && !ct.IsCancellationRequested)
                {
                    result = "conflict";
                    detail = e is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : e.Message;
                    await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
                    {
                        await RecordAsync(t, term, r, "conflict", detail);
                        await t.AuditAsync(new AuditEntry("pos.emergency_conflict", "terminal", term.Id, After: Json.Obj(new { r.Seq, r.Kind, detail }), BranchId: term.BranchId));
                    }, ct);
                }
                results.Add(new { r.Id, r.Seq, result, detail, duplicate = false });
            }
            return Results.Ok(new { results });
        }).RequireSignedIn();

        g.MapGet("/records", async (string? result, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.RowsAsync("""
                SELECT e.id, t.code AS "terminalCode", b.code AS "branchCode", e.terminal_seq AS seq, e.kind, e.recorded_at AS "recordedAt", e.received_at AS "receivedAt",
                       e.result, e.result_detail AS detail
                  FROM sales.emergency_record e JOIN core.terminal t ON t.id = e.terminal_id JOIN core.branch b ON b.id = e.branch_id
                 WHERE (@result::text IS NULL OR e.result = @result) ORDER BY e.received_at DESC, e.terminal_seq DESC LIMIT 500
                """, new { result }), ct)))
            .RequirePermission(Permissions.PosReportsView);
    }

    private static void RequireEmergencyTerminal(PosTerminal term)
    {
        if (term.Kind != "windows" || !term.EmergencyEnabled)
            throw new KiteException(ErrorCodes.EmergencyNotEnabled, "Emergency mode is not switched on for this terminal (Windows terminals only).", 403);
    }

    private static Task RecordAsync(DbTx t, PosTerminal term, EmergencyRecordInput r, string result, string? detail) =>
        t.ExecuteAsync("""
            INSERT INTO sales.emergency_record (id, business_id, branch_id, terminal_id, terminal_seq, kind, recorded_at, monotonic_ms, payload, result, result_detail)
            VALUES (@Id, (SELECT business_id FROM core.terminal WHERE id = @tid), @branch, @tid, @Seq, @kind, @RecordedAt, @MonotonicMs, @payload::jsonb, @result, @detail)
            """, new { r.Id, tid = term.Id, branch = term.BranchId, r.Seq, kind = r.Kind is "shift_open" or "sale" or "cash_event" or "shift_close" ? r.Kind : "sale",
                       r.RecordedAt, r.MonotonicMs, payload = r.Payload.GetRawText(), result, detail });

    private static T Payload<T>(EmergencyRecordInput r) =>
        r.Payload.Deserialize<T>(Json.Web) ?? throw new KiteException(ErrorCodes.EmergencyRecordInvalid, $"Record {r.Seq} has no payload.");

    private static async Task<string?> ApplyAsync(DbTx t, HttpContext http, PosTerminal term, EmergencyRecordInput r)
    {
        var biz = await BusinessInfo.LoadAsync(t);
        switch (r.Kind)
        {
            case "shift_open":
            {
                var p = Payload<EmShiftOpen>(r);
                if (!p.Number.StartsWith("EM", StringComparison.Ordinal)) throw new KiteException(ErrorCodes.EmergencyRecordInvalid, "Emergency shift numbers start with EM.");
                if (!(await PosPermissions.InBranchAsync(t, p.CashierId, term.BranchId)).Contains(Permissions.PosSell))
                    throw new KiteException(ErrorCodes.EmergencyRecordInvalid, "The cashier of that shift may not sell in this branch.");
                await t.ExecuteAsync("""
                    INSERT INTO sales.shift (id, business_id, branch_id, terminal_id, cashier_id, number, is_emergency, opened_at, opening_float, origin_server_id)
                    VALUES (@ShiftId, @b, @branch, @tid, @CashierId, @Number, true, @at, @OpeningFloat, (SELECT id FROM core.server WHERE is_self))
                    """, new { p.ShiftId, b = biz.Id, branch = term.BranchId, tid = term.Id, p.CashierId, p.Number, at = r.RecordedAt, p.OpeningFloat });
                await t.PublishAsync("sales.shift_opened", "shift", p.ShiftId, 1, Json.Obj(new { p.Number, terminal = term.Code, opening = p.OpeningFloat, emergency = true }), term.BranchId);
                return null;
            }
            case "sale":
            {
                var p = Payload<EmSale>(r);
                var shift = await EmergencyShiftAsync(t, term, p.ShiftId);
                if (!p.Number.StartsWith("EM-", StringComparison.Ordinal)) throw new KiteException(ErrorCodes.EmergencyRecordInvalid, "Emergency numbers start with EM-.");
                if (p.Lines is null || p.Lines.Count == 0) throw new KiteException(ErrorCodes.EmergencyRecordInvalid, "The sale has no lines.");
                foreach (var l in p.Lines)
                    if (l.Qty <= 0 || l.Net < 0 || l.Tax < 0 || l.Gross != l.Net + l.Tax || l.Discount < 0)
                        throw new KiteException(ErrorCodes.EmergencyRecordInvalid, $"A line of {p.Number} does not add up.");
                var gross = p.Lines.Sum(l => l.Gross);
                var tenders = (p.Payments ?? []).Select(x => new CheckedTender(x.Method, x.Amount, x.Tendered, x.Tendered is { } td ? td - x.Amount : null,
                    x.CardTerminal ?? (x.Method == "card" ? "UNKNOWN" : null), x.ApprovalCode ?? (x.Method == "card" ? "EMERGENCY" : null), x.Reference)).ToArray();
                if (tenders.Any(x => !PosMath.Methods.Contains(x.Method) || x.Amount <= 0))
                    throw new KiteException(ErrorCodes.EmergencyRecordInvalid, $"A payment of {p.Number} is not valid.");
                if (tenders.Sum(x => x.Amount) != gross + p.CashRounding)
                    throw new KiteException(ErrorCodes.EmergencyRecordInvalid, $"Payments of {p.Number} do not match its total.");
                var lines = p.Lines.Select(l => new DraftLine(l.VariantId, l.ProductType, l.DescriptionEn, l.DescriptionAr, l.Qty, l.UnitPrice, l.ListPrice, l.Discount,
                    l.Net, l.TaxCodeId, l.TaxRate, l.Tax, l.Gross, null, l.WarrantyDays)).ToArray();
                // The product type decides the accounts: take it from the catalog, not from the terminal.
                var types = (await t.QueryAsync<(Guid Id, string Type)>("SELECT v.id, p.product_type FROM catalog.variant v JOIN catalog.product p ON p.id = v.product_id WHERE v.id = ANY(@ids)",
                    new { ids = lines.Select(l => l.VariantId).ToArray() })).ToDictionary(x => x.Id, x => x.Type);
                lines = lines.Select(l => types.TryGetValue(l.VariantId, out var ty) ? l with { ProductType = ty }
                    : throw new KiteException(ErrorCodes.EmergencyRecordInvalid, "An item of the sale is not in the catalog.")).ToArray();
                var policy = await PosPolicies.ForBranchAsync(t, biz.Id, term.BranchId);
                var written = await SaleWriter.WriteAsync(t, biz, term, policy, new SaleDraft(p.SaleId, shift.Id, shift.CashierId, p.CustomerId, "simplified_invoice",
                    null, null, null, r.RecordedAt, lines, new CheckedPayments(tenders, p.CashRounding, gross + p.CashRounding), false, true, p.Number, p.Note));
                return written.StockExceptions > 0 ? $"{written.StockExceptions} item(s) went below zero stock; see stock exceptions." : null;
            }
            case "cash_event":
            {
                var p = Payload<EmCashEvent>(r);
                var shift = await EmergencyShiftAsync(t, term, p.ShiftId);
                if (p.Kind is not ("pay_in" or "pay_out" or "drop" or "no_sale")) throw new KiteException(ErrorCodes.EmergencyRecordInvalid, "Unknown cash event.");
                await t.ExecuteAsync("""
                    INSERT INTO sales.cash_event (id, business_id, branch_id, shift_id, kind, amount, reason, created_at, created_by)
                    VALUES (@EventId, @b, @branch, @ShiftId, @Kind, @amount, @Reason, @at, @CreatedBy)
                    """, new { p.EventId, b = biz.Id, branch = term.BranchId, p.ShiftId, p.Kind, amount = p.Kind == "no_sale" ? 0m : p.Amount, p.Reason, at = r.RecordedAt, p.CreatedBy });
                if (p.Kind == "pay_out")
                    await Posting.PostAsync(t, biz.Id, term.BranchId, PostingRuleTemplates.CashPayOut, new PostingFacts().Set("amount", p.Amount),
                        BusinessDay.DateOf(r.RecordedAt, biz.TimeZone), "cash_event", p.EventId, $"Cash paid out (emergency), shift {shift.Number}: {p.Reason}");
                return null;
            }
            case "shift_close":
            {
                var p = Payload<EmShiftClose>(r);
                var shift = await EmergencyShiftAsync(t, term, p.ShiftId);
                var f = await PosEndpoints.FiguresAsync(t, shift);
                var variance = p.CountedCash - f.ExpectedCash;
                foreach (var cs in p.CardSettlements ?? [])
                {
                    var recorded = f.CardByTerminal.FirstOrDefault(x => string.Equals(x.CardTerminal, cs.CardTerminal, StringComparison.OrdinalIgnoreCase))?.Total ?? 0m;
                    await t.ExecuteAsync("""
                        INSERT INTO sales.card_settlement (id, business_id, branch_id, shift_id, card_terminal, batch_total, recorded_total, difference, reason, created_by)
                        VALUES (@id, @b, @branch, @shift, @CardTerminal, @BatchTotal, @recorded, @diff, @reason, @by)
                        """, new { id = t.NewId(), b = biz.Id, branch = term.BranchId, shift = shift.Id, cs.CardTerminal, cs.BatchTotal, recorded, diff = cs.BatchTotal - recorded,
                                   reason = cs.BatchTotal == recorded ? cs.Reason : cs.Reason ?? "Recorded in emergency mode", by = p.ClosedBy });
                }
                if (variance != 0)
                    await Posting.PostAsync(t, biz.Id, term.BranchId, PostingRuleTemplates.ShiftVariance, new PostingFacts().Set("variance", variance),
                        BusinessDay.DateOf(r.RecordedAt, biz.TimeZone), "shift", shift.Id, $"Cash {(variance > 0 ? "over" : "short")} at close of emergency shift {shift.Number}");
                await t.ExecuteAsync("""
                    UPDATE sales.shift SET status = 'closed', closed_at = @at, closed_by = @ClosedBy, expected_cash = @expected, counted_cash = @CountedCash,
                           variance = @variance, variance_reason = @VarianceReason, totals = @totals::jsonb WHERE id = @id
                    """, new { id = shift.Id, at = r.RecordedAt, p.ClosedBy, expected = f.ExpectedCash, p.CountedCash, variance, p.VarianceReason,
                               totals = JsonSerializer.Serialize(f, Json.Web) });
                await t.PublishAsync("sales.shift_closed", "shift", shift.Id, 2, Json.Obj(new { shift.Number, expected = f.ExpectedCash, counted = p.CountedCash, variance, emergency = true }), term.BranchId);
                var policy = await PosPolicies.ForBranchAsync(t, biz.Id, term.BranchId);
                return Math.Abs(variance) > policy.VarianceThreshold ? $"Variance {variance:0.000} is above the threshold; a manager should review shift {shift.Number}." : null;
            }
            default:
                throw new KiteException(ErrorCodes.EmergencyRecordInvalid, $"Unknown record kind {r.Kind}.");
        }
    }

    private static async Task<ShiftRow> EmergencyShiftAsync(DbTx t, PosTerminal term, Guid shiftId)
    {
        var s = await t.QuerySingleOrDefaultAsync<ShiftRow>("""
            SELECT id, branch_id AS BranchId, terminal_id AS TerminalId, cashier_id AS CashierId, number, status, is_emergency AS IsEmergency,
                   opened_at AS OpenedAt, opening_float AS OpeningFloat, closed_at AS ClosedAt, expected_cash AS ExpectedCash, counted_cash AS CountedCash,
                   variance, variance_reason AS VarianceReason, variance_accepted_by AS VarianceAcceptedBy, totals::text AS Totals, version
              FROM sales.shift WHERE id = @shiftId FOR UPDATE
            """, new { shiftId }) ?? throw new KiteException(ErrorCodes.EmergencyRecordInvalid, "The emergency shift of this record was not uploaded first.");
        if (!s.IsEmergency || s.TerminalId != term.Id) throw new KiteException(ErrorCodes.EmergencyRecordInvalid, "That shift is not an emergency shift of this terminal.");
        if (s.Status != "open") throw new KiteException(ErrorCodes.EmergencyRecordInvalid, $"Emergency shift {s.Number} is already closed.");
        return s;
    }
}
