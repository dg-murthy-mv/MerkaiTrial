// =====================================================================
// Reports Landing Page
// Location: MerkaiTrial.Admin.Web/Pages/Reports/Index.cshtml.cs
// =====================================================================

using MerkaiTrial.Application.Services.Tenants;
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

        public void OnGet()
        {
            TenantName     = _tenantService.GetTenantName();
            CurrencyCode   = _tenantService.GetCurrencyCode();
            CurrencySymbol = _tenantService.GetCurrencySymbol();
            ViewData["ActiveReport"] = "index";
        }
    }
}
