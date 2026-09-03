// =====================================================================
// ActivitiesController.cs
// Location: MerkaiTrial.WebAPI/Controllers/ActivitiesController.cs
//
// Follows same pattern as LeadsController.
// Receives calls from ActivityService (Web layer) → routes to handlers.
// =====================================================================

using MerkaiTrial.Application.Commands.Activities;
using Microsoft.AspNetCore.Mvc;
using MerkaiTrial.Application.DTOs;
namespace MerkaiTrial.WebAPI.Controllers;

[ApiController]
[Route("api/activities")]
public class ActivitiesController : ControllerBase
{
    private readonly CreateActivityHandler      _create;
    private readonly GetActivitiesHandler       _getForEntity;
    private readonly GetUpcomingTasksHandler    _getUpcoming;
    private readonly CompleteActivityHandler    _complete;
    private readonly UpdateActivityHandler      _update;
    private readonly DeleteActivityHandler      _delete;
    private readonly ILogger<ActivitiesController> _logger;

    public ActivitiesController(
        CreateActivityHandler      create,
        GetActivitiesHandler       getForEntity,
        GetUpcomingTasksHandler    getUpcoming,
        CompleteActivityHandler    complete,
        UpdateActivityHandler      update,
        DeleteActivityHandler      delete,
        ILogger<ActivitiesController> logger)
    {
        _create      = create;
        _getForEntity = getForEntity;
        _getUpcoming = getUpcoming;
        _complete    = complete;
        _update      = update;
        _delete      = delete;
        _logger      = logger;
    }

    // POST api/activities
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateActivityDto dto, CancellationToken ct)
    {
        try
        {
            var result = await _create.Handle(dto, ct);
            return Ok(result);
        }
        catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating activity");
            return StatusCode(500, "An error occurred");
        }
    }

    // GET api/activities?tenantId=...&entityType=Lead&entityId=...
    [HttpGet]
    public async Task<IActionResult> GetForEntity(
        [FromQuery] Guid   tenantId,
        [FromQuery] string entityType,
        [FromQuery] Guid   entityId,
        [FromQuery] bool   includeCompleted = true,
        [FromQuery] bool   tasksOnly        = false,
        CancellationToken  ct               = default)
    {
        try
        {
            var result = await _getForEntity.Handle(
                new GetActivitiesQuery(tenantId, entityType, entityId, includeCompleted, tasksOnly), ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting activities");
            return StatusCode(500, "An error occurred");
        }
    }

    // GET api/activities/upcoming-tasks?tenantId=...&daysAhead=7
    [HttpGet("upcoming-tasks")]
    public async Task<IActionResult> GetUpcomingTasks(
        [FromQuery] Guid    tenantId,
        [FromQuery] int     daysAhead        = 7,
        [FromQuery] string? assignedToUserId = null,
        CancellationToken   ct               = default)
    {
        try
        {
            var result = await _getUpcoming.Handle(
                new GetUpcomingTasksQuery(tenantId, assignedToUserId, daysAhead), ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting upcoming tasks");
            return StatusCode(500, "An error occurred");
        }
    }

    // POST api/activities/{id}/complete
    [HttpPost("{id:guid}/complete")]
    public async Task<IActionResult> Complete(Guid id, [FromBody] CompleteActivityDto dto, CancellationToken ct)
    {
        try
        {
            await _complete.Handle(dto with { ActivityId = id }, ct);
            return Ok();
        }
        catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error completing activity {Id}", id);
            return StatusCode(500, "An error occurred");
        }
    }

    // PUT api/activities/{id}
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateActivityDto dto, CancellationToken ct)
    {
        try
        {
            await _update.Handle(dto with { ActivityId = id }, ct);
            return Ok();
        }
        catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating activity {Id}", id);
            return StatusCode(500, "An error occurred");
        }
    }

    // DELETE api/activities/{id}?tenantId=...
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, [FromQuery] Guid tenantId, CancellationToken ct)
    {
        try
        {
            await _delete.Handle(tenantId, id, ct);
            return Ok();
        }
        catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting activity {Id}", id);
            return StatusCode(500, "An error occurred");
        }
    }
}
