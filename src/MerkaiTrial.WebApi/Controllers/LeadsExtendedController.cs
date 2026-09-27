// =====================================================================
// LEADS EXTENDED CONTROLLER
// Location: MerkaiTrial.WebApi/Controllers/LeadsExtendedController.cs
//
// COMPLETE FILE — replaces the existing one.
//
// CHANGES (041a)
//
//   1. ★ EVERY ENDPOINT HERE WAS OPEN TO ANY SIGNED-IN USER. There was
//      not one [Authorize] attribute in this file. The FallbackPolicy in
//      Program.cs meant a caller still had to be authenticated, so this
//      was never open to the public — but "authenticated" is not
//      "permitted". A user with read-only access to Leads could reassign
//      any lead they could see, add notes and activities to it, and
//      export the whole list to Excel. DealsController gates every
//      action (Deals.Read / Deals.Update / Deals.Delete); this file
//      gated none.
//
//      Each action now carries the policy its work implies:
//
//        Leads.Read    getting notes, activities, reminders, the
//                      timeline, and the Excel export
//        Leads.Update  creating notes, activities and reminders,
//                      deleting a note, completing a reminder, and
//                      assigning the lead
//
//      Nothing here is Leads.Create or Leads.Delete: none of these
//      endpoints create or delete a LEAD. Deleting a note is a change to
//      a lead, not the removal of one.
//
//      Policy names are the plain strings, as in DealsController. The
//      permission handler compares them case-insensitively.
//
//   2. catch (InvalidOperationException) on every write action, before
//      the generic catch, returning 400 { "error": ex.Message }.
//      DealsController has had this since 019. Without it the owner
//      checks added in 041 come back as a 500 and the page shows
//      "Failed to assign lead" instead of the reason — "Somchai's
//      account is deactivated, so it cannot be given new work."
//
//   3. The assign action's summary now says what the three cases mean,
//      because this endpoint is the one an integration is most likely to
//      call: omit ownerUserId and the owner is KEPT; send "" and the
//      lead is unassigned.
// =====================================================================

using MerkaiTrial.Application.Commands.Leads;
using MerkaiTrial.Application.DTOs;
using Microsoft.AspNetCore.Authorization;      // 041a
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.WebApi.Controllers
{
    [ApiController]
    [Route("api/leads")]
    public class LeadsExtendedController : ControllerBase
    {
        private readonly ILogger<LeadsExtendedController> _logger;

        // Notes
        private readonly CreateLeadNoteHandler _createNoteHandler;
        private readonly GetLeadNotesHandler _getNotesHandler;
        private readonly DeleteLeadNoteHandler _deleteNoteHandler;

        // Activities
        private readonly CreateLeadActivityHandler _createActivityHandler;
        private readonly GetLeadActivitiesHandler _getActivitiesHandler;

        // Reminders
        private readonly CreateLeadReminderHandler _createReminderHandler;
        private readonly GetLeadRemindersHandler _getRemindersHandler;
        private readonly CompleteReminderHandler _completeReminderHandler;

        // Assignment
        private readonly AssignLeadHandler _assignLeadHandler;

        // Timeline
        private readonly GetLeadTimelineHandler _getTimelineHandler;

        // Export/Import
        private readonly ExportLeadsHandler _exportHandler;


        public LeadsExtendedController(
            CreateLeadNoteHandler createNoteHandler,
            GetLeadNotesHandler getNotesHandler,
            DeleteLeadNoteHandler deleteNoteHandler,
            CreateLeadActivityHandler createActivityHandler,
            GetLeadActivitiesHandler getActivitiesHandler,
            CreateLeadReminderHandler createReminderHandler,
            GetLeadRemindersHandler getRemindersHandler,
            CompleteReminderHandler completeReminderHandler,
            AssignLeadHandler assignLeadHandler,
            GetLeadTimelineHandler getTimelineHandler,
            ExportLeadsHandler exportHandler,
            ILogger<LeadsExtendedController> logger)
        {
            _createNoteHandler = createNoteHandler;
            _getNotesHandler = getNotesHandler;
            _deleteNoteHandler = deleteNoteHandler;
            _createActivityHandler = createActivityHandler;
            _getActivitiesHandler = getActivitiesHandler;
            _createReminderHandler = createReminderHandler;
            _getRemindersHandler = getRemindersHandler;
            _completeReminderHandler = completeReminderHandler;
            _assignLeadHandler = assignLeadHandler;
            _getTimelineHandler = getTimelineHandler;
            _exportHandler = exportHandler;
            _logger = logger;
        }

        // ==================== NOTES ====================

        [HttpPost("{leadId:guid}/notes")]
        [Authorize(Policy = "Leads.Update")]                       // 041a
        public async Task<ActionResult<LeadNoteDto>> CreateNote(
            [FromRoute] Guid leadId,
            [FromBody] CreateLeadNoteDto dto)
        {
            try
            {
                if (leadId != dto.LeadId)
                    return BadRequest(new { error = "Lead ID mismatch" });

                var result = await _createNoteHandler.Handle(dto);
                return Ok(result);
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            // 041a. The handler's refusals are written to be read.
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create note for lead {LeadId}", leadId);
                return StatusCode(500, new { error = "Failed to create note" });
            }
        }

        [HttpGet("{leadId:guid}/notes")]
        [Authorize(Policy = "Leads.Read")]                         // 041a
        public async Task<ActionResult<List<LeadNoteDto>>> GetNotes(
            [FromRoute] Guid leadId,
            [FromQuery] Guid tenantId)
        {
            try
            {
                var result = await _getNotesHandler.Handle(tenantId, leadId);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get notes for lead {LeadId}", leadId);
                return StatusCode(500, new { error = "Failed to get notes" });
            }
        }

        [HttpDelete("notes/{noteId:guid}")]
        [Authorize(Policy = "Leads.Update")]                       // 041a
        public async Task<IActionResult> DeleteNote(
            [FromRoute] Guid noteId,
            [FromQuery] Guid tenantId)
        {
            try
            {
                await _deleteNoteHandler.Handle(tenantId, noteId);
                return NoContent();
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)                   // 041a
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete note {NoteId}", noteId);
                return StatusCode(500, new { error = "Failed to delete note" });
            }
        }

        // ==================== ACTIVITIES ====================

        [HttpPost("{leadId:guid}/activities")]
        [Authorize(Policy = "Leads.Update")]                       // 041a
        public async Task<ActionResult<LeadActivityDto>> CreateActivity(
            [FromRoute] Guid leadId,
            [FromBody] CreateLeadActivityDto dto)
        {
            try
            {
                if (leadId != dto.LeadId)
                    return BadRequest(new { error = "Lead ID mismatch" });

                var result = await _createActivityHandler.Handle(dto);
                return Ok(result);
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)                   // 041a
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create activity for lead {LeadId}", leadId);
                return StatusCode(500, new { error = "Failed to create activity" });
            }
        }

        [HttpGet("{leadId:guid}/activities")]
        [Authorize(Policy = "Leads.Read")]                         // 041a
        public async Task<ActionResult<List<LeadActivityDto>>> GetActivities(
            [FromRoute] Guid leadId,
            [FromQuery] Guid tenantId)
        {
            try
            {
                var result = await _getActivitiesHandler.Handle(tenantId, leadId);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get activities for lead {LeadId}", leadId);
                return StatusCode(500, new { error = "Failed to get activities" });
            }
        }

        // ==================== REMINDERS ====================

        [HttpPost("{leadId:guid}/reminders")]
        [Authorize(Policy = "Leads.Update")]                       // 041a
        public async Task<ActionResult<LeadReminderDto>> CreateReminder(
            [FromRoute] Guid leadId,
            [FromBody] CreateLeadReminderDto dto)
        {
            try
            {
                if (leadId != dto.LeadId)
                    return BadRequest(new { error = "Lead ID mismatch" });

                var result = await _createReminderHandler.Handle(dto);
                return Ok(result);
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)                   // 041a
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create reminder for lead {LeadId}", leadId);
                return StatusCode(500, new { error = "Failed to create reminder" });
            }
        }

        [HttpGet("{leadId:guid}/reminders")]
        [Authorize(Policy = "Leads.Read")]                         // 041a
        public async Task<ActionResult<List<LeadReminderDto>>> GetReminders(
            [FromRoute] Guid leadId,
            [FromQuery] Guid tenantId)
        {
            try
            {
                var result = await _getRemindersHandler.Handle(tenantId, leadId);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get reminders for lead {LeadId}", leadId);
                return StatusCode(500, new { error = "Failed to get reminders" });
            }
        }

        [HttpPatch("reminders/{reminderId:guid}/complete")]
        [Authorize(Policy = "Leads.Update")]                       // 041a
        public async Task<IActionResult> CompleteReminder(
            [FromRoute] Guid reminderId,
            [FromQuery] Guid tenantId)
        {
            try
            {
                await _completeReminderHandler.Handle(new CompleteReminderDto(tenantId, reminderId));
                return NoContent();
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)                   // 041a
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to complete reminder {ReminderId}", reminderId);
                return StatusCode(500, new { error = "Failed to complete reminder" });
            }
        }

        // ==================== ASSIGNMENT ====================

        /// <summary>
        /// PATCH /api/leads/{leadId}/assign — hand a lead to someone, or to
        /// nobody.
        ///
        /// 041: the three cases, which this endpoint is the most likely one
        /// to be called from a script:
        ///
        ///   ownerUserId omitted or null  → the owner is KEPT, not cleared
        ///   ownerUserId ""               → the lead is unassigned
        ///   ownerUserId "&lt;guid&gt;"   → assigned, after the user is
        ///                                  checked against this tenant's
        ///                                  active users
        ///
        /// Assigning to a user who is deactivated, deleted, or belongs to
        /// another workspace is a 400 with the reason, not a silent no-op.
        /// </summary>
        [HttpPatch("{leadId:guid}/assign")]
        [Authorize(Policy = "Leads.Update")]                       // 041a
        public async Task<IActionResult> AssignLead(
            [FromRoute] Guid leadId,
            [FromBody] AssignLeadDto dto)
        {
            try
            {
                if (leadId != dto.LeadId)
                    return BadRequest(new { error = "Lead ID mismatch" });

                await _assignLeadHandler.Handle(dto);
                return NoContent();
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            // 041a. THE ONE THAT MATTERS MOST IN THIS FILE. Without it,
            // "that user is deactivated" reached the page as a 500 and the
            // person was told "Failed to assign lead" — which reads like
            // our bug, not their choice.
            catch (InvalidOperationException ex)
            {
                _logger.LogInformation(
                    "Assign lead {LeadId} refused: {Message}", leadId, ex.Message);

                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to assign lead {LeadId}", leadId);
                return StatusCode(500, new { error = "Failed to assign lead" });
            }
        }

        // ==================== TIMELINE ====================

        [HttpGet("{leadId:guid}/timeline")]
        [Authorize(Policy = "Leads.Read")]                         // 041a
        public async Task<ActionResult<List<TimelineItemDto>>> GetTimeline(
            [FromRoute] Guid leadId,
            [FromQuery] Guid tenantId)
        {
            try
            {
                var result = await _getTimelineHandler.Handle(tenantId, leadId);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get timeline for lead {LeadId}", leadId);
                return StatusCode(500, new { error = "Failed to get timeline" });
            }
        }

        // ==================== EXPORT ====================

        // 041a. Leads.Read, not "any signed-in user". This endpoint hands
        // back every lead the caller can see as an Excel file — the single
        // most useful thing in the product to walk out of the door with.
        [HttpGet("export")]
        [Authorize(Policy = "Leads.Read")]
        public async Task<IActionResult> ExportLeads(
            [FromQuery] Guid tenantId,
            [FromQuery] string? search = null,
            [FromQuery] string? status = null,
            [FromQuery] string? assignedTo = null)
        {
            try
            {
                var request = new ExportLeadsRequest(tenantId, search, status, assignedTo);
                var fileBytes = await _exportHandler.Handle(request);

                var fileName = $"Leads_Export_{DateTime.UtcNow:yyyyMMdd_HHmmss}.xlsx";
                return File(fileBytes,
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to export leads");
                return StatusCode(500, new { error = "Failed to export leads" });
            }
        }

    }
}
