// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Settings/ProductCategories/Index.cshtml.cs
//
// NEW FILE (068). The categories a workspace files its products under.
//
// ONE PAGE, NOT Index + Create + Edit. A category has four fields —
// name, icon, colour, on/off — and a workspace has perhaps a dozen of
// them. Three pages and two round trips to rename one would be worse
// than a list with an inline form, and this is a screen somebody visits
// twice a year.
//
// MODULE: Settings, same as Tax Rates and Company Profile. Changing the
// category list reshapes every product dropdown in the workspace; it is
// not something every salesperson who can add a product should be able
// to do.
//
// ─────────────────────────────────────────────────────────────────────
// THE TWO THINGS THIS PAGE HAS TO EXPLAIN WELL, BECAUSE THEY SURPRISE
// PEOPLE
//
//   1. ADOPTION. A new workspace is looking at the Merkai defaults,
//      which are shared rows. The first edit of any kind quietly takes a
//      private copy of the whole list. The banner says so BEFORE anybody
//      clicks, and the success message says it happened — because from
//      that moment a default Merkai adds later will not appear here, and
//      nobody should discover that months afterwards.
//
//   2. RENAMING CASCADES. Product.Category holds the NAME, so renaming
//      "Hardware" to "Equipment" rewrites it on every product filed
//      under it. The edit form shows the count before saving, and the
//      success message reports how many moved.
//
// Both are handled by the API; this page's job is to make sure neither
// is a surprise.
// ─────────────────────────────────────────────────────────────────────
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Products;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;

namespace MerkaiTrial.Admin.Web.Pages.Settings.ProductCategories
{
    public class IndexModel : AuthorizedPageModel
    {
        private readonly IProductCategoryService _categories;

        public IndexModel(
            IAuthorizationService authorizationService,
            ICurrentUserService currentUserService,
            ILogger<IndexModel> logger,
            IProductCategoryService categories)
            : base(authorizationService, currentUserService, logger)
        {
            _categories = categories;
        }

        protected override string ModuleName => Modules.Settings;

        // ── What the page renders ─────────────────────────────────────
        public List<ProductCategoryDto> Categories { get; private set; } = new();

        /// <summary>
        /// True while this workspace is still following the Merkai
        /// defaults. Drives the banner and the "Standard" badges.
        /// </summary>
        public bool FollowingDefaults { get; private set; }

        public int UncategorizedProductCount { get; private set; }

        /// <summary>The icons the picker offers. Curated — see ProductCategoryIcons.</summary>
        public IReadOnlyList<string> Icons => ProductCategoryIcons.All;

        /// <summary>The colour swatches. Free hex is accepted too.</summary>
        public IReadOnlyList<string> Swatches => ProductCategoryColors.Swatches;

        public int NameMaxLength => ProductCategoriesConfiguration.NameMaxLength;

        /// <summary>
        /// Names a removed category's products can be moved into.
        ///
        /// ACTIVE ones only, and that is the point: an inactive category
        /// is one somebody retired, and offering it as the destination for
        /// fourteen live products would quietly file them under something
        /// no new product can be filed under. Uncategorized is the honest
        /// option in that situation, and the dialog offers it first.
        ///
        /// Built here rather than re-derived per row in Razor.
        /// </summary>
        public List<string> ReassignTargetsFor(Guid id)
            => Categories
                .Where(c => c.Id != id && c.IsActive)
                .OrderBy(c => c.SortOrder)
                .Select(c => c.Name)
                .ToList();

        [TempData] public string? ErrorMessage { get; set; }
        [TempData] public string? SuccessMessage { get; set; }

        // ── Which form is open ────────────────────────────────────────
        //
        // In the QUERY STRING, not in JavaScript state, so a validation
        // failure, a refresh and a pasted link all reopen the same form.
        // An inline editor built on a hidden div loses that the first time
        // the server rejects something.

        /// <summary>?edit={id} — the row whose form is open.</summary>
        [BindProperty(SupportsGet = true, Name = "edit")]
        public Guid? EditId { get; set; }

        /// <summary>?add=1 — the new-category form is open.</summary>
        [BindProperty(SupportsGet = true, Name = "add")]
        public bool IsAdding { get; set; }

        // ── The inline form, used by both add and edit ────────────────
        [BindProperty] public InputModel Input { get; set; } = new();

        public class InputModel
        {
            /// <summary>Empty on add; the row's id on edit.</summary>
            public Guid? Id { get; set; }

            [Required(ErrorMessage = "A category needs a name.")]
            [StringLength(80, ErrorMessage = "A category name cannot exceed 80 characters.")]
            [Display(Name = "Name")]
            public string Name { get; set; } = string.Empty;

            [Required]
            [StringLength(60)]
            [Display(Name = "Icon")]
            public string Icon { get; set; } = ProductCategoriesConfiguration.FallbackIcon;

            /// <summary>
            /// #rrggbb. The pattern is here as well as on the server
            /// because a wrong colour is almost always a typo somebody can
            /// see and fix, and a round trip to be told so is wasteful.
            /// </summary>
            [Required(ErrorMessage = "Pick a colour.")]
            [RegularExpression("^#[0-9A-Fa-f]{6}$",
                ErrorMessage = "A colour must look like #3b82f6.")]
            [Display(Name = "Colour")]
            public string Color { get; set; } = ProductCategoriesConfiguration.FallbackColor;

            [Display(Name = "Offer this category for new products")]
            public bool IsActive { get; set; } = true;
        }

        // =============================================================
        // GET
        // =============================================================

        public async Task<IActionResult> OnGetAsync()
        {
            var denied = await ValidatePermissionAsync(Actions.Read);
            if (denied is not null) return denied;

            await InitializePermissionsAsync();
            await LoadAsync();

            // ?add=1 wins over ?edit={id}, matching the view. Both forms
            // on one page would duplicate the icon radios' ids, and a
            // <label for> binds to the FIRST match in the document — so
            // clicking an icon in one form would move the other's.
            if (IsAdding)
            {
                EditId = null;

                // A new category starts on a real colour rather than the
                // grey fallback, which is what "no category" looks like
                // everywhere else in the app and a poor thing to offer as
                // a default for a brand new one.
                Input = new InputModel
                {
                    Color = ProductCategoryColors.Swatches.FirstOrDefault()
                            ?? ProductCategoriesConfiguration.FallbackColor
                };
            }

            // ?edit={id}: fill the form from the row. Without this the
            // edit form would open with an empty name and a grey icon, and
            // saving it would rename the category to whatever got typed —
            // which is the kind of bug that only shows up once somebody
            // clicks Edit and then changes their mind about one field.
            if (EditId is { } id && id != Guid.Empty)
            {
                var row = Categories.FirstOrDefault(c => c.Id == id);

                if (row is null)
                {
                    // Removed in another tab. Drop back to the plain list
                    // rather than rendering a form for nothing.
                    EditId = null;
                    ErrorMessage = "That category no longer exists. The list has been refreshed.";
                }
                else
                {
                    Input = new InputModel
                    {
                        Id       = row.Id,
                        Name     = row.Name,
                        Icon     = row.Icon,
                        Color    = row.Color,
                        IsActive = row.IsActive
                    };
                }
            }

            return Page();
        }

        // =============================================================
        // ADD
        // =============================================================

        public async Task<IActionResult> OnPostAddAsync()
        {
            var denied = await ValidatePermissionAsync(Actions.Create);
            if (denied is not null) return denied;

            await InitializePermissionsAsync();

            // Keep the form the person was using OPEN on a failure —
            // theirs is the typing that has to survive, not the URL.
            IsAdding = true;
            EditId   = null;

            if (!ModelState.IsValid)
            {
                await LoadAsync();
                return Page();
            }

            try
            {
                var result = await _categories.CreateAsync(new CreateProductCategoryDto
                {
                    Name     = Input.Name.Trim(),
                    Icon     = Input.Icon,
                    Color    = Input.Color,
                    IsActive = Input.IsActive
                });

                SuccessMessage = Describe($"\"{Input.Name.Trim()}\" added.", result);
                return RedirectToPage();
            }
            catch (InvalidOperationException ex)
            {
                // The API's own sentence — "There is already a category
                // called \"Hardware\"." — not a second, vaguer one.
                ErrorMessage = ex.Message;
                await LoadAsync();
                return Page();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to add a product category");
                ErrorMessage = "Could not add the category. Please try again.";
                await LoadAsync();
                return Page();
            }
        }

        // =============================================================
        // SAVE AN EXISTING ONE
        // =============================================================

        public async Task<IActionResult> OnPostSaveAsync()
        {
            var denied = await ValidatePermissionAsync(Actions.Update);
            if (denied is not null) return denied;

            await InitializePermissionsAsync();

            // Same rule as Add: the form that failed stays open.
            IsAdding = false;
            EditId   = Input.Id;

            if (Input.Id is null || Input.Id == Guid.Empty)
            {
                ErrorMessage = "Could not tell which category to save. Please try again.";
                await LoadAsync();
                return Page();
            }

            if (!ModelState.IsValid)
            {
                await LoadAsync();
                return Page();
            }

            try
            {
                var result = await _categories.UpdateAsync(Input.Id.Value, new UpdateProductCategoryDto
                {
                    Name     = Input.Name.Trim(),
                    Icon     = Input.Icon,
                    Color    = Input.Color,
                    IsActive = Input.IsActive
                });

                // The product count is the point of this message. A rename
                // that silently rewrote forty products and said "Saved."
                // would be the kind of thing somebody only notices later.
                var msg = result.ProductsUpdated > 0
                    ? $"\"{Input.Name.Trim()}\" saved. {Plural(result.ProductsUpdated, "product")} moved with it."
                    : $"\"{Input.Name.Trim()}\" saved.";

                SuccessMessage = Describe(msg, result);
                return RedirectToPage();
            }
            catch (KeyNotFoundException)
            {
                ErrorMessage = "That category no longer exists. The list has been refreshed.";
                await LoadAsync();
                return Page();
            }
            catch (InvalidOperationException ex)
            {
                ErrorMessage = ex.Message;
                await LoadAsync();
                return Page();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to save product category {Id}", Input.Id);
                ErrorMessage = "Could not save the category. Please try again.";
                await LoadAsync();
                return Page();
            }
        }

        // =============================================================
        // TOGGLE ACTIVE
        //
        // Its own handler rather than a trip through the edit form,
        // because switching a category off is one click and should stay
        // one click. It still goes through the same API endpoint, so the
        // "last active category" guard applies.
        // =============================================================

        public async Task<IActionResult> OnPostToggleAsync(Guid id)
        {
            var denied = await ValidatePermissionAsync(Actions.Update);
            if (denied is not null) return denied;

            try
            {
                var list = await _categories.GetAllAsync(includeInactive: true);
                var row  = list.Categories.FirstOrDefault(c => c.Id == id);

                if (row is null)
                {
                    ErrorMessage = "That category no longer exists.";
                    return RedirectToPage();
                }

                var result = await _categories.UpdateAsync(id, new UpdateProductCategoryDto
                {
                    Name     = row.Name,
                    Icon     = row.Icon,
                    Color    = row.Color,
                    IsActive = !row.IsActive
                });

                SuccessMessage = Describe(
                    row.IsActive
                        ? $"\"{row.Name}\" switched off. It stays on the products already using it."
                        : $"\"{row.Name}\" switched back on.",
                    result);

                return RedirectToPage();
            }
            catch (InvalidOperationException ex)
            {
                ErrorMessage = ex.Message;
                return RedirectToPage();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to toggle product category {Id}", id);
                ErrorMessage = "Could not change the category. Please try again.";
                return RedirectToPage();
            }
        }

        // =============================================================
        // REMOVE
        // =============================================================

        public async Task<IActionResult> OnPostRemoveAsync(Guid id, string? reassignTo)
        {
            var denied = await ValidatePermissionAsync(Actions.Delete);
            if (denied is not null) return denied;

            try
            {
                // An empty string from the dialog's "leave them
                // uncategorized" option means NULL, not "".
                var target = string.IsNullOrWhiteSpace(reassignTo) ? null : reassignTo.Trim();

                var result = await _categories.DeleteAsync(id, target);

                var moved = result.ProductsUpdated switch
                {
                    0 => "Category removed.",
                    _ => target is null
                        ? $"Category removed. {Plural(result.ProductsUpdated, "product")} now uncategorized."
                        : $"Category removed. {Plural(result.ProductsUpdated, "product")} moved to \"{target}\"."
                };

                SuccessMessage = Describe(moved, result);
                return RedirectToPage();
            }
            catch (KeyNotFoundException)
            {
                ErrorMessage = "That category no longer exists.";
                return RedirectToPage();
            }
            catch (InvalidOperationException ex)
            {
                ErrorMessage = ex.Message;
                return RedirectToPage();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to remove product category {Id}", id);
                ErrorMessage = "Could not remove the category. Please try again.";
                return RedirectToPage();
            }
        }

        // =============================================================
        // REORDER
        //
        // Posted by the up/down buttons as the full new order. Buttons
        // rather than drag-and-drop: this list is short, it is edited
        // rarely, and drag-and-drop on a phone is a fight.
        // =============================================================

        public async Task<IActionResult> OnPostMoveAsync(Guid id, string direction)
        {
            var denied = await ValidatePermissionAsync(Actions.Update);
            if (denied is not null) return denied;

            try
            {
                var list    = await _categories.GetAllAsync(includeInactive: true);
                var ordered = list.Categories
                    .OrderBy(c => c.SortOrder)
                    .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(c => c.Id)
                    .ToList();

                var index = ordered.IndexOf(id);
                if (index < 0)
                {
                    ErrorMessage = "That category no longer exists.";
                    return RedirectToPage();
                }

                var target = direction == "up" ? index - 1 : index + 1;

                // Already at the end — nothing to say, and an error message
                // for pressing a button that was disabled anyway would be
                // noise.
                if (target < 0 || target >= ordered.Count)
                    return RedirectToPage();

                (ordered[index], ordered[target]) = (ordered[target], ordered[index]);

                await _categories.ReorderAsync(ordered);
                return RedirectToPage();
            }
            catch (InvalidOperationException ex)
            {
                ErrorMessage = ex.Message;
                return RedirectToPage();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to reorder product categories");
                ErrorMessage = "Could not change the order. Please try again.";
                return RedirectToPage();
            }
        }

        // =============================================================
        // Helpers
        // =============================================================

        private async Task LoadAsync()
        {
            try
            {
                // includeInactive: the page has to show a switched-off
                // category in order to offer switching it back on.
                var list = await _categories.GetAllAsync(includeInactive: true);

                Categories                = list.Categories;
                FollowingDefaults         = list.FollowingDefaults;
                UncategorizedProductCount = list.UncategorizedProductCount;
            }
            catch (Exception ex)
            {
                // Render anyway. A list that failed to load plus an error
                // banner beats a redirect to somewhere that explains
                // nothing.
                Logger.LogError(ex, "Failed to load product categories");
                ErrorMessage ??= "Could not load your categories. Please try again.";
                Categories = new List<ProductCategoryDto>();
            }
        }

        /// <summary>
        /// Adds the adoption sentence to a success message, once, the one
        /// time it is true. Worth saying out loud: from that moment the
        /// workspace's list has stopped tracking the Merkai defaults.
        /// </summary>
        private static string Describe(string message, ProductCategoryWriteResult result)
            => result.AdoptedDefaults
                ? message + " Your workspace now has its own copy of the category list, " +
                            "so you can rename or remove any of them."
                : message;

        private static string Plural(int count, string noun)
            => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
    }
}
