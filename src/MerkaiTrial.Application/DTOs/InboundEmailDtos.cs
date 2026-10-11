// =====================================================================
// InboundEmailDtos.cs
// Location: MerkaiTrial.Application/DTOs/InboundEmailDtos.cs
//
// NEW FILE (083). What the API returns about received email, and what the
// pages send back.
//
// THE BODY ONLY TRAVELS ON THE DETAIL CALL. Lists carry the snippet. A
// list of 25 emails with their full HTML would be megabytes for a page
// that shows two lines of each.
// =====================================================================

namespace MerkaiTrial.Application.DTOs
{
    /// <summary>One email in a list — the Inbox, or a record's Emails section.</summary>
    public record InboundEmailListItemDto(
        Guid Id,
        string Direction,          // "Received" | "SentByUser" | "Forwarded"
        string Status,             // "Unmatched" | "Matched" | "Dismissed"
        string FromAddress,
        string? FromName,
        string? CounterpartAddress,
        string? ToAddresses,
        string Subject,
        string? Snippet,
        DateTime ReceivedAtUtc,
        int AttachmentCount,
        bool IsAutoReply,
        bool? SenderVerified,
        string ProcessingState,    // "Pending" | "Processing" | "Done" | "Retry" | "Failed"
        string? ForwardedByName,
        string? MatchNote)
    {
        /// <summary>"Priya Shah" or "priya@acme.com".</summary>
        public string FromDisplay => string.IsNullOrWhiteSpace(FromName) ? FromAddress : FromName!;

        /// <summary>The person the conversation is WITH — the customer.</summary>
        public string WithDisplay => Direction switch
        {
            "SentByUser" => CounterpartAddress ?? ToAddresses ?? "—",
            "Forwarded"  => CounterpartAddress ?? FromDisplay,
            _            => FromDisplay
        };

        public bool IsStillProcessing => ProcessingState is "Pending" or "Processing" or "Retry";

        public string DirectionIcon => Direction switch
        {
            "SentByUser" => "bi-send",
            "Forwarded"  => "bi-forward",
            _            => "bi-envelope-open"
        };

        public string DirectionLabel => Direction switch
        {
            "SentByUser" => "Sent",
            "Forwarded"  => "Forwarded",
            _            => "Received"
        };
    }

    public record InboundEmailPageDto(
        List<InboundEmailListItemDto> Items,
        int TotalCount,
        int UnmatchedCount,
        int DismissedCount,
        bool HasMore);

    public record InboundEmailAttachmentDto(
        Guid Id,
        string FileName,
        string DownloadUrl,
        long FileSize,
        string? MimeType);

    /// <summary>A record the email is filed on, for the chips on the email page.</summary>
    public record InboundEmailLinkDto(
        string EntityType,         // "Lead" | "Deal" | "Contact" | "Company"
        Guid EntityId,
        string Name,
        bool CanOpen);

    public record InboundEmailDetailDto(
        Guid Id,
        string Direction,
        string Status,
        string MatchMethod,        // "None" | "ReplyToken" | "Thread" | "Address" | "Manual"
        string FromAddress,
        string? FromName,
        string? ToAddresses,
        string? CcAddresses,
        string? CounterpartAddress,
        string Subject,
        string? BodyText,
        string? BodyHtml,
        DateTime ReceivedAtUtc,
        bool IsAutoReply,
        bool? SenderVerified,
        string? AuthSummary,
        string? MatchNote,
        string? AttachmentNote,
        string ProcessingState,
        string? ProcessingError,
        string? ForwardedByName,
        string? LinkedBy,
        DateTime? LinkedAtUtc,
        List<InboundEmailLinkDto> Links,
        List<InboundEmailAttachmentDto> Attachments,

        // What the CALLER may do — decided by the API from their
        // permissions, so the page cannot offer a button the API refuses.
        bool CanLink,
        bool CanDismiss,
        bool CanDelete)
    {
        public string FromDisplay => string.IsNullOrWhiteSpace(FromName) ? FromAddress : FromName!;
        public bool IsStillProcessing => ProcessingState is "Pending" or "Processing" or "Retry";
    }

    /// <summary>A record's Emails section: its emails and the workspace address to BCC.</summary>
    public record RecordEmailsDto(
        List<InboundEmailListItemDto> Items,
        string? WorkspaceAddress,
        bool ReceivingOn);

    public record LinkInboundEmailDto(string EntityType, Guid EntityId);

    /// <summary>One search result in "Link to a record".</summary>
    public record InboundLinkTargetDto(
        string EntityType,         // "Lead" | "Deal" | "Contact"
        Guid EntityId,
        string Name,
        string? Detail,            // email / company / stage — whatever tells two Priyas apart
        bool EmailMatches);        // has the same address as the email's customer

    public record InboundLinkTargetsDto(List<InboundLinkTargetDto> Items);

    /// <summary>Settings → Inbound Email.</summary>
    public record InboundEmailSettingsDto(
        string? WorkspaceAddress,
        string? Alias,
        string? Domain,
        bool DomainConfigured,
        bool WebhookSecretConfigured,
        bool ApiKeyConfigured,
        bool ReplyTrackingOn,
        DateTime? LastReceivedAtUtc,
        int ReceivedLast30Days,
        int UnmatchedCount,
        int WaitingCount,
        int FailedCount)
    {
        public bool FullyReady => DomainConfigured && WebhookSecretConfigured && ApiKeyConfigured;
    }

    public record InboundCountDto(int Unmatched);
}
