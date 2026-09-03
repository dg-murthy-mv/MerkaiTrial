// =====================================================================
// CreateEditModel.cs
// Location: MerkaiTrial.Web/Pages/Admin/Plans/CreateEdit.cshtml.cs
// =====================================================================

using MerkaiTrial.Application.Commands.Plans;
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace MerkaiTrial.Admin.Web.Pages.Admin.Plans;

public class CreateEditModel : PageModel
{
    private readonly GetPlanDetailHandler  _getDetail;
    private readonly CreatePlanHandler     _create;
    private readonly UpdatePlanHandler     _update;
    private readonly ILogger<CreateEditModel> _logger;

    public CreateEditModel(
        GetPlanDetailHandler  getDetail,
        CreatePlanHandler     create,
        UpdatePlanHandler     update,
        ILogger<CreateEditModel> logger)
    {
        _getDetail = getDetail;
        _create    = create;
        _update    = update;
        _logger    = logger;
    }

    // ── Route param ───────────────────────────────────────────────
    [BindProperty(SupportsGet = true)]
    public Guid? PlanId { get; set; }

    // ── Form model ────────────────────────────────────────────────
    [BindProperty]
    public PlanInputModel Input { get; set; } = new();

    // ── Feature catalog shown as checkboxes ──────────────────────
    // Sourced from FeatureCatalog (Application/Configuration) so this list
    // and the Detail page's feature display can never drift apart.
    public List<FeatureDefinition> AvailableFeatures => FeatureCatalog.All;

    public HashSet<string> SelectedFeatures { get; private set; } = new();

    // ── Country pricing ──────────────────────────────────────────
    // Fixed currency set the form always renders a row for, regardless
    // of what's already stored (so new plans can price all markets up front).
    public static readonly string[] Currencies = PlanCurrencies.Codes;

    public List<PlanPricingInput> PricingRows { get; private set; } = new();

    // ── GET ───────────────────────────────────────────────────────
    public async Task<IActionResult> OnGetAsync()
    {
        if (PlanId.HasValue)
        {
            // Edit mode — load existing plan
            try
            {
                var plan = await _getDetail.Handle(PlanId.Value);
                Input = new PlanInputModel
                {
                    Name          = plan.Name,
                    DisplayName   = plan.DisplayName,
                    Description   = plan.Description,
                    MaxUsers      = plan.MaxUsers,
                    MaxLeads      = plan.MaxLeads,
                    MaxDeals      = plan.MaxDeals,
                    MaxContacts   = plan.MaxContacts,
                    MaxCompanies  = plan.MaxCompanies,
                    StorageLimitGB = (int)(plan.StorageLimitBytes / 1_073_741_824),
                    MonthlyPrice  = plan.MonthlyPrice,
                    AnnualPrice   = plan.AnnualPrice,
                    Features      = plan.Features,
                    SortOrder     = plan.SortOrder,
                    IsHighlighted = plan.IsHighlighted,
                    BadgeText     = plan.BadgeText,
                    IsPublic      = plan.IsPublic,
                    IsActive      = plan.IsActive,
                    IsTrial            = plan.IsTrial,
                    TrialDurationDays  = plan.TrialDurationDays > 0 ? plan.TrialDurationDays : 45
                };
                SelectedFeatures = plan.FeatureList.ToHashSet();
                PricingRows = BuildPricingRows(plan.PlanPricings.Select(
                    p => new PlanPricingInput(p.CurrencyCode, p.MonthlyPrice, p.AnnualPrice)));
                Input.PlanPricingsJson = JsonSerializer.Serialize(PricingRows);
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
        }
        else
        {
            // Create mode — sensible defaults
            Input = new PlanInputModel
            {
                MaxUsers       = 5,
                MaxLeads       = 100,
                MaxDeals       = 50,
                MaxContacts    = 500,
                MaxCompanies   = 100,
                StorageLimitGB = 5,
                MonthlyPrice   = 0,
                AnnualPrice    = 0,
                SortOrder      = 99,
                IsPublic       = true,
                IsActive       = true,
                IsTrial            = false,
                TrialDurationDays  = 45   // sensible default if the toggle is switched on
            };
            PricingRows = BuildPricingRows(Enumerable.Empty<PlanPricingInput>());
            Input.PlanPricingsJson = JsonSerializer.Serialize(PricingRows);
        }

        return Page();
    }

    // ── POST ──────────────────────────────────────────────────────
    public async Task<IActionResult> OnPostAsync()
    {
        // Parse features / pricing JSON from hidden fields back to validate
        SelectedFeatures = ParseFeatureSet(Input.Features);
        var pricing = ParsePricingSet(Input.PlanPricingsJson);
        PricingRows = BuildPricingRows(pricing);

        // Trial plans must specify a duration
        if (Input.IsTrial && Input.TrialDurationDays <= 0)
        {
            ModelState.AddModelError(nameof(Input.TrialDurationDays), "Trial duration (days) is required for trial plans.");
        }

        if (!ModelState.IsValid)
            return Page();

        try
        {
            if (PlanId.HasValue)
            {
                // ── Update ─────────────────────────────────────────
                var cmd = new UpdatePlanCommand(
                    PlanId:        PlanId.Value,
                    DisplayName:   Input.DisplayName,
                    Description:   Input.Description,
                    MaxUsers:      Input.MaxUsers,
                    MaxLeads:      Input.MaxLeads,
                    MaxDeals:      Input.MaxDeals,
                    MaxContacts:   Input.MaxContacts,
                    MaxCompanies:  Input.MaxCompanies,
                    StorageLimitGB: Input.StorageLimitGB,
                    MonthlyPrice:  Input.MonthlyPrice,
                    AnnualPrice:   Input.AnnualPrice,
                    Features:      Input.Features,
                    SortOrder:     Input.SortOrder,
                    IsHighlighted: Input.IsHighlighted,
                    IsPublic:      Input.IsPublic,
                    IsActive:      Input.IsActive,
                    BadgeText:     Input.BadgeText,
                    IsTrial:            Input.IsTrial,
                    TrialDurationDays:  Input.IsTrial ? Input.TrialDurationDays : 0,
                    PlanPricings:       pricing,
                    UpdatedBy:     User.Identity?.Name ?? "SuperAdmin"
                );

                await _update.Handle(cmd);
                TempData["Success"] = $"Plan '{Input.DisplayName}' updated successfully.";
            }
            else
            {
                // ── Create ─────────────────────────────────────────
                var cmd = new CreatePlanCommand(
                    Name:          Input.Name,
                    DisplayName:   Input.DisplayName,
                    Description:   Input.Description,
                    MaxUsers:      Input.MaxUsers,
                    MaxLeads:      Input.MaxLeads,
                    MaxDeals:      Input.MaxDeals,
                    MaxContacts:   Input.MaxContacts,
                    MaxCompanies:  Input.MaxCompanies,
                    StorageLimitGB: Input.StorageLimitGB,
                    MonthlyPrice:  Input.MonthlyPrice,
                    AnnualPrice:   Input.AnnualPrice,
                    Features:      Input.Features,
                    SortOrder:     Input.SortOrder,
                    IsHighlighted: Input.IsHighlighted,
                    IsPublic:      Input.IsPublic,
                    IsActive:      Input.IsActive,
                    BadgeText:     Input.BadgeText,
                    IsTrial:            Input.IsTrial,
                    TrialDurationDays:  Input.IsTrial ? Input.TrialDurationDays : 0,
                    PlanPricings:       pricing,
                    CreatedBy:     User.Identity?.Name ?? "SuperAdmin"
                );

                await _create.Handle(cmd);
                TempData["Success"] = $"Plan '{Input.DisplayName}' created successfully.";
            }

            return RedirectToPage("./Index");
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            SelectedFeatures = ParseFeatureSet(Input.Features);
            return Page();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving plan");
            ModelState.AddModelError(string.Empty, "An unexpected error occurred. Please try again.");
            SelectedFeatures = ParseFeatureSet(Input.Features);
            return Page();
        }
    }

    private static HashSet<string> ParseFeatureSet(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new HashSet<string>();
        try { return JsonSerializer.Deserialize<List<string>>(json)?.ToHashSet() ?? new(); }
        catch { return new HashSet<string>(); }
    }

    private static List<PlanPricingInput> ParsePricingSet(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<PlanPricingInput>();
        try
        {
            var rows = JsonSerializer.Deserialize<List<PlanPricingInput>>(json) ?? new();
            // Keep only recognized currencies, one row per currency.
            return rows
                .Where(r => PlanCurrencies.Codes.Contains(r.CurrencyCode))
                .GroupBy(r => r.CurrencyCode)
                .Select(g => g.Last())
                .ToList();
        }
        catch { return new List<PlanPricingInput>(); }
    }

    /// <summary>
    /// Ensures the form always shows one row per supported currency,
    /// pre-filled with existing values (or zero if not yet priced).
    /// </summary>
    private static List<PlanPricingInput> BuildPricingRows(IEnumerable<PlanPricingInput> existing)
    {
        var byCurrency = existing.ToDictionary(p => p.CurrencyCode, p => p);
        return PlanCurrencies.Codes
            .Select(c => byCurrency.TryGetValue(c, out var row)
                ? row
                : new PlanPricingInput(c, 0, 0))
            .ToList();
    }
}

// ── Form input model (flat, validation attributes) ────────────────
public class PlanInputModel
{
    [Required, StringLength(50, MinimumLength = 2)]
    [RegularExpression(@"^[a-z0-9_-]+$", ErrorMessage = "Lowercase letters, numbers, hyphens and underscores only")]
    public string Name { get; set; } = string.Empty;

    [Required, StringLength(100, MinimumLength = 2)]
    [Display(Name = "Display Name")]
    public string DisplayName { get; set; } = string.Empty;

    [StringLength(500)]
    public string? Description { get; set; }

    [Required, Range(1, 10000)]
    [Display(Name = "Max Users")]
    public int MaxUsers { get; set; }

    [Required, Range(1, 1_000_000)]
    [Display(Name = "Max Leads")]
    public int MaxLeads { get; set; }

    [Required, Range(1, 1_000_000)]
    [Display(Name = "Max Deals")]
    public int MaxDeals { get; set; }

    [Required, Range(1, 1_000_000)]
    [Display(Name = "Max Contacts")]
    public int MaxContacts { get; set; }

    [Required, Range(1, 1_000_000)]
    [Display(Name = "Max Companies")]
    public int MaxCompanies { get; set; }

    [Required, Range(1, 10000)]
    [Display(Name = "Storage Limit (GB)")]
    public int StorageLimitGB { get; set; }

    [Required, Range(0, 100_000)]
    [Display(Name = "Monthly Price")]
    public decimal MonthlyPrice { get; set; }

    [Required, Range(0, 100_000)]
    [Display(Name = "Annual Price")]
    public decimal AnnualPrice { get; set; }

    public string? Features { get; set; }   // JSON from hidden field

    [Range(1, 999)]
    [Display(Name = "Sort Order")]
    public int SortOrder { get; set; } = 99;

    [Display(Name = "Highlighted")]
    public bool IsHighlighted { get; set; }

    [StringLength(50)]
    [Display(Name = "Badge Text")]
    public string? BadgeText { get; set; }

    [Display(Name = "Public")]
    public bool IsPublic { get; set; } = true;

    [Display(Name = "Active")]
    public bool IsActive { get; set; } = true;

    // ── Trial plan ──────────────────────────────────────────────
    [Display(Name = "Trial Plan")]
    public bool IsTrial { get; set; }

    [Range(0, 365)]
    [Display(Name = "Trial Duration (days)")]
    public int TrialDurationDays { get; set; } = 45;

    // ── Country pricing (JSON from hidden field) ──────────────────
    [Display(Name = "Country Pricing")]
    public string? PlanPricingsJson { get; set; }
}
