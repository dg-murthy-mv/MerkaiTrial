// =====================================================================
// WhatsAppStatusController.cs
// Location: MerkaiTrial.WebApi/Controllers/WhatsAppStatusController.cs
//
// NEW FILE (046). One read-only endpoint: is WhatsApp configured HERE,
// and what does Meta say about our templates.
//
// ── WHY THIS ENDPOINT EXISTS ─────────────────────────────────────────
//
// The template screen runs in Admin.Web. The token lives in the WebApi.
// Two processes, two configurations — so the screen was reporting its
// OWN process's settings and saying "Off" while the API could send
// perfectly well. Admin.Web cannot answer the question, so it asks the
// process that can.
//
// ── WHY NOT A [Authorize(Policy = "...")] ATTRIBUTE ──────────────────
//
// Because PermissionHandler grants EVERY "Module.Action" policy to
// anyone holding IsTenantAdmin:
//
//     if (user.HasClaim(c => c.Type == "IsTenantAdmin" && c.Value == "true"))
//     { context.Succeed(requirement); return; }
//
// That bypass is right for tenant-scoped modules — a workspace admin can
// do anything inside their own workspace. It is exactly wrong here: this
// endpoint is about MadeeVision's WhatsApp account, and every tenant
// admin in every workspace would pass "Settings.Read" or any other
// policy I picked.
//
// Note the other half of that handler: a super admin does NOT bypass.
// The only claim that means "MadeeVision staff" is IsSuperAdmin, so the
// check below reads it directly. ApiTokenService copies that claim into
// the API token, which is what makes it available here at all.
// =====================================================================

using MerkaiTrial.Application.Security;
using MerkaiTrial.Application.Services.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace MerkaiTrial.WebApi.Controllers;

[ApiController]
[Route("api/whatsapp")]
[Authorize]   // authenticated; the super-admin check is below
public class WhatsAppStatusController : ControllerBase
{
    private readonly WhatsAppOptions _options;
    private readonly IWhatsAppTemplateDirectory _directory;
    private readonly ILogger<WhatsAppStatusController> _logger;

    public WhatsAppStatusController(
        IOptions<WhatsAppOptions> options,
        IWhatsAppTemplateDirectory directory,
        ILogger<WhatsAppStatusController> logger)
    {
        _options = options.Value;
        _directory = directory;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/whatsapp/status — super admin only.
    ///
    /// Answers two questions the template screen cannot answer itself:
    /// can this environment send, and what does Meta think of each
    /// template.
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(WhatsAppStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]   // not 403 — see below
    public async Task<IActionResult> GetStatus(CancellationToken ct)
    {
        if (!IsSuperAdmin())
        {
            // 404 rather than 403, deliberately. A tenant admin poking at
            // /api/whatsapp/status should not learn that MadeeVision has a
            // WhatsApp account, let alone that this endpoint exists.
            _logger.LogWarning(
                "Non-super-admin requested the WhatsApp status endpoint");

            return NotFound();
        }

        try
        {
            var directory = await _directory.GetAsync(ct);

            return Ok(new WhatsAppStatusDto(
                Configured: _options.IsConfigured,

                // The number, never the token. This response goes over the
                // wire to another process and into a page; there is no
                // version of this that should carry a credential.
                FromDisplayNumber: _options.FromDisplayNumber,
                PhoneNumberId: _options.PhoneNumberId,
                BusinessAccountId: _options.BusinessAccountId,

                DirectoryReachable: directory.Reachable,
                DirectoryError: directory.Error,
                Templates: directory.Templates.ToList()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read the WhatsApp status");

            return StatusCode(500, new { error = "Could not read the WhatsApp status." });
        }
    }

    /// <summary>
    /// MadeeVision staff, and nobody else.
    ///
    /// The claim TYPE comes from SignInService rather than a literal, so
    /// renaming it there cannot leave this check silently passing nobody —
    /// which would look like a broken page rather than a failed check.
    /// </summary>
    private bool IsSuperAdmin()
        => User.HasClaim(c =>
               c.Type == SignInService.ClaimIsSuperAdmin
               && string.Equals(c.Value, "true", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// What the template screen needs to tell the truth. No token, ever.
/// </summary>
public sealed record WhatsAppStatusDto(
    bool Configured,
    string? FromDisplayNumber,
    string? PhoneNumberId,
    string? BusinessAccountId,
    bool DirectoryReachable,
    string? DirectoryError,
    List<MetaTemplate> Templates);
