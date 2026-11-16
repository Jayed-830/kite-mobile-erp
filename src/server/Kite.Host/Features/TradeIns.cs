using System.Globalization;
using System.Text;
using System.Text.Json;
using Kite.Accounting;
using Kite.Devices;
using Kite.Host.Infrastructure;
using Kite.Identity;
using Kite.Persistence;
using Kite.Platform;
using Kite.Sales;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Kite.Host.Features;

public sealed record TradeInDeviceInput(Guid VariantId, string? Imei1 = null, string? Imei2 = null, string? Serial = null, string? Entry = null,
    string? Storage = null, string? Colour = null);
public sealed record TradeInCreateRequest(Guid CustomerId, TradeInDeviceInput Device, string? CivilId = null, string? SellerVatNumber = null,
    Guid? SaleId = null, string? Note = null);
public sealed record TradeInUpdateRequest(int Version, TradeInDeviceInput? Device = null, Guid? GradeId = null, string? Condition = null,
    Dictionary<string, string?>? Inspection = null, int? BatteryHealth = null, bool? CloudLockRemoved = null, List<string>? Deductions = null,
    decimal? AgreedValue = null, string? CivilId = null, string? SellerVatNumber = null, string? Note = null, Guid? SaleId = null);
public sealed record TradeInConsentInput(string Method, string? Name = null, string? Reference = null);
public sealed record TradeInSettleRequest(string Kind, decimal? Amount = null, Guid? ApprovalId = null);
public sealed record TradeInAcceptRequest(int Version, decimal AgreedValue, TradeInConsentInput Consent, Guid? ApprovalId = null,
    TradeInSettleRequest? Settle = null, Guid? SaleId = null);
public sealed record TradeInCloseRequest(string? Reason = null);
public sealed record TradeInSettingsRequest(decimal StaffLimit, decimal OverGuidePercent, bool RequireCivilId, string DeclarationEn, string DeclarationAr, int Version);
public sealed record PriceGuideRowInput(Guid VariantId, Guid GradeId, decimal? BaseValue);
public sealed record PriceGuideRequest(List<PriceGuideRowInput> Rows);
public sealed record DeductionRequest(string Code, string NameEn, string NameAr, string Kind, decimal Value, string? ChecklistCode = null, short? Sort = null,
    string? Status = null, int? Version = null);
public sealed record MarginSchemeRequest(bool Approved, string? Reference, DateOnly? ApprovedOn, string? Note, int Version);

/// <summary>
/// Trade-ins (blueprint 12.4, 12.5): the wizard's steps at the till (device, inspection, valuation, approval, consent), accepting
/// the device into stock, settling it as a sale's payment, store credit or cash, the price guide and deductions, the trade-in
/// documents, the margin scheme approval (12.3), and customers' store credit (14.6).
/// </summary>
public static class TradeInEndpoints
{
    public static void Map(WebApplication app)
    {
        MapTradeIns(app);
        MapSetup(app);
        MapCustomersAndTax(app);
    }

    private static async Task<object> DetailAsync(DbTx t, HttpContext http, Guid id)
    {
        var ti = await TradeInCore.LoadAsync(t, id);
        if (!http.User.HasIn(Permissions.TradeInCreate, ti.BranchId)) http.User.Demand(Permissions.TradeInView, ti.BranchId);
        var settings = await TradeInCore.SettingsAsync(t);
        var issues = await t.RowsAsync("""
            SELECT code, name_en AS "nameEn", name_ar AS "nameAr", amount FROM tradein.tradein_issue WHERE tradein_id = @id ORDER BY code
            """, new { id });
        var settlements = await t.RowsAsync("""
            SELECT st.id, st.kind, st.amount, s.number AS "saleNumber", st.created_at AS "createdAt", u.display_name_en AS "createdBy"
              FROM tradein.settlement st LEFT JOIN sales.sale s ON s.id = st.sale_id JOIN iam."user" u ON u.id = st.created_by
             WHERE st.tradein_id = @id ORDER BY st.created_at, st.id
            """, new { id });
        var outstanding = ti.Status is "accepted" or "settled" ? await TradeInCore.OutstandingAsync(t, id) : (decimal?)null;
        var canApprove = http.User.HasIn(Permissions.TradeInApprove, ti.BranchId);
        bool? needsApproval = ti.AgreedValue is { } agreed && ti.Status == "open"
            ? !canApprove && TradeInMath.NeedsApproval(agreed, ti.OfferedValue, settings.StaffLimit, settings.OverGuidePercent) : null;
        object? form = null;
        if (ti.Status == "open")
        {
            var (checklistId, items) = await DeviceEndpoints.ChecklistForAsync(t, ti.CategoryId);
            var grades = await t.RowsAsync("""
                SELECT g.id, g.code, g.name_en AS "nameEn", g.name_ar AS "nameAr", g.price_percent AS "pricePercent",
                       (SELECT pg.base_value FROM tradein.price_guide pg WHERE pg.variant_id = @v AND pg.grade_id = g.id AND pg.status = 'active') AS "guideValue"
                  FROM device.grade g WHERE g.status = 'active' AND (g.category_id IS NULL OR g.category_id = @c) ORDER BY g.sort, g.code
                """, new { v = ti.VariantId, c = ti.CategoryId });
            var deductions = await t.RowsAsync("""
                SELECT id, code, name_en AS "nameEn", name_ar AS "nameAr", kind, value, checklist_code AS "checklistCode" FROM tradein.deduction
                 WHERE status = 'active' ORDER BY sort, code
                """);
            form = new { checklistId, items, grades, deductions, settings.StaffLimit, settings.OverGuidePercent, settings.RequireCivilId,
                         settings.DeclarationEn, settings.DeclarationAr, canApprove };
        }
        return new
        {
            tradeIn = new
            {
                ti.Id, ti.Number, ti.Status, ti.BranchId, ti.BranchCode, ti.CustomerId, ti.CustomerName, ti.CustomerNameAr, ti.CustomerPhone,
                civilId = ti.CivilIdMasked, ti.SellerVatNumber, ti.VariantId, ti.Sku, ti.DescriptionEn, ti.DescriptionAr, ti.Imei1, ti.Imei2, ti.Serial,
                ti.Entry, ti.Storage, ti.Colour, ti.GradeId, ti.GradeCode, ti.GradeNameEn, ti.GradeNameAr, ti.Condition,
                inspection = JsonDocument.Parse(ti.Inspection).RootElement, ti.BatteryHealth, ti.CloudLockRemoved, ti.GuideValue, ti.DeductionsTotal,
                ti.OfferedValue, ti.AgreedValue, ti.ApprovalId, ti.ConsentMethod, ti.ConsentName, ti.ConsentReference, ti.ConsentAt, ti.SaleId,
                ti.DeviceUnitId, ti.Reconnected, ti.MarginScheme, ti.SelfInvoiceNumber, ti.Note, ti.RejectReason, ti.AcceptedAt, ti.AcceptedByName,
                ti.SettledAt, ti.ClosedAt, ti.CreatedAt, ti.CreatedByName, ti.Version, outstanding, needsApproval,
            },
            issues, settlements, form,
        };
    }

    // ------------------------------------------------------------------ the trade-in at the till
    private static void MapTradeIns(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/tradeins");

        g.MapGet("", async (string? status, string? q, Guid? customerId, Guid? branchId, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.RowsAsync($"""
                SELECT t.id, t.number, t.status, b.code AS "branchCode", c.name_en AS "customerName", c.phone_e164 AS "customerPhone",
                       {ItemPricing.DescriptionEn} AS "descriptionEn", {ItemPricing.DescriptionAr} AS "descriptionAr", coalesce(t.imei1, t.serial) AS identifier,
                       t.offered_value AS "offeredValue", t.agreed_value AS "agreedValue", tradein.outstanding(t.id) AS outstanding,
                       t.margin_scheme AS "marginScheme", t.reconnected, t.created_at AS "createdAt", t.accepted_at AS "acceptedAt", u.display_name_en AS "createdBy"
                  FROM tradein.tradein t JOIN core.branch b ON b.id = t.branch_id JOIN crm.customer c ON c.id = t.customer_id
                  JOIN catalog.variant v ON v.id = t.variant_id JOIN catalog.product p ON p.id = v.product_id JOIN iam."user" u ON u.id = t.created_by
                 WHERE (@status::text IS NULL OR (@status = 'unsettled' AND t.status = 'accepted') OR t.status = @status)
                   AND (@customerId::uuid IS NULL OR t.customer_id = @customerId) AND (@branchId::uuid IS NULL OR t.branch_id = @branchId)
                   AND (@q::text IS NULL OR t.number ILIKE '%' || @q || '%' OR c.name_en ILIKE '%' || @q || '%' OR t.imei1 = @q OR t.serial = upper(@q)
                        OR c.phone_e164 LIKE '%' || @q || '%')
                 ORDER BY t.created_at DESC LIMIT 300
                """, new { status = string.IsNullOrWhiteSpace(status) ? null : status, q = string.IsNullOrWhiteSpace(q) ? null : q.Trim(), customerId, branchId }), ct)))
            .RequirePermission(Permissions.TradeInView).RequireModule("tradein");

        g.MapGet("/{id:guid}", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => DetailAsync(t, http, id), ct)))
            .RequireSignedIn().RequireModule("tradein");

        // Step 1-2: the customer and the device. The IMEI is checked against the blocked list and the business's devices (and
        // head office's register) before anything is valued.
        g.MapPost("", async (TradeInCreateRequest req, HttpContext http, KiteDb db, LicenceService licence, CentralClient central, ISecretProtector secrets, CancellationToken ct) =>
        {
            await licence.RequireTransactAsync(ct);
            if (req.Device is null) throw new KiteException(ErrorCodes.Validation, "Describe the device.", 400, "device");
            var probe = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var item = await ItemAsync(t, req.Device.VariantId);
                var (i1, i2, sn) = DeviceUnits.CheckIdentifiers(item.Tracking, new UnitInput(req.Device.Imei1, req.Device.Imei2, req.Device.Serial), "device");
                return TradeInCore.IdsOf(i1, i2, sn, item.BrandName, item.ProductId);
            }, ct);
            var answer = await central.CheckAsync(probe, null, ct);
            if (answer is not null) TradeInCore.RefuseCentral(answer);
            var id = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var term = await PosTerminals.CurrentAsync(t, http);
                http.User.Demand(Permissions.TradeInCreate, term.BranchId);
                var item = await ItemAsync(t, req.Device.VariantId);
                var (imei1, imei2, serial) = DeviceUnits.CheckIdentifiers(item.Tracking, new UnitInput(req.Device.Imei1, req.Device.Imei2, req.Device.Serial), "device");
                var entry = req.Device.Entry ?? "scan";
                if (entry is not ("scan" or "manual")) throw new KiteException(ErrorCodes.Validation, "Entry is scan or manual.", 400, "device.entry");
                if (!await t.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM crm.customer WHERE id = @CustomerId AND status = 'active')", new { req.CustomerId }))
                    throw new KiteException(ErrorCodes.CustomerRequired, "Choose the customer selling the device (name and phone).", 400, "customerId");
                if (!await t.ExecuteScalarAsync<bool>("SELECT phone_e164 IS NOT NULL FROM crm.customer WHERE id = @CustomerId", new { req.CustomerId }))
                    throw new KiteException(ErrorCodes.CustomerRequired, "A trade-in needs the customer's phone number. Add it to the customer.", 400, "customerId");
                var ids = TradeInCore.IdsOf(imei1, imei2, serial, item.BrandName, item.ProductId);
                await CentralRegistry.LockAsync(t, ids.Select(CentralRegistry.Key.Of));
                var identity = await TradeInCore.CheckIdentityAsync(t, ids, item.BrandKey);
                var variant = identity.Reconnect?.VariantId ?? item.VariantId;   // a device the business sold keeps its model
                var biz = await BusinessInfo.LoadAsync(t);
                var today = BusinessDay.Today(biz.TimeZone, t.Clock);
                var number = await DocNumbers.NextAsync(t, biz.Id, term.BranchId, "trade_in", await FiscalPeriods.FiscalYearOfAsync(t, biz.Id, today), "TI");
                var id = t.NewId();
                var civil = string.IsNullOrWhiteSpace(req.CivilId) ? null : TradeInCore.NormCivilId(req.CivilId);
                await t.ExecuteAsync("""
                    INSERT INTO tradein.tradein (id, business_id, branch_id, number, customer_id, civil_id_enc, civil_id_last4, seller_vat_number, variant_id,
                                                 imei1, imei2, serial, entry, storage, colour, sale_id, terminal_id, note, created_by, origin_server_id)
                    VALUES (@id, @b, @branch, @number, @CustomerId, @enc, @last4, @vat, @variant, @imei1, @imei2, @serial, @entry, @storage, @colour, @SaleId,
                            @terminal, @note, @uid, (SELECT id FROM core.server WHERE is_self))
                    """, new { id, b = biz.Id, branch = term.BranchId, number, req.CustomerId, enc = civil is null ? null : TradeInCore.Protect(secrets, civil),
                               last4 = civil is null ? null : TradeInMath.Last4(civil), vat = Clean(req.SellerVatNumber), variant, imei1, imei2, serial, entry,
                               storage = Clean(req.Device.Storage), colour = Clean(req.Device.Colour), req.SaleId, terminal = term.Id, note = Clean(req.Note),
                               uid = http.User.UserIdOf() });
                await t.AuditAsync(new AuditEntry("tradein.created", "tradein", id, After: Json.Obj(new { number, imei1, serial, entry, reconnect = identity.Reconnect?.Id,
                    headOffice = answer is null && central.IsBranch ? "unreachable" : "checked" }), BranchId: term.BranchId));
                return id;
            }, ct);
            var detail = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => DetailAsync(t, http, id), ct);
            return Results.Created($"/api/v1/tradeins/{id}", new { detail, headOfficeChecked = !central.IsBranch || answer is not null });
        }).RequirePermission(Permissions.TradeInCreate).RequireModule("tradein");

        // Steps 2-4 while open: device, inspection, grade, deductions and the value agreed; the offer is worked out from the guide.
        g.MapPut("/{id:guid}", async (Guid id, TradeInUpdateRequest req, HttpContext http, KiteDb db, ISecretProtector secrets, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var ti = await TradeInCore.LoadAsync(t, id, forUpdate: true);
                http.User.Demand(Permissions.TradeInCreate, ti.BranchId);
                if (ti.Status != "open") throw new KiteException(ErrorCodes.TradeInState, $"Trade-in {ti.Number} is {ti.Status}; it can no longer change.", 409);
                if (ti.Version != req.Version) throw new KiteException(ErrorCodes.VersionMismatch, "Someone else changed this trade-in. Reload it.", 412);
                var variant = ti.VariantId;
                string? imei1 = ti.Imei1, imei2 = ti.Imei2, serial = ti.Serial;
                var entry = ti.Entry;
                if (req.Device is { } d)
                {
                    var item = await ItemAsync(t, d.VariantId);
                    (imei1, imei2, serial) = DeviceUnits.CheckIdentifiers(item.Tracking, new UnitInput(d.Imei1, d.Imei2, d.Serial), "device");
                    entry = d.Entry ?? entry;
                    if (entry is not ("scan" or "manual")) throw new KiteException(ErrorCodes.Validation, "Entry is scan or manual.", 400, "device.entry");
                    var ids = TradeInCore.IdsOf(imei1, imei2, serial, item.BrandName, item.ProductId);
                    await CentralRegistry.LockAsync(t, ids.Select(CentralRegistry.Key.Of));
                    var identity = await TradeInCore.CheckIdentityAsync(t, ids, item.BrandKey);
                    variant = identity.Reconnect?.VariantId ?? item.VariantId;
                }
                var condition = req.Condition ?? ti.Condition;
                if (condition is not ("used" or "damaged")) throw new KiteException(ErrorCodes.Validation, "A traded-in device is used or damaged.", 400, "condition");
                var gradeId = req.GradeId ?? ti.GradeId;
                if (variant != ti.VariantId && req.GradeId is null) gradeId = null;
                if (gradeId is { } gid) await DeviceUnits.CheckGradeAsync(t, gid, (await ItemAsync(t, variant)).CategoryId, "gradeId");
                if (req.BatteryHealth is < 0 or > 100) throw new KiteException(ErrorCodes.Validation, "Battery health is 0 to 100%.", 400, "batteryHealth");
                var guide = await TradeInCore.GuideAsync(t, variant, gradeId);

                // Deductions: the codes chosen now, else the ones already applied.
                var codes = req.Deductions ?? (await t.QueryAsync<string>("SELECT code FROM tradein.tradein_issue WHERE tradein_id = @id", new { id })).ToList();
                var defs = (await t.QueryAsync<(Guid Id, string Code, string NameEn, string NameAr, string Kind, decimal Value)>("""
                    SELECT id, code, name_en, name_ar, kind, value FROM tradein.deduction WHERE status = 'active' AND code = ANY(@codes)
                    """, new { codes = codes.Distinct().ToArray() })).ToDictionary(x => x.Code);
                var unknown = codes.Where(c => !defs.ContainsKey(c)).ToArray();
                if (unknown.Length > 0) throw new KiteException(ErrorCodes.Validation, $"Unknown deduction {string.Join(", ", unknown)}.", 400, "deductions");
                var chosen = codes.Distinct().Select(c => defs[c]).OrderBy(x => x.Code, StringComparer.Ordinal).ToList();
                var valuation = TradeInMath.Value(guide, chosen.Select(x => new TradeInDeduction(x.Code, x.Kind, x.Value)).ToList());
                await t.ExecuteAsync("DELETE FROM tradein.tradein_issue WHERE tradein_id = @id", new { id });
                for (var i = 0; i < chosen.Count; i++)
                    await t.ExecuteAsync("""
                        INSERT INTO tradein.tradein_issue (id, business_id, branch_id, tradein_id, deduction_id, code, name_en, name_ar, amount)
                        VALUES (@rid, @b, @branch, @id, @did, @code, @en, @ar, @amount)
                        """, new { rid = t.NewId(), b = ti.BusinessId, branch = ti.BranchId, id, did = chosen[i].Id, code = chosen[i].Code, en = chosen[i].NameEn,
                                   ar = chosen[i].NameAr, amount = valuation.Amounts[i] });

                var agreed = req.AgreedValue ?? ti.AgreedValue;
                if (agreed is < 0 || (agreed is { } a && a != Currency.OMR.Round(a))) throw new KiteException(ErrorCodes.Validation, "The agreed value is zero or more, in baisa.", 400, "agreedValue");
                var civil = req.CivilId is null ? null : string.IsNullOrWhiteSpace(req.CivilId) ? "" : TradeInCore.NormCivilId(req.CivilId);
                var inspection = req.Inspection is null ? ti.Inspection : JsonSerializer.Serialize(req.Inspection, Json.Web);
                await t.ExecuteAsync("""
                    UPDATE tradein.tradein
                       SET variant_id = @variant, imei1 = @imei1, imei2 = @imei2, serial = @serial, entry = @entry,
                           storage = coalesce(@storage, storage), colour = coalesce(@colour, colour), grade_id = @gradeId, condition = @condition,
                           inspection = @inspection::jsonb, battery_health = coalesce(@battery, battery_health),
                           cloud_lock_removed = coalesce(@cloud, cloud_lock_removed), guide_value = @guide, deductions_total = @total, offered_value = @offered,
                           agreed_value = @agreed, approval_id = CASE WHEN @agreed IS DISTINCT FROM agreed_value THEN NULL ELSE approval_id END,
                           civil_id_enc = CASE WHEN @civilSet THEN @enc ELSE civil_id_enc END, civil_id_last4 = CASE WHEN @civilSet THEN @last4 ELSE civil_id_last4 END,
                           seller_vat_number = CASE WHEN @vatSet THEN @vat ELSE seller_vat_number END, note = coalesce(@note, note),
                           sale_id = coalesce(@saleId, sale_id), updated_by = @uid
                     WHERE id = @id
                    """, new { id, variant, imei1, imei2, serial, entry, storage = Clean(req.Device?.Storage), colour = Clean(req.Device?.Colour), gradeId, condition,
                               inspection, battery = (short?)req.BatteryHealth, cloud = req.CloudLockRemoved, guide, total = valuation.DeductionsTotal,
                               offered = valuation.Offered, agreed, civilSet = civil is not null, enc = string.IsNullOrEmpty(civil) ? null : TradeInCore.Protect(secrets, civil),
                               last4 = string.IsNullOrEmpty(civil) ? null : TradeInMath.Last4(civil), vatSet = req.SellerVatNumber is not null,
                               vat = Clean(req.SellerVatNumber), note = Clean(req.Note), saleId = req.SaleId, uid = http.User.UserIdOf() });
                return await DetailAsync(t, http, id);
            }, ct))).RequirePermission(Permissions.TradeInCreate).RequireModule("tradein");

        // Steps 5-7: approval, consent, acceptance and (for store credit or cash) settlement, in one transaction.
        g.MapPost("/{id:guid}/accept", async (Guid id, TradeInAcceptRequest req, HttpContext http, KiteDb db, LicenceService licence, CentralClient central, CancellationToken ct) =>
        {
            await licence.RequireTransactAsync(ct);
            var probe = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var ti = await TradeInCore.LoadAsync(t, id);
                return TradeInCore.IdsOf(ti.Imei1, ti.Imei2, ti.Serial, ti.BrandName, (await ItemAsync(t, ti.VariantId)).ProductId);
            }, ct);
            var answer = await central.CheckAsync(probe, null, ct);
            if (answer is not null) TradeInCore.RefuseCentral(answer);
            var unitId = await PosEndpoints.WithRetryAsync(() => db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var term = await PosTerminals.CurrentAsync(t, http);
                // Lock order (see TradeInCore.LockForPaymentAsync): the drawer, the trade-in, the customer's store credit, then numbers and journals.
                if (req.Settle?.Kind == "cash") await PosEndpoints.OpenShiftForUpdateAsync(t, term, http);
                var ti = await TradeInCore.LoadAsync(t, id, forUpdate: true);
                if (req.Settle?.Kind == "store_credit") await TradeInCore.LockCreditAsync(t, ti.CustomerId);
                http.User.Demand(Permissions.TradeInCreate, ti.BranchId);
                if (term.BranchId != ti.BranchId) throw new KiteException(ErrorCodes.TradeInState, $"Trade-in {ti.Number} is accepted at {ti.BranchCode}.", 409);
                if (ti.Status != "open") throw new KiteException(ErrorCodes.TradeInState, $"Trade-in {ti.Number} is {ti.Status}.", 409);
                if (ti.Version != req.Version) throw new KiteException(ErrorCodes.VersionMismatch, "Someone else changed this trade-in. Reload it.", 412);
                if (!ti.CloudLockRemoved)
                    throw new KiteException(ErrorCodes.TradeInDevice, "Confirm that the customer removed their cloud account and activation lock in front of you.", 409, "cloudLockRemoved");
                var settings = await TradeInCore.SettingsAsync(t);
                if (settings.RequireCivilId && ti.CivilIdEnc is null)
                    throw new KiteException(ErrorCodes.TradeInCivilId, "Record the customer's civil ID: the business is buying a device from them.", 400, "civilId");
                var agreed = req.AgreedValue;
                if (agreed < 0 || agreed != Currency.OMR.Round(agreed)) throw new KiteException(ErrorCodes.Validation, "The agreed value is zero or more, in baisa.", 400, "agreedValue");
                var uid = http.User.UserIdOf();
                Guid? approvalId = null;
                if (!http.User.HasIn(Permissions.TradeInApprove, ti.BranchId) && TradeInMath.NeedsApproval(agreed, ti.OfferedValue, settings.StaffLimit, settings.OverGuidePercent))
                {
                    approvalId = req.ApprovalId ?? throw new KiteException(ErrorCodes.TradeInApprovalNeeded, ti.OfferedValue is null
                        ? "There is no guide value for this model and grade, so a manager approves the value."
                        : $"{agreed:0.000} is above your limit or the guide offer of {ti.OfferedValue:0.000}: a manager approves it.", 403, "approvalId");
                    await Approvals.UseAsync(t, approvalId.Value, uid, ti.BranchId, Permissions.TradeInApprove, "tradein", id);
                }
                var consent = req.Consent ?? throw new KiteException(ErrorCodes.TradeInConsent, "The customer confirms the sale and the declaration.", 400, "consent");
                if (consent.Method == "on_screen" && string.IsNullOrWhiteSpace(consent.Name))
                    throw new KiteException(ErrorCodes.TradeInConsent, "The customer types their name to confirm on screen.", 400, "consent.name");
                if (consent.Method == "paper" && string.IsNullOrWhiteSpace(consent.Reference))
                    throw new KiteException(ErrorCodes.TradeInConsent, "Give the reference of the signed paper form.", 400, "consent.reference");
                if (consent.Method is not ("on_screen" or "paper")) throw new KiteException(ErrorCodes.TradeInConsent, "Consent is on screen or on paper.", 400, "consent.method");
                var settle = req.Settle;
                if (settle is not null && settle.Kind is not ("sale" or "store_credit" or "cash"))
                    throw new KiteException(ErrorCodes.Validation, "Settle as sale, store_credit or cash.", 400, "settle.kind");
                var saleId = req.SaleId ?? ti.SaleId;
                if (settle?.Kind == "sale" && saleId is null) throw new KiteException(ErrorCodes.Validation, "Name the sale (cart) the trade-in pays for.", 400, "saleId");

                var biz = await BusinessInfo.LoadAsync(t);
                var today = BusinessDay.Today(biz.TimeZone, t.Clock);
                var item = await ItemAsync(t, ti.VariantId);
                var ids = TradeInCore.IdsOf(ti.Imei1, ti.Imei2, ti.Serial, ti.BrandName, item.ProductId);
                await CentralRegistry.LockAsync(t, ids.Select(CentralRegistry.Key.Of));
                var identity = await TradeInCore.CheckIdentityAsync(t, ids, ti.BrandKey);
                var margin = TradeInCore.MarginOn(await TradeInCore.MarginAsync(t), today) && ti.SellerVatNumber is null && ti.CustomerVat is null;
                var selfInvoice = margin
                    ? await DocNumbers.NextAsync(t, biz.Id, ti.BranchId, "margin_self_invoice", await FiscalPeriods.FiscalYearOfAsync(t, biz.Id, today), "SI") : null;
                var source = settle?.Kind == "cash" ? "buy_back" : "trade_in";
                var unitId = await TradeInCore.AcquireDeviceAsync(t, ti, term, agreed, identity.Reconnect, source);
                await t.ExecuteAsync("""
                    INSERT INTO device.acquisition (id, business_id, branch_id, device_unit_id, source, source_doc_type, source_doc_id, cost, margin_scheme, created_by)
                    VALUES (@aid, @b, @branch, @unitId, @source, 'tradein', @id, @agreed, @margin, @uid)
                    """, new { aid = t.NewId(), b = biz.Id, branch = ti.BranchId, unitId, source, id, agreed, margin, uid });
                await Posting.PostAsync(t, biz.Id, ti.BranchId, PostingRuleTemplates.TradeInAccept,
                    new PostingFacts().Add("value_by_product_type", PostingRuleTemplates.ClassOf(item.ProductType, ti.Condition), agreed), today,
                    "tradein", id, $"Trade-in {ti.Number} accepted: {ti.DescriptionEn} {ti.Identifier}", ti.CustomerId);
                var declaration = $"{settings.DeclarationEn}\n{settings.DeclarationAr}";
                await t.ExecuteAsync("""
                    UPDATE tradein.tradein
                       SET status = 'accepted', device_unit_id = @unitId, reconnected = @reconnected, agreed_value = @agreed, approval_id = coalesce(@approvalId, approval_id),
                           consent_method = @method, consent_name = @name, consent_reference = @reference, consent_text = @declaration, consent_at = now(),
                           margin_scheme = @margin, self_invoice_number = @selfInvoice, accepted_at = now(), accepted_by = @uid, terminal_id = @terminal,
                           sale_id = @saleId, updated_by = @uid
                     WHERE id = @id
                    """, new { id, unitId, reconnected = identity.Reconnect is not null, agreed, approvalId, method = consent.Method, name = Clean(consent.Name),
                               reference = Clean(consent.Reference), declaration, margin, selfInvoice, uid, terminal = term.Id, saleId });
                await t.AuditAsync(new AuditEntry("tradein.accepted", "tradein", id, After: Json.Obj(new { ti.Number, agreed, ti.OfferedValue, approvalId,
                    consent = consent.Method, unit = unitId, reconnected = identity.Reconnect is not null, margin, selfInvoice }), BranchId: ti.BranchId));
                await t.PublishAsync("tradein.accepted", "tradein", id, 1, Json.Obj(new { ti.Number, agreed, unit = unitId, margin }), ti.BranchId);
                if (settle is { Kind: "store_credit" or "cash" })
                {
                    var accepted = await TradeInCore.LoadAsync(t, id);
                    await TradeInCore.SettleAsync(t, http, accepted, settle.Kind, settle.Amount, settle.ApprovalId, term, (tx, tm) => DrawerAsync(tx, tm, http));
                }
                return unitId;
            }, ct));
            await PushAsync(http, central, unitId, ct);
            return Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => DetailAsync(t, http, id), ct));
        }).RequirePermission(Permissions.TradeInCreate).RequireModule("tradein");

        // What is left after acceptance (or after the sale took less): store credit, or cash at the till.
        g.MapPost("/{id:guid}/settle", async (Guid id, TradeInSettleRequest req, HttpContext http, KiteDb db, LicenceService licence, CancellationToken ct) =>
        {
            await licence.RequireTransactAsync(ct);
            return Results.Ok(await PosEndpoints.WithRetryAsync(() => db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                PosTerminal? term = http.User.TerminalIdOf() is null ? null : await PosTerminals.CurrentAsync(t, http);
                // Lock order: the drawer first when paying out cash, then the trade-in (the store-credit lock follows inside).
                if (req.Kind == "cash" && term is not null)
                    await PosEndpoints.OpenShiftForUpdateAsync(t, term, http);
                var ti = await TradeInCore.LoadAsync(t, id, forUpdate: true);
                if (!http.User.HasIn(Permissions.TradeInCreate, ti.BranchId) && !http.User.HasIn(Permissions.TradeInStoreCredit, ti.BranchId))
                    http.User.Demand(Permissions.TradeInPayout, ti.BranchId);
                await TradeInCore.SettleAsync(t, http, ti, req.Kind, req.Amount, req.ApprovalId, term, (tx, tm) => DrawerAsync(tx, tm, http));
                return await DetailAsync(t, http, id);
            }, ct)));
        }).RequireSignedIn().RequireModule("tradein");

        g.MapPost("/{id:guid}/{action:regex(^(reject|cancel)$)}", async (Guid id, string action, TradeInCloseRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var ti = await TradeInCore.LoadAsync(t, id, forUpdate: true);
                http.User.Demand(Permissions.TradeInCreate, ti.BranchId);
                if (ti.Status != "open") throw new KiteException(ErrorCodes.TradeInState, $"Trade-in {ti.Number} is {ti.Status}; only an open trade-in is {action}ed.", 409);
                if (action == "reject" && string.IsNullOrWhiteSpace(req.Reason))
                    throw new KiteException(ErrorCodes.Validation, "Say why the trade-in is rejected (for example: the customer declined the offer).", 400, "reason");
                var uid = http.User.UserIdOf();
                await t.ExecuteAsync("""
                    UPDATE tradein.tradein SET status = @to, reject_reason = @reason, closed_at = now(), closed_by = @uid, updated_by = @uid WHERE id = @id
                    """, new { id, to = action == "reject" ? "rejected" : "cancelled", reason = Clean(req.Reason), uid });
                await t.AuditAsync(new AuditEntry($"tradein.{action}ed", "tradein", id, After: Json.Obj(new { ti.Number, req.Reason }), BranchId: ti.BranchId));
                return await DetailAsync(t, http, id);
            }, ct))).RequirePermission(Permissions.TradeInCreate).RequireModule("tradein");

        // The full civil ID, for a manager, audited.
        g.MapPost("/{id:guid}/civil-id", async (Guid id, HttpContext http, KiteDb db, ISecretProtector secrets, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var ti = await TradeInCore.LoadAsync(t, id);
                http.User.Demand(Permissions.TradeInApprove, ti.BranchId);
                if (ti.CivilIdEnc is null) throw new KiteException(ErrorCodes.NotFound, "No civil ID was recorded on this trade-in.", 404);
                await t.AuditAsync(new AuditEntry("tradein.civil_id_viewed", "tradein", id, After: Json.Obj(new { ti.Number }), BranchId: ti.BranchId));
                return new { civilId = Encoding.UTF8.GetString(secrets.Unprotect(ti.CivilIdEnc)) };
            }, ct))).RequirePermission(Permissions.TradeInApprove).RequireModule("tradein");

        // Accepted trade-ins with value left, for a payment at the till.
        g.MapGet("/open", async (Guid? customerId, Guid? saleId, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var term = await PosTerminals.CurrentAsync(t, http);
                return await t.RowsAsync($"""
                    SELECT t.id, t.number, t.customer_id AS "customerId", c.name_en AS "customerName", {ItemPricing.DescriptionEn} AS "descriptionEn",
                           {ItemPricing.DescriptionAr} AS "descriptionAr", coalesce(t.imei1, t.serial) AS identifier, t.agreed_value AS "agreedValue",
                           tradein.outstanding(t.id) AS outstanding, t.sale_id AS "saleId"
                      FROM tradein.tradein t JOIN crm.customer c ON c.id = t.customer_id JOIN catalog.variant v ON v.id = t.variant_id
                      JOIN catalog.product p ON p.id = v.product_id
                     WHERE t.status = 'accepted' AND t.branch_id = @branch AND (@customerId::uuid IS NULL OR t.customer_id = @customerId)
                       AND (@saleId::uuid IS NULL OR t.sale_id = @saleId)
                     ORDER BY t.accepted_at DESC LIMIT 50
                    """, new { branch = term.BranchId, customerId, saleId });
            }, ct))).RequirePermission(Permissions.PosSell).RequireModule("tradein");

        // The trade-in document: purchase receipt, or the profit-margin self-invoice (12.3), as a bilingual A4 PDF.
        g.MapGet("/{id:guid}/document", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            var (ti, biz, settlements) = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var ti = await TradeInCore.LoadAsync(t, id);
                if (!http.User.HasIn(Permissions.TradeInCreate, ti.BranchId)) http.User.Demand(Permissions.TradeInView, ti.BranchId);
                if (ti.Status is not ("accepted" or "settled")) throw new KiteException(ErrorCodes.TradeInState, "Only an accepted trade-in has a document.", 409);
                var s = (await t.QueryAsync<(string Kind, decimal Amount, string? SaleNumber)>("""
                    SELECT st.kind, st.amount, s.number FROM tradein.settlement st LEFT JOIN sales.sale s ON s.id = st.sale_id WHERE st.tradein_id = @id ORDER BY st.created_at
                    """, new { id })).ToList();
                return (ti, await BusinessInfo.LoadAsync(t), s);
            }, ct);
            return Results.File(DocumentPdf(ti, biz, settlements), "application/pdf", $"{ti.SelfInvoiceNumber ?? ti.Number}.pdf");
        }).RequireSignedIn().RequireModule("tradein");
    }

    private static async Task<(ShiftRow Shift, decimal ExpectedCash)> DrawerAsync(DbTx t, PosTerminal term, HttpContext http)
    {
        var shift = await PosEndpoints.OpenShiftForUpdateAsync(t, term, http);
        var f = await PosEndpoints.FiguresAsync(t, shift);
        return (shift, f.ExpectedCash);
    }

    private static async Task PushAsync(HttpContext http, CentralClient central, Guid unitId, CancellationToken ct)
    {
        if (!central.IsBranch) return;
        try { await central.PushAsync([unitId], ct); }
        catch (Exception e) when (e is not OperationCanceledException)
        { http.RequestServices.GetRequiredService<ILogger<CentralClient>>().LogWarning("Device push failed: {Error}", e.Message); }
    }

    private sealed record ItemRow(Guid VariantId, Guid ProductId, string ProductType, string Tracking, Guid BrandKey, string? BrandName, Guid? CategoryId);

    private static async Task<ItemRow> ItemAsync(DbTx t, Guid variantId)
    {
        var item = await t.QuerySingleOrDefaultAsync<ItemRow>("""
            SELECT v.id AS VariantId, p.id AS ProductId, p.product_type AS ProductType, p.tracking, coalesce(p.brand_id, p.id) AS BrandKey, b.name_en AS BrandName,
                   p.category_id AS CategoryId
              FROM catalog.variant v JOIN catalog.product p ON p.id = v.product_id LEFT JOIN catalog.brand b ON b.id = p.brand_id WHERE v.id = @variantId
            """, new { variantId }) ?? throw new KiteException(ErrorCodes.Validation, "That model is not in the catalog.", 400, "device.variantId");
        if (item.Tracking == "none" || item.ProductType != "device")
            throw new KiteException(ErrorCodes.Validation, "Trade-ins are devices with an IMEI or serial number; choose the phone's model.", 400, "device.variantId");
        return item;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ------------------------------------------------------------------ settings, price guide, deductions, report
    private static void MapSetup(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/tradein");

        g.MapGet("/settings", async (HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var s = await TradeInCore.SettingsAsync(t);
                var m = await TradeInCore.MarginAsync(t);
                return new { s.StaffLimit, s.OverGuidePercent, s.RequireCivilId, s.DeclarationEn, s.DeclarationAr, s.Version,
                             margin = new { m.Approved, m.Reference, approvedOn = m.ApprovedOn is { } d ? DateOnly.FromDateTime(d) : (DateOnly?)null } };
            }, ct))).RequireSignedIn().RequireModule("tradein");

        g.MapPut("/settings", async (TradeInSettingsRequest req, HttpContext http, KiteDb db, ServerContext server, CancellationToken ct) =>
        {
            if (req.StaffLimit < 0 || req.StaffLimit != Currency.OMR.Round(req.StaffLimit)) throw new KiteException(ErrorCodes.Validation, "The staff limit is zero or more, in baisa.", 400, "staffLimit");
            if (req.OverGuidePercent is < 0 or > 100) throw new KiteException(ErrorCodes.Validation, "The percentage over the guide is 0 to 100.", 400, "overGuidePercent");
            if (string.IsNullOrWhiteSpace(req.DeclarationEn) || string.IsNullOrWhiteSpace(req.DeclarationAr))
                throw new KiteException(ErrorCodes.Validation, "The declaration is needed in English and Arabic.", 400, "declarationEn");
            await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var n = await t.ExecuteAsync("""
                    UPDATE tradein.settings SET staff_limit = @StaffLimit, over_guide_percent = @OverGuidePercent, require_civil_id = @RequireCivilId,
                           declaration_en = @en, declaration_ar = @ar, updated_by = @uid
                     WHERE business_id = @b AND version = @Version
                    """, new { req.StaffLimit, req.OverGuidePercent, req.RequireCivilId, en = req.DeclarationEn.Trim(), ar = req.DeclarationAr.Trim(),
                               uid = http.User.UserIdOf(), b = server.BusinessId, req.Version });
                if (n == 0) throw new KiteException(ErrorCodes.VersionMismatch, "Someone else changed the trade-in settings. Reload them.", 412);
                await t.AuditAsync(new AuditEntry("tradein.settings_changed", "tradein_settings", server.BusinessId, After: Json.Obj(req)));
                return 0;
            }, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.TradeInSettings).RequireModule("tradein");

        g.MapGet("/guide", async (Guid? variantId, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.RowsAsync($"""
                SELECT pg.id, pg.variant_id AS "variantId", v.sku, {ItemPricing.DescriptionEn} AS "descriptionEn", {ItemPricing.DescriptionAr} AS "descriptionAr",
                       pg.grade_id AS "gradeId", g.code AS "gradeCode", pg.base_value AS "baseValue", pg.status, pg.updated_at AS "updatedAt"
                  FROM tradein.price_guide pg JOIN catalog.variant v ON v.id = pg.variant_id JOIN catalog.product p ON p.id = v.product_id
                  JOIN device.grade g ON g.id = pg.grade_id
                 WHERE (@variantId::uuid IS NULL OR pg.variant_id = @variantId) AND pg.status = 'active'
                 ORDER BY p.name_en, v.sku, g.sort
                """, new { variantId }), ct))).RequireSignedIn().RequireModule("tradein");

        // Saves guide values: a value sets or changes one (model, grade); an empty value takes it off the guide.
        g.MapPut("/guide", async (PriceGuideRequest req, HttpContext http, KiteDb db, ServerContext server, CancellationToken ct) =>
        {
            if (req.Rows is null || req.Rows.Count == 0) throw new KiteException(ErrorCodes.Validation, "Give at least one guide value.", 400, "rows");
            await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var uid = http.User.UserIdOf();
                foreach (var (r, i) in req.Rows.Select((r, i) => (r, i)))
                {
                    await ItemAsync(t, r.VariantId);
                    if (r.BaseValue is < 0 || (r.BaseValue is { } bv && bv != Currency.OMR.Round(bv)))
                        throw new KiteException(ErrorCodes.Validation, "A guide value is zero or more, in baisa.", 400, $"rows[{i}].baseValue");
                    if (!await t.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM device.grade WHERE id = @GradeId)", new { r.GradeId }))
                        throw new KiteException(ErrorCodes.Validation, "That grade does not exist.", 400, $"rows[{i}].gradeId");
                    if (r.BaseValue is null)
                        await t.ExecuteAsync("UPDATE tradein.price_guide SET status = 'inactive', updated_by = @uid WHERE variant_id = @VariantId AND grade_id = @GradeId",
                            new { r.VariantId, r.GradeId, uid });
                    else
                        await t.ExecuteAsync("""
                            INSERT INTO tradein.price_guide (id, business_id, variant_id, grade_id, base_value, created_by, updated_by)
                            VALUES (@id, @b, @VariantId, @GradeId, @BaseValue, @uid, @uid)
                            ON CONFLICT ON CONSTRAINT price_guide_one DO UPDATE SET base_value = excluded.base_value, status = 'active', updated_by = excluded.updated_by
                            """, new { id = t.NewId(), b = server.BusinessId, r.VariantId, r.GradeId, r.BaseValue, uid });
                }
                await t.AuditAsync(new AuditEntry("tradein.guide_changed", "tradein_price_guide", server.BusinessId, After: Json.Obj(new { rows = req.Rows })));
                return 0;
            }, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.TradeInSettings).RequireModule("tradein");

        g.MapGet("/deductions", async (HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.RowsAsync("""
                SELECT id, code, name_en AS "nameEn", name_ar AS "nameAr", kind, value, checklist_code AS "checklistCode", sort, status, version
                  FROM tradein.deduction ORDER BY sort, code
                """), ct))).RequireSignedIn().RequireModule("tradein");

        g.MapPost("/deductions", async (DeductionRequest req, HttpContext http, KiteDb db, ServerContext server, CancellationToken ct) =>
        {
            CheckDeduction(req);
            var id = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var id = t.NewId();
                await t.ExecuteAsync("""
                    INSERT INTO tradein.deduction (id, business_id, code, name_en, name_ar, kind, value, checklist_code, sort, created_by)
                    VALUES (@id, @b, @code, @en, @ar, @Kind, @Value, @check, @sort, @uid)
                    """, new { id, b = server.BusinessId, code = req.Code.Trim().ToUpperInvariant(), en = req.NameEn.Trim(), ar = req.NameAr.Trim(), req.Kind, req.Value,
                               check = Clean(req.ChecklistCode), sort = req.Sort ?? 0, uid = http.User.UserIdOf() });
                await t.AuditAsync(new AuditEntry("tradein.deduction_created", "tradein_deduction", id, After: Json.Obj(req)));
                return id;
            }, ct);
            return Results.Created($"/api/v1/tradein/deductions/{id}", new { id });
        }).RequirePermission(Permissions.TradeInSettings).RequireModule("tradein");

        g.MapPut("/deductions/{id:guid}", async (Guid id, DeductionRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            CheckDeduction(req);
            if (req.Status is not (null or "active" or "inactive")) throw new KiteException(ErrorCodes.Validation, "Status is active or inactive.", 400, "status");
            await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var n = await t.ExecuteAsync("""
                    UPDATE tradein.deduction SET code = @code, name_en = @en, name_ar = @ar, kind = @Kind, value = @Value, checklist_code = @check,
                           sort = coalesce(@sort, sort), status = coalesce(@st, status), updated_by = @uid
                     WHERE id = @id AND version = @ver
                    """, new { id, code = req.Code.Trim().ToUpperInvariant(), en = req.NameEn.Trim(), ar = req.NameAr.Trim(), req.Kind, req.Value,
                               check = Clean(req.ChecklistCode), sort = req.Sort, st = req.Status, uid = http.User.UserIdOf(), ver = req.Version ?? 0 });
                if (n == 0) throw new KiteException(ErrorCodes.VersionMismatch, "That deduction was changed by someone else. Reload it.", 412);
                await t.AuditAsync(new AuditEntry("tradein.deduction_changed", "tradein_deduction", id, After: Json.Obj(req)));
                return 0;
            }, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.TradeInSettings).RequireModule("tradein");

        // Trade-in value against resale (12.6): what each traded-in device cost, what was added, and what it sold for.
        g.MapGet("/report", async (HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.RowsAsync($"""
                SELECT t.number, t.accepted_at AS "acceptedAt", b.code AS "branchCode", {ItemPricing.DescriptionEn} AS "descriptionEn",
                       coalesce(t.imei1, t.serial) AS identifier, t.agreed_value AS "agreedValue", t.margin_scheme AS "marginScheme", u.status AS "unitStatus",
                       u.accumulated_cost - t.agreed_value AS "costAdded", sl.sale_number AS "saleNumber", sl.net AS "resaleNet", sl.tax AS "resaleTax",
                       sl.net - sl.cost AS "profit"
                  FROM tradein.tradein t JOIN core.branch b ON b.id = t.branch_id JOIN device.device_unit u ON u.id = t.device_unit_id
                  JOIN catalog.variant v ON v.id = t.variant_id JOIN catalog.product p ON p.id = v.product_id
                  LEFT JOIN LATERAL (SELECT s.number AS sale_number, l.net_amount AS net, l.tax_amount AS tax, l.cost_value AS cost
                                       FROM sales.sale_line l JOIN sales.sale s ON s.id = l.sale_id
                                      WHERE l.device_unit_id = t.device_unit_id AND s.doc_type <> 'credit_note' AND s.completed_at > t.accepted_at
                                        AND NOT EXISTS (SELECT 1 FROM sales.sale_line x WHERE x.returned_line_id = l.id)
                                      ORDER BY s.completed_at LIMIT 1) sl ON true
                 WHERE t.status IN ('accepted','settled')
                 ORDER BY t.accepted_at DESC LIMIT 500
                """), ct))).RequirePermission(Permissions.TradeInView).RequireModule("tradein");
    }

    private static void CheckDeduction(DeductionRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Code) || !System.Text.RegularExpressions.Regex.IsMatch(req.Code.Trim().ToUpperInvariant(), "^[A-Z0-9_-]{2,20}$"))
            throw new KiteException(ErrorCodes.Validation, "A code is 2 to 20 letters, digits, dashes or underscores.", 400, "code");
        if (string.IsNullOrWhiteSpace(req.NameEn) || string.IsNullOrWhiteSpace(req.NameAr)) throw new KiteException(ErrorCodes.Validation, "Name it in English and Arabic.", 400, "nameEn");
        if (req.Kind is not ("amount" or "percent")) throw new KiteException(ErrorCodes.Validation, "A deduction is an amount or a percent.", 400, "kind");
        if (req.Value < 0 || (req.Kind == "percent" && req.Value > 100) || req.Value != Currency.OMR.Round(req.Value))
            throw new KiteException(ErrorCodes.Validation, req.Kind == "percent" ? "A percentage is 0 to 100." : "An amount is zero or more, in baisa.", 400, "value");
    }

    // ------------------------------------------------------------------ store credit and the margin scheme approval
    private static void MapCustomersAndTax(WebApplication app)
    {
        app.MapGet("/api/v1/customers/{id:guid}/store-credit", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var balance = await t.ExecuteScalarAsync<decimal>("SELECT crm.store_credit_balance(@id)", new { id });
                var lines = await t.RowsAsync("""
                    SELECT l.id, l.amount, l.source_doc_type AS "sourceDocType", l.source_doc_id AS "sourceDocId", l.note, l.created_at AS "createdAt",
                           b.code AS "branchCode", coalesce(s.number, ti.number) AS "documentNumber"
                      FROM crm.credit_ledger l JOIN core.branch b ON b.id = l.branch_id
                      LEFT JOIN sales.sale s ON l.source_doc_type = 'sale' AND s.id = l.source_doc_id
                      LEFT JOIN tradein.tradein ti ON l.source_doc_type = 'tradein' AND ti.id = l.source_doc_id
                     WHERE l.customer_id = @id AND l.balance_type = 'store_credit' ORDER BY l.created_at DESC LIMIT 200
                    """, new { id });
                return new { customerId = id, balance, lines };
            }, ct))).RequirePermission(Permissions.CustomersView);

        app.MapGet("/api/v1/tax/margin-scheme", async (HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var m = await TradeInCore.MarginAsync(t);
                var code = await t.QuerySingleOrDefaultAsync<(string Code, string Status)>("SELECT code, status FROM tax.tax_code WHERE kind = 'margin' ORDER BY code LIMIT 1");
                return new { m.Approved, m.Reference, approvedOn = m.ApprovedOn is { } d ? DateOnly.FromDateTime(d) : (DateOnly?)null, m.Note, m.Version,
                             taxCode = code.Code, taxCodeStatus = code.Status };
            }, ct))).RequireSignedIn();

        // The accountant records the Tax Authority's approval: the VAT-MARGIN code follows it (12.3).
        app.MapPut("/api/v1/tax/margin-scheme", async (MarginSchemeRequest req, HttpContext http, KiteDb db, ServerContext server, CancellationToken ct) =>
        {
            if (req.Approved && (string.IsNullOrWhiteSpace(req.Reference) || req.ApprovedOn is null))
                throw new KiteException(ErrorCodes.Validation, "Give the Tax Authority's approval reference and date.", 400, "reference");
            await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                await t.ExecuteAsync("INSERT INTO tax.margin_scheme (business_id) VALUES (@b) ON CONFLICT DO NOTHING", new { b = server.BusinessId });
                var n = await t.ExecuteAsync("""
                    UPDATE tax.margin_scheme SET approved = @Approved, reference = @reference, approved_on = @on::date, note = @note, updated_by = @uid
                     WHERE business_id = @b AND version = @Version
                    """, new { req.Approved, reference = Clean(req.Reference), on = req.ApprovedOn?.ToDateTime(TimeOnly.MinValue), note = Clean(req.Note),
                               uid = http.User.UserIdOf(), b = server.BusinessId, req.Version });
                if (n == 0) throw new KiteException(ErrorCodes.VersionMismatch, "Someone else changed the margin scheme record. Reload it.", 412);
                await t.ExecuteAsync("UPDATE tax.tax_code SET status = @st, updated_by = @uid WHERE kind = 'margin'",
                    new { st = req.Approved ? "active" : "inactive", uid = http.User.UserIdOf() });
                await t.AuditAsync(new AuditEntry("tax.margin_scheme_changed", "margin_scheme", server.BusinessId, After: Json.Obj(req)));
                await t.PublishAsync("tax.margin_scheme_changed", "margin_scheme", server.BusinessId ?? Guid.Empty, 1, Json.Obj(req), null);
                return 0;
            }, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.TaxManage);
    }

    // ------------------------------------------------------------------ the document
    private static string M(decimal v) => v.ToString("#,##0.000", CultureInfo.InvariantCulture);

    public static byte[] DocumentPdf(TradeInRow ti, BusinessInfo biz, IReadOnlyList<(string Kind, decimal Amount, string? SaleNumber)> settlements)
    {
        var (titleEn, titleAr) = ti.MarginScheme
            ? ("Profit margin self-invoice", "فاتورة ذاتية بنظام هامش الربح")
            : ("Trade-in purchase receipt", "إيصال شراء جهاز مستعمل");
        return Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(32);
            page.DefaultTextStyle(x => x.FontSize(10).FontFamily("Segoe UI", "Tahoma", "DejaVu Sans", "Noto Sans Arabic"));
            page.Header().Column(col =>
            {
                col.Item().Row(r =>
                {
                    r.RelativeItem().Column(c =>
                    {
                        c.Item().Text(titleEn).FontSize(16).Bold();
                        c.Item().Text(ti.SelfInvoiceNumber is { } si ? $"{si}  ·  {ti.Number}" : ti.Number);
                        c.Item().Text($"{ti.AcceptedAt:yyyy-MM-dd HH:mm} UTC");
                    });
                    r.RelativeItem().AlignRight().Column(c =>
                    {
                        c.Item().ContentFromRightToLeft().Text(titleAr).FontSize(16).Bold();
                        c.Item().Text(biz.SellerNameEn).Bold();
                        c.Item().ContentFromRightToLeft().Text(biz.SellerNameAr);
                        if (biz.VatNumber is { } vat) c.Item().Text($"VAT / الرقم الضريبي {vat}");
                    });
                });
            });
            page.Content().PaddingTop(14).Column(col =>
            {
                void Pair(string en, string ar, string value)
                {
                    col.Item().Row(r =>
                    {
                        r.RelativeItem(2).Text(en).Bold();
                        r.RelativeItem(3).Text(value);
                        r.RelativeItem(2).AlignRight().ContentFromRightToLeft().Text(ar).Bold();
                    });
                }
                col.Item().Text("Seller / البائع").FontSize(12).Bold();
                Pair("Name", "الاسم", ti.CustomerName);
                if (ti.CustomerPhone is { } ph) Pair("Phone", "الهاتف", ph);
                if (ti.CivilIdMasked is { } cid) Pair("Civil ID", "الرقم المدني", cid);
                if (ti.SellerVatNumber is { } sv) Pair("VAT number", "الرقم الضريبي", sv);
                col.Item().PaddingTop(10).Text("Device / الجهاز").FontSize(12).Bold();
                Pair("Model", "الطراز", ti.DescriptionEn);
                if (ti.Imei1 is { } i1) Pair("IMEI", "IMEI", i1);
                if (ti.Imei2 is { } i2) Pair("IMEI 2", "IMEI 2", i2);
                if (ti.Serial is { } sn) Pair("Serial number", "الرقم التسلسلي", sn);
                Pair("Condition", "الحالة", $"{ti.Condition}{(ti.GradeCode is { } gc ? $" · grade {gc}" : "")}");
                col.Item().PaddingTop(10).Text("Value / القيمة").FontSize(12).Bold();
                Pair("Agreed value", "القيمة المتفق عليها", $"OMR {M(ti.AgreedValue ?? 0)}");
                foreach (var (kind, amount, sale) in settlements)
                    Pair(kind switch { "sale" => $"Applied to sale {sale}", "store_credit" => "Store credit", _ => "Paid in cash" },
                         kind switch { "sale" => "مخصوم من فاتورة البيع", "store_credit" => "رصيد لدى المتجر", _ => "مدفوع نقدًا" }, $"OMR {M(amount)}");
                if (ti.MarginScheme)
                {
                    col.Item().PaddingTop(10).Text("Bought under the profit margin scheme from a person not registered for VAT. No input tax is deducted on this purchase.").Italic();
                    col.Item().ContentFromRightToLeft().Text("تم الشراء وفق نظام هامش الربح من شخص غير مسجل في ضريبة القيمة المضافة، ولا تُخصم ضريبة المدخلات عن هذا الشراء.").Italic();
                }
                col.Item().PaddingTop(14).Text("Declaration / الإقرار").FontSize(12).Bold();
                foreach (var line in (ti.ConsentText ?? "").Split('\n'))
                    col.Item().Text(line);
                col.Item().PaddingTop(6).Text(ti.ConsentMethod == "paper"
                    ? $"Signed on paper, form {ti.ConsentReference} · {ti.ConsentAt:yyyy-MM-dd HH:mm} UTC"
                    : $"Confirmed on screen by {ti.ConsentName} · {ti.ConsentAt:yyyy-MM-dd HH:mm} UTC");
            });
            page.Footer().AlignCenter().Text($"{ti.Number} · {ti.BranchCode} · {ti.AcceptedByName}").FontSize(8);
        })).GeneratePdf();
    }
}
