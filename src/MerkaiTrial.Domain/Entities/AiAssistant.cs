// =====================================================================
// FILE: MerkaiTrial.Domain/Entities/AiAssistant.cs
//
// NEW (122). The AI assistant's two tables.
//
//   AiSetting  one row per workspace: whether its admin has turned the
//              assistant on (it starts OFF — the admin decides whether the
//              workspace's questions may be sent to the AI provider), and
//              an optional monthly question limit MadeeVision can set
//              (null = the plan's limit from configuration).
//   AiUsage    one row per question: who asked, when, how it went, and the
//              provider's token counts — what the monthly limit counts and
//              what MadeeVision is billed for. The QUESTION itself is not
//              kept here; it is in the workspace's Activity Log (AiQuestion).
// Created by Sql/122_AiAssistant.sql.
//
// 123: AiSetting.CanReadRecords — the admin's second switch: may the
// assistant look at the workspace's records (always only what the asking
// person can see)? Starts OFF. Added by Sql/123_AiRecordAccess.sql.
//
// 125: AiProposal — something the assistant PREPARED (a task, a logged
// activity, a note) that is done only when the person who asked clicks
// Confirm. One row per proposal; used once; expires after 30 minutes.
// Created by Sql/125_AiProposals.sql.
// =====================================================================

namespace MerkaiTrial.Domain.Entities
{
    public class AiSetting
    {
        public Guid TenantId { get; set; }
        public bool IsOn { get; set; }
        /// <summary>123. The assistant may look at records — only ones the asking person can see. Starts off.</summary>
        public bool CanReadRecords { get; set; }
        /// <summary>Questions a month; null = the plan's limit (Ai:MonthlyQuestions).</summary>
        public int? MonthlyLimit { get; set; }
        public DateTime? ChangedAtUtc { get; set; }
        public string? ChangedBy { get; set; }
    }

    public static class AiOutcome
    {
        public const string Answered = "answered";
        public const string Failed = "failed";       // the provider did not answer; not counted
        public const string Limit = "limit";         // refused: the month's questions are used up
    }

    public class AiUsage
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid TenantId { get; set; }
        public Guid UserId { get; set; }
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public string Provider { get; set; } = string.Empty;
        public string? Model { get; set; }
        public string Outcome { get; set; } = AiOutcome.Answered;
        public int? InputTokens { get; set; }
        public int? OutputTokens { get; set; }
        public int DurationMs { get; set; }
    }

    public static class AiProposalStatus
    {
        public const string Pending = "pending";
        public const string Done = "done";
        public const string Cancelled = "cancelled";
        public const string Failed = "failed";
    }

    /// <summary>125. A change the assistant prepared; nothing happens until the person confirms it.</summary>
    public class AiProposal
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid TenantId { get; set; }
        /// <summary>Only this person can confirm or cancel it.</summary>
        public Guid UserId { get; set; }
        /// <summary>task | log | note</summary>
        public string Kind { get; set; } = string.Empty;
        /// <summary>Lead | Deal</summary>
        public string RecordType { get; set; } = string.Empty;
        public Guid RecordId { get; set; }
        /// <summary>The checked details, as JSON (what Confirm will do).</summary>
        public string PayloadJson { get; set; } = "{}";
        /// <summary>The sentence the person confirms, written by Merkai from the checked details.</summary>
        public string Summary { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime ExpiresAtUtc { get; set; } = DateTime.UtcNow.AddMinutes(30);
        public string Status { get; set; } = AiProposalStatus.Pending;
        public DateTime? DecidedAtUtc { get; set; }
        /// <summary>The task, activity or note that Confirm created.</summary>
        public Guid? ResultId { get; set; }
    }
}
