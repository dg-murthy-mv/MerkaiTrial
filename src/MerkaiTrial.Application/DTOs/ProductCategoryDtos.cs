// =====================================================================
// ProductCategoryDtos.cs
// Location: MerkaiTrial.Application/DTOs/ProductCategoryDtos.cs
//
// NEW FILE (068).
//
// The shapes the Settings page and the four Products pages read. One
// read DTO and four small commands — there is no "list item" variant,
// because a category has six fields and splitting them would mean two
// shapes to keep in step for no saving at all.
// =====================================================================

using System;
using System.Collections.Generic;

namespace MerkaiTrial.Application.DTOs
{
    public class ProductCategoryDto
    {
        public Guid Id { get; set; }

        /// <summary>
        /// NULL = a Merkai default. The Settings page shows these with a
        /// "Standard" badge and a note saying the first edit will take a
        /// private copy of the whole list.
        /// </summary>
        public Guid? TenantId { get; set; }

        /// <summary>This is the value stored in Product.Category.</summary>
        public string Name { get; set; } = string.Empty;

        public string Icon { get; set; } = "bi-box";
        public string Color { get; set; } = "#6b7280";
        public int SortOrder { get; set; }
        public bool IsActive { get; set; }

        /// <summary>
        /// How many live products currently carry this category NAME.
        ///
        /// Filled in by the list handler with one grouped query for the
        /// whole page, not one per row. The Settings page needs it for two
        /// different jobs and both are about not surprising anybody:
        /// warning before a rename ("14 products will be renamed with it")
        /// and refusing a silent delete ("14 products use this — where
        /// should they go?").
        /// </summary>
        public int ProductCount { get; set; }

        /// <summary>True for a Merkai default; mirrors the entity.</summary>
        public bool IsSystem => TenantId is null;

        /// <summary>
        /// False while the workspace is still following the defaults. The
        /// page uses it to decide whether a row's Edit and Delete buttons
        /// say what they do, or warn that they will adopt the list first.
        /// </summary>
        public bool IsEditable => TenantId is not null;
    }

    /// <summary>
    /// What the Settings page gets in one call: the effective list, plus
    /// the two facts the page needs to explain itself.
    /// </summary>
    public class ProductCategoryListDto
    {
        public List<ProductCategoryDto> Categories { get; set; } = new();

        /// <summary>
        /// True when this workspace owns no rows and is seeing the Merkai
        /// defaults. The first write adopts the list — see
        /// ProductCategoryResolution.
        /// </summary>
        public bool FollowingDefaults { get; set; }

        /// <summary>
        /// Products with no category at all. Product.Category is nullable
        /// and always has been, so this is information rather than a
        /// problem — but it belongs on the screen that manages categories,
        /// because it is the only place somebody would think to look.
        /// </summary>
        public int UncategorizedProductCount { get; set; }
    }

    public class CreateProductCategoryDto
    {
        public string Name { get; set; } = string.Empty;
        public string Icon { get; set; } = "bi-box";
        public string Color { get; set; } = "#6b7280";

        /// <summary>
        /// Omitted by the UI — a new category goes to the end of the list,
        /// which is what somebody adding one expects. Honoured if sent.
        /// </summary>
        public int? SortOrder { get; set; }

        public bool IsActive { get; set; } = true;
    }

    public class UpdateProductCategoryDto
    {
        /// <summary>
        /// The new name. CHANGING THIS REWRITES Product.Category ON EVERY
        /// PRODUCT CARRYING THE OLD NAME, in the same transaction — see
        /// UpdateProductCategoryHandler. The page says so before saving.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        public string Icon { get; set; } = "bi-box";
        public string Color { get; set; } = "#6b7280";
        public bool IsActive { get; set; } = true;
    }

    /// <summary>
    /// Deleting a category has to say what happens to its products, and
    /// the API will not guess.
    /// </summary>
    public class DeleteProductCategoryDto
    {
        /// <summary>
        /// Where the products go. A category NAME that must exist for this
        /// workspace, or NULL to leave them uncategorized.
        ///
        /// There is no third option. Leaving them pointing at a name with
        /// no row is exactly the orphaned state grid 3 of the migration
        /// exists to catch, and it makes the Edit page's dropdown come up
        /// blank for every one of those products.
        /// </summary>
        public string? ReassignTo { get; set; }
    }

    /// <summary>
    /// The new order, as category ids. Sent whole rather than as a pair of
    /// positions: a reorder is one atomic statement about the list, and a
    /// "move item 3 to position 5" API has to decide what happens when two
    /// people do it at once.
    /// </summary>
    public class ReorderProductCategoriesDto
    {
        public List<Guid> OrderedIds { get; set; } = new();
    }

    /// <summary>
    /// What a write gives back, so the page can say what actually happened
    /// rather than "Saved."
    /// </summary>
    public class ProductCategoryWriteResult
    {
        public ProductCategoryDto? Category { get; set; }

        /// <summary>
        /// Products whose Category string this write changed. Non-zero
        /// after a rename, or after a delete that reassigned.
        /// </summary>
        public int ProductsUpdated { get; set; }

        /// <summary>
        /// True when this write also took a private copy of the Merkai
        /// defaults. Worth telling the person once: their list has just
        /// stopped tracking ours.
        /// </summary>
        public bool AdoptedDefaults { get; set; }
    }
}
