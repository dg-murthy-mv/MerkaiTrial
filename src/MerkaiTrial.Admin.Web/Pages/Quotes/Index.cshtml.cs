// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Quotes/Index.cshtml.cs
//
// COMPLETE FILE — replaces the 017 version.
//
// WHAT WAS WRONG WITH THE OLD PAGE
//
//   1. QUOTES COULD DISAPPEAR. The board had six columns — Draft,
//      PendingApproval, Approved, Sent, Viewed, Accepted — but the app has
//      nine statuses. A Rejected, Expired or Revised quote was rendered
//      nowhere at all. With 7 quotes and 6 Accepted, exactly one was
//      invisible on the page that is supposed to list them.
//      StatusOrder below is now the single list of columns and covers
//      every status; anything unrecognised lands in an "Other" column
//      rather than vanishing.
//
//   2. THE COLUMN COUNTS COULD LIE. "Sent" and "Accepted" printed
//      Statistics.SentQuotes / Statistics.AcceptedQuotes — workspace-wide
//      totals — above cards drawn from the FILTERED list. Apply a date
//      filter and the header said 12 over two cards. Every per-column
//      number now comes from the same list the cards come from, and the
//      workspace totals are in their own strip, labelled as such. Nothing
//      in this file mixes the two.
//
//   3. THE FILTERS EXISTED BUT HAD NO UI. StatusFilter, FromDate and
//      ToDate were bound from the query string and never surfaced, so the
//      only way to use them was to hand-write a URL.
//
// NEW: a List view beside the board, because a board is the wrong shape
// for a handful of records — six of its columns were empty and the page
// scrolled for no reason. The choice is remembered per browser.
//
// ── 028a ─────────────────────────────────────────────────────────────
//   4. DELETE WAS LESS CAREFUL HERE THAN ON THE DETAIL PAGE. Detail hides
//      its Delete button whenever the quote is locked (Accepted, Rejected,
//      or it has an invoice) AND re-checks the invoice server-side before
//      deleting. This handler had neither check — it called DeleteAsync
//      straight after the permission check — so the list was a way round
//      a guard written specifically to stop an invoiced quote being
//      deleted. OnPostDelete now runs both checks, and Index.cshtml no
//      longer draws the button for an Accepted or Rejected quote.
//      This needs IInvoiceService, which is the only new constructor
//      argument; it is already registered in DI because DetailModel uses
//      it, so nothing else has to change.
// =====================================================================

using System.Globalization;
using MerkaiTrial.Admin.Web.Services.Invoices;
using MerkaiTrial.Admin.Web.Services.Quotes;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Admin.Web.Pages.Quotes
{
    public class IndexModel : AuthorizedPageModel
    {
        private readonly IQuoteService _quoteService;
        private readonly ICurrentUserService _currentUserService;
        private readonly ICurrentTenantService _tenantService;
        private readonly IInvoiceService _invoiceService;
        private readonly IQuoteApprovalService _approvals;
        private readonly ILogger<IndexModel> _logger;

        protected override string ModuleName => Modules.Quotes;

        // IInvoiceService is new in 028a — it is what lets OnPostDelete refuse
        // an invoiced quote the way the Detail page already does. It is
        // already registered in Startup/AdminWebServiceRegistration.cs
        // (DetailModel takes it), so no registration change is needed.
        public IndexModel(
            IQuoteService quoteService,
            ICurrentUserService currentUserService,
            ICurrentTenantService tenantService,
            IInvoiceService invoiceService,
            IQuoteApprovalService approvals,
            IAuthorizationService authorizationService,
            ILogger<IndexModel> logger)
            : base(authorizationService, currentUserService, logger)
        {
            _quoteService = quoteService;
            _currentUserService = currentUserService;
            _tenantService = tenantService;
            _invoiceService = invoiceService;
            _approvals = approvals;
            _logger = logger;
        }

        // ── Data ──────────────────────────────────────────────────────
        public List<QuoteListItem> Quotes { get; set; } = new();
        public QuoteStatisticsDto Statistics { get; set; } = new();

        /// <summary>True when the load threw — so the view doesn't claim the workspace is empty.</summary>
        public bool LoadFailed { get; private set; }

        /// <summary>Approval requests waiting for the current user's decision.</summary>
        public int PendingForMeCount { get; private set; }

        // ── Tenant context (use in view, never hardcode ₹ / $ / INR) ─
        public string TenantCurrencySymbol { get; private set; } = string.Empty;
        public string TenantCurrencyCode { get; private set; } = string.Empty;

        // ── Filters ───────────────────────────────────────────────────
        [BindProperty(SupportsGet = true)] public string? StatusFilter { get; set; }
        [BindProperty(SupportsGet = true)] public DateTime? FromDate { get; set; }
        [BindProperty(SupportsGet = true)] public DateTime? ToDate { get; set; }

        /// <summary>
        /// "list" or "board". PageModel has no View member of its own (View()
        /// is a Controller thing), so this name is free.
        /// </summary>
        [BindProperty(SupportsGet = true)] public string? View { get; set; }

        [TempData] public string? ErrorMessage { get; set; }
        [TempData] public string? SuccessMessage { get; set; }

        // ── GET ───────────────────────────────────────────────────────
        public async Task<IActionResult> OnGet()
        {
            var check = await ValidatePermissionAsync(Actions.Read);
            if (check != null) return check;

            await InitializePermissionsAsync();

            try
            {
                // Load tenant context before rendering.
                TenantCurrencySymbol = _tenantService.GetCurrencySymbol();
                TenantCurrencyCode = _tenantService.GetCurrencyCode();

                var tenantId = _currentUserService.GetCurrentTenantId();

                Statistics = await _quoteService.GetStatisticsAsync(tenantId);

                Quotes = await _quoteService.GetAllAsync(
                    tenantId, null, StatusFilter, FromDate, ToDate);

                // Approvals badge — never blocks the page.
                try
                {
                    PendingForMeCount = (await _approvals.GetPendingAsync()).Count;
                }
                catch (Exception apEx)
                {
                    _logger.LogWarning(apEx, "Could not load pending approvals count");
                }

                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load quotes");
                // Without this the view falls into its "No quotes yet — create
                // the first one" state underneath a red "failed to load"
                // banner, which reads as "your data is gone".
                LoadFailed = true;
                ErrorMessage = "Failed to load quotes. Please try again.";
                return Page();
            }
        }

        // ── DELETE ────────────────────────────────────────────────────
        public async Task<IActionResult> OnPostDelete(Guid quoteId)
        {
            try
            {
                var check = await ValidatePermissionAsync(Actions.Delete);
                if (check != null) return check;

                var tenantId = _currentUserService.GetCurrentTenantId();

                // The same two guards the Detail page enforces. Without them
                // the list was a way round them: Detail hides Delete for an
                // Accepted or Rejected quote and refuses an invoiced one, and
                // this handler used to call DeleteAsync with neither check —
                // so an accepted, invoiced quote could be deleted from the
                // list even though its own page says "Invoiced — View Only".
                // The view hides the button too; this is the half that still
                // holds when somebody posts the form by hand.
                var quote = await _quoteService.GetByIdAsync(tenantId, quoteId);

                if (quote is null)
                {
                    ErrorMessage = "That quote no longer exists.";
                    return Back();
                }

                if (quote.Status is "Accepted" or "Rejected")
                {
                    ErrorMessage = quote.Status == "Accepted"
                        ? "This quote has been accepted — it can't be deleted. Open it and cancel it instead."
                        : "This quote has been rejected — it can't be deleted.";
                    return Back();
                }

                var invoices = await _invoiceService.GetAllAsync(tenantId, quoteId: quoteId);
                if (invoices?.Any() == true)
                {
                    ErrorMessage = "This quote has an invoice — it cannot be deleted.";
                    return Back();
                }

                await _quoteService.DeleteAsync(tenantId, quoteId);
                SuccessMessage = "Quote deleted.";
            }
            catch (InvalidOperationException ex)
            {
                ErrorMessage = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete quote {QuoteId}", quoteId);
                ErrorMessage = "Failed to delete quote. Please try again.";
            }

            return Back();
        }

        // ── UPDATE STATUS ─────────────────────────────────────────────
        public async Task<IActionResult> OnPostUpdateStatus(Guid quoteId, string status)
        {
            try
            {
                var check = await ValidatePermissionAsync(Actions.Update);
                if (check != null) return check;

                var tenantId = _currentUserService.GetCurrentTenantId();
                await _quoteService.UpdateStatusAsync(tenantId, quoteId, status);
                SuccessMessage = $"Quote moved to {Label(status)}.";
            }
            catch (InvalidOperationException ex)
            {
                ErrorMessage = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update quote status {QuoteId}", quoteId);
                ErrorMessage = "Failed to update quote status. Please try again.";
            }

            return Back();
        }

        /// <summary>
        /// Back to the list the user was actually looking at. A bare
        /// RedirectToPage() drops the filter and the view, so deleting one
        /// quote out of a filtered board dumped you into an unfiltered list.
        /// </summary>
        private IActionResult Back() => RedirectToPage(new
        {
            View,
            StatusFilter,
            FromDate = FromDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ToDate = ToDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        });

        // =============================================================
        // STATUSES — one list, used by the board, the list and the filter
        // bar, so they can never drift apart again
        // =============================================================

        /// <param name="Key">The value stored on Quote.Status.</param>
        /// <param name="Label">What a person calls it.</param>
        /// <param name="Icon">Bootstrap icon name.</param>
        /// <param name="Accent">Hex used for the column rule and the count pill.</param>
        /// <param name="Badge">Bootstrap classes for the status badge in list view.</param>
        public record StatusMeta(string Key, string Label, string Icon, string Accent, string Badge);

        /// <summary>
        /// Every status the app can put on a quote, in the order a quote
        /// travels through them. The old board listed six of the nine.
        /// </summary>
        public static readonly StatusMeta[] StatusOrder =
        {
            new("Draft",           "Draft",             "bi-file-earmark",    "#64748b", "bg-secondary"),
            new("PendingApproval", "Awaiting approval", "bi-hourglass-split", "#fd7e14", "bg-warning text-dark"),
            new("Approved",        "Approved",          "bi-patch-check",     "#20c997", "bg-success-subtle text-success-emphasis border border-success-subtle"),
            new("Sent",            "Sent",              "bi-send",            "#0d6efd", "bg-primary"),
            new("Viewed",          "In review",         "bi-eye",             "#0dcaf0", "bg-info text-dark"),
            new("Accepted",        "Accepted",          "bi-check-circle",    "#198754", "bg-success"),
            new("Rejected",        "Rejected",          "bi-x-circle",        "#dc3545", "bg-danger"),
            new("Expired",         "Expired",           "bi-calendar-x",      "#b45309", "bg-warning text-dark"),
            new("Revised",         "Revised",           "bi-arrow-repeat",    "#1e293b", "bg-dark")
        };

        public static StatusMeta Meta(string? status)
            => StatusOrder.FirstOrDefault(s => string.Equals(s.Key, status, StringComparison.OrdinalIgnoreCase))
               ?? new StatusMeta(status ?? "", string.IsNullOrWhiteSpace(status) ? "Other" : status!,
                                 "bi-question-circle", "#94a3b8", "bg-secondary");

        public static string Label(string? status) => Meta(status).Label;

        /// <summary>Kept for any view still calling it.</summary>
        public string GetStatusBadgeClass(string status) => Meta(status).Badge;

        // One pass over the list, not twenty. Every count, total and column
        // below reads from this, so a card and its header can never disagree.
        private Dictionary<string, List<QuoteListItem>>? _byStatus;

        private Dictionary<string, List<QuoteListItem>> ByStatus =>
            _byStatus ??= Quotes
                .GroupBy(q => q.Status ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        public List<QuoteListItem> GetQuotesByStatus(string status)
            => ByStatus.TryGetValue(status, out var list) ? list : new List<QuoteListItem>();

        public int CountOf(string status) => GetQuotesByStatus(status).Count;

        public decimal ValueOf(string status) => GetQuotesByStatus(status).Sum(q => q.GrandTotal);

        /// <summary>
        /// Quotes whose status matches no known column. Should always be
        /// empty — it exists so that a status added to the domain and not to
        /// StatusOrder shows up as a visible oddity instead of silently
        /// disappearing the way Rejected used to.
        /// </summary>
        public List<QuoteListItem> Unclassified =>
            Quotes.Where(q => !StatusOrder.Any(s => string.Equals(s.Key, q.Status, StringComparison.OrdinalIgnoreCase)))
                  .ToList();

        // ── What's on screen right now ────────────────────────────────

        public bool HasFilter =>
            !string.IsNullOrWhiteSpace(StatusFilter) || FromDate.HasValue || ToDate.HasValue;

        public int ShownCount => Quotes.Count;

        public decimal ShownValue => Quotes.Sum(q => q.GrandTotal);

        /// <summary>"list" unless the query string says otherwise.</summary>
        public string ActiveView =>
            string.Equals(View, "board", StringComparison.OrdinalIgnoreCase) ? "board" : "list";

        /// <summary>For an &lt;input type="date"&gt;, which only accepts ISO in the Gregorian calendar.</summary>
        public static string IsoDate(DateTime? d)
            => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;

        // ── VIEW HELPERS ──────────────────────────────────────────────
        //
        // FormatDate, FormatDateTime and FormatCurrency are NOT redeclared
        // here. AuthorizedPageModel already provides all three, routed
        // through ICurrentTenantService. The 017 page model shadowed them,
        // which raised CS0108 twice and made FormatCurrency resolve to
        // whichever overload happened to match arity. Deleted; the base
        // class is the one source of truth.

        /// <summary>
        /// A compact amount for a 286px card: ₹1.25 Cr / ฿706k, with the full
        /// figure in the card's tooltip and in list view.
        ///
        /// Thresholds are compared BEFORE dividing, so 9,999,999 reads
        /// "₹1 Cr" rather than "₹100 L", and 999,999 reads "$1M" rather than
        /// "$1000k". Negative amounts fall back to the full formatter, which
        /// places the sign the way the tenant's culture does — "₹-2 L" is
        /// not a thing anyone writes.
        /// </summary>
        public string FormatCompact(decimal amount)
        {
            if (amount < 0) return FormatCurrency(amount);

            var sym = TenantCurrencySymbol;

            // India and its neighbours read lakh and crore.
            if (string.Equals(TenantCurrencyCode, "INR", StringComparison.OrdinalIgnoreCase))
            {
                if (amount >= 9_950_000m) return $"{sym}{Math.Round(amount / 10_000_000m, 2)} Cr";
                if (amount >= 100_000m) return $"{sym}{Math.Round(amount / 100_000m, 2)} L";
                return FormatCurrency(amount);
            }

            if (amount >= 999_500m) return $"{sym}{Math.Round(amount / 1_000_000m, 2)}M";
            if (amount >= 10_000m) return $"{sym}{Math.Round(amount / 1_000m, 1)}k";
            return FormatCurrency(amount);
        }

        // ── Expiry, in the tenant's day, not the server's ─────────────

        /// <summary>
        /// True when two instants fall on the same day for THIS tenant.
        ///
        /// FormatDate already converts UTC to the tenant's timezone, so
        /// comparing the two rendered dates answers "is this today?"
        /// correctly without this page needing to know the timezone.
        /// Comparing DateTime.UtcNow.Date directly got it wrong for the
        /// first five and a half hours of every Indian day.
        /// </summary>
        private bool SameTenantDay(DateTime a, DateTime b)
            => string.Equals(FormatDate(a), FormatDate(b), StringComparison.Ordinal);

        /// <summary>Whole days between today and the expiry. 0 means it expires today.</summary>
        public int DaysLeft(DateTime expiresUtc)
        {
            if (SameTenantDay(expiresUtc, DateTime.UtcNow)) return 0;

            var raw = (int)Math.Round((expiresUtc - DateTime.UtcNow).TotalDays,
                                      MidpointRounding.AwayFromZero);

            // Different tenant-local days, so the answer must not be 0 — the
            // UTC arithmetic can land there inside the timezone offset.
            if (raw == 0) return expiresUtc > DateTime.UtcNow ? 1 : -1;
            return raw;
        }

        /// <summary>
        /// "Expires in 3 days" / "Expired 2 days ago". Only called for a
        /// quote that is actually out with the customer.
        /// </summary>
        public string ExpiryNote(DateTime expiresUtc)
        {
            if (!HasExpiry(expiresUtc)) return "No expiry set";

            var d = DaysLeft(expiresUtc);
            if (d < 0) return d == -1 ? "Expired yesterday" : $"Expired {-d} days ago";
            if (d == 0) return "Expires today";
            if (d == 1) return "Expires tomorrow";
            return $"Expires in {d} days";
        }

        /// <summary>
        /// A quote saved without an expiry carries default(DateTime), which
        /// would otherwise render as "Expired 739123 days ago" in red.
        /// </summary>
        public static bool HasExpiry(DateTime expiresUtc)
            => expiresUtc != default && expiresUtc.Year > 1900;

        public static bool IsLive(string? status)
            => string.Equals(status, "Sent", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "Viewed", StringComparison.OrdinalIgnoreCase);
    }
}
