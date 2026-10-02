using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MerkaiTrial.Domain.Entities
{
    /// <summary>
    /// 056: TenantId is NULLABLE. NULL = a SYSTEM rate, available to every
    /// tenant in that country; a value = that tenant's own rate, which wins
    /// over the system one.
    ///
    /// It was a non-nullable Guid, and that single fact broke the module:
    ///
    ///   • The admin page stored Guid.Empty for a system rate. Every query
    ///     looked for NULL. They could never meet.
    ///   • `t.TenantId == null` on a non-nullable Guid is ALWAYS FALSE —
    ///     the compiler says so (CS8073). It appeared in four places and
    ///     did nothing in any of them, which is why the "System Rates"
    ///     figure was always 0 and why the tenant-or-system fallback in
    ///     GetDefaultTaxRateForCountry could only ever return nothing.
    ///
    /// Role and CompanyVertical already use NULL for exactly this, so this
    /// is the codebase's own convention rather than a new one.
    /// 056_TaxRateTenantScope.sql makes the column nullable and converts
    /// the existing Guid.Empty rows.
    /// </summary>
    public class TaxRate
    {
        public Guid Id { get; set; }

        /// <summary>NULL = system rate, shared by every tenant in this country.</summary>
        public Guid? TenantId { get; set; }

        /// <summary>Convenience for the screens. Not mapped.</summary>
        [System.ComponentModel.DataAnnotations.Schema.NotMapped]
        public bool IsSystem => TenantId is null;
        public string CountryCode { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string TaxType { get; set; } = string.Empty;  // VAT, GST, Sales Tax
        public decimal Rate { get; set; }
        public bool IsDefault { get; set; }
        public DateTime? EffectiveFrom { get; set; }
        public DateTime? EffectiveTo { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAtUtc { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }
        public string? UpdatedBy { get; set; }
        public bool IsDeleted { get; set; }
    }
}
