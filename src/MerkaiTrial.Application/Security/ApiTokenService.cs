// =====================================================================
// FILE: MerkaiTrial.Application/Security/ApiTokenService.cs
//
// STEP 2 CHANGE: the ViewingAs claim is now copied into the API token.
//
// WHY IT MATTERS: RoleScope (and every future tenant-scoped query) decides
// what a caller may see with:
//
//     isSuperAdmin && !isViewingAs   ->  sees all tenants
//
// The web app knows both facts, but only IsSuperAdmin was being copied into
// the token. So on the API side a super admin who was previewing a client's
// workspace still looked unrestricted, and the role list would show every
// tenant's roles inside a ViewAs session — the exact confusion ViewAs is
// meant to prevent.
//
// One line, marked below.
// =====================================================================

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace MerkaiTrial.Application.Security;

public interface IApiTokenService
{
    /// <summary>Mints a bearer token carrying the given principal's identity.
    /// The claims are copied from a session that was already authenticated —
    /// this never invents or elevates identity.</summary>
    string IssueForPrincipal(ClaimsPrincipal principal);
}

public class ApiTokenService : IApiTokenService
{
    private readonly SigningCredentials _credentials;
    private readonly string _issuer;
    private readonly string _audience;

    public ApiTokenService(IConfiguration config)
    {
        var secret = config["Jwt:SigningKey"]
            ?? throw new InvalidOperationException(
                "Jwt:SigningKey is not configured. Admin.Web cannot call the API without it.");

        if (Encoding.UTF8.GetByteCount(secret) < 32)
            throw new InvalidOperationException(
                "Jwt:SigningKey must be at least 32 bytes for HMAC-SHA256.");

        _issuer   = config["Jwt:Issuer"]   ?? "merkaitrial-admin";
        _audience = config["Jwt:Audience"] ?? "merkaitrial-api";

        _credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
            SecurityAlgorithms.HmacSha256);
    }

    public string IssueForPrincipal(ClaimsPrincipal principal)
    {
        var userId   = principal.FindFirst(SignInService.ClaimUserId)?.Value;
        var tenantId = principal.FindFirst(SignInService.ClaimTenantId)?.Value;

        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(tenantId))
            throw new UnauthorizedAccessException(
                "Cannot issue an API token: the current principal has no UserId/TenantId.");

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(ClaimTypes.NameIdentifier, userId),
            new(SignInService.ClaimUserId, userId),
            new(SignInService.ClaimTenantId, tenantId),
        };

        // Carry the authorization facts so the API does not re-query them.
        foreach (var type in new[]
                 {
                     SignInService.ClaimIsTenantAdmin,
                     SignInService.ClaimIsSuperAdmin,
                     SignInService.ClaimViewingAs,   // ← STEP 2: added
                     SignInService.ClaimPlan,
                     ClaimTypes.Email,
                 })
        {
            var value = principal.FindFirst(type)?.Value;
            if (!string.IsNullOrEmpty(value)) claims.Add(new Claim(type, value));
        }

        foreach (var perm in principal.FindAll(SignInService.ClaimPermission))
            claims.Add(new Claim(SignInService.ClaimPermission, perm.Value));

        var token = new JwtSecurityToken(
            issuer:             _issuer,
            audience:           _audience,
            claims:             claims,
            notBefore:          DateTime.UtcNow.AddSeconds(-30),  // clock-skew allowance
            expires:            DateTime.UtcNow.AddMinutes(5),
            signingCredentials: _credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
