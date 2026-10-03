// =====================================================================
// ProductCategory.cs
// Location: MerkaiTrial.Domain/Entities/ProductCategory.cs
//
// NEW FILE (068).
//
// WHY THIS EXISTS
//
//   Product categories were a static C# list in
//   MerkaiTrial.Application/Configuration/ProductCategoriesConfiguration.cs:
//
//       new ProductCategory { Name = "Electronics", ... },
//       new ProductCategory { Name = "Software",    ... },
//       ... eight of them
//
//   Which means the catalogue shipped with eight guesses about what four
//   countries' worth of SMEs sell. An interiors firm has no use for
//   "Subscriptions". A tuition centre needs "Exam prep" and "Course
//   material". Neither could add one without a rebuild and a redeploy.
//
// SHARED-OR-TENANT, the same pattern Role, CompanyVertical and TaxRate
// already use:
//
//   TenantId NULL  — a Merkai default, visible to every workspace.
//   TenantId set   — owned by that workspace, invisible to the others.
//
//   The query filter in FlowDbContext is the permissive one:
//       e.TenantId == null || e.TenantId == CurrentTenantId
//
//   ...and the WRITE side does not rely on it. A filter that admits
//   system rows would let a tenant admin edit a Merkai default and
//   change it for everybody, so every update and delete checks
//   ownership explicitly — exactly the reasoning TaxRateCommandHelper
//   spells out in round 056.
//
// HOW THE EFFECTIVE LIST IS RESOLVED (ProductCategoryResolution, in
// ProductCategoriesConfiguration.cs):
//
//   A workspace that owns NO rows sees the Merkai defaults.
//   A workspace that owns ANY rows sees only its own.
//
//   One rule, and no per-category override machinery. The alternative —
//   defaults plus tenant rows that shadow them by name — needs a stable
//   key separate from the name, cannot express "rename Electronics to
//   Devices", and produces a list nobody can predict. The first time
//   somebody edits anything, the whole default list is copied in as
//   tenant-owned rows ("adoption"), and from then on their list is
//   simply theirs.
//
// WHY Product.Category IS STILL A STRING AND NOT A CategoryId
//
//   It is an nvarchar today, and the products list, the filters, the
//   quote line editor's catalogue and the stats query all read it as
//   one. A foreign key would be a migration across six read paths that
//   must not fail halfway. Renaming a category instead updates every
//   product carrying the old name, in the same transaction — see
//   UpdateProductCategoryHandler. The trade-off is that the two can
//   drift if somebody edits dbo.Products by hand, which is a cost
//   Product.Category has carried since it was introduced.
//
// Table created by Sql/068_ProductCategories.sql.
// =====================================================================

namespace MerkaiTrial.Domain.Entities
{
    public class ProductCategory
    {
        public Guid Id { get; set; }

        /// <summary>
        /// NULL = a Merkai default shared with every workspace. Set = owned
        /// by that workspace alone. Nullable is the whole point of the
        /// entity; see the header.
        /// </summary>
        public Guid? TenantId { get; set; }

        /// <summary>
        /// NVARCHAR(80). THE VALUE STORED IN Product.Category — this is the
        /// join, such as it is. Unique per workspace among live rows, and
        /// compared case-insensitively on write so "Software" and
        /// "software" cannot both exist.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// A Bootstrap Icons class, e.g. "bi-cpu". Chosen from
        /// ProductCategoryIcons in the UI rather than typed, so a class
        /// that renders as nothing cannot be saved.
        /// </summary>
        public string Icon { get; set; } = "bi-box";

        /// <summary>"#rrggbb". Validated on write; never stored blank.</summary>
        public string Color { get; set; } = "#6b7280";

        /// <summary>
        /// Display order, hand-set rather than alphabetical. The two or
        /// three categories a business actually sells belong at the top of
        /// a dropdown somebody uses forty times a day.
        /// </summary>
        public int SortOrder { get; set; }

        /// <summary>
        /// False = keep the row, stop offering it in dropdowns. A category
        /// already used on a quote cannot simply vanish: the products still
        /// carry the name and issued documents still print it.
        /// </summary>
        public bool IsActive { get; set; } = true;

        public DateTime CreatedAtUtc { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }
        public string? UpdatedBy { get; set; }
        public bool IsDeleted { get; set; }

        /// <summary>
        /// True for a Merkai default. Convenience for the screens, which
        /// show these read-only with a "Standard" badge until the workspace
        /// adopts the list.
        /// </summary>
        public bool IsSystem => TenantId is null;
    }
}
