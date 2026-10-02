using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MerkaiTrial.Application.DTOs
{
    public class PaginatedResult<T>
    {
        public List<T> Items { get; set; } = new();
        public int Page { get; set; }  // ✅ CHANGED from Page
        public int PageSize { get; set; }
        public int TotalCount { get; set; }
        public int TotalPages => (int)Math.Ceiling(TotalCount / (double)PageSize);
        public bool HasPrevious => Page > 1;  // ✅ CHANGED from Page
        public bool HasNext => Page < TotalPages;  // ✅ CHANGED from Page
    }
    public record TaxRateStatsDto(
    int TotalRates,
    int ActiveCountries,
    int DefaultRates,
    int SystemRates
);
    public record CountryStatsDto(
        int TotalCountries,
        int ActiveCountries,
        int InactiveCountries,
        int TotalCurrencies
    );
    public record VerticalStatsDto(
       int TotalVerticals,
       int SystemVerticals,
       int CustomVerticals,
       int ActiveTenants
   );
    public class CountryDto
    {
        public Guid Id { get; set; }
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? DialCode { get; set; }
        public string? CurrencyCode { get; set; }
        public string? TaxLabel { get; set; }
        public decimal? DefaultTaxRate { get; set; }
        public bool IsActive { get; set; }
        public int DisplayOrder { get; set; }
    }

    public class CountryListItem
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? DialCode { get; set; }
        public string? CurrencyCode { get; set; }
        public string? TaxLabel { get; set; }
        public decimal? DefaultTaxRate { get; set; }
        public bool IsActive { get; set; }

        
    }

    public class UpdateCountryDto
    {
        // If your update uses the ID from the route, you can omit this.
        // Keep it if you sometimes pass the ID in the body.
        public Guid? Id { get; set; }

        [Required, StringLength(100)]
        public string Name { get; set; } = default!;   // e.g., "Thailand"

        // E.g., "+66" — optional
        [StringLength(10)]
        public string? DialCode { get; set; }

        // ISO 4217 like "USD", "THB" — optional in your code path
        [StringLength(3)]
        public string? CurrencyCode { get; set; }

        // E.g., "VAT", "GST", "Sales Tax" — optional
        [StringLength(32)]
        public string? TaxLabel { get; set; }

        // Default tax rate as a percentage (e.g., 7.0 for 7%)
        [Range(0, 100)]
        public decimal DefaultTaxRate { get; set; }

        public bool IsActive { get; set; }
    }

    public class CreateCountryDto
    {
        // ISO 3166 country code (e.g., "TH", "US"). Adjust length if you use alpha-3.
        [Required, StringLength(3, MinimumLength = 2)]
        public string Code { get; set; } = default!;

        [Required, StringLength(100)]
        public string Name { get; set; } = default!;

        // E.g., "+66"
        [StringLength(10)]
        public string? DialCode { get; set; }

        // ISO 4217 currency code (e.g., "THB", "USD")
        [StringLength(3)]
        public string? CurrencyCode { get; set; }

        // E.g., "VAT", "GST", "Sales Tax"
        [StringLength(32)]
        public string? TaxLabel { get; set; }

        // Percentage (e.g., 7.0 = 7%). Adjust range if you store as fraction.
        [Range(0, 100)]
        public decimal DefaultTaxRate { get; set; }
    }

    // =====================================================================
    // COMPANY VERTICAL DTOs
    // =====================================================================
    public class CompanyVerticalDto
    {
        public Guid Id { get; set; }
        public Guid? TenantId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? Icon { get; set; }
        public string? Color { get; set; }
        public bool IsActive { get; set; }
        public bool IsSystem { get; set; }
        public int DisplayOrder { get; set; }
    }

    public class CompanyVerticalListItem
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Icon { get; set; }
        public string? Color { get; set; }
        public bool IsSystem { get; set; }
        public Guid? TenantId { get; set; } // ✅ ADDED
        public string? TenantName { get; set; } // ✅ ADDED - For display
    }

    public class CreateCompanyVerticalDto
    {
        public Guid TenantId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? Icon { get; set; }
        public string? Color { get; set; }
        public string CreatedBy { get; set; } = string.Empty;
    }

    public class UpdateCompanyVerticalDto
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? Icon { get; set; }
        public string? Color { get; set; }
        public string UpdatedBy { get; set; } = string.Empty;
    }

    // =====================================================================
    // TAX RATE DTOs
    // =====================================================================
    /// <summary>
    /// 056: TenantId is NULLABLE — NULL means a SYSTEM rate shared by every
    /// tenant in that country. See TaxRate.cs for why this was the single
    /// thing keeping the module from working.
    /// </summary>
    public class TaxRateDto
    {
        public Guid Id { get; set; }

        /// <summary>NULL = system rate.</summary>
        public Guid? TenantId { get; set; }

        /// <summary>056: so a screen does not have to know what NULL means.</summary>
        public bool IsSystem => TenantId is null;
        public string CountryCode { get; set; } = string.Empty;
        public string CountryName { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string TaxType { get; set; } = string.Empty;
        public decimal Rate { get; set; }
        public bool IsDefault { get; set; }
        public DateTime? EffectiveFrom { get; set; }
        public DateTime? EffectiveTo { get; set; }
        public bool IsActive { get; set; }
    }

    public class TaxRateListItem
    {
        public Guid Id { get; set; }
        public string CountryCode { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string TaxType { get; set; } = string.Empty;
        public decimal Rate { get; set; }
        public bool IsDefault { get; set; }

        /// <summary>
        /// 057. The list could not tell a rate that is in force from one
        /// scheduled for next April or one retired last year — all three
        /// looked identical, and two of them are not what a new quote uses.
        /// </summary>
        public bool IsActive { get; set; } = true;
        public DateTime? EffectiveFrom { get; set; }
        public DateTime? EffectiveTo { get; set; }

        /// <summary>
        /// 056. The list showed no way to tell a system rate from a tenant's
        /// own, which matters the moment a tenant can have both: two rows
        /// called "Standard GST 18%" where one is editable and one is not.
        /// </summary>
        public Guid? TenantId { get; set; }
        public bool IsSystem => TenantId is null;
    }

    public class CreateTaxRateDto
    {
        /// <summary>
        /// 056. NULL (or Guid.Empty) asks for a SYSTEM rate, and the API
        /// refuses that unless the caller is a super admin.
        ///
        /// For a tenant rate this field is IGNORED — the controller uses the
        /// tenant on the caller's token. It was read straight from the body,
        /// so any authenticated user could create a tax rate for any tenant
        /// by typing their id into the request.
        /// </summary>
        public Guid? TenantId { get; set; }
        public string CountryCode { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string TaxType { get; set; } = string.Empty;
        public decimal Rate { get; set; }
        public bool IsDefault { get; set; }

        /// <summary>
        /// 057. The day this rate starts applying. NULL = it always has.
        ///
        /// This is how a VAT change is made SAFELY: create the new rate with
        /// EffectiveFrom set to the day it takes effect, and close the old
        /// one with EffectiveTo the day before. Both rows stay, the switch
        /// happens on its own, and a quote raised last month still shows the
        /// rate that applied then. The alternative — editing the row on the
        /// morning — leaves no record that the rate ever was anything else.
        /// </summary>
        public DateTime? EffectiveFrom { get; set; }

        /// <summary>The last day it applies. NULL = still in force.</summary>
        public DateTime? EffectiveTo { get; set; }

        public string CreatedBy { get; set; } = string.Empty;
    }

    public class UpdateTaxRateDto
    {
        public Guid Id { get; set; }

        /// <summary>
        /// 056: IGNORED by the API, and kept only so existing callers still
        /// compile. Which rate you may edit is decided by the row's own
        /// owner versus your token, never by what you send.
        /// </summary>
        public Guid? TenantId { get; set; }
        public string Name { get; set; } = string.Empty;
        public decimal Rate { get; set; }
        public bool IsDefault { get; set; }

        /// <summary>057. See CreateTaxRateDto — this is how a rate change is scheduled.</summary>
        public DateTime? EffectiveFrom { get; set; }
        public DateTime? EffectiveTo { get; set; }

        /// <summary>
        /// 057. The manual switch. Off means "retired by hand", regardless of
        /// the dates — and nothing could set it either way before, so no rate
        /// has ever been switched off and none could have been switched back
        /// on if it had been.
        /// </summary>
        public bool IsActive { get; set; } = true;

        public string UpdatedBy { get; set; } = string.Empty;
    }
}
