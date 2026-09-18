// =====================================================================
// PipelineStagesController.cs
// Location: MerkaiTrial.WebApi/Controllers/PipelineStagesController.cs
//
// NEW FILE.
//
// PERMISSIONS: reading is deals.read — every deal page needs the stage
// list to render a picker. Changing them is deals.update, because the
// pipeline shape is a sales-management decision, not a settings one.
// =====================================================================

using MerkaiTrial.Application.Commands.PipelineStages;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.WebApi.Controllers;

[ApiController]
[Authorize]
[Route("api/pipeline-stages")]
public class PipelineStagesController : ControllerBase
{
    private readonly GetPipelineStagesHandler _get;
    private readonly CreatePipelineStageHandler _create;
    private readonly UpdatePipelineStageHandler _update;
    private readonly ReorderPipelineStagesHandler _reorder;
    private readonly SetDefaultPipelineStageHandler _setDefault;
    private readonly DeletePipelineStageHandler _delete;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuthorizationService _auth;
    private readonly ILogger<PipelineStagesController> _logger;

    public PipelineStagesController(
        GetPipelineStagesHandler get,
        CreatePipelineStageHandler create,
        UpdatePipelineStageHandler update,
        ReorderPipelineStagesHandler reorder,
        SetDefaultPipelineStageHandler setDefault,
        DeletePipelineStageHandler delete,
        ICurrentUserService currentUser,
        IAuthorizationService auth,
        ILogger<PipelineStagesController> logger)
    {
        _get         = get;
        _create      = create;
        _update      = update;
        _reorder     = reorder;
        _setDefault  = setDefault;
        _delete      = delete;
        _currentUser = currentUser;
        _auth        = auth;
        _logger      = logger;
    }

    [HttpGet]
    public Task<IActionResult> Get([FromQuery] bool activeOnly = false, CancellationToken ct = default)
        => Run("Read", async () =>
        {
            var tenantId = _currentUser.GetCurrentTenantId();
            return Ok(await _get.Handle(tenantId, activeOnly, ct));
        }, "reading pipeline stages");

    [HttpPost]
    public Task<IActionResult> Create([FromBody] CreatePipelineStageDto dto, CancellationToken ct)
        => Run("Update", async () =>
        {
            var (tenantId, userId) = Identity();
            return Ok(await _create.Handle(dto with { TenantId = tenantId, CreatedBy = userId }, ct));
        }, "creating pipeline stage");

    [HttpPut("{id:guid}")]
    public Task<IActionResult> Update(Guid id, [FromBody] UpdatePipelineStageDto dto, CancellationToken ct)
        => Run("Update", async () =>
        {
            var (tenantId, userId) = Identity();
            await _update.Handle(dto with { TenantId = tenantId, StageId = id, UpdatedBy = userId }, ct);
            return Ok();
        }, "updating pipeline stage");

    [HttpPost("reorder")]
    public Task<IActionResult> Reorder([FromBody] ReorderPipelineStagesDto dto, CancellationToken ct)
        => Run("Update", async () =>
        {
            var (tenantId, userId) = Identity();
            await _reorder.Handle(dto with { TenantId = tenantId, UpdatedBy = userId }, ct);
            return Ok();
        }, "reordering pipeline stages");

    [HttpPost("{id:guid}/default")]
    public Task<IActionResult> SetDefault(Guid id, CancellationToken ct)
        => Run("Update", async () =>
        {
            var (tenantId, userId) = Identity();
            await _setDefault.Handle(tenantId, id, userId, ct);
            return Ok();
        }, "setting default stage");

    [HttpDelete("{id:guid}")]
    public Task<IActionResult> Delete(Guid id, CancellationToken ct)
        => Run("Delete", async () =>
        {
            var tenantId = _currentUser.GetCurrentTenantId();
            await _delete.Handle(tenantId, id, ct);
            return Ok();
        }, "deleting pipeline stage");

    // =================================================================

    private (Guid TenantId, string UserId) Identity()
        => (_currentUser.GetCurrentTenantId(), _currentUser.GetCurrentUserId().ToString());

    private async Task<IActionResult> Run(string action, Func<Task<IActionResult>> body, string what)
    {
        var allowed = await _auth.AuthorizeAsync(User, $"Deals.{action}");
        if (!allowed.Succeeded) return Forbid();

        try
        {
            return await body();
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
