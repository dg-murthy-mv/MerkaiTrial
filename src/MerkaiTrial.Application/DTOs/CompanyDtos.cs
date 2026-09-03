// =====================================================================
// COMPANY DTOs
// Location: MerkaiTrial.Application/DTOs/CompanyDtos.cs
//
// TENANT FIX: Removed hardcoded Country = "IN" default from all DTOs.
//   Country is now empty string — the Web layer sets it from
//   ICurrentTenantService.GetCountryCode() before sending to the API.
//   The API controller resolves TenantId server-side and never trusts
//   the country default from the client.
// =====================================================================

namespace MerkaiTrial.Application.DTOs
{
    public class CompanyDto
    {
        public Guid Id       { get; set; }
        public Guid TenantId { get; set; }

        public string  Name     { get; set; } = string.Empty;
        public string  Country  { get; set; } = string.Empty;  // ✅ no hardcoded "IN"
        public string? TaxId    { get; set; }
        public string  Vertical { get; set; } = "Generic";

        public int ContactCount        { get; set; }
        public int DealCount           { get; set; }
        public int PrimaryContactCount { get; set; }

        public DateTime  CreatedAtUtc { get; set; }
        public string?   CreatedBy    { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }
        public string?   UpdatedBy    { get; set; }
    }

    public class CompanyListItem
    {
        public Guid Id       { get; set; }
        public Guid TenantId { get; set; }

        public string  Name     { get; set; } = string.Empty;
        public string  Country  { get; set; } = string.Empty;  // ✅ no hardcoded "IN"
        public string? TaxId    { get; set; }
        public string  Vertical { get; set; } = "Generic";

        public int ContactCount { get; set; }
        public int DealCount    { get; set; }

        public DateTime CreatedAtUtc { get; set; }
    }

    public class CreateCompanyDto
    {
        public Guid TenantId { get; set; }

        public string  Name     { get; set; } = string.Empty;
        public string  Country  { get; set; } = string.Empty;  // ✅ set by Web layer from tenant
        public string? TaxId    { get; set; }
        public string  Vertical { get; set; } = "Generic";

        public string CreatedBy { get; set; } = string.Empty;
    }

    public class UpdateCompanyDto
    {
        public Guid Id       { get; set; }
        public Guid TenantId { get; set; }

        public string  Name     { get; set; } = string.Empty;
        public string  Country  { get; set; } = string.Empty;  // ✅ set by Web layer from tenant
        public string? TaxId    { get; set; }
        public string  Vertical { get; set; } = "Generic";

        public string UpdatedBy { get; set; } = string.Empty;
    }

    public record CompanyStatsDto(
        int TotalCompanies,
        int TotalContacts,
        int TotalDeals,
        int ActiveVerticals
    );
}
