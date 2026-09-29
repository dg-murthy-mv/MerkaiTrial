// =====================================================================
// WhatsAppTemplate.cs
// Location: MerkaiTrial.Domain/Entities/WhatsAppTemplate.cs
//
// NEW FILE (044).
//
// WHY A TABLE AND NOT A CONST
//
//   A WhatsApp message a business sends first — which is all of ours —
//   cannot be free text. It must name a template Meta has already
//   approved, and Meta's approval is per template NAME and per LANGUAGE.
//   Change the wording and it is a new approval; add a variable and it is
//   a new approval.
//
//   That review sits outside our deployment cycle. Someone edits a
//   template in Meta's Business Manager on a Tuesday, it is approved on a
//   Wednesday, and the app has to use the new name without waiting for a
//   release. A constant in C# would mean a deploy every time Meta says no
//   — and Meta says no fairly often, usually about wording.
//
//   So: the eight notification events map to template names in a table an
//   admin can edit. 044 seeds the rows and gives them a settings screen;
//   045 adds the sender that reads them.
//
// NO TenantId, ON PURPOSE
//
//   Phase 1 sends from MadeeVision's own WhatsApp number to the CRM's own
//   users — "a lead was assigned to you". Those are our messages about
//   our product, so the templates are ours and there is one set.
//
//   Phase 2 is different in kind: a tenant messaging THEIR customer about
//   a quote, from THEIR brand, under their own WhatsApp Business Account
//   connected through Meta's Embedded Signup. Those templates belong to
//   the tenant, are approved against the tenant's account, and carry the
//   tenant's own name — a different table, not a nullable column on this
//   one. Adding TenantId here now would mean a global query filter and a
//   "null means ours" rule on every read, to hold rows that will never
//   arrive.
//
// THE CATEGORY IS THE PRICE
//
//   Meta classifies each template as utility, authentication or
//   marketing, and charges accordingly — marketing costs several times
//   what utility does, and the gap is widest in exactly our markets.
//   Meta decides the category from the CONTENT, not from what we claim,
//   and it can re-categorise an existing template. "Your quote QUO-0012
//   was accepted" is utility. "Here's what's new in Merkai this month" is
//   marketing, however it is dressed up.
//
//   Category is stored so the settings page can show what a channel
//   actually costs before an admin switches it on for forty people.
// =====================================================================

namespace MerkaiTrial.Domain.Entities
{
    public class WhatsAppTemplate
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>Which notification this template is the WhatsApp form of.</summary>
        public NotificationEventType EventType { get; set; }

        /// <summary>
        /// Meta's language code for the approved template — "en", "en_US",
        /// "hi", "th". Part of the identity: the same name in two languages
        /// is two approvals, and asking for one Meta has not approved is a
        /// rejected send, not a fallback.
        ///
        /// 044 seeds "en" for all eight. Everyone in the four markets does
        /// business in English, and a half-translated notification is worse
        /// than an English one.
        /// </summary>
        public string LanguageCode { get; set; } = "en";

        /// <summary>
        /// The template name exactly as registered with Meta —
        /// "lead_assigned_v1". Lower case, digits and underscores only;
        /// Meta rejects anything else.
        ///
        /// The _v1 suffix is a habit worth keeping: wording changes need a
        /// new approval anyway, and a new name lets the old one keep
        /// working while the new one is in review.
        /// </summary>
        public string TemplateName { get; set; } = string.Empty;

        /// <summary>
        /// "utility", "authentication" or "marketing", as Meta categorised
        /// it. Stored for the cost warning on the settings page, not used
        /// when sending.
        /// </summary>
        public string Category { get; set; } = "utility";

        /// <summary>
        /// The approved body text, with Meta's {{1}} {{2}} placeholders, as
        /// a reminder of what was approved. Never sent — Meta renders the
        /// real thing from its own copy. This is here so an admin can read
        /// the row and know what it says without opening Business Manager.
        /// </summary>
        public string? BodyPreview { get; set; }

        /// <summary>
        /// How many {{n}} placeholders the approved template has.
        ///
        /// The sender builds the variable list in code and compares it to
        /// this before calling Meta. A mismatch means somebody edited the
        /// template without telling the app, and failing here names that
        /// plainly — Meta's own error for a wrong variable count is a
        /// numeric code that explains nothing.
        /// </summary>
        public int VariableCount { get; set; }

        /// <summary>
        /// What each placeholder holds, in order, for whoever is reading
        /// this row later: ["actor name","lead name","value"]. Stored as
        /// JSON. Documentation, not logic.
        /// </summary>
        public string? VariableHints { get; set; }

        /// <summary>
        /// False while a template is waiting for approval, or after Meta
        /// has rejected or paused it. An inactive template means the event
        /// simply does not go out on WhatsApp — the bell and the email are
        /// unaffected, and the send log records the skip rather than a
        /// failure, because nothing failed.
        /// </summary>
        public bool IsActive { get; set; }

        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
        public string? UpdatedBy { get; set; }

        /// <summary>Ready to send with: named, approved and switched on.</summary>
        public bool IsUsable =>
            IsActive
            && !string.IsNullOrWhiteSpace(TemplateName)
            && !string.IsNullOrWhiteSpace(LanguageCode);
    }
}
