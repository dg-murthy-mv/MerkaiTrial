// =====================================================================
// DevelopmentTenantContext.cs
// Location: MerkaiTrial.Application/Configuration/DevelopmentTenantContext.cs
// Mirror of POSFirst DevelopmentUserContext — dev-only bypass for JWT
// =====================================================================

namespace MerkaiTrial.Application.Configuration;

public class DevelopmentTenantContext
{
    public const string SectionName = "DevelopmentTenantContext";

    /// <summary>Set true in dev, false in production</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Current user ID for dev testing</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Tenant ID for dev testing</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Display name for dev testing</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>Email for dev testing</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Roles for dev testing</summary>
    public List<string> Roles { get; set; } = new();
}
