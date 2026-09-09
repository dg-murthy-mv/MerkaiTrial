using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MerkaiTrial.Domain.Entities
{
    public class RoleTemplate
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>Canonical key: sales_manager, sales_rep, viewer.</summary>
        public string Name { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;
        public string? Description { get; set; }

        /// <summary>
        /// Same JSON shape as Role.Permissions:
        ///   {"Leads":["read","create"], "Deals":["read"]}
        /// Module keys are PascalCase to match existing role data;
        /// PermissionHandler compares case-insensitively either way.
        /// </summary>
        public string? Permissions { get; set; }

        public int SortOrder { get; set; }

        /// <summary>
        /// Deactivate rather than delete. Tenants already holding the role
        /// keep it; only new provisioning stops including it.
        /// </summary>
        public bool IsActive { get; set; } = true;

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public string? CreatedBy { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }
        public string? UpdatedBy { get; set; }
    }
}
