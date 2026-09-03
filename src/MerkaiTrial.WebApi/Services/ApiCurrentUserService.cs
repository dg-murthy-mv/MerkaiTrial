// =====================================================================
// ApiCurrentUserService.cs
// Location: MerkaiTrial.WebApi/Services/ApiCurrentUserService.cs
//
// ROOT-CAUSE FIX (this pass):
//   This was the ONLY remaining piece of the app still resolving identity
//   from IConfiguration's "DevelopmentTenantContext" instead of
//   HttpContext.User claims. ICurrentTenantService (shared, Application
//   project) and Admin.Web's own ICurrentUserService were both already
//   fixed to be claims-based — this file was the one place still bypassing
//   ViewAs entirely, always resolving to whatever tenant/user is hardcoded
//   in appsettings.json regardless of who's actually logged in or being
//   viewed-as. That's why LeadsController (which trusts this service
//   server-side, unlike Deals/Quotes which take an explicit tenantId query
//   param as a workaround) was silently querying the wrong tenant.
//
//   Now mirrors CurrentTenantService.GetTenantId()'s exact pattern: read
//   "TenantId" / ClaimTypes.NameIdentifier claims from HttpContext.User,
//   which whatever WebApi-side auth handler consumes the forwarded
//   X-Tenant-Id/X-User-Id headers already populates correctly — proven by
//   CurrentTenantService's tenant resolution having been correct all along
//   in every log we've looked at.
//
//   IConfiguration is no longer used for identity at all. If you need a
//   direct-to-WebApi dev/testing path with no Admin.Web client in front
//   (e.g. hitting Swagger directly), that needs its own explicit decision
//   — silently falling back to a hardcoded tenant is exactly the bug this
//   fix removes, so it should not be reintroduced here.
// =====================================================================

using MerkaiTrial.Application.Services;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace MerkaiTrial.WebApi.Services
{
    public class ApiCurrentUserService : ICurrentUserService
    {
        private readonly IHttpContextAccessor _http;
        private readonly FlowDbContext _db;
        private CurrentUserContext? _cachedUser;

        public ApiCurrentUserService(
            IHttpContextAccessor httpContextAccessor,
            FlowDbContext db)
        {
            _http = httpContextAccessor;
            _db = db;
        }

        public Guid GetCurrentUserId()
        {
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

        public async Task<CurrentUserContext> GetCurrentUserAsync()
        {
            if (_cachedUser != null)
                return _cachedUser;

            var userId   = GetCurrentUserId();
            var tenantId = GetCurrentTenantId();

            var user = await _db.Users
                .Include(u => u.UserRoles)
                    .ThenInclude(ur => ur.Role)
                .FirstOrDefaultAsync(u =>
                    u.Id       == userId   &&
                    u.TenantId == tenantId &&
                    !u.IsDeleted);

            if (user == null)
                throw new UnauthorizedAccessException(
                    $"User {userId} not found in tenant {tenantId}.");

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
                catch (System.Text.Json.JsonException) { /* skip bad JSON */ }
            }

            _cachedUser = new CurrentUserContext(
                user.Id,
                user.TenantId,
                user.FirstName,
                user.LastName,
                user.Email,
                user.IsTenantAdmin,
                permissions
            );

            return _cachedUser;
        }
    }
}
