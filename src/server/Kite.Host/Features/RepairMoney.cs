using Kite.Accounting;
using Kite.Host.Infrastructure;
using Kite.Persistence;
using Kite.Platform;
using Kite.Sales;

namespace Kite.Host.Features;

public static partial class RepairEndpoints
{
    // ------------------------------------------------------------------ deposits and delivery (13.1, 13.5, 13.7)
    private static void MapMoney(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/repairs/{id:guid}");

        // A deposit, taken at the till (it goes into the drawer like any tender) against the job; it is the customer's money until the
        // repair invoice applies it.
        g.MapPost("/deposits", async (Guid id, DepositRequest req, HttpContext http, KiteDb db, LicenceService licence, CancellationToken ct) =>
        {
            await licence.RequireTransactAsync(ct);
            return Results.Ok(await PosEndpoints.WithRetryAsync(() => db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var j = await RepairCore.LoadAsync(t, id, forUpdate: true);
                http.User.Demand(Permissions.RepairIntake, j.BranchId);
                if (j.Ended) throw new KiteException(ErrorCodes.RepairState, $"Job {j.Number} has ended; no deposit is taken on it.", 409);
                var term = await PosTerminals.CurrentAsync(t, http);
                if (term.BranchId != j.BranchId) throw new KiteException(ErrorCodes.Validation, $"Take the deposit at {j.BranchCode}, the branch with the device.", 400);
                var shift = await PosEndpoints.OpenShiftForUpdateAsync(t, term, http);
                if (req.Method is not ("cash" or "card" or "bank_transfer")) throw new KiteException(ErrorCodes.Validation, "A deposit is paid in cash, by card or by transfer.", 400, "method");
                var tender = PosMath.CheckTenders([new TenderInput(req.Method, req.Amount, req.Tendered, req.CardTerminal, req.ApprovalCode, req.Reference)], req.Amount, 0m).Tenders[0];
                var biz = await BusinessInfo.LoadAsync(t);
                var settings = await RepairCore.SettingsAsync(t);
                var tax = settings.DepositVatPoint ? RepairMath.DepositTax(tender.Amount, await StandardRateAsync(t, biz)) : 0m;
                var now = t.Clock.UtcNow;
                var date = BusinessDay.DateOf(now, biz.TimeZone);
                var depositId = t.NewId();
                var number = await DocNumbers.NextAsync(t, biz.Id, j.BranchId, "repair_deposit", await FiscalPeriods.FiscalYearOfAsync(t, biz.Id, date), "DEP");
                var ledger = t.NewId();
                var uid = Uid(http);
                await t.ExecuteAsync("""
                    INSERT INTO crm.credit_ledger (id, business_id, branch_id, customer_id, balance_type, amount, source_doc_type, source_doc_id, note, created_by,
                                                   origin_server_id, repair_job_id)
                    VALUES (@ledger, @b, @BranchId, @CustomerId, 'deposit', @amount, 'repair_deposit', @depositId, @note, @uid, (SELECT id FROM core.server WHERE is_self), @id)
                    """, new { ledger, b = biz.Id, j.BranchId, j.CustomerId, amount = tender.Amount, depositId, note = $"Deposit {number} on {j.Number}", uid, id });
                var journal = await Posting.PostAsync(t, biz.Id, j.BranchId, PostingRuleTemplates.RepairDeposit,
                    new PostingFacts().Add("tenders", tender.Method, tender.Amount).Set("deposit", tender.Amount - tax).Set("tax", tax),
                    date, "repair_deposit", depositId, $"Deposit {number} on repair {j.Number}", j.CustomerId);
                await t.ExecuteAsync("""
                    INSERT INTO repair.deposit (id, business_id, branch_id, job_id, number, kind, amount, tax, method, shift_id, terminal_id, credit_entry_id, journal_id,
                                                created_by, origin_server_id)
                    VALUES (@depositId, @b, @BranchId, @id, @number, 'deposit', @amount, @tax, @method, @shift, @terminal, @ledger, @journal, @uid,
                            (SELECT id FROM core.server WHERE is_self))
                    """, new { depositId, b = biz.Id, j.BranchId, id, number, amount = tender.Amount, tax, method = tender.Method, shift = shift.Id, terminal = term.Id,
                               ledger, journal, uid });
                await t.ExecuteAsync("""
                    INSERT INTO sales.payment (id, business_id, branch_id, shift_id, method, amount, tendered, change_given, card_terminal, approval_code, reference,
                                               created_at, repair_deposit_id)
                    VALUES (@pid, @b, @BranchId, @shift, @Method, @Amount, @Tendered, @Change, @CardTerminal, @ApprovalCode, @Reference, @now, @depositId)
                    """, new { pid = t.NewId(), b = biz.Id, j.BranchId, shift = shift.Id, tender.Method, tender.Amount, tender.Tendered, tender.Change,
                               tender.CardTerminal, tender.ApprovalCode, tender.Reference, now, depositId });
                await t.AuditAsync(new AuditEntry("repair.deposit_taken", "repair_job", id, After: Json.Obj(new { j.Number, receipt = number, tender.Amount, tender.Method, tax }), BranchId: j.BranchId));
                await t.PublishAsync("repair.deposit_taken", "repair_job", id, j.Version, Json.Obj(new { j.Number, receipt = number, tender.Amount }), j.BranchId);
                return new { id = depositId, number, amount = tender.Amount, tax, change = tender.Change ?? 0m, balance = await RepairCore.DepositBalanceAsync(t, id) };
            }, ct)));
        }).RequirePermission(Permissions.RepairIntake).RequireModule("repair");

        // A deposit given back before delivery (13.5): in cash or to the card at the till, by transfer, or as store credit.
        g.MapPost("/deposits/refund", async (Guid id, DepositRefundRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await PosEndpoints.WithRetryAsync(() => db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var j = await RepairCore.LoadAsync(t, id, forUpdate: true);
                http.User.Demand(Permissions.RepairCancel, j.BranchId);
                if (j.Status == "delivered") throw new KiteException(ErrorCodes.RepairState, "The job was delivered; its deposit was settled then.", 409);
                if (string.IsNullOrWhiteSpace(req.Reason)) throw new KiteException(ErrorCodes.Validation, "Say why the deposit is given back.", 400, "reason");
                var balance = await RepairCore.DepositBalanceAsync(t, id);
                if (req.Amount <= 0 || req.Amount != decimal.Round(req.Amount, 3)) throw new KiteException(ErrorCodes.Validation, "The amount is above zero, in baisa.", 400, "amount");
                if (req.Amount > balance) throw new KiteException(ErrorCodes.RepairDepositShort, $"The job holds {balance:0.000} in deposits.", 409, "amount");
                var heldTax = await RepairCore.DepositTaxAsync(t, id);
                var r = await RefundAsync(t, http, j, req.Amount, RepairMath.TaxShare(heldTax, balance, req.Amount), req.Method, req.Reason.Trim(), req.CardTerminal, req.Reference);
                return new { r.Id, r.Number, amount = req.Amount, balance = await RepairCore.DepositBalanceAsync(t, id) };
            }, ct)))).RequirePermission(Permissions.RepairCancel).RequireModule("repair");

        // What collection will charge: shown at the till before the cashier takes payment.
        g.MapGet("/delivery", async (Guid id, decimal? diagnosisFee, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var j = await RepairCore.LoadAsync(t, id);
                http.User.Demand(Permissions.RepairView, j.BranchId);
                var settings = await RepairCore.SettingsAsync(t);
                var charges = await RepairCore.ChargesAsync(t, j, diagnosisFee ?? settings.DiagnosisFee);
                var deposit = await RepairCore.DepositBalanceAsync(t, id);
                var split = RepairMath.Split(charges.Sum(c => c.GrossAmount), deposit, await RepairCore.DepositTaxAsync(t, id));
                return new
                {
                    j.Number, j.Status, j.Outcome, charges, gross = charges.Sum(c => c.GrossAmount), deposit, split.Applied, split.Due, split.Excess,
                    wip = await RepairCore.WipAsync(t, id), qr = QrText(j), settings.DiagnosisFee, j.CustomerId, j.CustomerName,
                };
            }, ct))).RequirePermission(Permissions.RepairView).RequireModule("repair");

        // Collection (13.5): the job card's QR or a check of the customer, their signature, the repair invoice (a tax invoice that
        // applies the deposit and takes the rest by any tender), the deposit's excess back, work-in-progress cleared, the warranty
        // started, the passcode deleted.
        g.MapPost("/deliver", async (Guid id, DeliverRequest req, HttpContext http, KiteDb db, LicenceService licence, CancellationToken ct) =>
        {
            await licence.RequireTransactAsync(ct);
            return Results.Ok(await PosEndpoints.WithRetryAsync(() => db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => DeliverAsync(t, http, id, req), ct)));
        }).RequirePermission(Permissions.RepairIntake).RequireModule("repair");
    }

    private static async Task<decimal> StandardRateAsync(DbTx t, BusinessInfo biz) =>
        await t.ExecuteScalarAsync<decimal?>("""
            SELECT r.rate_percent FROM tax.tax_code tc JOIN tax.tax_rate r ON r.tax_code_id = tc.id
             WHERE tc.business_id = @b AND tc.code = 'VAT-STD' AND r.effective_from <= current_date AND (r.effective_to IS NULL OR r.effective_to > current_date)
             ORDER BY r.effective_from DESC LIMIT 1
            """, new { b = biz.Id }) ?? 5m;

    private sealed record RefundDone(Guid Id, string Number, Guid? PaymentShift);

    /// <summary>Gives part of a job's deposit back: a numbered refund receipt, the ledger line, the tender (from the drawer, to the card,
    /// by transfer, or to store credit) and the journal (13.7). The job's row lock is held by the caller.</summary>
    private static async Task<RefundDone> RefundAsync(DbTx t, HttpContext http, RepairJob j, decimal amount, decimal tax, string method, string reason,
        string? cardTerminal, string? reference)
    {
        if (method is not ("cash" or "card" or "bank_transfer" or "store_credit"))
            throw new KiteException(ErrorCodes.Validation, "A deposit goes back in cash, to the card, by transfer or as store credit.", 400, "method");
        if (method == "card" && string.IsNullOrWhiteSpace(cardTerminal)) throw new KiteException(ErrorCodes.Validation, "Name the bank terminal the card refund is made on.", 400, "cardTerminal");
        if (method == "bank_transfer" && string.IsNullOrWhiteSpace(reference)) throw new KiteException(ErrorCodes.Validation, "A transfer needs its reference.", 400, "reference");
        Guid? shiftId = null, terminalId = null;
        if (method is "cash" or "card" or "bank_transfer")
        {
            var term = await PosTerminals.CurrentAsync(t, http);
            if (term.BranchId != j.BranchId) throw new KiteException(ErrorCodes.Validation, $"Give the deposit back at {j.BranchCode}.", 400);
            shiftId = (await PosEndpoints.OpenShiftForUpdateAsync(t, term, http)).Id;
            terminalId = term.Id;
        }
        var biz = await BusinessInfo.LoadAsync(t);
        var now = t.Clock.UtcNow;
        var date = BusinessDay.DateOf(now, biz.TimeZone);
        var uid = Uid(http);
        var refundId = t.NewId();
        var number = await DocNumbers.NextAsync(t, biz.Id, j.BranchId, "repair_deposit", await FiscalPeriods.FiscalYearOfAsync(t, biz.Id, date), "DEP");
        var ledger = t.NewId();
        await t.ExecuteAsync("""
            INSERT INTO crm.credit_ledger (id, business_id, branch_id, customer_id, balance_type, amount, source_doc_type, source_doc_id, note, created_by,
                                           origin_server_id, repair_job_id)
            VALUES (@ledger, @b, @BranchId, @CustomerId, 'deposit', @neg, 'repair_deposit', @refundId, @note, @uid, (SELECT id FROM core.server WHERE is_self), @jobId)
            """, new { ledger, b = biz.Id, j.BranchId, j.CustomerId, neg = -amount, refundId, note = $"Deposit refund {number} on {j.Number}", uid, jobId = j.Id });
        Guid? credit = null;
        if (method == "store_credit")
        {
            credit = t.NewId();
            await t.ExecuteAsync("""
                INSERT INTO crm.credit_ledger (id, business_id, branch_id, customer_id, balance_type, amount, source_doc_type, source_doc_id, note, created_by, origin_server_id)
                VALUES (@credit, @b, @BranchId, @CustomerId, 'store_credit', @amount, 'repair_deposit', @refundId, @note, @uid, (SELECT id FROM core.server WHERE is_self))
                """, new { credit, b = biz.Id, j.BranchId, j.CustomerId, amount, refundId, note = $"Deposit of {j.Number} kept as store credit", uid });
        }
        var journal = await Posting.PostAsync(t, biz.Id, j.BranchId, PostingRuleTemplates.RepairDepositRefund,
            new PostingFacts().Set("deposit", amount - tax).Set("tax", tax).Add("tenders", method, amount),
            date, "repair_deposit", refundId, $"Deposit refund {number} on repair {j.Number}", j.CustomerId);
        await t.ExecuteAsync("""
            INSERT INTO repair.deposit (id, business_id, branch_id, job_id, number, kind, amount, tax, method, shift_id, terminal_id, credit_entry_id, store_credit_entry_id,
                                        reason, journal_id, created_by, origin_server_id)
            VALUES (@refundId, @b, @BranchId, @jobId, @number, 'refund', @amount, @tax, @method, @shiftId, @terminalId, @ledger, @credit, @reason, @journal, @uid,
                    (SELECT id FROM core.server WHERE is_self))
            """, new { refundId, b = biz.Id, j.BranchId, jobId = j.Id, number, amount, tax, method, shiftId, terminalId, ledger, credit, reason, journal, uid });
        if (shiftId is not null)
            await t.ExecuteAsync("""
                INSERT INTO sales.payment (id, business_id, branch_id, shift_id, method, amount, card_terminal, reference, created_at, repair_deposit_id)
                VALUES (@pid, @b, @BranchId, @shiftId, @method, @neg, @cardTerminal, @reference, @now, @refundId)
                """, new { pid = t.NewId(), b = biz.Id, j.BranchId, shiftId, method, neg = -amount, cardTerminal = Clean(cardTerminal), reference = Clean(reference), now, refundId });
        await t.AuditAsync(new AuditEntry("repair.deposit_refunded", "repair_job", j.Id, After: Json.Obj(new { j.Number, receipt = number, amount, method, reason }), BranchId: j.BranchId));
        await t.PublishAsync("repair.deposit_refunded", "repair_job", j.Id, j.Version, Json.Obj(new { j.Number, receipt = number, amount, method }), j.BranchId);
        return new RefundDone(refundId, number, shiftId);
    }

    private static async Task<object> DeliverAsync(DbTx t, HttpContext http, Guid id, DeliverRequest req)
    {
        var j = await RepairCore.LoadAsync(t, id, forUpdate: true);
        http.User.Demand(Permissions.RepairIntake, j.BranchId);
        if (j.Status != "ready") throw new KiteException(ErrorCodes.RepairState, $"Job {j.Number} is {RepairCore.Words(j.Status)}, not ready for collection.", 409);
        var uid = Uid(http);

        // Collection: the job card's QR, or a check of the customer by staff; the name of who collects and their signature.
        switch (req.Check)
        {
            case "qr":
                if (!string.Equals((req.QrCode ?? "").Trim(), QrText(j), StringComparison.Ordinal))
                    throw new KiteException(ErrorCodes.RepairCollection, "That QR code is not this job's card. Scan the customer's job card, or check the customer instead.", 409, "qrCode");
                break;
            case "verified":
                break;
            default:
                throw new KiteException(ErrorCodes.Validation, "Collection needs the job card's QR code or a check of the customer.", 400, "check");
        }
        var collectedBy = (req.CollectedBy ?? "").Trim();
        if (collectedBy.Length < 2) throw new KiteException(ErrorCodes.Validation, "Type the name of the person collecting.", 400, "collectedBy");
        if (string.IsNullOrWhiteSpace(req.Signature)) throw new KiteException(ErrorCodes.Validation, "The person collecting signs for the device.", 400, "signature");

        var biz = await BusinessInfo.LoadAsync(t);
        var settings = await RepairCore.SettingsAsync(t);
        var fee = req.DiagnosisFee ?? settings.DiagnosisFee;
        if (req.DiagnosisFee is { } given)
        {
            if (j.Outcome == "repaired") throw new KiteException(ErrorCodes.Validation, "A diagnosis fee is charged only when the job was cancelled or could not be repaired.", 400, "diagnosisFee");
            if (given < 0 || given != decimal.Round(given, 3)) throw new KiteException(ErrorCodes.Validation, "The fee is zero or more, in baisa.", 400, "diagnosisFee");
            if (given != settings.DiagnosisFee) http.User.Demand(Permissions.RepairCancel, j.BranchId);
        }
        var wip = await RepairCore.WipAsync(t, id);
        if (j.Outcome != "repaired" && wip != 0)
            throw new KiteException(ErrorCodes.RepairState, $"The job still holds {wip:0.000} of parts. Return them to stock or write them off before the device goes back.", 409);

        var charges = await RepairCore.ChargesAsync(t, j, fee);
        var gross = charges.Sum(c => c.GrossAmount);
        var deposit = await RepairCore.DepositBalanceAsync(t, id);
        var split = RepairMath.Split(gross, deposit, await RepairCore.DepositTaxAsync(t, id));
        if (req.ExpectedDue is { } shown && shown != split.Due)
            throw new KiteException(ErrorCodes.SaleChanged, $"The amount due is now {split.Due:0.000}, not {shown:0.000}. Check and try again.", 409, "expectedDue");

        PosTerminal? term = null;
        ShiftRow? shift = null;
        var needsTill = gross > 0 || (split.Excess > 0 && req.ExcessTo != "store_credit");
        if (needsTill)
        {
            term = await PosTerminals.CurrentAsync(t, http);
            if (term.BranchId != j.BranchId) throw new KiteException(ErrorCodes.Validation, $"The device is collected at {j.BranchCode}.", 400);
            shift = await PosEndpoints.OpenShiftForUpdateAsync(t, term, http);
        }

        // The repair invoice: the charges, the deposit applied, the rest by any tender (13.5).
        Guid? saleId = null;
        string? saleNumber = null;
        CheckedPayments? payments = null;
        if (gross > 0)
        {
            var policy = await PosPolicies.ForBranchAsync(t, biz.Id, term!.BranchId);
            var paid = PosMath.CheckTenders(req.Payments ?? [], split.Due, policy.CashRounding);
            if (paid.Tenders.Any(p => p.Method == "deposit")) throw new KiteException(ErrorCodes.Validation, "The deposit is applied by itself.", 400, "payments");
            await PosEndpoints.CheckCreditTendersAsync(t, term, j.CustomerId, paid.Tenders, refund: false);
            var tenders = paid.Tenders.ToList();
            if (split.Applied > 0) tenders.Add(new CheckedTender("deposit", split.Applied, null, null, null, null, null));
            payments = new CheckedPayments(tenders, paid.CashRounding, paid.Paid + split.Applied);
            var partCost = j.Outcome == "repaired" && !j.IsWarrantyClaim ? wip : 0m;
            var partIdx = charges.Select((c, i) => (c, i)).Where(x => x.c.Kind == "part").Select(x => x.i).ToArray();
            var costs = new decimal[charges.Count];
            if (partCost > 0)
            {
                var targets = partIdx.Length > 0 ? partIdx : [0];
                var shares = RepairMath.AllocateCost(partCost, targets.Select(i => charges[i].NetAmount).ToArray());
                for (var k = 0; k < targets.Length; k++) costs[targets[k]] = shares[k];
            }
            var lines = charges.Select((c, i) => new DraftLine(c.VariantId, c.ProductType, c.DescriptionEn, c.DescriptionAr, c.Qty, c.UnitPrice, c.UnitPrice,
                0m, c.NetAmount, c.TaxCodeId, c.TaxRate, c.TaxAmount, c.GrossAmount, null, c.Kind == "part" || c.Kind == "labour" ? j.WarrantyDays : 0,
                RepairCharge: true, KnownCost: costs[i])).ToList();
            saleId = t.NewId();
            var written = await SaleWriter.WriteAsync(t, biz, term, policy, new SaleDraft(saleId.Value, shift!.Id, uid, j.CustomerId, "tax_invoice", null, null, null,
                t.Clock.UtcNow, lines, payments, false, Note: $"Repair {j.Number}", RepairJobId: j.Id, DepositTax: split.AppliedTax,
                Audit: number => new AuditEntry("repair.invoiced", "sale", saleId.Value, After: Json.Obj(new { number, job = j.Number, gross }), BranchId: j.BranchId)));
            saleNumber = written.Number;
        }
        else if ((req.Payments?.Count ?? 0) > 0) throw new KiteException(ErrorCodes.PaymentMismatch, "Nothing is charged for this job.", 400, "payments");

        // The deposit's excess goes back (in cash from the drawer, or as store credit).
        RefundDone? refund = null;
        if (split.Excess > 0)
            refund = await RefundAsync(t, http, j, split.Excess, split.ExcessTax, req.ExcessTo == "store_credit" ? "store_credit" : "cash",
                $"Deposit above the charges of {j.Number}, given back at collection", null, null);

        // Work-in-progress leaves the job: the cost of the repair, or warranty expense on a warranty claim (13.7).
        if (j.Outcome == "repaired" && wip > 0)
        {
            var kind = j.IsWarrantyClaim ? "warranty" : "cost_of_repair";
            var cid = t.NewId();
            var journal = await Posting.PostAsync(t, biz.Id, j.BranchId, PostingRuleTemplates.RepairCost, new PostingFacts().Set(kind, wip),
                BusinessDay.Today(biz.TimeZone, t.Clock), "repair_wip", cid, $"Repair {j.Number}: {(j.IsWarrantyClaim ? "warranty repair" : "parts cost")}");
            await t.ExecuteAsync("""
                INSERT INTO repair.wip_clearing (id, business_id, branch_id, job_id, kind, amount, journal_id, created_by) VALUES (@cid, @b, @BranchId, @id, @kind, @wip, @journal, @uid)
                """, new { cid, b = biz.Id, j.BranchId, id, kind, wip, journal, uid });
        }

        await SavePhotoAsync(t, j, "collection_signature", req.Signature, $"Collected by {collectedBy}", null, uid);
        var today = BusinessDay.Today(biz.TimeZone, t.Clock);
        DateOnly? warrantyUntil = j.Outcome == "repaired" && j.WarrantyDays > 0 && !j.IsWarrantyClaim ? today.AddDays(j.WarrantyDays) : null;
        if (j.IsWarrantyClaim && j.WarrantyOfId is { } orig)
            // A warranty repair keeps the original repair's warranty.
            warrantyUntil = await t.ExecuteScalarAsync<DateTime?>("SELECT warranty_until FROM repair.repair_job WHERE id = @orig", new { orig }) is { } w
                ? DateOnly.FromDateTime(w) : null;
        await t.ExecuteAsync("SELECT set_config('kite.repair_note', @n, true)", new { n = $"Collected by {collectedBy} ({(req.Check == "qr" ? "job card QR" : "customer checked")})" });
        await t.ExecuteAsync("""
            UPDATE repair.repair_job
               SET status = 'delivered', delivered_at = now(), delivered_by = @uid, collected_by = @collectedBy, collection_check = @Check,
                   delivery_sale_id = @saleId, warranty_until = @until, passcode_enc = NULL,
                   passcode_purged_at = CASE WHEN passcode_enc IS NOT NULL THEN now() ELSE passcode_purged_at END, updated_by = @uid
             WHERE id = @id
            """, new { uid, collectedBy, req.Check, saleId, until = warrantyUntil?.ToDateTime(TimeOnly.MinValue), id });
        await t.ExecuteAsync("SELECT set_config('kite.repair_note', '', true)");
        await t.AuditAsync(new AuditEntry("repair.delivered", "repair_job", id,
            After: Json.Obj(new { j.Number, saleNumber, gross, applied = split.Applied, excess = split.Excess, wip, collectedBy, req.Check }), BranchId: j.BranchId));
        await t.PublishAsync("repair.delivered", "repair_job", id, j.Version + 1, Json.Obj(new { j.Number, saleNumber, gross }), j.BranchId);
        return new
        {
            id, number = j.Number, saleId, saleNumber, gross, applied = split.Applied, due = split.Due, excess = split.Excess, refundId = refund?.Id,
            refundNumber = refund?.Number, change = payments?.Tenders.Sum(p => p.Change ?? 0m) ?? 0m, cashRounding = payments?.CashRounding ?? 0m,
            warrantyUntil,
        };
    }
}
