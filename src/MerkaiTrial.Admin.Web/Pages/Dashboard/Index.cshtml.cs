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
using MerkaiTrial.Admin.Web.Services.Quotes;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
        private readonly ILogger<IndexModel>   _logger;

        public IndexModel(
            ILeadService          leadService,
            IDealService          dealService,
            IQuoteService         quoteService,
            IInvoiceService       invoiceService,
            ICurrentUserService   currentUserService,
            ICurrentTenantService tenantService,
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

        // ── Deal KPIs ─────────────────────────────────────────────────
        public int     ActiveDeals   { get; set; }
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
        public async Task<IActionResult> OnGet()
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

            var leadStatsTask    = SafeAsyncIf(canLeads,    () => _leadService.GetStatsAsync(tenantId),         "LeadStats");
            var dealsTask        = SafeAsyncIf(canDeals,    () => _dealService.GetAllAsync(tenantId),           "Deals");
            var quoteStatsTask   = SafeAsyncIf(canQuotes,   () => _quoteService.GetStatisticsAsync(tenantId),   "QuoteStats");
            var invoiceStatsTask = SafeAsyncIf(canInvoices, () => _invoiceService.GetStatisticsAsync(tenantId), "InvoiceStats");
            var recentQuotesTask = SafeAsyncIf(canQuotes,   () => _quoteService.GetAllAsync(tenantId),          "RecentQuotes");

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

                // ✅ FIX 4: activity counts for activities panel
                OverdueReminders = leadStats.OverdueReminders;
                TodayActivities  = leadStats.TodayActivities;
            }

            // ── Deal KPIs ─────────────────────────────────────────────
            var deals = (await dealsTask)?.Items ?? new();

            ActiveDeals   = deals.Count(d =>
                d.Stage is not ("ClosedWon" or "ClosedLost" or "Won" or "Lost"));
            DealsWon      = deals.Count(d => d.Stage is "ClosedWon" or "Won");
            DealsLost     = deals.Count(d => d.Stage is "ClosedLost" or "Lost");
            PipelineValue = deals
                .Where(d => d.Stage is not ("ClosedWon" or "ClosedLost" or "Won" or "Lost"))
                .Sum(d => d.ExpectedValue);

            // ✅ FIX 1: WonValue = ExpectedValue of won deals (not actual cash)
            WonValue = deals
                .Where(d => d.Stage is "ClosedWon" or "Won")
                .Sum(d => d.ExpectedValue);

            TopDeals = deals
                .Where(d => d.Stage is not ("ClosedWon" or "ClosedLost" or "Won" or "Lost"))
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

        public string GetStageClass(string stage) => stage switch
        {
            "New"          => "bg-secondary",
            "Qualified"    => "bg-info",
            "Proposal"     => "bg-primary",
            "Negotiation"  => "bg-warning text-dark",
            "ClosedWon"    => "bg-success",
            "Won"          => "bg-success",
            "ClosedLost"   => "bg-danger",
            "Lost"         => "bg-danger",
            _              => "bg-secondary"
        };

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
