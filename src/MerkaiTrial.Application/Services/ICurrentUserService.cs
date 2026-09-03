using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MerkaiTrial.Application.Services
{
    public interface ICurrentUserService
    {
        Task<CurrentUserContext> GetCurrentUserAsync();
        Guid GetCurrentUserId();
        Guid GetCurrentTenantId();
    }

    public record CurrentUserContext(
        Guid UserId,
        Guid TenantId,
        string FirstName,
        string LastName,
        string Email,
        bool IsTenantAdmin,
        Dictionary<string, List<string>> Permissions)
    {
        public string FullName => $"{FirstName} {LastName}".Trim();

        public bool HasPermission(string module, string action)
        {
            if (IsTenantAdmin) return true;
            if (!Permissions.ContainsKey(module)) return false;
            return Permissions[module].Contains(action);
        }

        public bool CanCreate(string module) => HasPermission(module, "create");
        public bool CanRead(string module) => HasPermission(module, "read");
        public bool CanUpdate(string module) => HasPermission(module, "update");
        public bool CanDelete(string module) => HasPermission(module, "delete");
    }

}
