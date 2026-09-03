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
    }

    public record ContactStatsDto(
    int TotalContacts,
    int PrimaryContacts,
    int ContactsWithCompany,
    int ActiveCompanies
);
}
