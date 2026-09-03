// =====================================================================
// UPDATED LEADS EXTENDED CONTROLLER (Remove GetStats)
// Location: MerkaiTrial.WebApi/Controllers/LeadsExtendedController.cs
// =====================================================================

using MerkaiTrial.Application.Commands.Leads;
using MerkaiTrial.Application.DTOs;
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
        private readonly ImportLeadsHandler _importHandler;

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
            ImportLeadsHandler importHandler,
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
            _importHandler = importHandler;
            _logger = logger;
        }

        // ==================== NOTES ====================

        [HttpPost("{leadId:guid}/notes")]
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create note for lead {LeadId}", leadId);
                return StatusCode(500, new { error = "Failed to create note" });
            }
        }

        [HttpGet("{leadId:guid}/notes")]
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete note {NoteId}", noteId);
                return StatusCode(500, new { error = "Failed to delete note" });
            }
        }

        // ==================== ACTIVITIES ====================

        [HttpPost("{leadId:guid}/activities")]
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create activity for lead {LeadId}", leadId);
                return StatusCode(500, new { error = "Failed to create activity" });
            }
        }

        [HttpGet("{leadId:guid}/activities")]
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create reminder for lead {LeadId}", leadId);
                return StatusCode(500, new { error = "Failed to create reminder" });
            }
        }

        [HttpGet("{leadId:guid}/reminders")]
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to complete reminder {ReminderId}", reminderId);
                return StatusCode(500, new { error = "Failed to complete reminder" });
            }
        }

        // ==================== ASSIGNMENT ====================

        [HttpPatch("{leadId:guid}/assign")]
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to assign lead {LeadId}", leadId);
                return StatusCode(500, new { error = "Failed to assign lead" });
            }
        }

        // ==================== TIMELINE ====================

        [HttpGet("{leadId:guid}/timeline")]
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

        [HttpGet("export")]
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

        // ==================== IMPORT ====================

        [HttpPost("import")]
        [Consumes("multipart/form-data")]
        public async Task<ActionResult<ImportLeadsResult>> ImportLeads(
            [FromQuery] Guid tenantId,
            IFormFile file,
            [FromQuery] string? importedBy = null)
        {
            try
            {
                if (file == null || file.Length == 0)
                    return BadRequest(new { error = "File is required" });

                List<ImportLeadRow> rows;

                // Check file type
                var extension = Path.GetExtension(file.FileName).ToLowerInvariant();

                if (extension == ".csv")
                {
                    using var reader = new StreamReader(file.OpenReadStream());
                    var csvContent = await reader.ReadToEndAsync();
                    rows = ParseCsvHelper.ParseCsv(csvContent);
                }
                else if (extension == ".xlsx" || extension == ".xls")
                {
                    using var stream = new MemoryStream();
                    await file.CopyToAsync(stream);
                    rows = ParseExcelHelper.ParseExcel(stream.ToArray());
                }
                else
                {
                    return BadRequest(new { error = "Invalid file type. Only .csv, .xlsx, .xls are supported" });
                }

                var request = new ImportLeadsRequest(tenantId, rows, importedBy);
                var result = await _importHandler.Handle(request);

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to import leads");
                return StatusCode(500, new { error = $"Failed to import leads: {ex.Message}" });
            }
        }

        // NOTE: GetStats endpoint REMOVED - already in LeadsController
    }
}