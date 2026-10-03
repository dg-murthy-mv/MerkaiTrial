// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Products/PriceGridVm.cs
//
// NEW FILE (069).
//
// Everything _PriceGrid.cshtml needs. Built in the page and passed to
// the partial; the partial reads nothing off the page model, so Create
// and Edit cannot diverge in what the grid accepts.
//
// Same arrangement as QuoteItemsEditorVm, and for the same reason that
// file sets out at length: two copies of one editor drift, and one of
// the copies ends up with a hole the other does not have.
// =====================================================================

using System.Text.Json;
using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Admin.Web.Pages.Products
{
    public sealed class PriceGridVm
    {
        /// <summary>
        /// id of the &lt;form&gt; this grid posts with. The grid hooks
        /// that form's submit event for one last sync.
        /// </summary>
        public string FormId { get; init; } = "productForm";

        /// <summary>
        /// The workspace's own currency code. EXCLUDED from the grid's
        /// dropdown: the home price is the main price field above it, and
        /// the API refuses a row in this currency.
        /// </summary>
        public string HomeCurrencyCode { get; init; } = string.Empty;

        /// <summary>
        /// The prices already on the product. Empty on Create.
        /// </summary>
        public List<ProductPriceDto> Prices { get; init; } = new();

        /// <summary>
        /// The name of the page handler parameter the grid posts into —
        /// "pricesJson" on both pages. Bound by NAME rather than with
        /// asp-for, because asp-for would derive it from THIS view model
        /// and post something no handler takes.
        /// </summary>
        public string PricesJsonFieldName { get; init; } = "pricesJson";

        /// <summary>
        /// The seeded prices as a JSON array, used for BOTH the hidden
        /// field's initial value and the script's starting rows — one
        /// serialisation, so the two can never begin out of step.
        ///
        /// NEVER "[]" when the product HAS prices. If the grid's script
        /// fails to run for any reason, an "[]" default would post
        /// "no other currencies" and delete every price on the product
        /// as a side effect of saving its name.
        /// </summary>
        public string PricesJson =>
            JsonSerializer.Serialize(
                Prices.Select(p => new
                {
                    currencyCode = p.CurrencyCode,
                    listPrice    = p.ListPrice
                }));
    }
}
