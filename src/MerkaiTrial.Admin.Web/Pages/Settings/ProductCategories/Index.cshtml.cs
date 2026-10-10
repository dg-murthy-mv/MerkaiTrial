// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Settings/ProductCategories/Index.cshtml.cs
//
// 075 — DUPLICATE SUCCESS/ERROR BANNERS REMOVED. The only change in this
//   round; see the note on PageError. Nothing else in this file moved.
//
// 073 — SEARCH, FILTERS AND PAGING
//
//   The list is filtered and paged IN MEMORY, on this page, and that is
//   a deliberate choice rather than a shortcut. /api/product-categories
//   returns the whole list in one call because the whole list is the
//   unit of meaning here: the ORDER is a property of all of it, Remove
//   needs every other category as a reassign target, and the up/down
//   buttons post the complete new order. Server-side paging would mean
//   three more round trips to rebuild what one call already gives us,
//   for a table that is a dozen rows on most workspaces.
//
//   ⚠ TWO THINGS THAT PAGING BREAKS IF YOU ARE NOT CAREFUL, and both
//   are handled below:
//
//   1. "FIRST" AND "LAST" ARE PROPERTIES OF THE WHOLE LIST, not of the
//      page you are looking at. The up arrow was disabled on row 0 of
//      the rendered list; paged, that would disable it on the first row
//      of EVERY page and silently strip the ability to move a category
//      off page 2. IsFirst/IsLast index into the full ordered list.
//
//   2. MOVING A ROW CAN MOVE IT TO ANOTHER PAGE. Press up on the first
//      row of page 2 and it belongs on page 1 — correctly, because the
//      order is global. Redirecting back to the page you were on would
//      show the row simply gone. OnPostMoveAsync works out which page
//      the row landed on and sends you there.
//
//   REORDERING IS DISABLED WHILE A SEARCH OR FILTER IS ACTIVE, which is
//   the one thing here I would defend hardest. "Move up" means "swap
//   with the row above" — and with rows filtered out, the row above on
//   screen is not the row above in the list. Either reading surprises
//   somebody: swap with the visible neighbour and the saved order jumps
//   further than it looks; swap with the real neighbour and nothing
//   appears to happen. Disabled, with a sentence saying why, is the only
//   version that cannot quietly do the wrong thing.
//
// 071b — THE ADD BUTTON NEVER WORKED. ?add=1 was bound to a bool, and
// ASP.NET Core's bool binder accepts "true"/"false" and nothing else, so
// the value failed to convert, the flag stayed false, and the form never
// rendered — on every workspace, since this file shipped. The flag is a
// string now and IsAdding decides what counts as on. The full note is on
// the property.
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

using MerkaiTrial.Admin.Web.Pages.Shared;
using MerkaiTrial.Admin.Web.Services.Products;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Routing;
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

        /// <summary>
        /// EVERY category this workspace has, in order. The authority for
        /// the order, for "is this row first or last", and for the Remove
        /// dialog's reassign targets.
        ///
        /// ⚠ Use this, never Categories, for anything about the LIST.
        /// Categories is one page of it.
        /// </summary>
        public List<ProductCategoryDto> AllCategories { get; private set; } = new();

        /// <summary>
        /// The slice actually rendered: AllCategories after the search and
        /// filters, cut to one page. 073.
        /// </summary>
        public List<ProductCategoryDto> Categories { get; private set; } = new();

        /// <summary>The filtered list before paging — what the count and the pager measure.</summary>
        public List<ProductCategoryDto> Matching { get; private set; } = new();

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
        /// <summary>
        /// 073: reads AllCategories, not Categories. Paged, the old
        /// version offered only the destinations that happened to be on
        /// the same page — so removing a category from page 2 could not
        /// move its products into anything on page 1.
        /// </summary>
        public List<string> ReassignTargetsFor(Guid id)
            => AllCategories
                .Where(c => c.Id != id && c.IsActive)
                .OrderBy(c => c.SortOrder)
                .Select(c => c.Name)
                .ToList();

        // 075 — MESSAGES, AND THE DUPLICATE BANNER THEY CAUSED
        //
        // These were [TempData] ErrorMessage / SuccessMessage properties,
        // and the view rendered them itself. _Layout ALSO renders
        // TempData["SuccessMessage"] and ["ErrorMessage"] for every page,
        // so every save drew two identical banners — and because the view
        // read the PROPERTIES, the 074d sweep (Find in Files "TempData[")
        // could not see it. A [TempData] property set on a request that
        // RENDERS also turns up again on the next page.
        //
        // Now: a message shown after a REDIRECT goes into TempData and the
        // layout shows it once; a message for a request that RENDERS (a
        // failed save that keeps the form open) goes into PageError, an
        // ordinary property, which the view shows.
        public string? PageError { get; set; }

        // ── Which form is open ────────────────────────────────────────
        //
        // In the QUERY STRING, not in JavaScript state, so a validation
        // failure, a refresh and a pasted link all reopen the same form.
        // An inline editor built on a hidden div loses that the first time
        // the server rejects something.

        /// <summary>?edit={id} — the row whose form is open.</summary>
        [BindProperty(SupportsGet = true, Name = "edit")]
        public Guid? EditId { get; set; }

        /// <summary>
        /// ?add=… — the new-category form is open.
        ///
        /// ⚠ BOUND AS A STRING, NOT A BOOL, AND THAT IS THE WHOLE POINT.
        ///
        /// This was a bool bound straight from the query string, and the
        /// Add button linked to ?add=1. ASP.NET Core binds a bool with
        /// BooleanConverter, which accepts "true" and "false" and NOTHING
        /// ELSE — so "1" failed to convert, IsAdding stayed false, and the
        /// add form never rendered. The Add button did nothing at all, on
        /// every workspace, since round 068.
        ///
        /// It failed in the quietest way available: the conversion error
        /// lands in ModelState under the key "add", which is a PROPERTY
        /// key, so the page's asp-validation-summary="ModelOnly" left it
        /// out. No message, no banner, no log line above Information —
        /// the page simply redrew itself. The only visible trace was
        /// "ModelState is Invalid" in the framework's own log, on exactly
        /// the request carrying ?add=1.
        ///
        /// A string binds whatever arrives, so there is no conversion to
        /// fail and no ModelState entry to swallow. IsAdding below then
        /// decides what counts as on, accepting the handful of spellings a
        /// person might type or a bookmark might carry.
        /// </summary>
        [BindProperty(SupportsGet = true, Name = "add")]
        public string? AddFlag { get; set; }

        /// <summary>
        /// True when the new-category form should be open.
        ///
        /// Settable, because the POST handlers set it directly to keep the
        /// form open after a validation failure — the person's typing is
        /// what has to survive, not the URL. An explicit set always wins
        /// over the query string.
        /// </summary>
        public bool IsAdding
        {
            get => _isAdding ?? IsOn(AddFlag);
            set => _isAdding = value;
        }

        private bool? _isAdding;

        /// <summary>
        /// What counts as "on" in a query string. Deliberately generous:
        /// the app's own link now says add=true, but ?add=1 is in people's
        /// history and address bars from before this fix, and a link that
        /// used to open a form should not quietly stop.
        /// </summary>
        private static bool IsOn(string? value)
            => value is not null &&
               (value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("1",    StringComparison.Ordinal)           ||
                value.Equals("yes",  StringComparison.OrdinalIgnoreCase) ||
                value.Equals("on",   StringComparison.OrdinalIgnoreCase));

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
        // 073 — SEARCH, FILTERS AND PAGING
        // =============================================================

        [BindProperty(SupportsGet = true, Name = "q")]
        public string? SearchTerm { get; set; }

        /// <summary>"" = any, "active", "inactive".</summary>
        [BindProperty(SupportsGet = true, Name = "status")]
        public string? StatusFilter { get; set; }

        /// <summary>
        /// "" = any, "standard" = a Merkai default this workspace still
        /// follows, "custom" = one this workspace owns.
        /// </summary>
        [BindProperty(SupportsGet = true, Name = "kind")]
        public string? KindFilter { get; set; }

        [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
        [BindProperty(SupportsGet = true)] public int PageSize { get; set; } = DefaultPageSize;

        /// <summary>
        /// ?moved={id} — the row that was just reordered, so the view can
        /// flash it. Set only by OnPostMoveAsync's redirect; it is a
        /// display hint and nothing reads it for a decision.
        /// </summary>
        [BindProperty(SupportsGet = true, Name = "moved")]
        public Guid? MovedId { get; set; }

        public const int DefaultPageSize = 10;
        public const int MaxPageSize = 100;

        public bool IsFiltered =>
            !string.IsNullOrWhiteSpace(SearchTerm) ||
            !string.IsNullOrWhiteSpace(StatusFilter) ||
            !string.IsNullOrWhiteSpace(KindFilter);

        public int TotalPages => Matching.Count == 0
            ? 1
            : (int)Math.Ceiling(Matching.Count / (double)PageSize);

        public int FirstRow => Matching.Count == 0 ? 0 : (PageNumber - 1) * PageSize + 1;
        public int LastRow  => Math.Min(PageNumber * PageSize, Matching.Count);

        // ── Reordering ────────────────────────────────────────────────

        /// <summary>
        /// Up/down are offered only on the UNFILTERED list. See the long
        /// note in the file header: with rows filtered out, "the row
        /// above" on screen and "the row above" in the saved order are
        /// two different rows, and both readings surprise somebody.
        ///
        /// Paging is fine — the order is global and the move handler
        /// follows the row to its new page.
        /// </summary>
        public bool CanReorder => CanUpdate && !IsFiltered;

        /// <summary>Why the arrows are missing, or null when they are not.</summary>
        public string? ReorderBlockedReason => CanUpdate && IsFiltered
            ? "Clear the search to rearrange the list — the order is the whole list's, not this filtered view's."
            : null;

        /// <summary>
        /// True when this row is first or last IN THE WHOLE LIST, which is
        /// what disables its arrow. Against the rendered page it would
        /// disable the up arrow on the first row of every page.
        /// </summary>
        public bool IsFirstOverall(Guid id) => AllCategories.Count > 0 && AllCategories[0].Id == id;
        public bool IsLastOverall(Guid id)  => AllCategories.Count > 0 && AllCategories[^1].Id == id;

        // ── The shared components (072) ───────────────────────────────

        public ListToolbarVm Toolbar => new()
        {
            SearchName        = "q",
            SearchValue       = SearchTerm,
            SearchPlaceholder = "Category name",
            SearchLabel       = "Search categories",
            SearchWidth       = "260px",

            Filters = new List<ListFilterVm>
            {
                new()
                {
                    Name    = "status",
                    Label   = "Filter by on or off",
                    Options = new List<SelectListItem>
                    {
                        new() { Value = "",         Text = "On and off",  Selected = string.IsNullOrWhiteSpace(StatusFilter) },
                        new() { Value = "active",   Text = "On only",     Selected = StatusFilter == "active" },
                        new() { Value = "inactive", Text = "Off only",    Selected = StatusFilter == "inactive" }
                    }
                },
                new()
                {
                    Name    = "kind",
                    Label   = "Filter by standard or custom",
                    Options = new List<SelectListItem>
                    {
                        new() { Value = "",         Text = "Standard and custom", Selected = string.IsNullOrWhiteSpace(KindFilter) },
                        new() { Value = "standard", Text = "Standard only",       Selected = KindFilter == "standard" },
                        new() { Value = "custom",   Text = "Custom only",         Selected = KindFilter == "custom" }
                    }
                }
            },

            // The page SIZE is carried; the page NUMBER is not, so a
            // search always lands on page 1.
            Carry      = new Dictionary<string, string> { [nameof(PageSize)] = PageSize.ToString() },
            ClearRoute = new Dictionary<string, string> { [nameof(PageSize)] = PageSize.ToString() },

            IsFiltered   = IsFiltered,
            TotalCount   = Matching.Count,
            FirstRow     = FirstRow,
            LastRow      = LastRow,
            NounSingular = "category",
            NounPlural   = "categories"
        };

        public PagerVm Pager => new()
        {
            Page       = PageNumber,
            PageSize   = PageSize,
            TotalCount = Matching.Count,
            TotalPages = TotalPages,
            PageField  = nameof(PageNumber),
            Route      = FilterRoute()
        };

        /// <summary>
        /// The active search and filters as route values. One definition,
        /// used by the pager's links, the page-size form, the Clear link
        /// AND by every Edit / Add link on the page — so opening a form on
        /// page 3 of a filtered list comes back to page 3 of that list.
        /// </summary>
        public Dictionary<string, string> FilterRoute()
        {
            var route = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(SearchTerm))   route["q"]      = SearchTerm!.Trim();
            if (!string.IsNullOrWhiteSpace(StatusFilter)) route["status"] = StatusFilter!;
            if (!string.IsNullOrWhiteSpace(KindFilter))   route["kind"]   = KindFilter!;

            return route;
        }

        /// <summary>
        /// The filters, the page AND the page size — for the links that
        /// open a form and for every redirect after a write, so nobody is
        /// dumped back on page 1 of everything after switching a category
        /// off.
        /// </summary>
        public Dictionary<string, string> StateRoute(Guid? editId = null, bool adding = false)
        {
            var route = FilterRoute();

            route[nameof(PageNumber)] = PageNumber <= 1 ? "1" : PageNumber.ToString();
            route[nameof(PageSize)]   = PageSize.ToString();

            // "true", never "1". ASP.NET Core binds a bool with
            // BooleanConverter, which takes "true"/"false" and nothing
            // else — ?add=1 is exactly how this button was dead for three
            // rounds. The flag is a string now and still accepts "1", but
            // the link the app emits says what the binder wants.
            if (adding) route["add"] = "true";
            if (editId is { } id && id != Guid.Empty) route["edit"] = id.ToString();

            return route;
        }

        /// <summary>StateRoute as route values a redirect will accept verbatim.</summary>
        private RouteValueDictionary ReturnRoute()
        {
            // A RouteValueDictionary and not a Dictionary: RedirectToPage
            // takes `object routeValues` and reflects over an ordinary
            // object's PROPERTIES, so a plain dictionary risks a redirect
            // to ?Comparer=...&Count=3.
            var values = new RouteValueDictionary();
            foreach (var pair in StateRoute()) values[pair.Key] = pair.Value;

            // ?moved is deliberately NOT carried. It is a one-shot hint
            // from a redirect the move handler makes; carrying it would
            // leave a row flashing after every unrelated save.
            return values;
        }

        /// <summary>
        /// Apply the search and filters to the full list, then cut one
        /// page out of it.
        ///
        /// Ordinal-ignore-case matching, not the current culture: a
        /// category list is short, the names are the workspace's own, and
        /// culture-sensitive comparison on a Turkish machine famously does
        /// not match "I" with "i".
        /// </summary>
        private void ApplyFilters()
        {
            IEnumerable<ProductCategoryDto> rows = AllCategories;

            if (!string.IsNullOrWhiteSpace(SearchTerm))
            {
                var needle = SearchTerm.Trim();
                rows = rows.Where(c => c.Name.Contains(needle, StringComparison.OrdinalIgnoreCase));
            }

            if (string.Equals(StatusFilter, "active", StringComparison.OrdinalIgnoreCase))
                rows = rows.Where(c => c.IsActive);
            else if (string.Equals(StatusFilter, "inactive", StringComparison.OrdinalIgnoreCase))
                rows = rows.Where(c => !c.IsActive);

            if (string.Equals(KindFilter, "standard", StringComparison.OrdinalIgnoreCase))
                rows = rows.Where(c => c.IsSystem);
            else if (string.Equals(KindFilter, "custom", StringComparison.OrdinalIgnoreCase))
                rows = rows.Where(c => !c.IsSystem);

            Matching = rows.ToList();

            if (PageSize <= 0) PageSize = DefaultPageSize;
            if (PageSize > MaxPageSize) PageSize = MaxPageSize;
            if (PageNumber < 1) PageNumber = 1;

            // A page past the end shows the LAST page, not an empty table.
            // Reachable without trying: be on page 3, switch a filter on,
            // and three rows match.
            if (PageNumber > TotalPages) PageNumber = TotalPages;

            Categories = Matching
                .Skip((PageNumber - 1) * PageSize)
                .Take(PageSize)
                .ToList();
        }

        /// <summary>
        /// Which page a given category is on, after the current filters.
        /// Used by the move handler to follow a row that has just crossed
        /// a page boundary. 1 when it is not in the filtered list at all.
        /// </summary>
        private int PageContaining(Guid id)
        {
            var index = Matching.FindIndex(c => c.Id == id);
            if (index < 0) return 1;

            return (index / Math.Max(PageSize, 1)) + 1;
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

            // ?add=true wins over ?edit={id}, matching the view. Both forms
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
                // 073: AllCategories, not Categories. Paged, a row being
                // edited on page 2 is not in the current page's slice when
                // the filters change under it, and this would have cleared
                // the form and claimed the category no longer exists.
                var row = AllCategories.FirstOrDefault(c => c.Id == id);

                if (row is null)
                {
                    // Removed in another tab. Drop back to the plain list
                    // rather than rendering a form for nothing.
                    EditId = null;
                    PageError = "That category no longer exists. The list has been refreshed.";
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

                TempData["SuccessMessage"] = Describe($"\"{Input.Name.Trim()}\" added.", result);
                return RedirectToPage(ReturnRoute());
            }
            catch (InvalidOperationException ex)
            {
                // The API's own sentence — "There is already a category
                // called \"Hardware\"." — not a second, vaguer one.
                PageError = ex.Message;
                await LoadAsync();
                return Page();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to add a product category");
                PageError = "Could not add the category. Please try again.";
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
                PageError = "Could not tell which category to save. Please try again.";
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

                TempData["SuccessMessage"] = Describe(msg, result);
                return RedirectToPage(ReturnRoute());
            }
            catch (KeyNotFoundException)
            {
                PageError = "That category no longer exists. The list has been refreshed.";
                await LoadAsync();
                return Page();
            }
            catch (InvalidOperationException ex)
            {
                PageError = ex.Message;
                await LoadAsync();
                return Page();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to save product category {Id}", Input.Id);
                PageError = "Could not save the category. Please try again.";
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
                    TempData["ErrorMessage"] = "That category no longer exists.";
                    return RedirectToPage(ReturnRoute());
                }

                var result = await _categories.UpdateAsync(id, new UpdateProductCategoryDto
                {
                    Name     = row.Name,
                    Icon     = row.Icon,
                    Color    = row.Color,
                    IsActive = !row.IsActive
                });

                TempData["SuccessMessage"] = Describe(
                    row.IsActive
                        ? $"\"{row.Name}\" switched off. It stays on the products already using it."
                        : $"\"{row.Name}\" switched back on.",
                    result);

                return RedirectToPage(ReturnRoute());
            }
            catch (InvalidOperationException ex)
            {
                TempData["ErrorMessage"] = ex.Message;
                return RedirectToPage(ReturnRoute());
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to toggle product category {Id}", id);
                TempData["ErrorMessage"] = "Could not change the category. Please try again.";
                return RedirectToPage(ReturnRoute());
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

                TempData["SuccessMessage"] = Describe(moved, result);
                return RedirectToPage(ReturnRoute());
            }
            catch (KeyNotFoundException)
            {
                TempData["ErrorMessage"] = "That category no longer exists.";
                return RedirectToPage(ReturnRoute());
            }
            catch (InvalidOperationException ex)
            {
                TempData["ErrorMessage"] = ex.Message;
                return RedirectToPage(ReturnRoute());
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to remove product category {Id}", id);
                TempData["ErrorMessage"] = "Could not remove the category. Please try again.";
                return RedirectToPage(ReturnRoute());
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

            // 073: refuse a move while the list is filtered, on the SERVER.
            // The view hides the arrows, but a hidden button is not a
            // control — this form is one hand-written POST away. The
            // reasoning is in the file header: with rows filtered out,
            // "swap with the row above" has two different answers and
            // neither is the one the user is looking at.
            if (IsFiltered)
            {
                TempData["ErrorMessage"] = "Clear the search before rearranging — the order belongs to the whole list.";
                return RedirectToPage(ReturnRoute());
            }

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
                    TempData["ErrorMessage"] = "That category no longer exists.";
                    return RedirectToPage(ReturnRoute());
                }

                var target = direction == "up" ? index - 1 : index + 1;

                // Already at the end — nothing to say, and an error message
                // for pressing a button that was disabled anyway would be
                // noise.
                if (target < 0 || target >= ordered.Count)
                    return RedirectToPage(ReturnRoute());

                (ordered[index], ordered[target]) = (ordered[target], ordered[index]);

                await _categories.ReorderAsync(ordered);

                // ── 073: FOLLOW THE ROW TO ITS NEW PAGE ───────────────
                //
                // Press up on the first row of page 2 and the category
                // correctly belongs on page 1 — the order is global, the
                // view is what happens to be paginated. Redirecting back
                // to page 2 would show the row simply gone, which reads
                // as the button having deleted something.
                //
                // Reload, re-page, and send the user to wherever it landed.
                var moved = ordered[target];
                await LoadAsync();

                var route = ReturnRoute();
                route[nameof(PageNumber)] = PageContaining(moved);

                // So the view can flash the row that just moved. Without
                // it, a move that crosses a page boundary is two visual
                // changes at once — a different page AND a reordered list.
                route["moved"] = moved.ToString();

                return RedirectToPage(route);
            }
            catch (InvalidOperationException ex)
            {
                TempData["ErrorMessage"] = ex.Message;
                return RedirectToPage(ReturnRoute());
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to reorder product categories");
                TempData["ErrorMessage"] = "Could not change the order. Please try again.";
                return RedirectToPage(ReturnRoute());
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

                // 073: the FULL list, in display order, is the authority
                // for the ordering and for the reassign targets. The
                // service already returns it sorted; sorting again here
                // means IsFirstOverall/IsLastOverall and the move handler
                // cannot disagree with what the view draws.
                AllCategories = list.Categories
                    .OrderBy(c => c.SortOrder)
                    .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                FollowingDefaults         = list.FollowingDefaults;
                UncategorizedProductCount = list.UncategorizedProductCount;
            }
            catch (Exception ex)
            {
                // Render anyway. A list that failed to load plus an error
                // banner beats a redirect to somewhere that explains
                // nothing.
                Logger.LogError(ex, "Failed to load product categories");
                PageError ??= "Could not load your categories. Please try again.";
                AllCategories = new List<ProductCategoryDto>();
            }

            // Always, including after a failure — so Matching and
            // Categories are empty lists rather than nulls and the view
            // has nothing to guard against.
            ApplyFilters();
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
