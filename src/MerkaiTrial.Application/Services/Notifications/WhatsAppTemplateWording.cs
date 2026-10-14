// =====================================================================
// WhatsAppTemplateWording.cs
// Location: MerkaiTrial.Application/Services/Notifications/WhatsAppTemplateWording.cs
//
// NEW FILE (086b). The wording to submit to Meta for each notification
// event, with sample values and a preview of what the person will read —
// shown on /Admin/WhatsAppTemplates so nobody has to remember the rules.
//
// THE SHAPE, for every event:
//
//     {fixed sentence naming the event} {{headline}} — {{detail}}. Sign in to your account to view it.
//
//   • The fixed sentence names the event, which is what gets the template
//     classified as Utility rather than Marketing (round 048).
//   • {{headline}} is the event's SUBJECT ("Quote QUO-0042") — NOT a
//     sentence saying what happened, because the fixed text already did
//     (086b; NotificationRequest.Headline).
//   • Text on both sides of the variables — Meta rejects a body that
//     starts or ends with one.
//
// These are the same fixed sentences round 048 put in the reference
// column, so a template already approved with that wording needs NO
// re-approval: only what Merkai puts in {{headline}} changed.
//
// Reference only. Nothing here is sent — Meta sends its own approved copy.
// =====================================================================

using MerkaiTrial.Domain.Entities;

namespace MerkaiTrial.Application.Services.Notifications;

/// <summary>What to type into WhatsApp Manager for one event.</summary>
public sealed record WhatsAppWording(
    string SuggestedName,
    string Body,
    string SampleHeadline,
    string SampleDetail,
    string? Note = null)
{
    /// <summary>The message as the person will read it, filled with the samples.</summary>
    public string Preview => Body
        .Replace("{{headline}}", SampleHeadline, StringComparison.Ordinal)
        .Replace("{{detail}}", SampleDetail, StringComparison.Ordinal);
}

public static class WhatsAppTemplateWording
{
    private const string Tail = " {{headline}} — {{detail}}. Sign in to your account to view it.";

    public static WhatsAppWording? For(NotificationEventType eventType) => eventType switch
    {
        NotificationEventType.LeadAssigned => new(
            "lead_assigned_v1",
            "A lead has been assigned to you in Merkai." + Tail,
            "Priya Shah", "Acme Ltd · INR 95,000.00"),

        NotificationEventType.DealAssigned => new(
            "deal_assigned_v1",
            "A deal has been assigned to you in Merkai." + Tail,
            "Acme office fit-out", "Proposal · THB 250,000.00"),

        NotificationEventType.DealStageChanged => new(
            "deal_stage_changed_v1",
            "A deal in your Merkai account has moved to a new stage." + Tail,
            "Acme office fit-out is now Negotiation", "From Proposal"),

        NotificationEventType.QuoteSent => new(
            "quote_sent_v1",
            "A quote has been sent from your Merkai account." + Tail,
            "Quote QUO-0042", "Acme office fit-out — THB 250,000.00"),

        NotificationEventType.QuoteAccepted => new(
            "quote_accepted_v1",
            "A quote in your Merkai account has been accepted." + Tail,
            "Quote QUO-0042", "Acme office fit-out — THB 250,000.00"),

        NotificationEventType.QuoteRejected => new(
            "quote_rejected_v1",
            "A quote in your Merkai account has been declined." + Tail,
            "Quote QUO-0042", "Acme office fit-out — THB 250,000.00"),

        NotificationEventType.QuoteApprovalRequested => new(
            "quote_approval_requested_v1",
            "A quote in your Merkai account needs your approval." + Tail,
            "Quote QUO-0042", "Somchai Wiriya is asking you to approve THB 250,000.00"),

        NotificationEventType.QuoteApprovalDecided => new(
            "quote_approval_decided_v1",
            "An approval request in your Merkai account has been decided." + Tail,
            "Quote QUO-0042 approved — ready to send", "Nattaporn Srisai approved it"),

        NotificationEventType.CustomerEmailReceived => new(
            "customer_email_received_v1",
            "A customer email has been filed on one of your records in Merkai." + Tail,
            "Email from priya@acme.com filed on your record", "Re: Quote QUO-0042",
            "Optional. Email already tells people about this; WhatsApp for every customer email can get noisy."),

        NotificationEventType.EmailBounced => new(
            "email_bounced_v1",
            "An email you sent from Merkai could not be delivered." + Tail,
            "Invoice INV-0003 to priya@acme.com bounced", "The recipient's mailbox does not exist",
            "Optional. Useful if reps rarely read email."),

        _ => null
    };
}
