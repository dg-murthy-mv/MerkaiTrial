// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Shared/DocumentFieldComponents.cs
//
// NEW FILE (081 — custom fields on quotes and invoices).
//
// Two small shared pieces, used by the quote AND invoice pages so the two
// cannot drift (the 029 lesson the 075 CustomFieldComponents header tells):
//
//   DocumentFieldsVm      the model for _DocumentFieldsCard.cshtml — the
//                         "Printed on the quote / invoice" card on both
//                         Detail pages: exactly what the PDF and the
//                         customer's link print, and whether that is the
//                         frozen copy or a preview.
//
//   CustomFieldColumns    the list pages' custom columns: which fields,
//                         the formatted value, and the text the quick
//                         search box matches on.
//
//   DocumentCulture.LineText (082) — a line's product fields as one short
//                         line under the item name, in the document's own
//                         number format, on every screen that lists lines.
//
//   DocumentCulture       the number format a DOCUMENT uses — chosen by
//                         the document's currency, exactly as the two PDF
//                         services choose it — so a screen that says "this
//                         is what prints" groups digits the way the PDF
//                         does (1,25,000.00 on an INR quote).
// =====================================================================

using System.Globalization;
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Admin.Web.Pages.Shared
{
    /// <summary>Model for _DocumentFieldsCard.cshtml.</summary>
    public class DocumentFieldsVm
    {
        /// <summary>What the document prints, in order (QuoteDto/InvoiceDto.DocumentFields).</summary>
        public List<DocumentFieldEntry> Fields { get; set; } = new();

        /// <summary>When the printed copy was frozen. Null = live values.</summary>
        public DateTime? FrozenAtUtc { get; set; }

        /// <summary>
        /// True when the document is with the customer — a sent quote, an
        /// issued invoice. With FrozenAtUtc null that means it went out
        /// before 081 and prints its current values.
        /// </summary>
        public bool IsOut { get; set; }

        /// <summary>"quote" or "invoice".</summary>
        public string Noun { get; set; } = "quote";

        /// <summary>"sent" or "issued" — the moment the copy is frozen.</summary>
        public string FreezeVerb { get; set; } = "sent";

        /// <summary>Numbers in the document's currency culture, like the PDF.</summary>
        public CultureInfo Culture { get; set; } = CultureInfo.InvariantCulture;

        public string DateFormat { get; set; } = "dd/MM/yyyy";

        /// <summary>The freeze time as the page shows times ("12 Oct 2026, 14:05"). Set by the page.</summary>
        public string? FrozenAtDisplay { get; set; }

        /// <summary>settings.read — shows "Choose what prints".</summary>
        public bool CanConfigure { get; set; }

        /// <summary>"Quote" / "Invoice" — for the Settings link's ?entity=.</summary>
        public string EntityType { get; set; } = CustomFieldEntityTypes.Quote;

        public bool IsFrozen => FrozenAtUtc.HasValue;
    }

    /// <summary>
    /// The number culture of a quote or invoice, by its currency — the same
    /// choice QuotePdfService / InvoicePdfService make. INR, PHP, THB and
    /// AED have their own grouping; every other currency the PDFs know
    /// groups exactly as InvariantCulture does (1,234,567.89), so that is
    /// what they get here.
    /// </summary>
    public static class DocumentCulture
    {
        public static CultureInfo For(string? currencyCode) => (currencyCode ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "INR" => CultureInfo.GetCultureInfo("en-IN"),   // lakh grouping
            "PHP" => CultureInfo.GetCultureInfo("en-PH"),
            "THB" => CultureInfo.GetCultureInfo("th-TH"),
            "AED" => CultureInfo.GetCultureInfo("en-AE"),
            _     => CultureInfo.InvariantCulture
        };

        /// <summary>
        /// 082. A line's printed product fields as one line of text —
        /// "Warranty: 2 years  ·  Colour: Red" — numbers in the document's
        /// currency format (as its PDF), dates in the tenant's format. Empty
        /// when the line prints none.
        /// </summary>
        public static string LineText(IEnumerable<DocumentFieldEntry>? entries, string? currencyCode, string? dateFormat)
            => DocumentFieldEntry.JoinForLine(entries, For(currencyCode), dateFormat);
    }

    /// <summary>The custom columns on the quote and invoice lists.</summary>
    public static class CustomFieldColumns
    {
        /// <summary>The active "Show on the list" fields, in order, capped like every other list.</summary>
        public static List<CustomFieldDefinitionDto> Pick(IEnumerable<CustomFieldDefinitionDto> fields)
            => fields
                .Where(f => f.IsActive && f.ShowInList)
                .OrderBy(f => f.SortOrder)
                .Take(CustomFieldLimits.MaxListColumns)
                .ToList();

        /// <summary>One cell's text. Empty when unset; an unticked checkbox is empty too.</summary>
        public static string Value(
            CustomFieldDefinitionDto field, IDictionary<Guid, string>? values, CultureInfo culture, string dateFormat)
        {
            string? wire = null;
            values?.TryGetValue(field.Id, out wire);

            if (field.FieldType == CustomFieldTypes.Checkbox)
                return wire == "true" ? "Yes" : string.Empty;

            return CustomFieldFormatter.Display(field, wire, culture, dateFormat);
        }

        /// <summary>
        /// The column values as one lower-case string, appended to a row's
        /// data-find so the list's quick search finds "PO-4471" too.
        /// Checkbox columns are left out — "yes" matching half the list
        /// would be noise.
        /// </summary>
        public static string SearchText(
            IEnumerable<CustomFieldDefinitionDto> columns, IDictionary<Guid, string>? values,
            CultureInfo culture, string dateFormat)
            => string.Join(" ", columns
                    .Where(c => c.FieldType != CustomFieldTypes.Checkbox)
                    .Select(c => Value(c, values, culture, dateFormat))
                    .Where(s => s.Length > 0))
                .ToLowerInvariant();
    }
}
