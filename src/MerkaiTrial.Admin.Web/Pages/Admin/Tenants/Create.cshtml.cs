// =====================================================================
// TENANT CREATE PAGE - Backend (REFACTORED)
// Location: MerkaiTrial.Admin.Web/Pages/Admin/Tenants/Create.cshtml.cs
//
// CHANGE 1: Injected GetPublicPlansHandler — plan dropdown from DB
// CHANGE 2: Removed GetPlanDisplayText() hardcoded switch
// CHANGE 3: Removed _tenantService.GetPlansAsync() call (returned hardcoded strings)
// CHANGE 4: PlanOptions text now built from live DB data (name, price, limits)
// CHANGE 5: Added PlanDetails property — embedded as JSON for JS limits preview
// Everything else (input model, validations, dropdowns) is identical to original.
// =====================================================================

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using MerkaiTrial.Admin.Web.Services.Tenants;
using MerkaiTrial.Application.Commands.Plans;   // ← NEW
using MerkaiTrial.Application.DTOs;
using System.ComponentModel.DataAnnotations;

namespace MerkaiTrial.Admin.Web.Pages.Admin.Tenants
{
    public class CreateModel : PageModel
    {
        private readonly ITenantService       _tenantService;
        private readonly GetPublicPlansHandler _getPlans;        // ← NEW
        private readonly ILogger<CreateModel>  _logger;

        public CreateModel(
            ITenantService        tenantService,
            GetPublicPlansHandler  getPlans,                     // ← NEW
            ILogger<CreateModel>   logger)
        {
            _tenantService = tenantService;
            _getPlans      = getPlans;
            _logger        = logger;
        }

        // ==================== PROPERTIES ====================
        [BindProperty]
        public InputModel Input { get; set; } = new();

        [TempData]
        public string? ErrorMessage { get; set; }

        public List<SelectListItem> CountryOptions  { get; set; } = new();
        public List<SelectListItem> TimezoneOptions { get; set; } = new();
        public List<SelectListItem> CurrencyOptions { get; set; } = new();
        public List<SelectListItem> PlanOptions     { get; set; } = new();

        // Full plan detail cards — available as JSON in the view for
        // a JS-powered "selected plan limits" preview panel.
        public List<PlanCardDto> PlanDetails { get; set; } = new();  // ← NEW

        // ==================== INPUT MODEL ====================
        public class InputModel
        {
            [Required(ErrorMessage = "Tenant name is required")]
            [StringLength(200, MinimumLength = 2, ErrorMessage = "Name must be between 2 and 200 characters")]
            public string Name { get; set; } = string.Empty;

            [Required(ErrorMessage = "Email is required")]
            [EmailAddress(ErrorMessage = "Invalid email format")]
            public string FromEmail { get; set; } = string.Empty;

            [Phone(ErrorMessage = "Invalid phone number")]
            public string? Phone { get; set; }

            public Guid? CountryId { get; set; }

            [Required(ErrorMessage = "Currency is required")]
            public string DefaultCurrency { get; set; } = "INR";

            [Required(ErrorMessage = "Timezone is required")]
            public string TimeZone { get; set; } = "UTC";

            [Required(ErrorMessage = "Plan is required")]
            public string Plan { get; set; } = "starter";       // ← lowercase to match Plans.Name

            public string PreferredLanguage { get; set; } = "en";
            public bool   IsActive          { get; set; } = true;
            public string? Domain           { get; set; }

            [EmailAddress(ErrorMessage = "Invalid reply-to email format")]
            public string? ReplyToEmail { get; set; }
        }

        // ==================== GET ====================
        public async Task<IActionResult> OnGetAsync()
        {
            try
            {
                await LoadDropdownsAsync();
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading create tenant page");
                TempData["ErrorMessage"] = "Failed to load form.";
                return RedirectToPage("./Index");
            }
        }

        // ==================== POST ====================
        public async Task<IActionResult> OnPostAsync()
        {
            if (!ModelState.IsValid)
            {
                await LoadDropdownsAsync();
                return Page();
            }

            try
            {
                var countries    = await _tenantService.GetCountriesAsync();
                var currencyCode = (Input.DefaultCurrency ?? string.Empty).Trim().ToUpperInvariant();

                if (currencyCode.Length != 3)
                {
                    ModelState.AddModelError("Input.DefaultCurrency",
                        "Currency code must be a 3-letter ISO code (e.g., THB).");
                    await LoadDropdownsAsync();
                    return Page();
                }

                var command = new CreateTenantCommand(
                    Name:              Input.Name,
                    FromEmail:         Input.FromEmail,
                    DefaultCurrency:   currencyCode,
                    TimeZone:          Input.TimeZone,
                    Phone:             Input.Phone,
                    CountryId:         Input.CountryId,
                    PreferredLanguage: Input.PreferredLanguage,
                    Plan:              Input.Plan,
                    IsActive:          Input.IsActive,
                    Domain:            Input.Domain,
                    ReplyToEmail:      Input.ReplyToEmail
                );

                var result = await _tenantService.CreateAsync(command);

                TempData["SuccessMessage"] = $"Tenant '{result.Name}' created successfully!";
                return RedirectToPage("./Detail", new { id = result.Id });
            }
            catch (InvalidOperationException ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
                await LoadDropdownsAsync();
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating tenant");
                ModelState.AddModelError(string.Empty, "Failed to create tenant. Please try again.");
                await LoadDropdownsAsync();
                return Page();
            }
        }

        // ==================== LOAD DROPDOWNS ====================
        private async Task LoadDropdownsAsync()
        {
            try
            {
                // Countries
                var countries = await _tenantService.GetCountriesAsync();
                CountryOptions = countries.Select(c => new SelectListItem
                {
                    Value    = c.Id.ToString(),
                    Text     = $"{c.Name} ({c.Code})",
                    Selected = Input.CountryId.HasValue && Input.CountryId == c.Id
                }).ToList();
                CountryOptions.Insert(0, new SelectListItem("-- Select Country --", "",
                    Input.CountryId == null));

                // Timezones
                var timezones = await _tenantService.GetTimezonesAsync(commonOnly: true);
                TimezoneOptions = timezones.Select(tz => new SelectListItem
                {
                    Value    = tz.Value,
                    Text     = tz.DisplayName,
                    Selected = tz.Value == Input.TimeZone
                }).ToList();

                // Currencies
                var currencyCodes = countries
                    .Select(c => (c.CurrencyCode ?? "").Trim().ToUpper())
                    .Where(code => !string.IsNullOrEmpty(code))
                    .Distinct()
                    .OrderBy(code => code)
                    .ToList();

                CurrencyOptions = currencyCodes.Select(code => new SelectListItem
                {
                    Value    = code,
                    Text     = $"{code} - {GetCurrencyDisplayName(code)}",
                    Selected = string.Equals(code, Input.DefaultCurrency,
                                             StringComparison.OrdinalIgnoreCase)
                }).ToList();

                // ── Plans from DB (replaces hardcoded GetPlansAsync + GetPlanDisplayText) ──
                PlanDetails = await _getPlans.Handle();

                PlanOptions = PlanDetails.Select(p => new SelectListItem
                {
                    Value    = p.Name,   // canonical lowercase key stored in Tenant.Plan
                    Text     = BuildPlanOptionText(p),
                    Selected = string.Equals(p.Name, Input.Plan,
                                             StringComparison.OrdinalIgnoreCase)
                }).ToList();

                _logger.LogInformation(
                    "Loaded {Countries} countries, {Timezones} timezones, {Plans} plans",
                    countries.Count, timezones.Count, PlanDetails.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading dropdowns");
                throw;
            }
        }

        // Builds option text from live DB data — no hardcoded limits.
        // Example: "Professional ($29/mo) — 10 users, 1,000 leads, 500 deals, 10 GB"
        private static string BuildPlanOptionText(PlanCardDto p)
        {
            var price = p.MonthlyPrice == 0 ? "Free" : $"${p.MonthlyPrice:N0}/mo";
            return $"{p.DisplayName} ({price}) — {p.MaxUsers} users, " +
                   $"{p.MaxLeads:N0} leads, {p.MaxDeals:N0} deals, {p.StorageLimitDisplay}";
        }

        // ==================== HELPER: CURRENCY NAME ====================
        private static readonly Dictionary<string, string> CurrencyNames =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["USD"] = "US Dollar",       ["EUR"] = "Euro",
                ["GBP"] = "British Pound",   ["INR"] = "Indian Rupee",
                ["JPY"] = "Japanese Yen",    ["AUD"] = "Australian Dollar",
                ["CAD"] = "Canadian Dollar", ["SGD"] = "Singapore Dollar",
                ["THB"] = "Thai Baht",       ["MYR"] = "Malaysian Ringgit",
                ["IDR"] = "Indonesian Rupiah",["PHP"] = "Philippine Peso",
                ["VND"] = "Vietnamese Dong", ["AED"] = "UAE Dirham",
                ["SAR"] = "Saudi Riyal"
            };

        private static string GetCurrencyDisplayName(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return "";
            return CurrencyNames.TryGetValue(code.Trim().ToUpperInvariant(), out var name)
                ? name : code.Trim().ToUpperInvariant();
        }
    }
}
