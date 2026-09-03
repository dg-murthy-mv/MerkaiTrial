using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MerkaiTrial.Domain.Entities
{
    public class LeadChannel
    {
        public Guid Id { get; set; }
        public Guid? TenantId { get; set; }   // NULL = system default
        public string Name { get; set; } = string.Empty;
        public bool IsActive { get; set; } = true;
        public int DisplayOrder { get; set; } = 999;
        public DateTime CreatedAtUtc { get; set; }
        public string? CreatedBy { get; set; }
        public bool IsDeleted { get; set; } = false;
    }
}
