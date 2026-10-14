// =====================================================================
// SentEmailsCardComponents.cs
// Location: MerkaiTrial.Admin.Web/Pages/Shared/SentEmailsCardComponents.cs
//
// NEW FILE (085). The model behind _SentEmailsCard.cshtml — "Email
// history" on Quotes/Detail and Invoices/Detail. One component for both,
// so the two pages say the same thing the same way.
//
// WHAT IT ANSWERS
//   "Did the customer get it?" Each email the document was sent in, with
//   who it went to, who sent it, and what happened to it: Queued → Sent →
//   Delivered, or Bounced / Marked as spam / Failed with the reason.
//
// SEND AGAIN
//   A failed email (never left — Failed / Dead / Cancelled) gets a "Send
//   again" button. It posts to /Emails/Sent/{id}?handler=Retry with this
//   page as the return address, so the retry rule and its permission
//   check live in ONE place (the API, then that page) instead of being
//   copied into every page that shows the card. The button is only drawn
//   when the page's own permission allows it (CanRetry); the API checks
//   again regardless.
//
// Never throws: a failure costs the card (LoadFailed), not the page.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Emails;
using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Admin.Web.Pages.Shared
{
    public sealed class SentEmailsCardVm
    {
        public List<SentEmailListItemDto> Items { get; init; } = new();
        public bool LoadFailed { get; init; }
        public bool HasMore { get; init; }

        /// <summary>"quote" or "invoice" — for the sentences.</summary>
        public string DocumentLabel { get; init; } = "document";

        /// <summary>The page allows sending this document (quotes.update / invoices.update).</summary>
        public bool CanRetry { get; init; }

        /// <summary>Where "Send again" returns to — this page's own URL.</summary>
        public string ReturnUrl { get; init; } = "/";

        /// <summary>The page's own tenant-aware date formatter.</summary>
        public Func<DateTime, string> FormatDateTime { get; init; } = d => d.ToString("dd MMM yyyy HH:mm");

        /// <summary>Optional sentence for the empty state ("Use Email invoice above…").</summary>
        public string? EmptyHint { get; init; }

        public int Count => Items.Count;
        public int ProblemCount => Items.Count(i => i.IsProblem);

        /// <summary>The newest email's state — the one-word answer for the header.</summary>
        public SentEmailListItemDto? Latest => Items.FirstOrDefault();

        /// <summary>Failed / Dead / Cancelled — the states "Send again" applies to.</summary>
        public static bool IsRetryable(SentEmailListItemDto e) => e.Status is "Failed" or "Dead" or "Cancelled";

        public static async Task<SentEmailsCardVm> LoadAsync(
            ISentEmailService service,
            string entityType,
            Guid entityId,
            string documentLabel,
            bool canRetry,
            string returnUrl,
            Func<DateTime, string> formatDateTime,
            ILogger logger,
            string? emptyHint = null)
        {
            try
            {
                var result = await service.GetForEntityAsync(entityType, entityId);
                return new SentEmailsCardVm
                {
                    Items = result.Items ?? new(),
                    HasMore = result.HasMore,
                    DocumentLabel = documentLabel,
                    CanRetry = canRetry,
                    ReturnUrl = returnUrl,
                    FormatDateTime = formatDateTime,
                    EmptyHint = emptyHint
                };
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load email history for {EntityType} {EntityId}", entityType, entityId);
                return new SentEmailsCardVm
                {
                    LoadFailed = true,
                    DocumentLabel = documentLabel,
                    CanRetry = canRetry,
                    ReturnUrl = returnUrl,
                    FormatDateTime = formatDateTime,
                    EmptyHint = emptyHint
                };
            }
        }
    }
}
