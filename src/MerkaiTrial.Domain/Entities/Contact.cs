namespace MerkaiTrial.Domain.Entities
{
    public class Contact
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public Guid? CompanyId { get; set; }

        // Basic Info
        public string FirstName { get; set; } = string.Empty;
        public string? LastName { get; set; }
        public string? JobTitle { get; set; }

        // Contact Details
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public string? Mobile { get; set; }

        // Address
        public string? Address { get; set; }
        public string? City { get; set; }
        public string? Country { get; set; }
        public string? PostalCode { get; set; }

        // Additional
        public string? Notes { get; set; }
        public bool IsPrimary { get; set; }
        public string? LineUserId { get; set; }

        // Audit
        public DateTime CreatedAtUtc { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }
        public string? UpdatedBy { get; set; }
        public bool IsDeleted { get; set; }

        // Navigation
        public Company? Company { get; set; }
        public ICollection<Deal> Deals { get; set; } = new List<Deal>();

        // Calculated
        public string FullName => $"{FirstName} {LastName}".Trim();
        public string DisplayName => !string.IsNullOrEmpty(LastName) ? $"{FirstName} {LastName}" : FirstName;
    }
}
