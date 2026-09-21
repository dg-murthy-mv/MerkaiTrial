// =====================================================================
// QUOTE APPROVAL RULES — Settings
// Location: MerkaiTrial.Admin.Web/Pages/Settings/QuoteApprovals/Index.cshtml.cs
// URL:      /Settings/QuoteApprovals
//
// NEW FILE (017). When does a quote need a manager's sign-off?
//   • any line more than X% below list price (default 10%)
//   • and/or the quote total above an amount (default: no limit)
// Anyone with Quotes.Read can LOOK (reps like to know the limit); only a
// workspace admin can change it — the API enforces that too.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Quotes;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.Admin.Web.Pages.Settings.QuoteApprovals;

public class IndexModel : AuthorizedPageModel
{
    private readonly IQuoteApprovalService _approvals;
    private readonly ICurrentUserService _currentUser;
    private readonly ICurrentTenantService _tenant;

    protected override string ModuleName => Modules.Quotes;

    public IndexModel(
        IQuoteApprovalService approvals,
        ICurrentTenantService tenant,
        IAuthorizationService authorizationService,
        ICurrentUserService currentUserService,
        ILogger<IndexModel> logger)
        : base(authorizationService, currentUserService, logger)
    {
        _approvals = approvals;
        _currentUser = currentUserService;
        _tenant = tenant;
    }

    public QuoteApprovalSettingsDto? Settings { get; private set; }
    public bool CanEdit { get; private set; }
    public string TenantCurrencyCode { get; private set; } = string.Empty;
    public string TenantCurrencySymbol { get; private set; } = string.Empty;

    // ── Form ──────────────────────────────────────────────────────────
    [BindProperty] public bool IsEnabled { get; set; }
    [BindProperty] public bool UseDiscountRule { get; set; }
    [BindProperty] public decimal? MaxDiscountPercent { get; set; }
    [BindProperty] public bool UseTotalRule { get; set; }
    [BindProperty] public decimal? MaxQuoteTotal { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var check = await ValidatePermissionAsync(Actions.Read);
        if (check != null) return check;

        await InitializePermissionsAsync();
        await LoadAsync();

        if (Settings != null)
        {
            IsEnabled = Settings.IsEnabled;
            UseDiscountRule = Settings.MaxDiscountPercent.HasValue;
            MaxDiscountPercent = Settings.MaxDiscountPercent ?? 10m;
            UseTotalRule = Settings.MaxQuoteTotal.HasValue;
            MaxQuoteTotal = Settings.MaxQuoteTotal;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var check = await ValidatePermissionAsync(Actions.Read);
        if (check != null) return check;

        var me = await _currentUser.GetCurrentUserAsync();
        if (!me.IsTenantAdmin)
        {
            TempData["ErrorMessage"] = "Only workspace admins can change the approval rules.";
            return RedirectToPage();
        }

        try
        {
            await _approvals.SaveSettingsAsync(new SaveQuoteApprovalSettingsDto(
                IsEnabled,
                UseDiscountRule ? MaxDiscountPercent : null,
                UseTotalRule ? MaxQuoteTotal : null));

            TempData["SuccessMessage"] = !IsEnabled
                ? "Saved. Quote approvals are off — every quote can be sent straight away."
                : "Saved. New rules apply from the next quote anyone sends; quotes already waiting keep their request.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to save quote approval settings");
            TempData["ErrorMessage"] = "Couldn't save the approval rules. Please try again.";
        }

        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        TenantCurrencyCode = _tenant.GetCurrencyCode();
        TenantCurrencySymbol = _tenant.GetCurrencySymbol();

        try
        {
            CanEdit = (await _currentUser.GetCurrentUserAsync()).IsTenantAdmin;
            Settings = await _approvals.GetSettingsAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load quote approval settings");
            TempData["ErrorMessage"] = "Couldn't load the approval rules. Please try again.";
        }
    }
}
