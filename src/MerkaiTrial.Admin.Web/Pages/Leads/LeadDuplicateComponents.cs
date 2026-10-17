// =====================================================================
// LeadDuplicateComponents.cs
// Location: MerkaiTrial.Admin.Web/Pages/Leads/LeadDuplicateComponents.cs
//
// NEW FILE (093). What Add lead and Edit lead share for the duplicate
// warning:
//
//   LeadDuplicateCheck      asks the API, and swallows a failure — a
//                           duplicate check must never stop a save
//   LeadDuplicateWarningVm  the model for _LeadDuplicateWarning.cshtml
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Leads;
using LeadDuplicateDto = MerkaiTrial.Application.Commands.Leads.LeadDuplicateDto;

namespace MerkaiTrial.Admin.Web.Pages.Leads
{
    public static class LeadDuplicateCheck
    {
        /// <summary>
        /// The leads that look like this one, or an empty list — when there
        /// are none, when nothing identifying was typed, or when the check
        /// itself failed (logged as a warning).
        /// </summary>
        public static async Task<List<LeadDuplicateDto>> FindAsync(
            ILeadService leads, ILogger logger,
            string? email, string? phone, string? fullName, string? companyName, Guid? excludeLeadId)
        {
            try
            {
                return await leads.FindDuplicatesAsync(email, phone, fullName, companyName, excludeLeadId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "The duplicate check failed; saving without it");
                return new List<LeadDuplicateDto>();
            }
        }

        /// <summary>Trimmed, case-insensitive: did a field that identifies a person change?</summary>
        public static bool Changed(string? before, string? after)
            => !string.Equals((before ?? "").Trim(), (after ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public class LeadDuplicateWarningVm
    {
        public List<LeadDuplicateDto> Duplicates { get; set; } = new();

        /// <summary>Edit: the lead being edited, so each match can offer "Merge these two".</summary>
        public Guid? CurrentLeadId { get; set; }

        /// <summary>Merging needs update and delete rights; without them the link is not offered.</summary>
        public bool CanMerge { get; set; }

        public string SaveAnywayText { get; set; } = "Save anyway";
    }
}
