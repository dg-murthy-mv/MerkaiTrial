// FILE: MerkaiTrial.WebApi/Authorization/PermissionRequirement.cs

using Microsoft.AspNetCore.Authorization;

namespace MerkaiTrial.WebApi.Authorization
{
    // Requirement: User must have specific permission
    public class PermissionRequirement : IAuthorizationRequirement
    {
        public string Module { get; }
        public string Action { get; }

        public PermissionRequirement(string module, string action)
        {
            Module = module;
            Action = action;
        }
    }
}
