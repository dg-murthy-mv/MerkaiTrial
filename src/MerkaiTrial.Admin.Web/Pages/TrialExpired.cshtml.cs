// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/TrialExpired.cshtml.cs
//
// Where AccountStatePageFilter sends a write attempt from a workspace that
// is read-only. Reads still work, so this is not a wall: it is the
// explanation for why the save did not go through.
//
// 103: a workspace can be read-only for two reasons now, and the page says
// which — the trial ran out, or the workspace was paused (suspended or
// switched off by MadeeVision).
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

    /// <summary>True when the trial ran out; false when the workspace was paused for another reason.</summary>
    public bool TrialEnded { get; private set; } = true;

    public void OnGet()
    {
        TenantName = _tenant.GetTenantName();
        TrialEnded = _tenant.IsTrialExpired() || _tenant.IsAccountActive();
        ExpiredOn = TrialEnded ? _tenant.GetTrialExpiresAt() : null;
    }
}
