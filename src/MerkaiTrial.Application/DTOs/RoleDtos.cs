// =====================================================================
// ROLE DTOs - Updated with Pagination
// Location: MerkaiTrial.Application/DTOs/RoleDtos.cs
// =====================================================================

using System;
using System.Collections.Generic;

namespace MerkaiTrial.Application.DTOs
{
    // ==================== LIST ITEM ====================
    public record RoleListItem(
        Guid Id,
        string Name,
        string DisplayName,
        string? Description,
        bool IsSystemRole,
        int UserCount
    );

    // ==================== PAGINATED RESPONSE ====================
    public record PaginatedRolesResponse(
        List<RoleListItem> Items,
        int TotalCount,
        int Page,
        int PageSize
    )
    {
        public int TotalPages => (int)Math.Ceiling(TotalCount / (double)PageSize);
        public bool HasPrevious => Page > 1;
        public bool HasNext => Page < TotalPages;
    }

    // ==================== DETAIL DTO ====================
    public record RoleDto(
        Guid Id,
        string Name,
        string DisplayName,
        string? Description,
        bool IsSystemRole,
        string? Permissions,
        DateTime CreatedAtUtc,
        int UserCount
    );

    // ==================== LOOKUP DTO ====================
    public record RoleLookupDto(
        Guid Id,
        string Name,
        string DisplayName
    );

    public record SalesTeamMemberDto(
      Guid Id,
      string FullName,
      string Email,
      string? JobTitle
  );


    // ==================== CREATE COMMAND ====================
    public record CreateRoleCommand(
        string Name,
        string DisplayName,
        string? Description,
        string? Permissions
    );

    // ==================== UPDATE COMMAND ====================
    public record UpdateRoleCommand(
        Guid RoleId,
        string Name,
        string DisplayName,
        string? Description,
        string? Permissions
    );

    // ==================== ROLE STATS ====================
    public record RoleStatsDto(
        int TotalRoles,
        int SystemRoles,
        int CustomRoles,
        int TotalUsers
    );
}
