using System.ComponentModel.DataAnnotations;
using MerkaiTrial.Admin.Web.Services.Tenants;
using MerkaiTrial.Application.Commands.Plans;
using MerkaiTrial.Application.Commands.Tenants;
using MerkaiTrial.Application.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace MerkaiTrial.Admin.Web.Pages.Admin.Tenants;

public class CreateModel : PageModel
{
    private readonly ITenantProvisioningService _provisioning;
    private readonly ITenantService _tenantService;
    private readonly GetPublicPlansHandler _getPlans;
    private readonly ILogger<CreateModel> _logger;

    public CreateModel(
        ITenantProvisioningService provisioning,
        ITenantService tenantService,
        GetPublicPlansHandler getPlans,
        ILogger<CreateModel> logger)
    {
        _provisioning = provisioning;
        _tenantService = tenantService;
        _getPlans = getPlans;
        _logger = logger;
    }

    [BindProperty] public InputModel Input { get; set; } = new();

    public List<SelectListItem> CountryOptions { get; set; } = new();
    public List<SelectListItem> TimezoneOptions { get; set; } = new();
    public List<SelectListItem> CurrencyOptions { get; set; } = new();
    public List<SelectListItem> PlanOptions { get; set; } = new();

    /// <summary>Live plan data — the view renders the limits panel from
    /// this rather than the hardcoded card the old page had, which claimed
    /// Starter was 100 leads while the database said 500.</summary>
    public List<PlanCardDto> PlanDetails { get; set; } = new();

    public List<string> DuplicateWarnings { get; set; } = new();
    public string? ErrorMessage { get; set; }

    public class InputModel
    {
        // ── Workspace ────────────────────────────────────────────────
        [Required(ErrorMessage = "Workspace name is required")]
        [StringLength(200, MinimumLength = 2)]
        public string Name { get; set; } = string.Empty;

        [Required(ErrorMessage = "Company email is required")]
        [EmailAddress]
        public string FromEmail { get; set; } = string.Empty;

        [Phone] public string? Phone { get; set; }

        [Required(ErrorMessage = "Country is required")]
        public Guid? CountryId { get; set; }

        [Required] public string DefaultCurrency { get; set; } = "THB";
        [Required] public string TimeZone { get; set; } = "Asia/Bangkok";
        [Required] public string Plan { get; set; } = "trial";

        public string PreferredLanguage { get; set; } = "en";
        public string? Domain { get; set; }

        [EmailAddress] public string? ReplyToEmail { get; set; }

        // ── The person who receives the invite ───────────────────────
        [Required(ErrorMessage = "Administrator first name is required")]
        [StringLength(100)]
        public string AdminFirstName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Administrator last name is required")]
        [StringLength(100)]
        public string AdminLastName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Administrator email is required")]
        [EmailAddress]
        public string AdminEmail { get; set; } = string.Empty;

        /// <summary>Ticked by the user after reading the duplicate
        /// warnings. Not a hidden field — it must be a deliberate act.</summary>
        public bool ConfirmDespiteWarnings { get; set; }
    }

    // =================================================================
    public async Task<IActionResult> OnGetAsync()
    {
        try
        {
            await LoadDropdownsAsync();
            return Page();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading the provisioning form");
            TempData["Error"] = "Failed to load the form.";
            return RedirectToPage("./Index");
        }
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadDropdownsAsync();

        if (!ModelState.IsValid) return Page();

        // ── Duplicate check ──────────────────────────────────────────
        // Warnings, not a block. Sometimes a second trial is the right
        // commercial answer; the point is that someone decided.
        DuplicateWarnings = (await _provisioning.CheckForDuplicateTrialsAsync(
            Input.Name, Input.FromEmail)).ToList();

        if (DuplicateWarnings.Count > 0 && !Input.ConfirmDespiteWarnings)
            return Page();

        var provisionedBy = User.Identity?.Name
                         ?? User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
                         ?? "SuperAdmin";

        try
        {
            var request = new ProvisionTenantRequest(
                Name: Input.Name.Trim(),
                FromEmail: Input.FromEmail.Trim(),
                Phone: Input.Phone?.Trim(),
                CountryId: Input.CountryId,
                DefaultCurrency: Input.DefaultCurrency.Trim().ToUpperInvariant(),
                TimeZone: Input.TimeZone,
                PreferredLanguage: Input.PreferredLanguage,
                Plan: Input.Plan,
                Domain: Input.Domain?.Trim(),
                ReplyToEmail: Input.ReplyToEmail?.Trim(),
                AdminEmail: Input.AdminEmail.Trim(),
                AdminFirstName: Input.AdminFirstName.Trim(),
                AdminLastName: Input.AdminLastName.Trim());

            var result = await _provisioning.ProvisionAsync(request, provisionedBy);

            // ── Hand the result to the Result page via TempData ──────
            // NOT a query string. The raw invite token would land in
            // Serilog's request log and the browser history, and it is a
            // credential — anyone holding it can set the admin password.
            TempData["Provision.TenantId"] = result.TenantId.ToString();
            TempData["Provision.TenantName"] = result.TenantName;
            TempData["Provision.AdminEmail"] = result.AdminEmail;
            TempData["Provision.Token"] = result.InviteToken;
            TempData["Provision.InviteUntil"] = result.InviteExpiresAtUtc.ToString("u");
            TempData["Provision.TrialUntil"] = result.TrialExpiresAtUtc?.ToString("u") ?? "";

            return RedirectToPage("./Result");
        }
        catch (InvalidOperationException ex)
        {
            // Duplicate workspace email, or an admin email already in use.
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
        catch (KeyNotFoundException ex)
        {
            // Plan name not in dbo.Plans.
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Provisioning failed for '{Name}'", Input.Name);
            ErrorMessage = "Provisioning failed and nothing was created. Check the logs and try again.";
            return Page();
        }
    }

    // =================================================================
    private async Task LoadDropdownsAsync()
    {
        var countries = await _tenantService.GetCountriesAsync();

        CountryOptions = countries.Select(c => new SelectListItem
        {
            Value = c.Id.ToString(),
            Text = $"{c.Name} ({c.Code})",
            Selected = Input.CountryId == c.Id
        }).ToList();
        CountryOptions.Insert(0, new SelectListItem("-- Select country --", "", Input.CountryId == null));

        var timezones = await _tenantService.GetTimezonesAsync(commonOnly: true);
        TimezoneOptions = timezones.Select(tz => new SelectListItem
        {
            Value = tz.Value,
            Text = tz.DisplayName,
            Selected = tz.Value == Input.TimeZone
        }).ToList();

        CurrencyOptions = countries
            .Select(c => (c.CurrencyCode ?? "").Trim().ToUpperInvariant())
            .Where(code => code.Length == 3)
            .Distinct()
            .OrderBy(code => code)
            .Select(code => new SelectListItem
            {
                Value = code,
                Text = code,
                Selected = string.Equals(code, Input.DefaultCurrency, StringComparison.OrdinalIgnoreCase)
            }).ToList();

        // Plans from the database. GetPublicPlansHandler excludes
        // IsPublic = false, which is exactly the trial plan — so it is
        // added explicitly below. Provisioning a trial IS the point of
        // this page.
        PlanDetails = await _getPlans.Handle();

        PlanOptions = PlanDetails.Select(p => new SelectListItem
        {
            Value = p.Name,
            Text = $"{p.DisplayName} — {p.MaxUsers} users, {p.MaxLeads:N0} leads, {p.MaxDeals:N0} deals",
            Selected = string.Equals(p.Name, Input.Plan, StringComparison.OrdinalIgnoreCase)
        }).ToList();

        if (!PlanOptions.Any(o => o.Value == "trial"))
        {
            PlanOptions.Insert(0, new SelectListItem
            {
                Value = "trial",
                Text = "Free Trial — 30 days",
                Selected = string.Equals(Input.Plan, "trial", StringComparison.OrdinalIgnoreCase)
            });
        }
    }
}