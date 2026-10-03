// =====================================================================
// ProductCategoryService.cs
// Location: MerkaiTrial.Admin.Web/Services/Products/ProductCategoryService.cs
//
// NEW FILE (068).
//
// Thin wrapper over /api/product-categories, in the same shape as
// ProductService and QuoteService next door. No tenantId parameters
// anywhere: that controller reads the workspace from the signed token,
// unlike /api/products which still takes it in the query string.
//
// ONE PIECE OF REAL LOGIC LIVES HERE: ProductCategoryLookup, at the
// bottom. The four Products pages need "what icon and colour goes with
// this category name", they each used to answer it from a static C#
// list, and after this round the answer comes from data that can be
// missing — a product can carry a category somebody removed. The lookup
// is the one place that falls back, so a deleted category shows a grey
// box on every page instead of three different things.
//
// Errors are deliberately NOT swallowed on the write paths. IApiService
// turns a 400 with { "error": "..." } into an
// InvalidOperationException carrying the message, and those messages are
// written for the person: "There is already a category called
// \"Hardware\"." Catching them here would replace that with silence and
// a page that looks like it saved.
//
// Removal goes out as POST {id}/remove rather than DELETE {id}, because
// the destination for the orphaned products is part of the instruction
// and therefore needs a body. ProductCategoriesController.Remove sets
// out the full reasoning.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Core;
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Admin.Web.Services.Products
{
    public interface IProductCategoryService
    {
        /// <summary>
        /// The workspace's effective category list.
        ///
        /// <paramref name="includeInactive"/> true is for the Settings
        /// page, which has to show a switched-off category in order to
        /// switch it back on. Everything else wants the active ones.
        /// </summary>
        Task<ProductCategoryListDto> GetAllAsync(bool includeInactive = false);

        /// <summary>
        /// Name → icon and colour, for the pages that render a product.
        /// Never throws and never returns null — see ProductCategoryLookup.
        /// </summary>
        Task<ProductCategoryLookup> GetLookupAsync();

        Task<ProductCategoryWriteResult> CreateAsync(CreateProductCategoryDto dto);
        Task<ProductCategoryWriteResult> UpdateAsync(Guid id, UpdateProductCategoryDto dto);
        Task<ProductCategoryWriteResult> DeleteAsync(Guid id, string? reassignTo);
        Task<int> ReorderAsync(List<Guid> orderedIds);
    }

    public class ProductCategoryService : IProductCategoryService
    {
        private readonly IApiService _apiService;
        private readonly ILogger<ProductCategoryService> _logger;

        public ProductCategoryService(
            IApiService apiService,
            ILogger<ProductCategoryService> logger)
        {
            _apiService = apiService;
            _logger     = logger;
        }

        public async Task<ProductCategoryListDto> GetAllAsync(bool includeInactive = false)
        {
            try
            {
                var url = $"api/product-categories?includeInactive={includeInactive.ToString().ToLowerInvariant()}";
                return await _apiService.GetAsync<ProductCategoryListDto>(url)
                       ?? new ProductCategoryListDto();
            }
            catch (Exception ex)
            {
                // A READ, so it degrades instead of throwing. The Settings
                // page shows an empty list and its own error banner; a
                // products page that threw because the category service
                // was unreachable would be a worse outcome than grey
                // icons.
                _logger.LogError(ex, "Failed to load product categories");
                return new ProductCategoryListDto();
            }
        }

        public async Task<ProductCategoryLookup> GetLookupAsync()
        {
            var list = await GetAllAsync(includeInactive: true);
            return new ProductCategoryLookup(list.Categories);
        }

        public async Task<ProductCategoryWriteResult> CreateAsync(CreateProductCategoryDto dto)
            => await _apiService.PostAsync<ProductCategoryWriteResult>("api/product-categories", dto)
               ?? new ProductCategoryWriteResult();

        public async Task<ProductCategoryWriteResult> UpdateAsync(Guid id, UpdateProductCategoryDto dto)
            => await _apiService.PutAsync<ProductCategoryWriteResult>($"api/product-categories/{id}", dto)
               ?? new ProductCategoryWriteResult();

        /// <summary>
        /// Remove a category and say where its products go. NULL leaves
        /// them uncategorized, which is legal — Product.Category has
        /// always been nullable.
        ///
        /// POST, not DELETE, and the controller's own comment explains
        /// why: the destination is part of the instruction, so the call
        /// needs a body, and a category name in a query string would end
        /// up in the request log.
        /// </summary>
        public async Task<ProductCategoryWriteResult> DeleteAsync(Guid id, string? reassignTo)
            => await _apiService.PostAsync<ProductCategoryWriteResult>(
                   $"api/product-categories/{id}/remove",
                   new DeleteProductCategoryDto { ReassignTo = reassignTo })
               ?? new ProductCategoryWriteResult();

        public async Task<int> ReorderAsync(List<Guid> orderedIds)
        {
            var result = await _apiService.PutAsync<ReorderResponse>(
                "api/product-categories/order",
                new ReorderProductCategoriesDto { OrderedIds = orderedIds ?? new List<Guid>() });

            return result?.Moved ?? 0;
        }

        private sealed class ReorderResponse
        {
            public int Moved { get; set; }
        }
    }

    // =================================================================
    // THE LOOKUP
    // =================================================================

    /// <summary>
    /// Name → icon and colour for the pages that render products.
    ///
    /// WHY IT FALLS BACK RATHER THAN FAILING
    ///
    ///   Product.Category is a string and categories can be removed, so a
    ///   product can perfectly well carry a name with no row behind it:
    ///   deleted on the Settings page while its products were reassigned
    ///   in another tab, imported before round 068, or edited straight in
    ///   the database. A products list that threw on one such row would
    ///   take out the whole page.
    ///
    ///   Order of answers, for a name:
    ///     1. the workspace's own row, if there is one;
    ///     2. the Merkai default of that name, via
    ///        ProductCategoriesConfiguration — so "Hardware" keeps its
    ///        indigo CPU icon even after somebody removes the row;
    ///     3. a grey box.
    ///
    ///   Built once per request by the page, not per row: a products list
    ///   of fifty rows asking fifty times would be fifty dictionary
    ///   lookups, which is fine, but fifty API calls would not be.
    /// </summary>
    public sealed class ProductCategoryLookup
    {
        private readonly Dictionary<string, ProductCategoryDto> _byName;

        public ProductCategoryLookup(IEnumerable<ProductCategoryDto>? categories)
        {
            _byName = new Dictionary<string, ProductCategoryDto>(StringComparer.OrdinalIgnoreCase);

            foreach (var c in categories ?? Enumerable.Empty<ProductCategoryDto>())
                if (!string.IsNullOrWhiteSpace(c.Name))
                    _byName[c.Name.Trim()] = c;
        }

        /// <summary>The names, in display order, for a dropdown.</summary>
        public List<string> ActiveNames =>
            _byName.Values
                .Where(c => c.IsActive)
                .OrderBy(c => c.SortOrder)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(c => c.Name)
                .ToList();

        public string IconFor(string? categoryName)
        {
            if (string.IsNullOrWhiteSpace(categoryName))
                return ProductCategoriesConfiguration.FallbackIcon;

            return _byName.TryGetValue(categoryName.Trim(), out var c)
                ? c.Icon
                : ProductCategoriesConfiguration.GetCategoryIcon(categoryName);
        }

        public string ColorFor(string? categoryName)
        {
            if (string.IsNullOrWhiteSpace(categoryName))
                return ProductCategoriesConfiguration.FallbackColor;

            return _byName.TryGetValue(categoryName.Trim(), out var c)
                ? c.Color
                : ProductCategoriesConfiguration.GetCategoryColor(categoryName);
        }

        /// <summary>
        /// True when this product's category no longer has a row. The
        /// products list uses it for a quiet "no longer a category" title
        /// on the icon — visible if you look, invisible if you don't, and
        /// the only hint anybody would otherwise get is a grey box.
        /// </summary>
        public bool IsOrphaned(string? categoryName)
            => !string.IsNullOrWhiteSpace(categoryName) &&
               !_byName.ContainsKey(categoryName.Trim());
    }
}
