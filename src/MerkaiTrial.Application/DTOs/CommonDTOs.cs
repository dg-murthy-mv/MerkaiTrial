using MerkaiTrial.Domain.Enums;
using Microsoft.AspNetCore.Http.HttpResults;
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading.Channels;
namespace MerkaiTrial.Application.DTOs
{
    public record LeadChannelDto(Guid Id, string Name);
    public record LeadSourceDto(Guid Id, string Name);

    // ✅ NEW: Country and Currency DTOs for dropdowns
    public record CountryDropdownDto(Guid Id, string Name, string Code, string CurrencyCode);
    public record CurrencyDropdownDto(string Code);

    // Extended LeadListItem with all fields for detail view
    public record LeadDetailDto(
        Guid Id,
        Guid TenantId,
        Guid ContactId,
        string FullName,
        string Email,
        string Phone,
        string? CompanyName,
        string Channel,
        string Source,
        Guid? ChannelId,          // ✅ ADD THIS
        Guid? SourceId,           // ✅ ADD THIS
        string Status,
        int Score,
        string? OwnerUserId,
        string? OwnerName,          // ✅ ADDED: Owner's full name
        string? OwnerJobTitle,      // ✅ ADDED: Owner's job title
        DateTime CreatedAtUtc,
        DateTime? UpdatedAtUtc,
        bool HasDeal,
        bool IsConverted,
        Guid? DealId,
        Guid? VerticalId,      // ← ADD
        string? VerticalName,
        string? DealStage,
        decimal ExpectedValue,
        string Currency,
        string? Address,           // ✅ NEW
        Guid? CountryId,          // ✅ NEW
        string? CountryName      // ✅ NEW


    );
    // Soft delete lead
    public record DeleteLeadDto(
        Guid TenantId,
        Guid LeadId
    );
    // Paginated response
    public record PaginatedLeadsResponse(
        List<LeadListItem> Items,
        int TotalCount,
        int Page,
        int PageSize,
        int TotalPages
    );

    // Update only lead status
    public record UpdateLeadStatusDto(
        Guid TenantId,
        Guid LeadId,
        string StatusKey
    );

    // Update existing lead details
    public record UpdateLeadDto(
         Guid TenantId,
         Guid LeadId,
         string FullName,
         string? Email,
         string? Phone,
         string? CompanyName,
         Guid? ChannelId,
         Guid? SourceId,
         Guid? VerticalId,
         int Score,
         decimal? EstimatedValue,
         string? OwnerUserId,
         string? Address,          // ✅ NEW
         Guid? CountryId,          // ✅ NEW (Mandatory)
         string? Currency         // ✅ NEW (Mandatory)
     );

    public record LeadListItemDto(
        Guid Id,
        Guid TenantId,
        string ContactName,
        string Channel,
        string Status,
        int Score,
        DateTime CreatedAtUtc
    );

    // Admin Web - ApiClient.cs (record near the bottom)
    public record LeadListItem(
        Guid Id,
        string FullName,
        string Email,
        string Phone,
        string Channel,
        string Source,
        string Status,
        int Score,
        DateTime CreatedAtUtc,
        bool HasDeal,             // NEW
        Guid? DealId,             // NEW
        string DealStage,         // NEW
        decimal EstimatedValue,    // NEW
        string Currency,           // NEW
        string? OwnerUserId,      // ← ADD THIS
        string? OwnerName
    );

    public record JourneyDto(
        Guid Id,
        Guid TenantId,
        string Name,
        string StepsJson,
        DateTime CreatedUtc
    );

    public record ContactLiteDto(
        Guid Id,
        string FirstName,
        string Email
    );

    public record InboxMessageDto(
        Guid Id,
        DateTime CreatedAtUtc,
        string Text,
        bool Outbound,
        string Status,
        ContactLiteDto Contact
    );

    public record PagedLeads(
        int total,
        int page,
        int pageSize,
        List<LeadDto> items
    );

    public record ConvertLeadResultDto(
        Guid ContactId,
        Guid? CompanyId,
        Guid? DealId,
        bool Success,
        string Message
    );
    
    public record ConvertLeadDto(
        Guid TenantId,
        Guid LeadId,
        bool CreateCompany,
        string? CompanyName,
        Guid? ExistingCompanyId,
        bool CreateDeal,
        decimal? DealValue,
        string ConvertedBy
    );

   

    public record LeadDto(
        Guid Id,
        Guid TenantId,
        string FullName,
        string Email,
        string Phone,
        string Source,
        DateTime CreatedUtc,
        string OwnerUserId
    );
    public record CreateLeadDto(
        Guid TenantId,
        string FullName,
        string? Email,
        string? Phone,
        string? CompanyName,
        Guid? ChannelId,        // NEW - Use lookup table
        Guid? SourceId,         // NEW - Use lookup table
        decimal? EstimatedValue,
         Guid? VerticalId,
        string? OwnerUserId,
        string? CreatedBy,
        string? Address,          // ✅ NEW
        Guid? CountryId,          // ✅ NEW (Mandatory)
        string? Currency        // ✅ NEW (Mandatory)
    );

    public record InvoiceSummaryDto(
        string Number,
        DateTime IssueDateUtc,
        string Currency,
        decimal Total,
        string Status,
        string? Customer,
        Guid? DealId
        
    );

    /// <summary>
    /// Request DTO for converting a qualified lead to a deal
    /// </summary>
    public class ConvertLeadToDealDto
    {
        public Guid TenantId { get; set; }
        public Guid LeadId { get; set; }

        // Deal information
        public string DealTitle { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string Stage { get; set; } = "Qualification";  // Default stage
        public decimal ExpectedValue { get; set; }
        public string Currency { get; set; } = "USD";
        public DateTime ExpectedCloseDateUtc { get; set; }

        // Optional: Override owner (otherwise inherit from lead)
        public string? OwnerUserId { get; set; }

        // Metadata
        public string? ConvertedBy { get; set; }
    }

    /// <summary>
    /// Response DTO after successful conversion
    /// </summary>
    public class ConvertLeadToDealResultDto
    {
        public Guid DealId { get; set; }
        public Guid LeadId { get; set; }
        public Guid ContactId { get; set; }
        public bool ContactWasCreated { get; set; }
        public bool CompanyWasCreated { get; set; }
        public string DealTitle { get; set; } = string.Empty;
        public string Stage { get; set; } = string.Empty;
        public DateTime ConvertedAtUtc { get; set; }
        public string Message { get; set; } = string.Empty;
    }


    public record CheckoutResponse(
        string Number,
        string CheckoutUrl,
        string Provider
    );

    

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum CurrencyCode
    {
        INR,
        THB,
        PHP
    }

    // ========== LEAD NOTES ==========
    public record CreateLeadNoteDto(
        Guid TenantId,
        Guid LeadId,
        string Note,
        string? CreatedBy
    );

    public record LeadNoteDto(
        Guid Id,
        Guid LeadId,
        string Note,
        DateTime CreatedAtUtc,
        string? CreatedBy
    );

    // ========== LEAD ACTIVITIES ==========
    public record CreateLeadActivityDto(
        Guid TenantId,
        Guid LeadId,
        string ActivityType,  // Call, Email, Meeting, SMS, WhatsApp
        string? Subject,
        string? Description,
        int? Duration,  // In minutes
        DateTime? ActivityDate,
        string? CreatedBy
    );

    public record LeadActivityDto(
        Guid Id,
        Guid LeadId,
        string ActivityType,
        string? Subject,
        string? Description,
        int? Duration,
        DateTime ActivityDate,
        DateTime CreatedAtUtc,
        string? CreatedBy
    );

    // ========== LEAD REMINDERS ==========
    public record CreateLeadReminderDto(
        Guid TenantId,
        Guid LeadId,
        string Title,
        string? Description,
        DateTime ReminderDate,
        string? CreatedBy
    );

    public record LeadReminderDto(
        Guid Id,
        Guid LeadId,
        string Title,
        string? Description,
        DateTime ReminderDate,
        bool IsCompleted,
        DateTime? CompletedAtUtc,
        DateTime CreatedAtUtc,
        string? CreatedBy
    );

    public record CompleteReminderDto(
        Guid TenantId,
        Guid ReminderId
    );

    // ========== LEAD ASSIGNMENT ==========
    public record AssignLeadDto(
        Guid TenantId,
        Guid LeadId,
        string? OwnerUserId  // User name or ID
    );

    // ========== ENHANCED LEAD LIST WITH COUNTS ==========
    public record EnhancedLeadListItem(
        Guid Id,
        string FullName,
        string Email,
        string Phone,
        string Channel,
        string Source,
        string Status,
        int Score,
        string? OwnerUserId,
        DateTime CreatedAtUtc,
        DateTime? LastActivityDate,
        int NotesCount,
        int ActivitiesCount,
        int PendingRemindersCount,
        bool HasOverdueReminder,
        bool HasDeal,
        Guid? DealId,
        string DealStage,
        decimal ExpectedValue,
        string Currency
    );

    // ========== TIMELINE ITEM (Combined view of notes, activities, reminders) ==========
   

    // ========== EXPORT/IMPORT ==========
    public record ExportLeadsRequest(
        Guid TenantId,
        string? SearchTerm,
        string? Status,
        string? AssignedTo
    );

    

    

    // ========== STATISTICS ==========
    public record LeadStatsDto(
        int TotalLeads,
        int NewLeads,
        int WorkingLeads,
        int QualifiedLeads,
        int UnqualifiedLeads,
        int ConvertedLeads,
        int OverdueReminders,
        int TodayActivities,
        int QuotaUsed = 0
    );
}
