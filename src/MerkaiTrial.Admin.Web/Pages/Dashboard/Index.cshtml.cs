// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Dashboard/Index.cshtml.cs
//
// ✅ SESSION 5 CLEANUP — AppPageModel retirement
//   AppPageModel -> AuthorizedPageModel. This was the last of the two
//   remaining AppPageModel subclasses; AppPageModel.cs can now be DELETED.
//
//   ModuleName returns string.Empty ON PURPOSE. Dashboard aggregates Leads,
//   Deals, Quotes and Invoices and owns none of them, so there is no single
//   module to scope page-level policies to. It uses the UserCan* cross-module
//   helpers instead. AuthorizedPageModel now throws if a module-scoped method
//   (ValidatePermissionAsync / Can*Async) is called on a page in this state,
//   so the choice fails loudly rather than silently building a policy named
//   ".read" and denying everything.
//
//   NO page-entry gate: every role in the matrix has read on every module, so
//   gating entry would deny nobody. The per-panel UserCanRead checks below are
//   the meaningful ones, and they now also guard the DATA LOADS, not just the
//   buttons — previously the page fetched lead/deal/quote/invoice stats
//   regardless of permission and relied on the view to hide them.
//
//   The old CanCreate("Leads")/CanRead("Deals") calls were AppPageModel methods
//   doing a case-SENSITIVE claim read. They happened to work, because
//   "Leads.create" matched the claim casing exactly — but that was luck, and
//   the same pattern is what silently broke PermissionHandler before. UserCan*
//   compares case-insensitively.
//
// RECORD VISIBILITY (015):
//   The lead KPIs are what THIS user can see (a rep with Own visibility
//   sees their own leads). The LEAD QUOTA is the whole workspace — the plan
//   limit counts every lead — so it now reads LeadQuotaUsed (from
//   LeadStatsDto.QuotaUsed), not TotalLeads. Before, a rep saw "1 / 2000"
//   while the workspace was at 10.
//
// RECORD VISIBILITY — DEALS (016):
//   Same split for deals. The deal KPIs (Active, Won, Lost, Pipeline value,
//   Top deals) are what THIS user can see. The DEAL QUOTA bar reads
//   DealQuotaUsed (GetDealsResponse.QuotaUsed — every deal in the
//   workspace). It used to read ActiveDeals, which was also wrong before
//   visibility: it counted only open deals, and only from the first 20.
//   Deals now load with pageSize 500 so the KPIs cover the whole book.
//
// CHANGES (043)
//
//   1. ★ THE DEAL KPIs WERE WRONG FOR ANY TENANT WITH THEIR OWN PIPELINE.
//      Four lines counted stages by NAME:
//
//        ActiveDeals = deals.Count(d => d.Stage is not
//                        ("ClosedWon" or "ClosedLost" or "Won" or "Lost"));
//        DealsWon    = deals.Count(d => d.Stage is "ClosedWon" or "Won");
//        DealsLost   = deals.Count(d => d.Stage is "ClosedLost" or "Lost");
//
//      For a workspace running Prospect → Demo → Commercials → Closed,
//      EVERY deal counts as active, Won and Lost are both zero, and the
//      win rate reads 0% forever — on the first screen anyone sees. Same
//      bug class as 031 and 036; this is the last of it.
//
//      Stages now come from the tenant's own pipeline and are judged by
//      StageCategory, so a stage named "Contract Signed" counts as won
//      because it IS won. GetStageClass follows the same route, and the
//      badge on Top Deals shows the stage's NAME rather than its key.
//
//      A deal sitting in a stage the workspace no longer has — a ghost
//      key, the very thing 036 fixed at the source — is counted and
//      reported separately instead of being quietly filed as "active".
//
//   2. FISCAL PERIOD SELECTOR (the 035 work, finally on screen). The
//      dashboard now answers "how did we do in FY 2026-27" rather than
//      only "how are we doing in total". April-to-March for India,
//      January-to-December for Thailand and the Philippines, from the
//      tenant's own setting — IFiscalYearService decides, not this page.
//
//      HONESTY, DELIBERATELY: the period drives the DEAL panels, which
//      are computed here from deals already loaded. The lead, quote and
//      invoice panels are aggregated inside the API, and their endpoints
//      do not yet accept a range — 043 widens the three HANDLERS, but the
//      controllers and Admin.Web services still call them without one. So
//      those cards are LABELLED "All time" rather than left to imply they
//      follow the selector. A dashboard that silently mixes periods is
//      worse than one that admits which is which.
//
//      Deals with no expected close date are excluded from a period and
//      counted separately, because silently dropping them is how a
//      forecast quietly loses half a million.
//
// CHANGES (043a)
//
//   The quote and invoice panels now follow the selector too: their
//   endpoints take from / toExclusive, filtered server side on the ISSUE
//   date with the DATE-ONLY range (034's distinction — a timezone shift
//   there moves a 31 March invoice into the wrong GST year). Their chips
//   went from grey to blue, which is the whole point of having had
//   chips: the page told the truth before the wiring existed, and tells
//   a different truth now that it does.
//
//   Recent Quotes follows the period as well. Its list filter is
//   INCLUSIVE at both ends, written long before any of this, so the
//   half-open bound is converted with a single AddTicks(-1) at the call
//   site rather than by loosening anything.
//
//   LEADS ARE STILL ALL-TIME. /api/leads/stats accepts the range as of
//   043a, but ILeadService.GetStatsAsync does not pass it yet — that
//   file is the one piece still outstanding. The lead card keeps its
//   grey "All time" chip until it does, which is exactly what the chip
//   is for.
//
// EARLIER FIXES (unchanged):
//   1. WonValue label clarified — ExpectedValue of ClosedWon deals,
//      NOT actual collected. Actual collected = PaidAmount (invoices).
//   2. Leads breakdown — ConvertedLeads now included in bar segments.
//   3. ConversionRate property added (ConvertedLeads / TotalLeads).
//   4. OverdueReminders + TodayActivities exposed for activities panel.
//   5. ActualCollected property added = PaidAmount (from invoices).
// =====================================================================
using MerkaiTrial.Admin.Web.Services.Deals;
using MerkaiTrial.Admin.Web.Services.Invoices;
using MerkaiTrial.Admin.Web.Services.Leads;
using MerkaiTrial.Admin.Web.Services.Pipeline;          // 043: tenant stages
using MerkaiTrial.Admin.Web.Services.Quotes;
using MerkaiTrial.Application.Commands.PipelineStages;  // 043: PipelineStageDto
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Fiscal;          // 043: IFiscalYearService
using MerkaiTrial.Application.Services.Tenants;
using MerkaiTrial.Domain.Entities;                      // 043: StageCategory
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;               // 043: period dropdown
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Admin.Web.Pages.Dashboard
{
    public class IndexModel : AuthorizedPageModel
    {
        private readonly ILeadService          _leadService;
        private readonly IDealService          _dealService;
        private readonly IQuoteService         _quoteService;
        private readonly IInvoiceService       _invoiceService;
        private readonly ICurrentUserService   _currentUserService;
        private readonly ICurrentTenantService _tenantService;
        private readonly IPipelineStageService _stageService;     // 043
        private readonly IFiscalYearService    _fiscal;           // 043
        private readonly ILogger<IndexModel>   _logger;

        public IndexModel(
            ILeadService          leadService,
            IDealService          dealService,
            IQuoteService         quoteService,
            IInvoiceService       invoiceService,
            ICurrentUserService   currentUserService,
            ICurrentTenantService tenantService,
            IPipelineStageService stageService,                   // 043
            IFiscalYearService    fiscal,                         // 043
            IAuthorizationService authorizationService,
            ILogger<IndexModel>   logger)
            : base(authorizationService, currentUserService, logger)
        {
            _leadService        = leadService;
            _dealService        = dealService;
            _quoteService       = quoteService;
            _invoiceService     = invoiceService;
            _currentUserService = currentUserService;
            _tenantService      = tenantService;
            _stageService       = stageService;
            _fiscal             = fiscal;
            _logger             = logger;
        }

        // ✅ Intentionally empty — cross-module page. See header note.
        protected override string ModuleName => string.Empty;

        // ── Tenant context ────────────────────────────────────────────
        public string CurrencySymbol { get; private set; } = string.Empty;
        public string CurrencyCode   { get; private set; } = string.Empty;
        public string TaxLabel       { get; private set; } = "Tax";
        public string TenantName     { get; private set; } = string.Empty;

        // ── Lead KPIs ─────────────────────────────────────────────────
        public int TotalLeads      { get; set; }
        public int NewLeads        { get; set; }
        public int WorkingLeads    { get; set; }
        public int QualifiedLeads  { get; set; }
        public int ConvertedLeads  { get; set; }

        /// <summary>Every lead in the workspace — for the plan quota bar and the Add Lead limit.</summary>
        public int LeadQuotaUsed   { get; set; }

        // ✅ FIX 4: activity counts from lead stats
        public int OverdueReminders { get; set; }
        public int TodayActivities  { get; set; }

        // ✅ FIX 3: conversion rate
        public string ConversionRate
        {
            get
            {
                if (TotalLeads == 0) return "0%";
                return $"{(int)Math.Round(ConvertedLeads * 100.0 / TotalLeads)}%";
            }
        }

        // ── 043: the period ───────────────────────────────────────────

        /// <summary>The key of the selected period — "fy:2026", "fy:2026:q1", or "all".</summary>
        public string SelectedPeriodKey { get; private set; } = AllTimeKey;

        /// <summary>"FY 2026-27", "Q1 (Apr-Jun 2026)", "All time".</summary>
        public string SelectedPeriodLabel { get; private set; } = "All time";

        /// <summary>The dropdown, grouped into financial years and quarters.</summary>
        public List<SelectListItem> PeriodOptions { get; private set; } = new();

        /// <summary>False for "all" — the view drops the period wording entirely.</summary>
        public bool HasPeriod => !string.Equals(SelectedPeriodKey, AllTimeKey, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The label with the months trimmed off — "Q1" rather than
        /// "Q1 (Apr-Jun 2026)" — for the chip that sits beside a KPI's name.
        /// </summary>
        public string SelectedPeriodShort
        {
            get
            {
                if (!HasPeriod) return "All time";

                var bracket = SelectedPeriodLabel.IndexOf(" (", StringComparison.Ordinal);
                return bracket > 0 ? SelectedPeriodLabel[..bracket] : SelectedPeriodLabel;
            }
        }

        /// <summary>
        /// The workspace's financial year in words — "April to March" — so
        /// nobody has to guess why FY 2026-27 starts in April. Taken from the
        /// tenant's own setting, which falls back to the country's.
        /// </summary>
        public string FiscalYearDescription { get; private set; } = string.Empty;

        /// <summary>
        /// Deals with no expected close date. They cannot belong to a period,
        /// so they are left OUT of the numbers above and reported here — a
        /// forecast that quietly drops them is worse than one that says so.
        /// </summary>
        public int UndatedDeals { get; private set; }

        /// <summary>
        /// Deals sitting in a stage this workspace no longer has. 036 stopped
        /// these being created; any that remain are from before, and they are
        /// counted here rather than silently folded into "active".
        /// </summary>
        public int GhostStageDeals { get; private set; }

        /// <summary>
        /// False when the tenant's pipeline could not be fetched. The deal
        /// panels then cannot be trusted, and the view says so rather than
        /// showing numbers built on an empty stage list.
        /// </summary>
        public bool StagesLoaded { get; private set; }

        public const string AllTimeKey = "all";

        // ── Deal KPIs ─────────────────────────────────────────────────
        public int     ActiveDeals   { get; set; }

        /// <summary>Every deal in the workspace — for the plan quota bar.</summary>
        public int     DealQuotaUsed { get; set; }
        public int     DealsWon      { get; set; }
        public int     DealsLost     { get; set; }
        public decimal PipelineValue { get; set; }

        // ✅ FIX 1: WonValue = sum of ExpectedValue of ClosedWon deals
        //    This is NOT the same as actual money collected.
        //    Actual collected = PaidAmount (from invoices).
        public decimal WonValue      { get; set; }

        // ── Quote KPIs ────────────────────────────────────────────────
        public int     TotalQuotes       { get; set; }
        public int     QuotesSent        { get; set; }
        public int     QuotesAccepted    { get; set; }
        public decimal QuotesTotalValue  { get; set; }
        public decimal QuotesPendingValue { get; set; }

        // ── Invoice KPIs ──────────────────────────────────────────────
        public int     TotalInvoices   { get; set; }
        public int     PaidInvoices    { get; set; }
        public int     OverdueInvoices { get; set; }
        public decimal TotalInvoiced   { get; set; }
        public decimal PaidAmount      { get; set; }   // ✅ actual collected cash
        public decimal UnpaidAmount    { get; set; }

        // ── Lists ─────────────────────────────────────────────────────
        public List<DealListItem>  TopDeals     { get; set; } = new();
        public List<QuoteListItem> RecentQuotes { get; set; } = new();

        [TempData] public string? ErrorMessage   { get; set; }
        [TempData] public string? SuccessMessage { get; set; }

        // ── GET ───────────────────────────────────────────────────────
        //
        // 043: `period` arrives on the query string ("?period=fy:2026:q1"),
        // so a dashboard someone is looking at can be bookmarked and shared
        // and still mean the same thing tomorrow. No posting, no session
        // state — a report you cannot send to your co-founder is half a
        // report.
        public async Task<IActionResult> OnGet(string? period = null)
        {
            // ✅ SuperAdmin doesn't have tenant context — redirect to Admin,
            // UNLESS they're deliberately previewing a tenant via ViewAs
            // (ViewingAs claim only exists during an active /Admin/ViewAs
            // session — see DemoAuthenticationHandler). Without this
            // exception, ViewAs would set IsSuperAdmin=true (needed so the
            // Exit control and /Admin/* access keep working) and this guard
            // would immediately bounce every preview straight back to /Admin.
            if (HttpContext.User.HasClaim("IsSuperAdmin", "true")
                && !HttpContext.User.HasClaim("ViewingAs", "true"))
                return RedirectToPage("/Admin/Index");

            CurrencySymbol = _tenantService.GetCurrencySymbol();
            CurrencyCode   = _tenantService.GetCurrencyCode();
            TaxLabel       = _tenantService.GetTaxLabel();
            TenantName     = _tenantService.GetTenantName();

            var tenantId = _currentUserService.GetCurrentTenantId();

            _logger.LogInformation(
                "Loading dashboard for tenant {TenantId} ({Currency})", tenantId, CurrencyCode);

            // ✅ Each load is gated on that module's read permission. Previously
            // every panel was fetched unconditionally and merely hidden in the
            // view, so a role without (say) quotes.read still had the data sent
            // to the browser. Denied panels stay null and render empty.
            //
            // Routed through SafeAsyncIf so T is INFERRED from each service call.
            // A ternary here needs both branches to agree on T, which meant
            // naming five DTO types by hand — and getting one wrong (GetDealsResponse
            // vs PaginatedResult<DealListItem>) is a CS0173. The helper removes
            // the guesswork: skipped panels return null, exactly as SafeAsync
            // already does on failure, so the existing null checks below cover
            // both cases with no extra branching.
            var canLeads    = UserCanRead(Modules.Leads);
            var canDeals    = UserCanRead(Modules.Deals);
            var canQuotes   = UserCanRead(Modules.Quotes);
            var canInvoices = UserCanRead(Modules.Invoices);

            if (!(canLeads && canDeals && canQuotes && canInvoices))
                _logger.LogInformation(
                    "Dashboard panels restricted — leads:{L} deals:{D} quotes:{Q} invoices:{I}",
                    canLeads, canDeals, canQuotes, canInvoices);

            // ── 043: the period and the tenant's stages ───────────────
            // Both before the panels, because the deal KPIs below cannot be
            // computed without them. Neither is fatal: a failure here leaves
            // the dashboard on "All time" with the stage list empty, and the
            // page still renders.
            var selected = await ResolvePeriodAsync(tenantId, period);
            var stages   = await SafeAsync(() => _stageService.GetAsync(activeOnly: false), "PipelineStages");

            _stageByKey = (stages ?? new List<PipelineStageDto>())
                .GroupBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            // Without this flag, a pipeline that failed to load would make
            // EVERY deal look like a ghost and every won deal look open — the
            // exact bug this round removes, wearing a different hat. The view
            // says "couldn't load your pipeline" instead of inventing numbers.
            StagesLoaded = _stageByKey.Count > 0;

            // ── 043a: the period now reaches the API ──────────────────
            // Quotes and invoices are filtered on their ISSUE DATE, server
            // side, using the DATE-ONLY range. Leads are not, yet — see
            // LeadsPeriodFiltered below — so the lead card keeps its "All
            // time" chip and is honest about it.
            var from = _periodDates?.FromUtc;
            var toEx = _periodDates?.ToUtcExclusive;

            var leadStatsTask    = SafeAsyncIf(canLeads,    () => _leadService.GetStatsAsync(tenantId),         "LeadStats");
            var dealsTask        = SafeAsyncIf(canDeals,    () => _dealService.GetAllAsync(tenantId, pageSize: 500), "Deals");
            var quoteStatsTask   = SafeAsyncIf(canQuotes,   () => _quoteService.GetStatisticsAsync(tenantId, from, toEx),   "QuoteStats");
            var invoiceStatsTask = SafeAsyncIf(canInvoices, () => _invoiceService.GetStatisticsAsync(tenantId, from, toEx), "InvoiceStats");

            // The quote LIST filter is INCLUSIVE at both ends — it was written
            // long before any of this, and `toDate` there means "on or before".
            // One tick back off the half-open bound converts between the two
            // without moving a quote into the wrong year. Everything else in
            // this round is half-open; this one line is the adapter.
            var recentQuotesTask = SafeAsyncIf(canQuotes,
                () => _quoteService.GetAllAsync(
                    tenantId,
                    fromDate: from,
                    toDate: toEx?.AddTicks(-1)),
                "RecentQuotes");

            await Task.WhenAll(
                leadStatsTask, dealsTask, quoteStatsTask,
                invoiceStatsTask, recentQuotesTask);

            // ── Lead KPIs ─────────────────────────────────────────────
            var leadStats = await leadStatsTask;
            if (leadStats != null)
            {
                TotalLeads      = leadStats.TotalLeads;
                NewLeads        = leadStats.NewLeads;
                WorkingLeads    = leadStats.WorkingLeads;
                QualifiedLeads  = leadStats.QualifiedLeads;
                ConvertedLeads  = leadStats.ConvertedLeads;
                LeadQuotaUsed   = leadStats.QuotaUsed;

                // ✅ FIX 4: activity counts for activities panel
                OverdueReminders = leadStats.OverdueReminders;
                TodayActivities  = leadStats.TodayActivities;
            }

            // ── Deal KPIs ─────────────────────────────────────────────
            var dealsResponse = await dealsTask;
            var deals = dealsResponse?.Items ?? new();
            DealQuotaUsed = dealsResponse?.QuotaUsed ?? 0;

            // ── 043: counted by CATEGORY, and within the chosen period ──
            //
            // Every line below used to test the stage NAME against four
            // literals. See change 1 in the header for what that did to a
            // workspace with its own pipeline.
            //
            // A deal counts in a period by the date that period is about:
            // an open deal by when it is EXPECTED to close (that is what a
            // forecast is), a closed one by when it ACTUALLY closed, falling
            // back to the expected date when the actual is missing.
            GhostStageDeals = StagesLoaded ? deals.Count(d => !IsKnownStage(d.Stage)) : 0;

            var inPeriod = deals.Where(InSelectedPeriod).ToList();

            UndatedDeals = deals.Count(d => !HasCloseDate(d));

            var openDeals = inPeriod.Where(d => CategoryOf(d.Stage) == StageCategory.Open).ToList();
            var wonDeals  = inPeriod.Where(d => CategoryOf(d.Stage) == StageCategory.Won).ToList();

            ActiveDeals   = openDeals.Count;
            DealsWon      = wonDeals.Count;
            DealsLost     = inPeriod.Count(d => CategoryOf(d.Stage) == StageCategory.Lost);
            PipelineValue = openDeals.Sum(d => d.ExpectedValue);

            // ✅ FIX 1: WonValue = ExpectedValue of won deals (not actual cash)
            WonValue = wonDeals.Sum(d => d.ExpectedValue);

            TopDeals = openDeals
                .OrderByDescending(d => d.ExpectedValue)
                .Take(5)
                .ToList();

            // ── Quote KPIs ────────────────────────────────────────────
            var quoteStats = await quoteStatsTask;
            if (quoteStats != null)
            {
                TotalQuotes        = quoteStats.TotalQuotes;
                QuotesSent         = quoteStats.SentQuotes;
                QuotesAccepted     = quoteStats.AcceptedQuotes;
                QuotesTotalValue   = quoteStats.TotalValue;
                QuotesPendingValue = quoteStats.PendingValue;
            }

            // ── Invoice KPIs ──────────────────────────────────────────
            var invoiceStats = await invoiceStatsTask;
            if (invoiceStats != null)
            {
                TotalInvoices   = invoiceStats.TotalInvoices;
                PaidInvoices    = invoiceStats.PaidInvoices;
                OverdueInvoices = invoiceStats.OverdueInvoices;
                TotalInvoiced   = invoiceStats.TotalAmount;
                PaidAmount      = invoiceStats.PaidAmount;   // ✅ actual collected
                UnpaidAmount    = invoiceStats.UnpaidAmount;
            }

            // ── Recent Quotes ─────────────────────────────────────────
            var allQuotes = await recentQuotesTask;
            RecentQuotes = (allQuotes ?? new())
                .OrderByDescending(q => q.IssueDateUtc)
                .Take(5)
                .ToList();

            return Page();
        }

        // ── HELPERS ───────────────────────────────────────────────────

        /// <summary>
        /// SafeAsync, but skipped entirely when the caller lacks permission.
        /// T is inferred from <paramref name="fn"/>, so no DTO type needs naming
        /// at the call site. Returns null when not allowed — indistinguishable
        /// from the null SafeAsync returns on failure, which the KPI blocks below
        /// already handle.
        /// </summary>
        private Task<T?> SafeAsyncIf<T>(bool allowed, Func<Task<T>> fn, string name) where T : class
            => allowed ? SafeAsync(fn, name) : Task.FromResult<T?>(null);

        private async Task<T?> SafeAsync<T>(Func<Task<T>> fn, string name) where T : class
        {
            try   { return await fn(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Dashboard: failed to load {Name}", name);
                return null;
            }
        }

        public string FormatCurrency(decimal amount) => _tenantService.FormatCurrency(amount);

        // ── 043: the tenant's own pipeline ────────────────────────────

        private Dictionary<string, PipelineStageDto> _stageByKey = new(StringComparer.OrdinalIgnoreCase);

        private bool IsKnownStage(string? key)
            => !string.IsNullOrEmpty(key) && _stageByKey.ContainsKey(key);

        /// <summary>
        /// Open / Won / Lost for a stage key.
        ///
        /// An unknown key answers Open, and that is a considered choice: a
        /// deal in a stage that no longer exists is certainly not won and
        /// certainly not lost, and treating it as open keeps it in front of
        /// somebody. GhostStageDeals counts them so it is not only in front
        /// of them but explained.
        /// </summary>
        public StageCategory CategoryOf(string? key)
            => key is not null && _stageByKey.TryGetValue(key, out var s)
                ? s.Category
                : StageCategory.Open;

        /// <summary>
        /// The stage's display name. The list DTO carries the KEY, which may
        /// be "CLOSED_WON" or "stage-3"; a dashboard should show what the
        /// workspace calls it.
        /// </summary>
        public string StageName(string? key)
            => key is not null && _stageByKey.TryGetValue(key, out var s) ? s.Name : (key ?? "—");

        /// <summary>
        /// 043. Category first, then position in the pipeline — never a stage
        /// name. Open stages shade from grey through blue to amber as they
        /// approach the end, so the board still reads at a glance for a
        /// workspace whose stages we have never seen.
        /// </summary>
        public string GetStageClass(string stage)
        {
            if (!_stageByKey.TryGetValue(stage ?? "", out var s))
                return "bg-secondary";          // ghost stage — deliberately dull

            if (s.Category == StageCategory.Won)  return "bg-success";
            if (s.Category == StageCategory.Lost) return "bg-danger";

            var open = _stageByKey.Values
                .Where(x => x.Category == StageCategory.Open)
                .OrderBy(x => x.SortOrder)
                .ToList();

            if (open.Count == 0) return "bg-secondary";

            var index = open.FindIndex(x => string.Equals(x.Key, s.Key, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return "bg-secondary";

            // Thirds of the open pipeline: early, middle, nearly there.
            var third = (index + 1) * 3.0 / open.Count;

            return third <= 1 ? "bg-secondary"
                 : third <= 2 ? "bg-primary"
                 : "bg-warning text-dark";
        }

        // ── 043: the period ───────────────────────────────────────────

        /// <summary>The range the deal panels are filtered by. Null = all time.</summary>
        private UtcRange? _periodDates;

        private static bool HasCloseDate(DealListItem d)
            => d.ExpectedCloseDateUtc != default
            || (d.ActualCloseDateUtc.HasValue && d.ActualCloseDateUtc.Value != default);

        /// <summary>
        /// Is this deal part of the selected period?
        ///
        /// Compared against DateOnlyRange, not the timestamp range: since 034
        /// these close dates hold a picked calendar date at midnight with no
        /// offset, and shifting them by the tenant's timezone would move every
        /// deal that closes on the first or last day of the year into the
        /// wrong one.
        ///
        /// ActualCloseDateUtc is the one column 034 deliberately did NOT
        /// repair — it holds a mixture of picked dates and DateTime.UtcNow
        /// stamps — so a deal closed late on the final day of a financial year
        /// can still land in the next one. That is a known, bounded
        /// inaccuracy of at most one day on a column we chose not to rewrite;
        /// it is not worth losing real close dates over.
        /// </summary>
        private bool InSelectedPeriod(DealListItem d)
        {
            if (_periodDates is not { } range) return true;      // all time
            if (!HasCloseDate(d)) return false;                  // counted separately

            var closed = CategoryOf(d.Stage) != StageCategory.Open;

            var date = closed && d.ActualCloseDateUtc.HasValue && d.ActualCloseDateUtc.Value != default
                ? d.ActualCloseDateUtc.Value
                : d.ExpectedCloseDateUtc;

            return date >= range.FromUtc && date < range.ToUtcExclusive;
        }

        /// <summary>
        /// Builds the dropdown and works out which period was asked for.
        /// Never throws: a workspace with no fiscal settings, or an API that
        /// is having a bad morning, falls back to All time with the selector
        /// still usable.
        /// </summary>
        private async Task<FiscalPeriodOption?> ResolvePeriodAsync(Guid tenantId, string? requested)
        {
            var options = new List<FiscalPeriodOption>();

            try
            {
                options.AddRange(await _fiscal.PeriodOptionsAsync(tenantId, pastYears: 3));

                var ctx = await _fiscal.GetContextAsync(tenantId);
                FiscalYearDescription = DescribeFiscalYear(ctx.StartMonth);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Dashboard: could not build the period list");
            }

            // An unknown or missing key means the CURRENT financial year —
            // the answer somebody opening a dashboard wants, rather than a
            // lifetime total that only grows.
            var chosen = options.FirstOrDefault(
                             o => string.Equals(o.Key, requested, StringComparison.OrdinalIgnoreCase))
                      ?? (string.Equals(requested, AllTimeKey, StringComparison.OrdinalIgnoreCase)
                            ? null
                            : options.FirstOrDefault(o => o.IsCurrent && o.Group != "Quarters"));

            PeriodOptions = new List<SelectListItem>
            {
                new()
                {
                    Value = AllTimeKey,
                    Text = "All time",
                    Selected = chosen is null
                }
            };

            foreach (var group in options.GroupBy(o => o.Group))
            {
                var g = new SelectListGroup { Name = group.Key };

                foreach (var o in group)
                    PeriodOptions.Add(new SelectListItem
                    {
                        Value = o.Key,
                        Text = o.IsCurrent ? $"{o.Label} (current)" : o.Label,
                        Group = g,
                        Selected = chosen is not null && o.Key == chosen.Key
                    });
            }

            SelectedPeriodKey   = chosen?.Key ?? AllTimeKey;
            SelectedPeriodLabel = chosen?.Label ?? "All time";
            _periodDates        = chosen?.Dates;

            return chosen;
        }

        /// <summary>"April to March", "January to December".</summary>
        private static string DescribeFiscalYear(int startMonth)
        {
            var start = new DateTime(2000, Math.Clamp(startMonth, 1, 12), 1);
            var end = start.AddMonths(11);

            return $"{start:MMMM} to {end:MMMM}";
        }

        public string GetQuoteStatusClass(string status) => status switch
        {
            "Draft"    => "bg-secondary",
            "Sent"     => "bg-primary",
            "Viewed"   => "bg-info",
            "Accepted" => "bg-success",
            "Rejected" => "bg-danger",
            "Expired"  => "bg-warning text-dark",
            _          => "bg-secondary"
        };

        public string GetRelativeTime(DateTime utcTime)
        {
            var diff = DateTime.UtcNow - utcTime;
            if (diff.TotalMinutes < 1)  return "Just now";
            if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
            if (diff.TotalHours   < 24) return $"{(int)diff.TotalHours}h ago";
            if (diff.TotalDays    < 7)  return $"{(int)diff.TotalDays}d ago";
            return _tenantService.FormatDate(utcTime);
        }

        public string WinRate
        {
            get
            {
                var closed = DealsWon + DealsLost;
                if (closed == 0) return "0%";
                return $"{(int)Math.Round(DealsWon * 100.0 / closed)}%";
            }
        }

        // ✅ FIX 2: percentage of each lead status for multi-segment bar
        public int NewPct       => TotalLeads == 0 ? 0 : (int)Math.Round(NewLeads       * 100.0 / TotalLeads);
        public int WorkingPct   => TotalLeads == 0 ? 0 : (int)Math.Round(WorkingLeads   * 100.0 / TotalLeads);
        public int QualifiedPct => TotalLeads == 0 ? 0 : (int)Math.Round(QualifiedLeads * 100.0 / TotalLeads);
        public int ConvertedPct => TotalLeads == 0 ? 0 : (int)Math.Round(ConvertedLeads * 100.0 / TotalLeads);
    }
}
