// =====================================================================
// MERGE LEADS - Backend
// Location: MerkaiTrial.Admin.Web/Pages/Leads/Merge.cshtml.cs
//
// NEW FILE (093). Two leads into one, choosing each field.
//
//   /Leads/Merge?keepId=…               step 1: find the other lead
//   /Leads/Merge?keepId=…&q=…           (search results)
//   /Leads/Merge?keepId=…&otherId=…     step 2: side by side, pick, merge
//
// THE KEPT LEAD IS ON THE LEFT and its values are chosen by default —
// except where it has nothing and the other lead has something, where the
// other value is pre-chosen so nothing is lost by just pressing Merge.
// "Keep the other one instead" swaps the two.
//
// Rows where both leads hold the same value are shown without a choice.
// Custom fields are listed when either lead has a value, retired fields
// included (marked), so a value on a retired field is not silently lost.
//
// WHO: leads.update AND leads.delete (a merge removes the other lead), the
// same rule the API enforces. Only leads the person can see are found or
// loaded; a converted lead is refused on either side.
// =====================================================================

using System.Globalization;
using MerkaiTrial.Admin.Web.Pages.Shared;
using MerkaiTrial.Admin.Web.Services.CustomFields;
using MerkaiTrial.Admin.Web.Services.Leads;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.LeadStatuses;
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using LeadMergeFields = MerkaiTrial.Application.Commands.Leads.LeadMergeFields;
using MergeLeadsDto = MerkaiTrial.Application.Commands.Leads.MergeLeadsDto;

namespace MerkaiTrial.Admin.Web.Pages.Leads
{
    /// <summary>One field on the merge screen.</summary>
    public class MergeRow
    {
        public string Key { get; init; } = "";
        public string Label { get; init; } = "";
        public string KeepText { get; init; } = "";
        public string OtherText { get; init; } = "";
        public bool Same { get; init; }

        /// <summary>The other lead's value is pre-chosen: the kept lead has none.</summary>
        public bool DefaultOther { get; init; }

        public bool IsCustom { get; init; }
        public bool IsRetired { get; init; }
    }

    public class MergeModel : AuthorizedPageModel
    {
        public const string Keep = "keep";
        public const string Other = "other";

        private readonly ILeadService _leads;
        private readonly ILeadStatusService _statuses;
        private readonly ICustomFieldService _customFields;
        private readonly ICurrentUserService _currentUser;
        private readonly ILogger<MergeModel> _logger;

        protected override string ModuleName => Modules.Leads;

        public MergeModel(
            ILeadService leads,
            ILeadStatusService statuses,
            ICustomFieldService customFields,
            IAuthorizationService authorizationService,
            ICurrentUserService currentUser,
            ILogger<MergeModel> logger)
            : base(authorizationService, currentUser, logger)
        {
            _leads = leads;
            _statuses = statuses;
            _customFields = customFields;
            _currentUser = currentUser;
            _logger = logger;
        }

        // ── State, in the URL ─────────────────────────────────────────
        [BindProperty(SupportsGet = true)] public Guid KeepId { get; set; }
        [BindProperty(SupportsGet = true)] public Guid? OtherId { get; set; }
        [BindProperty(SupportsGet = true)] public string? Q { get; set; }

        /// <summary>Posted: field key → "keep" or "other".</summary>
        [BindProperty] public Dictionary<string, string> Pick { get; set; } = new();

        // ── What the page shows ───────────────────────────────────────
        public LeadDetailDto? KeepLead { get; private set; }
        public LeadDetailDto? OtherLead { get; private set; }
        public List<LeadListItem> SearchResults { get; private set; } = new();
        public List<MergeRow> Rows { get; private set; } = new();

        /// <summary>Why these two cannot be merged (converted). Null when they can.</summary>
        public string? Blocked { get; private set; }

        /// <summary>Shown on THIS response only.</summary>
        public string? PageError { get; private set; }

        public bool Comparing => OtherLead is not null;
        public IEnumerable<MergeRow> DifferentRows => Rows.Where(r => !r.Same);
        public IEnumerable<MergeRow> SameRows => Rows.Where(r => r.Same);

        private List<LeadStatusDto> _statusList = new();

        // =============================================================

        public async Task<IActionResult> OnGetAsync()
        {
            var check = await EnsureAllowedAsync();
            if (check is not null) return check;

            var tenantId = _currentUser.GetCurrentTenantId();
            await LoadStatusesAsync();

            KeepLead = await LoadAsync(tenantId, KeepId);
            if (KeepLead is null)
            {
                TempData["ErrorMessage"] = "That lead no longer exists.";
                return RedirectToPage("./Index");
            }
            if (KeepLead.IsConverted)
            {
                TempData["ErrorMessage"] = "This lead has been converted to a deal, so it can't be merged.";
                return RedirectToPage("./Detail", new { id = KeepId });
            }

            if (OtherId is { } otherId && otherId != Guid.Empty)
            {
                await LoadComparisonAsync(tenantId, otherId);
                return Page();
            }

            await SearchAsync(tenantId);
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            var check = await EnsureAllowedAsync();
            if (check is not null) return check;

            var tenantId = _currentUser.GetCurrentTenantId();
            if (OtherId is not { } otherId || otherId == Guid.Empty)
                return RedirectToPage(new { keepId = KeepId });

            try
            {
                var take = Pick
                    .Where(kv => string.Equals(kv.Value, Other, StringComparison.OrdinalIgnoreCase))
                    .Select(kv => kv.Key)
                    .ToList();

                var result = await _leads.MergeAsync(new MergeLeadsDto
                {
                    KeepLeadId = KeepId,
                    MergeLeadId = otherId,
                    TakeFromOther = take
                });

                TempData["SuccessMessage"] = result.Message;
                return RedirectToPage("./Detail", new { id = result.KeptLeadId });
            }
            catch (InvalidOperationException ex)
            {
                PageError = ex.Message;          // the API's own reason
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Merging lead {Other} into {Keep} failed", otherId, KeepId);
                PageError = "The merge didn't complete, and nothing was changed. Please try again.";
            }

            // Redraw the comparison with the person's choices kept.
            await LoadStatusesAsync();
            KeepLead = await LoadAsync(tenantId, KeepId);
            if (KeepLead is null)
            {
                TempData["ErrorMessage"] = PageError ?? "That lead no longer exists.";
                return RedirectToPage("./Index");
            }
            await LoadComparisonAsync(tenantId, otherId);
            return Page();
        }

        // =============================================================

        /// <summary>Update and delete, like the API. Null when allowed.</summary>
        private async Task<IActionResult?> EnsureAllowedAsync()
        {
            var update = await ValidatePermissionAsync(Actions.Update);
            if (update is not null) return update;
            var delete = await ValidatePermissionAsync(Actions.Delete);
            if (delete is not null) return delete;

            await InitializePermissionsAsync();
            return null;
        }

        private async Task<LeadDetailDto?> LoadAsync(Guid tenantId, Guid id)
        {
            try { return await _leads.GetByIdAsync(tenantId, id); }
            catch (Exception ex)
            {
                _logger.LogInformation(ex, "Lead {Id} could not be loaded for merging", id);
                return null;
            }
        }

        private async Task LoadStatusesAsync()
        {
            try { _statusList = await _statuses.GetAsync(selectableOnly: false); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lead statuses could not be loaded for the merge page");
                _statusList = new();
            }
        }

        public string StatusName(string? key)
            => string.IsNullOrEmpty(key) ? "—" : _statusList.FirstOrDefault(s => s.Key == key)?.Name ?? key;

        private bool IsConvertedKey(string? key)
            => _statusList.FirstOrDefault(s => s.Key == key)?.Category == LeadStatusCategory.Converted;

        /// <summary>Step 1: leads matching Q, not this one and not converted.</summary>
        private async Task SearchAsync(Guid tenantId)
        {
            if (string.IsNullOrWhiteSpace(Q)) return;
            try
            {
                var page = await _leads.GetPaginatedAsync(tenantId, 1, 20, Q.Trim());
                SearchResults = page.Items
                    .Where(l => l.Id != KeepId && !l.HasDeal && !IsConvertedKey(l.Status))
                    .Take(10)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lead search failed on the merge page");
                PageError = "The search didn't work. Please try again.";
            }
        }

        /// <summary>Step 2: the other lead, and one row per field.</summary>
        private async Task LoadComparisonAsync(Guid tenantId, Guid otherId)
        {
            if (otherId == KeepId)
            {
                PageError = "That's the same lead. Choose a different one to merge with.";
                OtherId = null;
                return;
            }

            OtherLead = await LoadAsync(tenantId, otherId);
            if (OtherLead is null)
            {
                PageError = "The other lead no longer exists, or you can't see it.";
                OtherId = null;
                return;
            }

            if (OtherLead.IsConverted)
                Blocked = $"\"{OtherLead.FullName}\" has been converted to a deal, so it can't be merged.";

            List<CustomFieldDefinitionDto> defs;
            try
            {
                defs = await _customFields.GetDefinitionsAsync(CustomFieldEntityTypes.Lead, includeInactive: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Custom fields could not be loaded for the merge page");
                defs = new();
            }

            Rows = BuildRows(KeepLead!, OtherLead, defs);
        }

        private List<MergeRow> BuildRows(LeadDetailDto k, LeadDetailDto o, List<CustomFieldDefinitionDto> defs)
        {
            var culture = CustomFieldFormatter.ResolveCulture(CultureName);
            var dateFormat = TenantCtx.GetDateFormat();
            var rows = new List<MergeRow>();

            void Add(string key, string? keepRaw, string? otherRaw, string keepText, string otherText)
            {
                var same = string.Equals((keepRaw ?? "").Trim(), (otherRaw ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
                rows.Add(new MergeRow
                {
                    Key = key,
                    Label = LeadMergeFields.LabelOf(key),
                    KeepText = keepText,
                    OtherText = otherText,
                    Same = same,
                    DefaultOther = !same && string.IsNullOrWhiteSpace(keepRaw) && !string.IsNullOrWhiteSpace(otherRaw)
                });
            }

            string Money(LeadDetailDto d) => d.ExpectedValue == 0m ? "" : $"{d.Currency} {d.ExpectedValue.ToString("N0", culture)}";
            string Val(LeadDetailDto d) => d.ExpectedValue == 0m ? "" : d.ExpectedValue.ToString(CultureInfo.InvariantCulture);

            Add(LeadMergeFields.FullName, k.FullName, o.FullName, k.FullName, o.FullName);
            Add(LeadMergeFields.Email, k.Email, o.Email, k.Email, o.Email);
            Add(LeadMergeFields.Phone, k.Phone, o.Phone, k.Phone, o.Phone);
            Add(LeadMergeFields.CompanyName, k.CompanyName, o.CompanyName, k.CompanyName ?? "", o.CompanyName ?? "");
            Add(LeadMergeFields.Address, k.Address, o.Address, k.Address ?? "", o.Address ?? "");
            Add(LeadMergeFields.Country, k.CountryId?.ToString(), o.CountryId?.ToString(), k.CountryName ?? "", o.CountryName ?? "");
            Add(LeadMergeFields.Currency, k.Currency, o.Currency, k.Currency, o.Currency);
            Add(LeadMergeFields.Channel, k.ChannelId?.ToString(), o.ChannelId?.ToString(), k.Channel, o.Channel);
            Add(LeadMergeFields.Source, k.SourceId?.ToString() ?? k.Source, o.SourceId?.ToString() ?? o.Source, k.Source, o.Source);
            Add(LeadMergeFields.Industry, k.VerticalId?.ToString(), o.VerticalId?.ToString(), k.VerticalName ?? "", o.VerticalName ?? "");
            Add(LeadMergeFields.Status, k.Status, o.Status, StatusName(k.Status), StatusName(o.Status));
            Add(LeadMergeFields.EstimatedValue, Val(k), Val(o), Money(k), Money(o));
            Add(LeadMergeFields.Owner, k.OwnerUserId, o.OwnerUserId,
                string.IsNullOrEmpty(k.OwnerUserId) ? "" : (k.OwnerName ?? "—"),
                string.IsNullOrEmpty(o.OwnerUserId) ? "" : (o.OwnerName ?? "—"));

            foreach (var f in defs.OrderBy(d => d.IsActive ? 0 : 1).ThenBy(d => d.SortOrder))
            {
                k.CustomFieldValues.TryGetValue(f.Id, out var kw);
                o.CustomFieldValues.TryGetValue(f.Id, out var ow);
                if (string.IsNullOrEmpty(kw) && string.IsNullOrEmpty(ow)) continue;

                string Show(string? wire) => f.FieldType == CustomFieldTypes.Checkbox
                    ? (wire == "true" ? "Yes" : "")
                    : CustomFieldFormatter.Display(f, wire, culture, dateFormat);

                var same = string.Equals(kw ?? "", ow ?? "", StringComparison.Ordinal);
                rows.Add(new MergeRow
                {
                    Key = "cf_" + f.Id.ToString("N"),
                    Label = f.Label,
                    KeepText = Show(kw),
                    OtherText = Show(ow),
                    Same = same,
                    DefaultOther = !same && string.IsNullOrEmpty(kw) && !string.IsNullOrEmpty(ow),
                    IsCustom = true,
                    IsRetired = !f.IsActive
                });
            }

            return rows;
        }

        /// <summary>What a row's radios start on: the posted choice, else the default.</summary>
        public string ChoiceFor(MergeRow row)
            => Pick.TryGetValue(row.Key, out var v) && (v == Keep || v == Other)
                ? v
                : (row.DefaultOther ? Other : Keep);
    }
}
