// =====================================================================
// LEAD STATUSES — Settings
// Location: MerkaiTrial.Admin.Web/Pages/Settings/LeadStatuses/Index.cshtml.cs
//
// NEW FILE. Mirrors Settings/Pipeline for deals.
//
// WHY THIS MATTERS
//   "Lead" is not a universal word. Sathorn's process starts with an
//   enquiry from a site visit request; an Indian software firm's starts
//   with a demo signup. Telling both that their first step is called
//   "New" is a small thing that reads as the software not fitting.
//
// WHAT IS NOT EDITABLE, AND WHY
//   Category. Moving a status from Qualified to Open would silently
//   change what conversion allows, and every past report of qualified
//   leads would mean something different. If a client needs a different
//   category, they add a status and move the leads.
//
//   The system status (Converted) can be renamed but not retired,
//   deleted, or made the starting point — conversion has to have
//   somewhere to put the lead.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Leads;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.LeadStatuses;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;

namespace MerkaiTrial.Admin.Web.Pages.Settings.LeadStatuses;

public class IndexModel : AuthorizedPageModel
{
    private readonly ILeadStatusService _statuses;

    protected override string ModuleName => Modules.Leads;

    public IndexModel(
        ILeadStatusService statuses,
        IAuthorizationService authorizationService,
        ICurrentUserService currentUserService,
        ILogger<IndexModel> logger)
        : base(authorizationService, currentUserService, logger)
    {
        _statuses = statuses;
    }

    public List<LeadStatusDto> Statuses { get; private set; } = new();

    /// <summary>Statuses a person can move a lead through.</summary>
    public List<LeadStatusDto> WorkingStatuses =>
        Statuses.Where(s => !s.IsSystem).OrderBy(s => s.SortOrder).ToList();

    /// <summary>Set by the system — shown, but barely editable.</summary>
    public List<LeadStatusDto> SystemStatuses =>
        Statuses.Where(s => s.IsSystem).OrderBy(s => s.SortOrder).ToList();

    [BindProperty] public StatusInput Input { get; set; } = new();

    [BindProperty(SupportsGet = true)] public Guid? EditId { get; set; }

    public class StatusInput
    {
        public Guid? StatusId { get; set; }

        [Required(ErrorMessage = "Give the status a name")]
        [StringLength(100)]
        public string Name { get; set; } = string.Empty;

        [Range(0, 15, ErrorMessage = "Score must be between 0 and 15")]
        public int Score { get; set; } = 5;

        public LeadStatusCategory Category { get; set; } = LeadStatusCategory.Open;

        public bool IsActive { get; set; } = true;
    }

    // =================================================================

    public async Task<IActionResult> OnGetAsync()
    {
        var check = await ValidatePermissionAsync(Actions.Read);
        if (check != null) return check;

        await InitializePermissionsAsync();
        await LoadAsync();

        if (EditId.HasValue)
        {
            var s = Statuses.FirstOrDefault(x => x.Id == EditId.Value);
            if (s is null) EditId = null;
            else Input = new StatusInput
            {
                StatusId = s.Id, Name = s.Name, Score = s.Score,
                Category = s.Category, IsActive = s.IsActive
            };
        }

        return Page();
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        var check = await ValidatePermissionAsync(Actions.Update);
        if (check != null) return check;

        ModelState.Clear();
        if (!TryValidateModel(Input, nameof(Input)))
        {
            await InitializePermissionsAsync();
            await LoadAsync();
            return Page();
        }

        try
        {
            await _statuses.CreateAsync(new CreateLeadStatusDto(
                TenantId: Guid.Empty,              // set server-side
                Name: Input.Name,
                Score: Input.Score,
                Category: Input.Category));

            TempData["SuccessMessage"] = $"\"{Input.Name}\" added.";
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = Explain(ex, "Couldn't add that status.");
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUpdateAsync()
    {
        var check = await ValidatePermissionAsync(Actions.Update);
        if (check != null) return check;

        if (Input.StatusId is null) return RedirectToPage();

        ModelState.Clear();
        if (!TryValidateModel(Input, nameof(Input)))
        {
            EditId = Input.StatusId;
            await InitializePermissionsAsync();
            await LoadAsync();
            return Page();
        }

        try
        {
            await _statuses.UpdateAsync(new UpdateLeadStatusDefDto(
                TenantId: Guid.Empty,
                StatusId: Input.StatusId.Value,
                Name: Input.Name,
                Score: Input.Score,
                IsActive: Input.IsActive));

            TempData["SuccessMessage"] = "Status updated.";
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = Explain(ex, "Couldn't save that change.");
        }

        return RedirectToPage();
    }

    /// <summary>
    /// Sends the whole new order rather than one position, so two people
    /// reordering at once cannot leave duplicate positions behind.
    /// </summary>
    public async Task<IActionResult> OnPostMoveAsync(Guid statusId, string direction)
    {
        var check = await ValidatePermissionAsync(Actions.Update);
        if (check != null) return check;

        try
        {
            await LoadAsync();

            var ordered = Statuses.OrderBy(s => s.SortOrder).Select(s => s.Id).ToList();
            var i = ordered.IndexOf(statusId);
            if (i < 0) return RedirectToPage();

            var j = direction == "up" ? i - 1 : i + 1;
            if (j < 0 || j >= ordered.Count) return RedirectToPage();

            (ordered[i], ordered[j]) = (ordered[j], ordered[i]);

            await _statuses.ReorderAsync(new ReorderLeadStatusesDto(Guid.Empty, ordered));
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = Explain(ex, "Couldn't reorder the statuses.");
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSetDefaultAsync(Guid statusId)
    {
        var check = await ValidatePermissionAsync(Actions.Update);
        if (check != null) return check;

        try
        {
            await _statuses.SetDefaultAsync(statusId);
            TempData["SuccessMessage"] = "New leads will start here.";
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = Explain(ex, "Couldn't change the starting status.");
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid statusId)
    {
        var check = await ValidatePermissionAsync(Actions.Delete);
        if (check != null) return check;

        try
        {
            await _statuses.DeleteAsync(statusId);
            TempData["SuccessMessage"] = "Status removed.";
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = Explain(ex, "Couldn't remove that status.");
        }

        return RedirectToPage();
    }

    // =================================================================

    private async Task LoadAsync()
    {
        try
        {
            // selectableOnly: false — settings must show retired statuses
            // and the system one, or they cannot be managed at all.
            Statuses = await _statuses.GetAsync(selectableOnly: false);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load lead statuses");
            TempData["ErrorMessage"] = "Couldn't load your lead statuses. Please try again.";
            Statuses = new();
        }
    }

    private static string Explain(Exception ex, string fallback)
        => string.IsNullOrWhiteSpace(ex.Message) || ex is NullReferenceException
            ? fallback
            : ex.Message;

    // ── View helpers ──────────────────────────────────────────────────

    public bool IsEditing(LeadStatusDto s) => EditId.HasValue && EditId.Value == s.Id;

    public bool IsFirst(LeadStatusDto s) => Statuses.OrderBy(x => x.SortOrder).First().Id == s.Id;
    public bool IsLast(LeadStatusDto s)  => Statuses.OrderBy(x => x.SortOrder).Last().Id  == s.Id;

    public string CategoryBadge(LeadStatusCategory c) => c switch
    {
        LeadStatusCategory.Qualified    => "bg-success",
        LeadStatusCategory.Disqualified => "bg-secondary",
        LeadStatusCategory.Converted    => "bg-warning text-dark",
        _ => "bg-primary"
    };

    public string CategoryLabel(LeadStatusCategory c) => c switch
    {
        LeadStatusCategory.Qualified    => "Qualified",
        LeadStatusCategory.Disqualified => "Not pursuing",
        LeadStatusCategory.Converted    => "Converted",
        _ => "Open"   // was "Working" — it put a "Working" badge on the New status
    };
}
