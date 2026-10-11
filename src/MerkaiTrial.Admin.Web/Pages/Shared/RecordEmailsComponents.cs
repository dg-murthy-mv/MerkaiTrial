// =====================================================================
// RecordEmailsComponents.cs
// Location: MerkaiTrial.Admin.Web/Pages/Shared/RecordEmailsComponents.cs
//
// NEW FILE (083). The model behind _RecordEmails.cshtml — the "Emails"
// section on a lead, a deal and a contact. One component, three pages, so
// the three cannot drift apart in what they show or how they say "this
// could not be loaded".
// =====================================================================

using MerkaiTrial.Admin.Web.Services.InboundEmails;
using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Admin.Web.Pages.Shared
{
    public sealed class RecordEmailsVm
    {
        public List<InboundEmailListItemDto> Items { get; init; } = new();

        /// <summary>The emails could not be loaded — the card says so instead of "none yet".</summary>
        public bool LoadFailed { get; init; }

        /// <summary>The workspace address to BCC, or null when receiving is not set up.</summary>
        public string? WorkspaceAddress { get; init; }

        /// <summary>Tracked reply addresses go on quote emails.</summary>
        public bool ReceivingOn { get; init; }

        /// <summary>"lead", "deal", "contact" — for the empty-state sentence.</summary>
        public string RecordLabel { get; init; } = "record";

        /// <summary>The page's own tenant-aware date formatter.</summary>
        public Func<DateTime, string> FormatDateTime { get; init; } = d => d.ToString("dd MMM yyyy HH:mm");

        public int Count => Items.Count;

        /// <summary>
        /// Loads the section for one record. Never throws: a failure costs
        /// the section (LoadFailed), not the page — like every other
        /// optional panel on these pages.
        /// </summary>
        public static async Task<RecordEmailsVm> LoadAsync(
            IInboundEmailService service,
            string entityType,
            Guid entityId,
            string recordLabel,
            Func<DateTime, string> formatDateTime,
            ILogger logger)
        {
            try
            {
                var result = await service.GetForRecordAsync(entityType, entityId);
                return new RecordEmailsVm
                {
                    Items = result.Items ?? new(),
                    WorkspaceAddress = result.WorkspaceAddress,
                    ReceivingOn = result.ReceivingOn,
                    RecordLabel = recordLabel,
                    FormatDateTime = formatDateTime
                };
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load emails for {EntityType} {EntityId}", entityType, entityId);
                return new RecordEmailsVm
                {
                    LoadFailed = true,
                    RecordLabel = recordLabel,
                    FormatDateTime = formatDateTime
                };
            }
        }
    }
}
