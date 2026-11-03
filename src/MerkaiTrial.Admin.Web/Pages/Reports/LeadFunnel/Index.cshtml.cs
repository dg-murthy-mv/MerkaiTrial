// =====================================================================
// FILE: Pages/Reports/LeadFunnel/Index.cshtml.cs                (Round 112)
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Meta;
using MerkaiTrial.Admin.Web.Services.Reports;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;

namespace MerkaiTrial.Admin.Web.Pages.Reports.LeadFunnel
{
    public class IndexModel : ReportBaseModel
    {
        public IndexModel(IReportService r, IMetaService m, ICurrentUserService u, ICurrentTenantService t)
            : base(r, m, u, t) { }

        public LeadFunnelReportDto? Data { get; private set; }

        public async Task OnGetAsync()
        {
            await InitAsync("leadfunnel");
            try { Data = await ReportService.GetLeadFunnelAsync(FromDate, ToDate, Vertical); }
            catch (Exception) { ErrorMessage = "Failed to load report data."; }
        }
    }
}
