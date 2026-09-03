// FILE: MerkaiTrial.Admin.Web/Authorization/PermissionRequirement.cs

using Microsoft.AspNetCore.Authorization;

namespace MerkaiTrial.Application.Authorization
{
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
