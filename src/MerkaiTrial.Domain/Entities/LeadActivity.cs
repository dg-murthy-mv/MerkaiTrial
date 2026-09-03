using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MerkaiTrial.Domain.Entities
{
    public class LeadActivity
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid LeadId { get; set; }
        public Guid TenantId { get; set; }
        public string ActivityType { get; set; } = "Call"; // Call, Email, Meeting, SMS, WhatsApp
        public string? Subject { get; set; }
        public string? Description { get; set; }
        public int? Duration { get; set; } // In minutes
        public DateTime ActivityDate { get; set; } = DateTime.UtcNow;
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public string? CreatedBy { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }
        public string? UpdatedBy { get; set; }
        public bool IsDeleted { get; set; }

        // Navigation
        public Lead? Lead { get; set; }
    }
}
