// =====================================================================
// TENANT EDIT PAGE - Backend (Final, enum-free)
// Location: MerkaiTrial.Admin.Web/Pages/Admin/Tenants/Edit.cshtml.cs
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Tenants;
using MerkaiTrial.Application.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using System.ComponentModel.DataAnnotations;

namespace MerkaiTrial.Admin.Web.Pages.Admin.Tenants
{
    public class EditModel : PageModel
    {
        private readonly ITenantService _tenantService;
        private readonly ILogger<EditModel> _logger;

        public EditModel(ITenantService tenantService, ILogger<EditModel> logger)
        {
            _tenantService = tenantService;
            _logger = logger;
        }

        // ==================== PROPERTIES ====================
        [BindProperty] public InputModel Input { get; set; } = new();

        [BindProperty(SupportsGet = true)]
        public Guid Id { get; set; }

        [TempData]
        public string? ErrorMessage { get; set; }
        public List<SelectListItem> CountryOptions { get; set; } = new();
        public List<SelectListItem> TimezoneOptions { get; set; } = new();
        public List<SelectListItem> CurrencyOptions { get; set; } = new();

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
            public string DefaultCurrency { get; set; } = string.Empty;

            [Required(ErrorMessage = "Timezone is required")]
            public string TimeZone { get; set; } = string.Empty;
        }

        // ==================== GET ====================
        public async Task<IActionResult> OnGetAsync()
        {
            try
            {
                var tenant = await _tenantService.GetByIdAsync(Id);

                Input = new InputModel
                {
                    Name = tenant.Name,
                    FromEmail = tenant.FromEmail,
                    Phone = tenant.Phone,
                    DefaultCurrency = (tenant.DefaultCurrency ?? "").Trim().ToUpperInvariant(),
                    TimeZone = tenant.TimeZone,
                    // If your TenantDto has CountryId, set it directly:
                    CountryId = tenant.CountryId   // <-- ensure your DTO exposes this; else map after loading countries
                    
                };

                await LoadDropdownsAsync();
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading tenant {TenantId}", Id);
                TempData["ErrorMessage"] = "Failed to load tenant.";
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
                var currencyCode = (Input.DefaultCurrency ?? "").Trim().ToUpperInvariant();
                if (currencyCode.Length != 3 || !currencyCode.All(char.IsLetter))
                {
                    ModelState.AddModelError("Input.DefaultCurrency", "Currency code must be a 3-letter ISO code (e.g., THB).");
                    await LoadDropdownsAsync();
                    return Page();
                }

                var cmd = new UpdateTenantCommand(
                    TenantId: Id,
                    Name: Input.Name.Trim(),
                    FromEmail: Input.FromEmail.Trim(),
                    Phone: Input.Phone?.Trim(),
                    DefaultCurrency: currencyCode,   // string, not enum
                    TimeZone: Input.TimeZone.Trim()
                // If your update also accepts CountryId, add it to your command and pass Input.CountryId
                );

                await _tenantService.UpdateAsync(cmd);

                TempData["SuccessMessage"] = "Tenant updated successfully!";
                return RedirectToPage("./Detail", new { id = Id });
            }
            catch (InvalidOperationException ex)
            {
                ErrorMessage = ex.Message;
                await LoadDropdownsAsync();
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating tenant {TenantId}", Id);
                ErrorMessage = "Failed to update tenant. Please try again.";
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

                // If CountryId is not set but the current currency matches a country's currency, preselect that country
                if (!Input.CountryId.HasValue && !string.IsNullOrWhiteSpace(Input.DefaultCurrency))
                {
                    var match = countries.FirstOrDefault(c =>
                        string.Equals((c.CurrencyCode ?? "").Trim(), Input.DefaultCurrency.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (match != null) Input.CountryId = match.Id;
                }

                CountryOptions = countries.Select(c => new SelectListItem
                {
                    Value = c.Id.ToString(),
                    Text = $"{c.Name} ({c.Code})",
                    Selected = Input.CountryId.HasValue && Input.CountryId == c.Id
                }).ToList();

                CountryOptions.Insert(0, new SelectListItem("-- Select Country --", "", !Input.CountryId.HasValue));

                // Timezones
                var timezones = await _tenantService.GetTimezonesAsync(commonOnly: false);
                TimezoneOptions = timezones.Select(tz => new SelectListItem
                {
                    Value = tz.Value,
                    Text = tz.DisplayName,
                    Selected = tz.Value == Input.TimeZone
                }).ToList();

                // Currencies (distinct from Countries table)
                var currencyCodes = countries
                    .Select(c => (c.CurrencyCode ?? "").Trim().ToUpper())
                    .Where(code => code.Length == 3)
                    .Distinct()
                    .OrderBy(code => code)
                    .ToList();

                // If currency is empty, derive from selected country
                if (string.IsNullOrWhiteSpace(Input.DefaultCurrency) && Input.CountryId.HasValue)
                {
                    var sel = countries.FirstOrDefault(c => c.Id == Input.CountryId.Value);
                    if (!string.IsNullOrWhiteSpace(sel?.CurrencyCode))
                        Input.DefaultCurrency = sel!.CurrencyCode!.Trim().ToUpperInvariant();
                }

                CurrencyOptions = currencyCodes.Select(code => new SelectListItem
                {
                    Value = code,
                    Text = $"{code} - {GetCurrencyDisplayName(code)}",
                    Selected = string.Equals(code, Input.DefaultCurrency, StringComparison.OrdinalIgnoreCase)
                }).ToList();

                _logger.LogInformation("Loaded {CountryCount} countries, {TimezoneCount} timezones, {CurrencyCount} currencies",
                       countries.Count, timezones.Count, currencyCodes.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading dropdowns");
                throw;
            }
        }

        // ==================== HELPER: GET CURRENCY NAME ====================
        private static readonly Dictionary<string, string> CurrencyNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ["USD"] = "US Dollar",
            ["EUR"] = "Euro",
            ["GBP"] = "British Pound",
            ["INR"] = "Indian Rupee",
            ["JPY"] = "Japanese Yen",
            ["AUD"] = "Australian Dollar",
            ["CAD"] = "Canadian Dollar",
            ["SGD"] = "Singapore Dollar",
            ["THB"] = "Thai Baht",
            ["MYR"] = "Malaysian Ringgit",
            ["IDR"] = "Indonesian Rupiah",
            ["PHP"] = "Philippine Peso",
            ["VND"] = "Vietnamese Dong",
            ["AED"] = "UAE Dirham",
            ["SAR"] = "Saudi Riyal"
        };

        private static string GetCurrencyDisplayName(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return "";
            return CurrencyNames.TryGetValue(code.Trim().ToUpperInvariant(), out var name)
                ? name
                : code.Trim().ToUpperInvariant(); // fallback to the code
        }
    }
}
