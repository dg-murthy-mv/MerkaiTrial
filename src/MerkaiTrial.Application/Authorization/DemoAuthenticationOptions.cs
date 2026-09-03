// FILE: MerkaiTrial.Admin.Web/Authorization/DemoAuthenticationOptions.cs

using Microsoft.AspNetCore.Authentication;

namespace MerkaiTrial.Application.Authorization
{
    public class DemoAuthenticationOptions : AuthenticationSchemeOptions
    {
        public string UserId { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public string UserName { get; set; } = "Demo User";
        public string Email { get; set; } = string.Empty;
        public bool IsSuperAdmin { get; set; } = false;
        public string[] Roles { get; set; } = Array.Empty<string>();
    }
}
