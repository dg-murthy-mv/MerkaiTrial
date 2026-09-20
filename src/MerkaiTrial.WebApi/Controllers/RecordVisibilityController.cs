// =====================================================================
// RecordVisibilityController.cs
// Location: MerkaiTrial.WebApi/Controllers/RecordVisibilityController.cs
//
// NEW FILE.
//
//   GET    api/record-visibility/scopes            Roles.Read
//   PUT    api/record-visibility/scopes            Roles.Update
//   GET    api/record-visibility/teams             Roles.Read
//   POST   api/record-visibility/teams             Roles.Update
//   PUT    api/record-visibility/teams/{id}        Roles.Update
//   DELETE api/record-visibility/teams/{id}        Roles.Update
//   PUT    api/record-visibility/teams/members     Roles.Update
//   PUT    api/record-visibility/teams/managers    Roles.Update
//
// PERMISSIONS: Roles.*, because "who sees what" is part of the same
// decision as "who can do what" — it lives next to Roles & Permissions.
// Tenant id always comes from the signed-in user, never the request.
// =====================================================================

using MerkaiTrial.Application.Commands.RecordVisibility;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.WebApi.Controllers;

[ApiController]
[Authorize]
[Route("api/record-visibility")]
public class RecordVisibilityController : ControllerBase
{
    private readonly GetRecordScopeMatrixHandler _getMatrix;
    private readonly SetRecordScopeHandler _setScope;
    private readonly GetTeamsHandler _getTeams;
    private readonly CreateTeamHandler _createTeam;
    private readonly UpdateTeamHandler _updateTeam;
    private readonly DeleteTeamHandler _deleteTeam;
    private readonly SetUserTeamHandler _setUserTeam;
    private readonly SetUserManagedTeamsHandler _setManagedTeams;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuthorizationService _auth;
    private readonly ILogger<RecordVisibilityController> _logger;

    public RecordVisibilityController(
        GetRecordScopeMatrixHandler getMatrix,
        SetRecordScopeHandler setScope,
        GetTeamsHandler getTeams,
        CreateTeamHandler createTeam,
        UpdateTeamHandler updateTeam,
        DeleteTeamHandler deleteTeam,
        SetUserTeamHandler setUserTeam,
        SetUserManagedTeamsHandler setManagedTeams,
        ICurrentUserService currentUser,
        IAuthorizationService auth,
        ILogger<RecordVisibilityController> logger)
    {
        _getMatrix = getMatrix;
        _setScope = setScope;
        _getTeams = getTeams;
        _createTeam = createTeam;
        _updateTeam = updateTeam;
        _deleteTeam = deleteTeam;
        _setUserTeam = setUserTeam;
        _setManagedTeams = setManagedTeams;
        _currentUser = currentUser;
        _auth = auth;
        _logger = logger;
    }

    // ── Scopes ────────────────────────────────────────────────────────

    [HttpGet("scopes")]
    public Task<IActionResult> GetScopes(CancellationToken ct)
        => Run("Read", async () => Ok(await _getMatrix.Handle(TenantId(), ct)), "reading record scopes");

    [HttpPut("scopes")]
    public Task<IActionResult> SetScope([FromBody] SetRecordScopeDto dto, CancellationToken ct)
        => Run("Update", async () =>
        {
            await _setScope.Handle(dto with { TenantId = TenantId(), UpdatedBy = UserId() }, ct);
            return Ok();
        }, "setting record scope");

    // ── Teams ─────────────────────────────────────────────────────────

    [HttpGet("teams")]
    public Task<IActionResult> GetTeams(CancellationToken ct)
        => Run("Read", async () => Ok(await _getTeams.Handle(TenantId(), ct)), "reading teams");

    [HttpPost("teams")]
    public Task<IActionResult> CreateTeam([FromBody] CreateTeamDto dto, CancellationToken ct)
        => Run("Update", async () =>
            Ok(await _createTeam.Handle(dto with { TenantId = TenantId(), CreatedBy = UserId() }, ct)),
            "creating team");

    [HttpPut("teams/{id:guid}")]
    public Task<IActionResult> UpdateTeam(Guid id, [FromBody] UpdateTeamDto dto, CancellationToken ct)
        => Run("Update", async () =>
        {
            await _updateTeam.Handle(dto with { TenantId = TenantId(), TeamId = id, UpdatedBy = UserId() }, ct);
            return Ok();
        }, "updating team");

    [HttpDelete("teams/{id:guid}")]
    public Task<IActionResult> DeleteTeam(Guid id, CancellationToken ct)
        => Run("Update", async () =>
        {
            await _deleteTeam.Handle(TenantId(), id, ct);
            return Ok();
        }, "deleting team");

    [HttpPut("teams/members")]
    public Task<IActionResult> SetUserTeam([FromBody] SetUserTeamDto dto, CancellationToken ct)
        => Run("Update", async () =>
        {
            await _setUserTeam.Handle(dto with { TenantId = TenantId() }, ct);
            return Ok();
        }, "setting user team");

    [HttpPut("teams/managers")]
    public Task<IActionResult> SetManagedTeams([FromBody] SetUserManagedTeamsDto dto, CancellationToken ct)
        => Run("Update", async () =>
        {
            await _setManagedTeams.Handle(dto with { TenantId = TenantId(), UpdatedBy = UserId() }, ct);
            return Ok();
        }, "setting managed teams");

    // =================================================================

    private Guid TenantId() => _currentUser.GetCurrentTenantId();
    private string UserId() => _currentUser.GetCurrentUserId().ToString();

    private async Task<IActionResult> Run(string action, Func<Task<IActionResult>> body, string what)
    {
        var allowed = await _auth.AuthorizeAsync(User, $"Roles.{action}");
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
