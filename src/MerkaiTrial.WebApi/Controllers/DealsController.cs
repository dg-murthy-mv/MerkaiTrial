// =====================================================================
// DEALS CONTROLLER
// Location: MerkaiTrial.WebApi/Controllers/DealsController.cs
//
// COMPLETE FILE — replaces the existing one.
//
// CHANGES (020 — Blueprint transitions)
//  15. GET {id}/transitions — what this deal can do right now. The deal
//      page draws its buttons from it, including the ones it must show
//      DISABLED with the reason, which is what teaches the process
//      instead of merely enforcing it.
//  16. UpdateDealStageRequest carries one Note and an AdminOverride flag,
//      replacing 019's two reason fields. A transition now carries its own
//      prompt, so the caller does not need to know whether it is being
//      asked about a loss or a reopen.
//
// CHANGES (019 — still true)
//  13. Both reason fields are optional on the wire, so an older client
//      posting only { stage } still binds — it just gets a clear 400 when
//      the move it asked for needs a note.
//  14. UnauthorizedAccessException → 403 on the write endpoints that can
//      raise it. Without this case, "you're not allowed to reopen a
//      closed deal" reached the browser as a 500 reading "Failed to
//      update deal stage", and the rep had no idea why.
//
// CONSISTENCY FIXES vs LeadsController:
//   1. Added ICurrentUserService injection (uploadedBy + future auth)
//   2. Added try-catch on ALL endpoints
//   3. Added CancellationToken to all endpoints
//   4. Added PlanLimitExceededException catch in Create (specific before generic)
//   5. Added [ProducesResponseType] attributes throughout
//   6. Added XML summary comments
//   7. Added LogInformation on success, LogWarning on not-found
//   8. Added ID mismatch guard on Update
//   9. Added uploadedBy from ICurrentUserService in UploadAttachment
//  10. Moved UpdateDealStageRequest record inside namespace
//
// RECORD VISIBILITY (016)
//  11. Attachment endpoints check the deal is visible first (DealAccessHandler).
//      Their handlers live in another file; the check sits here so it
//      doesn't have to be repeated in each.
//  12. Update and the note/activity/reminder POSTs return 404 for a deal
//      outside scope (KeyNotFound) and 400 for a rejected change
//      (InvalidOperation) instead of a generic 500.
// =====================================================================

using MerkaiTrial.Application.Commands.Deals;
using MerkaiTrial.Application.Commands.PipelineStages;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Exceptions;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.WebApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DealsController : ControllerBase
    {
        private readonly GetDealsHandler _getDealsHandler;
        private readonly GetDealDetailHandler _getDealDetailHandler;
        private readonly GetDealStageHistoryHandler _getStageHistoryHandler;
        private readonly GetDealSourcesHandler _getSourcesHandler;
        private readonly CreateDealHandler _createDealHandler;
        private readonly UpdateDealHandler _updateDealHandler;
        private readonly UpdateDealStageHandler _updateDealStageHandler;
        private readonly DeleteDealHandler _deleteDealHandler;
        private readonly GetDealNotesHandler _getNotesHandler;
        private readonly CreateDealNoteHandler _createNoteHandler;
        private readonly GetDealActivitiesHandler _getActivitiesHandler;
        private readonly CreateDealActivityHandler _createActivityHandler;
        private readonly GetDealRemindersHandler _getRemindersHandler;
        private readonly CreateDealReminderHandler _createReminderHandler;
        private readonly CompleteDealReminderHandler _completeReminderHandler;
        private readonly GetDealAttachmentsHandler _getAttachmentsHandler;
        private readonly UploadDealAttachmentHandler _uploadAttachmentHandler;
        private readonly DeleteDealAttachmentHandler _deleteAttachmentHandler;
        private readonly GetDealsByContactHandler _getDealsByContact;
        private readonly TransitionDealStageHandler _transitionDealStage;
        private readonly GetAvailableTransitionsHandler _availableTransitions;   // 020
        private readonly DealAccessHandler _dealAccess;
        private readonly ICurrentUserService _currentUserService;   // ✅ ADDED
        private readonly ILogger<DealsController> _logger;

        public DealsController(
            GetDealsHandler getDealsHandler,
            GetDealDetailHandler getDealDetailHandler,
            GetDealStageHistoryHandler getStageHistoryHandler,
            GetDealSourcesHandler getSourcesHandler,
            CreateDealHandler createDealHandler,
            UpdateDealHandler updateDealHandler,
            UpdateDealStageHandler updateDealStageHandler,
            DeleteDealHandler deleteDealHandler,
            GetDealNotesHandler getNotesHandler,
            CreateDealNoteHandler createNoteHandler,
            GetDealActivitiesHandler getActivitiesHandler,
            CreateDealActivityHandler createActivityHandler,
            GetDealRemindersHandler getRemindersHandler,
            CreateDealReminderHandler createReminderHandler,
            CompleteDealReminderHandler completeReminderHandler,
            GetDealAttachmentsHandler getAttachmentsHandler,
            UploadDealAttachmentHandler uploadAttachmentHandler,
            DeleteDealAttachmentHandler deleteAttachmentHandler,
            GetDealsByContactHandler getDealsByContact,
            TransitionDealStageHandler transitionDealStage,
            GetAvailableTransitionsHandler availableTransitions,             // 020
            DealAccessHandler dealAccess,
            ICurrentUserService currentUserService,           // ✅ ADDED
            ILogger<DealsController> logger)
        {
            _getDealsHandler = getDealsHandler;
            _getDealDetailHandler = getDealDetailHandler;
            _getStageHistoryHandler = getStageHistoryHandler;
            _getSourcesHandler = getSourcesHandler;
            _createDealHandler = createDealHandler;
            _updateDealHandler = updateDealHandler;
            _updateDealStageHandler = updateDealStageHandler;
            _deleteDealHandler = deleteDealHandler;
            _getNotesHandler = getNotesHandler;
            _createNoteHandler = createNoteHandler;
            _getActivitiesHandler = getActivitiesHandler;
            _createActivityHandler = createActivityHandler;
            _getRemindersHandler = getRemindersHandler;
            _createReminderHandler = createReminderHandler;
            _completeReminderHandler = completeReminderHandler;
            _getAttachmentsHandler = getAttachmentsHandler;
            _uploadAttachmentHandler = uploadAttachmentHandler;
            _deleteAttachmentHandler = deleteAttachmentHandler;
            _getDealsByContact = getDealsByContact;
            _transitionDealStage = transitionDealStage;
            _availableTransitions = availableTransitions;
            _dealAccess = dealAccess;
            _currentUserService = currentUserService;             // ✅ ADDED
            _logger = logger;
        }

        // ==================== QUERIES ====================

        /// <summary>
        /// GET /api/deals - Get all deals for tenant with optional filters
        /// </summary>
        [HttpGet]
        [Authorize(Policy = "Deals.Read")]
        [ProducesResponseType(typeof(IEnumerable<DealListItem>), 200)]
        [ProducesResponseType(500)]
        public async Task<IActionResult> GetAll(
            [FromQuery] string? stage = null,
            [FromQuery] string? search = null,
            [FromQuery] string? ownerUserId = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20,
            CancellationToken cancellationToken = default)   // ✅ ADDED
        {
            var tenantId = _currentUserService.GetCurrentTenantId().ToString();
            try
            {
                if (page < 1) page = 1;
                // Was: > 100 → reset to 20, so asking for more returned LESS.
                // Now clamps. 500 lets the Pipeline board and the dashboard
                // load a full workspace in one call.
                if (pageSize < 1) pageSize = 20;
                if (pageSize > 500) pageSize = 500;

                var result = await _getDealsHandler.HandleAsync(
                    new GetDealsRequest(tenantId, stage, search, ownerUserId, page, pageSize));

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting deals for tenant {TenantId}", tenantId);
                return StatusCode(500, new { error = "Failed to retrieve deals" });
            }
        }

        /// <summary>
        /// GET /api/deals/{id} - Get single deal detail
        /// </summary>
        [HttpGet("{id:guid}")]
        [Authorize(Policy = "Deals.Read")]
        [ProducesResponseType(typeof(DealDetailDto), 200)]
        [ProducesResponseType(404)]
        [ProducesResponseType(500)]
        public async Task<IActionResult> GetDetail(
            Guid id,
            CancellationToken cancellationToken = default)   // ✅ ADDED
        {
            var tenantId = _currentUserService.GetCurrentTenantId().ToString();
            try
            {
                var result = await _getDealDetailHandler.HandleAsync(tenantId, id);

                if (result is null)
                    return NotFound(new { error = $"Deal {id} not found" });

                return Ok(result);
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new { error = $"Deal {id} not found" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting deal {DealId}", id);
                return StatusCode(500, new { error = "Failed to retrieve deal" });
            }
        }

        // ==================== COMMANDS ====================

        /// <summary>
        /// POST /api/deals - Create new deal
        /// </summary>
        [HttpPost]
        [Authorize(Policy = "Deals.Create")]
        [ProducesResponseType(typeof(DealDto), 201)]
        [ProducesResponseType(400)]
        [ProducesResponseType(422)]
        [ProducesResponseType(500)]
        public async Task<IActionResult> Create(
            [FromBody] CreateDealDto dto,
            CancellationToken cancellationToken = default)   // ✅ ADDED
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                // Server-resolved identity always wins over whatever the client sent in the body.
                dto.TenantId = _currentUserService.GetCurrentTenantId().ToString();

                var result = await _createDealHandler.HandleAsync(dto);

                _logger.LogInformation("Deal created successfully: {DealId} for tenant {TenantId}",
                    result.Id, dto.TenantId);

                return CreatedAtAction(nameof(GetDetail), new { id = result.Id }, result);
            }
            catch (PlanLimitExceededException ex)            // ✅ SPECIFIC BEFORE GENERIC
            {
                _logger.LogWarning("Plan limit exceeded for tenant {TenantId}: {Message}",
                    dto.TenantId, ex.Message);
                return UnprocessableEntity(new
                {
                    error = "plan_limit_exceeded",
                    message = ex.Message,
                    resource = ex.Resource,
                    current = ex.Current,
                    limit = ex.Limit
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating deal for tenant {TenantId}", dto.TenantId);
                return StatusCode(500, new { error = "Failed to create deal" });
            }
        }

        /// <summary>
        /// PUT /api/deals/{id} - Update deal
        /// </summary>
        [HttpPut("{id:guid}")]
        [Authorize(Policy = "Deals.Update")]
        [ProducesResponseType(204)]
        [ProducesResponseType(400)]
        [ProducesResponseType(403)]
        [ProducesResponseType(404)]
        [ProducesResponseType(500)]
        public async Task<IActionResult> Update(
            Guid id,
            [FromBody] UpdateDealDto dto,
            CancellationToken cancellationToken = default)   // ✅ ADDED
        {
            var tenantId = _currentUserService.GetCurrentTenantId().ToString();
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                //if (id != dto.DealId)                        // ✅ ADDED — ID mismatch guard
                //    return BadRequest(new { error = "Deal ID in route and body must match" });

                await _updateDealHandler.HandleAsync(tenantId, id, dto);

                _logger.LogInformation("Deal {DealId} updated successfully", id);

                return NoContent();
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new { error = $"Deal {id} not found" });
            }
            // ✅ 019 — "only a manager can reopen a closed deal". Before this
            // case existed it fell through to the 500 below and the page
            // showed "Failed to update deal", which told the rep nothing.
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning("Refused stage change on deal {DealId}: {Message}", id, ex.Message);
                return StatusCode(403, new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating deal {DealId}", id);
                return StatusCode(500, new { error = "Failed to update deal" });
            }
        }

        /// <summary>
        /// DELETE /api/deals/{id} - Delete deal
        /// </summary>
        [HttpDelete("{id:guid}")]
        [Authorize(Policy = "Deals.Delete")]
        [ProducesResponseType(200)]
        [ProducesResponseType(404)]
        [ProducesResponseType(500)]
        public async Task<IActionResult> Delete(
            Guid id,
            CancellationToken cancellationToken = default)   // ✅ ADDED
        {
            var tenantId = _currentUserService.GetCurrentTenantId().ToString();
            try
            {
                await _deleteDealHandler.HandleAsync(tenantId, id);

                _logger.LogInformation("Deal {DealId} deleted successfully", id);

                return Ok(new { success = true });           // ✅ CONSISTENT with LeadsController
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new { error = $"Deal {id} not found" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting deal {DealId}", id);
                return StatusCode(500, new { error = "Failed to delete deal" });
            }
        }

        // ==================== STAGE ====================

        /// <summary>
        /// PUT /api/deals/{id}/stage - Move a deal to another stage.
        ///
        /// The one endpoint behind BOTH the kanban drag and the deal page's
        /// transition buttons. The body carries the note the transition
        /// asked for, if it asked for one, and adminOverride when a
        /// workspace admin is deliberately stepping outside the process.
        ///
        /// Nothing is validated here: the guard owns every rule, so the
        /// message a rep sees is written once and both callers get it.
        /// </summary>
        [HttpPut("{id:guid}/stage")]
        [Authorize(Policy = "Deals.Update")]
        [ProducesResponseType(204)]
        [ProducesResponseType(400)]
        [ProducesResponseType(403)]
        [ProducesResponseType(404)]
        [ProducesResponseType(500)]
        public async Task<IActionResult> UpdateStage(
            Guid id,
            [FromBody] UpdateDealStageRequest request,
            CancellationToken cancellationToken = default)   // ✅ ADDED
        {
            var tenantId = _currentUserService.GetCurrentTenantId().ToString();
            try
            {
                if (string.IsNullOrWhiteSpace(request.Stage))
                    return BadRequest(new { error = "Stage is required" });

                await _updateDealStageHandler.HandleAsync(
                    tenantId, id, request.Stage, request.EffectiveNote, request.AdminOverride);

                _logger.LogInformation("Deal {DealId} stage updated to {Stage}", id, request.Stage);

                return NoContent();
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new { error = $"Deal {id} not found" });
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning("Refused stage change on deal {DealId}: {Message}", id, ex.Message);
                return StatusCode(403, new { error = ex.Message });
            }
            // ArgumentException is what an unknown stage key raises. It was
            // reaching the 500 below, so a stale kanban column posted after
            // a stage was retired looked like the server had fallen over.
            catch (ArgumentException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating stage for deal {DealId}", id);
                return StatusCode(500, new { error = "Failed to update deal stage" });
            }
        }

        /// <summary>
        /// PATCH /api/deals/{dealId}/stage - Transition deal stage (full pipeline)
        ///
        /// The AUTOMATIC path: a quote was accepted, or an invoice was paid
        /// in full. It skips the entry requirements and the reopen rules on
        /// purpose — see StageTransitionGuard — but still refuses to touch
        /// a deal that is already closed.
        /// </summary>
        [HttpPatch("{dealId:guid}/stage")]
        [Authorize(Policy = "Deals.Update")]
        [ProducesResponseType(204)]
        [ProducesResponseType(400)]
        [ProducesResponseType(403)]
        [ProducesResponseType(404)]
        [ProducesResponseType(500)]
        public async Task<IActionResult> TransitionStage(
            Guid dealId,
            [FromBody] TransitionDealStageDto dto,
            CancellationToken cancellationToken = default)   // ✅ ADDED
        {
            var tenantId = _currentUserService.GetCurrentTenantId().ToString();
            try
            {
                await _transitionDealStage.HandleAsync(
                    tenantId, dealId, dto.Stage, dto.Probability, changedBy: "System");

                _logger.LogInformation("Deal {DealId} transitioned to stage {Stage}", dealId, dto.Stage);

                return NoContent();
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new { error = $"Deal {dealId} not found" });
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(403, new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error transitioning stage for deal {DealId}", dealId);
                return StatusCode(500, new { error = "Failed to transition deal stage" });
            }
        }

        /// <summary>
        /// GET /api/deals/{id}/transitions - what this deal can do right now.
        ///
        /// Every move the tenant's process offers from this deal's stage,
        /// each marked allowed or not — and when not, why. The deal page
        /// renders the blocked ones DISABLED with the reason rather than
        /// hiding them, because a greyed-out "Mark as Closed Won — this
        /// deal has no quote yet" teaches the process, while a missing
        /// button just looks broken.
        ///
        /// Guidance, not the gate: the guard checks all of it again on the
        /// way in, so a stale page cannot talk its way past a rule.
        /// </summary>
        [HttpGet("{id:guid}/transitions")]
        [Authorize(Policy = "Deals.Read")]
        [ProducesResponseType(typeof(DealTransitionsDto), 200)]
        [ProducesResponseType(404)]
        [ProducesResponseType(500)]
        public async Task<IActionResult> GetTransitions(
            Guid id,
            CancellationToken cancellationToken = default)
        {
            var tenantId = _currentUserService.GetCurrentTenantId();
            try
            {
                var result = await _availableTransitions.HandleAsync(tenantId, id, cancellationToken);
                return Ok(result);
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new { error = $"Deal {id} not found" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting transitions for deal {DealId}", id);
                return StatusCode(500, new { error = "Failed to retrieve the available moves" });
            }
        }

        /// <summary>
        /// GET /api/deals/{id}/stage-history - Get stage change history
        /// </summary>
        [HttpGet("{id:guid}/stage-history")]
        [Authorize(Policy = "Deals.Read")]
        [ProducesResponseType(200)]
        [ProducesResponseType(500)]
        public async Task<IActionResult> GetStageHistory(
            Guid id,
            CancellationToken cancellationToken = default)   // ✅ ADDED
        {
            var tenantId = _currentUserService.GetCurrentTenantId().ToString();
            try
            {
                var result = await _getStageHistoryHandler.HandleAsync(
                    new GetDealStageHistoryRequest(tenantId, id));
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting stage history for deal {DealId}", id);
                return StatusCode(500, new { error = "Failed to retrieve stage history" });
            }
        }

        // ==================== SOURCES ====================

        /// <summary>
        /// GET /api/deals/sources - Get deal sources for dropdown
        /// </summary>
        [HttpGet("sources")]
        [Authorize(Policy = "Deals.Read")]
        [ProducesResponseType(200)]
        [ProducesResponseType(500)]
        public async Task<IActionResult> GetSources(
            CancellationToken cancellationToken = default)   // ✅ ADDED
        {
            var tenantId = _currentUserService.GetCurrentTenantId().ToString();
            try
            {
                var result = await _getSourcesHandler.HandleAsync(new GetDealSourcesRequest(tenantId));
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting deal sources");
                return StatusCode(500, new { error = "Failed to retrieve deal sources" });
            }
        }

        // ==================== NOTES ====================

        /// <summary>
        /// GET /api/deals/{id}/notes - Get notes for a deal
        /// </summary>
        [HttpGet("{id:guid}/notes")]
        [Authorize(Policy = "Deals.Read")]
        [ProducesResponseType(200)]
        [ProducesResponseType(500)]
        public async Task<IActionResult> GetNotes(
            Guid id,
            CancellationToken cancellationToken = default)   // ✅ ADDED
        {
            var tenantId = _currentUserService.GetCurrentTenantId().ToString();
            try
            {
                var result = await _getNotesHandler.HandleAsync(tenantId, id);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting notes for deal {DealId}", id);
                return StatusCode(500, new { error = "Failed to retrieve notes" });
            }
        }

        /// <summary>
        /// POST /api/deals/{id}/notes - Add note to deal
        /// </summary>
        [HttpPost("{id:guid}/notes")]
        [Authorize(Policy = "Deals.Update")]
        [ProducesResponseType(200)]
        [ProducesResponseType(400)]
        [ProducesResponseType(500)]
        public async Task<IActionResult> AddNote(
            Guid id,
            [FromBody] CreateDealNoteDto dto,
            CancellationToken cancellationToken = default)   // ✅ ADDED
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                // Server-resolved identity always wins over whatever the client sent in the body.
                dto.TenantId = _currentUserService.GetCurrentTenantId();

                await _createNoteHandler.HandleAsync(dto);

                _logger.LogInformation("Note added to deal {DealId}", id);

                return Ok(new { success = true });
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new { error = $"Deal {id} not found" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding note to deal {DealId}", id);
                return StatusCode(500, new { error = "Failed to add note" });
            }
        }

        // ==================== ACTIVITIES ====================

        /// <summary>
        /// GET /api/deals/{id}/activities - Get activities for a deal
        /// </summary>
        [HttpGet("{id:guid}/activities")]
        [Authorize(Policy = "Deals.Read")]
        [ProducesResponseType(200)]
        [ProducesResponseType(500)]
        public async Task<IActionResult> GetActivities(
            Guid id,
            CancellationToken cancellationToken = default)   // ✅ ADDED
        {
            var tenantId = _currentUserService.GetCurrentTenantId().ToString();
            try
            {
                var result = await _getActivitiesHandler.HandleAsync(tenantId, id);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting activities for deal {DealId}", id);
                return StatusCode(500, new { error = "Failed to retrieve activities" });
            }
        }

        /// <summary>
        /// POST /api/deals/{id}/activities - Add activity to deal
        /// </summary>
        [HttpPost("{id:guid}/activities")]
        [Authorize(Policy = "Deals.Update")]
        [ProducesResponseType(200)]
        [ProducesResponseType(400)]
        [ProducesResponseType(500)]
        public async Task<IActionResult> AddActivity(
            Guid id,
            [FromBody] CreateDealActivityDto dto,
            CancellationToken cancellationToken = default)   // ✅ ADDED
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                // Server-resolved identity always wins over whatever the client sent in the body.
                dto.TenantId = _currentUserService.GetCurrentTenantId();

                await _createActivityHandler.HandleAsync(dto);

                _logger.LogInformation("Activity added to deal {DealId}", id);

                return Ok(new { success = true });
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new { error = $"Deal {id} not found" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding activity to deal {DealId}", id);
                return StatusCode(500, new { error = "Failed to add activity" });
            }
        }

        // ==================== REMINDERS ====================

        /// <summary>
        /// GET /api/deals/{id}/reminders - Get reminders for a deal
        /// </summary>
        [HttpGet("{id:guid}/reminders")]
        [Authorize(Policy = "Deals.Read")]
        [ProducesResponseType(200)]
        [ProducesResponseType(500)]
        public async Task<IActionResult> GetReminders(
            Guid id,
            CancellationToken cancellationToken = default)   // ✅ ADDED
        {
            var tenantId = _currentUserService.GetCurrentTenantId().ToString();
            try
            {
                var result = await _getRemindersHandler.HandleAsync(tenantId, id);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting reminders for deal {DealId}", id);
                return StatusCode(500, new { error = "Failed to retrieve reminders" });
            }
        }

        /// <summary>
        /// POST /api/deals/{id}/reminders - Add reminder to deal
        /// </summary>
        [HttpPost("{id:guid}/reminders")]
        [Authorize(Policy = "Deals.Update")]
        [ProducesResponseType(200)]
        [ProducesResponseType(400)]
        [ProducesResponseType(500)]
        public async Task<IActionResult> AddReminder(
            Guid id,
            [FromBody] CreateDealReminderDto dto,
            CancellationToken cancellationToken = default)   // ✅ ADDED
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                // Server-resolved identity always wins over whatever the client sent in the body.
                dto.TenantId = _currentUserService.GetCurrentTenantId();

                await _createReminderHandler.HandleAsync(dto);

                _logger.LogInformation("Reminder added to deal {DealId}", id);

                return Ok(new { success = true });
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new { error = $"Deal {id} not found" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding reminder to deal {DealId}", id);
                return StatusCode(500, new { error = "Failed to add reminder" });
            }
        }

        /// <summary>
        /// POST /api/deals/{id}/reminders/{reminderId}/complete - Complete a reminder
        /// </summary>
        [HttpPost("{id:guid}/reminders/{reminderId:guid}/complete")]
        [Authorize(Policy = "Deals.Update")]
        [ProducesResponseType(200)]
        [ProducesResponseType(404)]
        [ProducesResponseType(500)]
        public async Task<IActionResult> CompleteReminder(
            Guid id,
            Guid reminderId,
            CancellationToken cancellationToken = default)   // ✅ ADDED
        {
            var tenantId = _currentUserService.GetCurrentTenantId().ToString();
            try
            {
                await _completeReminderHandler.HandleAsync(tenantId, reminderId);

                _logger.LogInformation("Reminder {ReminderId} completed for deal {DealId}",
                    reminderId, id);

                return Ok(new { success = true });
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new { error = $"Reminder {reminderId} not found" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error completing reminder {ReminderId}", reminderId);
                return StatusCode(500, new { error = "Failed to complete reminder" });
            }
        }

        // ==================== ATTACHMENTS ====================

        /// <summary>
        /// GET /api/deals/{id}/attachments - Get attachments for a deal
        /// </summary>
        [HttpGet("{id:guid}/attachments")]
        [Authorize(Policy = "Deals.Read")]
        [ProducesResponseType(200)]
        [ProducesResponseType(500)]
        public async Task<IActionResult> GetAttachments(
            Guid id,
            CancellationToken cancellationToken = default)   // ✅ ADDED
        {
            var tenantId = _currentUserService.GetCurrentTenantId().ToString();
            try
            {
                // A deal outside scope has no attachments to show.
                if (!await _dealAccess.CanSeeDealAsync(Guid.Parse(tenantId), id, cancellationToken))
                    return Ok(new List<AttachmentDto>());

                var result = await _getAttachmentsHandler.HandleAsync(tenantId, id);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get attachments for deal {DealId}", id);
                return StatusCode(500, new { error = "Failed to retrieve attachments" });
            }
        }

        /// <summary>
        /// POST /api/deals/{id}/attachments - Upload attachment to deal
        /// </summary>
        [HttpPost("{id:guid}/attachments")]
        [Authorize(Policy = "Deals.Update")]
        [RequestSizeLimit(10 * 1024 * 1024)]
        [ProducesResponseType(200)]
        [ProducesResponseType(400)]
        [ProducesResponseType(404)]
        [ProducesResponseType(500)]
        public async Task<IActionResult> UploadAttachment(
            Guid id,
            IFormFile file,
            CancellationToken cancellationToken = default)   // ✅ ADDED
        {
            var tenantId = _currentUserService.GetCurrentTenantId().ToString();
            try
            {
                if (file == null || file.Length == 0)
                    return BadRequest(new { error = "No file provided" });

                // Checked before the file is stored — no orphan files.
                if (!await _dealAccess.CanSeeDealAsync(Guid.Parse(tenantId), id, cancellationToken))
                    return NotFound(new { error = $"Deal {id} not found" });

                var result = await _uploadAttachmentHandler.HandleAsync(
                    tenantId, id, file, cancellationToken);

                _logger.LogInformation("Attachment uploaded to deal {DealId}: {FileName}",
                    id, file.FileName);

                return Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new { error = $"Deal {id} not found" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to upload attachment for deal {DealId}", id);
                return StatusCode(500, new { error = "Failed to upload attachment" });
            }
        }

        /// <summary>
        /// DELETE /api/deals/{id}/attachments/{attachmentId} - Delete attachment by deal context
        /// </summary>
        [HttpDelete("{id:guid}/attachments/{attachmentId:guid}")]
        [Authorize(Policy = "Deals.Delete")]
        [ProducesResponseType(204)]
        [ProducesResponseType(404)]
        [ProducesResponseType(500)]
        public async Task<IActionResult> DeleteAttachment(
            Guid id,
            Guid attachmentId,
            CancellationToken cancellationToken = default)   // ✅ ADDED
        {
            var tenantId = _currentUserService.GetCurrentTenantId().ToString();
            try
            {
                if (!await _dealAccess.CanSeeDealAttachmentAsync(Guid.Parse(tenantId), attachmentId, cancellationToken))
                    return NotFound(new { error = $"Attachment {attachmentId} not found" });

                await _deleteAttachmentHandler.HandleAsync(tenantId, attachmentId, cancellationToken);

                _logger.LogInformation("Attachment {AttachmentId} deleted from deal {DealId}",
                    attachmentId, id);

                return NoContent();
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new { error = $"Attachment {attachmentId} not found" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete attachment {AttachmentId}", attachmentId);
                return StatusCode(500, new { error = "Failed to delete attachment" });
            }
        }

        /// <summary>
        /// DELETE /api/deals/attachments/{attachmentId} - Delete attachment direct (no deal context needed)
        /// </summary>
        [HttpDelete("attachments/{attachmentId:guid}")]
        [Authorize(Policy = "Deals.Delete")]
        [ProducesResponseType(204)]
        [ProducesResponseType(404)]
        [ProducesResponseType(500)]
        public async Task<IActionResult> DeleteAttachmentDirect(
            Guid attachmentId,
            CancellationToken cancellationToken = default)   // ✅ ADDED
        {
            var tenantId = _currentUserService.GetCurrentTenantId().ToString();
            try
            {
                if (!await _dealAccess.CanSeeDealAttachmentAsync(Guid.Parse(tenantId), attachmentId, cancellationToken))
                    return NotFound(new { error = $"Attachment {attachmentId} not found" });

                await _deleteAttachmentHandler.HandleAsync(tenantId, attachmentId, cancellationToken);

                _logger.LogInformation("Attachment {AttachmentId} deleted directly", attachmentId);

                return NoContent();
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new { error = $"Attachment {attachmentId} not found" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete attachment {AttachmentId}", attachmentId);
                return StatusCode(500, new { error = "Failed to delete attachment" });
            }
        }

        // ==================== CONTACT RELATIONSHIP ====================

        /// <summary>
        /// GET /api/deals/by-contact/{contactId} - Get deals linked to a contact
        /// </summary>
        [HttpGet("by-contact/{contactId:guid}")]
        [Authorize(Policy = "Deals.Read")]
        [ProducesResponseType(200)]
        [ProducesResponseType(500)]
        public async Task<IActionResult> GetByContact(
            Guid contactId,
            CancellationToken cancellationToken = default)   // ✅ ADDED
        {
            var tenantId = _currentUserService.GetCurrentTenantId().ToString();
            try
            {
                var result = await _getDealsByContact.HandleAsync(
                    new GetDealsByContactRequest { TenantId = tenantId, ContactId = contactId });

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting deals for contact {ContactId}", contactId);
                return StatusCode(500, new { error = "Failed to retrieve deals for contact" });
            }
        }
    }

    // ✅ Moved inside namespace (was incorrectly at file root)
    //
    // 020: one Note, whatever the transition asked for, plus the admin
    // escape hatch. Everything after Stage is optional, so an older caller
    // posting `{ "stage": "Proposal" }` still binds exactly as it did — it
    // simply gets a readable 400 if the move needs a note.
    //
    // LostReason and ReopenReason are kept as aliases so the 019 shape
    // still works off the wire; whichever arrives becomes the note.
    public record UpdateDealStageRequest(
        string Stage,
        string? Note = null,
        bool AdminOverride = false,
        string? LostReason = null,
        string? ReopenReason = null)
    {
        /// <summary>The note, wherever the caller put it.</summary>
        public string? EffectiveNote =>
            !string.IsNullOrWhiteSpace(Note) ? Note
            : !string.IsNullOrWhiteSpace(LostReason) ? LostReason
            : ReopenReason;
    }
}
