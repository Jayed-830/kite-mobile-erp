using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Kite.Host.Infrastructure;
using Kite.Persistence;
using Kite.Platform;
using QRCoder;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Kite.Host.Features;

public sealed record ReceiptParty(string NameEn, string NameAr, string? VatNumber, string? CrNumber, string? AddressEn, string? AddressAr, string? Phone);
/// <summary>A receipt line; a device line also names its IMEI or serial number (11.5) and the warranty end.</summary>
/// <summary>A margin-scheme line (12.3) shows no VAT: its <see cref="Tax"/> is 0 and <see cref="Net"/> is the price paid.</summary>
public sealed record ReceiptLine(string DescriptionEn, string DescriptionAr, decimal Qty, decimal UnitPrice, decimal Discount, decimal Net, decimal Tax,
    decimal Gross, decimal TaxRate, string? Serial, DateTime? WarrantyUntil, bool Margin = false);
public sealed record ReceiptTax(decimal Rate, decimal Net, decimal Tax);
public sealed record ReceiptPayment(string Method, decimal Amount, decimal? Tendered, decimal? Change, string? CardTerminal);
public sealed record ReceiptModel(
    Guid SaleId, string Number, string DocType, string TitleEn, string TitleAr, string? OriginalNumber, string? CreditReason, DateTime LocalTime,
    string TerminalCode, string CashierName, string? CustomerName, string? CustomerVat, ReceiptParty Business, ReceiptParty Branch,
    IReadOnlyList<ReceiptLine> Lines, decimal Net, decimal Discount, decimal Tax, decimal Gross, decimal CashRounding, decimal Total,
    IReadOnlyList<ReceiptTax> TaxBreakdown, IReadOnlyList<ReceiptPayment> Payments, string QrPayload, string? FooterEn, string? FooterAr,
    bool IsCopy, bool IsEmergency, int PaperWidthMm, decimal MarginGross = 0m, string? MarginNoteEn = null, string? MarginNoteAr = null);

public sealed record PrinterRequest(Guid BranchId, string Name, string Host, int? Port = null, int? PaperWidthMm = null, bool? HasDrawer = null,
    string? Status = null, int? Version = null);
public sealed record PrintJobRequest(string Kind, string? Data = null, Guid? SaleId = null);
public sealed record TerminalPrinterRequest(Guid? PrinterId);
public sealed record PrinterDto(Guid Id, Guid BranchId, string Name, string Host, int Port, short PaperWidthMm, bool HasDrawer, string Status,
    string? LastResult, DateTime? LastUsedAt, int Version);

/// <summary>
/// Receipts and printing (10.5, pulled forward): the receipt model every terminal renders (80 mm thermal, bilingual, QR),
/// the A4 tax invoice PDF, network printers and the print relay that sends ESC/POS bytes from browser terminals.
/// </summary>
public static class ReceiptEndpoints
{
    public static void Map(WebApplication app)
    {
        QuestPDF.Settings.License = LicenseType.Community;   // confirmed by Nextdab on 16 November 2026 (ADR 22)

        app.MapGet("/api/v1/sales/{id:guid}/receipt", async (Guid id, bool? reprint, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var r = await BuildAsync(t, http, id);
                if (reprint == true)
                {
                    http.User.Demand(Permissions.PosReceiptReprint, await t.ExecuteScalarAsync<Guid>("SELECT branch_id FROM sales.sale WHERE id = @id", new { id }));
                    await t.AuditAsync(new AuditEntry("pos.receipt_reprinted", "sale", id, After: Json.Obj(new { r.Number })));
                    r = r with { IsCopy = true };
                }
                return r;
            }, ct))).RequireSignedIn();

        app.MapGet("/api/v1/sales/{id:guid}/invoice.pdf", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            var r = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => BuildAsync(t, http, id), ct);
            return Results.File(InvoicePdf(r), "application/pdf", $"{r.Number}.pdf");
        }).RequireSignedIn();

        // ---------------------------------------------------------- printers and the relay
        var g = app.MapGroup("/api/v1/printers");
        const string select = """
            SELECT id, branch_id AS BranchId, name, host, port, paper_width_mm AS PaperWidthMm, has_drawer AS HasDrawer, status, last_result AS LastResult,
                   last_used_at AS LastUsedAt, version FROM sales.printer
            """;

        g.MapGet("", async (Guid? branchId, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t =>
                t.QueryAsync<PrinterDto>(select + " WHERE (@branchId::uuid IS NULL OR branch_id = @branchId) ORDER BY name", new { branchId }), ct)))
            .RequireSignedIn();

        g.MapPost("", async (PrinterRequest req, HttpContext http, KiteDb db, ServerContext server, CancellationToken ct) =>
        {
            http.User.Demand(Permissions.TerminalsApprove, req.BranchId);
            CheckPrinter(req);
            var dto = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var id = t.NewId();
                await t.ExecuteAsync("""
                    INSERT INTO sales.printer (id, business_id, branch_id, name, host, port, paper_width_mm, has_drawer, created_by)
                    VALUES (@id, @b, @BranchId, @name, @host, @port, @width, @drawer, @uid)
                    """, new { id, b = server.BusinessId, req.BranchId, name = req.Name.Trim(), host = req.Host.Trim(), port = req.Port ?? 9100,
                               width = (short)(req.PaperWidthMm ?? 80), drawer = req.HasDrawer ?? true, uid = http.User.UserIdOf() });
                await t.AuditAsync(new AuditEntry("pos.printer_added", "printer", id, After: Json.Obj(req), BranchId: req.BranchId));
                return await t.QuerySingleOrDefaultAsync<PrinterDto>(select + " WHERE id = @id", new { id });
            }, ct);
            return Results.Created($"/api/v1/printers/{dto!.Id}", dto);
        }).RequireSignedIn();

        g.MapPut("/{id:guid}", async (Guid id, PrinterRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            http.User.Demand(Permissions.TerminalsApprove, req.BranchId);
            CheckPrinter(req);
            var status = req.Status ?? "active";
            if (status is not ("active" or "inactive")) throw new KiteException(ErrorCodes.Validation, "Status must be active or inactive.", 400, "status");
            var dto = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var n = await t.ExecuteAsync("""
                    UPDATE sales.printer SET name = @name, host = @host, port = @port, paper_width_mm = @width, has_drawer = @drawer, status = @status, updated_by = @uid
                     WHERE id = @id AND branch_id = @BranchId AND (@Version::int IS NULL OR version = @Version)
                    """, new { id, req.BranchId, name = req.Name.Trim(), host = req.Host.Trim(), port = req.Port ?? 9100, width = (short)(req.PaperWidthMm ?? 80),
                               drawer = req.HasDrawer ?? true, status, req.Version, uid = http.User.UserIdOf() });
                if (n == 0) throw new KiteException(ErrorCodes.VersionMismatch, "The printer changed or does not exist. Reload and try again.", 412);
                await t.AuditAsync(new AuditEntry("pos.printer_changed", "printer", id, After: Json.Obj(req), BranchId: req.BranchId));
                return await t.QuerySingleOrDefaultAsync<PrinterDto>(select + " WHERE id = @id", new { id });
            }, ct);
            return Results.Ok(dto);
        }).RequireSignedIn();

        app.MapPut("/api/v1/terminals/{id:guid}/printer", async (Guid id, TerminalPrinterRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var branch = await t.ExecuteScalarAsync<Guid?>("SELECT branch_id FROM core.terminal WHERE id = @id", new { id })
                    ?? throw new KiteException(ErrorCodes.NotFound, "Terminal not found in your branches.", 404);
                http.User.Demand(Permissions.TerminalsApprove, branch);
                if (req.PrinterId is { } pid && await t.ExecuteScalarAsync<Guid?>("SELECT branch_id FROM sales.printer WHERE id = @pid", new { pid }) != branch)
                    throw new KiteException(ErrorCodes.Validation, "That printer is in another branch.", 400, "printerId");
                await t.ExecuteAsync("UPDATE core.terminal SET printer_id = @PrinterId, updated_by = @uid WHERE id = @id", new { id, req.PrinterId, uid = http.User.UserIdOf() });
                await t.AuditAsync(new AuditEntry("terminal.printer_set", "terminal", id, After: Json.Obj(req), BranchId: branch));
            }, ct);
            return Results.NoContent();
        }).RequireSignedIn();

        // Browser terminals print through the server: the terminal renders the receipt to ESC/POS raster bytes and the
        // server relays them to the printer on the shop network (raw TCP). The drawer kick needs no data.
        g.MapPost("/{id:guid}/jobs", async (Guid id, PrintJobRequest req, HttpContext http, KiteDb db, KiteOptions options, CancellationToken ct) =>
        {
            var p = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.QuerySingleOrDefaultAsync<PrinterDto>(select + " WHERE id = @id", new { id }), ct)
                ?? throw new KiteException(ErrorCodes.NotFound, "Printer not found in your branches.", 404);
            http.User.Demand(Permissions.PosSell, p.BranchId);
            if (p.Status != "active") throw new KiteException(ErrorCodes.Validation, "That printer is switched off in KITE.", 400);
            byte[] bytes = req.Kind switch
            {
                "drawer" when p.HasDrawer => EscPos.DrawerKick,
                "drawer" => throw new KiteException(ErrorCodes.Validation, "That printer has no cash drawer.", 400, "kind"),
                "test" => EscPos.TestPage(p.Name, DateTime.Now),
                "receipt" or "raw" => DecodeData(req.Data),
                _ => throw new KiteException(ErrorCodes.Validation, "Kind must be receipt, raw, drawer or test.", 400, "kind"),
            };
            if (req.Kind == "drawer")
                await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
                {
                    var shiftOpen = http.User.TerminalIdOf() is { } tid && await t.ExecuteScalarAsync<bool>(
                        "SELECT EXISTS (SELECT 1 FROM sales.shift WHERE terminal_id = @tid AND status = 'open' AND cashier_id = @uid)", new { tid, uid = http.User.UserIdOf() });
                    if (!shiftOpen) throw new KiteException(ErrorCodes.NoOpenShift, "The drawer opens only during your shift.", 409);
                }, ct);
            string result;
            try
            {
                await PrintRelay.SendAsync(p.Host, p.Port, bytes, options.Printing.AllowPublicAddresses, ct);
                result = "ok";
            }
            catch (KiteException) { throw; }
            catch (Exception e) when (e is SocketException or IOException or TimeoutException or OperationCanceledException)
            {
                result = e is OperationCanceledException && !ct.IsCancellationRequested ? "timeout" : e.Message;
            }
            await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                await t.ExecuteAsync("UPDATE sales.printer SET last_result = @result, last_used_at = now() WHERE id = @id", new { id, result });
                if (req.Kind == "drawer") await t.AuditAsync(new AuditEntry("pos.drawer_opened", "printer", id, After: Json.Obj(new { req.SaleId }), BranchId: p.BranchId));
            }, ct);
            if (result != "ok") throw new KiteException(ErrorCodes.PrinterUnreachable, $"Printer {p.Name} ({p.Host}:{p.Port}) did not answer: {result}", 502);
            return Results.Ok(new { printed = true, bytes = bytes.Length });
        }).RequireSignedIn();
    }

    private static byte[] DecodeData(string? data)
    {
        if (string.IsNullOrEmpty(data)) throw new KiteException(ErrorCodes.Validation, "Send the receipt bytes.", 400, "data");
        if (data.Length > 4_000_000) throw new KiteException(ErrorCodes.Validation, "The print job is too large.", 400, "data");
        try { return Convert.FromBase64String(data); }
        catch (FormatException) { throw new KiteException(ErrorCodes.Validation, "Print data must be base64.", 400, "data"); }
    }

    private static void CheckPrinter(PrinterRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) throw new KiteException(ErrorCodes.Validation, "Name the printer.", 400, "name");
        if (string.IsNullOrWhiteSpace(req.Host) || !System.Text.RegularExpressions.Regex.IsMatch(req.Host.Trim(), "^[A-Za-z0-9.-]{1,253}$"))
            throw new KiteException(ErrorCodes.Validation, "Give the printer's IP address or host name on the shop network.", 400, "host");
        if (req.Port is < 1 or > 65535) throw new KiteException(ErrorCodes.Validation, "Port is 1 to 65535 (usually 9100).", 400, "port");
        if (req.PaperWidthMm is not (null or 58 or 80)) throw new KiteException(ErrorCodes.Validation, "Paper is 58 or 80 mm.", 400, "paperWidthMm");
    }

    public static async Task<ReceiptModel> BuildAsync(DbTx t, HttpContext http, Guid id)
    {
        var s = await t.QuerySingleOrDefaultAsync<SaleHeader>(SaleHeader.Select + " WHERE s.id = @id", new { id })
            ?? throw new KiteException(ErrorCodes.NotFound, "That sale was not found in your branches.", 404);
        if (!http.User.HasIn(Permissions.PosSell, s.BranchId)) http.User.Demand(Permissions.PosReportsView, s.BranchId);
        var biz = await BusinessInfo.LoadAsync(t);
        var branch = await t.QuerySingleOrDefaultAsync<(string Code, string NameEn, string NameAr, string? AddressEn, string? AddressAr, string? Phone)>(
            "SELECT code, name_en, name_ar, address_en, address_ar, phone FROM core.branch WHERE id = @b", new { b = s.BranchId });
        var lines = (await t.QueryAsync<ReceiptLine>("""
            SELECT l.description_en AS DescriptionEn, l.description_ar AS DescriptionAr, l.qty, l.unit_price AS UnitPrice, l.discount_amount AS Discount,
                   l.net_amount AS Net, l.tax_amount AS Tax, l.gross_amount AS Gross, l.tax_rate AS TaxRate,
                   (SELECT CASE r.kind WHEN 'serial' THEN 'S/N ' ELSE 'IMEI ' END || r.value FROM device.imei_registry r
                     WHERE r.device_unit_id = l.device_unit_id ORDER BY (r.status = 'active') DESC, r.kind, r.slot LIMIT 1) AS Serial,
                   CASE WHEN l.device_unit_id IS NOT NULL THEN l.warranty_until END AS WarrantyUntil, l.tax_scheme = 'margin' AS Margin
              FROM sales.sale_line l WHERE l.sale_id = @id ORDER BY l.line_no
            """, new { id })).ToArray();
        // Margin-scheme lines (12.3) carry their VAT inside the price and the receipt never shows it.
        var marginGross = lines.Where(l => l.Margin).Sum(l => l.Gross);
        lines = lines.Select(l => l.Margin ? l with { Net = l.Gross, Tax = 0m } : l).ToArray();
        var payments = (await t.QueryAsync<ReceiptPayment>("""
            SELECT method, abs(amount) AS Amount, tendered, change_given AS Change, card_terminal AS CardTerminal FROM sales.payment WHERE sale_id = @id ORDER BY created_at, id
            """, new { id })).ToArray();
        var policy = await PosPolicies.ForBranchAsync(t, biz.Id, s.BranchId);
        var width = await t.ExecuteScalarAsync<short?>("SELECT p.paper_width_mm FROM sales.sale s JOIN core.terminal t ON t.id = s.terminal_id JOIN sales.printer p ON p.id = t.printer_id WHERE s.id = @id", new { id }) ?? 80;
        var (en, ar) = s.DocType switch
        {
            "tax_invoice" => ("Tax invoice", "فاتورة ضريبية"),
            "credit_note" => ("Credit note", "إشعار دائن"),
            _ => ("Simplified tax invoice", "فاتورة ضريبية مبسطة"),
        };
        var breakdown = lines.Where(l => !l.Margin).GroupBy(l => l.TaxRate).OrderBy(g => g.Key).Select(g => new ReceiptTax(g.Key, g.Sum(l => l.Net), g.Sum(l => l.Tax))).ToArray();
        var standardNet = breakdown.Sum(b => b.Net);
        var standardTax = breakdown.Sum(b => b.Tax);
        return new ReceiptModel(s.Id, s.Number, s.DocType, en, ar, s.OriginalNumber, s.CreditReason,
            BusinessDay.Local(new DateTimeOffset(DateTime.SpecifyKind(s.CompletedAt, DateTimeKind.Utc)), biz.TimeZone),
            s.TerminalCode, s.CashierName, s.CustomerName, s.CustomerVat,
            new ReceiptParty(biz.SellerNameEn, biz.SellerNameAr, biz.VatNumber, biz.CrNumber, biz.AddressEn, biz.AddressAr, biz.Phone),
            new ReceiptParty(branch.NameEn, branch.NameAr, null, null, branch.AddressEn, branch.AddressAr, branch.Phone),
            lines, standardNet, s.DiscountTotal, standardTax, s.GrossTotal, s.CashRounding, s.GrossTotal + s.CashRounding, breakdown, payments, s.QrPayload,
            policy.ReceiptFooterEn, policy.ReceiptFooterAr, false, s.IsEmergency, width, marginGross,
            marginGross == 0 ? null : MarginNoteEn, marginGross == 0 ? null : MarginNoteAr);
    }

    /// <summary>The wording a margin-scheme receipt carries in place of the VAT amount (to be confirmed by the business's accountant).</summary>
    public const string MarginNoteEn = "Second-hand goods sold under the profit margin scheme: VAT is included in the price and not shown separately.";
    public const string MarginNoteAr = "سلع مستعملة مباعة وفق نظام هامش الربح: الضريبة مشمولة في السعر ولا تظهر بشكل منفصل.";

    private static string M(decimal v) => v.ToString("#,##0.000", CultureInfo.InvariantCulture);
    private static string Q(decimal v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>The bilingual A4 tax invoice (Arabic shaped right-to-left by QuestPDF, proven in the Phase 2 PoC).</summary>
    public static byte[] InvoicePdf(ReceiptModel r)
    {
        using var qrData = QRCodeGenerator.GenerateQrCode(r.QrPayload, QRCodeGenerator.ECCLevel.M);
        var qrPng = new PngByteQRCode(qrData).GetGraphic(6);
        return Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(32);
            page.DefaultTextStyle(x => x.FontSize(10).FontFamily("Segoe UI", "Tahoma", "DejaVu Sans", "Noto Sans Arabic"));
            page.Header().Column(col =>
            {
                col.Item().Row(row =>
                {
                    row.RelativeItem().Column(c =>
                    {
                        c.Item().Text(r.Business.NameEn).Bold().FontSize(15);
                        if (r.Business.AddressEn is { } a) c.Item().Text(a);
                        c.Item().Text($"{r.Branch.NameEn}{(r.Branch.Phone is null ? "" : " · " + r.Branch.Phone)}");
                        if (r.Business.VatNumber is { } v) c.Item().Text($"VAT No. {v}");
                        if (r.Business.CrNumber is { } cr) c.Item().Text($"CR No. {cr}");
                    });
                    row.ConstantItem(90).AlignCenter().Image(qrPng);
                    row.RelativeItem().ContentFromRightToLeft().Column(c =>
                    {
                        c.Item().Text(r.Business.NameAr).Bold().FontSize(15);
                        if (r.Business.AddressAr is { } a) c.Item().Text(a);
                        c.Item().Text(r.Branch.NameAr);
                        if (r.Business.VatNumber is { } v) c.Item().Text($"الرقم الضريبي {v}");
                        if (r.Business.CrNumber is { } cr) c.Item().Text($"السجل التجاري {cr}");
                    });
                });
                col.Item().PaddingVertical(8).LineHorizontal(1).LineColor(Colors.Grey.Medium);
                col.Item().Row(row =>
                {
                    row.RelativeItem().Text($"{r.TitleEn}{(r.IsCopy ? " (copy)" : "")}").Bold().FontSize(13);
                    row.RelativeItem().AlignRight().ContentFromRightToLeft().Text($"{r.TitleAr}{(r.IsCopy ? " (نسخة)" : "")}").Bold().FontSize(13);
                });
                col.Item().PaddingTop(4).Row(row =>
                {
                    row.RelativeItem().Column(c =>
                    {
                        c.Item().Text($"No. / الرقم: {r.Number}");
                        if (r.OriginalNumber is { } o) c.Item().Text($"For invoice / عن الفاتورة: {o}");
                        c.Item().Text($"Date / التاريخ: {r.LocalTime:yyyy-MM-dd HH:mm}");
                    });
                    row.RelativeItem().Column(c =>
                    {
                        if (r.CustomerName is { } cn) c.Item().Text($"Customer / العميل: {cn}");
                        if (r.CustomerVat is { } cv) c.Item().Text($"Customer VAT / الرقم الضريبي للعميل: {cv}");
                        c.Item().Text($"Terminal {r.TerminalCode} · {r.CashierName}");
                    });
                });
            });
            page.Content().PaddingTop(10).Column(col =>
            {
                col.Item().Table(table =>
                {
                    table.ColumnsDefinition(c => { c.RelativeColumn(5); c.RelativeColumn(1); c.RelativeColumn(2); c.RelativeColumn(2); c.RelativeColumn(2); c.RelativeColumn(2); });
                    table.Header(h =>
                    {
                        foreach (var (en, ar) in new[] { ("Item", "الصنف"), ("Qty", "الكمية"), ("Price", "السعر"), ("Discount", "الخصم"), ("VAT", "الضريبة"), ("Total", "الإجمالي") })
                            h.Cell().Background(Colors.Grey.Lighten3).Padding(4).Column(c => { c.Item().Text(en).Bold(); c.Item().Text(ar).FontSize(8); });
                    });
                    foreach (var l in r.Lines)
                    {
                        table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(4).Column(c =>
                        {
                            c.Item().Text(l.DescriptionEn);
                            c.Item().ContentFromRightToLeft().Text(l.DescriptionAr).FontSize(9);
                            if (l.Serial is { } sn) c.Item().Text(sn).FontSize(8);
                            if (l.WarrantyUntil is { } w) c.Item().Text($"Warranty until / الضمان حتى {w:yyyy-MM-dd}").FontSize(8);
                        });
                        table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(4).AlignRight().Text(Q(l.Qty));
                        table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(4).AlignRight().Text(M(l.UnitPrice));
                        table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(4).AlignRight().Text(l.Discount == 0 ? "" : M(l.Discount));
                        table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(4).AlignRight().Text(l.Margin ? "Margin / هامش" : $"{M(l.Tax)} ({Q(l.TaxRate)}%)");
                        table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(4).AlignRight().Text(M(l.Gross));
                    }
                });
                col.Item().PaddingTop(10).AlignRight().Width(260).Table(table =>
                {
                    table.ColumnsDefinition(c => { c.RelativeColumn(3); c.RelativeColumn(2); });
                    void Row(string label, decimal value, bool bold = false)
                    {
                        table.Cell().Padding(2).Text(label).Bold();
                        var cell = table.Cell().Padding(2).AlignRight().Text($"OMR {M(value)}");
                        if (bold) cell.Bold().FontSize(12);
                    }
                    Row("Total before VAT / الإجمالي قبل الضريبة", r.Net);
                    foreach (var b in r.TaxBreakdown) Row($"VAT {Q(b.Rate)}% / ضريبة القيمة المضافة", b.Tax);
                    if (r.MarginGross != 0) Row("Margin scheme items / سلع بنظام الهامش", r.MarginGross);
                    if (r.CashRounding != 0) Row("Cash rounding / تقريب النقد", r.CashRounding);
                    Row(r.DocType == "credit_note" ? "Refund / المبلغ المسترد" : "Total / الإجمالي", r.Total, true);
                });
                col.Item().PaddingTop(10).Text(string.Join("   ", r.Payments.Select(p => $"{MethodName(p.Method)}: OMR {M(p.Amount)}")));
                if (r.MarginNoteEn is { } mn) col.Item().PaddingTop(8).Text(mn).Italic().FontSize(9);
                if (r.MarginNoteAr is { } ma) col.Item().ContentFromRightToLeft().Text(ma).Italic().FontSize(9);
                if (r.FooterEn is { } fe) col.Item().PaddingTop(16).AlignCenter().Text(fe);
                if (r.FooterAr is { } fa) col.Item().AlignCenter().ContentFromRightToLeft().Text(fa);
            });
            page.Footer().AlignCenter().Text(x =>
            {
                x.Span($"{r.Number} · ").FontSize(8);
                x.CurrentPageNumber().FontSize(8);
                x.Span(" / ").FontSize(8);
                x.TotalPages().FontSize(8);
            });
        })).GeneratePdf();
    }

    private static string MethodName(string m) => m switch
    {
        "cash" => "Cash / نقداً", "card" => "Card / بطاقة", "trade_in" => "Trade-in / جهاز مستبدل", "store_credit" => "Store credit / رصيد المتجر",
        _ => "Bank transfer / تحويل بنكي",
    };
}

/// <summary>Minimal ESC/POS commands the server itself sends (the receipt bytes are rendered by the terminal).</summary>
public static class EscPos
{
    /// <summary>ESC p 0: pulse drawer pin 2 for 50 ms on, 500 ms off.</summary>
    public static readonly byte[] DrawerKick = [0x1B, 0x70, 0x00, 0x19, 0xFA];

    public static byte[] TestPage(string printer, DateTime at)
    {
        var text = $"KITE MOBILE ERP\nPrinter test: {printer}\n{at:yyyy-MM-dd HH:mm}\n\n\n";
        return [0x1B, 0x40, .. Encoding.ASCII.GetBytes(text), 0x1D, 0x56, 0x42, 0x00];
    }
}

/// <summary>Sends raw bytes to a printer on the shop network over TCP (5 s to connect, 15 s to send).</summary>
public static class PrintRelay
{
    public static async Task SendAsync(string host, int port, byte[] data, bool allowPublic, CancellationToken ct)
    {
        var addresses = IPAddress.TryParse(host, out var ip) ? [ip] : await Dns.GetHostAddressesAsync(host, ct);
        var target = addresses.FirstOrDefault(a => allowPublic || IsLocal(a))
            ?? throw new KiteException(ErrorCodes.Validation, "Printers must be on the shop network (a private address).", 400, "host");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var client = new TcpClient(target.AddressFamily);
        await client.ConnectAsync(target, port, timeout.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await using var stream = client.GetStream();
        await stream.WriteAsync(data, timeout.Token);
        await stream.FlushAsync(timeout.Token);
    }

    public static bool IsLocal(IPAddress a)
    {
        if (IPAddress.IsLoopback(a)) return true;
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (a.AddressFamily == AddressFamily.InterNetworkV6) return a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6UniqueLocal;
        var b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }
}
