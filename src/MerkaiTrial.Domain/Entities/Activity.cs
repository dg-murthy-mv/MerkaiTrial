// =====================================================================
// Activity.cs — Domain Entity
// Location: MerkaiTrial.Domain/Entities/Activity.cs
//
// Replaces: LeadActivity (Lead-specific)
// Covers:   Lead, Deal, Contact, Company activities + Tasks
// Migration: Existing LeadActivity rows can stay — this is additive.
//            New code uses Activity. Old LeadActivity still works.
// =====================================================================

namespace MerkaiTrial.Domain.Entities;

public class Activity
{
    public Guid      Id           { get; set; }
    public Guid      TenantId     { get; set; }

    // ── Entity link ───────────────────────────────────────────────────
    /// <summary>"Lead" | "Deal" | "Contact" | "Company"</summary>
    public string    EntityType   { get; set; } = string.Empty;
    public Guid      EntityId     { get; set; }

    // ── Activity / Task fields ────────────────────────────────────────
    /// <summary>"Call" | "Email" | "Meeting" | "SMS" | "WhatsApp" | "Task" | "Note" | "Demo"</summary>
    public string    ActivityType { get; set; } = string.Empty;
    public string    Subject      { get; set; } = string.Empty;
    public string?   Description  { get; set; }
    public int?      Duration     { get; set; }  // minutes (for calls/meetings)
    public DateTime  ActivityDate { get; set; }  // when it happened (UTC)

    // ── Task-specific ─────────────────────────────────────────────────
    /// <summary>true = future task to be done; false = completed activity log</summary>
    public bool      IsTask       { get; set; }
    public DateTime? DueDate      { get; set; }  // UTC — required when IsTask=true
    public bool      IsCompleted  { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string?   Outcome      { get; set; }  // result/notes after completion

    // ── Assignment ────────────────────────────────────────────────────
    public string?   AssignedToUserId { get; set; }

    // ── Audit ─────────────────────────────────────────────────────────
    public DateTime  CreatedAtUtc { get; set; }
    public string?   CreatedBy    { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public string?   UpdatedBy    { get; set; }
    public bool      IsDeleted    { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
}
