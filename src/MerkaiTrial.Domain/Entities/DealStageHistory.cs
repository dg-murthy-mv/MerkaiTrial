// =====================================================================
// DealStageHistory.cs
// Location: MerkaiTrial.Domain/Entities/DealStageHistory.cs
// Tracks every stage transition on a Deal — enables funnel reports,
// time-in-stage analytics, velocity metrics.
// =====================================================================

namespace MerkaiTrial.Domain.Entities;

public class DealStageHistory
{
    public Guid   Id           { get; set; } = Guid.NewGuid();
    public Guid   TenantId     { get; set; }
    public Guid   DealId       { get; set; }

    public string FromStage    { get; set; } = string.Empty;  // New|Qualified|Proposal|Won|Lost
    public string ToStage      { get; set; } = string.Empty;  // New|Qualified|Proposal|Won|Lost

    public DateTime ChangedAtUtc { get; set; } = DateTime.UtcNow;
    public string?  ChangedBy    { get; set; }

    /// <summary>Optional reason — required when moving to Lost</summary>
    public string? Note { get; set; }

    // Add this navigation property to fix CS1061
    public Deal Deal { get; set; }
}
