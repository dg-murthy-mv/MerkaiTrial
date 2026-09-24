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
// PERMISSIONS (024 — this is the change)
//
//   READING is deals.read. Unchanged, and it must stay that way: every
//   deal page, kanban board and quote screen reads this endpoint to
//   render a stage picker. A rep who could not read it could not see a
//   deal.
//
//   WRITING is settings.*. It was deals.* — the same permission a rep
//   needs to edit their own deal — so any Sales Rep could POST to this
//   controller and rename, reorder, retire or recolour the tenant's
//   stages, and from 023 move every deal out of one in a single call.
//   That was my error: the header of this file used to argue that "the
//   pipeline shape is a sales-management decision, not a settings one",
//   which is true and is an argument for its OWN permission, not for
//   borrowing the one every rep already holds.
//
//   The nav link was hidden from reps by a casing typo in _Layout, so
//   this was never visible in the UI — but the endpoint was always open
//   to anyone who could reach it with a token.
//
//   move-deals is settings.update and NOT settings.delete. It deletes
//   nothing; it rewrites the stage of every deal in one stage, which is
//   the largest thing settings.update can be asked to do — and requiring
//   delete would mean a sales manager who may reshape the pipeline could
//   not empty a stage in order to do so.
// =====================================================================

using MerkaiTrial.Application.Authorization;
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
        => Run(Policies.DealsRead, async () =>
        {
            var tenantId = _currentUser.GetCurrentTenantId();
            return Ok(await _get.Handle(tenantId, activeOnly, detail, ct));
        }, "reading pipeline stages");

    [HttpPost]
    public Task<IActionResult> Create([FromBody] CreatePipelineStageDto dto, CancellationToken ct)
        => Run(Policies.SettingsUpdate, async () =>
        {
            var (tenantId, userId) = Identity();
            return Ok(await _create.Handle(dto with { TenantId = tenantId, CreatedBy = userId }, ct));
        }, "creating pipeline stage");

    [HttpPut("{id:guid}")]
    public Task<IActionResult> Update(Guid id, [FromBody] UpdatePipelineStageDto dto, CancellationToken ct)
        => Run(Policies.SettingsUpdate, async () =>
        {
            var (tenantId, userId) = Identity();
            await _update.Handle(dto with { TenantId = tenantId, StageId = id, UpdatedBy = userId }, ct);
            return Ok();
        }, "updating pipeline stage");

    [HttpPost("reorder")]
    public Task<IActionResult> Reorder([FromBody] ReorderPipelineStagesDto dto, CancellationToken ct)
        => Run(Policies.SettingsUpdate, async () =>
        {
            var (tenantId, userId) = Identity();
            await _reorder.Handle(dto with { TenantId = tenantId, UpdatedBy = userId }, ct);
            return Ok();
        }, "reordering pipeline stages");

    [HttpPost("{id:guid}/default")]
    public Task<IActionResult> SetDefault(Guid id, CancellationToken ct)
        => Run(Policies.SettingsUpdate, async () =>
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
        => Run(Policies.SettingsUpdate, async () =>
        {
            var (tenantId, userId) = Identity();
            var result = await _moveDeals.Handle(
                dto with { TenantId = tenantId, FromStageId = id, MovedBy = userId }, ct);
            return Ok(result);
        }, "moving deals between pipeline stages");

    [HttpDelete("{id:guid}")]
    public Task<IActionResult> Delete(Guid id, CancellationToken ct)
        => Run(Policies.SettingsDelete, async () =>
        {
            var tenantId = _currentUser.GetCurrentTenantId();
            await _delete.Handle(tenantId, id, ct);
            return Ok();
        }, "deleting pipeline stage");

    // =================================================================

    private (Guid TenantId, string UserId) Identity()
        => (_currentUser.GetCurrentTenantId(), _currentUser.GetCurrentUserId().ToString());

    /// <summary>
    /// 024: takes the whole policy name rather than an action appended to
    /// a hardcoded "Deals.". Reading and writing this controller are now
    /// two different modules, so there is no single prefix to assume —
    /// and a helper that builds the module name for you is how every
    /// endpoint in a file silently inherits the wrong one.
    /// </summary>
    private async Task<IActionResult> Run(string policy, Func<Task<IActionResult>> body, string what)
    {
        var allowed = await _auth.AuthorizeAsync(User, policy);
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
