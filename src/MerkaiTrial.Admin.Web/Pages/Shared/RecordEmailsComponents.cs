// =====================================================================
// RecordEmailsComponents.cs
// Location: MerkaiTrial.Admin.Web/Pages/Shared/RecordEmailsComponents.cs
//
// NEW FILE (083). The model behind _RecordEmails.cshtml — the "Emails"
// section on a lead, a deal and a contact. One component, three pages, so
// the three cannot drift apart in what they show or how they say "this
// could not be loaded".
//
// CHANGES (085 — both directions)
//   The section now shows the quote and invoice emails WE sent, merged
//   with what came in, newest first, as one conversation:
//
//     ↓ customer's reply                     (received — 083)
//     ↑ Invoice INV-0003 · Delivered          (sent — 085)
//     ↑ Quote QUO-0007 · Bounced              (sent — 085)
//
//   The two lists load SEPARATELY and fail separately: if the sent list
//   cannot be loaded, the received emails still show, with a one-line
//   note that sent emails are missing (and the other way round). Neither
//   failure costs the page. LoadAsync's signature is unchanged, so the
//   three detail pages did not need touching.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.InboundEmails;
using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Admin.Web.Pages.Shared
{
    public sealed class RecordEmailsVm
    {
        /// <summary>Received emails (and ones a user BCC'd in).</summary>
        public List<InboundEmailListItemDto> Items { get; init; } = new();

        /// <summary>085. Quote and invoice emails we sent.</summary>
        public List<SentEmailListItemDto> SentItems { get; init; } = new();

        /// <summary>The received emails could not be loaded.</summary>
        public bool LoadFailed { get; init; }

        /// <summary>085. The sent emails could not be loaded.</summary>
        public bool SentLoadFailed { get; init; }

        /// <summary>085. Either list was cut at its limit.</summary>
        public bool HasMore { get; init; }

        /// <summary>The workspace address to BCC, or null when receiving is not set up.</summary>
        public string? WorkspaceAddress { get; init; }

        /// <summary>Tracked reply addresses go on quote emails.</summary>
        public bool ReceivingOn { get; init; }

        /// <summary>"lead", "deal", "contact" — for the empty-state sentence.</summary>
        public string RecordLabel { get; init; } = "record";

        /// <summary>The page's own tenant-aware date formatter.</summary>
        public Func<DateTime, string> FormatDateTime { get; init; } = d => d.ToString("dd MMM yyyy HH:mm");

        /// <summary>Both directions — the tab badge counts every email on the record.</summary>
        public int Count => Items.Count + SentItems.Count;

        /// <summary>Both lists failed: nothing at all to show.</summary>
        public bool AllFailed => LoadFailed && SentLoadFailed;

        /// <summary>085. Problem emails (bounced / spam / failed) — a red dot on the header.</summary>
        public int ProblemCount => SentItems.Count(s => s.IsProblem);

        /// <summary>085. One row of the merged timeline: exactly one of the two is set.</summary>
        public sealed record Row(DateTime AtUtc, InboundEmailListItemDto? In, SentEmailListItemDto? Out);

        /// <summary>085. Both lists, newest first.</summary>
        public IReadOnlyList<Row> Timeline =>
            Items.Select(i => new Row(i.ReceivedAtUtc, i, null))
                 .Concat(SentItems.Select(s => new Row(s.CreatedAtUtc, null, s)))
                 .OrderByDescending(r => r.AtUtc)
                 .ToList();

        /// <summary>
        /// Loads the section for one record. Never throws: a failure costs
        /// the section (LoadFailed / SentLoadFailed), not the page — like
        /// every other optional panel on these pages.
        /// </summary>
        public static async Task<RecordEmailsVm> LoadAsync(
            IInboundEmailService service,
            string entityType,
            Guid entityId,
            string recordLabel,
            Func<DateTime, string> formatDateTime,
            ILogger logger)
        {
            RecordEmailsDto? received = null;
            SentEmailsDto? sent = null;
            var receivedFailed = false;
            var sentFailed = false;

            try
            {
                received = await service.GetForRecordAsync(entityType, entityId);
            }
            catch (Exception ex)
            {
                receivedFailed = true;
                logger.LogWarning(ex, "Failed to load emails for {EntityType} {EntityId}", entityType, entityId);
            }

            try
            {
                sent = await service.GetSentForRecordAsync(entityType, entityId);
            }
            catch (Exception ex)
            {
                sentFailed = true;
                logger.LogWarning(ex, "Failed to load sent emails for {EntityType} {EntityId}", entityType, entityId);
            }

            var receivedItems = received?.Items ?? new();

            return new RecordEmailsVm
            {
                Items = receivedItems,
                SentItems = sent?.Items ?? new(),
                LoadFailed = receivedFailed,
                SentLoadFailed = sentFailed,
                HasMore = (sent?.HasMore ?? false) || receivedItems.Count >= 50,
                WorkspaceAddress = received?.WorkspaceAddress,
                ReceivingOn = received?.ReceivingOn ?? false,
                RecordLabel = recordLabel,
                FormatDateTime = formatDateTime
            };
        }
    }
}
