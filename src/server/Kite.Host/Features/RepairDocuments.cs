using System.Globalization;
using System.Text.Json;
using Kite.Host.Infrastructure;
using Kite.Persistence;
using Kite.Platform;
using QRCoder;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Kite.Host.Features;

public static partial class RepairEndpoints
{
    // ------------------------------------------------------------------ settings, categories and fault codes
    private static void MapSetup(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/repair-setup");

        g.MapGet("", async (HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t => new
            {
                settings = await RepairCore.SettingsAsync(t),
                categories = await t.RowsAsync("""
                    SELECT id, code, name_en AS "nameEn", name_ar AS "nameAr", turnaround_hours AS "turnaroundHours", warranty_days AS "warrantyDays",
                           pre_authorised AS "preAuthorised", sort, status, version FROM repair.category ORDER BY sort, code
                    """),
                faultCodes = await t.RowsAsync("""
                    SELECT id, category_id AS "categoryId", code, name_en AS "nameEn", name_ar AS "nameAr", sort, status, version FROM repair.fault_code ORDER BY sort, code
                    """),
                services = await t.RowsAsync($"""
                    SELECT v.id, v.sku, p.name_en AS "nameEn", p.name_ar AS "nameAr", p.product_type AS "productType"
                      FROM catalog.variant v JOIN catalog.product p ON p.id = v.product_id
                     WHERE p.product_type = 'service' AND v.status = 'active' AND p.status = 'active' ORDER BY (upper(v.sku) LIKE 'REPAIR-%') DESC, p.name_en
                    """),
                accessories = AccessoryCodes, conditionChecks = ConditionCodes,
            }, ct))).RequirePermission(Permissions.RepairView).RequireModule("repair");

        g.MapPut("/settings", async (RepairSettingsRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var before = await RepairCore.SettingsAsync(t);
                if (before.Version != req.Version) throw new KiteException(ErrorCodes.VersionMismatch, "Someone else changed the repair settings. Reload and try again.", 412);
                if (req.DepositVatPoint != before.DepositVatPoint) http.User.Demand(Permissions.TaxManage, null);
                if ((req.UncollectedDays ?? []).Any(d => d is < 1 or > 365)) throw new KiteException(ErrorCodes.Validation, "Reminder ages are 1 to 365 days.", 400, "uncollectedDays");
                if (string.IsNullOrWhiteSpace(req.TermsEn) || string.IsNullOrWhiteSpace(req.TermsAr)) throw new KiteException(ErrorCodes.Validation, "Write the terms in English and Arabic.", 400, "termsEn");
                await t.ExecuteAsync("""
                    UPDATE repair.settings SET pre_authorised = @PreAuthorised, warranty_days = @WarrantyDays, passcode_keep_days = @PasscodeKeepDays,
                           uncollected_days = @days, diagnosis_fee = @DiagnosisFee, hourly_rate = @HourlyRate, deposit_vat_point = @DepositVatPoint,
                           notify_statuses = @NotifyStatuses, terms_en = @TermsEn, terms_ar = @TermsAr, updated_by = @uid
                    """, new { req.PreAuthorised, req.WarrantyDays, req.PasscodeKeepDays, days = (req.UncollectedDays ?? []).Distinct().Order().ToArray(), req.DiagnosisFee,
                               req.HourlyRate, req.DepositVatPoint, req.NotifyStatuses, req.TermsEn, req.TermsAr, uid = Uid(http) });
                await t.AuditAsync(new AuditEntry("repair.settings_changed", "repair_settings", null, Before: Json.Obj(before), After: Json.Obj(req)));
                return await RepairCore.SettingsAsync(t);
            }, ct))).RequirePermission(Permissions.RepairSettings).RequireModule("repair");

        g.MapPost("/categories", async (RepairCategoryRequest req, HttpContext http, KiteDb db, ServerContext server, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var id = t.NewId();
                await t.ExecuteAsync("""
                    INSERT INTO repair.category (id, business_id, code, name_en, name_ar, turnaround_hours, warranty_days, pre_authorised, sort)
                    VALUES (@id, @b, upper(@Code), @NameEn, @NameAr, @TurnaroundHours, @WarrantyDays, @PreAuthorised, coalesce(@Sort, 100))
                    """, new { id, b = server.BusinessId, req.Code, req.NameEn, req.NameAr, req.TurnaroundHours, req.WarrantyDays, req.PreAuthorised, req.Sort });
                await t.AuditAsync(new AuditEntry("repair.category_created", "repair_category", id, After: Json.Obj(req)));
                return new { id };
            }, ct))).RequirePermission(Permissions.RepairSettings).RequireModule("repair");

        g.MapPut("/categories/{id:guid}", async (Guid id, RepairCategoryRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var n = await t.ExecuteAsync("""
                    UPDATE repair.category SET code = upper(@Code), name_en = @NameEn, name_ar = @NameAr, turnaround_hours = @TurnaroundHours, warranty_days = @WarrantyDays,
                           pre_authorised = @PreAuthorised, sort = coalesce(@Sort, sort), status = coalesce(@Status, status)
                     WHERE id = @id AND version = @Version
                    """, new { id, req.Code, req.NameEn, req.NameAr, req.TurnaroundHours, req.WarrantyDays, req.PreAuthorised, req.Sort, req.Status, req.Version });
                if (n == 0) throw new KiteException(ErrorCodes.VersionMismatch, "Someone else changed this category. Reload and try again.", 412);
                await t.AuditAsync(new AuditEntry("repair.category_changed", "repair_category", id, After: Json.Obj(req)));
                return new { id };
            }, ct))).RequirePermission(Permissions.RepairSettings).RequireModule("repair");

        g.MapPost("/fault-codes", async (FaultCodeRequest req, HttpContext http, KiteDb db, ServerContext server, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var id = t.NewId();
                await t.ExecuteAsync("""
                    INSERT INTO repair.fault_code (id, business_id, category_id, code, name_en, name_ar, sort)
                    VALUES (@id, @b, @CategoryId, upper(@Code), @NameEn, @NameAr, coalesce(@Sort, 100))
                    """, new { id, b = server.BusinessId, req.CategoryId, req.Code, req.NameEn, req.NameAr, req.Sort });
                return new { id };
            }, ct))).RequirePermission(Permissions.RepairSettings).RequireModule("repair");

        g.MapPut("/fault-codes/{id:guid}", async (Guid id, FaultCodeRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var n = await t.ExecuteAsync("""
                    UPDATE repair.fault_code SET category_id = @CategoryId, code = upper(@Code), name_en = @NameEn, name_ar = @NameAr, sort = coalesce(@Sort, sort),
                           status = coalesce(@Status, status)
                     WHERE id = @id AND version = @Version
                    """, new { id, req.CategoryId, req.Code, req.NameEn, req.NameAr, req.Sort, req.Status, req.Version });
                if (n == 0) throw new KiteException(ErrorCodes.VersionMismatch, "Someone else changed this fault code. Reload and try again.", 412);
                return new { id };
            }, ct))).RequirePermission(Permissions.RepairSettings).RequireModule("repair");

        // The customer's devices (for the intake wizard's customer step).
        app.MapGet("/api/v1/customers/{id:guid}/devices", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.RowsAsync("""
                SELECT d.id, d.imei1, d.imei2, d.serial, d.brand, d.model, d.colour, d.device_unit_id AS "deviceUnitId",
                       (SELECT count(*) FROM repair.repair_job j WHERE j.customer_device_id = d.id) AS jobs,
                       (SELECT j.number FROM repair.repair_job j WHERE j.customer_device_id = d.id AND j.status <> 'delivered' LIMIT 1) AS "openJob"
                  FROM device.customer_device d WHERE d.customer_id = @id AND d.status = 'active' ORDER BY d.created_at DESC
                """, new { id }), ct))).RequirePermission(Permissions.RepairView).RequireModule("repair");
    }

    // ------------------------------------------------------------------ documents (13.1, 13.5)
    private static void MapDocuments(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/repairs/{id:guid}");

        // The job card: A5, bilingual, with the QR used at collection, the condition, accessories and the signed terms. Never the passcode.
        g.MapGet("/job-card", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            var pdf = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var j = await RepairCore.LoadAsync(t, id);
                http.User.Demand(Permissions.RepairView, j.BranchId);
                var biz = await BusinessInfo.LoadAsync(t);
                var settings = await RepairCore.SettingsAsync(t);
                var signature = await t.ExecuteScalarAsync<byte[]?>("SELECT content FROM repair.photo WHERE job_id = @id AND stage = 'terms_signature' ORDER BY created_at LIMIT 1", new { id });
                var deposit = await RepairCore.DepositBalanceAsync(t, id);
                var faults = (await t.QueryAsync<(string Code, string En, string Ar)>("SELECT code, name_en, name_ar FROM repair.fault_code WHERE code = ANY(@c)", new { c = j.FaultCodes })).ToList();
                return JobCardPdf(j, biz, settings, signature, deposit, faults);
            }, ct);
            return Results.File(pdf, "application/pdf", $"job-card-{id:N}.pdf");
        }).RequirePermission(Permissions.RepairView).RequireModule("repair");

        // The device tag: a small label with the job number, the QR and the customer's name, to keep with the device.
        g.MapGet("/tag", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            var pdf = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var j = await RepairCore.LoadAsync(t, id);
                http.User.Demand(Permissions.RepairView, j.BranchId);
                return TagPdf(j);
            }, ct);
            return Results.File(pdf, "application/pdf", $"device-tag-{id:N}.pdf");
        }).RequirePermission(Permissions.RepairView).RequireModule("repair");

        // A deposit receipt or refund receipt (80 mm).
        g.MapGet("/deposits/{depositId:guid}/receipt", async (Guid id, Guid depositId, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            var pdf = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var j = await RepairCore.LoadAsync(t, id);
                http.User.Demand(Permissions.RepairView, j.BranchId);
                var d = await t.QuerySingleOrDefaultAsync<(string Number, string Kind, decimal Amount, decimal Tax, string Method, DateTime At, string By, string? Reason)>("""
                    SELECT d.number, d.kind, d.amount, d.tax, d.method, d.created_at, u.display_name_en, d.reason FROM repair.deposit d JOIN iam."user" u ON u.id = d.created_by
                     WHERE d.id = @depositId AND d.job_id = @id AND d.kind <> 'applied'
                    """, new { depositId, id });
                if (d.Number is null) throw new KiteException(ErrorCodes.NotFound, "That receipt is not on this job.", 404);
                var biz = await BusinessInfo.LoadAsync(t);
                return DepositPdf(j, biz, d.Number, d.Kind, d.Amount, d.Tax, d.Method, d.At, d.By, d.Reason, await RepairCore.DepositBalanceAsync(t, id));
            }, ct);
            return Results.File(pdf, "application/pdf", $"deposit-{depositId:N}.pdf");
        }).RequirePermission(Permissions.RepairView).RequireModule("repair");

        // The delivery receipt (13.5): who collected, when, the warranty, the invoice number; signed.
        g.MapGet("/delivery-receipt", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            var pdf = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var j = await RepairCore.LoadAsync(t, id);
                http.User.Demand(Permissions.RepairView, j.BranchId);
                if (j.Status != "delivered") throw new KiteException(ErrorCodes.RepairState, "The device has not been collected yet.", 409);
                var biz = await BusinessInfo.LoadAsync(t);
                var signature = await t.ExecuteScalarAsync<byte[]?>("SELECT content FROM repair.photo WHERE job_id = @id AND stage = 'collection_signature' ORDER BY created_at DESC LIMIT 1", new { id });
                var gross = j.DeliverySaleId is { } s ? await t.ExecuteScalarAsync<decimal>("SELECT gross_total FROM sales.sale WHERE id = @s", new { s }) : 0m;
                return DeliveryPdf(j, biz, signature, gross);
            }, ct);
            return Results.File(pdf, "application/pdf", $"delivery-{id:N}.pdf");
        }).RequirePermission(Permissions.RepairView).RequireModule("repair");
    }

    private static byte[] Qr(string text)
    {
        using var data = QRCodeGenerator.GenerateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(6);
    }

    private static string M(decimal v) => v.ToString("0.000", CultureInfo.InvariantCulture);
    private static readonly string[] Fonts = ["Segoe UI", "Tahoma", "DejaVu Sans", "Noto Sans Arabic"];

    private static readonly Dictionary<string, (string En, string Ar)> AccessoryNames = new()
    {
        ["sim_tray"] = ("SIM tray", "درج الشريحة"), ["sim_card"] = ("SIM card", "الشريحة"), ["memory_card"] = ("Memory card", "بطاقة الذاكرة"),
        ["case"] = ("Case", "الغطاء"), ["charger"] = ("Charger", "الشاحن"), ["box"] = ("Box", "العلبة"),
    };
    private static readonly Dictionary<string, (string En, string Ar)> CheckNames = new()
    {
        ["screen"] = ("Screen", "الشاشة"), ["body"] = ("Body", "الهيكل"), ["buttons"] = ("Buttons", "الأزرار"), ["cameras"] = ("Cameras", "الكاميرات"),
        ["liquid"] = ("Liquid indicator", "مؤشر السوائل"),
    };

    public static byte[] JobCardPdf(RepairJob j, BusinessInfo biz, RepairSettings settings, byte[]? signature, decimal deposit, IReadOnlyList<(string Code, string En, string Ar)> faults)
    {
        var qr = Qr(QrText(j));
        var condition = JsonDocument.Parse(j.Condition).RootElement;
        var acc = JsonDocument.Parse(j.Accessories).RootElement;
        return Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.A5);
            page.Margin(20);
            page.DefaultTextStyle(x => x.FontSize(8.5f).FontFamily(Fonts));
            page.Header().Row(r =>
            {
                r.RelativeItem().Column(c =>
                {
                    c.Item().Text(biz.SellerNameEn).Bold().FontSize(11);
                    c.Item().Text("Repair job card").FontSize(13).Bold();
                    c.Item().Text(j.Number).FontSize(12).Bold();
                    c.Item().Text($"{BusinessDay.Local(j.CreatedAt, biz.TimeZone):yyyy-MM-dd HH:mm} · {j.BranchCode}");
                });
                r.ConstantItem(84).Image(qr);
                r.RelativeItem().AlignRight().Column(c =>
                {
                    c.Item().ContentFromRightToLeft().Text(biz.SellerNameAr).Bold().FontSize(11);
                    c.Item().ContentFromRightToLeft().Text("بطاقة طلب صيانة").FontSize(13).Bold();
                    if (biz.Phone is { } ph) c.Item().AlignRight().Text(ph);
                });
            });
            page.Content().PaddingTop(8).Column(col =>
            {
                void Pair(string en, string ar, string? value)
                {
                    if (string.IsNullOrWhiteSpace(value)) return;
                    col.Item().Row(r =>
                    {
                        r.RelativeItem(2).Text(en).SemiBold();
                        r.RelativeItem(4).Text(value);
                        r.RelativeItem(2).AlignRight().ContentFromRightToLeft().Text(ar).SemiBold();
                    });
                }
                Pair("Customer", "العميل", j.CustomerName);
                Pair("Phone", "الهاتف", j.CustomerPhone);
                Pair("Device", "الجهاز", j.DeviceName + (j.Colour is { } col1 ? $" · {col1}" : ""));
                Pair("IMEI / serial", "IMEI / الرقم التسلسلي", string.Join(" · ", new[] { j.Imei1, j.Imei2, j.Serial }.Where(x => x is not null)));
                Pair("Repair", "نوع الصيانة", $"{j.CategoryNameEn} / {j.CategoryNameAr}");
                if (j.WarrantyOfNumber is { } wo) Pair("Warranty claim on", "مطالبة ضمان على", wo);
                if (j.SoldSaleNumber is { } sold) Pair("Bought here", "اشتُري من المحل", sold);
                Pair("Problem", "المشكلة", j.Problem);
                Pair("Faults", "الأعطال", string.Join(", ", faults.Select(f => $"{f.En} / {f.Ar}")));
                var checks = string.Join("  ·  ", CheckNames.Where(c => condition.TryGetProperty(c.Key, out _))
                    .Select(c => $"{c.Value.En}: {condition.GetProperty(c.Key).GetString()?.Replace('_', ' ')}"));
                Pair("Condition", "الحالة", checks + (j.ConditionNotes is { } cn ? $" — {cn}" : ""));
                var items = acc.TryGetProperty("items", out var it) ? it.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];
                var accText = string.Join(", ", items.Select(i => AccessoryNames.TryGetValue(i, out var n) ? $"{n.En} / {n.Ar}" : i));
                if (acc.TryGetProperty("other", out var other) && other.ValueKind == JsonValueKind.String) accText += (accText.Length > 0 ? ", " : "") + other.GetString();
                Pair("Accessories received", "الملحقات المستلمة", accText.Length == 0 ? "None / لا شيء" : accText);
                Pair("Device access", "الوصول إلى الجهاز", j.PasscodeMode switch
                {
                    "provided" => "Passcode given (kept encrypted, not printed) / الرمز مُعطى (محفوظ مشفرًا ولا يُطبع)",
                    "customer_present" => "Tested with the customer present / يُختبر بحضور العميل",
                    _ => "Not given / غير مُعطى",
                });
                Pair("Pre-authorised", "الموافقة المسبقة", $"OMR {M(j.PreAuthorised)}");
                if (deposit > 0) Pair("Deposit held", "العربون", $"OMR {M(deposit)}");
                Pair("Promised", "الموعد المتوقع", BusinessDay.Local(j.PromisedAt, biz.TimeZone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
                col.Item().PaddingTop(8).Text("Terms / الشروط").Bold();
                foreach (var line in settings.TermsEn.Split('\n')) col.Item().Text(line).FontSize(7);
                foreach (var line in settings.TermsAr.Split('\n')) col.Item().ContentFromRightToLeft().Text(line).FontSize(7);
                col.Item().PaddingTop(6).Row(r =>
                {
                    r.RelativeItem().Column(c =>
                    {
                        c.Item().Text($"Accepted by {j.TermsName} · {BusinessDay.Local(j.TermsAcceptedAt, biz.TimeZone):yyyy-MM-dd HH:mm}");
                        c.Item().ContentFromRightToLeft().Text($"وافق عليها {j.TermsName}");
                    });
                    if (signature is not null) r.ConstantItem(120).Height(40).Image(signature).FitArea();
                });
                col.Item().PaddingTop(6).Text("Bring this card to collect your device. / أحضر هذه البطاقة لاستلام جهازك.").Italic();
            });
            page.Footer().AlignCenter().Text($"{j.Number} · {j.CreatedByName}").FontSize(7);
        })).GeneratePdf();
    }

    public static byte[] TagPdf(RepairJob j)
    {
        var qr = Qr(QrText(j));
        return Document.Create(doc => doc.Page(page =>
        {
            page.Size(new PageSize(62, 40, Unit.Millimetre));
            page.Margin(2, Unit.Millimetre);
            page.DefaultTextStyle(x => x.FontSize(7).FontFamily(Fonts));
            page.Content().Row(r =>
            {
                r.ConstantItem(34, Unit.Millimetre).Image(qr);
                r.RelativeItem().PaddingLeft(2).Column(c =>
                {
                    c.Item().Text(j.Number).Bold().FontSize(8);
                    c.Item().Text(j.CustomerName);
                    c.Item().Text(j.Model);
                    c.Item().Text(j.Identifier);
                });
            });
        })).GeneratePdf();
    }

    public static byte[] DepositPdf(RepairJob j, BusinessInfo biz, string number, string kind, decimal amount, decimal tax, string method, DateTime at, string by,
        string? reason, decimal balance)
    {
        var (en, ar) = kind == "refund" ? ("Deposit refund", "رد عربون") : tax > 0 ? ("Tax invoice - advance payment", "فاتورة ضريبية - دفعة مقدمة") : ("Deposit receipt", "إيصال عربون");
        return Document.Create(doc => doc.Page(page =>
        {
            page.ContinuousSize(80, Unit.Millimetre);
            page.Margin(4, Unit.Millimetre);
            page.DefaultTextStyle(x => x.FontSize(8).FontFamily(Fonts));
            page.Content().Column(c =>
            {
                c.Item().AlignCenter().Text(biz.SellerNameEn).Bold();
                c.Item().AlignCenter().ContentFromRightToLeft().Text(biz.SellerNameAr).Bold();
                if (biz.VatNumber is { } vat) c.Item().AlignCenter().Text($"VAT {vat}");
                c.Item().PaddingTop(4).AlignCenter().Text($"{en} / {ar}").Bold();
                c.Item().AlignCenter().Text(number);
                c.Item().AlignCenter().Text($"{BusinessDay.Local(at, biz.TimeZone):yyyy-MM-dd HH:mm}");
                c.Item().PaddingTop(4).Text($"Job / الطلب: {j.Number}");
                c.Item().Text($"Customer / العميل: {j.CustomerName}");
                c.Item().Text($"Device / الجهاز: {j.DeviceName}");
                c.Item().PaddingTop(4).Text($"Amount / المبلغ: OMR {M(amount)}").Bold();
                if (tax > 0) c.Item().Text($"VAT included / شامل الضريبة: OMR {M(tax)}");
                c.Item().Text($"Method / الطريقة: {method.Replace('_', ' ')}");
                if (reason is not null) c.Item().Text($"Reason / السبب: {reason}");
                c.Item().Text($"Deposit held now / العربون الحالي: OMR {M(balance)}");
                c.Item().PaddingTop(4).AlignCenter().Text(by).FontSize(7);
            });
        })).GeneratePdf();
    }

    public static byte[] DeliveryPdf(RepairJob j, BusinessInfo biz, byte[]? signature, decimal gross) =>
        Document.Create(doc => doc.Page(page =>
        {
            page.ContinuousSize(80, Unit.Millimetre);
            page.Margin(4, Unit.Millimetre);
            page.DefaultTextStyle(x => x.FontSize(8).FontFamily(Fonts));
            page.Content().Column(c =>
            {
                c.Item().AlignCenter().Text(biz.SellerNameEn).Bold();
                c.Item().AlignCenter().ContentFromRightToLeft().Text(biz.SellerNameAr).Bold();
                c.Item().PaddingTop(4).AlignCenter().Text("Delivery receipt / إيصال تسليم").Bold();
                c.Item().AlignCenter().Text(j.Number);
                c.Item().AlignCenter().Text($"{BusinessDay.Local(j.DeliveredAt ?? DateTime.UtcNow, biz.TimeZone):yyyy-MM-dd HH:mm}");
                c.Item().PaddingTop(4).Text($"Customer / العميل: {j.CustomerName}");
                c.Item().Text($"Device / الجهاز: {j.DeviceName} · {j.Identifier}");
                c.Item().Text($"Result / النتيجة: {j.Outcome switch { "repaired" => "Repaired / تم الإصلاح", "cancelled" => "Cancelled / ملغى", _ => "Not repairable / غير قابل للإصلاح" }}");
                if (j.DeliverySaleNumber is { } inv) c.Item().Text($"Invoice / الفاتورة: {inv} · OMR {M(gross)}");
                if (j.WarrantyUntil is { } w) c.Item().Text($"Warranty until / الضمان حتى: {w:yyyy-MM-dd}").Bold();
                c.Item().Text($"Collected by / استلمه: {j.CollectedBy} ({(j.CollectionCheck == "qr" ? "job card" : "checked")})");
                if (signature is not null) c.Item().PaddingTop(4).Height(40).Image(signature).FitArea();
                c.Item().PaddingTop(4).AlignCenter().Text(j.DeliveredByName ?? "").FontSize(7);
            });
        })).GeneratePdf();
}
