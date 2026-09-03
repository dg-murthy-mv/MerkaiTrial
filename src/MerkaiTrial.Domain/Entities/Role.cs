using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MerkaiTrial.Domain.Entities
{
    public class Role
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>
        /// NULL  = system role — shared by all tenants, READ-ONLY from tenant UI.
        /// Value = custom role owned by that tenant.
        ///
        /// Before this existed, "sales_manager" was one row assigned to users
        /// in every tenant: one tenant admin editing it silently rewrote every
        /// other tenant's permissions. Every role query must filter on
        /// (TenantId == currentTenant || TenantId == null).
        /// </summary>
        public Guid? TenantId { get; set; }

        public string Name { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string? Description { get; set; }
        public bool IsSystemRole { get; set; }
        public string? Permissions { get; set; } // JSON
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public string? CreatedBy { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }
        public string? UpdatedBy { get; set; }
        public bool IsDeleted { get; set; }

        public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();

        /// <summary>Tenant admins may edit their own custom roles only.</summary>
        public bool IsEditableBy(Guid tenantId, bool isSuperAdmin)
            => isSuperAdmin || (!IsSystemRole && TenantId == tenantId);
    }
}
