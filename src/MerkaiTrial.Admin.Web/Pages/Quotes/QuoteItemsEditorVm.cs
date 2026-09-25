// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Quotes/QuoteItemsEditorVm.cs
//
// NEW FILE (029).
//
// WHY THIS EXISTS
//
// Create.cshtml and Edit.cshtml each carried their own copy of the
// line-item editor — roughly 300 lines of near-identical JavaScript in
// each file. They had already drifted apart, and not cosmetically:
//
//   • Create built its product-catalog buttons with JsonSerializer, which
//     escapes quotes safely. Edit built them with
//     @Html.Raw(product.Name.Replace("'", "\\'")) inside a double-quoted
//     onclick attribute — so a product named  Widget "Pro"  closed the
//     attribute early and put whatever followed into the markup, and a
//     product with a NULL name threw an NRE and 500'd the page.
//
//   • Create normalised a new line's tax rate from the tenant; Edit had a
//     second normaliser with slightly different rules.
//
//   • Create's submit read item.dbId (never set anywhere, so always null);
//     Edit's read item.itemId (correct).
//
// Two copies of the same 300 lines will drift again. This view model plus
// _QuoteItemsEditor.cshtml is the one copy both pages render.
// =====================================================================

using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Admin.Web.Pages.Quotes
{
    /// <summary>
    /// Everything _QuoteItemsEditor.cshtml needs. Build it in the page and
    /// pass it to the partial; the partial reads nothing off the page model,
    /// so Create and Edit cannot diverge.
    /// </summary>
    public sealed class QuoteItemsEditorVm
    {
        /// <summary>id of the &lt;form&gt; this editor posts with.</summary>
        public string FormId { get; init; } = "quoteForm";

        /// <summary>ISO code shown beside the totals, e.g. "INR".</summary>
        public string CurrencyCode { get; init; } = string.Empty;

        /// <summary>
        /// Default tax rate for a NEW line, as a PERCENTAGE (7 for Thai VAT,
        /// 18 for Indian GST). Both page models already normalise the tenant
        /// value to a percentage before handing it over.
        /// </summary>
        public decimal DefaultTaxRate { get; init; }

        /// <summary>Label for the tax column, e.g. "GST" or "VAT".</summary>
        public string TaxLabel { get; init; } = "Tax";

        /// <summary>{ enabled, maxDiscountPercent, maxQuoteTotal, exempt } — already JSON.</summary>
        public string ApprovalRulesJson { get; init; } = "{\"enabled\":false}";

        /// <summary>Catalogue to pick from. May be empty.</summary>
        public List<ProductListItem> Products { get; init; } = new();

        /// <summary>
        /// Lines already on the quote, as a JSON array. "[]" on Create.
        /// Each element: { itemId, productId, name, description, quantity,
        /// unitPrice, lineDiscount, taxRate } with taxRate as a PERCENTAGE.
        /// </summary>
        public string InitialItemsJson { get; init; } = "[]";

        /// <summary>productId → catalogue list price, for the discount maths.</summary>
        public string ListPricesJson { get; init; } = "{}";

        /// <summary>
        /// True on Create, where an empty quote makes no sense, so the editor
        /// opens with one blank line ready to type into. False on Edit, where
        /// adding a phantom line to an existing quote would be wrong.
        /// </summary>
        public bool StartWithOneBlankLine { get; init; }
    }
}
