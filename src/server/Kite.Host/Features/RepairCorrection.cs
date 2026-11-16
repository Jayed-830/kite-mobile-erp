using Kite.Accounting;
using Kite.Host.Infrastructure;
using Kite.Persistence;
using Kite.Platform;
using Kite.Sales;

namespace Kite.Host.Features;

public sealed record RepairCorrectionLine(string Kind, Guid VariantId, decimal Qty, decimal? UnitPrice, int? Minutes = null, string? DescriptionEn = null,
    string? DescriptionAr = null);
public sealed record RepairCorrectionRequest(Guid Id, string Reason, List<TenderInput>? Refunds, List<RepairCorrectionLine>? Lines = null,
    List<TenderInput>? Payments = null, decimal? ExpectedCredit = null, Guid? CorrectedId = null);

public static partial class PosEndpoints
{
    /// <summary>
    /// Correcting a delivered repair invoice at the till (owner decision, Phase 9): a manager credits it in full, the money goes back
    /// from the drawer or to store credit, and a corrected invoice may be issued and paid there. The job keeps its record; its
    /// commissions are reversed and generated again from the corrected invoice (15.3).
    /// </summary>
    public static void MapRepairCorrection(WebApplication app)
    {
        app.MapPost("/api/v1/repairs/{id:guid}/correct", async (Guid id, RepairCorrectionRequest req, HttpContext http, KiteDb db, LicenceService licence,
            CancellationToken ct) =>
        {
            await licence.RequireTransactAsync(ct);
            return Results.Ok(await WithRetryAsync(() => db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => CorrectAsync(t, http, id, req), ct)));
        }).RequirePermission(Permissions.RepairInvoiceCorrect).RequireModule("repair");
    }

    private static async Task<object> CorrectAsync(DbTx t, HttpContext http, Guid jobId, RepairCorrectionRequest req)
    {
        // Lock order (ADR 54): the job first, then the till's shift and everything after it.
        var j = await RepairCore.LoadAsync(t, jobId, forUpdate: true);
        http.User.Demand(Permissions.RepairInvoiceCorrect, j.BranchId);
        var done = await t.QuerySingleOrDefaultAsync<(Guid Credited, Guid? Corrected)>(
            "SELECT credited_sale_id, corrected_sale_id FROM repair.invoice_correction WHERE credit_note_id = @Id", new { req.Id });
        if (done.Credited != Guid.Empty) return await SummaryAsync(t, j, req.Id, done.Corrected);
        if (j.Status != "delivered") throw new KiteException(ErrorCodes.RepairCorrection, "Only a delivered job's invoice is corrected; before delivery, change the estimate.", 409);
        if (string.IsNullOrWhiteSpace(req.Reason) || req.Reason.Trim().Length < 3)
            throw new KiteException(ErrorCodes.Validation, "Give the reason for the correction.", 400, "reason");
        // The job's commissions next (job, then its accruals), so a payout racing this correction waits here, not later.
        await t.ExecuteAsync("SELECT id FROM comm.accrual WHERE job_id = @jobId ORDER BY id FOR UPDATE", new { jobId });
        var currentId = await t.ExecuteScalarAsync<Guid?>("SELECT repair.current_invoice(@jobId)", new { jobId })
            ?? throw new KiteException(ErrorCodes.RepairCorrection, $"Job {j.Number} has no invoice standing: it was credited already.", 409);

        var (term, shift, original, lines, biz, policy) = await PrepareCreditAsync(t, http, currentId, req.Id, repairCorrection: true);
        if (lines.Any(l => l.ReturnedQty != 0)) throw new KiteException(ErrorCodes.RepairCorrection, "Part of this invoice was credited already.", 409);
        var uid = http.User.UserIdOf();
        var now = t.Clock.UtcNow;
        var kinds = (await t.QueryAsync<(Guid Id, string? Kind)>("SELECT id, repair_kind FROM sales.sale_line WHERE sale_id = @currentId", new { currentId }))
            .ToDictionary(x => x.Id, x => x.Kind);

        // 1. The credit note: every line in full, nothing back to stock (the parts are in the customer's device).
        var credit = lines.Select(o => new DraftLine(o.VariantId, o.ProductType, o.DescriptionEn, o.DescriptionAr, o.Qty, o.UnitPrice, o.ListPrice, o.Discount,
            o.Net, o.TaxCodeId, o.TaxRate, o.Tax, o.Gross, null, 0, o.Id, null, null, null, null, null, RepairCharge: true, KnownCost: o.CostValue,
            RepairKind: kinds.GetValueOrDefault(o.Id))).ToList();
        var creditGross = credit.Sum(d => d.Gross);
        if (req.ExpectedCredit is { } shown && shown != creditGross)
            throw new KiteException(ErrorCodes.SaleChanged, $"The invoice to credit is {creditGross:0.000}, not {shown:0.000}.", 409, "expectedCredit");
        var refunds = PosMath.CheckTenders(req.Refunds ?? [], creditGross, policy.CashRounding, refund: true);
        await CheckCreditTendersAsync(t, term, original.CustomerId, refunds.Tenders, refund: true);
        foreach (var (p, i) in refunds.Tenders.Select((p, i) => (p, i)))
        {
            if (p.Method is not ("cash" or "card" or "bank_transfer" or "store_credit"))
                throw new KiteException(ErrorCodes.RefundMethod, "A correction refunds in cash, to a card, by transfer or as store credit.", 400, $"refunds[{i}].method");
            if (p.Method == "card" && p.CardTerminal is null)
                throw new KiteException(ErrorCodes.Validation, "Name the bank terminal the card refund is made on.", 400, $"refunds[{i}].cardTerminal");
        }

        // 2. The corrected invoice, priced like an estimate and paid at the till.
        List<DraftLine>? corrected = null;
        CheckedPayments? payments = null;
        if (req.Lines is { Count: > 0 })
        {
            var settings = await RepairCore.SettingsAsync(t);
            var priced = await RepairCore.PriceLinesAsync(t, biz, j.BranchId, settings,
                req.Lines.Select(l => new EstimateLineInput(l.Kind, l.VariantId, l.Qty, l.UnitPrice, l.Minutes, l.DescriptionEn, l.DescriptionAr)).ToList());
            var jobCost = await t.ExecuteScalarAsync<decimal>("""
                SELECT coalesce(sum(amount), 0) FROM repair.wip_clearing WHERE job_id = @jobId AND kind IN ('cost_of_repair','outside_work')
                """, new { jobId });
            var partIdx = priced.Select((c, i) => (c, i)).Where(x => x.c.Kind == "part").Select(x => x.i).ToArray();
            var costs = new decimal[priced.Count];
            if (jobCost > 0)
            {
                var targets = partIdx.Length > 0 ? partIdx : [0];
                var shares = RepairMath.AllocateCost(jobCost, targets.Select(i => priced[i].NetAmount).ToArray());
                for (var k = 0; k < targets.Length; k++) costs[targets[k]] = shares[k];
            }
            corrected = priced.Select((c, i) => new DraftLine(c.VariantId, c.ProductType, c.DescriptionEn, c.DescriptionAr, c.Qty, c.UnitPrice, c.UnitPrice, 0m,
                c.NetAmount, c.TaxCodeId, c.TaxRate, c.TaxAmount, c.GrossAmount, null, c.Kind is "part" or "labour" ? j.WarrantyDays : 0,
                RepairCharge: true, KnownCost: costs[i], RepairKind: c.Kind)).ToList();
            payments = PosMath.CheckTenders(req.Payments ?? [], corrected.Sum(d => d.Gross), policy.CashRounding);
            if (payments.Tenders.Any(p => p.Method is "deposit" or "trade_in"))
                throw new KiteException(ErrorCodes.Validation, "A corrected invoice is paid in cash, by card, by transfer or with store credit.", 400, "payments");
            await CheckCreditTendersAsync(t, term, j.CustomerId, payments.Tenders, refund: false);
        }
        else if ((req.Payments?.Count ?? 0) > 0) throw new KiteException(ErrorCodes.PaymentMismatch, "Nothing is charged again.", 400, "payments");

        var cashOut = refunds.Tenders.Where(p => p.Method == "cash").Sum(p => p.Amount) - (payments?.Tenders.Where(p => p.Method == "cash").Sum(p => p.Amount) ?? 0m);
        if (cashOut > 0 && cashOut > (await FiguresAsync(t, shift)).ExpectedCash)
            throw new KiteException(ErrorCodes.RefundMethod, "The drawer does not hold enough cash for this refund.", 400, "refunds");

        // Lock order (ADR 54): the customer's store credit, then both document numbers, before the first journal.
        if (refunds.Tenders.Concat(payments?.Tenders ?? []).Any(p => p.Method == "store_credit"))
            await TradeInCore.LockForPaymentAsync(t, [], j.CustomerId);
        var fy = await FiscalPeriods.FiscalYearOfAsync(t, biz.Id, BusinessDay.DateOf(now, biz.TimeZone));
        var creditNumber = await DocNumbers.NextAsync(t, biz.Id, term.BranchId, "credit_note", fy, "CN");
        var correctedNumberTaken = corrected is null ? null : await DocNumbers.NextAsync(t, biz.Id, term.BranchId, "sale_invoice", fy, "INV");

        var creditNote = await SaleWriter.WriteAsync(t, biz, term, policy, new SaleDraft(req.Id, shift.Id, uid, original.CustomerId, "credit_note", "correction",
            original.Id, null, now, credit, refunds, true, Note: $"Correction of {original.Number} (repair {j.Number}): {req.Reason.Trim()}", RepairJobId: j.Id, Number: creditNumber,
            Audit: number => new AuditEntry("repair.invoice_credited", "sale", req.Id, After: Json.Obj(new { number, original = original.Number, job = j.Number, creditGross }),
                Reason: req.Reason.Trim(), BranchId: term.BranchId)));
        Guid? correctedId = null;
        string? correctedNumber = null;
        if (corrected is not null)
        {
            correctedId = req.CorrectedId ?? t.NewId();
            var written = await SaleWriter.WriteAsync(t, biz, term, policy, new SaleDraft(correctedId.Value, shift.Id, uid, j.CustomerId, "tax_invoice", null, null, null,
                now, corrected, payments!, false, Note: $"Repair {j.Number}, corrected ({original.Number})", RepairJobId: j.Id, Number: correctedNumberTaken,
                Audit: number => new AuditEntry("repair.invoice_corrected", "sale", correctedId.Value, After: Json.Obj(new { number, job = j.Number, replaces = original.Number }),
                    BranchId: term.BranchId)));
            correctedNumber = written.Number;
        }
        await t.ExecuteAsync("""
            INSERT INTO repair.invoice_correction (id, business_id, branch_id, job_id, credited_sale_id, credit_note_id, corrected_sale_id, reason, created_by)
            VALUES (@cid, @b, @BranchId, @jobId, @currentId, @Id, @correctedId, @reason, @uid)
            """, new { cid = t.NewId(), b = biz.Id, j.BranchId, jobId, currentId, req.Id, correctedId, reason = req.Reason.Trim(), uid });

        // 3. The commissions follow the invoice (15.3): reversed with the credit note, generated again from the corrected invoice.
        var today = BusinessDay.DateOf(now, biz.TimeZone);
        await Commissions.ReverseInvoiceAsync(t, jobId, currentId, $"Invoice {original.Number} credited: {req.Reason.Trim()}", uid, today);
        if (correctedId is not null) await Commissions.GenerateAsync(t, jobId, uid);
        await t.AuditAsync(new AuditEntry("repair.corrected", "repair_job", jobId,
            After: Json.Obj(new { j.Number, credited = original.Number, creditNote = creditNote.Number, corrected = correctedNumber }), Reason: req.Reason.Trim(), BranchId: j.BranchId));
        return new
        {
            jobId, creditNoteId = req.Id, creditNoteNumber = creditNote.Number, credited = creditGross, refunded = refunds.Paid,
            correctedId, correctedNumber, correctedGross = corrected?.Sum(d => d.Gross), paid = payments?.Paid,
            change = payments?.Tenders.Sum(p => p.Change ?? 0m) ?? 0m,
        };
    }

    private static async Task<object> SummaryAsync(DbTx t, RepairJob j, Guid creditNoteId, Guid? correctedId)
    {
        var numbers = await t.QueryAsync<(Guid Id, string Number, decimal Gross)>("SELECT id, number, gross_total FROM sales.sale WHERE id = ANY(@ids)",
            new { ids = new[] { creditNoteId, correctedId ?? Guid.Empty } });
        var cn = numbers.First(n => n.Id == creditNoteId);
        var co = numbers.FirstOrDefault(n => n.Id == correctedId);
        return new { jobId = j.Id, creditNoteId, creditNoteNumber = cn.Number, credited = cn.Gross, correctedId, correctedNumber = co.Number, correctedGross = (decimal?)co.Gross };
    }
}
