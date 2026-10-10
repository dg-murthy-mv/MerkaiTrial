// =====================================================================
// COMPANY DTOs
// Location: MerkaiTrial.Application/DTOs/CompanyDtos.cs
//
// TENANT FIX: Removed hardcoded Country = "IN" default from all DTOs.
//   Country is now empty string — the Web layer sets it from
//   ICurrentTenantService.GetCountryCode() before sending to the API.
//   The API controller resolves TenantId server-side and never trusts
//   the country default from the client.
//
// 078 — CUSTOM FIELDS ON COMPANIES (the same shape as Contacts and Deals)
//   CompanyDto.CustomFieldValues       what is stored, keyed by field id
//   CompanyListItem.CustomFieldValues  the list-column fields, this page only
//   CreateCompanyDto.CustomFields      what the form is stating
//   UpdateCompanyDto.CustomFields      ditto
//
//   Values travel in the invariant wire shape described in
//   CustomFieldDtos.cs ("1250.5", "2027-03-01", an option key, "true").
//
//   On Create/Update, NULL means "this caller says nothing about custom
//   fields" — every caller written before 078 sends null and behaves
//   exactly as before. A dictionary means "these are the values"; a key
//   with an empty value clears that field; an absent key is left alone.
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

        /// <summary>
        /// 078. Every stored custom field value on this company, keyed by
        /// field id, in the wire shape. Includes values of RETIRED fields,
        /// which the detail page shows marked as retired.
        /// </summary>
        public Dictionary<Guid, string> CustomFieldValues { get; set; } = new();
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

        /// <summary>
        /// 078. Values of the fields shown as list columns, for the paged
        /// Companies list only. Empty everywhere else (lookups), which do
        /// not render custom columns.
        /// </summary>
        public Dictionary<Guid, string> CustomFieldValues { get; set; } = new();
    }

    public class CreateCompanyDto
    {
        public Guid TenantId { get; set; }

        public string  Name     { get; set; } = string.Empty;
        public string  Country  { get; set; } = string.Empty;  // ✅ set by Web layer from tenant
        public string? TaxId    { get; set; }
        public string  Vertical { get; set; } = "Generic";

        public string CreatedBy { get; set; } = string.Empty;

        /// <summary>078. Null = say nothing about custom fields. See the file header.</summary>
        public Dictionary<Guid, string?>? CustomFields { get; set; }
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

        /// <summary>078. Null = say nothing about custom fields. See the file header.</summary>
        public Dictionary<Guid, string?>? CustomFields { get; set; }
    }

    public record CompanyStatsDto(
        int TotalCompanies,
        int TotalContacts,
        int TotalDeals,
        int ActiveVerticals
    );
}
