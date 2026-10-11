// =====================================================================
// FILE: MerkaiTrial.Application/Services/Pdf/DocumentFieldsPdf.cs
//
// NEW FILE (081). The "Additional details" block — the custom fields
// switched to "Print on quotes & invoices" — drawn ONCE, here, for both
// QuotePdfService and InvoicePdfService. A quote and the invoice raised
// from it print the same block in the same place, the same way.
//
// Drawn only when there is something in it: a document with no printed
// fields looks exactly as it did before 081, with no empty heading.
//
// Two label / value pairs per row, so six fields take three lines rather
// than six. A long text value wraps inside its own cell.
// =====================================================================

using MerkaiTrial.Application.DTOs;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MerkaiTrial.Application.Services.Pdf
{
    public static class DocumentFieldsPdf
    {
        private const string MutedHex  = "#6B7280"; // gray-500, as both services
        private const string BorderHex = "#E5E7EB"; // gray-200
        private const string FillHex   = "#F9FAFB"; // gray-50

        /// <summary>True when the block has anything to draw.</summary>
        public static bool HasAny(IReadOnlyCollection<DocumentFieldEntry>? entries)
            => entries is { Count: > 0 };

        /// <summary>
        /// Draws the block into <paramref name="container"/>. Call only when
        /// <see cref="HasAny"/> is true; draws nothing otherwise.
        /// </summary>
        public static void Compose(
            IContainer container,
            IReadOnlyCollection<DocumentFieldEntry>? entries,
            CultureInfo numberCulture,
            string? dateFormat)
        {
            if (!HasAny(entries))
                return;

            var list = entries!.ToList();

            container
                .Background(FillHex)
                .Border(0.5f).BorderColor(BorderHex)
                .Padding(8)
                .Column(c =>
                {
                    c.Item().Text("ADDITIONAL DETAILS")
                        .FontSize(8).Bold().FontColor(MutedHex);

                    c.Item().PaddingTop(4).Table(t =>
                    {
                        t.ColumnsDefinition(cols =>
                        {
                            cols.RelativeColumn(1.3f);   // label
                            cols.RelativeColumn(2f);     // value
                            cols.ConstantColumn(12);     // gutter
                            cols.RelativeColumn(1.3f);   // label
                            cols.RelativeColumn(2f);     // value
                        });

                        for (var i = 0; i < list.Count; i++)
                        {
                            var e = list[i];

                            t.Cell().PaddingVertical(2).PaddingRight(4)
                                .Text(e.Label + ":").FontSize(9).FontColor(MutedHex);
                            t.Cell().PaddingVertical(2)
                                .Text(e.Format(numberCulture, dateFormat)).FontSize(9).Bold();

                            // After the left pair, the gutter; after the
                            // right pair, the row is full.
                            if (i % 2 == 0)
                                t.Cell();
                        }

                        // An odd count leaves the right half of the last
                        // row empty — fill it so the table closes cleanly.
                        if (list.Count % 2 == 1)
                        {
                            t.Cell();
                            t.Cell();
                        }
                    });
                });
        }
    }
}
