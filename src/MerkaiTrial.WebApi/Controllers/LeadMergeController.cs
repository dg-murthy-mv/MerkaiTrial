// =====================================================================
// LeadMergeController.cs
// Location: MerkaiTrial.WebApi/Controllers/LeadMergeController.cs
//
// NEW FILE (093). Duplicate leads: finding them, and merging two.
//
//   GET  /api/leads/duplicates?email=&phone=&name=&company=&excludeId=
//        leads.read. Only leads the caller can see are compared.
//   POST /api/leads/merge   { keepLeadId, mergeLeadId, takeFromOther }
//        leads.update AND leads.delete — a merge changes one lead and
//        deletes the other, so it needs both rights.
//
// Tenant and user come from the signed-in principal, never the request,
// as in LeadsImportController.
// =====================================================================

using MerkaiTrial.Application.Commands.Leads;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.WebApi.Controllers;

[ApiController]
[Route("api/leads")]
public class LeadMergeController : ControllerBase
{
    private readonly FindLeadDuplicatesHandler _find;
    private readonly MergeLeadsHandler _merge;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuthorizationService _auth;
    private readonly ILogger<LeadMergeController> _logger;

    public LeadMergeController(
        FindLeadDuplicatesHandler find,
        MergeLeadsHandler merge,
        ICurrentUserService currentUser,
        IAuthorizationService auth,
        ILogger<LeadMergeController> logger)
    {
        _find = find;
        _merge = merge;
        _currentUser = currentUser;
        _auth = auth;
        _logger = logger;
    }

    [HttpGet("duplicates")]
    [Authorize(Policy = "Leads.Read")]
    public async Task<ActionResult<List<LeadDuplicateDto>>> Duplicates(
        [FromQuery] string? email = null,
        [FromQuery] string? phone = null,
        [FromQuery] string? name = null,
        [FromQuery] string? company = null,
        [FromQuery] Guid? excludeId = null,
        CancellationToken ct = default)
    {
        try
        {
            var tenantId = _currentUser.GetCurrentTenantId();
            return Ok(await _find.Handle(new LeadDuplicateQuery(tenantId, email, phone, name, company, excludeId), ct));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to look for duplicate leads");
            return StatusCode(500, new { error = "Couldn't check for duplicates." });
        }
    }

    [HttpPost("merge")]
    [Authorize(Policy = "Leads.Update")]
    public async Task<ActionResult<MergeLeadsResultDto>> Merge([FromBody] MergeLeadsDto dto, CancellationToken ct = default)
    {
        // Update is on the attribute; delete is checked here, because a
        // merge removes the other lead and must not be a way round a role
        // that cannot delete leads.
        var canDelete = await _auth.AuthorizeAsync(User, "leads.delete");
        if (!canDelete.Succeeded)
            return StatusCode(403, new { error = "Merging removes one of the two leads, so it needs permission to delete leads." });

        try
        {
            var tenantId = _currentUser.GetCurrentTenantId();
            return Ok(await _merge.Handle(tenantId, dto, ct));
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = "One of those leads no longer exists." });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to merge lead {Other} into {Keep}", dto.MergeLeadId, dto.KeepLeadId);
            return StatusCode(500, new { error = "The merge didn't complete, and nothing was changed. Please try again." });
        }
    }
}
