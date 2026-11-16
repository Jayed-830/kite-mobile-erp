using System.Globalization;
using System.Text.Json;
using Kite.Accounting;
using Kite.Persistence;
using Kite.Platform;
using Kite.Sales;

namespace Kite.Host.Infrastructure;

/// <summary>A repair job as the server works with it (blueprint 13).</summary>
public sealed class RepairJob
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }
    public Guid BranchId { get; set; }
    public string BranchCode { get; set; } = "";
    public string Number { get; set; } = "";
    public string Status { get; set; } = "";
    public string? Outcome { get; set; }
    public Guid CustomerId { get; set; }
    public string CustomerName { get; set; } = "";
    public string? CustomerNameAr { get; set; }
    public string? CustomerPhone { get; set; }
    public bool CustomerOptIn { get; set; }
    public DateTime? CustomerOptInAt { get; set; }
    public Guid CustomerDeviceId { get; set; }
    public string? Imei1 { get; set; }
    public string? Imei2 { get; set; }
    public string? Serial { get; set; }
    public string? Brand { get; set; }
    public string Model { get; set; } = "";
    public string? Colour { get; set; }
    public Guid? DeviceUnitId { get; set; }
    public Guid CategoryId { get; set; }
    public string CategoryCode { get; set; } = "";
    public string CategoryNameEn { get; set; } = "";
    public string CategoryNameAr { get; set; } = "";
    public Guid? TechnicianId { get; set; }
    public string? TechnicianName { get; set; }
    public Guid? WarrantyOfId { get; set; }
    public string? WarrantyOfNumber { get; set; }
    public Guid? SoldSaleId { get; set; }
    public string? SoldSaleNumber { get; set; }
    public string Problem { get; set; } = "";
    public string[] FaultCodes { get; set; } = [];
    public string Condition { get; set; } = "{}";
    public string? ConditionNotes { get; set; }
    public string Accessories { get; set; } = "{}";
    public string PasscodeMode { get; set; } = "none";
    public bool PasscodeHeld { get; set; }
    public DateTime? PasscodePurgedAt { get; set; }
    public decimal PreAuthorised { get; set; }
    public int WarrantyDays { get; set; }
    public DateTime PromisedAt { get; set; }
    public string TermsName { get; set; } = "";
    public DateTime TermsAcceptedAt { get; set; }
    public bool NotifyWhatsapp { get; set; }
    public string Lang { get; set; } = "ar";
    public Guid? DeliverySaleId { get; set; }
    public string? DeliverySaleNumber { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public string? DeliveredByName { get; set; }
    public string? CollectedBy { get; set; }
    public string? CollectionCheck { get; set; }
    public DateTime? WarrantyUntil { get; set; }
    public int RemindedDays { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid CreatedBy { get; set; }
    public string CreatedByName { get; set; } = "";
    public int Version { get; set; }

    public string Identifier => Imei1 ?? Serial ?? "";
    public string DeviceName => string.IsNullOrWhiteSpace(Brand) ? Model : $"{Brand} {Model}";
    public bool IsWarrantyClaim => WarrantyOfId is not null;
    public bool Ended => Outcome is not null;
}

public sealed class RepairSettings
{
    public decimal PreAuthorised { get; set; }
    public int WarrantyDays { get; set; }
    public int PasscodeKeepDays { get; set; }
    public int[] UncollectedDays { get; set; } = [];
    public decimal DiagnosisFee { get; set; }
    public decimal HourlyRate { get; set; }
    public bool DepositVatPoint { get; set; }
    public string[] NotifyStatuses { get; set; } = [];
    public string TermsEn { get; set; } = "";
    public string TermsAr { get; set; } = "";
    public int Version { get; set; }
}

public sealed class RepairEstimate
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public int EstimateNo { get; set; }
    public string Status { get; set; } = "";
    public decimal NetTotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal Total { get; set; }
    public string? ApprovalMethod { get; set; }
    public int Version { get; set; }
}

public sealed class RepairEstimateLine
{
    public Guid Id { get; set; }
    public short LineNo { get; set; }
    public string Kind { get; set; } = "";
    public Guid VariantId { get; set; }
    public string ProductType { get; set; } = "";
    public string DescriptionEn { get; set; } = "";
    public string DescriptionAr { get; set; } = "";
    public decimal Qty { get; set; }
    public int? Minutes { get; set; }
    public decimal UnitPrice { get; set; }
    public Guid TaxCodeId { get; set; }
    public decimal TaxRate { get; set; }
    public decimal NetAmount { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal GrossAmount { get; set; }
}

/// <summary>An estimate line as staff write it: a catalog service (labour or an extra charge) or a part, at its price or the given one.</summary>
public sealed record EstimateLineInput(string Kind, Guid VariantId, decimal Qty = 1m, decimal? UnitPrice = null, int? Minutes = null,
    string? DescriptionEn = null, string? DescriptionAr = null);

/// <summary>The repair job's rules that are shared by the endpoints and the WhatsApp gateway (blueprint 13).</summary>
public static class RepairCore
{
    public const string JobSelect = """
        SELECT j.id, j.business_id AS BusinessId, j.branch_id AS BranchId, b.code AS BranchCode, j.number, j.status, j.outcome,
               j.customer_id AS CustomerId, c.name_en AS CustomerName, c.name_ar AS CustomerNameAr, c.phone_e164 AS CustomerPhone,
               c.whatsapp_opt_in AS CustomerOptIn, c.whatsapp_opt_in_at AS CustomerOptInAt,
               j.customer_device_id AS CustomerDeviceId, d.imei1, d.imei2, d.serial, d.brand, d.model, d.colour, d.device_unit_id AS DeviceUnitId,
               j.category_id AS CategoryId, cat.code AS CategoryCode, cat.name_en AS CategoryNameEn, cat.name_ar AS CategoryNameAr,
               j.technician_id AS TechnicianId, tu.display_name_en AS TechnicianName, j.warranty_of_id AS WarrantyOfId, wj.number AS WarrantyOfNumber,
               j.sold_sale_id AS SoldSaleId, ss.number AS SoldSaleNumber, j.problem, j.fault_codes AS FaultCodes, j.condition::text AS Condition,
               j.condition_notes AS ConditionNotes, j.accessories::text AS Accessories, j.passcode_mode AS PasscodeMode,
               (j.passcode_enc IS NOT NULL) AS PasscodeHeld, j.passcode_purged_at AS PasscodePurgedAt, j.pre_authorised AS PreAuthorised,
               j.warranty_days AS WarrantyDays, j.promised_at AS PromisedAt, j.terms_name AS TermsName, j.terms_accepted_at AS TermsAcceptedAt,
               j.notify_whatsapp AS NotifyWhatsapp, j.lang, j.delivery_sale_id AS DeliverySaleId, ds.number AS DeliverySaleNumber,
               j.delivered_at AS DeliveredAt, du.display_name_en AS DeliveredByName, j.collected_by AS CollectedBy, j.collection_check AS CollectionCheck,
               j.warranty_until AS WarrantyUntil, j.reminded_days AS RemindedDays, j.created_at AS CreatedAt, j.created_by AS CreatedBy,
               cu.display_name_en AS CreatedByName, j.version
          FROM repair.repair_job j
          JOIN core.branch b ON b.id = j.branch_id
          JOIN crm.customer c ON c.id = j.customer_id
          JOIN device.customer_device d ON d.id = j.customer_device_id
          JOIN repair.category cat ON cat.id = j.category_id
          JOIN iam."user" cu ON cu.id = j.created_by
          LEFT JOIN iam."user" tu ON tu.id = j.technician_id
          LEFT JOIN iam."user" du ON du.id = j.delivered_by
          LEFT JOIN repair.repair_job wj ON wj.id = j.warranty_of_id
          LEFT JOIN sales.sale ss ON ss.id = j.sold_sale_id
          LEFT JOIN sales.sale ds ON ds.id = j.delivery_sale_id
        """;

    public static readonly string[] Statuses =
    [
        "received", "inspection_pending", "under_diagnosis", "waiting_approval", "waiting_parts", "in_progress", "testing",
        "completed", "ready", "delivered", "cancelled", "unrepairable",
    ];

    /// <summary>The transitions of 13.2 (the database holds the same table and refuses any other).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Transitions = new Dictionary<string, string[]>
    {
        ["received"] = ["inspection_pending", "cancelled"],
        ["inspection_pending"] = ["under_diagnosis", "cancelled"],
        ["under_diagnosis"] = ["waiting_approval", "in_progress", "unrepairable"],
        ["waiting_approval"] = ["waiting_parts", "in_progress", "cancelled"],
        ["waiting_parts"] = ["in_progress", "cancelled"],
        ["in_progress"] = ["testing", "waiting_approval", "waiting_parts", "unrepairable"],
        ["testing"] = ["completed", "in_progress"],
        ["completed"] = ["ready"],
        ["ready"] = ["delivered"],
        ["delivered"] = [],
        ["cancelled"] = ["ready"],
        ["unrepairable"] = ["ready"],
    };

    public static async Task<RepairJob> LoadAsync(DbTx t, Guid id, bool forUpdate = false)
    {
        if (forUpdate)
        {
            // The job row first: every repair writer locks it before a shift, stock, the deposit ledger or numbers (ADR 56).
            var found = await t.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM repair.repair_job WHERE id = @id FOR UPDATE)", new { id });
            if (!found) throw new KiteException(ErrorCodes.RepairNotFound, "That repair job was not found in your branches.", 404);
        }
        return await t.QuerySingleOrDefaultAsync<RepairJob>(JobSelect + " WHERE j.id = @id", new { id })
            ?? throw new KiteException(ErrorCodes.RepairNotFound, "That repair job was not found in your branches.", 404);
    }

    public static async Task<RepairSettings> SettingsAsync(DbTx t) =>
        await t.QuerySingleOrDefaultAsync<RepairSettings>("""
            SELECT pre_authorised AS PreAuthorised, warranty_days AS WarrantyDays, passcode_keep_days AS PasscodeKeepDays,
                   uncollected_days AS UncollectedDays, diagnosis_fee AS DiagnosisFee, hourly_rate AS HourlyRate,
                   deposit_vat_point AS DepositVatPoint, notify_statuses AS NotifyStatuses, terms_en AS TermsEn, terms_ar AS TermsAr, version
              FROM repair.settings LIMIT 1
            """) ?? throw new KiteException(ErrorCodes.Internal, "Repair settings are missing.", 500);

    public static Task<RepairEstimate?> CurrentEstimateAsync(DbTx t, Guid jobId) =>
        t.QuerySingleOrDefaultAsync<RepairEstimate>("""
            SELECT id, job_id AS JobId, estimate_no AS EstimateNo, status, net_total AS NetTotal, tax_total AS TaxTotal, total,
                   approval_method AS ApprovalMethod, version
              FROM repair.estimate WHERE job_id = @jobId ORDER BY estimate_no DESC LIMIT 1
            """, new { jobId });

    public static async Task<IReadOnlyList<RepairEstimateLine>> EstimateLinesAsync(DbTx t, Guid estimateId) =>
        (await t.QueryAsync<RepairEstimateLine>("""
            SELECT l.id, l.line_no AS LineNo, l.kind, l.variant_id AS VariantId, p.product_type AS ProductType, l.description_en AS DescriptionEn,
                   l.description_ar AS DescriptionAr, l.qty, l.minutes, l.unit_price AS UnitPrice, l.tax_code_id AS TaxCodeId, l.tax_rate AS TaxRate,
                   l.net_amount AS NetAmount, l.tax_amount AS TaxAmount, l.gross_amount AS GrossAmount
              FROM repair.estimate_line l JOIN catalog.variant v ON v.id = l.variant_id JOIN catalog.product p ON p.id = v.product_id
             WHERE l.estimate_id = @estimateId ORDER BY l.line_no
            """, new { estimateId })).ToList();

    public static Task<decimal> CeilingAsync(DbTx t, Guid jobId) =>
        t.ExecuteScalarAsync<decimal>("SELECT repair.authorised_ceiling(@jobId)", new { jobId });

    public static Task<decimal> DepositBalanceAsync(DbTx t, Guid jobId) =>
        t.ExecuteScalarAsync<decimal>("SELECT repair.deposit_balance(@jobId)", new { jobId });

    public static Task<decimal> DepositTaxAsync(DbTx t, Guid jobId) =>
        t.ExecuteScalarAsync<decimal>("SELECT repair.deposit_tax(@jobId)", new { jobId });

    public static Task<decimal> WipAsync(DbTx t, Guid jobId) =>
        t.ExecuteScalarAsync<decimal>("SELECT repair.wip(@jobId)", new { jobId });

    /// <summary>Moves a job to another status; the database refuses a transition 13.2 does not allow and writes the history row
    /// with this note. A version from the client must match.</summary>
    public static async Task MoveAsync(DbTx t, RepairJob j, string to, string? note, Guid? actor, int? expectedVersion = null, string? outcome = null)
    {
        if (expectedVersion is { } v && v != j.Version)
            throw new KiteException(ErrorCodes.VersionMismatch, "Someone else changed this job. Reload and try again.", 412, "version");
        if (!Statuses.Contains(to)) throw new KiteException(ErrorCodes.Validation, $"'{to}' is not a repair status.", 400, "to");
        if (!Transitions[j.Status].Contains(to))
            throw new KiteException(ErrorCodes.RepairTransition, $"A job that is {Words(j.Status)} cannot move to {Words(to)}.", 409, "to");
        var newOutcome = to switch
        {
            "completed" => "repaired",
            "cancelled" => "cancelled",
            "unrepairable" => "unrepairable",
            _ => j.Outcome,
        };
        await t.ExecuteAsync("SELECT set_config('kite.repair_note', @note, true)", new { note = string.IsNullOrWhiteSpace(note) ? "" : note.Trim() });
        await t.ExecuteAsync("""
            UPDATE repair.repair_job SET status = @to, outcome = @newOutcome, updated_by = @actor WHERE id = @id
            """, new { to, newOutcome = outcome ?? newOutcome, actor, id = j.Id });
        await t.ExecuteAsync("SELECT set_config('kite.repair_note', '', true)");
        var from = j.Status;
        j.Status = to;
        j.Outcome = outcome ?? newOutcome;
        j.Version++;
        await t.AuditAsync(new AuditEntry("repair.status_changed", "repair_job", j.Id,
            After: Json.Obj(new { j.Number, from, to, note = string.IsNullOrWhiteSpace(note) ? null : note.Trim() }), BranchId: j.BranchId));
        await t.PublishAsync("repair.status_changed", "repair_job", j.Id, j.Version, Json.Obj(new { j.Number, from, to }), j.BranchId);
        // A job that has ended no longer holds parts or waits for them (13.4).
        if (to is "completed" or "cancelled" or "unrepairable") await RepairParts.ReconcileAsync(t, j, actor);
        // Work cannot start without its parts: a job sent to repair while parts are short waits for them, and moves on by
        // itself when they arrive (13.4).
        if (to == "in_progress")
        {
            var missing = await t.ExecuteScalarAsync<decimal>("SELECT coalesce(sum(qty), 0) FROM repair.part_request WHERE job_id = @id", new { id = j.Id });
            if (missing > 0) await MoveAsync(t, j, "waiting_parts", $"Waiting for parts: {missing:0.###} not in stock", actor);
        }
    }

    public static string Words(string status) => status.Replace('_', ' ');

    // ------------------------------------------------------------------ estimates
    /// <summary>Prices estimate lines (13.5): labour and extra charges are catalog services; parts are spare parts, accessories or
    /// consumables counted by quantity. Labour can be charged by the hour at the repair rate.</summary>
    public static async Task<List<RepairEstimateLine>> PriceLinesAsync(DbTx t, BusinessInfo biz, Guid branchId, RepairSettings settings,
        IReadOnlyList<EstimateLineInput>? input, bool allowEmpty = false)
    {
        if (input is null || (input.Count == 0 && !allowEmpty)) throw new KiteException(ErrorCodes.Validation, "Add at least one line.", 400, "lines");
        if (input.Count > 60) throw new KiteException(ErrorCodes.Validation, "An estimate has at most 60 lines.", 400, "lines");
        var items = await ItemPricing.LoadAsync(t, branchId, input.Select(l => l.VariantId), BusinessDay.Today(biz.TimeZone, t.Clock));
        var result = new List<RepairEstimateLine>();
        for (var i = 0; i < input.Count; i++)
        {
            var l = input[i];
            var f = $"lines[{i}]";
            if (!items.TryGetValue(l.VariantId, out var item)) throw new KiteException(ErrorCodes.Validation, "That item is not in the catalog.", 400, f + ".variantId");
            if (l.Kind is not ("labour" or "part" or "extra")) throw new KiteException(ErrorCodes.Validation, "A line is labour, a part or an extra charge.", 400, f + ".kind");
            // A part is counted by quantity, or (13.4) is a spare part tracked by serial number and fitted one unit at a time.
            if (l.Kind == "part" && (item.ProductType is "service" or "device" || item.Tracking == "imei"))
                throw new KiteException(ErrorCodes.Validation, $"{item.Sku} is not a part: a part is a spare part, accessory or consumable.", 400, f + ".variantId");
            if (l.Kind == "part" && item.Tracking == "serial" && l.Qty != decimal.Truncate(l.Qty))
                throw new KiteException(ErrorCodes.Validation, $"{item.Sku} is tracked by serial number: whole units only.", 400, f + ".qty");
            if (l.Kind != "part" && item.ProductType != "service")
                throw new KiteException(ErrorCodes.Validation, $"{item.Sku} is not a service: labour and extra charges are services in the catalog.", 400, f + ".variantId");
            if (l.Minutes is not null && l.Kind != "labour") throw new KiteException(ErrorCodes.Validation, "Only labour is charged by the minute.", 400, f + ".minutes");
            decimal qty = l.Qty;
            decimal? price = l.UnitPrice;
            if (l.Minutes is { } minutes)
            {
                qty = 1m;
                price = RepairMath.LabourPrice(minutes, settings.HourlyRate);
            }
            // A serial part is charged on the invoice by quantity; which unit was fitted is on the job (13.4).
            var priced = PosMath.PriceLine(i + 1, new CartLine(l.VariantId, qty, price), item.Tracking == "serial" ? item with { Tracking = "none" } : item,
                biz.PricesIncludeTax, DiscountLimit.Any);
            var en = string.IsNullOrWhiteSpace(l.DescriptionEn) ? item.DescriptionEn : l.DescriptionEn.Trim();
            var ar = string.IsNullOrWhiteSpace(l.DescriptionAr) ? item.DescriptionAr : l.DescriptionAr.Trim();
            if (l.Minutes is { } m2) { en += $" ({m2} min)"; ar += $" ({m2} دقيقة)"; }
            result.Add(new RepairEstimateLine
            {
                LineNo = (short)(i + 1), Kind = l.Kind, VariantId = l.VariantId, ProductType = item.ProductType, DescriptionEn = en, DescriptionAr = ar,
                Qty = priced.Qty, Minutes = l.Minutes, UnitPrice = priced.UnitPrice, TaxCodeId = item.TaxCodeId, TaxRate = item.TaxRate,
                NetAmount = priced.Net, TaxAmount = priced.Tax, GrossAmount = priced.Gross,
            });
        }
        return result;
    }

    /// <summary>Replaces a draft estimate's lines and totals.</summary>
    public static async Task WriteLinesAsync(DbTx t, Guid estimateId, IReadOnlyList<RepairEstimateLine> lines, string? note, Guid actor)
    {
        await t.ExecuteAsync("DELETE FROM repair.estimate_line WHERE estimate_id = @estimateId", new { estimateId });
        foreach (var l in lines)
            await t.ExecuteAsync("""
                INSERT INTO repair.estimate_line (id, estimate_id, line_no, kind, variant_id, description_en, description_ar, qty, minutes, unit_price,
                                                  tax_code_id, tax_rate, net_amount, tax_amount, gross_amount)
                VALUES (@id, @estimateId, @LineNo, @Kind, @VariantId, @DescriptionEn, @DescriptionAr, @Qty, @Minutes, @UnitPrice, @TaxCodeId, @TaxRate,
                        @NetAmount, @TaxAmount, @GrossAmount)
                """, new { id = t.NewId(), estimateId, l.LineNo, l.Kind, l.VariantId, l.DescriptionEn, l.DescriptionAr, l.Qty, l.Minutes, l.UnitPrice,
                           l.TaxCodeId, l.TaxRate, l.NetAmount, l.TaxAmount, l.GrossAmount });
        await t.ExecuteAsync("""
            UPDATE repair.estimate SET net_total = @net, tax_total = @tax, total = @gross, note = @note, updated_by = @actor WHERE id = @estimateId
            """, new { net = lines.Sum(l => l.NetAmount), tax = lines.Sum(l => l.TaxAmount), gross = lines.Sum(l => l.GrossAmount),
                       note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(), actor, estimateId });
    }

    /// <summary>A new estimate version (13.3): a draft; earlier drafts and sent versions are superseded by the database.</summary>
    public static async Task<Guid> NewEstimateAsync(DbTx t, RepairJob j, IReadOnlyList<RepairEstimateLine> lines, string? note, Guid actor)
    {
        var id = t.NewId();
        var no = await t.ExecuteScalarAsync<int>("SELECT coalesce(max(estimate_no), 0) + 1 FROM repair.estimate WHERE job_id = @id", new { id = j.Id });
        await t.ExecuteAsync("""
            INSERT INTO repair.estimate (id, business_id, branch_id, job_id, estimate_no, created_by) VALUES (@id, @b, @BranchId, @jobId, @no, @actor)
            """, new { id, b = j.BusinessId, j.BranchId, jobId = j.Id, no, actor });
        await WriteLinesAsync(t, id, lines, note, actor);
        await t.AuditAsync(new AuditEntry("repair.estimate_created", "repair_job", j.Id,
            After: Json.Obj(new { j.Number, version = no, total = lines.Sum(l => l.GrossAmount) }), BranchId: j.BranchId));
        return id;
    }

    /// <summary>Records the approval of the job's current estimate (13.3). The database checks the method's details, that it is the
    /// current version and that staff approve on their own only within what the customer authorised.</summary>
    public static async Task ApproveAsync(DbTx t, RepairJob j, RepairEstimate e, string method, string? approverName, string? reference,
        Guid? actor, DateTimeOffset at)
    {
        await t.ExecuteAsync("""
            UPDATE repair.estimate SET status = 'approved', approval_method = @method, approved_at = @at, approved_by = @actor,
                   approver_name = @approverName, approval_ref = @reference, updated_by = @actor
             WHERE id = @id
            """, new { method, at, actor, approverName = string.IsNullOrWhiteSpace(approverName) ? null : approverName.Trim(),
                       reference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim(), id = e.Id });
        await t.AuditAsync(new AuditEntry("repair.estimate_approved", "repair_job", j.Id,
            After: Json.Obj(new { j.Number, version = e.EstimateNo, e.Total, method, approverName, reference }), BranchId: j.BranchId));
        await t.PublishAsync("repair.estimate_approved", "repair_job", j.Id, j.Version, Json.Obj(new { j.Number, version = e.EstimateNo, method }), j.BranchId);
        // The approved estimate's parts are reserved; what the branch does not have is requested (13.4).
        await RepairParts.ReconcileAsync(t, j, actor);
    }

    // ------------------------------------------------------------------ WhatsApp (5.3)
    /// <summary>Queues a WhatsApp message about the job when the customer agreed to updates (its consent goes with it). Passcodes and
    /// civil IDs are never in a message: the server fills the template from these fields only.</summary>
    public static async Task<Guid?> NotifyAsync(DbTx t, RepairJob j, string template, IReadOnlyList<string> args, string sourceType, Guid sourceId, Guid? actor)
    {
        if (!j.NotifyWhatsapp || !j.CustomerOptIn || j.CustomerPhone is null || j.CustomerOptInAt is null) return null;
        var id = t.NewId();
        await t.ExecuteAsync("""
            INSERT INTO notif.outbound_message (id, business_id, branch_id, customer_id, phone_e164, lang, template, params, source_doc_type, source_doc_id,
                                                opt_in_at, expires_at, created_by, origin_server_id)
            VALUES (@id, @b, @BranchId, @CustomerId, @CustomerPhone, @Lang, @template, @args::jsonb, @sourceType, @sourceId, @CustomerOptInAt,
                    now() + interval '24 hours', @actor, (SELECT id FROM core.server WHERE is_self))
            """, new { id, b = j.BusinessId, j.BranchId, j.CustomerId, j.CustomerPhone, j.Lang, template, args = JsonSerializer.Serialize(args),
                       sourceType, sourceId, j.CustomerOptInAt, actor });
        return id;
    }

    public static string Money(decimal amount, string lang) =>
        lang == "ar" ? $"{amount.ToString("0.000", CultureInfo.InvariantCulture)} ر.ع." : $"OMR {amount.ToString("0.000", CultureInfo.InvariantCulture)}";

    public static string FirstName(RepairJob j) =>
        (j.Lang == "ar" && !string.IsNullOrWhiteSpace(j.CustomerNameAr) ? j.CustomerNameAr! : j.CustomerName).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

    /// <summary>The message a status queues when the business switched it on (13.2): received, waiting for approval, ready for collection.</summary>
    public static async Task NotifyStatusAsync(DbTx t, RepairJob j, RepairSettings settings, BusinessInfo biz, Guid? actor)
    {
        if (!settings.NotifyStatuses.Contains(j.Status)) return;
        switch (j.Status)
        {
            case "received":
                await NotifyAsync(t, j, "repair_received",
                    [FirstName(j), j.Number, j.DeviceName, BusinessDay.Local(j.PromisedAt, biz.TimeZone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)],
                    "repair_job", j.Id, actor);
                break;
            case "waiting_approval":
                var e = await CurrentEstimateAsync(t, j.Id);
                if (e is not null && e.Status is "sent")
                    await NotifyAsync(t, j, "repair_estimate", [FirstName(j), j.Number, Money(e.Total, j.Lang), e.EstimateNo.ToString(CultureInfo.InvariantCulture)],
                        "repair_estimate", e.Id, actor);
                break;
            case "ready":
                var due = await AmountDueAsync(t, j, settings);
                await NotifyAsync(t, j, "repair_ready", [FirstName(j), j.Number, Money(due, j.Lang)], "repair_job", j.Id, actor);
                break;
        }
    }

    /// <summary>What the customer still pays at collection: the charges less the deposit (never below zero).</summary>
    public static async Task<decimal> AmountDueAsync(DbTx t, RepairJob j, RepairSettings settings)
    {
        var charges = await ChargesAsync(t, j, settings.DiagnosisFee);
        return Math.Max(0m, charges.Sum(c => c.GrossAmount) - await DepositBalanceAsync(t, j.Id));
    }

    /// <summary>
    /// What the repair invoice charges (13.5): the approved estimate when the job was repaired (nothing on a warranty claim); the
    /// diagnosis fee when it was cancelled or could not be repaired.
    /// </summary>
    public static async Task<IReadOnlyList<RepairEstimateLine>> ChargesAsync(DbTx t, RepairJob j, decimal diagnosisFee)
    {
        if (j.IsWarrantyClaim) return [];
        if (j.Outcome is "cancelled" or "unrepairable")
        {
            if (diagnosisFee <= 0) return [];
            var biz = await BusinessInfo.LoadAsync(t);
            var variant = await t.ExecuteScalarAsync<Guid?>("SELECT id FROM catalog.variant WHERE upper(sku) = 'REPAIR-DIAG' AND business_id = @b", new { b = biz.Id })
                ?? throw new KiteException(ErrorCodes.Internal, "The diagnosis fee service (REPAIR-DIAG) is missing from the catalog.", 500);
            var settings = await SettingsAsync(t);
            return await PriceLinesAsync(t, biz, j.BranchId, settings, [new EstimateLineInput("extra", variant, 1m, diagnosisFee)]);
        }
        var e = await CurrentEstimateAsync(t, j.Id);
        if (e is null || e.Status != "approved") return [];
        return await EstimateLinesAsync(t, e.Id);
    }

    // ------------------------------------------------------------------ setup
    public static readonly (string Code, string En, string Ar, int Hours, int? Warranty)[] Categories =
    [
        ("SCREEN", "Screen and display", "الشاشة والعرض", 24, null),
        ("BATTERY", "Battery", "البطارية", 24, null),
        ("CHARGING", "Charging port and power", "منفذ الشحن والطاقة", 48, null),
        ("CAMERA", "Cameras", "الكاميرات", 48, null),
        ("AUDIO", "Speaker, earpiece and microphone", "السماعة والميكروفون", 48, null),
        ("WATER", "Liquid damage", "التلف بالسوائل", 72, 30),
        ("BOARD", "Board and no power", "اللوحة الأم وعدم التشغيل", 120, 30),
        ("SOFTWARE", "Software and data", "البرمجيات والبيانات", 24, 7),
        ("OTHER", "Other", "أخرى", 48, null),
    ];

    public static readonly (string Code, string? Category, string En, string Ar)[] FaultCodes =
    [
        ("SCR_CRACKED", "SCREEN", "Cracked glass", "زجاج مكسور"),
        ("SCR_NO_DISPLAY", "SCREEN", "No display or lines", "لا توجد صورة أو خطوط"),
        ("SCR_TOUCH", "SCREEN", "Touch not working", "اللمس لا يعمل"),
        ("BAT_DRAINS", "BATTERY", "Drains fast", "تفرغ بسرعة"),
        ("BAT_SWOLLEN", "BATTERY", "Swollen battery", "بطارية منتفخة"),
        ("CHG_NOT_CHARGING", "CHARGING", "Does not charge", "لا يشحن"),
        ("CHG_LOOSE", "CHARGING", "Loose cable connection", "توصيل الكابل غير ثابت"),
        ("CAM_BLURRY", "CAMERA", "Blurry or black camera", "كاميرا ضبابية أو سوداء"),
        ("AUD_NO_SOUND", "AUDIO", "No sound", "لا يوجد صوت"),
        ("AUD_MIC", "AUDIO", "Others cannot hear me", "الطرف الآخر لا يسمعني"),
        ("WAT_DROPPED", "WATER", "Dropped in water", "سقط في الماء"),
        ("BRD_NO_POWER", "BOARD", "Does not turn on", "لا يعمل"),
        ("BRD_RESTARTS", "BOARD", "Keeps restarting", "يعيد التشغيل باستمرار"),
        ("SW_SLOW", "SOFTWARE", "Slow or freezing", "بطيء أو يتجمد"),
        ("SW_DATA", "SOFTWARE", "Data transfer or backup", "نقل البيانات أو النسخ الاحتياطي"),
        ("GEN_DROPPED", null, "Dropped", "سقط"),
    ];

    public const string TermsEn =
        "1. The shop is not responsible for data on the device. Back up your data; repairs and tests can erase it.\n" +
        "2. Devices with liquid damage or earlier repairs may develop other faults during repair; the shop is not liable for them.\n" +
        "3. Work above the amount you pre-authorised is done only after you approve the estimate.\n" +
        "4. Repairs carry the warranty stated on the invoice, for the parts and work done only.\n" +
        "5. Collect the device with this job card. Devices not collected within 90 days of the ready notice may be disposed of as allowed by law, after reminders.";

    public const string TermsAr =
        "١. المحل غير مسؤول عن البيانات الموجودة على الجهاز. احتفظ بنسخة احتياطية؛ فقد تمحو الصيانة والاختبارات البيانات.\n" +
        "٢. الأجهزة المتعرضة للسوائل أو التي سبق إصلاحها قد تظهر بها أعطال أخرى أثناء الصيانة، ولا يتحمل المحل مسؤوليتها.\n" +
        "٣. لا يُنفَّذ أي عمل يتجاوز المبلغ الذي وافقت عليه مسبقًا إلا بعد موافقتك على التقدير.\n" +
        "٤. تشمل الصيانة الضمان المذكور في الفاتورة، للقطع والعمل المنجز فقط.\n" +
        "٥. استلم جهازك بهذه البطاقة. الأجهزة التي لا تُستلم خلال 90 يومًا من إشعار الجاهزية يجوز التصرف فيها وفق القانون بعد التذكير.";

    /// <summary>The repair services every business needs in its catalog: labour, an extra charge and the diagnosis fee.</summary>
    public static readonly (string Sku, string En, string Ar)[] Services =
    [
        ("REPAIR-LABOUR", "Repair labour", "أجور الصيانة"),
        ("REPAIR-EXTRA", "Additional repair charge", "رسوم صيانة إضافية"),
        ("REPAIR-DIAG", "Diagnosis fee", "رسوم الفحص"),
    ];
}

/// <summary>Repair and WhatsApp defaults for a new business (13, 5.3): settings, categories, fault codes, repair services, templates.</summary>
public static class Phase8Seed
{
    public static readonly (string Code, string Lang, string Name, string Body, string[] Params, string[] Buttons)[] Templates =
    [
        ("repair_received", "en", "kme_repair_received_en",
            "Hello {{1}}, we received your {{3}} for repair. Job number {{2}}. We expect it to be ready by {{4}}. Reply STOP to stop these messages.",
            ["name", "job", "device", "promised"], []),
        ("repair_received", "ar", "kme_repair_received_ar",
            "مرحبًا {{1}}، استلمنا جهازك {{3}} للصيانة. رقم الطلب {{2}}. نتوقع أن يكون جاهزًا بحلول {{4}}. أرسل STOP لإيقاف هذه الرسائل.",
            ["name", "job", "device", "promised"], []),
        ("repair_estimate", "en", "kme_repair_estimate_en",
            "Hello {{1}}, the repair estimate for job {{2}} (version {{4}}) is {{3}}. Tap Approve to go ahead, or Call me to talk to us first.",
            ["name", "job", "amount", "version"], ["Approve", "Call me"]),
        ("repair_estimate", "ar", "kme_repair_estimate_ar",
            "مرحبًا {{1}}، تقدير تكلفة الصيانة للطلب {{2}} (النسخة {{4}}) هو {{3}}. اضغط موافق للمتابعة، أو اتصلوا بي للتحدث معنا أولًا.",
            ["name", "job", "amount", "version"], ["موافق", "اتصلوا بي"]),
        ("repair_ready", "en", "kme_repair_ready_en",
            "Hello {{1}}, your device (job {{2}}) is ready for collection. Amount due: {{3}}. Please bring your job card.",
            ["name", "job", "due"], []),
        ("repair_ready", "ar", "kme_repair_ready_ar",
            "مرحبًا {{1}}، جهازك (الطلب {{2}}) جاهز للاستلام. المبلغ المستحق: {{3}}. يرجى إحضار بطاقة الطلب.",
            ["name", "job", "due"], []),
        ("repair_uncollected", "en", "kme_repair_uncollected_en",
            "Hello {{1}}, your device (job {{2}}) has been ready for {{3}} days. Please collect it soon.",
            ["name", "job", "days"], []),
        ("repair_uncollected", "ar", "kme_repair_uncollected_ar",
            "مرحبًا {{1}}، جهازك (الطلب {{2}}) جاهز منذ {{3}} يومًا. يرجى استلامه قريبًا.",
            ["name", "job", "days"], []),
    ];

    public static async Task EnsureAsync(DbTx t, Guid businessId)
    {
        await t.ExecuteAsync("INSERT INTO comm.settings (business_id) VALUES (@businessId) ON CONFLICT DO NOTHING", new { businessId });
        await t.ExecuteAsync("""
            INSERT INTO repair.settings (business_id, terms_en, terms_ar) VALUES (@businessId, @en, @ar) ON CONFLICT DO NOTHING
            """, new { businessId, en = RepairCore.TermsEn, ar = RepairCore.TermsAr });
        if (!await t.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM repair.category WHERE business_id = @businessId)", new { businessId }))
        {
            short sort = 0;
            var ids = new Dictionary<string, Guid>();
            foreach (var c in RepairCore.Categories)
            {
                var id = t.NewId();
                ids[c.Code] = id;
                await t.ExecuteAsync("""
                    INSERT INTO repair.category (id, business_id, code, name_en, name_ar, turnaround_hours, warranty_days, sort)
                    VALUES (@id, @businessId, @Code, @En, @Ar, @Hours, @Warranty, @sort)
                    """, new { id, businessId, c.Code, c.En, c.Ar, c.Hours, c.Warranty, sort = sort++ });
            }
            sort = 0;
            foreach (var f in RepairCore.FaultCodes)
                await t.ExecuteAsync("""
                    INSERT INTO repair.fault_code (id, business_id, category_id, code, name_en, name_ar, sort) VALUES (@id, @businessId, @cat, @Code, @En, @Ar, @sort)
                    """, new { id = t.NewId(), businessId, cat = f.Category is null ? (Guid?)null : ids[f.Category], f.Code, f.En, f.Ar, sort = sort++ });
        }
        var vat = await t.ExecuteScalarAsync<Guid?>("SELECT id FROM tax.tax_code WHERE business_id = @businessId AND code = 'VAT-STD'", new { businessId });
        if (vat is { } taxCode)
            foreach (var s in RepairCore.Services)
            {
                if (await t.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM catalog.variant WHERE business_id = @businessId AND upper(sku) = @Sku)", new { businessId, s.Sku }))
                    continue;
                var pid = t.NewId();
                await t.ExecuteAsync("""
                    INSERT INTO catalog.product (id, business_id, name_en, name_ar, product_type, tracking, tax_code_id) VALUES (@pid, @businessId, @En, @Ar, 'service', 'none', @taxCode)
                    """, new { pid, businessId, s.En, s.Ar, taxCode });
                await t.ExecuteAsync("INSERT INTO catalog.variant (id, business_id, product_id, sku) VALUES (@id, @businessId, @pid, @Sku)",
                    new { id = t.NewId(), businessId, pid, s.Sku });
            }
        await t.ExecuteAsync("INSERT INTO notif.settings (business_id) VALUES (@businessId) ON CONFLICT DO NOTHING", new { businessId });
        foreach (var tp in Templates)
            await t.ExecuteAsync("""
                INSERT INTO notif.template (id, business_id, code, lang, meta_name, meta_lang, body, params, buttons)
                VALUES (@id, @businessId, @Code, @Lang, @Name, @Lang, @Body, @Params, @Buttons) ON CONFLICT DO NOTHING
                """, new { id = t.NewId(), businessId, tp.Code, tp.Lang, tp.Name, tp.Body, tp.Params, tp.Buttons });
    }
}
