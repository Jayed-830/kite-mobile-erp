using Kite.Devices;
using System.Text.Json;
using Kite.Accounting;
using Kite.Host.Infrastructure;
using Kite.Identity;
using Kite.Persistence;
using Kite.Platform;
using Kite.Sales;
using Npgsql;

namespace Kite.Host.Features;

public sealed record OpenShiftRequest(decimal? OpeningFloat, Dictionary<string, int>? OpeningCount);
public sealed record CashEventRequest(string Kind, decimal Amount, string Reason, Guid? ApprovalId = null);
public sealed record CardSettlementInput(string CardTerminal, decimal BatchTotal, string? Reason);
public sealed record CloseShiftRequest(Dictionary<string, int>? ClosingCount, decimal? CountedCash, List<CardSettlementInput>? CardSettlements,
    string? VarianceReason, Guid? ApprovalId = null);
public sealed record CartLineInput(Guid VariantId, decimal Qty, decimal? UnitPrice = null, decimal? Discount = null, Guid? ApprovalId = null,
    Guid? DeviceUnitId = null, Guid? DamagedApprovalId = null);
public sealed record CompleteSaleRequest(List<CartLineInput> Lines, List<TenderInput>? Payments, string? DocType = null, Guid? CustomerId = null,
    decimal? ExpectedTotal = null, string? Note = null, Guid? ApprovalId = null);
public sealed record ReturnLineInput(Guid LineId, decimal Qty, bool Restock = true, string? Code = null);
public sealed record ReturnRequest(Guid Id, List<ReturnLineInput> Lines, List<TenderInput> Refunds, string? Reason, Guid? ApprovalId = null,
    Guid? ExchangeGroupId = null);
public sealed record VoidRequest(Guid Id, string Reason, Guid? ApprovalId = null);
public sealed record ParkRequest(Guid Id, string? Label, JsonElement Cart, List<Guid>? DeviceUnitIds = null);
public sealed record PinApprovalRequest(string Action, string ApproverUsername, string Pin, JsonElement? Context = null, string? Reason = null);
public sealed record RemoteApprovalRequest(string Action, JsonElement? Context = null, string? Reason = null);
public sealed record DecideApprovalRequest(string? Reason);
public sealed record PosPolicyRequest(Guid? BranchId, string NegativeStock, int ReturnWindowDays, decimal CashRefundCap, decimal VarianceThreshold,
    bool BlindClose, string RoundingLevel, decimal CashRounding, decimal[]? Denominations, string? ReceiptFooterEn, string? ReceiptFooterAr, int? Version);

public sealed record ShiftRow(Guid Id, Guid BranchId, Guid TerminalId, Guid CashierId, string Number, string Status, bool IsEmergency,
    DateTime OpenedAt, decimal OpeningFloat, DateTime? ClosedAt, decimal? ExpectedCash, decimal? CountedCash, decimal? Variance,
    string? VarianceReason, Guid? VarianceAcceptedBy, string? Totals, int Version);

public sealed record ShiftFigures(
    int SalesCount, decimal SalesGross, decimal SalesNet, decimal SalesTax, decimal SalesDiscount, int CreditNotesCount, decimal CreditNotesGross,
    decimal CashRounding, decimal CashPayments, decimal CardPayments, decimal TransferPayments, decimal PayIns, decimal PayOuts, decimal Drops,
    int NoSales, decimal OpeningFloat, decimal ExpectedCash, IReadOnlyList<CardTerminalTotal> CardByTerminal);

public sealed record CardTerminalTotal(string CardTerminal, decimal Total);

public sealed record SaleSummary(Guid Id, string Number, string DocType, decimal Gross, decimal CashRounding, decimal Paid, decimal Change, int StockExceptions);

/// <summary>POS: policy, shifts and cash (10.4), approvals (10.2, 18.3), sales, returns, voids and parked carts (10.2, 10.3).</summary>
public static class PosEndpoints
{
    public static readonly string[] ApprovableActions =
    [
        Permissions.PosDiscountAboveLimit, Permissions.PosPriceOverride, Permissions.PosSaleVoid, Permissions.PosRefund,
        Permissions.PosCashPayOut, Permissions.PosDrawerNoSale, Permissions.PosShiftAcceptVariance, Permissions.DeviceSellDamaged,
    ];

    private const string ShiftSelect = """
        SELECT id, branch_id AS BranchId, terminal_id AS TerminalId, cashier_id AS CashierId, number, status, is_emergency AS IsEmergency,
               opened_at AS OpenedAt, opening_float AS OpeningFloat, closed_at AS ClosedAt, expected_cash AS ExpectedCash, counted_cash AS CountedCash,
               variance, variance_reason AS VarianceReason, variance_accepted_by AS VarianceAcceptedBy, totals::text AS Totals, version
          FROM sales.shift
        """;

    public static void Map(WebApplication app)
    {
        MapPolicy(app);
        MapShifts(app);
        MapApprovals(app);
        MapSales(app);
    }

    // ------------------------------------------------------------------ policy
    private static void MapPolicy(WebApplication app)
    {
        app.MapGet("/api/v1/pos/policy", async (Guid? branchId, HttpContext http, KiteDb db, ServerContext server, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var b = branchId ?? (http.User.TerminalIdOf() is { } tid ? (await PosTerminals.ByIdAsync(t, tid)).BranchId : (Guid?)null);
                return b is null
                    ? await t.QuerySingleOrDefaultAsync<PosPolicy>(PosPolicies.Select + " WHERE branch_id IS NULL") ?? new PosPolicy()
                    : await PosPolicies.ForBranchAsync(t, server.BusinessId!.Value, b.Value);
            }, ct))).RequireSignedIn();

        app.MapPut("/api/v1/pos/policy", async (PosPolicyRequest req, HttpContext http, KiteDb db, ServerContext server, CancellationToken ct) =>
        {
            http.User.Demand(Permissions.PosPolicyManage, req.BranchId);
            if (req.NegativeStock is not ("block" or "allow")) throw new KiteException(ErrorCodes.Validation, "Negative stock must be block or allow.", 400, "negativeStock");
            if (req.RoundingLevel is not ("line" or "invoice")) throw new KiteException(ErrorCodes.Validation, "Rounding must be line or invoice.", 400, "roundingLevel");
            if (req.CashRounding is not (0m or 0.005m or 0.010m or 0.050m or 0.100m))
                throw new KiteException(ErrorCodes.Validation, "Cash rounding must be 0, 0.005, 0.010, 0.050 or 0.100.", 400, "cashRounding");
            var policy = await db.InTransactionAsync(req.BranchId is null ? AccessScope.System : http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var before = await t.QuerySingleOrDefaultAsync<PosPolicy>(PosPolicies.Select + " WHERE branch_id IS NOT DISTINCT FROM @b FOR UPDATE", new { b = req.BranchId });
                if (before is not null && req.Version is { } v && v != before.Version)
                    throw new KiteException(ErrorCodes.VersionMismatch, "Someone else changed the policy. Reload and try again.", 412);
                var uid = http.User.UserIdOf();
                var p = new
                {
                    id = before?.Id ?? t.NewId(), business = server.BusinessId, b = req.BranchId, req.NegativeStock, req.ReturnWindowDays, req.CashRefundCap,
                    req.VarianceThreshold, req.BlindClose, req.RoundingLevel, req.CashRounding, den = req.Denominations ?? before?.Denominations ?? new PosPolicy().Denominations,
                    en = req.ReceiptFooterEn, ar = req.ReceiptFooterAr, uid,
                };
                if (before is null)
                    await t.ExecuteAsync("""
                        INSERT INTO sales.pos_policy (id, business_id, branch_id, negative_stock, return_window_days, cash_refund_cap, variance_threshold, blind_close,
                                                      rounding_level, cash_rounding, denominations, receipt_footer_en, receipt_footer_ar, created_by)
                        VALUES (@id, @business, @b, @NegativeStock, @ReturnWindowDays, @CashRefundCap, @VarianceThreshold, @BlindClose, @RoundingLevel,
                                @CashRounding, @den, @en, @ar, @uid)
                        """, p);
                else
                    await t.ExecuteAsync("""
                        UPDATE sales.pos_policy SET negative_stock = @NegativeStock, return_window_days = @ReturnWindowDays, cash_refund_cap = @CashRefundCap,
                               variance_threshold = @VarianceThreshold, blind_close = @BlindClose, rounding_level = @RoundingLevel, cash_rounding = @CashRounding,
                               denominations = @den, receipt_footer_en = @en, receipt_footer_ar = @ar, updated_by = @uid WHERE id = @id
                        """, p);
                await t.AuditAsync(new AuditEntry("pos.policy_changed", "pos_policy", p.id, Before: before is null ? null : Json.Obj(before), After: Json.Obj(req), BranchId: req.BranchId));
                return await t.QuerySingleOrDefaultAsync<PosPolicy>(PosPolicies.Select + " WHERE id = @id", new { p.id });
            }, ct);
            return Results.Ok(policy);
        }).RequireSignedIn();
    }

    // ------------------------------------------------------------------ shifts and cash
    private static void MapShifts(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/pos/shifts");

        g.MapPost("", async (OpenShiftRequest req, HttpContext http, KiteDb db, ServerContext server, LicenceService licence, CancellationToken ct) =>
        {
            await licence.RequireTransactAsync(ct);
            var shift = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var term = await PosTerminals.CurrentAsync(t, http);
                http.User.Demand(Permissions.PosShiftOpen, term.BranchId);
                var policy = await PosPolicies.ForBranchAsync(t, server.BusinessId!.Value, term.BranchId);
                var counted = PosMath.CountTotal(req.OpeningCount, policy.Denominations);
                var opening = req.OpeningFloat ?? counted;
                if (req.OpeningCount is not null && req.OpeningFloat is { } f && f != counted)
                    throw new KiteException(ErrorCodes.Validation, $"The count adds up to {counted:0.000}, not {f:0.000}.", 400, "openingFloat");
                if (opening < 0) throw new KiteException(ErrorCodes.Validation, "The opening cash cannot be negative.", 400, "openingFloat");
                var biz = await BusinessInfo.LoadAsync(t);
                var fy = await FiscalPeriods.FiscalYearOfAsync(t, biz.Id, BusinessDay.Today(biz.TimeZone, t.Clock));
                var number = await DocNumbers.NextAsync(t, biz.Id, term.BranchId, "shift", fy, "SH");
                var id = t.NewId();
                await t.ExecuteAsync("""
                    INSERT INTO sales.shift (id, business_id, branch_id, terminal_id, cashier_id, number, opening_float, opening_count, origin_server_id)
                    VALUES (@id, @business, @branch, @terminal, @cashier, @number, @opening, @count::jsonb, (SELECT id FROM core.server WHERE is_self))
                    """, new { id, business = biz.Id, branch = term.BranchId, terminal = term.Id, cashier = http.User.UserIdOf(), number, opening,
                               count = JsonSerializer.Serialize(req.OpeningCount ?? new Dictionary<string, int>()) });
                await t.AuditAsync(new AuditEntry("pos.shift_opened", "shift", id, After: Json.Obj(new { number, terminal = term.Code, opening }), BranchId: term.BranchId));
                await t.PublishAsync("sales.shift_opened", "shift", id, 1, Json.Obj(new { number, terminal = term.Code, opening }), term.BranchId);
                return await t.QuerySingleOrDefaultAsync<ShiftRow>(ShiftSelect + " WHERE id = @id", new { id });
            }, ct);
            return Results.Created($"/api/v1/pos/shifts/{shift!.Id}", shift);
        }).RequireSignedIn();

        // The terminal's open shift with live (X) figures; expected cash is hidden on a blind close for cashiers.
        g.MapGet("/current", async (HttpContext http, KiteDb db, ServerContext server, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var term = await PosTerminals.CurrentAsync(t, http);
                var s = await t.QuerySingleOrDefaultAsync<ShiftRow>(ShiftSelect + " WHERE terminal_id = @id AND status = 'open' AND NOT is_emergency", new { id = term.Id });
                if (s is null) return (object)new { shift = (ShiftRow?)null };
                var policy = await PosPolicies.ForBranchAsync(t, server.BusinessId!.Value, term.BranchId);
                var figures = await FiguresAsync(t, s);
                var hide = policy.BlindClose && !http.User.HasIn(Permissions.PosReportsView, term.BranchId);
                return new { shift = s, figures = hide ? figures with { ExpectedCash = 0m, CashPayments = 0m } : figures, blind = hide };
            }, ct))).RequireSignedIn();

        g.MapGet("", async (string? status, DateOnly? date, Guid? branchId, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.QueryAsync<ShiftRow>(ShiftSelect + """
                 WHERE (@status::text IS NULL OR status = @status) AND (@branchId::uuid IS NULL OR branch_id = @branchId)
                   AND (@d::date IS NULL OR opened_at::date = @d::date)
                 ORDER BY opened_at DESC LIMIT 200
                """, new { status, branchId, d = date?.ToDateTime(TimeOnly.MinValue) }), ct)))
            .RequirePermission(Permissions.PosReportsView);

        // X report for an open shift, Z report (frozen totals) for a closed one.
        g.MapGet("/{id:guid}", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var s = await t.QuerySingleOrDefaultAsync<ShiftRow>(ShiftSelect + " WHERE id = @id", new { id })
                    ?? throw new KiteException(ErrorCodes.NotFound, "Shift not found in your branches.", 404);
                if (s.CashierId != http.User.UserIdOf()) http.User.Demand(Permissions.PosReportsView, s.BranchId);
                var figures = s.Status == "closed" && s.Totals is not null
                    ? JsonSerializer.Deserialize<ShiftFigures>(s.Totals, Json.Web)!
                    : await FiguresAsync(t, s);
                var settlements = await t.QueryAsync<(string CardTerminal, decimal BatchTotal, decimal RecordedTotal, decimal Difference, string? Reason)>(
                    "SELECT card_terminal, batch_total, recorded_total, difference, reason FROM sales.card_settlement WHERE shift_id = @id ORDER BY card_terminal", new { id });
                var events = await t.QueryAsync<(string Kind, decimal Amount, string Reason, DateTime CreatedAt)>(
                    "SELECT kind, amount, reason, created_at FROM sales.cash_event WHERE shift_id = @id ORDER BY created_at", new { id });
                return new
                {
                    shift = s, report = s.Status == "closed" ? "Z" : "X", figures,
                    cardSettlements = settlements.Select(x => new { x.CardTerminal, x.BatchTotal, x.RecordedTotal, x.Difference, x.Reason }),
                    cashEvents = events.Select(x => new { x.Kind, x.Amount, x.Reason, x.CreatedAt }),
                };
            }, ct))).RequireSignedIn();

        g.MapPost("/current/cash-events", async (CashEventRequest req, HttpContext http, KiteDb db, ServerContext server, LicenceService licence, CancellationToken ct) =>
        {
            await licence.RequireTransactAsync(ct);
            if (req.Kind is not ("pay_in" or "pay_out" or "drop" or "no_sale"))
                throw new KiteException(ErrorCodes.Validation, "Kind must be pay_in, pay_out, drop or no_sale.", 400, "kind");
            if (string.IsNullOrWhiteSpace(req.Reason)) throw new KiteException(ErrorCodes.Validation, "Give a reason.", 400, "reason");
            var amount = req.Kind == "no_sale" ? 0m : req.Amount;
            if (req.Kind != "no_sale" && (amount <= 0 || amount != Currency.OMR.Round(amount)))
                throw new KiteException(ErrorCodes.Validation, "Amount must be above zero with at most 3 decimals.", 400, "amount");
            var result = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var term = await PosTerminals.CurrentAsync(t, http);
                var s = await OpenShiftForUpdateAsync(t, term, http);
                var uid = http.User.UserIdOf();
                var id = t.NewId();
                var needed = req.Kind switch { "pay_out" => Permissions.PosCashPayOut, "no_sale" => Permissions.PosDrawerNoSale, _ => Permissions.PosShiftOpen };
                Guid? approvalId = null;
                if (!http.User.HasIn(needed, term.BranchId))
                {
                    if (req.ApprovalId is null || needed == Permissions.PosShiftOpen)
                        throw new KiteException(ErrorCodes.Forbidden, $"{needed} is needed. Ask a manager to approve.", 403, "approvalId");
                    approvalId = (await Approvals.UseAsync(t, req.ApprovalId.Value, uid, term.BranchId, needed, "cash_event", id)).Id;
                }
                if (req.Kind is "pay_out" or "drop")
                {
                    var f = await FiguresAsync(t, s);
                    if (amount > f.ExpectedCash)
                        throw new KiteException(ErrorCodes.Validation, "The drawer does not hold that much cash.", 400, "amount");
                }
                await t.ExecuteAsync("""
                    INSERT INTO sales.cash_event (id, business_id, branch_id, shift_id, kind, amount, reason, approval_id, created_by)
                    VALUES (@id, @business, @branch, @shift, @Kind, @amount, @reason, @approvalId, @uid)
                    """, new { id, business = server.BusinessId, branch = term.BranchId, shift = s.Id, req.Kind, amount, reason = req.Reason.Trim(), approvalId, uid });
                if (req.Kind == "pay_out")
                {
                    var biz = await BusinessInfo.LoadAsync(t);
                    await Posting.PostAsync(t, biz.Id, term.BranchId, PostingRuleTemplates.CashPayOut, new PostingFacts().Set("amount", amount),
                        BusinessDay.Today(biz.TimeZone, t.Clock), "cash_event", id, $"Cash paid out, shift {s.Number}: {req.Reason.Trim()}");
                }
                await t.AuditAsync(new AuditEntry($"pos.cash_{req.Kind}", "cash_event", id, After: Json.Obj(new { shift = s.Number, amount, req.Reason, approvalId }), BranchId: term.BranchId));
                await t.PublishAsync("sales.cash_event", "cash_event", id, 1, Json.Obj(new { shift = s.Number, req.Kind, amount }), term.BranchId);
                return new { id, kind = req.Kind, amount, openDrawer = true };
            }, ct);
            return Results.Ok(result);
        }).RequireSignedIn();

        g.MapPost("/current/close", async (CloseShiftRequest req, HttpContext http, KiteDb db, ServerContext server, CancellationToken ct) =>
        {
            var result = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var term = await PosTerminals.CurrentAsync(t, http);
                var s = await OpenShiftForUpdateAsync(t, term, http, allowManager: true);
                return await CloseAsync(t, http, server, term, s, req);
            }, ct);
            return Results.Ok(result);
        }).RequireSignedIn();

        // A manager closes a shift left open on another terminal (e.g. the cashier went home).
        g.MapPost("/{id:guid}/close", async (Guid id, CloseShiftRequest req, HttpContext http, KiteDb db, ServerContext server, CancellationToken ct) =>
        {
            var result = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var s = await t.QuerySingleOrDefaultAsync<ShiftRow>(ShiftSelect + " WHERE id = @id FOR UPDATE", new { id })
                    ?? throw new KiteException(ErrorCodes.NotFound, "Shift not found in your branches.", 404);
                if (s.Status != "open") throw new KiteException(ErrorCodes.NoOpenShift, "That shift is already closed.", 409);
                http.User.Demand(Permissions.PosShiftAcceptVariance, s.BranchId);
                var term = await PosTerminals.ByIdAsync(t, s.TerminalId);
                return await CloseAsync(t, http, server, term, s, req);
            }, ct);
            return Results.Ok(result);
        }).RequireSignedIn();
    }

    private static async Task<object> CloseAsync(DbTx t, HttpContext http, ServerContext server, PosTerminal term, ShiftRow s, CloseShiftRequest req)
    {
        var biz = await BusinessInfo.LoadAsync(t);
        var policy = await PosPolicies.ForBranchAsync(t, biz.Id, term.BranchId);
        if (req.ClosingCount is null && req.CountedCash is null)
            throw new KiteException(ErrorCodes.Validation, "Count the cash in the drawer.", 400, "closingCount");
        var counted = req.ClosingCount is not null ? PosMath.CountTotal(req.ClosingCount, policy.Denominations) : req.CountedCash!.Value;
        if (req.ClosingCount is not null && req.CountedCash is { } c && c != counted)
            throw new KiteException(ErrorCodes.Validation, $"The count adds up to {counted:0.000}, not {c:0.000}.", 400, "countedCash");
        var f = await FiguresAsync(t, s);
        var variance = counted - f.ExpectedCash;
        var uid = http.User.UserIdOf();

        Guid? acceptedBy = null;
        if (Math.Abs(variance) > policy.VarianceThreshold)
        {
            if (string.IsNullOrWhiteSpace(req.VarianceReason))
                throw new KiteException(ErrorCodes.VarianceNeedsAcceptance, "The count does not match the expected cash. Recount, or give a reason and ask a manager to accept it.", 409, "varianceReason");
            if (http.User.HasIn(Permissions.PosShiftAcceptVariance, term.BranchId)) acceptedBy = uid;
            else if (req.ApprovalId is { } aid)
                acceptedBy = (await Approvals.UseAsync(t, aid, uid, term.BranchId, Permissions.PosShiftAcceptVariance, "shift", s.Id)).ApproverId;
            else
                throw new KiteException(ErrorCodes.VarianceNeedsAcceptance, "The count does not match the expected cash. A manager must accept the difference.", 409, "approvalId");
        }

        // Standalone bank terminals: every terminal with card payments needs its printed batch total (10.4).
        var given = (req.CardSettlements ?? []).GroupBy(x => x.CardTerminal?.Trim() ?? "", StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);
        var missing = f.CardByTerminal.Where(x => x.Total != 0 && !given.ContainsKey(x.CardTerminal)).Select(x => x.CardTerminal).ToArray();
        if (missing.Length > 0)
            throw new KiteException(ErrorCodes.Validation, $"Enter the batch total printed by bank terminal {string.Join(", ", missing)}.", 400, "cardSettlements");
        foreach (var (name, cs) in given)
        {
            if (name.Length == 0) throw new KiteException(ErrorCodes.Validation, "Name the bank terminal.", 400, "cardSettlements");
            var recorded = f.CardByTerminal.FirstOrDefault(x => string.Equals(x.CardTerminal, name, StringComparison.OrdinalIgnoreCase))?.Total ?? 0m;
            var diff = cs.BatchTotal - recorded;
            if (diff != 0 && string.IsNullOrWhiteSpace(cs.Reason))
                throw new KiteException(ErrorCodes.Validation, $"Bank terminal {name}: the batch total differs from the card sales by {diff:0.000}. Give a reason.", 400, "cardSettlements");
            await t.ExecuteAsync("""
                INSERT INTO sales.card_settlement (id, business_id, branch_id, shift_id, card_terminal, batch_total, recorded_total, difference, reason, created_by)
                VALUES (@id, @business, @branch, @shift, @name, @BatchTotal, @recorded, @diff, @Reason, @uid)
                """, new { id = t.NewId(), business = biz.Id, branch = term.BranchId, shift = s.Id, name, cs.BatchTotal, recorded, diff, cs.Reason, uid });
        }

        if (variance != 0)
            await Posting.PostAsync(t, biz.Id, term.BranchId, PostingRuleTemplates.ShiftVariance, new PostingFacts().Set("variance", variance),
                BusinessDay.Today(biz.TimeZone, t.Clock), "shift", s.Id, $"Cash {(variance > 0 ? "over" : "short")} at close of shift {s.Number}");

        await t.ExecuteAsync("""
            UPDATE sales.shift SET status = 'closed', closed_at = now(), closed_by = @uid, expected_cash = @expected, counted_cash = @counted,
                   closing_count = @count::jsonb, variance = @variance, variance_reason = @reason, variance_accepted_by = @acceptedBy,
                   variance_accepted_at = CASE WHEN @acceptedBy::uuid IS NULL THEN NULL ELSE now() END, totals = @totals::jsonb
             WHERE id = @id
            """, new { id = s.Id, uid, expected = f.ExpectedCash, counted, count = req.ClosingCount is null ? null : JsonSerializer.Serialize(req.ClosingCount),
                       variance, reason = req.VarianceReason, acceptedBy, totals = JsonSerializer.Serialize(f, Json.Web) });
        await t.AuditAsync(new AuditEntry("pos.shift_closed", "shift", s.Id,
            After: Json.Obj(new { s.Number, expected = f.ExpectedCash, counted, variance, req.VarianceReason, acceptedBy }), BranchId: term.BranchId));
        await t.PublishAsync("sales.shift_closed", "shift", s.Id, s.Version + 1, Json.Obj(new { s.Number, expected = f.ExpectedCash, counted, variance }), term.BranchId);
        return new { id = s.Id, s.Number, report = "Z", figures = f, counted, variance, varianceAcceptedBy = acceptedBy };
    }

    private static async Task<ShiftRow> OpenShiftForUpdateAsync(DbTx t, PosTerminal term, HttpContext http, bool allowManager = false)
    {
        var s = await t.QuerySingleOrDefaultAsync<ShiftRow>(ShiftSelect + " WHERE terminal_id = @id AND status = 'open' AND NOT is_emergency FOR UPDATE", new { id = term.Id })
            ?? throw new KiteException(ErrorCodes.NoOpenShift, "Open a shift on this terminal first.", 409);
        if (s.CashierId != http.User.UserIdOf() && !(allowManager && http.User.HasIn(Permissions.PosShiftAcceptVariance, term.BranchId)))
            throw new KiteException(ErrorCodes.ShiftNotYours, "This terminal's shift belongs to another cashier.", 403);
        return s;
    }

    /// <summary>Shift figures (X/Z report) straight from the documents.</summary>
    public static async Task<ShiftFigures> FiguresAsync(DbTx t, ShiftRow s)
    {
        var sale = await t.QuerySingleOrDefaultAsync<(long Count, decimal Gross, decimal Net, decimal Tax, decimal Discount, long CnCount, decimal CnGross, decimal Rounding)>("""
            SELECT count(*) FILTER (WHERE doc_type <> 'credit_note'),
                   coalesce(sum(gross_total) FILTER (WHERE doc_type <> 'credit_note'), 0),
                   coalesce(sum(net_total) FILTER (WHERE doc_type <> 'credit_note'), 0) - coalesce(sum(net_total) FILTER (WHERE doc_type = 'credit_note'), 0),
                   coalesce(sum(tax_total) FILTER (WHERE doc_type <> 'credit_note'), 0) - coalesce(sum(tax_total) FILTER (WHERE doc_type = 'credit_note'), 0),
                   coalesce(sum(discount_total) FILTER (WHERE doc_type <> 'credit_note'), 0),
                   count(*) FILTER (WHERE doc_type = 'credit_note'),
                   coalesce(sum(gross_total) FILTER (WHERE doc_type = 'credit_note'), 0),
                   coalesce(sum(cash_rounding) FILTER (WHERE doc_type <> 'credit_note'), 0) - coalesce(sum(cash_rounding) FILTER (WHERE doc_type = 'credit_note'), 0)
              FROM sales.sale WHERE shift_id = @id
            """, new { id = s.Id });
        var pay = (await t.QueryAsync<(string Method, decimal Total)>(
            "SELECT method, sum(amount) FROM sales.payment WHERE shift_id = @id GROUP BY method", new { id = s.Id })).ToDictionary(x => x.Method, x => x.Total);
        var ev = (await t.QueryAsync<(string Kind, decimal Total, long Count)>(
            "SELECT kind, sum(amount), count(*) FROM sales.cash_event WHERE shift_id = @id GROUP BY kind", new { id = s.Id })).ToDictionary(x => x.Kind);
        var cards = (await t.QueryAsync<CardTerminalTotal>(
            "SELECT coalesce(card_terminal, '') AS CardTerminal, sum(amount) AS Total FROM sales.payment WHERE shift_id = @id AND method = 'card' GROUP BY 1 ORDER BY 1",
            new { id = s.Id })).ToArray();
        decimal E(string k) => ev.TryGetValue(k, out var v) ? v.Total : 0m;
        var cash = pay.GetValueOrDefault("cash");
        return new ShiftFigures((int)sale.Count, sale.Gross, sale.Net, sale.Tax, sale.Discount, (int)sale.CnCount, sale.CnGross, sale.Rounding,
            cash, pay.GetValueOrDefault("card"), pay.GetValueOrDefault("bank_transfer"), E("pay_in"), E("pay_out"), E("drop"),
            ev.TryGetValue("no_sale", out var ns) ? (int)ns.Count : 0, s.OpeningFloat,
            PosMath.ExpectedCash(s.OpeningFloat, cash, E("pay_in"), E("pay_out"), E("drop")), cards);
    }

    // ------------------------------------------------------------------ approvals
    private static void MapApprovals(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/approvals");

        // The manager types their PIN on the cashier's terminal.
        g.MapPost("/pin", async (PinApprovalRequest req, HttpContext http, KiteDb db, ServerContext server, IPasswordHasher hasher,
            LockoutPolicy lockout, IClock clock, CancellationToken ct) =>
        {
            CheckAction(req.Action);
            var (branchId, approver) = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var term = await PosTerminals.CurrentAsync(t, http);
                var u = await t.QuerySingleOrDefaultAsync<(Guid Id, string? PinHash, string Status, int Failed, DateTime? LockedUntil, string Name)>("""
                    SELECT id, pin_hash, status, failed_login_count, locked_until, display_name_en FROM iam."user" WHERE business_id = @b AND username = @u
                    """, new { b = server.BusinessId, u = (req.ApproverUsername ?? "").Trim().ToLowerInvariant() });
                return (term.BranchId, u);
            }, ct);
            if (approver.Id == Guid.Empty || approver.PinHash is null || approver.Status != "active")
                throw new KiteException(ErrorCodes.ApprovalInvalid, "That manager cannot approve with a PIN.", 403);
            if (approver.LockedUntil is { } lu && DateTime.SpecifyKind(lu, DateTimeKind.Utc) > clock.UtcNow.UtcDateTime)
                throw new KiteException(ErrorCodes.AccountLocked, "That manager's account is locked after failed attempts.", 423);
            if (approver.Id == http.User.UserIdOf())
                throw new KiteException(ErrorCodes.ApprovalInvalid, "You cannot approve your own request.", 403);
            if (!hasher.Verify(req.Pin ?? "", approver.PinHash))
            {
                var count = approver.Failed + 1;
                var lockFor = lockout.LockFor(count);
                await db.InTransactionAsync(AccessScope.System, http.ActorOf(), async t =>
                {
                    await t.ExecuteAsync("""UPDATE iam."user" SET failed_login_count = @count, locked_until = coalesce(@until, locked_until) WHERE id = @id""",
                        new { count, until = lockFor is null ? (DateTimeOffset?)null : clock.UtcNow + lockFor.Value, id = approver.Id });
                    await t.AuditAsync(new AuditEntry("approval.pin_failed", "user", approver.Id, After: Json.Obj(new { req.Action, failedCount = count }), BranchId: branchId));
                }, ct);
                throw new KiteException(ErrorCodes.ApprovalInvalid, "The PIN is not correct.", 403, "pin");
            }
            var result = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var perms = await PosPermissions.InBranchAsync(t, approver.Id, branchId);
                if (!perms.Contains(req.Action))
                    throw new KiteException(ErrorCodes.ApprovalInvalid, $"{approver.Name} does not hold {req.Action} in this branch.", 403);
                await t.ExecuteAsync("""UPDATE iam."user" SET failed_login_count = 0 WHERE id = @id AND failed_login_count <> 0""", new { id = approver.Id });
                var id = t.NewId();
                var expires = clock.UtcNow.AddMinutes(5);
                await t.ExecuteAsync("""
                    INSERT INTO iam.approval (id, business_id, branch_id, terminal_id, action_code, method, status, requester_id, approver_id, context, reason, decided_at, expires_at)
                    VALUES (@id, @b, @branchId, @terminal, @Action, 'pin', 'approved', @requester, @approver, @context::jsonb, @Reason, now(), @expires)
                    """, new { id, b = server.BusinessId, branchId, terminal = http.User.TerminalIdOf(), req.Action, requester = http.User.UserIdOf(),
                               approver = approver.Id, context = req.Context?.GetRawText() ?? "{}", req.Reason, expires });
                await t.AuditAsync(new AuditEntry("approval.granted", "approval", id, After: Json.Obj(new { req.Action, method = "pin", approver = approver.Name, req.Reason }), BranchId: branchId));
                return new { id, status = "approved", approverName = approver.Name, expiresAt = expires };
            }, ct);
            return Results.Ok(result);
        }).RequireSignedIn();

        g.MapPost("", async (RemoteApprovalRequest req, HttpContext http, KiteDb db, ServerContext server, IClock clock, CancellationToken ct) =>
        {
            CheckAction(req.Action);
            var result = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var term = await PosTerminals.CurrentAsync(t, http);
                var id = t.NewId();
                var expires = clock.UtcNow.AddMinutes(10);
                await t.ExecuteAsync("""
                    INSERT INTO iam.approval (id, business_id, branch_id, terminal_id, action_code, method, status, requester_id, context, reason, expires_at)
                    VALUES (@id, @b, @branch, @terminal, @Action, 'remote', 'pending', @requester, @context::jsonb, @Reason, @expires)
                    """, new { id, b = server.BusinessId, branch = term.BranchId, terminal = term.Id, req.Action, requester = http.User.UserIdOf(),
                               context = req.Context?.GetRawText() ?? "{}", req.Reason, expires });
                await t.AuditAsync(new AuditEntry("approval.requested", "approval", id, After: Json.Obj(new { req.Action, req.Reason }), BranchId: term.BranchId));
                return new { id, status = "pending", expiresAt = expires };
            }, ct);
            return Results.Created($"/api/v1/approvals/{result.id}", result);
        }).RequireSignedIn();

        g.MapGet("", async (string? status, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.RowsAsync("""
                SELECT a.id, a.branch_id AS "branchId", b.code AS "branchCode", a.action_code AS "actionCode", a.method, a.status, a.reason,
                       a.context::text AS context, u.display_name_en AS "requesterName", t.code AS "terminalCode", a.created_at AS "createdAt", a.expires_at AS "expiresAt"
                  FROM iam.approval a JOIN core.branch b ON b.id = a.branch_id JOIN iam."user" u ON u.id = a.requester_id
                  LEFT JOIN core.terminal t ON t.id = a.terminal_id
                 WHERE (@status::text IS NULL OR a.status = @status) AND (a.status <> 'pending' OR a.expires_at > now())
                 ORDER BY a.created_at DESC LIMIT 100
                """, new { status }), ct)))
            .RequirePermission(Permissions.ApprovalsDecide);

        g.MapGet("/{id:guid}", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            var a = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.QuerySingleOrDefaultAsync<ApprovalRow>(Approvals.Select + " WHERE id = @id", new { id }), ct)
                ?? throw new KiteException(ErrorCodes.NotFound, "Approval not found.", 404);
            if (a.RequesterId != http.User.UserIdOf()) http.User.Demand(Permissions.ApprovalsDecide, a.BranchId);
            var status = a.Status == "pending" && DateTime.SpecifyKind(a.ExpiresAt, DateTimeKind.Utc) <= DateTime.UtcNow ? "expired" : a.Status;
            return Results.Ok(new { a.Id, a.ActionCode, status, a.Reason, a.ExpiresAt });
        }).RequireSignedIn();

        g.MapPost("/{id:guid}/{decision:regex(^(approve|reject)$)}", async (Guid id, string decision, DecideApprovalRequest? req, HttpContext http, KiteDb db, IClock clock, CancellationToken ct) =>
        {
            var result = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var a = await t.QuerySingleOrDefaultAsync<ApprovalRow>(Approvals.Select + " WHERE id = @id FOR UPDATE", new { id })
                    ?? throw new KiteException(ErrorCodes.NotFound, "Approval not found in your branches.", 404);
                http.User.Demand(Permissions.ApprovalsDecide, a.BranchId);
                var uid = http.User.UserIdOf();
                if (a.RequesterId == uid) throw new KiteException(ErrorCodes.ApprovalInvalid, "You cannot decide your own request.", 403);
                if (a.Status != "pending" || DateTime.SpecifyKind(a.ExpiresAt, DateTimeKind.Utc) <= clock.UtcNow.UtcDateTime)
                    throw new KiteException(ErrorCodes.ApprovalInvalid, "That request is no longer waiting.", 409);
                if (decision == "approve" && !(await PosPermissions.InBranchAsync(t, uid, a.BranchId)).Contains(a.ActionCode))
                    throw new KiteException(ErrorCodes.Forbidden, $"You do not hold {a.ActionCode} in that branch.", 403);
                var status = decision == "approve" ? "approved" : "rejected";
                await t.ExecuteAsync("""
                    UPDATE iam.approval SET status = @status, approver_id = @uid, decided_at = now(),
                           expires_at = CASE WHEN @status = 'approved' THEN now() + interval '5 minutes' ELSE expires_at END,
                           reason = coalesce(@reason, reason) WHERE id = @id
                    """, new { id, status, uid, reason = req?.Reason });
                await t.AuditAsync(new AuditEntry($"approval.{status}", "approval", id, After: Json.Obj(new { a.ActionCode, req?.Reason }), BranchId: a.BranchId));
                return new { id, status };
            }, ct);
            return Results.Ok(result);
        }).RequireSignedIn();
    }

    private static void CheckAction(string? action)
    {
        if (action is null || !ApprovableActions.Contains(action))
            throw new KiteException(ErrorCodes.Validation, "That action does not take a manager approval.", 400, "action");
    }

    // ------------------------------------------------------------------ sales
    private static void MapSales(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/sales");

        g.MapPost("/quote", async (CompleteSaleRequest req, HttpContext http, KiteDb db, ServerContext server, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var term = await PosTerminals.CurrentAsync(t, http);
                http.User.Demand(Permissions.PosSell, term.BranchId);
                var biz = await BusinessInfo.LoadAsync(t);
                var policy = await PosPolicies.ForBranchAsync(t, biz.Id, term.BranchId);
                var (lines, _) = await PriceAsync(t, http, biz, term, policy, req.Lines, BusinessDay.Today(biz.TimeZone, t.Clock));
                var totals = PosMath.Totals(lines);
                return new
                {
                    lines = lines.Select(l => new { l.LineNo, l.Item.VariantId, l.Item.Sku, l.Item.DescriptionEn, l.Item.DescriptionAr, l.Qty, l.ListPrice, l.UnitPrice,
                                                    l.Discount, l.Net, l.Tax, l.Gross, l.Item.TaxRate, l.NeedsPriceOverride, l.NeedsDiscountApproval }),
                    totals, cashDue = PosMath.RoundCash(totals.Gross, policy.CashRounding),
                };
            }, ct))).RequireSignedIn();

        g.MapPost("/{id:guid}/complete", async (Guid id, CompleteSaleRequest req, HttpContext http, KiteDb db, ServerContext server, LicenceService licence, CancellationToken ct) =>
        {
            await licence.RequireTransactAsync(ct);
            var r = await WithRetryAsync(() => db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => CompleteAsync(t, http, id, req), ct));
            return Results.Ok(r);
        }).RequireSignedIn();

        g.MapPost("/{originalId:guid}/return", async (Guid originalId, ReturnRequest req, HttpContext http, KiteDb db, LicenceService licence, CancellationToken ct) =>
        {
            await licence.RequireTransactAsync(ct);
            var r = await WithRetryAsync(() => db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => ReturnAsync(t, http, originalId, req), ct));
            return Results.Ok(r);
        }).RequireSignedIn();

        g.MapPost("/{originalId:guid}/void", async (Guid originalId, VoidRequest req, HttpContext http, KiteDb db, LicenceService licence, CancellationToken ct) =>
        {
            await licence.RequireTransactAsync(ct);
            var r = await WithRetryAsync(() => db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => VoidAsync(t, http, originalId, req), ct));
            return Results.Ok(r);
        }).RequireSignedIn();

        g.MapGet("", async (Guid? shiftId, DateOnly? date, string? q, Guid? branchId, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                if (shiftId is { } sid)
                {
                    var owner = await t.QuerySingleOrDefaultAsync<(Guid Cashier, Guid Branch)>("SELECT cashier_id, branch_id FROM sales.shift WHERE id = @sid", new { sid });
                    if (owner.Cashier != http.User.UserIdOf()) http.User.Demand(Permissions.PosReportsView, owner.Branch);
                }
                else http.User.Demand(Permissions.PosReportsView, branchId);
                return await t.RowsAsync("""
                    SELECT s.id, s.number, s.doc_type AS "docType", s.credit_reason AS "creditReason", s.completed_at AS "completedAt", s.gross_total AS "grossTotal",
                           s.cash_rounding AS "cashRounding", s.is_emergency AS "isEmergency", c.name_en AS "customerName", u.display_name_en AS "cashierName",
                           t.code AS "terminalCode", o.number AS "originalNumber"
                      FROM sales.sale s JOIN iam."user" u ON u.id = s.cashier_id JOIN core.terminal t ON t.id = s.terminal_id
                      LEFT JOIN crm.customer c ON c.id = s.customer_id LEFT JOIN sales.sale o ON o.id = s.original_sale_id
                     WHERE (@shiftId::uuid IS NULL OR s.shift_id = @shiftId) AND (@branchId::uuid IS NULL OR s.branch_id = @branchId)
                       AND (@d::date IS NULL OR s.sale_date = @d::date) AND (@q::text IS NULL OR s.number ILIKE '%' || @q || '%')
                     ORDER BY s.completed_at DESC LIMIT 200
                    """, new { shiftId, branchId, d = date?.ToDateTime(TimeOnly.MinValue), q = string.IsNullOrWhiteSpace(q) ? null : q.Trim() });
            }, ct))).RequireSignedIn();

        g.MapGet("/{id:guid}", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => LoadSaleAsync(t, http, id), ct))).RequireSignedIn();

        g.MapGet("/by-number/{number}", async (string number, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var id = await t.ExecuteScalarAsync<Guid?>("SELECT id FROM sales.sale WHERE upper(number) = upper(@number)", new { number = number.Trim() })
                    ?? throw new KiteException(ErrorCodes.NotFound, $"No sale {number} in your branches.", 404);
                return await LoadSaleAsync(t, http, id);
            }, ct))).RequireSignedIn();

        // Parked carts (10.3): recalled at any terminal of the branch.
        g.MapPost("/parked", async (ParkRequest req, HttpContext http, KiteDb db, ServerContext server, CancellationToken ct) =>
        {
            await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var term = await PosTerminals.CurrentAsync(t, http);
                http.User.Demand(Permissions.PosSell, term.BranchId);
                if (await t.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM sales.sale WHERE id = @Id)", new { req.Id }))
                    throw new KiteException(ErrorCodes.Conflict, "That cart was already completed.", 409);
                await t.ExecuteAsync("""
                    INSERT INTO sales.parked_cart (id, business_id, branch_id, terminal_id, parked_by, label, cart)
                    VALUES (@Id, @b, @branch, @terminal, @uid, @Label, @cart::jsonb)
                    ON CONFLICT (id) DO UPDATE SET label = EXCLUDED.label, cart = EXCLUDED.cart, terminal_id = EXCLUDED.terminal_id,
                                                   parked_by = EXCLUDED.parked_by, parked_at = now()
                    """, new { req.Id, b = server.BusinessId, branch = term.BranchId, terminal = term.Id, uid = http.User.UserIdOf(), req.Label, cart = req.Cart.GetRawText() });
                // Devices in a parked sale stay held for it (11.3), for the parked lease.
                if (req.DeviceUnitIds is { Count: > 0 } ids)
                {
                    var settings = await DeviceUnits.SettingsAsync(t, server.BusinessId!.Value);
                    foreach (var unitId in ids.Distinct().Order())
                        await DeviceEndpoints.ReserveAsync(t, server.BusinessId!.Value, await DeviceUnits.LoadAsync(t, unitId), term, req.Id, http.User.UserIdOf(), settings.ParkedReservationMinutes);
                }
            }, ct);
            return Results.NoContent();
        }).RequireSignedIn();

        g.MapGet("/parked", async (HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var term = await PosTerminals.CurrentAsync(t, http);
                return await t.RowsAsync("""
                    SELECT p.id, p.label, p.parked_at AS "parkedAt", u.display_name_en AS "parkedBy", t.code AS "terminalCode", p.cart::text AS cart
                      FROM sales.parked_cart p JOIN iam."user" u ON u.id = p.parked_by JOIN core.terminal t ON t.id = p.terminal_id
                     WHERE p.branch_id = @branch ORDER BY p.parked_at
                    """, new { branch = term.BranchId });
            }, ct))).RequireSignedIn();

        g.MapPost("/parked/{id:guid}/recall", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            var cart = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var term = await PosTerminals.CurrentAsync(t, http);
                http.User.Demand(Permissions.PosSell, term.BranchId);
                var c = await t.ExecuteScalarAsync<string?>("DELETE FROM sales.parked_cart WHERE id = @id AND branch_id = @branch RETURNING cart::text", new { id, branch = term.BranchId })
                    ?? throw new KiteException(ErrorCodes.NotFound, "That parked cart was already recalled.", 404);
                return c;
            }, ct);
            return Results.Text(cart, "application/json");
        }).RequireSignedIn();
    }

    private static async Task<T> WithRetryAsync<T>(Func<Task<T>> work)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return await work(); }
            catch (PostgresException e) when (e.SqlState is "40P01" or "40001" && attempt < 4)
            {
                await Task.Delay(Random.Shared.Next(10, 60) * attempt);
            }
        }
    }

    /// <summary>Prices a cart; a device line is priced as its own unit (12.1) and the units are returned with it.</summary>
    private static async Task<(IReadOnlyList<PricedLine> Lines, Dictionary<Guid, DeviceUnitRow> Units)> PriceAsync(DbTx t, HttpContext http, BusinessInfo biz,
        PosTerminal term, PosPolicy policy, IReadOnlyList<CartLineInput>? input, DateOnly date)
    {
        if (input is null || input.Count == 0) throw new KiteException(ErrorCodes.Validation, "The cart is empty.", 400, "lines");
        if (input.Count > 500) throw new KiteException(ErrorCodes.Validation, "A sale can have at most 500 lines.", 400, "lines");
        var items = await ItemPricing.LoadAsync(t, term.BranchId, input.Select(l => l.VariantId), date);
        var limit = await PosPermissions.DiscountLimitAsync(t, http, term.BranchId);
        var units = new Dictionary<Guid, DeviceUnitRow>();
        var lines = new List<PricedLine>();
        for (var i = 0; i < input.Count; i++)
        {
            var l = input[i];
            if (!items.TryGetValue(l.VariantId, out var item))
                throw new KiteException(ErrorCodes.BarcodeUnknown, "An item in the cart is not in the catalog.", 400, $"lines[{i}].variantId");
            if (l.DeviceUnitId is { } unitId)
            {
                if (units.ContainsKey(unitId)) throw new KiteException(ErrorCodes.Validation, "The same device is in the cart twice.", 400, $"lines[{i}].deviceUnitId");
                var u = await DeviceUnits.LoadAsync(t, unitId);
                if (u.VariantId != l.VariantId) throw new KiteException(ErrorCodes.DeviceMismatch, $"{u.Identifier} is a {u.Sku}, not the item on this line.", 400, $"lines[{i}].deviceUnitId");
                await DeviceEndpoints.CheckSellableAsync(t, u, term);
                units[unitId] = u;
                item = await DeviceEndpoints.PricedItemAsync(t, biz, u, term.BranchId, date);
            }
            lines.Add(PosMath.PriceLine(i + 1, new CartLine(l.VariantId, l.Qty, l.UnitPrice, l.Discount ?? 0m, l.ApprovalId, l.DeviceUnitId), item, biz.PricesIncludeTax, limit));
        }
        return (policy.RoundingLevel == "invoice" ? PosMath.ApplyInvoiceRounding(lines, biz.PricesIncludeTax) : lines, units);
    }

    private static async Task<SaleSummary> CompleteAsync(DbTx t, HttpContext http, Guid id, CompleteSaleRequest req)
    {
        await t.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))", new { k = id.ToString() });
        var term = await PosTerminals.CurrentAsync(t, http);
        var existing = await ExistingAsync(t, id);
        if (existing is not null)
        {
            if (existing.Value.TerminalId != term.Id) throw new KiteException(ErrorCodes.Conflict, "That sale ID belongs to another terminal.", 409);
            return existing.Value.Summary;   // the same request again (e.g. a retry after a lost answer)
        }
        http.User.Demand(Permissions.PosSell, term.BranchId);
        var uid = http.User.UserIdOf();
        var shift = await t.QuerySingleOrDefaultAsync<ShiftRow>(ShiftSelect + " WHERE terminal_id = @id AND status = 'open' AND NOT is_emergency FOR SHARE", new { id = term.Id })
            ?? throw new KiteException(ErrorCodes.NoOpenShift, "Open a shift on this terminal first.", 409);
        if (shift.CashierId != uid) throw new KiteException(ErrorCodes.ShiftNotYours, "This terminal's shift belongs to another cashier.", 403);

        var biz = await BusinessInfo.LoadAsync(t);
        var policy = await PosPolicies.ForBranchAsync(t, biz.Id, term.BranchId);
        var now = t.Clock.UtcNow;
        var (lines, units) = await PriceAsync(t, http, biz, term, policy, req.Lines, BusinessDay.DateOf(now, biz.TimeZone));

        // Devices (11.5): not in another cart's lease; a damaged one only with DEVICE.SELL_DAMAGED or a manager's approval (12.1).
        foreach (var (u, i) in req.Lines.Select((l, i) => (l, i)).Where(x => x.l.DeviceUnitId is not null).Select(x => (units[x.l.DeviceUnitId!.Value], x.i)))
        {
            var holder = await t.ExecuteScalarAsync<Guid?>("SELECT cart_id FROM device.reservation WHERE device_unit_id = @id AND expires_at > now()", new { id = u.Id });
            if (holder is { } cart && cart != id)
                throw new KiteException(ErrorCodes.DeviceReserved, $"{u.Identifier} is in another cart. Remove it, or wait until that cart lets it go.", 409, $"lines[{i}].deviceUnitId");
            if (u.Condition == "damaged" && !http.User.HasIn(Permissions.DeviceSellDamaged, term.BranchId))
            {
                var approval = req.Lines[i].DamagedApprovalId ?? throw new KiteException(ErrorCodes.DeviceDamaged,
                    $"{u.Identifier} is damaged: selling it needs a manager's approval and telling the customer.", 403, $"lines[{i}].damagedApprovalId");
                await Approvals.UseAsync(t, approval, http.User.UserIdOf(), term.BranchId, Permissions.DeviceSellDamaged, "sale", id);
            }
        }

        // Approvals: a price change or a price under the minimum needs POS.PRICE.OVERRIDE; a discount above the cashier's
        // limit needs POS.DISCOUNT.ABOVE_LIMIT. Held by the cashier, or given by a manager for this sale.
        var uses = new Dictionary<Guid, string>();
        var draft = new List<DraftLine>();
        foreach (var l in lines)
        {
            string? action = l.NeedsPriceOverride && !http.User.HasIn(Permissions.PosPriceOverride, term.BranchId) ? Permissions.PosPriceOverride
                : l.NeedsDiscountApproval ? Permissions.PosDiscountAboveLimit : null;
            Guid? approval = null;
            if (action is not null)
            {
                approval = l.ApprovalId ?? req.ApprovalId ?? throw new KiteException(
                    action == Permissions.PosPriceOverride ? ErrorCodes.BelowMinimumPrice : ErrorCodes.DiscountAboveLimit,
                    action == Permissions.PosPriceOverride ? $"{l.Item.DescriptionEn}: the price needs a manager's approval." : $"{l.Item.DescriptionEn}: the discount is above your limit and needs a manager's approval.",
                    403, $"lines[{l.LineNo - 1}]");
                if (uses.TryGetValue(approval.Value, out var other) && other != action)
                    throw new KiteException(ErrorCodes.ApprovalInvalid, "One approval cannot cover both a price change and a discount.", 400, $"lines[{l.LineNo - 1}].approvalId");
                uses[approval.Value] = action;
            }
            var unitId = req.Lines[l.LineNo - 1].DeviceUnitId;
            draft.Add(DraftLine.From(l, approval, unitId, unitId is { } uid2 ? units[uid2].Condition : null));
        }
        foreach (var (aid, action) in uses) await Approvals.UseAsync(t, aid, uid, term.BranchId, action, "sale", id);

        var docType = req.DocType ?? "simplified_invoice";
        if (docType is not ("simplified_invoice" or "tax_invoice")) throw new KiteException(ErrorCodes.Validation, "Document type must be simplified_invoice or tax_invoice.", 400, "docType");
        if (docType == "tax_invoice" && req.CustomerId is null) throw new KiteException(ErrorCodes.CustomerRequired, "A tax invoice needs a customer.", 400, "customerId");
        if (req.CustomerId is { } cid && !await t.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM crm.customer WHERE id = @cid AND status = 'active')", new { cid }))
            throw new KiteException(ErrorCodes.Validation, "That customer does not exist or is inactive.", 400, "customerId");

        var totals = PosMath.Totals(lines);
        if (req.ExpectedTotal is { } shown && shown != totals.Gross)
            throw new KiteException(ErrorCodes.SaleChanged, $"Prices changed: the total is now {totals.Gross:0.000}, not {shown:0.000}. Check the cart and try again.", 409, "expectedTotal");
        var payments = PosMath.CheckTenders(req.Payments ?? [], totals.Gross, policy.CashRounding);

        var written = await SaleWriter.WriteAsync(t, biz, term, policy, new SaleDraft(id, shift.Id, uid, req.CustomerId, docType, null, null, null, now,
            draft, payments, false, Note: string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim(),
            Audit: uses.Count == 0 ? null : number => new AuditEntry("pos.sale_with_approval", "sale", id, After: Json.Obj(new { number, approvals = uses.Keys }), BranchId: term.BranchId)));
        return new SaleSummary(id, written.Number, docType, written.Gross, written.CashRounding, payments.Paid,
            payments.Tenders.Sum(x => x.Change ?? 0m), written.StockExceptions);
    }

    private static async Task<(Guid TerminalId, SaleSummary Summary)?> ExistingAsync(DbTx t, Guid id)
    {
        var s = await t.QuerySingleOrDefaultAsync<(Guid TerminalId, string Number, string DocType, decimal Gross, decimal Rounding)>(
            "SELECT terminal_id, number, doc_type, gross_total, cash_rounding FROM sales.sale WHERE id = @id", new { id });
        if (s.TerminalId == Guid.Empty) return null;
        var change = await t.ExecuteScalarAsync<decimal>("SELECT coalesce(sum(change_given), 0) FROM sales.payment WHERE sale_id = @id", new { id });
        var paid = await t.ExecuteScalarAsync<decimal>("SELECT coalesce(sum(abs(amount)), 0) FROM sales.payment WHERE sale_id = @id", new { id });
        return (s.TerminalId, new SaleSummary(id, s.Number, s.DocType, s.Gross, s.Rounding, paid, change, 0));
    }

    private sealed record OriginalSale(Guid Id, Guid BranchId, Guid ShiftId, string Number, string DocType, Guid? CustomerId, DateTime SaleDate, decimal CashRounding);
    private sealed record OriginalLine(Guid Id, Guid VariantId, string ProductType, string DescriptionEn, string DescriptionAr, decimal Qty, decimal UnitPrice,
        decimal ListPrice, decimal Discount, decimal Net, Guid TaxCodeId, decimal TaxRate, decimal Tax, decimal Gross, decimal CostValue,
        decimal ReturnedQty, decimal ReturnedNet, decimal ReturnedTax, decimal ReturnedGross, decimal ReturnedDiscount, Guid? DeviceUnitId);

    private const string OriginalLinesSql = """
        SELECT l.id, l.variant_id AS VariantId, l.product_type AS ProductType, l.description_en AS DescriptionEn, l.description_ar AS DescriptionAr,
               l.qty, l.unit_price AS UnitPrice, l.list_price AS ListPrice, l.discount_amount AS Discount, l.net_amount AS Net, l.tax_code_id AS TaxCodeId,
               l.tax_rate AS TaxRate, l.tax_amount AS Tax, l.gross_amount AS Gross, l.cost_value AS CostValue,
               coalesce(r.qty, 0) AS ReturnedQty, coalesce(r.net, 0) AS ReturnedNet, coalesce(r.tax, 0) AS ReturnedTax, coalesce(r.gross, 0) AS ReturnedGross,
               coalesce(r.discount, 0) AS ReturnedDiscount, l.device_unit_id AS DeviceUnitId
          FROM sales.sale_line l
          LEFT JOIN LATERAL (SELECT sum(x.qty) AS qty, sum(x.net_amount) AS net, sum(x.tax_amount) AS tax, sum(x.gross_amount) AS gross,
                                    sum(x.discount_amount) AS discount
                               FROM sales.sale_line x WHERE x.returned_line_id = l.id) r ON true
         WHERE l.sale_id = @id ORDER BY l.line_no
        """;

    private static async Task<(PosTerminal Term, ShiftRow Shift, OriginalSale Original, OriginalLine[] Lines, BusinessInfo Biz, PosPolicy Policy)> PrepareCreditAsync(
        DbTx t, HttpContext http, Guid originalId, Guid creditId)
    {
        await t.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))", new { k = originalId.ToString() });
        var term = await PosTerminals.CurrentAsync(t, http);
        http.User.Demand(Permissions.PosSell, term.BranchId);
        var shift = await t.QuerySingleOrDefaultAsync<ShiftRow>(ShiftSelect + " WHERE terminal_id = @id AND status = 'open' AND NOT is_emergency FOR SHARE", new { id = term.Id })
            ?? throw new KiteException(ErrorCodes.NoOpenShift, "Open a shift on this terminal first.", 409);
        if (shift.CashierId != http.User.UserIdOf()) throw new KiteException(ErrorCodes.ShiftNotYours, "This terminal's shift belongs to another cashier.", 403);
        var original = await t.QuerySingleOrDefaultAsync<OriginalSale>("""
            SELECT id, branch_id AS BranchId, shift_id AS ShiftId, number, doc_type AS DocType, customer_id AS CustomerId, sale_date AS SaleDate, cash_rounding AS CashRounding
              FROM sales.sale WHERE id = @originalId
            """, new { originalId }) ?? throw new KiteException(ErrorCodes.NotFound, "That sale was not found in your branches.", 404);
        if (original.DocType == "credit_note") throw new KiteException(ErrorCodes.Validation, "A credit note cannot be returned.", 400);
        if (original.BranchId != term.BranchId) throw new KiteException(ErrorCodes.Validation, "Return the items at the branch that sold them.", 400);
        var lines = (await t.QueryAsync<OriginalLine>(OriginalLinesSql, new { id = originalId })).ToArray();
        var biz = await BusinessInfo.LoadAsync(t);
        var policy = await PosPolicies.ForBranchAsync(t, biz.Id, term.BranchId);
        return (term, shift, original, lines, biz, policy);
    }

    private static async Task<SaleSummary> ReturnAsync(DbTx t, HttpContext http, Guid originalId, ReturnRequest req)
    {
        var existing = await ExistingAsync(t, req.Id);
        if (existing is not null) return existing.Value.Summary;
        var (term, shift, original, lines, biz, policy) = await PrepareCreditAsync(t, http, originalId, req.Id);
        if (await ExistingAsync(t, req.Id) is { } again) return again.Summary;
        if (req.Lines is null || req.Lines.Count == 0) throw new KiteException(ErrorCodes.Validation, "Choose the items to return.", 400, "lines");
        if (string.IsNullOrWhiteSpace(req.Reason)) throw new KiteException(ErrorCodes.Validation, "Give the reason for the return.", 400, "reason");
        var uid = http.User.UserIdOf();
        var now = t.Clock.UtcNow;
        var today = BusinessDay.DateOf(now, biz.TimeZone);

        var draft = new List<DraftLine>();
        foreach (var (r, i) in req.Lines.Select((r, i) => (r, i)))
        {
            var o = lines.FirstOrDefault(x => x.Id == r.LineId) ?? throw new KiteException(ErrorCodes.Validation, "That line is not on the sale.", 400, $"lines[{i}].lineId");
            if (draft.Any(d => d.ReturnedLineId == o.Id)) throw new KiteException(ErrorCodes.Validation, "A line appears twice.", 400, $"lines[{i}].lineId");
            var a = PosMath.ApportionReturn(new ReturnableLine(o.Qty, o.Net, o.Tax, o.Gross, o.Discount, o.ReturnedQty, o.ReturnedNet, o.ReturnedTax, o.ReturnedGross, o.ReturnedDiscount), r.Qty);
            if (o.DeviceUnitId is { } unitId)
            {
                // The device that comes back must be the one sold on that line (11.5); it goes to inspection, never straight back on sale.
                var unit = await DeviceUnits.LoadAsync(t, unitId);
                var code = string.IsNullOrWhiteSpace(r.Code) ? throw new KiteException(ErrorCodes.DeviceMismatch, $"Scan the returned device's IMEI or serial number ({o.DescriptionEn}).", 400, $"lines[{i}].code")
                    : DeviceMath.Classify(r.Code).Value;
                if (code != unit.Imei1 && code != unit.Imei2 && code != unit.Serial)
                    throw new KiteException(ErrorCodes.DeviceMismatch, $"{code} is not the device sold on this line.", 400, $"lines[{i}].code");
                draft.Add(new DraftLine(o.VariantId, o.ProductType, o.DescriptionEn, o.DescriptionAr, r.Qty, o.UnitPrice, o.ListPrice, a.Discount, a.Net, o.TaxCodeId,
                    o.TaxRate, a.Tax, a.Gross, null, 0, o.Id, false, null, unitId, unit.Condition));
                continue;
            }
            var stocked = o.ProductType != "service";
            draft.Add(new DraftLine(o.VariantId, o.ProductType, o.DescriptionEn, o.DescriptionAr, r.Qty, o.UnitPrice, o.ListPrice, a.Discount, a.Net, o.TaxCodeId,
                o.TaxRate, a.Tax, a.Gross, null, 0, o.Id, stocked ? r.Restock : null, stocked && r.Restock ? decimal.Round(o.CostValue / o.Qty, 6) : null));
        }
        var gross = draft.Sum(d => d.Gross);
        var refunds = PosMath.CheckTenders(req.Refunds ?? [], gross, policy.CashRounding, refund: true);
        foreach (var (p, i) in refunds.Tenders.Select((p, i) => (p, i)))
            if (p.Method == "card" && p.CardTerminal is null)
                throw new KiteException(ErrorCodes.Validation, "Name the bank terminal the card refund is made on.", 400, $"refunds[{i}].cardTerminal");
        var cardRefund = refunds.Tenders.Where(p => p.Method == "card").Sum(p => p.Amount);
        if (cardRefund > 0)
        {
            var cardLeft = await t.ExecuteScalarAsync<decimal>("""
                SELECT coalesce(sum(p.amount), 0) FROM sales.payment p JOIN sales.sale s ON s.id = p.sale_id
                 WHERE p.method = 'card' AND (s.id = @originalId OR s.original_sale_id = @originalId)
                """, new { originalId });
            if (cardRefund > cardLeft) throw new KiteException(ErrorCodes.RefundMethod, $"At most {cardLeft:0.000} can go back to a card.", 400, "refunds");
        }

        // Late returns and large cash refunds need POS.REFUND (10.3).
        var reasons = new List<string>();
        if (DateOnly.FromDateTime(original.SaleDate).AddDays(policy.ReturnWindowDays) < today) reasons.Add($"older than {policy.ReturnWindowDays} days");
        var cashRefund = refunds.Tenders.Where(p => p.Method == "cash").Sum(p => p.Amount);
        if (cashRefund > policy.CashRefundCap) reasons.Add($"cash refund above {policy.CashRefundCap:0.000}");
        if (reasons.Count > 0 && !http.User.HasIn(Permissions.PosRefund, term.BranchId))
        {
            if (req.ApprovalId is null)
                throw new KiteException(cashRefund > policy.CashRefundCap ? ErrorCodes.CashRefundCap : ErrorCodes.ReturnWindow,
                    $"This return needs a manager's approval ({string.Join(", ", reasons)}).", 403, "approvalId");
            await Approvals.UseAsync(t, req.ApprovalId.Value, uid, term.BranchId, Permissions.PosRefund, "sale", req.Id);
        }
        if (cashRefund > 0)
        {
            var f = await FiguresAsync(t, shift);
            if (cashRefund > f.ExpectedCash) throw new KiteException(ErrorCodes.RefundMethod, "The drawer does not hold enough cash for this refund.", 400, "refunds");
        }

        var written = await SaleWriter.WriteAsync(t, biz, term, policy, new SaleDraft(req.Id, shift.Id, uid, original.CustomerId, "credit_note",
            req.ExchangeGroupId is null ? "return" : "exchange", original.Id, req.ExchangeGroupId, now, draft, refunds, true, Note: req.Reason.Trim(),
            Audit: number => new AuditEntry("pos.sale_returned", "sale", req.Id,
                After: Json.Obj(new { number, original = original.Number, gross, req.Reason, approval = req.ApprovalId }), BranchId: term.BranchId)));
        return new SaleSummary(req.Id, written.Number, "credit_note", written.Gross, written.CashRounding, refunds.Paid, 0m, 0);
    }

    private static async Task<SaleSummary> VoidAsync(DbTx t, HttpContext http, Guid originalId, VoidRequest req)
    {
        var existing = await ExistingAsync(t, req.Id);
        if (existing is not null) return existing.Value.Summary;
        var (term, shift, original, lines, biz, policy) = await PrepareCreditAsync(t, http, originalId, req.Id);
        if (await ExistingAsync(t, req.Id) is { } again) return again.Summary;
        if (string.IsNullOrWhiteSpace(req.Reason)) throw new KiteException(ErrorCodes.Validation, "Give the reason for the void.", 400, "reason");
        if (original.ShiftId != shift.Id) throw new KiteException(ErrorCodes.VoidNotAllowed, "Only sales of the open shift can be voided; use a return instead.", 409);
        if (lines.Any(l => l.ReturnedQty > 0)) throw new KiteException(ErrorCodes.VoidNotAllowed, "Part of this sale was already returned; use a return instead.", 409);
        var uid = http.User.UserIdOf();
        if (!http.User.HasIn(Permissions.PosSaleVoid, term.BranchId))
        {
            if (req.ApprovalId is null) throw new KiteException(ErrorCodes.Forbidden, "Voiding a sale needs a manager's approval.", 403, "approvalId");
            await Approvals.UseAsync(t, req.ApprovalId.Value, uid, term.BranchId, Permissions.PosSaleVoid, "sale", req.Id);
        }
        var conditions = (await t.QueryAsync<(Guid Id, string Condition)>("SELECT id, condition FROM device.device_unit WHERE id = ANY(@ids)",
            new { ids = lines.Where(l => l.DeviceUnitId is not null).Select(l => l.DeviceUnitId!.Value).ToArray() })).ToDictionary(x => x.Id, x => x.Condition);
        var draft = lines.Select(o => new DraftLine(o.VariantId, o.ProductType, o.DescriptionEn, o.DescriptionAr, o.Qty, o.UnitPrice, o.ListPrice, o.Discount,
            o.Net, o.TaxCodeId, o.TaxRate, o.Tax, o.Gross, null, 0, o.Id, o.ProductType != "service" ? true : null,
            o.ProductType != "service" ? decimal.Round(o.CostValue / o.Qty, 6) : null, o.DeviceUnitId,
            o.DeviceUnitId is { } du ? conditions[du] : null)).ToArray();
        // The money goes back exactly as it came in, cash rounding included.
        var paid = await t.QueryAsync<(string Method, decimal Amount, string? CardTerminal, string? ApprovalCode, string? Reference)>(
            "SELECT method, amount, card_terminal, approval_code, reference FROM sales.payment WHERE sale_id = @originalId ORDER BY created_at, id", new { originalId });
        var tenders = paid.Select(p => new CheckedTender(p.Method, p.Amount, null, null, p.CardTerminal, p.ApprovalCode, p.Reference)).ToArray();
        var refunds = new CheckedPayments(tenders, original.CashRounding, draft.Sum(d => d.Gross) + original.CashRounding);
        var written = await SaleWriter.WriteAsync(t, biz, term, policy, new SaleDraft(req.Id, shift.Id, uid, original.CustomerId, "credit_note", "void",
            original.Id, null, t.Clock.UtcNow, draft, refunds, true, Note: req.Reason.Trim(),
            Audit: number => new AuditEntry("pos.sale_voided", "sale", req.Id, After: Json.Obj(new { number, original = original.Number, req.Reason, approval = req.ApprovalId }), BranchId: term.BranchId)));
        return new SaleSummary(req.Id, written.Number, "credit_note", written.Gross, written.CashRounding, refunds.Paid, 0m, 0);
    }

    private static async Task<object> LoadSaleAsync(DbTx t, HttpContext http, Guid id)
    {
        var s = await t.QuerySingleOrDefaultAsync<SaleHeader>(SaleHeader.Select + " WHERE s.id = @id", new { id })
            ?? throw new KiteException(ErrorCodes.NotFound, "That sale was not found in your branches.", 404);
        if (!http.User.HasIn(Permissions.PosSell, s.BranchId)) http.User.Demand(Permissions.PosReportsView, s.BranchId);
        var lines = (await t.QueryAsync<OriginalLine>(OriginalLinesSql, new { id })).ToArray();
        var identifiers = (await t.QueryAsync<(Guid Unit, string Value)>("""
            SELECT DISTINCT ON (device_unit_id) device_unit_id, value FROM device.imei_registry WHERE device_unit_id = ANY(@ids) ORDER BY device_unit_id, (status = 'active') DESC, kind, slot
            """, new { ids = lines.Where(l => l.DeviceUnitId is not null).Select(l => l.DeviceUnitId!.Value).ToArray() })).ToDictionary(x => x.Unit, x => x.Value);
        var payments = await t.RowsAsync("""
            SELECT method, amount, tendered, change_given AS "changeGiven", card_terminal AS "cardTerminal", approval_code AS "approvalCode", reference
              FROM sales.payment WHERE sale_id = @id ORDER BY created_at, id
            """, new { id });
        var credits = await t.RowsAsync("SELECT id, number, credit_reason AS \"creditReason\", gross_total AS \"grossTotal\" FROM sales.sale WHERE original_sale_id = @id ORDER BY completed_at", new { id });
        return new
        {
            sale = s,
            lines = lines.Select(l => new { l.Id, l.VariantId, l.ProductType, l.DescriptionEn, l.DescriptionAr, l.Qty, l.UnitPrice, l.ListPrice, l.Discount, l.Net,
                                            l.TaxRate, l.Tax, l.Gross, l.ReturnedQty, l.ReturnedNet, l.ReturnedTax, l.ReturnedGross, l.ReturnedDiscount,
                                            returnable = l.Qty - l.ReturnedQty, l.DeviceUnitId,
                                            identifier = l.DeviceUnitId is { } du ? identifiers.GetValueOrDefault(du) : null }),
            payments, creditNotes = credits,
        };
    }
}

public sealed record SaleHeader(Guid Id, Guid BranchId, string Number, string DocType, string? CreditReason, Guid? OriginalSaleId, string? OriginalNumber,
    DateTime SaleDate, DateTime CompletedAt, decimal NetTotal, decimal DiscountTotal, decimal TaxTotal, decimal GrossTotal, decimal CashRounding,
    bool IsEmergency, string? Note, Guid? CustomerId, string? CustomerName, string? CustomerVat, string CashierName, string TerminalCode, Guid ShiftId,
    string QrPayload, Guid EinvoiceUuid)
{
    public const string Select = """
        SELECT s.id, s.branch_id AS BranchId, s.number, s.doc_type AS DocType, s.credit_reason AS CreditReason, s.original_sale_id AS OriginalSaleId,
               o.number AS OriginalNumber, s.sale_date AS SaleDate, s.completed_at AS CompletedAt, s.net_total AS NetTotal, s.discount_total AS DiscountTotal,
               s.tax_total AS TaxTotal, s.gross_total AS GrossTotal, s.cash_rounding AS CashRounding, s.is_emergency AS IsEmergency, s.note,
               s.customer_id AS CustomerId, c.name_en AS CustomerName, c.vat_number AS CustomerVat, u.display_name_en AS CashierName, t.code AS TerminalCode,
               s.shift_id AS ShiftId, s.qr_payload AS QrPayload, s.einvoice_uuid AS EinvoiceUuid
          FROM sales.sale s JOIN iam."user" u ON u.id = s.cashier_id JOIN core.terminal t ON t.id = s.terminal_id
          LEFT JOIN crm.customer c ON c.id = s.customer_id LEFT JOIN sales.sale o ON o.id = s.original_sale_id
        """;
}
