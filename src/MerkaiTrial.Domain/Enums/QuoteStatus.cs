// =====================================================================
// QuoteStatus.cs
// Location: MerkaiTrial.Domain/Enums/QuoteStatus.cs
//
// CHANGES (017 — quote approvals)
//   ✅ PendingApproval = 7 and Approved = 8 added AT THE END. Existing
//      values keep their numbers, so rows already stored are unaffected.
//
// THE FLOW
//   Draft ──(no rule triggered)──────────────────────────► Sent
//   Draft ──Submit──► PendingApproval ──Approve──► Approved ──► Sent
//                          │
//                          ├─Request changes──► Draft (with the comment)
//                          └─Recall───────────► Draft
//   Sent/Viewed ──► Accepted / Rejected / Expired
//   Rejected/Expired ──► Revised ──► (same as Draft: send, or submit)
//
//   Editing an Approved quote sends it back to Draft — what was approved
//   is no longer what would be sent.
// =====================================================================

namespace MerkaiTrial.Domain.Enums
{
    public enum QuoteStatus
    {
        Draft = 0,            // Quote created but not sent
        Sent = 1,             // Quote sent to customer
        Viewed = 2,           // Customer viewed the quote
        Accepted = 3,         // Customer accepted - ready for invoice
        Rejected = 4,         // Customer rejected
        Expired = 5,          // Quote expired
        Revised = 6,          // Quote revised (new version created)
        PendingApproval = 7,  // Waiting for a manager / admin to approve
        Approved = 8          // Approved internally — ready to send
    }
}
