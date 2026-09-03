using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MerkaiTrial.Domain.Entities
{
    public class DealNote
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public Guid DealId { get; set; }
        public string Note { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; }
        public string? CreatedBy { get; set; }
        public bool IsDeleted { get; set; }

        // Navigation properties
        public Deal Deal { get; set; } = null!;
    }
}
