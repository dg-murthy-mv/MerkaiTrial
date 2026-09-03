// =====================================================================
// Reports Landing Page
// Location: MerkaiTrial.Admin.Web/Pages/Reports/Index.cshtml.cs
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Meta;
using MerkaiTrial.Admin.Web.Services.Reports;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MerkaiTrial.Admin.Web.Pages.Reports.WinLoss
{
    public class IndexModel : ReportBaseModel
    {
        public IndexModel(
            IReportService r, IMetaService m,
            ICurrentUserService u, ICurrentTenantService t)
            : base(r, m, u, t) { }

        public WinLossReportDto? Data { get; private set; }
        public string DataJson { get; private set; } = "null";

        public async Task OnGetAsync()
        {
            await InitAsync("winloss");
            try
            {
                Data = await ReportService.GetWinLossAsync(FromDate, ToDate, Vertical);
                DataJson = ToJson(Data);
            }
            catch (Exception) { ErrorMessage = "Failed to load report data."; }
        }

        public async Task<IActionResult> OnGetExportAsync()
        {
            var bytes = await ReportService.ExportWinLossAsync(FromDate, ToDate, Vertical);
            return ExcelFile(bytes, "WinLossAnalysis");
        }
    }
}
