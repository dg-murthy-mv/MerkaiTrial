using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MerkaiTrial.Domain.Entities
{
    public class DealActivity
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public Guid DealId { get; set; }
        public string ActivityType { get; set; } = string.Empty;
        public string? Subject { get; set; }
        public string? Description { get; set; }
        public DateTime ActivityDate { get; set; }
        public int? Duration { get; set; }
        public string? Outcome { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public string? CreatedBy { get; set; }
        public bool IsDeleted { get; set; }

        // Navigation properties
        public Deal Deal { get; set; } = null!;
    }
}
