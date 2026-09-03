using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MerkaiTrial.Domain.Entities
{
    public class TenantSettings
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid TenantId { get; set; }
        public int MaxUsers { get; set; } = 5;
        public int MaxLeads { get; set; } = 100;
        public int MaxDeals { get; set; } = 50;
        public long StorageLimit { get; set; } = 1073741824; // 1GB in bytes
        public string? FeatureFlags { get; set; } // JSON string
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
        public string? UpdatedBy { get; set; }

        // Navigation properties
        public Tenant? Tenant { get; set; }
    }
}
