// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Invoices/Index.cshtml.cs
// Invoice Index Page Backend - List with filters and statistics
//
// CHANGES (096 — the Invoices list joins the other lists)
//   The page loaded EVERY invoice in one go, had no search, no paging and
//   its own hand-made filter card, and every Delete or Issue dropped you
//   back on an unfiltered list. It now works exactly like Contacts,
//   Companies, Products and Leads:
//
//   1. THE SHARED TOOLBAR AND PAGER. _ListToolbar holds the search, the
//      Status filter and the issued From / To dates, with the "1–10 of
//      340" count; _Pager pages it, 10 to a page by default, and the size
//      a person picks is remembered on their browser (ListPreferences).
//      Everything is in the URL, so a filtered list can be bookmarked and
//      survives Back.
//
//   2. SEARCH, ON THE SERVER: the invoice number, the quote number, the
//      deal, the customer (company or person) and the invoice's custom
//      text / dropdown values. GET api/invoices/paged does the work, with
//      record visibility applied in the same handler as the old list.
//
//   3. SORTING. Invoice, Customer, Issued, Due, Amount and Balance are
//      links above the cards (and a Sort menu on a phone). No sort =
//      newest issue date first.
//
//   4. CUSTOM FIELD FILTERS through the shared _CustomFieldFilterPanel,
//      like every other list. (The custom values already showed on the
//      cards; now they can be filtered on.)
//
//   5. DELETE AND ISSUE KEEP YOUR PLACE. Both forms post the whole list
//      state and the handlers redirect back to it — search, status,
//      dates, sort, custom filters, page and size.
//
//   6. THE EMPTY LIST TELLS THE TRUTH. "No invoice matches these filters"
//      with a Clear link when something is filtered; "The list could not
//      be loaded" with Try again when the load failed; "No invoices yet"
//      only when there really are none.
//
//   7. DATES ARE READ AS yyyy-MM-dd STRINGS, not bound as DateTime. A
//      DateTime bound from a FORM uses the request culture, and the
//      delete/issue forms post the list state — under a Thai (Buddhist)
//      culture "2026-10-01" is a date in the year 1483. Parsing it here,
//      invariantly, means the query string and the forms read the same
//      day. An unreadable date is ignored with a sentence saying so, and
//      a From after the To applies neither and says so, rather than
//      showing an empty list.
//
//   8. THE STATISTICS NO LONGER TAKE THE LIST DOWN — they load on their
//      own; the tiles fall back to zeros if they fail.
//
// CHANGES (081 — custom fields on invoices)
//   ✅ Invoice fields switched to "Show on the list" appear on each card,
//      under the customer, as "Label: value" — up to the usual four.
//   ✅ FormatDate / FormatDateTime / FormatCurrency come from the base class.
//
// CHANGES (018 — invoice workflow)
//   ✅ "Send" on a draft is ISSUE (OnPostIssue): it gets its INV number and
//      is locked. A manual invoice over the approval limits comes back with
//      the reason and who can issue it.
//   ✅ Delete is for drafts only (the API refuses the rest).
//   ✅ Labels: Sent → "Issued", Cancelled → "Void"; drafts show "Draft".
//
// PERMISSIONS (Session 5, unchanged): invoices.read to see the page;
// POST handlers check with ValidatePermissionAsync, never the Can*
// properties (those are filled only by InitializePermissionsAsync on GET).
//
// 074b (unchanged): messages after a redirect go in TempData and _Layout
// shows them once; a message for a response that RENDERS is a plain
// property (PageError).
// =====================================================================

using System.Globalization;
using MerkaiTrial.Admin.Web.Pages.Shared;
using MerkaiTrial.Admin.Web.Services.CustomFields;
using MerkaiTrial.Admin.Web.Services.Invoices;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;

namespace MerkaiTrial.Admin.Web.Pages.Invoices
{
    public class IndexModel : AuthorizedPageModel
    {
        /// <summary>The toolbar form's id — the custom filter panel's inputs submit with it.</summary>
        public const string FilterFormId = "invoicesFilter";

        public const int DefaultPageSize = PagerVm.DefaultSize;

        private readonly IInvoiceService _invoiceService;
        private readonly ICurrentUserService _currentUserService;
        private readonly ICurrentTenantService _tenantService;
        private readonly ICustomFieldService _customFields;
        private readonly ILogger<IndexModel> _logger;

        public IndexModel(
            IInvoiceService invoiceService,
            ICurrentUserService currentUserService,
            ICurrentTenantService tenantService,
            ICustomFieldService customFields,
            IAuthorizationService authorizationService,
            ILogger<IndexModel> logger)
            : base(authorizationService, currentUserService, logger)
        {
            _customFields = customFields;
            _invoiceService = invoiceService;
            _currentUserService = currentUserService;
            _tenantService = tenantService;
            _logger = logger;
        }

        protected override string ModuleName => Modules.Invoices;

        // ── What the page renders ─────────────────────────────────────

        public PaginatedResult<InvoiceListItem> PaginatedInvoices { get; private set; } = new();

        /// <summary>The rows on this page — the cards loop over these.</summary>
        public List<InvoiceListItem> Invoices => PaginatedInvoices.Items;

        public InvoiceStatisticsDto Statistics { get; private set; } = new();

        /// <summary>081. The "Show on the list" invoice fields, in order. Empty when none or not loadable.</summary>
        public List<CustomFieldDefinitionDto> ListColumns { get; private set; } = new();

        /// <summary>096. The custom field filters: parsed, checked, ready for the API and the panel.</summary>
        public CustomFieldListFilterState CustomFilters { get; private set; } = new();

        /// <summary>A failed list load, for THIS response only (074b).</summary>
        public string? PageError { get; private set; }

        /// <summary>096 — note 7. Why a date in the URL was not applied, or null.</summary>
        public string? DateWarning { get; private set; }

        // ── List state, all in the URL ────────────────────────────────

        [BindProperty(SupportsGet = true)] public string? SearchTerm { get; set; }
        [BindProperty(SupportsGet = true)] public string? StatusFilter { get; set; }

        /// <summary>yyyy-MM-dd. A string on purpose — note 7.</summary>
        [BindProperty(SupportsGet = true)] public string? FromDate { get; set; }

        /// <summary>yyyy-MM-dd. A string on purpose — note 7.</summary>
        [BindProperty(SupportsGet = true)] public string? ToDate { get; set; }

        [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
        [BindProperty(SupportsGet = true)] public int PageSize { get; set; } = DefaultPageSize;
        [BindProperty(SupportsGet = true)] public string? Sort { get; set; }
        [BindProperty(SupportsGet = true)] public string? Dir { get; set; }

        /// <summary>The statuses the filter offers: value sent to the API → what a person calls it.</summary>
        public static readonly (string Value, string Label)[] StatusChoices =
        {
            ("Draft",         "Draft"),
            ("Sent",          "Issued"),
            ("Viewed",        "Viewed"),
            ("PartiallyPaid", "Partially paid"),
            ("Paid",          "Paid"),
            ("Overdue",       "Overdue"),
            ("Cancelled",     "Void")
        };

        private string? Status => string.IsNullOrWhiteSpace(StatusFilter) ? null : StatusFilter.Trim();

        private static DateTime? ParseIso(string? raw)
            => DateTime.TryParseExact(raw?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                      DateTimeStyles.None, out var d) ? d : null;

        // Both ends as they were typed, parsed invariantly. Applied only when
        // readable AND in order — see ResolveDates().
        private DateTime? _from, _to;
        private bool _datesResolved;

        private void ResolveDates()
        {
            if (_datesResolved) return;
            _datesResolved = true;

            var from = ParseIso(FromDate);
            var to   = ParseIso(ToDate);

            var unreadable = new List<string>();
            if (!string.IsNullOrWhiteSpace(FromDate) && from is null) unreadable.Add("From");
            if (!string.IsNullOrWhiteSpace(ToDate)   && to   is null) unreadable.Add("To");

            if (from is { } f && to is { } t && f > t)
            {
                DateWarning = "The From date is after the To date, so neither was applied.";
                return;
            }

            if (unreadable.Count > 0)
                DateWarning = $"The {string.Join(" and ", unreadable)} date could not be read, so it was not applied.";

            _from = from;
            _to   = to;
        }

        private DateTime? From { get { ResolveDates(); return _from; } }
        private DateTime? To   { get { ResolveDates(); return _to; } }

        private bool ToolbarFiltered =>
            !string.IsNullOrWhiteSpace(SearchTerm) || Status is not null || From.HasValue || To.HasValue;

        public bool IsFiltered => ToolbarFiltered || CustomFilters.HasAny;

        // ── Sorting (note 3) ──────────────────────────────────────────

        public static readonly SortOption[] SortOptions =
        {
            new("number",   "Invoice"),
            new("customer", "Customer"),
            new("issued",   "Issued",  DefaultDescending: true, AscLabel: "oldest first", DescLabel: "newest first"),
            new("due",      "Due",     AscLabel: "soonest first", DescLabel: "latest first"),
            new("total",    "Amount",  DefaultDescending: true),
            new("balance",  "Balance", DefaultDescending: true)
        };

        private ListSortVm SortState(Dictionary<string, string>? route = null) => new()
        {
            Options           = SortOptions,
            Sort              = Sort,
            Dir               = Dir,
            DefaultKey        = "issued",       // the API's order when none is asked for
            DefaultDescending = true,
            Route             = route ?? new Dictionary<string, string>()
        };

        /// <summary>The headers' and the phone menu's links: every filter and the size, never the page or the sort.</summary>
        public ListSortVm Sorter
        {
            get
            {
                var route = FilterRoute();
                route.Remove(nameof(Sort));
                route.Remove(nameof(Dir));
                route[nameof(PageSize)] = PageSize.ToString(CultureInfo.InvariantCulture);
                return SortState(route);
            }
        }

        // ── The shared components ─────────────────────────────────────

        public int TotalCount => PaginatedInvoices.TotalCount;
        public int TotalPages => TotalCount == 0 ? 1 : (int)Math.Ceiling(TotalCount / (double)PageSize);
        public int FirstRow   => TotalCount == 0 ? 0 : (PageNumber - 1) * PageSize + 1;
        public int LastRow    => Math.Min(PageNumber * PageSize, TotalCount);

        public ListToolbarVm Toolbar => new()
        {
            FormId            = FilterFormId,
            SearchName        = nameof(SearchTerm),
            SearchValue       = SearchTerm,
            SearchPlaceholder = "Number, customer, deal or quote",
            SearchLabel       = "Search invoices",
            SearchWidth       = "300px",

            Filters = new List<ListFilterVm>
            {
                new()
                {
                    Name    = nameof(StatusFilter),
                    Label   = "Filter by status",
                    Options = StatusOptions()
                }
            },

            // A date box can only hold a real date, so an unreadable one shows
            // empty — and DateWarning says it was not applied.
            Dates = new List<ListDateFilterVm>
            {
                new() { Name = nameof(FromDate), Label = "Issued from", Value = ListDateFilterVm.Iso(ParseIso(FromDate)) },
                new() { Name = nameof(ToDate),   Label = "to",          Value = ListDateFilterVm.Iso(ParseIso(ToDate)) }
            },

            // The page SIZE and the sort are carried; the page NUMBER never is (072).
            Carry      = SizeAndSort(),
            ClearRoute = SizeAndSort(),

            IsFiltered   = IsFiltered,
            TotalCount   = TotalCount,
            FirstRow     = FirstRow,
            LastRow      = LastRow,
            NounSingular = "invoice",
            NounPlural   = "invoices"
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

        /// <summary>The page size and the current sort — what a search or a Clear keeps.</summary>
        private Dictionary<string, string> SizeAndSort()
        {
            var route = SortState().Current();
            route[nameof(PageSize)] = PageSize.ToString(CultureInfo.InvariantCulture);
            return route;
        }

        /// <summary>"Clear the search and filters": the page size and the sort, nothing else.</summary>
        public Dictionary<string, string> ClearAllRoute() => SizeAndSort();

        /// <summary>The toolbar's filters and the sort, WITHOUT the custom ones — what "Clear these" keeps.</summary>
        private Dictionary<string, string> ToolbarRoute()
        {
            var route = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(SearchTerm)) route[nameof(SearchTerm)]   = SearchTerm.Trim();
            if (Status is { } st)                       route[nameof(StatusFilter)] = st;

            // Only dates that were applied travel on; an unreadable one is
            // dropped from the links the same way it was dropped from the list.
            if (From is { } f) route[nameof(FromDate)] = ListDateFilterVm.Iso(f);
            if (To   is { } t) route[nameof(ToDate)]   = ListDateFilterVm.Iso(t);

            foreach (var (k, v) in SortState().Current()) route[k] = v;
            return route;
        }

        /// <summary>Every filter — toolbar and custom — and the sort. What a page link carries.</summary>
        public Dictionary<string, string> FilterRoute()
        {
            var route = ToolbarRoute();
            foreach (var (k, v) in CustomFilters.Route) route[k] = v;
            return route;
        }

        /// <summary>FilterRoute plus the page and size — the hidden fields of the Delete and Issue forms.</summary>
        public Dictionary<string, string> StateRoute()
        {
            var route = FilterRoute();
            route[nameof(PageNumber)] = PageNumber.ToString(CultureInfo.InvariantCulture);
            route[nameof(PageSize)]   = PageSize.ToString(CultureInfo.InvariantCulture);
            return route;
        }

        private List<SelectListItem> StatusOptions()
        {
            var items = new List<SelectListItem>
            {
                new() { Value = "", Text = "All statuses", Selected = Status is null }
            };
            items.AddRange(StatusChoices.Select(c => new SelectListItem
            {
                Value    = c.Value,
                Text     = c.Label,
                Selected = string.Equals(c.Value, Status, StringComparison.OrdinalIgnoreCase)
            }));

            // "Void" is accepted by the API as another name for Cancelled; any
            // other value the dropdown does not know stays visible and selected,
            // so the list is never filtered while the box says "All statuses".
            if (Status is { } current && !items.Any(i => string.Equals(i.Value, current, StringComparison.OrdinalIgnoreCase)))
                items.Insert(1, new SelectListItem { Value = current, Text = $"{current} (not in the list)", Selected = true });

            return items;
        }

        // ==================== ON GET ====================

        public async Task<IActionResult> OnGetAsync()
        {
            var permissionCheck = await ValidatePermissionAsync(Actions.Read);
            if (permissionCheck != null) return permissionCheck;

            await InitializePermissionsAsync();

            // The URL's size, else this browser's last choice, else 10.
            PageSize   = ListPreferences.PageSize(HttpContext, "invoices");
            PageNumber = Math.Max(PageNumber, 1);

            var tenantId = _currentUserService.GetCurrentTenantId();

            // ── Custom fields: columns and filters. Never blocks the page. ──
            List<CustomFieldDefinitionDto> fields;
            var fieldsFailed = false;
            try
            {
                fields = await _customFields.GetDefinitionsAsync(CustomFieldEntityTypes.Invoice, includeInactive: false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Custom fields could not be loaded for the invoices list");
                fields = new List<CustomFieldDefinitionDto>();
                fieldsFailed = true;
            }

            ListColumns = CustomFieldColumns.Pick(fields);

            CustomFilters = CustomFieldListFilterState.Parse(Request.Query, fields);
            CustomFilters.FormId     = FilterFormId;
            CustomFilters.PageRoute  = "./Index";
            CustomFilters.LoadFailed = fieldsFailed;
            CustomFilters.Noun       = "invoice";
            CustomFilters.Culture    = CustomFieldFormatter.ResolveCulture(CultureName);
            CustomFilters.DateFormat = TenantCtx.GetDateFormat();

            var clear = ToolbarRoute();
            clear[nameof(PageSize)] = PageSize.ToString(CultureInfo.InvariantCulture);
            CustomFilters.ClearRoute = clear;

            // ── The tiles: never the reason the list fails (note 8) ──
            try
            {
                Statistics = await _invoiceService.GetStatisticsAsync(tenantId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Invoice statistics could not be loaded");
                Statistics = new InvoiceStatisticsDto();
            }

            // ── The list ──────────────────────────────────────────────
            try
            {
                var sort = SortState();
                PaginatedInvoices = await _invoiceService.GetPagedAsync(
                    pageNumber:    PageNumber,
                    pageSize:      PageSize,
                    search:        SearchTerm,
                    status:        Status,
                    fromDate:      From,
                    toDate:        To,
                    sort:          sort.ApiSort,
                    dir:           sort.ApiDir,
                    customFilters: CustomFilters.Filters);

                // The API pulls a page past the end back to the last one;
                // follow it so the pager and the count agree with the rows.
                PageNumber = Math.Max(PaginatedInvoices.Page, 1);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading invoices");
                PageError = "Couldn't load your invoices. Please try again.";
                PaginatedInvoices = new PaginatedResult<InvoiceListItem>
                {
                    Items      = new List<InvoiceListItem>(),
                    Page       = 1,
                    PageSize   = PageSize,
                    TotalCount = 0
                };
            }

            return Page();
        }

        // ==================== DELETE INVOICE ====================

        public async Task<IActionResult> OnPostDeleteAsync(Guid id)
        {
            var permissionCheck = await ValidatePermissionAsync(Actions.Delete);
            if (permissionCheck != null) return permissionCheck;

            // Every path REDIRECTS, so TempData is right here: the layout
            // shows the message once on the page the redirect lands on.
            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();

                // (018) Drafts only — the API refuses the rest; this is the
                // friendly early answer.
                var invoice = await _invoiceService.GetByIdAsync(tenantId, id);
                if (invoice != null && invoice.Status != "Draft")
                {
                    TempData["ErrorMessage"] = $"Invoice {invoice.Number} has been issued, so it can't be deleted. Open it and void it instead.";
                }
                else
                {
                    await _invoiceService.DeleteAsync(tenantId, id);
                    TempData["SuccessMessage"] = "Draft invoice deleted.";
                }
            }
            catch (KeyNotFoundException)
            {
                TempData["ErrorMessage"] = "That invoice no longer exists.";
            }
            catch (InvalidOperationException ex)
            {
                TempData["ErrorMessage"] = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting invoice {Id}", id);
                TempData["ErrorMessage"] = "Failed to delete invoice. Please try again.";
            }

            // Back to the same page of the same list (note 5).
            return RedirectToPage(ReturnRouteFrom(Request.Form));
        }

        // ==================== ISSUE (018) ====================

        public async Task<IActionResult> OnPostIssueAsync(Guid invoiceId)
        {
            var permissionCheck = await ValidatePermissionAsync(Actions.Update);
            if (permissionCheck != null) return permissionCheck;

            try
            {
                var number = await _invoiceService.IssueAsync(invoiceId);
                TempData["SuccessMessage"] = $"Invoice issued as {number}.";
            }
            catch (InvalidOperationException ex)
            {
                // e.g. "This invoice is over your workspace's limits, so … needs to issue it."
                TempData["ErrorMessage"] = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error issuing invoice {Id}", invoiceId);
                TempData["ErrorMessage"] = "Failed to issue the invoice. Please try again.";
            }

            return RedirectToPage(ReturnRouteFrom(Request.Form));
        }

        /// <summary>
        /// The list state the form posted, as route values for the redirect.
        /// The toolbar's fields and the sort bind to the properties above
        /// (dates as strings — note 7); the custom ones are read from the form.
        /// A RouteValueDictionary — RedirectToPage reflects over plain objects.
        /// </summary>
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
            values[nameof(PageSize)]   = PagerVm.Sizes.Contains(PageSize) ? PageSize : DefaultPageSize;
            return values;
        }

        // ==================== HELPER METHODS ====================

        public string GetStatusBadgeClass(string status) => status switch
        {
            "Draft"         => "bg-secondary",
            "Sent"          => "bg-primary",
            "Viewed"        => "bg-info",
            "PartiallyPaid" => "bg-warning text-dark",
            "Paid"          => "bg-success",
            "Overdue"       => "bg-danger",
            "Cancelled"     => "bg-dark",
            _               => "bg-secondary"
        };

        public string GetStatusIcon(string status) => status switch
        {
            "Draft"         => "bi-file-earmark",
            "Sent"          => "bi-send",
            "Viewed"        => "bi-eye",
            "PartiallyPaid" => "bi-coin",
            "Paid"          => "bi-check-circle-fill",
            "Overdue"       => "bi-exclamation-triangle-fill",
            "Cancelled"     => "bi-x-circle",
            _               => "bi-file-earmark"
        };

        private CultureInfo? _cfCulture;
        private string? _cfDateFormat;

        /// <summary>
        /// 081. This card's custom values that are set, as (label, text) —
        /// an unset field is left off the card rather than shown as a dash.
        /// </summary>
        public IEnumerable<(CustomFieldDefinitionDto Field, string Text)> CustomValues(InvoiceListItem invoice)
        {
            if (ListColumns.Count == 0) yield break;

            _cfCulture    ??= CustomFieldFormatter.ResolveCulture(CultureName);
            _cfDateFormat ??= TenantCtx.GetDateFormat();

            foreach (var col in ListColumns)
            {
                var text = CustomFieldColumns.Value(col, invoice.CustomFieldValues, _cfCulture, _cfDateFormat);
                if (text.Length > 0) yield return (col, text);
            }
        }

        /// <summary>074b. Delegates to CurrencyConfiguration (the one symbol table).</summary>
        public string GetSymbol(string? code) =>
            string.IsNullOrWhiteSpace(code)
                ? _tenantService.GetCurrencySymbol()
                : CurrencyConfiguration.GetCurrencySymbol(code);

        public string GetRelativeDate(DateTime? date)
        {
            if (!date.HasValue) return "No due date";

            var days = (date.Value - DateTime.UtcNow).Days;

            if (days < 0)  return $"Overdue by {Math.Abs(days)} days";
            if (days == 0) return "Due today";
            if (days == 1) return "Due tomorrow";
            if (days <= 7) return $"Due in {days} days";
            return _tenantService.FormatDate(date.Value);
        }

        public bool IsOverdue(InvoiceListItem invoice) => invoice.IsOverdue;

        public string GetStatusDisplay(string status) => status switch
        {
            "Draft"         => "Draft",
            "Sent"          => "Issued",
            "Viewed"        => "Viewed",
            "PartiallyPaid" => "Partially Paid",
            "Paid"          => "Paid",
            "Overdue"       => "Overdue",
            "Cancelled"     => "Void",
            _               => status
        };

        /// <summary>A draft's DRAFT-XXXX placeholder isn't a number people should quote.</summary>
        public static string DisplayNumber(string number)
            => number.StartsWith("DRAFT-", StringComparison.Ordinal) ? "Draft" : number;
    }
}
