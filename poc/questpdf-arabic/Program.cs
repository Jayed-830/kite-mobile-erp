// PoC: a bilingual A4 tax invoice with Arabic shaped and laid out right-to-left by QuestPDF.
// QuestPDF's Community licence covers businesses under its revenue threshold; Nextdab must
// confirm which licence applies before production (blueprint 20.1).
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

QuestPDF.Settings.License = LicenseType.Community;
var output = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine("output", "arabic-invoice.pdf"));
Directory.CreateDirectory(Path.GetDirectoryName(output)!);

var lines = new[]
{
    (En: "iPhone 15 128GB Black", Ar: "آيفون 15 سعة 128 جيجابايت أسود", Imei: "356938035643809", Qty: 1, Amount: "299.000"),
    (En: "Screen protector", Ar: "واقي شاشة", Imei: "", Qty: 2, Amount: "6.000"),
    (En: "Repair labour: battery replacement", Ar: "أجور صيانة: استبدال البطارية", Imei: "", Qty: 1, Amount: "8.500"),
};

Document.Create(doc => doc.Page(page =>
{
    page.Size(PageSizes.A4);
    page.Margin(36);
    page.DefaultTextStyle(x => x.FontSize(11).FontFamily("Segoe UI", "Tahoma", "DejaVu Sans", "Noto Sans Arabic"));

    page.Header().Row(row =>
    {
        row.RelativeItem().Column(c =>
        {
            c.Item().Text("Al Noor Mobiles LLC").Bold().FontSize(16);
            c.Item().Text("Muscat - Ruwi");
            c.Item().Text("VAT No. OM1100223344");
        });
        row.RelativeItem().ContentFromRightToLeft().Column(c =>
        {
            c.Item().Text("شركة النور للهواتف").Bold().FontSize(16);
            c.Item().Text("مسقط - روي");
            c.Item().Text("الرقم الضريبي OM1100223344");
        });
    });

    page.Content().PaddingVertical(18).Column(col =>
    {
        col.Spacing(10);
        col.Item().Row(r =>
        {
            r.RelativeItem().Text("Tax invoice  INV-B01-2026-000123").SemiBold();
            r.RelativeItem().ContentFromRightToLeft().Text("فاتورة ضريبية").SemiBold();
        });
        col.Item().Table(t =>
        {
            t.ColumnsDefinition(cd => { cd.RelativeColumn(4); cd.RelativeColumn(4); cd.ConstantColumn(36); cd.ConstantColumn(80); });
            t.Header(h =>
            {
                h.Cell().BorderBottom(1).PaddingBottom(4).Text("Item").Bold();
                h.Cell().BorderBottom(1).PaddingBottom(4).ContentFromRightToLeft().Text("الصنف").Bold();
                h.Cell().BorderBottom(1).PaddingBottom(4).AlignCenter().Text("Qty").Bold();
                h.Cell().BorderBottom(1).PaddingBottom(4).AlignRight().Text("OMR").Bold();
            });
            foreach (var l in lines)
            {
                t.Cell().PaddingVertical(4).Column(c => { c.Item().Text(l.En); if (l.Imei != "") c.Item().Text($"IMEI {l.Imei}").FontSize(9); });
                t.Cell().PaddingVertical(4).ContentFromRightToLeft().Text(l.Ar);
                t.Cell().PaddingVertical(4).AlignCenter().Text(l.Qty.ToString(System.Globalization.CultureInfo.InvariantCulture));
                t.Cell().PaddingVertical(4).AlignRight().Text(l.Amount);
            }
        });
        col.Item().AlignRight().Width(260).Column(c =>
        {
            c.Item().Row(r => { r.RelativeItem().Text("VAT 5% included"); r.RelativeItem().ContentFromRightToLeft().Text("شامل ضريبة القيمة المضافة 5%"); });
            c.Item().AlignRight().Text("14.929");
            c.Item().Row(r => { r.RelativeItem().Text("TOTAL  313.500").Bold().FontSize(14); r.RelativeItem().ContentFromRightToLeft().Text("الإجمالي").Bold().FontSize(14); });
        });
    });

    page.Footer().AlignCenter().Text(x => { x.Span("KITE MOBILE ERP · Page "); x.CurrentPageNumber(); });
})).GeneratePdf(output);

Console.WriteLine($"QuestPDF PoC: wrote {output} ({new FileInfo(output).Length} bytes)");
