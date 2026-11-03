// =====================================================================
// FILE: Pages/Reports/Forecast/Index.cshtml.cs                (Round 112)
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Meta;
using MerkaiTrial.Admin.Web.Services.Reports;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;

namespace MerkaiTrial.Admin.Web.Pages.Reports.Forecast
{
    public class IndexModel : ReportBaseModel
    {
        public IndexModel(IReportService r, IMetaService m, ICurrentUserService u, ICurrentTenantService t)
            : base(r, m, u, t) { }

        public RevenueForecastReportDto? Data { get; private set; }

        public async Task OnGetAsync()
        {
            await InitAsync("forecast");
            try { Data = await ReportService.GetRevenueForecastAsync(Vertical, Months); }
            catch (Exception) { ErrorMessage = "Failed to load report data."; }
        }
    }
}
