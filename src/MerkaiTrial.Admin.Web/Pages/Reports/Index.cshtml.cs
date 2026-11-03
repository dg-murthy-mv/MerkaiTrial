// =====================================================================
// Reports Landing Page
// Location: MerkaiTrial.Admin.Web/Pages/Reports/Index.cshtml.cs
//
// 111 — the reports in five categories (ReportCatalog). HasAdvanced says
// whether the plan includes the advanced reports, so they can be marked.
// 113 — needs reports.read (ReportGate), and shows only the categories
// this person may open (Revenue & Finance needs Finance Reports too).
// =====================================================================

using MerkaiTrial.Application.Services.Tenants;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MerkaiTrial.Admin.Web.Pages.Reports
{
    public class IndexModel : PageModel
    {
        private readonly ICurrentTenantService _tenantService;

        public IndexModel(ICurrentTenantService tenantService)
            => _tenantService = tenantService;

        public string TenantName     { get; private set; } = string.Empty;
        public string CurrencyCode   { get; private set; } = string.Empty;
        public string CurrencySymbol { get; private set; } = string.Empty;
        public bool   HasAdvanced    { get; private set; }

        public IReadOnlyList<ReportCategory> Categories => ReportCatalog.VisibleTo(User);

        public async Task<IActionResult> OnGetAsync()
        {
            var refused = await ReportGate.CheckAsync(HttpContext, "/Reports/Index", _tenantService);
            if (refused is not null) return refused;

            TenantName     = _tenantService.GetTenantName();
            CurrencyCode   = _tenantService.GetCurrencyCode();
            CurrencySymbol = _tenantService.GetCurrencySymbol();
            HasAdvanced    = _tenantService.HasFeature("advanced_reports");
            ViewData["ActiveReport"] = "index";
            return Page();
        }
    }
}
