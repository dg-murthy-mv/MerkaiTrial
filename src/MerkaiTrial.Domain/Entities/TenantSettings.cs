// =====================================================================
// TenantSettings.cs
// Location: MerkaiTrial.Domain/Entities/TenantSettings.cs
//
// 035: Added FiscalYearStartMonth — a NULLABLE override of the country's
//      default. Null is the normal case and means "use whatever my
//      country says", so no existing row needs touching and no default
//      is duplicated in two places.
// =====================================================================

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

        // ── Fiscal year (035) ─────────────────────────────────────────
        /// <summary>
        /// The month this workspace's financial year starts in, 1-12.
        ///
        /// NULL — the normal case — means "inherit from Country
        /// .FiscalYearStartMonth". Only set this when a tenant's financial
        /// year differs from their country's default: an Indian group's Thai
        /// subsidiary reporting on April-March, or a Philippine company that
        /// has elected a non-calendar accounting period.
        ///
        /// Resolved through IFiscalYearService, never read directly by a
        /// page or handler.
        /// </summary>
        public int? FiscalYearStartMonth { get; set; }

        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
        public string? UpdatedBy { get; set; }

        // Navigation properties
        public Tenant? Tenant { get; set; }
    }
}
