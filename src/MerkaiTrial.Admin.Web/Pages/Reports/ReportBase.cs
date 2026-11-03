// =====================================================================
// ReportBase.cs
// Location: MerkaiTrial.Admin.Web/Pages/Reports/ReportBase.cs
//
// Shared base class for all report page models.
// Handles: filter binding, tenant context, verticals dropdown,
//          ViewData for sidebar highlighting.
//
// 113 — WHO MAY OPEN A REPORT. Before every handler (GET and any export
// POST) ReportGate checks, through the same permission handler the rest of
// the app uses:
//   • reports.read                      — every report
//   • the category's own permission     — Revenue & Finance needs
//                                          financereports.read as well
//   • the plan                          — an Advanced report needs the
//                                          advanced_reports feature
// Until 113 only the sidebar link was hidden; the pages opened for anyone
// signed in who typed the address. Refused → the Access Denied page (or,
// for the plan, back to the Reports page, which shows it locked).
// =====================================================================

using MerkaiTrial.Application.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Filters;

using MerkaiTrial.Admin.Web.Services.Meta;
using MerkaiTrial.Admin.Web.Services.Reports;
using MerkaiTrial.Application.Commands.Meta;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Text.Json;

namespace MerkaiTrial.Admin.Web.Pages.Reports
{
    // ── Filter view model (passed to _ReportFilter partial) ───────────

    public class ReportFilterViewModel
    {
        public DateTime?        FromDate         { get; set; }
        public DateTime?        ToDate           { get; set; }
        public string?          SelectedVertical { get; set; }
        public List<VerticalDto> Verticals       { get; set; } = new();
        public int?             Days             { get; set; }   // 112 — Stalled Deals
        public int?             Months           { get; set; }   // 112 — Revenue Forecast
    }

    // ── Base page model ───────────────────────────────────────────────

    public abstract class ReportBaseModel : PageModel
    {
        protected readonly IReportService        ReportService;
        protected readonly IMetaService          MetaService;
        protected readonly ICurrentUserService   CurrentUserService;
        protected readonly ICurrentTenantService TenantService;

        protected static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        protected ReportBaseModel(
            IReportService        reportService,
            IMetaService          metaService,
            ICurrentUserService   currentUserService,
            ICurrentTenantService tenantService)
        {
            ReportService      = reportService;
            MetaService        = metaService;
            CurrentUserService = currentUserService;
            TenantService      = tenantService;
        }

        // ── Filters (bound from query string) ─────────────────────────
        [BindProperty(SupportsGet = true)] public DateTime? FromDate { get; set; }
        [BindProperty(SupportsGet = true)] public DateTime? ToDate   { get; set; }
        [BindProperty(SupportsGet = true)] public string?   Vertical { get; set; }
        [BindProperty(SupportsGet = true)] public int?      Days     { get; set; }   // 112 — Stalled Deals
        [BindProperty(SupportsGet = true)] public int?      Months   { get; set; }   // 112 — Revenue Forecast

        // ── Tenant context ────────────────────────────────────────────
        public string CurrencySymbol { get; protected set; } = string.Empty;
        public string CurrencyCode   { get; protected set; } = string.Empty;
        public string TenantName     { get; protected set; } = string.Empty;

        // ── Filter view model (for partial) ───────────────────────────
        public ReportFilterViewModel Filter { get; protected set; } = new();

        [TempData] public string? ErrorMessage { get; set; }

        // ── 113: the gate, before every handler ───────────────────────
        public override async Task OnPageHandlerExecutionAsync(
            PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
        {
            var refused = await ReportGate.CheckAsync(HttpContext, context.ActionDescriptor.ViewEnginePath, TenantService);
            if (refused is not null)
            {
                context.Result = refused;
                return;
            }
            await next();
        }

        // ── Called by every derived OnGetAsync ────────────────────────
        protected async Task InitAsync(string reportKey)
        {
            CurrencySymbol = TenantService.GetCurrencySymbol();
            CurrencyCode   = TenantService.GetCurrencyCode();
            TenantName     = TenantService.GetTenantName();

            // Default: last 12 months
            FromDate ??= DateTime.Today.AddMonths(-12);
            ToDate   ??= DateTime.Today;

            // ViewData for sidebar sub-link highlight
            ViewData["ActiveReport"] = reportKey;
            ViewData["FromDate"]     = FromDate?.ToString("yyyy-MM-dd");
            ViewData["ToDate"]       = ToDate?.ToString("yyyy-MM-dd");
            ViewData["Vertical"]     = Vertical;

            // Load verticals for filter dropdown
            try
            {
                var verticals = await MetaService.GetVerticalsAsync();
                Filter = new ReportFilterViewModel
                {
                    FromDate         = FromDate,
                    ToDate           = ToDate,
                    SelectedVertical = Vertical,
                    Verticals        = verticals,
                    Days             = Days,
                    Months           = Months
                };
            }
            catch
            {
                Filter = new ReportFilterViewModel
                {
                    FromDate = FromDate, ToDate = ToDate,
                    SelectedVertical = Vertical,
                    Days = Days, Months = Months
                };
            }
        }

        // ── Helpers ───────────────────────────────────────────────────
        public string FormatCurrency(decimal amount) =>
            TenantService.FormatCurrency(amount);

        public string ToJson(object obj) =>
            JsonSerializer.Serialize(obj, JsonOpts);

        protected FileContentResult ExcelFile(byte[] bytes, string name) =>
            File(bytes,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"{name}_{DateTime.Today:yyyyMMdd}.xlsx");
    }

    // ── 113: who may open a report ────────────────────────────────────

    /// <summary>
    /// The one check for every report page and the Reports landing page.
    /// Null = go ahead; otherwise the result to send instead.
    /// </summary>
    public static class ReportGate
    {
        public static async Task<IActionResult?> CheckAsync(
            HttpContext http, string? pagePath, ICurrentTenantService tenant)
        {
            var auth = http.RequestServices.GetRequiredService<IAuthorizationService>();
            var user = http.User;

            if (!(await auth.AuthorizeAsync(user, Policies.ReportsRead)).Succeeded)
                return new RedirectToPageResult("/AccessDenied");

            var found = ReportCatalog.Find(pagePath);
            if (found is null) return null;   // the landing page: reports.read is enough

            var (category, link) = found.Value;
            if (category.Permission is not null &&
                !(await auth.AuthorizeAsync(user, $"{category.Permission}.read")).Succeeded)
                return new RedirectToPageResult("/AccessDenied");

            if (link.Advanced && !tenant.HasFeature("advanced_reports"))
                return new RedirectToPageResult("/Reports/Index");

            return null;
        }
    }
}
