// Domain/Entities/Deal.cs
namespace MerkaiTrial.Domain.Entities
{
    public class Deal
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public Guid ContactId { get; set; }
        public Guid? LeadId { get; set; }
        public Guid? CompanyId { get; set; }

        // ── Basic Info ────────────────────────────────────────────────
        public string Title { get; set; } = string.Empty;
        public string? Description { get; set; }

        /// <summary>Stored as nvarchar. Valid values: New | Qualified | Proposal | Won | Lost</summary>
        public string Stage { get; set; } = "New";
        public int Probability { get; set; } = 10;
        public string Currency { get; set; } = string.Empty;

        // ── Industry / Vertical ────────────────────────────────────────
        /// <summary>FK → CompanyVerticals. Inherited from Lead on conversion.</summary>
        public Guid? VerticalId { get; set; }

        // ── Financial ─────────────────────────────────────────────────
        public decimal ExpectedValue { get; set; }
        public decimal? ActualValue { get; set; }

        // ── Dates ─────────────────────────────────────────────────────
        public DateTime ExpectedCloseDateUtc { get; set; }
        public DateTime? ActualCloseDateUtc { get; set; }

        // ── Relationships ─────────────────────────────────────────────
        public string? OwnerUserId { get; set; }
        public Guid? SourceId { get; set; }
        public string? Source { get; set; }

        // ── Closed deal ───────────────────────────────────────────────
        public string? LostReason { get; set; }

        // ── Tags & Custom ─────────────────────────────────────────────
        public string? Tags { get; set; }
        public string? CustomFields { get; set; }

        // ── Audit ─────────────────────────────────────────────────────
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public string? CreatedBy { get; set; }
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
        public string? UpdatedBy { get; set; }
        public bool IsDeleted { get; set; }
        public DateTime? DeletedAtUtc { get; set; }
        public string? DeletedBy { get; set; }

        // ── Navigation Properties ─────────────────────────────────────
        public Company? Company { get; set; }
        public Contact? Contact { get; set; }
        public Lead? Lead { get; set; }
        public CompanyVertical? Vertical { get; set; }
        public ICollection<DealActivity> Activities { get; set; } = new List<DealActivity>();
        public ICollection<DealNote> Notes { get; set; } = new List<DealNote>();
        public ICollection<DealReminder> Reminders { get; set; } = new List<DealReminder>();
        public ICollection<DealStageHistory> StageHistory { get; set; } = new List<DealStageHistory>();
    }
}
