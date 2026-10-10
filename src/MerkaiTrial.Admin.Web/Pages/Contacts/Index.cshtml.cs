// =====================================================================
// CONTACTS INDEX - BACKEND
// Location: MerkaiTrial.Admin.Web/Pages/Contacts/Index.cshtml.cs
//
// ✅ MIGRATED to AuthorizedPageModel (was AppPageModel).
//
// 076 — CUSTOM FIELDS ON THE LIST, AND THE SHARED TOOLBAR AND PAGER (072)
//
//   1. THE SHARED COMPONENTS. Search, the Company and Primary filters and
//      the count go through _ListToolbar; paging through _Pager. The
//      hand-built search card and pager — with every filter spelled out
//      on each page link — are gone. All list state lives in the URL, so
//      a filtered list can be bookmarked and survives the back button.
//
//   2. CUSTOM FIELD COLUMNS. Fields marked "Show on the list" in
//      Settings → Custom Fields (at most four) are columns on the desktop
//      table and lines on the mobile cards, in the tenant's own number
//      and date format.
//
//   3. CUSTOM FIELD FILTERS through the shared _CustomFieldFilterPanel:
//      every active field can be filtered on, combined with the search
//      and the toolbar's filters in one GET. The search box also finds
//      custom text values and dropdown choices (done by the API).
//
//   4. THE BOOL BINDING TRAP. IsPrimaryFilter was a bool? bound from the
//      query string. That binder takes only "true"/"false"; anything else
//      leaves a ModelState error nobody sees (round 071b). It is a string
//      now, parsed here. CompanyFilter likewise, so a mangled id in a
//      bookmark shows all companies instead of a silent binding error.
//
//   5. `public int Page` IS NOW PageNumber. It compiled — `return Page()`
//      still finds the method, because a CALL ignores the property — but
//      the shared pager's field is PageNumber everywhere else, and one
//      name per concept is what makes the dictionary-based pager safe.
//
//   6. DELETE KEEPS YOUR PLACE. The dialog posts the list state, and the
//      handler redirects back to the same page of the same filtered list
//      instead of page 1 of everything. A page emptied by the delete is
//      pulled back to the last page by the API.
//
//   FormatDate / FormatDateTime and GetCompanyName are gone: the first two
//   were copies of the base class's, the third looked up a name the list
//   item already carries (ContactListItem.CompanyName).
// =====================================================================

using MerkaiTrial.Admin.Web.Pages;
using MerkaiTrial.Admin.Web.Pages.Shared;
using MerkaiTrial.Admin.Web.Services.Contacts;
using MerkaiTrial.Admin.Web.Services.CustomFields;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;

namespace MerkaiTrial.Admin.Web.Pages.Contacts
{
    public class IndexModel : AuthorizedPageModel
    {
        private readonly IContactService     _contactService;
        private readonly ICustomFieldService _customFields;

        protected override string ModuleName => Modules.Contacts;

        /// <summary>The toolbar form's id — the filter panel's inputs submit with it.</summary>
        public const string FilterFormId = "contactsFilter";

        public const int DefaultPageSize = 10;

        public IndexModel(
            IContactService         contactService,
            ICustomFieldService     customFields,
            IAuthorizationService   authorizationService,
            ICurrentUserService     currentUserService,
            ILogger<IndexModel>     logger)
            : base(authorizationService, currentUserService, logger)
        {
            _contactService = contactService;
            _customFields   = customFields;
        }

        // ── What the page renders ─────────────────────────────────────

        public PaginatedResult<ContactListItem> PaginatedContacts { get; private set; } = new();
        public ContactStatsDto Stats { get; private set; } = new(0, 0, 0, 0);
        public List<CompanyListItem> Companies { get; private set; } = new();

        /// <summary>Active custom fields shown as list columns, in order.</summary>
        public List<CustomFieldDefinitionDto> ListColumns { get; private set; } = new();

        /// <summary>The custom field filters: parsed, checked, ready for the API and the panel.</summary>
        public CustomFieldListFilterState CustomFilters { get; private set; } = new();

        /// <summary>Shown on THIS response only — a list that failed to load.</summary>
        public string? PageError { get; private set; }

        // ── List state, all in the URL ────────────────────────────────

        [BindProperty(SupportsGet = true)] public string? SearchTerm { get; set; }

        /// <summary>A company id, or empty for all. A string — see note 4.</summary>
        [BindProperty(SupportsGet = true)] public string? CompanyFilter { get; set; }

        /// <summary>"", "true" or "false". A string — see note 4.</summary>
        [BindProperty(SupportsGet = true)] public string? IsPrimaryFilter { get; set; }

        [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
        [BindProperty(SupportsGet = true)] public int PageSize { get; set; } = DefaultPageSize;

        private Guid? CompanyId =>
            Guid.TryParse(CompanyFilter, out var id) && id != Guid.Empty ? id : null;

        private bool? IsPrimary =>
            string.Equals(IsPrimaryFilter, "true",  StringComparison.OrdinalIgnoreCase) ? true :
            string.Equals(IsPrimaryFilter, "false", StringComparison.OrdinalIgnoreCase) ? false : null;

        private bool ToolbarFiltered =>
            !string.IsNullOrWhiteSpace(SearchTerm) || CompanyId.HasValue || IsPrimary.HasValue;

        public bool IsFiltered => ToolbarFiltered || CustomFilters.HasAny;

        // ── The shared components ─────────────────────────────────────

        public int TotalCount => PaginatedContacts.TotalCount;
        public int TotalPages => TotalCount == 0 ? 1 : (int)Math.Ceiling(TotalCount / (double)PageSize);
        public int FirstRow   => TotalCount == 0 ? 0 : (PageNumber - 1) * PageSize + 1;
        public int LastRow    => Math.Min(PageNumber * PageSize, TotalCount);

        public ListToolbarVm Toolbar => new()
        {
            FormId            = FilterFormId,
            SearchName        = nameof(SearchTerm),
            SearchValue       = SearchTerm,
            SearchPlaceholder = "Name, email, phone or any text detail",
            SearchLabel       = "Search contacts",
            SearchWidth       = "320px",

            Filters = new List<ListFilterVm>
            {
                new()
                {
                    Name    = nameof(CompanyFilter),
                    Label   = "Filter by company",
                    Options = CompanyOptions()
                },
                new()
                {
                    Name    = nameof(IsPrimaryFilter),
                    Label   = "Filter by primary contact",
                    Options = new List<SelectListItem>
                    {
                        new() { Value = "",      Text = "All contacts",  Selected = IsPrimary is null },
                        new() { Value = "true",  Text = "Primary only",  Selected = IsPrimary == true },
                        new() { Value = "false", Text = "Non-primary",   Selected = IsPrimary == false }
                    }
                }
            },

            // The page SIZE is carried; the page NUMBER never is (072).
            Carry      = new Dictionary<string, string> { [nameof(PageSize)] = PageSize.ToString() },
            ClearRoute = new Dictionary<string, string> { [nameof(PageSize)] = PageSize.ToString() },

            IsFiltered   = IsFiltered,
            TotalCount   = TotalCount,
            FirstRow     = FirstRow,
            LastRow      = LastRow,
            NounSingular = "contact",
            NounPlural   = "contacts"
        };

        public PagerVm Pager => new()
        {
            Page       = PageNumber,
            PageSize   = PageSize,
            TotalCount = TotalCount,
            TotalPages = TotalPages,
            PageField  = nameof(PageNumber),
            Route      = FilterRoute()
        };

        /// <summary>
        /// The toolbar's filters as route values, WITHOUT the custom ones —
        /// what the panel's "Clear these" keeps.
        /// </summary>
        private Dictionary<string, string> ToolbarRoute()
        {
            var route = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(SearchTerm)) route[nameof(SearchTerm)]      = SearchTerm.Trim();
            if (CompanyId is { } c)                     route[nameof(CompanyFilter)]   = c.ToString();
            if (IsPrimary is { } p)                     route[nameof(IsPrimaryFilter)] = p ? "true" : "false";
            return route;
        }

        /// <summary>Every active filter — toolbar and custom — for the pager and the delete dialog.</summary>
        public Dictionary<string, string> FilterRoute()
        {
            var route = ToolbarRoute();
            foreach (var (k, v) in CustomFilters.Route) route[k] = v;
            return route;
        }

        /// <summary>FilterRoute plus the page and size — the hidden fields of the delete dialog.</summary>
        public Dictionary<string, string> StateRoute()
        {
            var route = FilterRoute();
            route[nameof(PageNumber)] = PageNumber.ToString();
            route[nameof(PageSize)]   = PageSize.ToString();
            return route;
        }

        private List<SelectListItem> CompanyOptions()
        {
            var selected = CompanyId?.ToString();
            var items = new List<SelectListItem>
            {
                new() { Value = "", Text = "All companies", Selected = selected is null }
            };
            items.AddRange(Companies
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(c => new SelectListItem
                {
                    Value    = c.Id.ToString(),
                    Text     = c.Name,
                    Selected = c.Id.ToString() == selected
                }));
            return items;
        }

        // ── Display helpers ───────────────────────────────────────────

        private System.Globalization.CultureInfo? _culture;
        private string? _dateFormat;

        /// <summary>A custom column's value as a person reads it. Empty when not filled in.</summary>
        public string ColumnValue(ContactListItem contact, CustomFieldDefinitionDto field)
        {
            _culture    ??= CustomFieldFormatter.ResolveCulture(CultureName);
            _dateFormat ??= TenantCtx.GetDateFormat();

            contact.CustomFieldValues.TryGetValue(field.Id, out var wire);

            // An unticked checkbox has no value; on a list a blank cell says
            // "no" more quietly than a column full of the word.
            if (field.FieldType == CustomFieldTypes.Checkbox)
                return wire == "true" ? "Yes" : string.Empty;

            return CustomFieldFormatter.Display(field, wire, _culture, _dateFormat);
        }

        // =============================================================
        // GET
        // =============================================================

        public async Task<IActionResult> OnGetAsync()
        {
            // ✅ Check READ permission
            var permissionCheck = await ValidatePermissionAsync(Actions.Read);
            if (permissionCheck != null) return permissionCheck;

            // Initialize permissions for UI buttons (Add Contact / Edit / Delete)
            await InitializePermissionsAsync();

            await LoadAsync(Request.Query);
            return Page();
        }

        // =============================================================
        // DELETE
        // =============================================================

        public async Task<IActionResult> OnPostDeleteAsync(Guid id)
        {
            // ✅ Check DELETE permission
            var permissionCheck = await ValidatePermissionAsync(Actions.Delete);
            if (permissionCheck != null) return permissionCheck;

            try
            {
                var tenantId = CurrentUserService.GetCurrentTenantId();
                await _contactService.DeleteAsync(tenantId, id);
                TempData["SuccessMessage"] = "Contact deleted successfully!";
            }
            catch (KeyNotFoundException)
            {
                TempData["ErrorMessage"] = "That contact no longer exists.";
            }
            catch (InvalidOperationException ex)
            {
                TempData["ErrorMessage"] = ex.Message;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error deleting contact {Id}", id);
                TempData["ErrorMessage"] = "Failed to delete contact. Please try again.";
            }

            // Back to the same page of the same filtered list (note 6). The
            // dialog posted the list state as hidden fields: the toolbar
            // ones bind to the properties above, the custom ones are read
            // straight from the form.
            return RedirectToPage(ReturnRouteFrom(Request.Form));
        }

        /// <summary>A RouteValueDictionary — RedirectToPage reflects over plain objects.</summary>
        private RouteValueDictionary ReturnRouteFrom(IEnumerable<KeyValuePair<string, StringValues>> source)
        {
            var values = new RouteValueDictionary();

            foreach (var (k, v) in ToolbarRoute()) values[k] = v;

            foreach (var (key, val) in source)
            {
                if (!key.StartsWith(CustomFieldListFilterState.Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var first = val.Count > 0 ? val[0] : null;
                if (!string.IsNullOrWhiteSpace(first)) values[key] = first;
            }

            values[nameof(PageNumber)] = Math.Max(PageNumber, 1);
            values[nameof(PageSize)]   = ClampPageSize(PageSize);
            return values;
        }

        // =============================================================
        // Helpers
        // =============================================================

        private static int ClampPageSize(int size)
            => PagerVm.Sizes.Contains(size) ? size : DefaultPageSize;

        private async Task LoadAsync(IEnumerable<KeyValuePair<string, StringValues>> query)
        {
            PageSize   = ClampPageSize(PageSize);
            PageNumber = Math.Max(PageNumber, 1);

            var tenantId = CurrentUserService.GetCurrentTenantId();

            // ── Custom fields: columns and filters. Non-fatal. ────────
            List<CustomFieldDefinitionDto> fields;
            var fieldsFailed = false;
            try
            {
                fields = await _customFields.GetDefinitionsAsync(CustomFieldEntityTypes.Contact, includeInactive: false);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Custom fields could not be loaded for the contacts list");
                fields = new List<CustomFieldDefinitionDto>();
                fieldsFailed = true;
            }

            ListColumns = fields
                .Where(f => f.IsActive && f.ShowInList)
                .OrderBy(f => f.SortOrder)
                .Take(CustomFieldLimits.MaxListColumns)
                .ToList();

            CustomFilters = CustomFieldListFilterState.Parse(query, fields);
            CustomFilters.FormId     = FilterFormId;
            CustomFilters.PageRoute  = "./Index";
            CustomFilters.LoadFailed = fieldsFailed;
            CustomFilters.Noun       = "contact";
            CustomFilters.Culture    = CustomFieldFormatter.ResolveCulture(CultureName);
            CustomFilters.DateFormat = TenantCtx.GetDateFormat();

            var clear = ToolbarRoute();
            clear[nameof(PageSize)] = PageSize.ToString();
            CustomFilters.ClearRoute = clear;

            // ── The list ──────────────────────────────────────────────
            try
            {
                Stats = await _contactService.GetStatsAsync(tenantId);

                // Open item 8 — every company on every view. Kept for the
                // filter dropdown; a type-ahead replaces it when Companies
                // gets its own list round.
                Companies = await _contactService.GetCompaniesLookupAsync(tenantId);

                PaginatedContacts = await _contactService.GetAllAsync(
                    tenantId, PageNumber, PageSize, CompanyId, SearchTerm, IsPrimary, CustomFilters.Filters);

                // The API pulls a page past the end back to the last one;
                // follow it so the pager and the count agree with the rows.
                PageNumber = Math.Max(PaginatedContacts.Page, 1);

                Logger.LogInformation("Loaded page {Page} with {Count} contacts (Total: {Total})",
                    PageNumber, PaginatedContacts.Items.Count, PaginatedContacts.TotalCount);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error loading contacts");
                PageError = "Failed to load contacts. Please try again.";

                PaginatedContacts = new PaginatedResult<ContactListItem>
                {
                    Items      = new List<ContactListItem>(),
                    Page       = 1,
                    PageSize   = PageSize,
                    TotalCount = 0
                };
            }
        }
    }
}
