using MerkaiTrial.Domain.Enums;

namespace MerkaiTrial.Domain.Entities
{
    public class Lead
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }

        // Lead Information (NOT linked to Contact initially)
        public string FullName { get; set; } = string.Empty;
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public string? CompanyName { get; set; }

        // ✅ NEW: Address Information
        public string? Address { get; set; }
        public Guid? CountryId { get; set; }
       

        // Tracking (using lookup tables now)
        public Guid? ChannelId { get; set; }
        public Guid? SourceId { get; set; }

        // Keep old Channel enum for backward compatibility (will migrate data)
        public Channel Channel { get; set; }
        public string Source { get; set; } = "widget";

        public LeadStatus Status { get; set; }
        public int Score { get; set; }
        public string? OwnerUserId { get; set; }
        public decimal? EstimatedValue { get; set; }
        public string? Currency { get; set; } = "INR";
        public string? CustomFieldsJson { get; set; }

        // Links to Contacts/Companies (NULLABLE - only set initially if known)
        public Guid? ContactId { get; set; }
        public Guid? CompanyId { get; set; }
        public Guid? VerticalId { get; set; }

        // Conversion Tracking
        public bool IsConverted { get; set; }
        public Guid? ConvertedToContactId { get; set; }
        public Guid? ConvertedToCompanyId { get; set; }
        public Guid? DealId { get; set; }
        public DateTime? ConvertedAtUtc { get; set; }
        public string? ConvertedBy { get; set; }

        // Audit
        public DateTime CreatedAtUtc { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }
        public string? UpdatedBy { get; set; }
        public bool IsDeleted { get; set; }

        // Navigation Properties
        public Contact? Contact { get; set; }
        public Company? Company { get; set; }
        public Contact? ConvertedContact { get; set; }
        public Company? ConvertedCompany { get; set; }
        public LeadChannel? LeadChannel { get; set; }
        public LeadSource? LeadSource { get; set; }

        // ✅ NEW: Navigation Properties for Country and Currency
        public Country? Country { get; set; }
        public CompanyVertical? Vertical { get; set; }
        public ICollection<LeadNote> Notes { get; set; } = new List<LeadNote>();
        public ICollection<LeadActivity> Activities { get; set; } = new List<LeadActivity>();
        public ICollection<LeadReminder> Reminders { get; set; } = new List<LeadReminder>();
    }
}