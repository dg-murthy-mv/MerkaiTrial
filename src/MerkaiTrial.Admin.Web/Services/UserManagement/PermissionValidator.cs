// File: MerkaiTrial.Admin.Web/Services/UserManagement/PermissionValidator.cs
using MerkaiTrial.Application.Services;

namespace MerkaiTrial.Admin.Web.Services.UserManagement
{
    public interface IPermissionValidator
    {
        Task ValidatePermissionAsync(string module, string action);
        Task<CurrentUserContext> GetCurrentUserAsync();
    }

    public class PermissionValidator : IPermissionValidator
    {
        private readonly ICurrentUserService _currentUserService;

        public PermissionValidator(ICurrentUserService currentUserService)
        {
            _currentUserService = currentUserService;
        }

        public async Task<CurrentUserContext> GetCurrentUserAsync()
        {
            return await _currentUserService.GetCurrentUserAsync();
        }

        public async Task ValidatePermissionAsync(string module, string action)
        {
            var user = await _currentUserService.GetCurrentUserAsync();

            if (!user.HasPermission(module, action))
            {
                throw new UnauthorizedAccessException(
                    $"User {user.FullName} does not have '{action}' permission for '{module}' module"
                );
            }
        }
    }
}