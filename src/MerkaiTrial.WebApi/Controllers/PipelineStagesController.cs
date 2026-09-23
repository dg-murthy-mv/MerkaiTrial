// =====================================================================
// PipelineStagesController.cs
// Location: MerkaiTrial.WebApi/Controllers/PipelineStagesController.cs
//
// COMPLETE FILE — replaces the existing one.
//
// CHANGES (023)
//   ✅ POST {id}/move-deals — empty a stage and optionally retire it.
//
//      It is a POST on the stage rather than a PUT on the deals because
//      the thing being changed is the stage's fate; the deals moving are
//      how that happens. It returns the result rather than 204 for the
//      same reason PUT deals/{id}/stage started returning one in 022: the
//      page has something true and useful to say afterwards — how many
//      moved, and whether the retire went through.
//
// PERMISSIONS: reading is deals.read — every deal page needs the stage
// list to render a picker. Changing them is deals.update, because the
// pipeline shape is a sales-management decision, not a settings one.
//
// move-deals is deliberately deals.update and NOT deals.delete. It
// deletes nothing; it rewrites the stage of every deal in one stage,
// which is the largest thing deals.update can be asked to do — and
// requiring deals.delete would mean a sales manager who may reshape the
// pipeline could not empty a stage in order to do so.
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
    private readonly MoveStageDealsHandler _moveDeals;
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
        MoveStageDealsHandler moveDeals,
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
        _moveDeals   = moveDeals;
        _delete      = delete;
        _currentUser = currentUser;
        _auth        = auth;
        _logger      = logger;
    }

    /// <param name="detail">
    /// Ways in/out and the delete/retire reasons. Costs two extra round
    /// trips, so only the settings page asks for it — every deal page,
    /// board and quote screen reads this endpoint for a picker and must
    /// not pay for numbers it will not display.
    /// </param>
    [HttpGet]
    public Task<IActionResult> Get(
        [FromQuery] bool activeOnly = false,
        [FromQuery] bool detail = false,
        CancellationToken ct = default)
        => Run("Read", async () =>
        {
            var tenantId = _currentUser.GetCurrentTenantId();
            return Ok(await _get.Handle(tenantId, activeOnly, detail, ct));
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

    /// <summary>
    /// Sends every deal in this stage to another stage of the same kind,
    /// and optionally retires this one afterwards.
    ///
    /// Returns MoveStageDealsResult so the page can say what actually
    /// happened. A caller that ignores the body is not wrong — the work
    /// is done by the time this responds.
    /// </summary>
    [HttpPost("{id:guid}/move-deals")]
    public Task<IActionResult> MoveDeals(Guid id, [FromBody] MoveStageDealsDto dto, CancellationToken ct)
        => Run("Update", async () =>
        {
            var (tenantId, userId) = Identity();
            var result = await _moveDeals.Handle(
                dto with { TenantId = tenantId, FromStageId = id, MovedBy = userId }, ct);
            return Ok(result);
        }, "moving deals between pipeline stages");

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
