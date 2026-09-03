using System;
using System.ComponentModel.DataAnnotations;

namespace MerkaiTrial.Domain.Entities
{
    public class Journey
    {
        public Guid Id { get; set; }

        [Required]
        public Guid TenantId { get; set; }

        [Required, MaxLength(100)]
        public string Name { get; set; } = string.Empty;

        // JSON like: [{"delay":"P2D","action":"LINE","template":"th_followup"}]
        [Required]
        public string StepsJson { get; set; } = "[]";

        public DateTime CreatedUtc { get; set; }

        [MaxLength(64)]
        public string? CreatedBy { get; set; }

        public DateTime? UpdatedAtUtc { get; set; }

        [MaxLength(64)]
        public string? UpdatedBy { get; set; }

        public bool IsDeleted { get; set; }
    }
}
