// =====================================================================
// FILE: MerkaiTrial.Application/Services/Pdf/QuotePdfService.cs
// PACKAGE: QuestPDF (already installed for InvoicePdfService)
// SETUP:  In WebApi/Program.cs add:
//   builder.Services.AddScoped<IQuotePdfService, QuotePdfService>();
//   (QuestPDF.Settings.License = LicenseType.Community already set)
//
// COMPLETE FILE — 055. The Discount column prints the PERCENTAGE when the
// line was agreed as one — "-10%" with the amount beneath it — because that
// is what the customer was told on the phone, and a quote that answers back
// with a bare figure makes them do the division to check it.
//
// 053. One change outside the line-items table: the
// QUOTE TO block prints the customer's COMPANY in bold with the CONTACT
// under it, the way InvoicePdfService's BILL TO already did. QuoteDto had
// no ContactName field until 053, which is why the company line was being
// used for a person's name.
//
// 052b. Three changes, all in the line-items table.
//
// 1. QUANTITY. Was item.Quantity.ToString(). Quantity is DECIMAL(18,4)
//    since 052, so that prints "3.0000" on the document the customer
//    keeps. QuantityDisplay gives "3", "12.5 m²", "3.5 days" — the
//    trailing zeros trimmed and the unit attached, worked out once on
//    the DTO so this PDF, the quote page and the invoice page cannot
//    each trim them differently.
//
// 2. THE TAX COLUMN WAS ROUNDING THE RATE. "N0" on (TaxRate * 100)
//    turns 12.5% into "13%" and 7.5% into "8%" — on a document a
//    customer may hand to their accountant, beside a tax AMOUNT
//    calculated from the real rate, so the two visibly disagree. It is
//    "0.##" now: 18 stays "18", 12.5 stays "12.5". It also formats with
//    the tenant's culture like every other number on the page, instead
//    of whatever culture the server thread happened to have.
//
// 3. The Qty column was 1 relative unit, sized for "12". "12.5 m²" does
//    not fit in it. Widened, taking the space from the Item column,
//    which has the most to spare.
// =====================================================================

using MerkaiTrial.Application.DTOs;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System.Globalization;

namespace MerkaiTrial.Application.Services.Pdf
{
    public interface IQuotePdfService
    {
        /// <summary>Generate PDF bytes for a quote. Stream to client or save to disk.</summary>
        byte[] Generate(QuotePdfModel model);
    }

    public class QuotePdfService : IQuotePdfService
    {
        // ── Currency → CultureInfo (same mapping as InvoicePdfService) ─
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

        // ── Brand colours (matches InvoicePdfService) ──────────────────
        private static readonly string AccentHex  = "#4F46E5"; // indigo-600
        private static readonly string LightHex   = "#EEF2FF"; // indigo-50
        private static readonly string MutedHex   = "#6B7280"; // gray-500
        private static readonly string BorderHex  = "#E5E7EB"; // gray-200
        private static readonly string DangerHex  = "#DC2626"; // red-600

        public byte[] Generate(QuotePdfModel model)
        {
            var culture = GetCultureForCurrency(model.CurrencyCode);
            var sym     = model.CurrencySymbol;

            return Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(40);
                    page.DefaultTextStyle(x => x.FontSize(10).FontFamily("Arial"));

                    page.Content().Column(col =>
                    {
                        // ── HEADER ──────────────────────────────────────────────
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

                            // Right: QUOTE label + number + status
                            row.ConstantItem(200).Column(c =>
                            {
                                c.Item().AlignRight()
                                    .Text("QUOTE")
                                    .FontSize(26).Bold()
                                    .FontColor(AccentHex);

                                c.Item().AlignRight()
                                    .Text(model.Quote.Number)
                                    .FontSize(13).Bold();

                                var statusColor = model.Quote.Status switch
                                {
                                    "Accepted" => "#16A34A",
                                    "Rejected" => DangerHex,
                                    "Expired"  => "#D97706",
                                    "Sent"     => "#2563EB",
                                    "Viewed"   => "#0891B2",
                                    "Revised"  => "#1F2937",
                                    _          => AccentHex    // Draft
                                };

                                c.Item().AlignRight().PaddingTop(4)
                                    .Background(statusColor)
                                    .PaddingHorizontal(8).PaddingVertical(3)
                                    .Text(model.Quote.Status.ToUpperInvariant())
                                    .FontSize(8).Bold().FontColor("#FFFFFF");
                            });
                        });

                        col.Item().PaddingVertical(12)
                            .LineHorizontal(1.5f).LineColor(AccentHex);

                        // ── QUOTE TO + DATES ────────────────────────────────────
                        col.Item().Row(row =>
                        {
                            // Quote To (contact / company)
                            row.RelativeItem().Column(c =>
                            {
                                c.Item().Text("QUOTE TO")
                                    .FontSize(8).Bold().FontColor(MutedHex);

                                // 053: company first, contact underneath. When
                                // there is no company — a person-to-person sale —
                                // the contact takes the bold line rather than the
                                // heading sitting above an empty space.
                                var toCompany = model.Quote.CompanyName;
                                var toContact = model.Quote.ContactName;
                                var hasCompany = !string.IsNullOrWhiteSpace(toCompany);

                                c.Item().PaddingTop(4)
                                    .Text(hasCompany ? toCompany
                                          : (string.IsNullOrWhiteSpace(toContact) ? "—" : toContact))
                                    .Bold().FontSize(11);

                                if (hasCompany && !string.IsNullOrWhiteSpace(toContact))
                                    c.Item().Text(toContact)
                                        .FontSize(9).FontColor(MutedHex);

                                if (!string.IsNullOrEmpty(model.Quote.DealTitle))
                                    c.Item().PaddingTop(2)
                                        .Text($"Re: {model.Quote.DealTitle}")
                                        .FontSize(9).FontColor(MutedHex);

                                if (!string.IsNullOrEmpty(model.Quote.VerticalName))
                                    c.Item().PaddingTop(1)
                                        .Text(model.Quote.VerticalName)
                                        .FontSize(9).FontColor(MutedHex);
                            });

                            // Dates
                            row.ConstantItem(200).Column(c =>
                            {
                                void DateRow(string label, string value)
                                {
                                    c.Item().PaddingBottom(4).Row(r =>
                                    {
                                        r.RelativeItem()
                                            .Text(label)
                                            .FontSize(9).FontColor(MutedHex);
                                        r.ConstantItem(110).AlignRight()
                                            .Text(value)
                                            .FontSize(9).Bold();
                                    });
                                }

                                DateRow("Issue Date:",
                                    model.Quote.IssueDateUtc.ToString(model.DateFormat, CultureInfo.InvariantCulture));

                                var isExpired  = model.Quote.ExpiresAtUtc < DateTime.UtcNow
                                                 && model.Quote.Status is "Draft" or "Sent" or "Viewed";
                                var expiryColor = isExpired ? DangerHex : "#000000";

                                c.Item().PaddingBottom(4).Row(r =>
                                {
                                    r.RelativeItem()
                                        .Text("Valid Until:")
                                        .FontSize(9).FontColor(MutedHex);
                                    r.ConstantItem(110).AlignRight()
                                        .Text(model.Quote.ExpiresAtUtc.ToString(model.DateFormat, CultureInfo.InvariantCulture))
                                        .FontSize(9).Bold().FontColor(expiryColor);
                                });

                                c.Item().PaddingBottom(4).Row(r =>
                                {
                                    r.RelativeItem()
                                        .Text("Currency:")
                                        .FontSize(9).FontColor(MutedHex);
                                    r.ConstantItem(110).AlignRight()
                                        .Text(model.Quote.Currency)
                                        .FontSize(9).Bold();
                                });
                            });
                        });

                        col.Item().PaddingVertical(14);

                        // ── LINE ITEMS TABLE ────────────────────────────────────
                        col.Item().Table(table =>
                        {
                            table.ColumnsDefinition(cols =>
                            {
                                // 052b: Qty 1 -> 2, taken off Name/Description.
                                // A quantity is "12.5 m²" now, not "12".
                                cols.RelativeColumn(3.4f); // Name/Description
                                cols.RelativeColumn(2);    // Qty
                                cols.RelativeColumn(2);    // Unit Price
                                cols.RelativeColumn(2);    // Discount
                                cols.RelativeColumn(1.2f); // Tax %
                                cols.RelativeColumn(2);    // Total
                            });

                            // Header row
                            void HeaderCell(string text, bool alignRight = false)
                                => table.Cell()
                                    .Background(AccentHex)
                                    .PaddingHorizontal(6).PaddingVertical(5)
                                    .Element(e => alignRight ? e.AlignRight() : e)
                                    .Text(text).FontSize(8.5f).Bold().FontColor("#FFFFFF");

                            HeaderCell("Item");
                            HeaderCell("Qty",       alignRight: true);
                            HeaderCell("Unit Price", alignRight: true);
                            HeaderCell("Discount",   alignRight: true);
                            // 052b: the tenant's own word for it — GST, VAT — the
                            // same label the totals block below already uses. The
                            // header said "Tax" while the total said "GST".
                            HeaderCell(model.TaxLabel, alignRight: true);
                            HeaderCell("Total",      alignRight: true);

                            // Data rows
                            var isOdd = false;
                            foreach (var item in model.Quote.Items)
                            {
                                isOdd = !isOdd;
                                var rowBg = isOdd ? "#FFFFFF" : "#F9FAFB";

                                void DataCell(string text, bool alignRight = false,
                                              bool bold = false, string? color = null)
                                {
                                    var cell = table.Cell()
                                        .Background(rowBg)
                                        .BorderBottom(0.5f).BorderColor(BorderHex)
                                        .PaddingHorizontal(6).PaddingVertical(5)
                                        .Element(e => alignRight ? e.AlignRight() : e)
                                        .Text(text)
                                        .FontSize(9)
                                        .FontColor(color ?? "#000000");
                                    if (bold) cell.Bold();
                                }

                                // Name + description stacked in first cell
                                table.Cell()
                                    .Background(rowBg)
                                    .BorderBottom(0.5f).BorderColor(BorderHex)
                                    .PaddingHorizontal(6).PaddingVertical(5)
                                    .Column(c =>
                                    {
                                        c.Item().Text(item.Name).FontSize(9).Bold();
                                        if (!string.IsNullOrWhiteSpace(item.Description))
                                            c.Item().Text(item.Description)
                                                .FontSize(8).FontColor(MutedHex);
                                    });

                                // 052b — see the header. "12.5 m²", not "12.5000".
                                DataCell(item.QuantityDisplay, alignRight: true);
                                DataCell($"{sym}{item.UnitPrice.ToString("N2", culture)}", alignRight: true);
                                // 055: percentage first when there was one.
                                var discText = item.LineDiscount <= 0
                                    ? "—"
                                    : item.DiscountLabel is not null
                                        ? $"{item.DiscountLabel}\n{sym}{item.LineDiscount.ToString("N2", culture)}"
                                        : $"-{sym}{item.LineDiscount.ToString("N2", culture)}";

                                DataCell(discText,
                                    alignRight: true, color: item.LineDiscount > 0 ? "#16A34A" : MutedHex);
                                // 052b: "0.##" not "N0". N0 rounded 12.5% to "13%"
                                // next to a tax amount worked out from the real rate.
                                DataCell($"{(item.TaxRate * 100).ToString("0.##", culture)}%",
                                    alignRight: true, color: MutedHex);
                                DataCell($"{sym}{item.LineGrandTotal.ToString("N2", culture)}",
                                    alignRight: true, bold: true);
                            }
                        });

                        // ── TOTALS ──────────────────────────────────────────────
                        col.Item().PaddingTop(12).Row(row =>
                        {
                            row.RelativeItem(); // spacer

                            row.ConstantItem(240).Column(totals =>
                            {
                                void TotalRow(string label, decimal amount,
                                              bool bold = false, string? color = null,
                                              bool separator = false)
                                {
                                    if (separator)
                                        totals.Item().LineHorizontal(0.5f).LineColor(BorderHex);

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

                                TotalRow("Subtotal:", model.Quote.Subtotal);

                                if (model.Quote.DiscountTotal > 0)
                                    TotalRow("Discount:", -model.Quote.DiscountTotal,
                                             color: "#16A34A");

                                TotalRow($"{model.TaxLabel}:", model.Quote.TaxTotal);

                                totals.Item().PaddingVertical(4)
                                    .LineHorizontal(1.5f).LineColor(AccentHex);

                                totals.Item().PaddingVertical(3).Row(r =>
                                {
                                    r.RelativeItem()
                                        .Text("Grand Total:")
                                        .FontSize(12).Bold().FontColor(AccentHex);
                                    r.ConstantItem(100).AlignRight()
                                        .Text($"{sym}{model.Quote.GrandTotal.ToString("N2", culture)}")
                                        .FontSize(13).Bold().FontColor(AccentHex);
                                });
                            });
                        });

                        // ── VALIDITY NOTICE ─────────────────────────────────────
                        col.Item().PaddingTop(20)
                            .Background(LightHex)
                            .Padding(10)
                            .Column(c =>
                            {
                                c.Item().Text("Terms & Validity")
                                    .FontSize(9).Bold().FontColor(AccentHex);

                                c.Item().PaddingTop(4)
                                    .Text($"This quote is valid until {model.Quote.ExpiresAtUtc.ToString(model.DateFormat, CultureInfo.InvariantCulture)}. " +
                                          $"Prices are subject to change after the validity period. " +
                                          $"All amounts are in {model.Quote.Currency}.")
                                    .FontSize(9).FontColor(MutedHex);
                            });
                    });

                    // ── FOOTER ───────────────────────────────────────────────────
                    page.Footer().Row(row =>
                    {
                        row.RelativeItem()
                            .Text(x =>
                            {
                                x.Span("Generated by Merkai CRM  ·  ")
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
