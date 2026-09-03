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
    public class TaxRateDto
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
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
    }

    public class CreateTaxRateDto
    {
        public Guid TenantId { get; set; }
        public string CountryCode { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string TaxType { get; set; } = string.Empty;
        public decimal Rate { get; set; }
        public bool IsDefault { get; set; }
        public string CreatedBy { get; set; } = string.Empty;
    }

    public class UpdateTaxRateDto
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public string Name { get; set; } = string.Empty;
        public decimal Rate { get; set; }
        public bool IsDefault { get; set; }
        public string UpdatedBy { get; set; } = string.Empty;
    }
}
