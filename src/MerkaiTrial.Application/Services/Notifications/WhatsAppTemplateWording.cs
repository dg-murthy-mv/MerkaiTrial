// =====================================================================
// WhatsAppTemplateWording.cs
// Location: MerkaiTrial.Application/Services/Notifications/WhatsAppTemplateWording.cs
//
// The wording to submit to Meta for each notification event, with sample
// values and a preview of what the person will read. Shown on
// /Admin/WhatsAppTemplates, so nobody has to remember the rules.
//
// 087 — UPDATED TO THE WORDING META ACTUALLY APPROVED. The 086b shape
// ("A deal has been assigned to you in Merkai. {{headline}} — {{detail}}.
// Sign in to your account to view it.") was refused in WhatsApp Manager.
// What passed, for every event, is:
//
//     This is a notification from your Merkai CRM account. {event sentence}:
//     {{headline}} ({{detail}}). Sign in to view the details.
//
//   • The opening sentence says it is an account notification. Together
//     with the event sentence, that keeps the category Utility rather than
//     Marketing.
//   • {{headline}} is the event's SUBJECT ("Quote QUO-0042"), not a
//     sentence saying what happened; the event sentence already says that
//     (086b; NotificationRequest.Headline).
//   • There is text on both sides of the variables. Meta rejects a body
//     that starts or ends with one.
//   • The SAMPLES carry no prices. Amounts in the sample values were what
//     triggered "Category does not match" (Marketing). What Merkai really
//     sends in {{detail}} can still contain an amount; Meta judges the
//     template, not each message.
//
// Templates already approved with the wording you typed need nothing: the
// only thing that has to match exactly is the template NAME in Merkai.
//
// Reference only. Nothing here is sent; Meta sends its own approved copy.
//
// CHANGES (097)
//   • Task assigned (event 12, round 091) has its wording: task_assigned_v1.
//     It was the one event with nothing to submit.
//   • Optional is a field now, not a sentence in Note. The template page
//     lists what is still to submit to Meta and leaves the optional two
//     out of that list.
//   • The stage-changed and approval-decided samples now match what Merkai
//     really sends (097's shorter headlines).
// =====================================================================

using MerkaiTrial.Domain.Entities;

namespace MerkaiTrial.Application.Services.Notifications;

/// <summary>What to type into WhatsApp Manager for one event.</summary>
public sealed record WhatsAppWording(
    string SuggestedName,
    string Body,
    string SampleHeadline,
    string SampleDetail,
    string? Note = null,
    bool Optional = false)                          // 097
{
    /// <summary>The message as the person will read it, filled with the samples.</summary>
    public string Preview => Body
        .Replace("{{headline}}", SampleHeadline, StringComparison.Ordinal)
        .Replace("{{detail}}", SampleDetail, StringComparison.Ordinal);
}

public static class WhatsAppTemplateWording
{
    private const string Opening = "This is a notification from your Merkai CRM account. ";
    private const string Tail = ": {{headline}} ({{detail}}). Sign in to view the details.";

    /// <summary>The approved shape around one event sentence.</summary>
    public static string BodyFor(string eventSentence) => Opening + eventSentence + Tail;

    public static WhatsAppWording? For(NotificationEventType eventType) => eventType switch
    {
        NotificationEventType.LeadAssigned => new(
            "lead_assigned_v1",
            BodyFor("A lead has been assigned to you"),
            "Priya Shah", "Acme Ltd"),

        NotificationEventType.DealAssigned => new(
            "deal_assigned_v1",
            BodyFor("A deal has been assigned to you"),
            "Acme office fit-out", "Proposal stage"),

        NotificationEventType.DealStageChanged => new(
            "deal_stage_changed_v1",
            BodyFor("A deal has moved to a new stage"),
            "Acme office fit-out", "now Negotiation, from Proposal"),

        NotificationEventType.QuoteSent => new(
            "quote_sent_v1",
            BodyFor("A quote has been sent to a customer"),
            "Quote QUO-0042", "Acme office fit-out"),

        NotificationEventType.QuoteAccepted => new(
            "quote_accepted_v1",
            BodyFor("A customer has accepted a quote"),
            "Quote QUO-0042", "Acme office fit-out"),

        NotificationEventType.QuoteRejected => new(
            "quote_rejected_v1",
            BodyFor("A customer has declined a quote"),
            "Quote QUO-0042", "Acme office fit-out"),

        NotificationEventType.QuoteApprovalRequested => new(
            "quote_approval_requested_v1",
            BodyFor("A quote is waiting for your approval"),
            "Quote QUO-0042", "requested by Somchai Wiriya"),

        NotificationEventType.QuoteApprovalDecided => new(
            "quote_approval_decided_v1",
            BodyFor("Your approval request has been decided"),
            "Quote QUO-0042 approved", "Nattaporn Srisai approved it"),

        // 097 — round 091's event. Samples carry a date, not a price.
        NotificationEventType.TaskAssigned => new(
            "task_assigned_v1",
            BodyFor("A task has been assigned to you"),
            "Call the site engineer", "Due 22 Oct 2026, 11:00 · Lead: Priya Shah"),

        NotificationEventType.CustomerEmailReceived => new(
            "customer_email_received_v1",
            BodyFor("A customer email has been filed on one of your records"),
            "Email from priya@acme.com", "Re: Quote QUO-0042",
            "Optional. Email already tells people about this; a WhatsApp for every customer email can get noisy.",
            Optional: true),

        NotificationEventType.EmailBounced => new(
            "email_bounced_v1",
            BodyFor("An email you sent could not be delivered"),
            "Invoice INV-0003 to priya@acme.com", "the mailbox does not exist",
            "Optional. Useful if reps rarely read email.",
            Optional: true),

        _ => null
    };
}
