// =====================================================================
// ReportBase.cs
// Location: MerkaiTrial.Admin.Web/Pages/Reports/ReportBase.cs
//
// Shared base class for all report page models.
// Handles: filter binding, tenant context, verticals dropdown,
//          ViewData for sidebar highlighting.
// =====================================================================

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

        // ── Tenant context ────────────────────────────────────────────
        public string CurrencySymbol { get; protected set; } = string.Empty;
        public string CurrencyCode   { get; protected set; } = string.Empty;
        public string TenantName     { get; protected set; } = string.Empty;

        // ── Filter view model (for partial) ───────────────────────────
        public ReportFilterViewModel Filter { get; protected set; } = new();

        [TempData] public string? ErrorMessage { get; set; }

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
                    Verticals        = verticals
                };
            }
            catch
            {
                Filter = new ReportFilterViewModel
                {
                    FromDate = FromDate, ToDate = ToDate,
                    SelectedVertical = Vertical
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
}
