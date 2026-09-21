// =====================================================================
// DEAL DTOs - FINAL
// Location: MerkaiTrial.Application/DTOs/DealDTOs.cs
//
// Stage canonical values (match handlers + DB): New|Qualified|Proposal|Won|Lost
// Probabilities:  New=10, Qualified=30, Proposal=50, Won=100, Lost=0
// Currency:       stored per-deal on Deal entity (column: Currency nvarchar(8))
// TenantId:       always string ("BB100001" etc.) — never Guid
// =====================================================================

namespace MerkaiTrial.Application.DTOs
{
    // ==================== LIST ====================

    public record DealListItem(
        Guid Id,
        string Title,
        string? CompanyName,
        string Stage,
        decimal ExpectedValue,
        string Currency,
        DateTime ExpectedCloseDateUtc,
        DateTime? ActualCloseDateUtc,
        string? OwnerUserId,
        string? OwnerName,
        string? OwnerInitials,
        int Probability
    );

    public record TimelineItemDto(
        Guid Id,
        string Type,        // "Note" | "Activity" | "Reminder" | "StageChange"
        string Title,
        string? Description,
        DateTime Date,
        string? CreatedBy,
        string Icon,        // e.g. "bi-telephone"
        string BadgeClass   // e.g. "bg-success"
    );
    public record GetDealsRequest(
        string TenantId,
        string? Stage = null,
        string? Search = null,
        string? OwnerUserId = null,
        int Page = 1,
        int PageSize = 20
    );

    public record GetDealsResponse(
        List<DealListItem> Items,
        int TotalCount,
        int Page,
        int PageSize,
        int QuotaUsed = 0
    );

    // ==================== DETAIL ====================

    public record DealDetailDto(
        Guid Id,
        string TenantId,
        string Title,
        string? Description,
        string Stage,
        int Probability,
        decimal ExpectedValue,
        string Currency,
        DateTime ExpectedCloseDateUtc,
        DateTime? ActualCloseDateUtc,
        string? CompanyName,
        Guid? CompanyId,
        Guid ContactId,
        string? ContactName,
        string? OwnerUserId,
        string? OwnerName,
        string? OwnerInitials,
        Guid? LeadId,
        string? Source,
        string? Tags,
        DateTime CreatedAtUtc,
        string? CreatedBy,
        DateTime? UpdatedAtUtc,
        string? UpdatedBy,
        // ✅ Vertical — inherited from Lead on conversion, editable on Edit page
        Guid? VerticalId,
        string? VerticalName
    );

    // ==================== SUMMARY (used by GetByIdAsync) ====================

    public record DealDto(
        Guid Id,
        string TenantId,
        string Title,
        string? Description,
        string Stage,
        int Probability,
        decimal ExpectedValue,
        string Currency,
        DateTime ExpectedCloseDateUtc,
        DateTime? ActualCloseDateUtc,
        decimal? ActualValue,
        Guid? CompanyId,
        string? CompanyName,
        Guid ContactId,
        string? ContactName,
        string? OwnerUserId,
        string? OwnerName,
        Guid? LeadId,
        Guid? SourceId,
        string? Tags,
        DateTime CreatedAtUtc,
        string? CreatedBy,
        DateTime? UpdatedAtUtc,
        string? UpdatedBy
    );

    // ==================== CREATE ====================

    public class CreateDealDto
    {
        public string TenantId { get; set; } = string.Empty;
        public Guid ContactId { get; set; }
        public Guid? LeadId { get; set; }
        public Guid? CompanyId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? Description { get; set; }

        // ✅ Default is Discovery — matches DB constraint
        public string Stage { get; set; } = DealStages.Discovery;
        public int? Probability { get; set; }

        // ✅ Multi-tenant: currency set from ICurrentTenantService in page model
        public decimal ExpectedValue { get; set; }
        public string Currency { get; set; } = string.Empty;

        public DateTime ExpectedCloseDateUtc { get; set; }
        public string? OwnerUserId { get; set; }

        // ✅ Source stored as string name from LeadSources.Name
        //    No SourceId int FK — avoids Guid conversion crash
        public Guid? SourceId { get; set; }
        public string? Source { get; set; }
        public string? Tags { get; set; }
        public string? CreatedBy { get; set; }

        // ✅ Vertical — set from Lead on conversion, optional on manual create
        public Guid? VerticalId { get; set; }
    }

    // ==================== UPDATE ====================

    public class UpdateDealDto
    {
        public string Title { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string Stage { get; set; } = string.Empty;
        public int Probability { get; set; }
        public decimal ExpectedValue { get; set; }
        public string Currency { get; set; } = string.Empty;
        public DateTime ExpectedCloseDateUtc { get; set; }

        // ✅ ADDED — were missing and caused all the compile errors
        public decimal? ActualValue { get; set; }
        public DateTime? ActualCloseDateUtc { get; set; }
        public string? LostReason { get; set; }
        public Guid? SourceId { get; set; }   // FK → LeadSources.Id
        public string? Source { get; set; }
        public string? OwnerUserId { get; set; }
        public string? Tags { get; set; }
        public string? UpdatedBy { get; set; }
        // ✅ Vertical — editable on Edit Deal page
        public Guid? VerticalId { get; set; }
        public string? ReopenReason { get; set; }

    }

    // ==================== NOTES ====================

    public record DealNoteDto(
        Guid Id,
        Guid DealId,       // ✅ required — was missing in handler projections
        string Note,
        DateTime CreatedAtUtc,
        string? CreatedBy
    );

    public class CreateDealNoteDto
    {
        public Guid TenantId { get; set; } 
        public Guid DealId { get; set; }
        public string Note { get; set; } = string.Empty;
        public string? CreatedBy { get; set; }
    }

    // ==================== ACTIVITIES ====================

    public record DealActivityDto(
        Guid Id,
        Guid DealId,       // ✅ required
        string ActivityType,
        string? Subject,
        string? Description,
        int? Duration,
        DateTime ActivityDate,
        DateTime CreatedAtUtc,
        string? CreatedBy
    );

    public class CreateDealActivityDto
    {
        public Guid TenantId { get; set; }
        public Guid DealId { get; set; }
        public string ActivityType { get; set; } = string.Empty;
        public string? Subject { get; set; }
        public string? Description { get; set; }
        public int? Duration { get; set; }
        public string? CreatedBy { get; set; }
    }

    // ==================== REMINDERS ====================

    public record DealReminderDto(
        Guid Id,
        Guid DealId,      // ✅ required
        string Title,
        string? Description,
        DateTime ReminderDate,
        bool IsCompleted,
        DateTime? CompletedAtUtc,
        DateTime CreatedAtUtc,
        string? CreatedBy
    );

    public class CreateDealReminderDto
    {
        public Guid TenantId { get; set; } 
        public Guid DealId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? Description { get; set; }
        public DateTime ReminderDate { get; set; }
        public string? CreatedBy { get; set; }
    }

    // ==================== STAGE HISTORY ====================

    public record DealStageHistoryDto(
        Guid Id,
        Guid DealId,
        string? FromStage,
        string ToStage,
        DateTime ChangedAtUtc,
        string? ChangedBy
    );

    public record GetDealStageHistoryRequest(string TenantId, Guid DealId);

    // ==================== SOURCES ====================

    public record DealSourceDto(string Value, string Label);

    public record GetDealSourcesRequest(string TenantId);

    // ==================== STAGE CONSTANTS ====================

    /// <summary>
    /// Single source of truth for stage names.
    /// These are the values stored in the DB (nvarchar column).
    /// Use DealStages.New etc — never scatter raw strings.
    /// </summary>
    public static class DealStages
    {
        public const string Discovery = "Discovery";
        public const string Qualification = "Qualification";
        public const string Proposal = "Proposal";
        public const string Negotiation = "Negotiation";
        public const string ClosedWon = "ClosedWon";
        public const string ClosedLost = "ClosedLost";

        public static readonly IReadOnlySet<string> All =
            new HashSet<string>
            {
                Discovery, Qualification, Proposal,
                Negotiation, ClosedWon, ClosedLost
            };

        public static bool IsValid(string? stage) =>
            !string.IsNullOrEmpty(stage) && All.Contains(stage);

        public static int DefaultProbability(string stage) => stage switch
        {
            Discovery => 20,
            Qualification => 30,
            Proposal => 40,
            Negotiation => 60,
            ClosedWon => 100,
            ClosedLost => 0,
            _ => 20
        };

        public static bool IsTerminal(string? stage) =>
            stage is ClosedWon or ClosedLost;
        public static bool IsQuoteEligible(string? stage) =>
           stage is Proposal or Negotiation;
    }
}
