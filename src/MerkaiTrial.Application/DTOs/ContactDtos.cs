// =====================================================================
// FILE: MerkaiTrial.Application/DTOs/ContactDtos.cs
//
// 075 — CUSTOM FIELDS
//   ContactDto.CustomFieldValues      what is stored, keyed by field id
//   CreateContactDto.CustomFields     what the form is stating
//   UpdateContactDto.CustomFields     ditto
//
//   Values travel in the invariant wire shape described in
//   CustomFieldDtos.cs ("1250.5", "2027-03-01", an option key, "true").
//
//   On Create/Update, NULL means "this caller says nothing about custom
//   fields" — every caller written before 075 sends null and behaves
//   exactly as before. A dictionary means "these are the values", and a
//   key with an empty value clears that field. Keys that are absent are
//   left alone. CustomFieldValueWriter carries the full note.
//
// 076 — ContactListItem.CustomFieldValues: the values of the fields shown
//   as list columns, for the paged Contacts list.
// =====================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MerkaiTrial.Application.DTOs
{
    public class ContactDto
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public Guid? CompanyId { get; set; }

        public string FirstName { get; set; } = string.Empty;
        public string? LastName { get; set; }
        public string? JobTitle { get; set; }

        public string? Email { get; set; }
        public string? Phone { get; set; }
        public string? Mobile { get; set; }

        public string? Address { get; set; }
        public string? City { get; set; }
        public string? Country { get; set; }
        public string? PostalCode { get; set; }

        public string? Notes { get; set; }
        public bool IsPrimary { get; set; }
        public string? LineUserId { get; set; }

        // Related data
        public string? CompanyName { get; set; }
        public int DealCount { get; set; }

        // Audit
        public DateTime CreatedAtUtc { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }
        public string? UpdatedBy { get; set; }

        /// <summary>
        /// 075. Every stored custom field value on this contact, keyed by
        /// field id, in the wire shape. Includes retired (switched-off)
        /// fields, which the detail page shows marked as retired.
        /// </summary>
        public Dictionary<Guid, string> CustomFieldValues { get; set; } = new();

        // Calculated
        public string FullName => $"{FirstName} {LastName}".Trim();
        public string DisplayName => !string.IsNullOrEmpty(LastName) ? $"{FirstName} {LastName}" : FirstName;
        public string InitialsDisplay => $"{FirstName.Substring(0, 1)}{(LastName?.Substring(0, 1) ?? "")}".ToUpper();
    }

    public class ContactListItem
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public Guid? CompanyId { get; set; }

        public string FirstName { get; set; } = string.Empty;
        public string? LastName { get; set; }
        public string? JobTitle { get; set; }

        public string? Email { get; set; }
        public string? Phone { get; set; }
        public string? Mobile { get; set; }

        public string? CompanyName { get; set; }
        public bool IsPrimary { get; set; }
        public int DealCount { get; set; }

        public DateTime CreatedAtUtc { get; set; }

        /// <summary>
        /// 076. Values of the custom fields shown as LIST COLUMNS only
        /// (ShowInList), keyed by field id, in the wire shape. Filled by
        /// GetContactsHandler for the paged list; empty everywhere else
        /// (lookups, by-company), which do not render custom columns.
        /// </summary>
        public Dictionary<Guid, string> CustomFieldValues { get; set; } = new();

        // Calculated
        public string FullName => $"{FirstName} {LastName}".Trim();
        public string DisplayName => !string.IsNullOrEmpty(LastName) ? $"{FirstName} {LastName}" : FirstName;
        public string InitialsDisplay => $"{FirstName.Substring(0, 1)}{(LastName?.Substring(0, 1) ?? "")}".ToUpper();
    }

    public class CreateContactDto
    {
        public Guid TenantId { get; set; }
        public Guid? CompanyId { get; set; }

        public string FirstName { get; set; } = string.Empty;
        public string? LastName { get; set; }
        public string? JobTitle { get; set; }

        public string? Email { get; set; }
        public string? Phone { get; set; }
        public string? Mobile { get; set; }

        public string? Address { get; set; }
        public string? City { get; set; }
        public string? Country { get; set; }
        public string? PostalCode { get; set; }

        public string? Notes { get; set; }
        public bool IsPrimary { get; set; }

        public string CreatedBy { get; set; } = string.Empty;

        /// <summary>075. Null = say nothing about custom fields. See the file header.</summary>
        public Dictionary<Guid, string?>? CustomFields { get; set; }
    }

    public class UpdateContactDto
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public Guid? CompanyId { get; set; }

        public string FirstName { get; set; } = string.Empty;
        public string? LastName { get; set; }
        public string? JobTitle { get; set; }

        public string? Email { get; set; }
        public string? Phone { get; set; }
        public string? Mobile { get; set; }

        public string? Address { get; set; }
        public string? City { get; set; }
        public string? Country { get; set; }
        public string? PostalCode { get; set; }

        public string? Notes { get; set; }
        public bool IsPrimary { get; set; }

        public string UpdatedBy { get; set; } = string.Empty;

        /// <summary>075. Null = say nothing about custom fields. See the file header.</summary>
        public Dictionary<Guid, string?>? CustomFields { get; set; }
    }

    public record ContactStatsDto(
    int TotalContacts,
    int PrimaryContacts,
    int ContactsWithCompany,
    int ActiveCompanies
);
}
