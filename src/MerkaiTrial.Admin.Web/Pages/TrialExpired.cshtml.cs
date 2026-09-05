// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/TrialExpired.cshtml.cs
//
// Where AccountStatePageFilter sends a write attempt from a tenant whose
// trial has ended. Reads still work, so this is not a wall — it is the
// explanation for why the save did not go through.
// =====================================================================

using MerkaiTrial.Application.Services.Tenants;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MerkaiTrial.Admin.Web.Pages;

public class TrialExpiredModel : PageModel
{
    private readonly ICurrentTenantService _tenant;

    public TrialExpiredModel(ICurrentTenantService tenant) => _tenant = tenant;

    public string TenantName { get; private set; } = string.Empty;
    public DateTime? ExpiredOn { get; private set; }

    public void OnGet()
    {
        TenantName = _tenant.GetTenantName();
        ExpiredOn = _tenant.GetTrialExpiresAt();
    }
}
