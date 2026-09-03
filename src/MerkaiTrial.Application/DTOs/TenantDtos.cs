// =====================================================================
// UPDATED TENANT DTOs with Plan field
// Location: MerkaiTrial.Application/DTOs/TenantDtos.cs
// =====================================================================

using MerkaiTrial.Domain.Entities;
using System.ComponentModel.DataAnnotations;

namespace MerkaiTrial.Application.DTOs
{
    // ==================== TENANT DTOs ====================

    public record TenantDto(
        Guid Id,
        string Name,
        string FromEmail,
        string? Phone,
        string DefaultCurrency,
        string TimeZone,
        Guid? CountryId,          // <-- FK used by the UI select
        string? CountryCode,      // optional, nice for display
        string? CountryName,      // optional, nice for display
        bool IsActive,
        DateTime CreatedAtUtc,
        DateTime UpdatedAtUtc,
        string Plan  // ✅ ADDED
    );

    public record CreateTenantCommand(
        [Required(ErrorMessage = "Tenant name is required")]
        [StringLength(200, MinimumLength = 2, ErrorMessage = "Name must be between 2 and 200 characters")]
        string Name,

        [Required(ErrorMessage = "Email is required")]
        [EmailAddress(ErrorMessage = "Invalid email format")]
        [StringLength(320, ErrorMessage = "Email cannot exceed 320 characters")]
        string FromEmail,

        [Required(ErrorMessage = "Default currency is required")]
        string DefaultCurrency,

        [Required(ErrorMessage = "Timezone is required")]
        [StringLength(100, ErrorMessage = "Timezone cannot exceed 100 characters")]
        string TimeZone,

        [Phone(ErrorMessage = "Invalid phone number format")]
        [StringLength(50, ErrorMessage = "Phone cannot exceed 50 characters")]
        string? Phone = null,

        Guid? CountryId = null,

        [Required]
        [StringLength(10, ErrorMessage = "Language code cannot exceed 10 characters")]
        string PreferredLanguage = "en",

        [Required]
        [StringLength(32, ErrorMessage = "Plan cannot exceed 32 characters")]
        string Plan = "Starter",

        bool IsActive = true,

        [StringLength(255, ErrorMessage = "Domain cannot exceed 255 characters")]
        string? Domain = null,

        [EmailAddress(ErrorMessage = "Invalid reply-to email format")]
        [StringLength(320, ErrorMessage = "Reply-to email cannot exceed 320 characters")]
        string? ReplyToEmail = null,

        [StringLength(255, ErrorMessage = "CreatedBy cannot exceed 255 characters")]
        string? CreatedBy = null,

        [StringLength(255, ErrorMessage = "UpdatedBy cannot exceed 255 characters")]
        string? UpdatedBy = null
    );

    public record UpdateTenantCommand(
        Guid TenantId,
        string Name,
        string FromEmail,
        string? Phone,
        string? DefaultCurrency,
        string TimeZone
    );

    public record TenantSettingsDto(
        Guid TenantId,
        int MaxUsers,
        int MaxLeads,
        int MaxDeals,
        long StorageLimit,
        string? FeatureFlags
    );

    public record UpdateTenantSettingsCommand(
        Guid TenantId,
        int MaxUsers,
        int MaxLeads,
        int MaxDeals,
        long StorageLimit,
        string? FeatureFlags
    );

    public record TenantListItem(
        Guid Id,
        string Name,
        string FromEmail,
        string? DefaultCurrency,        
        bool IsActive,
        int UserCount,
        int LeadCount,
        DateTime CreatedAtUtc,
        string? CountryCode,
        string? CountryName
    );

    public record PaginatedTenantsResponse(
        List<TenantListItem> Items,
        int TotalCount,
        int Page,
        int PageSize,
        int TotalPages
    );

    // ==================== USER DTOs ====================

    public record UserDto(
        Guid Id,
        Guid TenantId,
        string FirstName,
        string LastName,
        string Email,
        string? Phone,
        string? Department,
        string? JobTitle,
        bool IsActive,
        bool IsTenantAdmin,
        DateTime? LastLoginUtc,
        DateTime CreatedAtUtc,
        List<string> Roles
    );

    public record CreateUserCommand(
        Guid TenantId,
        string FirstName,
        string LastName,
        string Email,
        string? Phone,
        string? Department,
        string? JobTitle,
        bool IsTenantAdmin,
        List<Guid>? RoleIds
    );

    public record UpdateUserCommand(
        Guid TenantId,
        Guid UserId,
        string FirstName,
        string LastName,
        string Email,
        string? Phone,
        string? Department,
        string? JobTitle,
        bool IsActive,
        bool IsTenantAdmin
    );

    public record UserListItem(
        Guid Id,
        string FullName,
        string Email,
        string? Phone,
        string? Department,
        string? JobTitle,
        bool IsActive,
        bool IsTenantAdmin,
        DateTime? LastLoginUtc,
        DateTime CreatedAtUtc,
        List<string> Roles
    );

    public record PaginatedUsersResponse(
        List<UserListItem> Items,
        int TotalCount,
        int Page,
        int PageSize,
        int TotalPages
    );

    public record UpdateUserStatusCommand(
        Guid TenantId,
        Guid UserId,
        bool IsActive
    );

    public record AssignUserRolesCommand(
        Guid TenantId,
        Guid UserId,
        List<Guid> RoleIds
    );

    // ==================== USER ROLE DTOs ====================

    public record UserRoleDto(
        Guid UserId,
        Guid RoleId,
        string RoleName,
        string RoleDisplayName,
        DateTime AssignedAtUtc
    );

    // ==================== STATISTICS DTOs ====================

    public record TenantStatsDto(
    int TotalUsers = 0,
    int TotalRoles = 0,
    int ActiveUsers = 0,
    int InactiveUsers = 0,
    int CustomRoles = 0,
    int TotalLeads = 0,
    int TotalDeals = 0
);

    public record UserStatsDto(
        int TotalUsers,
        int ActiveUsers,
        int InactiveUsers,
        int AdminUsers,
        int RecentLogins
    );

    // ==================== LOOKUP DTOs ====================
    
    public record TenantLookupDto(
        Guid Id,
        string Name
    );

    public record UserLookupDto(
        Guid Id,
        string FullName,
        string Email
    );

    // ✅ NEW: Timezone DTO
    public record TimezoneDto(
        string Value,
        string DisplayName
    );

    // ✅ NEW: Country from Countries table
    

    // ==================== BULK OPERATIONS ====================

    public record BulkUpdateUserStatusCommand(
        Guid TenantId,
        List<Guid> UserIds,
        bool IsActive
    );

    public record BulkOperationResult(
        int TotalCount,
        int SuccessCount,
        int ErrorCount,
        List<string> Errors
    );

    // ==================== ROLE SELECTION DTO ====================
    
    public record RoleSelectionDto(
        Guid Id,
        string DisplayName,
        bool IsSelected
    );
}
