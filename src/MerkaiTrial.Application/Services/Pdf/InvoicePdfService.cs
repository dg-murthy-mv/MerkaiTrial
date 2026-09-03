// =====================================================================
// FILE: MerkaiTrial.Application/Services/Pdf/InvoicePdfService.cs
// PACKAGE: QuestPDF (install: dotnet add package QuestPDF)
// PURPOSE: Generate professional PDF invoices with tenant branding
//
// SETUP: In Program.cs add:
//   QuestPDF.Settings.License = LicenseType.Community;  // free for < $1M revenue
//   builder.Services.AddScoped<IInvoicePdfService, InvoicePdfService>();
// =====================================================================

using MerkaiTrial.Application.DTOs;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System.Globalization;

namespace MerkaiTrial.Application.Services.Pdf
{
    public interface IInvoicePdfService
    {
        /// <summary>Generate PDF bytes for an invoice. Stream to client or save to disk.</summary>
        byte[] Generate(InvoicePdfModel model);
    }

    // ── Model passed from handler to PDF service ──────────────────────
    public class InvoicePdfModel
    {
        public InvoiceDto   Invoice       { get; set; } = null!;
        public TenantPdfInfo Tenant       { get; set; } = null!;
        public string        CurrencySymbol { get; set; } = string.Empty;
        public string        DateFormat    { get; set; } = "dd/MM/yyyy";
        public string        TaxLabel      { get; set; } = "Tax";
        public string        CurrencyCode  { get; set; } = string.Empty;  // ✅ e.g. "PHP", "INR" — used to derive CultureInfo

        public string NumberFormat { get; set; }
    }

    public class TenantPdfInfo
    {
        public string Name    { get; set; } = string.Empty;
        public string? Phone  { get; set; }
        public string? Email  { get; set; }
        public string Country { get; set; } = string.Empty;
    }

    // ── QuestPDF implementation ───────────────────────────────────────
    public class InvoicePdfService : IInvoicePdfService
    {
        // ✅ Map currency code → CultureInfo for number formatting
        // Countries.NumberFormat stores "#,##0.00" patterns (Excel-style), not culture names
        // We derive culture from currency code instead
        private static CultureInfo GetCultureForCurrency(string? currencyCode) => currencyCode switch
        {
            "INR" => new CultureInfo("en-IN"),
            "PHP" => new CultureInfo("en-PH"),
            "THB" => new CultureInfo("th-TH"),
            "AED" => new CultureInfo("en-AE"),
            "USD" => new CultureInfo("en-US"),
            "EUR" => new CultureInfo("en-IE"),
            "GBP" => new CultureInfo("en-GB"),
            _     => CultureInfo.InvariantCulture
        };

        // Brand colours
        private static readonly string AccentHex   = "#4F46E5"; // indigo-600
        private static readonly string LightHex    = "#EEF2FF"; // indigo-50
        private static readonly string MutedHex    = "#6B7280"; // gray-500
        private static readonly string BorderHex   = "#E5E7EB"; // gray-200
        private static readonly string DangerHex   = "#DC2626"; // red-600

        public byte[] Generate(InvoicePdfModel model)
        {
            // ✅ Resolve culture once — never use model.NumberFormat directly
            var culture = GetCultureForCurrency(model.CurrencyCode);

            return Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(40);
                    page.DefaultTextStyle(x => x.FontSize(10).FontFamily("Arial"));

                    page.Content().Column(col =>
                    {
                        // ── HEADER ─────────────────────────────────────────────
                        col.Item().Row(row =>
                        {
                            // Left: tenant name + contact
                            row.RelativeItem().Column(c =>
                            {
                                c.Item()
                                    .Text(model.Tenant.Name)
                                    .FontSize(20).Bold()
                                    .FontColor(AccentHex);

                                if (!string.IsNullOrEmpty(model.Tenant.Email))
                                    c.Item().Text(model.Tenant.Email)
                                        .FontSize(9).FontColor(MutedHex);

                                if (!string.IsNullOrEmpty(model.Tenant.Phone))
                                    c.Item().Text(model.Tenant.Phone)
                                        .FontSize(9).FontColor(MutedHex);

                                c.Item().Text(model.Tenant.Country)
                                    .FontSize(9).FontColor(MutedHex);
                            });

                            // Right: INVOICE label + number
                            row.ConstantItem(200).Column(c =>
                            {
                                c.Item().AlignRight()
                                    .Text("INVOICE")
                                    .FontSize(26).Bold()
                                    .FontColor(AccentHex);

                                c.Item().AlignRight()
                                    .Text(model.Invoice.Number)
                                    .FontSize(13).Bold();

                                // Status badge
                                var statusColor = model.Invoice.Status switch
                                {
                                    "Paid"          => "#16A34A",
                                    "PartiallyPaid" => "#D97706",
                                    "Overdue"       => DangerHex,
                                    "Cancelled"     => "#6B7280",
                                    _               => AccentHex
                                };
                                var statusLabel = model.Invoice.Status switch
                                {
                                    "PartiallyPaid" => "PARTIALLY PAID",
                                    _               => model.Invoice.Status.ToUpperInvariant()
                                };

                                c.Item().AlignRight().PaddingTop(4)
                                    .Background(statusColor)
                                    .PaddingHorizontal(8).PaddingVertical(3)
                                    .Text(statusLabel)
                                    .FontSize(8).Bold().FontColor("#FFFFFF");
                            });
                        });

                        col.Item().PaddingVertical(12)
                            .LineHorizontal(1.5f).LineColor(AccentHex);

                        // ── BILL TO + DATES ────────────────────────────────────
                        col.Item().Row(row =>
                        {
                            // Bill To
                            row.RelativeItem().Column(c =>
                            {
                                c.Item().Text("BILL TO")
                                    .FontSize(8).Bold().FontColor(MutedHex);

                                c.Item().PaddingTop(4)
                                    .Text(model.Invoice.CompanyName ?? model.Invoice.ContactName ?? "—")
                                    .Bold().FontSize(11);

                                if (!string.IsNullOrEmpty(model.Invoice.ContactName) &&
                                    model.Invoice.ContactName != model.Invoice.CompanyName)
                                    c.Item().Text(model.Invoice.ContactName)
                                        .FontSize(9).FontColor(MutedHex);

                                if (!string.IsNullOrEmpty(model.Invoice.DealTitle))
                                    c.Item().PaddingTop(2)
                                        .Text($"Re: {model.Invoice.DealTitle}")
                                        .FontSize(9).Italic().FontColor(MutedHex);
                            });

                            // Dates
                            row.ConstantItem(220).Column(c =>
                            {
                                void DateRow(string label, string value, bool overdue = false)
                                {
                                    c.Item().Row(r =>
                                    {
                                        r.RelativeItem()
                                            .Text(label).FontSize(9).FontColor(MutedHex);
                                        var t = r.ConstantItem(120).AlignRight()
                                            .Text(value).FontSize(9)
                                            .FontColor(overdue ? DangerHex : "#000000");
                                        if (overdue) t.Bold();
                                    });
                                    c.Item().PaddingBottom(3);
                                }

                                DateRow("Issue date:",
                                    model.Invoice.IssueDateUtc.ToString(
                                        model.DateFormat, CultureInfo.InvariantCulture));

                                if (model.Invoice.DueDateUtc.HasValue)
                                {
                                    var overdue = model.Invoice.IsOverdue;
                                    DateRow($"Due date{(overdue ? " ⚠" : "")}:",
                                        model.Invoice.DueDateUtc.Value.ToString(
                                            model.DateFormat, CultureInfo.InvariantCulture),
                                        overdue);
                                }

                                if (!string.IsNullOrEmpty(model.Invoice.QuoteNumber))
                                    DateRow("Quote ref:", model.Invoice.QuoteNumber);
                            });
                        });

                        col.Item().PaddingTop(20);

                        // ── LINE ITEMS TABLE ───────────────────────────────────
                        col.Item().Table(table =>
                        {
                            // Column definitions
                            table.ColumnsDefinition(cols =>
                            {
                                cols.ConstantColumn(24);  // #
                                cols.RelativeColumn(3);   // Item
                                cols.RelativeColumn(4);   // Description
                                cols.ConstantColumn(36);  // Qty
                                cols.ConstantColumn(80);  // Unit Price
                                cols.ConstantColumn(44);  // Tax %
                                cols.ConstantColumn(80);  // Total
                            });

                            // Header row
                            void HeaderCell(string text, bool alignRight = false)
                            {
                                table.Cell()
                                    .Background(AccentHex)
                                    .PaddingHorizontal(6).PaddingVertical(5)
                                    .Element(e => alignRight ? e.AlignRight() : e)
                                    .Text(text)
                                    .FontSize(8.5f).Bold().FontColor("#FFFFFF");
                            }

                            HeaderCell("#");
                            HeaderCell("Item");
                            HeaderCell("Description");
                            HeaderCell("Qty", true);
                            HeaderCell("Unit Price", true);
                            HeaderCell($"{model.TaxLabel} %", true);
                            HeaderCell("Total", true);

                            // Data rows
                            var lineItems = model.Invoice.Lines.Where(l => l.LineGrandTotal >= 0).ToList();
                            for (int i = 0; i < lineItems.Count; i++)
                            {
                                var line   = lineItems[i];
                                var rowBg  = i % 2 == 0 ? "#FFFFFF" : LightHex;

                                void DataCell(string text, bool alignRight = false,
                                              bool bold = false, string? color = null)
                                {
                                    var t = table.Cell()
                                        .Background(rowBg)
                                        .BorderBottom(0.5f).BorderColor(BorderHex)
                                        .PaddingHorizontal(6).PaddingVertical(5)
                                        .Element(e => alignRight ? e.AlignRight() : e)
                                        .Text(text)
                                        .FontSize(9)
                                        .FontColor(color ?? "#000000");
                                    if (bold) t.Bold();
                                }

                                var sym = model.CurrencySymbol;

                                DataCell((i + 1).ToString(), color: MutedHex);
                                DataCell(line.Name ?? "—", bold: true);
                                DataCell(line.Description ?? "—", color: MutedHex);
                                DataCell(line.Quantity.ToString(), alignRight: true);
                                DataCell($"{sym}{line.UnitPrice.ToString("N2", culture)}", alignRight: true);
                                DataCell($"{(line.TaxRate * 100):N0}%", alignRight: true, color: MutedHex);
                                DataCell($"{sym}{line.LineGrandTotal.ToString("N2", culture)}", alignRight: true, bold: true);
                            }
                        });

                        // ── TOTALS ─────────────────────────────────────────────
                        col.Item().PaddingTop(12).Row(row =>
                        {
                            row.RelativeItem(); // spacer

                            row.ConstantItem(240).Column(totals =>
                            {
                                var sym = model.CurrencySymbol;

                                void TotalRow(string label, decimal amount,
                                              bool bold = false, string? color = null,
                                              bool separator = false)
                                {
                                    if (separator)
                                        totals.Item()
                                            .LineHorizontal(0.5f).LineColor(BorderHex);

                                    totals.Item().PaddingVertical(2).Row(r =>
                                    {
                                        var lbl = r.RelativeItem()
                                            .Text(label)
                                            .FontSize(9).FontColor(color ?? MutedHex);
                                        if (bold) lbl.Bold();
                                        var amt = r.ConstantItem(100).AlignRight()
                                            .Text($"{sym}{amount.ToString("N2", culture)}")
                                            .FontSize(9).FontColor(color ?? "#000000");
                                        if (bold) amt.Bold();
                                    });
                                }

                                TotalRow("Subtotal:", model.Invoice.Subtotal);

                                if (model.Invoice.DiscountTotal > 0)
                                    TotalRow("Discount:", -model.Invoice.DiscountTotal,
                                             color: "#16A34A");

                                TotalRow($"{model.TaxLabel}:", model.Invoice.TaxTotal);

                                totals.Item().PaddingVertical(4)
                                    .LineHorizontal(1.5f).LineColor(AccentHex);

                                totals.Item().PaddingVertical(3).Row(r =>
                                {
                                    r.RelativeItem()
                                        .Text("Grand Total:")
                                        .FontSize(12).Bold().FontColor(AccentHex);
                                    r.ConstantItem(100).AlignRight()
                                        .Text($"{sym}{model.Invoice.GrandTotal.ToString("N2", culture)}")
                                        .FontSize(13).Bold().FontColor(AccentHex);
                                });

                                if (model.Invoice.TotalPaid > 0)
                                {
                                    TotalRow("Paid:", model.Invoice.TotalPaid, color: "#16A34A");
                                    totals.Item().PaddingVertical(3).Row(r =>
                                    {
                                        r.RelativeItem()
                                            .Text("Balance Due:")
                                            .FontSize(11).Bold()
                                            .FontColor(model.Invoice.Balance > 0 ? DangerHex : "#16A34A");
                                        r.ConstantItem(100).AlignRight()
                                            .Text($"{sym}{model.Invoice.Balance.ToString("N2", culture)}")
                                            .FontSize(12).Bold()
                                            .FontColor(model.Invoice.Balance > 0 ? DangerHex : "#16A34A");
                                    });
                                }
                            });
                        });

                        // ── PAYMENT HISTORY ────────────────────────────────────
                        if (model.Invoice.Payments.Any())
                        {
                            col.Item().PaddingTop(20);
                            col.Item().Text("Payment History")
                                .FontSize(10).Bold().FontColor(AccentHex);
                            col.Item().PaddingTop(4);

                            col.Item().Table(table =>
                            {
                                table.ColumnsDefinition(cols =>
                                {
                                    cols.RelativeColumn(2);
                                    cols.RelativeColumn(2);
                                    cols.RelativeColumn(3);
                                    cols.RelativeColumn(2);
                                });

                                void PH(string text)
                                    => table.Cell()
                                        .Background(LightHex)
                                        .PaddingHorizontal(6).PaddingVertical(4)
                                        .Text(text).FontSize(8.5f).Bold().FontColor(MutedHex);

                                PH("Date"); PH("Method"); PH("Reference"); PH("Amount");

                                foreach (var p in model.Invoice.Payments)
                                {
                                    void PD(string text, bool right = false)
                                        => table.Cell()
                                            .BorderBottom(0.5f).BorderColor(BorderHex)
                                            .PaddingHorizontal(6).PaddingVertical(4)
                                            .Element(e => right ? e.AlignRight() : e)
                                            .Text(text).FontSize(9);

                                    PD(p.PaidAtUtc.ToString(model.DateFormat, CultureInfo.InvariantCulture));
                                    PD(p.Method);
                                    PD(p.ProviderTxnId ?? p.Notes ?? "—");
                                    PD($"{model.CurrencySymbol}{p.Amount:N2}", right: true);
                                }
                            });
                        }

                        // ── NOTES ──────────────────────────────────────────────
                        if (!string.IsNullOrWhiteSpace(model.Invoice.Notes))
                        {
                            col.Item().PaddingTop(20)
                                .Background(LightHex)
                                .Padding(10)
                                .Column(c =>
                                {
                                    c.Item().Text("Notes")
                                        .FontSize(9).Bold().FontColor(AccentHex);
                                    c.Item().PaddingTop(4)
                                        .Text(model.Invoice.Notes)
                                        .FontSize(9).FontColor(MutedHex);
                                });
                        }
                    });

                    // ── FOOTER ─────────────────────────────────────────────────
                    page.Footer().Row(row =>
                    {
                        row.RelativeItem()
                            .Text(x =>
                            {
                                x.Span("Generated by LeadFlow CRM  ·  ")
                                    .FontSize(8).FontColor(MutedHex);
                                x.Span(DateTime.UtcNow.ToString("dd/MM/yyyy HH:mm"))
                                    .FontSize(8).FontColor(MutedHex);
                            });
                        row.ConstantItem(60).AlignRight()
                            .Text(x =>
                            {
                                x.Span("Page ").FontSize(8).FontColor(MutedHex);
                                x.CurrentPageNumber().FontSize(8).FontColor(MutedHex);
                                x.Span(" of ").FontSize(8).FontColor(MutedHex);
                                x.TotalPages().FontSize(8).FontColor(MutedHex);
                            });
                    });
                });
            }).GeneratePdf();
        }
    }
}
