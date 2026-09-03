// =====================================================================
// CurrentUserService.cs
// Location: MerkaiTrial.Admin.Web/Services/UserManagement/CurrentUserService.cs
//
//   - Resolves identity from HttpContext.User claims, which
//     DemoAuthenticationHandler always keeps correct (ViewAs cookie/header
//     override, or the appsettings DevelopmentTenantContext default when
//     no override is active — that fallback lives in the auth handler now,
//     not duplicated here).
//   - DB lookup for user + roles + permissions
//   - Per-request cache (_cachedUser)
// =====================================================================

using MerkaiTrial.Application.Services;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.Json;

namespace MerkaiTrial.Admin.Web.Services.UserManagement;

public class CurrentUserService : ICurrentUserService
{
    private readonly IHttpContextAccessor _http;
    private readonly FlowDbContext _db;
    private readonly ILogger<CurrentUserService> _logger;

    private CurrentUserContext? _cachedUser;

    public CurrentUserService(
        IHttpContextAccessor httpContextAccessor,
        FlowDbContext db,
        ILogger<CurrentUserService> logger)
    {
        _http = httpContextAccessor;
        _db = db;
        _logger = logger;
    }

    // ── IDENTITY ──────────────────────────────────────────────────────

    public Guid GetCurrentUserId()
    {
        // Previously short-circuited to _devCtx.Value.UserId whenever
        // DevelopmentTenantContext.Enabled=true, bypassing the ViewAs
        // cookie/header override entirely — same bug as CurrentTenantService
        // had. DemoAuthenticationHandler already resolves the correct value
        // (ViewAs override, or the appsettings default when no override is
        // active) and puts it in the claim, so there's no need for a second,
        // independent fallback here.
        var claim = _http.HttpContext?.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (Guid.TryParse(claim, out var id)) return id;
        throw new UnauthorizedAccessException("UserId claim missing from authenticated user");
    }

    public Guid GetCurrentTenantId()
    {
        var claim = _http.HttpContext?.User?.FindFirst("TenantId")?.Value;
        if (Guid.TryParse(claim, out var id)) return id;
        throw new UnauthorizedAccessException("TenantId claim missing from authenticated user");
    }

    // ── FULL CONTEXT (with DB lookup + permissions) ───────────────────

    public async Task<CurrentUserContext> GetCurrentUserAsync()
    {
        if (_cachedUser != null)
            return _cachedUser;

        var userId = GetCurrentUserId();
        var tenantId = GetCurrentTenantId();

        var user = await _db.Users
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u =>
                u.Id == userId &&
                u.TenantId == tenantId &&
                !u.IsDeleted);

        if (user == null)
        {
            _logger.LogError(
                "User {UserId} not found in tenant {TenantId}. " +
                "Run seed script if in dev mode.",
                userId, tenantId);
            throw new UnauthorizedAccessException(
                $"User {userId} not found in tenant {tenantId}");
        }

        // Build permissions from roles
        var permissions = new Dictionary<string, List<string>>();
        foreach (var userRole in user.UserRoles)
        {
            if (userRole.Role == null || string.IsNullOrEmpty(userRole.Role.Permissions))
                continue;

            try
            {
                var rolePerms = System.Text.Json.JsonSerializer
                    .Deserialize<Dictionary<string, List<string>>>(userRole.Role.Permissions);

                if (rolePerms == null) continue;

                foreach (var kvp in rolePerms)
                {
                    if (!permissions.ContainsKey(kvp.Key))
                        permissions[kvp.Key] = new List<string>();
                    permissions[kvp.Key].AddRange(kvp.Value.Except(permissions[kvp.Key]));
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Invalid Permissions JSON on role \"{RoleId}\"", userRole.Role.Id);

                // If this is a TenantAdmin role, still grant all permissions
                // so a bad JSON column doesn't lock admins out of the system.
                // The real fix is to correct the JSON in the DB (see Fix_Role_Permissions.sql).
                if (user.IsTenantAdmin)
                {
                    _logger.LogWarning("TenantAdmin role {RoleId} has corrupt Permissions JSON — granting full access as fallback.", userRole.Role.Id);
                    permissions = GetAllModulePermissions();  // see helper below
                }
            }
        }



        _cachedUser = new CurrentUserContext(
            UserId: user.Id,
            TenantId: user.TenantId,
            FirstName: user.FirstName,
            LastName: user.LastName,
            Email: user.Email,
            IsTenantAdmin: user.IsTenantAdmin,
            Permissions: permissions
        );

        _logger.LogDebug("CurrentUserService: loaded {Email} / tenant {TenantId}",
            user.Email, user.TenantId);

        return _cachedUser;
    }

    private static Dictionary<string, List<string>> GetAllModulePermissions()
    {
        var allActions = new List<string> { "read", "create", "update", "delete" };
        return new Dictionary<string, List<string>>
        {
            ["Leads"] = allActions,
            ["Deals"] = allActions,
            ["Contacts"] = allActions,
            ["Products"] = allActions,
            ["Users"] = allActions,
            ["Roles"] = allActions,
            ["Tenants"] = allActions,
            ["Reports"] = new List<string> { "read" },
            ["Settings"] = new List<string> { "read", "update" },
        };
    }
}
