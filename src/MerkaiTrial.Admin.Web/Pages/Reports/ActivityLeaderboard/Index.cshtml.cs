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

namespace MerkaiTrial.Admin.Web.Pages.Reports.ActivityLeaderboard
{
    public class IndexModel : ReportBaseModel
    {
        public IndexModel(IReportService r, IMetaService m, ICurrentUserService u, ICurrentTenantService t) : base(r, m, u, t) { }
        public ActivityLeaderboardReportDto? Data { get; private set; }
        public string DataJson { get; private set; } = "null";
        public async Task OnGetAsync() { await InitAsync("activityleaderboard"); try { Data = await ReportService.GetActivityLeaderboardAsync(FromDate, ToDate, Vertical); DataJson = ToJson(Data); } catch { ErrorMessage = "Failed to load report data."; } }
        public async Task<IActionResult> OnGetExportAsync() => RedirectToPage();
    }
}
