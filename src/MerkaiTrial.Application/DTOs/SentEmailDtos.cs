// =====================================================================
// SentEmailDtos.cs
// Location: MerkaiTrial.Application/DTOs/SentEmailDtos.cs
//
// NEW FILE (085). Email WE sent, as the pages see it — the twin of
// InboundEmailDtos.cs for mail that came in. Plus the shapes for emailing
// an invoice and for the customer's invoice page.
//
// ONE STATE FOR PEOPLE. Two columns decide what happened to an email:
// Status (did Resend take it?) and DeliveryStatus (what did the
// recipient's server do?). Nobody wants to read both, so State folds
// them into the one word a rep needs: Queued, Sending, Sent, Delivered,
// Delayed, Bounced, Spam, Failed, Cancelled. Written once, here, and used
// by every page.
// =====================================================================

namespace MerkaiTrial.Application.DTOs
{
    /// <summary>One email we sent, in a list.</summary>
    public record SentEmailListItemDto(
        Guid Id,
        string Subject,
        string ToAddress,
        string? ToName,
        string? CcAddresses,
        string Status,              // OutboundStatus
        string DeliveryStatus,      // OutboundDeliveryStatus
        string? DeliveryDetail,
        string? LastError,
        int AttemptCount,
        DateTime CreatedAtUtc,
        DateTime? SentAtUtc,
        DateTime? DeliveryStatusAtUtc,
        string? EntityType,
        Guid? EntityId,
        string? EntityLabel,        // "Invoice INV-0003"
        string? SentByName,
        int AttachmentCount)
    {
        /// <summary>The one word — see the header.</summary>
        public string State => SentEmailState.Of(Status, DeliveryStatus);

        public string StateLabel => SentEmailState.Label(State);
        public string StateBadge => SentEmailState.Badge(State);
        public string StateIcon => SentEmailState.Icon(State);
        public string StateHint => SentEmailState.Hint(State, DeliveryDetail, LastError);

        public bool IsProblem => State is "Bounced" or "Spam" or "Failed" or "DeliveryFailed";

        /// <summary>When the latest thing happened to it.</summary>
        public DateTime LastEventAtUtc => DeliveryStatusAtUtc ?? SentAtUtc ?? CreatedAtUtc;

        public string ToDisplay => string.IsNullOrWhiteSpace(ToName) || ToName == ToAddress
            ? ToAddress
            : $"{ToName} <{ToAddress}>";
    }

    /// <summary>One email we sent, in full, for the detail page.</summary>
    public record SentEmailDetailDto(
        SentEmailListItemDto Summary,
        string? ReplyToAddress,
        string EventType,
        string Channel,

        /// <summary>
        /// Only for an email to a CUSTOMER about a record the caller may
        /// see. Null for our own notification emails — the log's long-
        /// standing rule (038): a log page is not where colleagues read
        /// each other's mail.
        /// </summary>
        string? BodyHtml,
        string? BodyText,
        List<SentEmailAttachmentDto> Attachments,

        /// <summary>Decided by the API from the caller's permissions.</summary>
        bool CanRetry,
        string? RetryBlockedReason);

    public record SentEmailAttachmentDto(Guid Id, string FileName, long SizeBytes);

    /// <summary>A record's sent emails.</summary>
    public record SentEmailsDto(List<SentEmailListItemDto> Items, bool HasMore);

    // ── Emailing an invoice ───────────────────────────────────────────

    /// <summary>What the "Email invoice" dialog starts from.</summary>
    public record InvoiceEmailPrefillDto(
        bool CanSend,
        string? BlockedReason,
        string? ToAddress,
        string? ToName,
        string? ContactName,
        string Subject,
        string? CompanyName,
        DateTime? LastSentAtUtc,
        string? LastSentTo,
        bool ReceivingOn);

    public record SendInvoiceEmailDto(
        string ToAddress,
        string? ToName,
        string? CcAddresses,        // comma / semicolon separated, up to five
        string? Message,
        string? BaseUrl);           // this host's public address, for the link

    public record SendInvoiceEmailResultDto(Guid MessageId, string To, int CcCount, string PublicUrl);

    // ── The customer's invoice page (/i/{token}) ──────────────────────

    /// <summary>
    /// Exactly what the anonymous page prints — and nothing else. No ids
    /// beyond the invoice's own, no notes from the workspace's other
    /// records, no payment details beyond the totals.
    /// </summary>
    public record PublicInvoiceDto(
        InvoiceDto Invoice,
        QuoteSellerDto? Seller,
        bool HasPdf,
        DateTime? PdfSentAtUtc,
        string? PdfFileName);

    /// <summary>The one place the folding rule lives.</summary>
    public static class SentEmailState
    {
        public static string Of(string status, string delivery)
        {
            if (status == "Sent")
            {
                return delivery switch
                {
                    "Delivered"  => "Delivered",
                    "Delayed"    => "Delayed",
                    "Bounced"    => "Bounced",
                    "Complained" => "Spam",
                    "Failed"     => "DeliveryFailed",
                    _            => "Sent"
                };
            }

            return status switch
            {
                "Pending"   => "Queued",
                "Sending"   => "Sending",
                "Failed"    => "Retrying",
                "Dead"      => "Failed",
                "Cancelled" => "Cancelled",
                _           => status
            };
        }

        public static string Label(string state) => state switch
        {
            "DeliveryFailed" => "Not delivered",
            "Spam"           => "Marked as spam",
            "Retrying"       => "Retrying",
            _                => state
        };

        public static string Badge(string state) => state switch
        {
            "Delivered"      => "bg-success-subtle text-success-emphasis border border-success-subtle",
            "Sent"           => "bg-primary-subtle text-primary-emphasis border border-primary-subtle",
            "Queued"         => "bg-secondary-subtle text-secondary-emphasis border border-secondary-subtle",
            "Sending"        => "bg-info-subtle text-info-emphasis border border-info-subtle",
            "Delayed"        => "bg-warning-subtle text-warning-emphasis border border-warning-subtle",
            "Retrying"       => "bg-warning-subtle text-warning-emphasis border border-warning-subtle",
            "Bounced"        => "bg-danger-subtle text-danger-emphasis border border-danger-subtle",
            "Spam"           => "bg-danger-subtle text-danger-emphasis border border-danger-subtle",
            "DeliveryFailed" => "bg-danger-subtle text-danger-emphasis border border-danger-subtle",
            "Failed"         => "bg-danger-subtle text-danger-emphasis border border-danger-subtle",
            _                => "bg-light text-muted border"
        };

        public static string Icon(string state) => state switch
        {
            "Delivered"                              => "bi-check2-all",
            "Sent"                                   => "bi-check2",
            "Queued" or "Sending"                    => "bi-hourglass-split",
            "Delayed" or "Retrying"                  => "bi-clock-history",
            "Bounced" or "DeliveryFailed" or "Failed" => "bi-envelope-x",
            "Spam"                                   => "bi-exclamation-octagon",
            _                                        => "bi-dash-circle"
        };

        public static string Hint(string state, string? deliveryDetail, string? lastError) => state switch
        {
            "Delivered"      => "The recipient's mail server accepted it.",
            "Sent"           => "Handed to the email provider. Waiting to hear that it was delivered.",
            "Queued"         => "Waiting to be sent — usually a few seconds.",
            "Sending"        => "Being sent right now.",
            "Delayed"        => deliveryDetail ?? "Delivery is delayed — the provider is still trying.",
            "Retrying"       => string.IsNullOrWhiteSpace(lastError) ? "The last try failed; it will be tried again automatically." : $"The last try failed ({lastError}); it will be tried again automatically.",
            "Bounced"        => deliveryDetail ?? "The recipient's server refused it.",
            "Spam"           => deliveryDetail ?? "The recipient marked it as spam.",
            "DeliveryFailed" => deliveryDetail ?? "The email provider could not deliver it.",
            "Failed"         => string.IsNullOrWhiteSpace(lastError) ? "It could not be sent after several tries." : $"It could not be sent after several tries: {lastError}",
            "Cancelled"      => "Cancelled before it was sent.",
            _                => state
        };
    }
}
