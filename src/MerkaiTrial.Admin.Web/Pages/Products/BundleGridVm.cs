// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Products/BundleGridVm.cs
//
// NEW FILE (070).
//
// Everything _BundleGrid.cshtml needs. Built in the page and passed to
// the partial; the partial reads nothing off the page model, so Create
// and Edit cannot diverge in what the bundle grid accepts.
//
// Same arrangement as PriceGridVm (069) and QuoteItemsEditorVm (029),
// and for the reason that last file sets out at length: two copies of
// one editor drift, and one of the copies ends up with a hole the other
// does not have.
// =====================================================================

using System.Text.Json;
using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Admin.Web.Pages.Products
{
    public sealed class BundleGridVm
    {
        /// <summary>
        /// id of the &lt;form&gt; this grid posts with. The grid hooks
        /// that form's submit event for one last sync.
        /// </summary>
        public string FormId { get; init; } = "productForm";

        /// <summary>
        /// The product being edited, or Guid.Empty on Create. EXCLUDED
        /// from the component picker: a bundle cannot contain itself, and
        /// offering it would be offering a mistake.
        /// </summary>
        public Guid SelfProductId { get; init; }

        /// <summary>
        /// The components already on the bundle. Empty on Create.
        /// </summary>
        public List<ProductBundleItemDto> Items { get; init; } = new();

        /// <summary>
        /// Everything that can go IN a bundle: this workspace's products
        /// except itself and except other bundles.
        ///
        /// The page filters them, not the partial, because the page is
        /// what knows how to call the product service — but the partial
        /// is what renders them, so the filtering rule is written down in
        /// both places and the page's version is authoritative.
        /// </summary>
        public List<ProductListItem> Choosable { get; init; } = new();

        /// <summary>The workspace's currency symbol, for the rollup hint.</summary>
        public string CurrencySymbol { get; init; } = string.Empty;

        /// <summary>
        /// The bundle's OWN price, as the form currently holds it, so the
        /// hint can say how far below the components' total it sits.
        /// Updated live in the browser as the price field changes.
        /// </summary>
        public decimal BundlePrice { get; init; }

        /// <summary>
        /// The name of the page handler parameter the grid posts into —
        /// "bundleJson" on both pages. Bound by NAME rather than with
        /// asp-for, because asp-for would derive it from THIS view model
        /// and post something no handler takes.
        /// </summary>
        public string ItemsJsonFieldName { get; init; } = "bundleJson";

        /// <summary>
        /// The seeded components as a JSON array, used for BOTH the
        /// hidden field's initial value and the script's starting rows —
        /// one serialisation, so the two can never begin out of step.
        ///
        /// NEVER "[]" when the bundle HAS components. If the grid's
        /// script fails to run, an "[]" default would post an empty
        /// bundle — which the API refuses outright with "A bundle has to
        /// contain something", so the save fails loudly rather than
        /// quietly emptying the bundle. That refusal is the backstop;
        /// seeding properly is the fix.
        /// </summary>
        public string ItemsJson =>
            JsonSerializer.Serialize(
                Items.Select(i => new
                {
                    componentProductId = i.ComponentProductId,
                    quantity           = i.Quantity
                }));
    }
}
