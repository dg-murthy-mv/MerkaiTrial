// =====================================================================
// LeadsImportController.cs
// Location: MerkaiTrial.WebApi/Controllers/LeadsImportController.cs
//
// NEW FILE. Replaces the import endpoint on LeadsExtendedController —
// delete that one (see PATCHES.md).
//
// Tenant and user come from the signed-in principal, never the query
// string. Import creates records, so it needs leads.create.
//
// 092 — CUSTOM FIELDS
//   • Upload is async now (it reads the workspace's custom fields to
//     suggest columns for them) and returns the full field list.
//   • GET fields: the same list on its own, for a mapping screen redrawn
//     after a failed check.
//   • Preview and Commit stamp the request with the WORKSPACE's date
//     format, the way they already stamp the tenant and the importer — a
//     date format sent by the browser is ignored. Custom date columns are
//     read in that format first.
// =====================================================================

using MerkaiTrial.Application.Commands.Leads.Import;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.WebApi.Controllers;

[ApiController]
[Authorize]
[Route("api/leads/import")]
public class LeadsImportController : ControllerBase
{
    private readonly UploadLeadImportHandler _upload;
    private readonly PreviewLeadImportHandler _preview;
    private readonly CommitLeadImportHandler _commit;
    private readonly GetLeadImportFieldsHandler _fields;     // 092
    private readonly ICurrentUserService _currentUser;
    private readonly ICurrentTenantService _tenant;
    private readonly IAuthorizationService _auth;
    private readonly ILogger<LeadsImportController> _logger;

    public LeadsImportController(
        UploadLeadImportHandler upload,
        PreviewLeadImportHandler preview,
        CommitLeadImportHandler commit,
        GetLeadImportFieldsHandler fields,
        ICurrentUserService currentUser,
        ICurrentTenantService tenant,
        IAuthorizationService auth,
        ILogger<LeadsImportController> logger)
    {
        _upload      = upload;
        _preview     = preview;
        _commit      = commit;
        _fields      = fields;
        _currentUser = currentUser;
        _tenant      = tenant;
        _auth        = auth;
        _logger      = logger;
    }

    /// <summary>Step 1 — parse the file and suggest a column mapping.</summary>
    [HttpPost("upload")]
    [RequestSizeLimit(LeadFileParser.MaxFileBytes)]
    [Consumes("multipart/form-data")]
    public Task<IActionResult> Upload(IFormFile file, CancellationToken ct)
        => Run(async () =>
        {
            if (file is null || file.Length == 0)
                return BadRequest(new { error = "Choose a file to import." });

            var tenantId = _currentUser.GetCurrentTenantId();

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);

            var result = await _upload.Handle(tenantId, ms.ToArray(), file.FileName, _tenant.GetDateFormat(), ct);
            return Ok(result);
        }, "uploading import file");

    /// <summary>092 — every field a column can be mapped onto: built-in, then custom.</summary>
    [HttpGet("fields")]
    public Task<IActionResult> Fields(CancellationToken ct)
        => Run(async () =>
        {
            var tenantId = _currentUser.GetCurrentTenantId();
            var result = await _fields.Handle(tenantId, _tenant.GetDateFormat(), ct);
            return Ok(result);
        }, "listing import fields");

    /// <summary>Step 2 — validate every row and report what will happen.</summary>
    [HttpPost("preview")]
    public Task<IActionResult> Preview([FromBody] ImportRequest request, CancellationToken ct)
        => Run(async () =>
        {
            var tenantId = _currentUser.GetCurrentTenantId();
            var result = await _preview.Handle(
                request with { TenantId = tenantId, DateFormat = _tenant.GetDateFormat() },
                _tenant.GetCurrencyCode(), _tenant.GetCountryCode(), ct);
            return Ok(result);
        }, "previewing import");

    /// <summary>Step 3 — write the leads, in one transaction.</summary>
    [HttpPost("commit")]
    public Task<IActionResult> Commit([FromBody] ImportRequest request, CancellationToken ct)
        => Run(async () =>
        {
            var tenantId = _currentUser.GetCurrentTenantId();
            var userId   = _currentUser.GetCurrentUserId().ToString();

            var result = await _commit.Handle(
                request with { TenantId = tenantId, ImportedBy = userId, DateFormat = _tenant.GetDateFormat() },
                _tenant.GetCurrencyCode(), _tenant.GetCountryCode(), ct);
            return Ok(result);
        }, "committing import");

    // =================================================================

    private async Task<IActionResult> Run(Func<Task<IActionResult>> action, string what)
    {
        var allowed = await _auth.AuthorizeAsync(User, "leads.create");
        if (!allowed.Succeeded) return Forbid();

        try
        {
            return await action();
        }
        catch (KeyNotFoundException ex)      { return NotFound(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (UnauthorizedAccessException)  { return Unauthorized(); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error {What}", what);
            return StatusCode(500, new { error = "Something went wrong. Please try again." });
        }
    }
}
