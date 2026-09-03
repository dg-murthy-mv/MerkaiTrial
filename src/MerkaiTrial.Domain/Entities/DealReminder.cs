using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MerkaiTrial.Domain.Entities
{
    public class DealReminder
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public Guid DealId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? Description { get; set; }
        public DateTime ReminderDate { get; set; }
        public bool IsCompleted { get; set; }
        public DateTime? CompletedAtUtc { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public string? CreatedBy { get; set; }
        public bool IsDeleted { get; set; }

        // Navigation properties
        public Deal Deal { get; set; } = null!;
    }
}
