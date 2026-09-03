using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MerkaiTrial.Domain.Entities
{
    public class UserRole
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid UserId { get; set; }
        public Guid RoleId { get; set; }
        public DateTime AssignedAtUtc { get; set; } = DateTime.UtcNow;
        public string? AssignedBy { get; set; }

        // Navigation properties
        public User? User { get; set; }
        public Role? Role { get; set; }
    }
}
