// =====================================================================
// LEADS CONTROLLER - Main CRUD Operations
// Location: MerkaiTrial.WebApi/Controllers/LeadsController.cs
// =====================================================================

using MerkaiTrial.Application.Commands.Countries;
using MerkaiTrial.Application.Commands.Leads;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Exceptions;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.WebApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class LeadsController : ControllerBase
    {

        private readonly ConvertLeadToDealHandler _convertLeadToDealHandler;
        private readonly GetLeadsPaginatedHandler _getPaginatedHandler;
        private readonly GetLeadDetailHandler _getDetailHandler;
        private readonly GetLeadChannelsHandler _getChannelsHandler;
        private readonly GetLeadSourcesHandler _getSourcesHandler;
        private readonly GetLeadStatsHandler _getStatsHandler;
        private readonly CreateLeadHandler _createHandler;
        private readonly UpdateLeadHandler _updateHandler;
        private readonly UpdateLeadStatusHandler _updateStatusHandler;
        private readonly DeleteLeadHandler _deleteHandler;
        private readonly ConvertLeadHandler _convertHandler;
        private readonly ICurrentUserService _currentUserService;
        private readonly GetSalesTeamHandler _getSalesTeamHandler;
        private readonly ILogger<LeadsController> _logger;
        private readonly GetCountriesForDropdownHandler _getCountriesHandler;
        private readonly GetCurrenciesForDropdownHandler _getCurrenciesHandler;
        private readonly UploadLeadAttachmentHandler _uploadAttachment;
        private readonly GetLeadAttachmentsHandler _getAttachments;
        private readonly DeleteLeadAttachmentHandler _deleteAttachment;
        public LeadsController(
            GetLeadsPaginatedHandler getPaginatedHandler,
            GetLeadDetailHandler getDetailHandler,
            GetLeadChannelsHandler getChannelsHandler,
            GetLeadSourcesHandler getSourcesHandler,
            GetLeadStatsHandler getStatsHandler,
            CreateLeadHandler createHandler,
            UpdateLeadHandler updateHandler,
            GetSalesTeamHandler getSalesTeamHandler,
            UpdateLeadStatusHandler updateStatusHandler,
            DeleteLeadHandler deleteHandler,
            ConvertLeadHandler convertHandler,
            ICurrentUserService currentUserService,
            GetCountriesForDropdownHandler getCountriesHandler,
             ConvertLeadToDealHandler convertLeadToDealHandler,
            GetCurrenciesForDropdownHandler getCurrenciesHandler,
                UploadLeadAttachmentHandler uploadAttachment,
                GetLeadAttachmentsHandler getAttachments,
                DeleteLeadAttachmentHandler deleteAttachment,
            ILogger<LeadsController> logger)
        {
            _getPaginatedHandler = getPaginatedHandler;
            _getDetailHandler = getDetailHandler;
            _getChannelsHandler = getChannelsHandler;
            _getSourcesHandler = getSourcesHandler;
            _getSalesTeamHandler = getSalesTeamHandler;
            _getStatsHandler = getStatsHandler;
            _createHandler = createHandler;
            _updateHandler = updateHandler;
            _updateStatusHandler = updateStatusHandler;
            _getCountriesHandler = getCountriesHandler;
            _getCurrenciesHandler = getCurrenciesHandler;
            _convertLeadToDealHandler = convertLeadToDealHandler;
            _deleteHandler = deleteHandler;
            _convertHandler = convertHandler;
            _currentUserService = currentUserService;
                _uploadAttachment = uploadAttachment;
                _getAttachments = getAttachments;
                _deleteAttachment = deleteAttachment;
            _logger = logger;
        }

        // ==================== QUERIES ====================

        /// <summary>
        /// GET /api/leads - Get paginated leads
        /// </summary>
        [HttpGet]
        [Authorize(Policy = "Leads.Read")]
        [ProducesResponseType(typeof(PaginatedResult<LeadListItem>), 200)]
        public async Task<IActionResult> GetAll(
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] string? searchTerm = null,
            [FromQuery] string? status = null,
            [FromQuery] string? assignedTo = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();

                if (pageNumber < 1) pageNumber = 1;
                if (pageSize < 1 || pageSize > 100) pageSize = 10;

                var query = new GetLeadsPaginatedQuery(
                    TenantId: tenantId,
                    PageNumber: pageNumber,
                    PageSize: pageSize,
                    SearchTerm: searchTerm,
                    Status: status,
                    AssignedTo: assignedTo
                );

                var result = await _getPaginatedHandler.Handle(query, cancellationToken);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting paginated leads");
                return StatusCode(500, new { error = "Failed to retrieve leads" });
            }
        }

        /// <summary>
        /// GET /api/leads/{id} - Get single lead detail
        /// </summary>
        [HttpGet("{id:guid}")]
        [Authorize(Policy = "Leads.Read")]
        [ProducesResponseType(typeof(LeadDetailDto), 200)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> GetById(
            [FromRoute] Guid id,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();
                var query = new GetLeadDetailQuery(tenantId, id);
                var result = await _getDetailHandler.Handle(query, cancellationToken);

                return Ok(result);
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new { error = $"Lead {id} not found" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting lead {LeadId}", id);
                return StatusCode(500, new { error = "Failed to retrieve lead" });
            }
        }

        /// <summary>
        /// GET /api/leads/channels - Get available channels for dropdown
        /// </summary>
        [HttpGet("channels")]
        [Authorize(Policy = "Leads.Read")]
        [ProducesResponseType(typeof(List<LeadChannelDto>), 200)]
        public async Task<IActionResult> GetChannels(CancellationToken cancellationToken = default)
        {
            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();
                var query = new GetLeadChannelsQuery(tenantId);
                var result = await _getChannelsHandler.Handle(query, cancellationToken);

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting lead channels");
                return StatusCode(500, new { error = "Failed to retrieve channels" });
            }
        }

        [HttpGet("sales-team")]
        [Authorize(Policy = "Leads.Read")]
        [ProducesResponseType(typeof(List<SalesTeamMemberDto>), 200)]
        public async Task<IActionResult> GetSalesTeam(CancellationToken cancellationToken = default)
        {
            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();
                var query = new GetSalesTeamQuery(tenantId);
                var result = await _getSalesTeamHandler.Handle(query, cancellationToken);

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting sales team");
                return StatusCode(500, new { error = "Failed to retrieve sales team" });
            }
        }
        /// <summary>
        /// GET /api/leads/sources - Get available sources for dropdown
        /// </summary>
        [HttpGet("sources")]
        [Authorize(Policy = "Leads.Read")]
        [ProducesResponseType(typeof(List<LeadSourceDto>), 200)]
        public async Task<IActionResult> GetSources(CancellationToken cancellationToken = default)
        {
            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();
                var query = new GetLeadSourcesQuery(tenantId);
                var result = await _getSourcesHandler.Handle(query, cancellationToken);

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting lead sources");
                return StatusCode(500, new { error = "Failed to retrieve sources" });
            }
        }

        //[HttpGet("verticals")]
        //public async Task<IActionResult> GetVerticals(CancellationToken ct)
        //{
        //    // Reuse MetaController logic or inline:
        //    var verticals = await _db.CompanyVerticals
        //        .AsNoTracking()
        //        .Where(v => v.IsActive && !v.IsDeleted)
        //        .OrderBy(v => v.Name)
        //        .Select(v => new { v.Id, v.Name })
        //        .ToListAsync(ct);
        //    return Ok(verticals);
        //}

        /// <summary>
        /// GET /api/leads/stats - Get lead statistics
        /// </summary>
        [HttpGet("stats")]
        [Authorize(Policy = "Leads.Read")]
        [ProducesResponseType(typeof(LeadStatsDto), 200)]
        public async Task<IActionResult> GetStats(CancellationToken cancellationToken = default)
        {
            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();
                var query = new GetLeadStatsQuery(tenantId);
                var result = await _getStatsHandler.Handle(query, cancellationToken);

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting lead stats");
                return StatusCode(500, new { error = "Failed to retrieve statistics" });
            }
        }

        // ==================== COMMANDS ====================

        /// <summary>
        /// POST /api/leads - Create new lead
        /// </summary>
        [HttpPost]
        [Authorize(Policy = "Leads.Create")]
        [ProducesResponseType(typeof(LeadDto), 200)]
        [ProducesResponseType(400)]
        [ProducesResponseType(422)]
        public async Task<IActionResult> Create(
            [FromBody] CreateLeadDto dto,
            CancellationToken cancellationToken = default)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                var result = await _createHandler.Handle(dto, cancellationToken);

                _logger.LogInformation("Lead created successfully: {LeadId}", result.Id);

                return Ok(result);
            }
            catch (PlanLimitExceededException ex)
            {
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
                _logger.LogError(ex, "Error creating lead");
                return StatusCode(500, new { error = "Failed to create lead" });
            }
            
        }

        /// <summary>
        /// PUT /api/leads/{id} - Update lead
        /// </summary>
        [HttpPut("{id:guid}")]
        [Authorize(Policy = "Leads.Update")]
        [ProducesResponseType(typeof(LeadDetailDto), 200)]
        [ProducesResponseType(400)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> Update(
            [FromRoute] Guid id,
            [FromBody] UpdateLeadDto dto,
            CancellationToken cancellationToken = default)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                if (id != dto.LeadId)
                    return BadRequest(new { error = "Lead ID mismatch" });

                var result = await _updateHandler.Handle(dto, cancellationToken);

                _logger.LogInformation("Lead updated successfully: {LeadId}", id);

                return Ok(result);
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new { error = $"Lead {id} not found" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating lead {LeadId}", id);
                return StatusCode(500, new { error = "Failed to update lead" });
            }
        }

        /// <summary>
        /// PUT /api/leads/{id}/status - Update lead status
        /// </summary>
        [HttpPut("{id:guid}/status")]
        [Authorize(Policy = "Leads.Update")]
        [ProducesResponseType(200)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> UpdateStatus(
            [FromRoute] Guid id,
            [FromQuery] LeadStatus status,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();
                await _updateStatusHandler.Handle(tenantId, id, status, cancellationToken);

                _logger.LogInformation("Lead status updated: {LeadId} -> {Status}", id, status);

                return Ok(new { message = "Status updated successfully" });
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new { error = $"Lead {id} not found" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating lead status {LeadId}", id);
                return StatusCode(500, new { error = "Failed to update status" });
            }
        }

        /// <summary>
        /// DELETE /api/leads/{id} - Soft delete lead
        /// </summary>
        [HttpDelete("{id:guid}")]
        [Authorize(Policy = "Leads.Delete")]
        [ProducesResponseType(204)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> Delete(
            [FromRoute] Guid id,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();
                var command = new DeleteLeadCommand(tenantId, id);
                await _deleteHandler.Handle(command, cancellationToken);

                _logger.LogInformation("Lead deleted successfully: {LeadId}", id);

                return NoContent();
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new { error = $"Lead {id} not found" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting lead {LeadId}", id);
                return StatusCode(500, new { error = "Failed to delete lead" });
            }
        }
        // ✅ NEW: GET COUNTRIES (For Dropdown)
        [HttpGet("countries")]
        [Authorize(Policy = "Leads.Read")]
        public async Task<ActionResult<List<CountryDropdownDto>>> GetCountries()
        {
            try
            {
                var result = await _getCountriesHandler.Handle(new GetCountriesForDropdownQuery());
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get countries");
                return StatusCode(500, new { error = "Failed to get countries" });
            }
        }

        // ✅ NEW: GET CURRENCIES (For Dropdown)
        [HttpGet("currencies")]
        [Authorize(Policy = "Leads.Read")]
        public async Task<ActionResult<List<CurrencyDropdownDto>>> GetCurrencies()
        {
            try
            {
                var result = await _getCurrenciesHandler.Handle(new GetCurrenciesForDropdownQuery());
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get currencies");
                return StatusCode(500, new { error = "Failed to get currencies" });
            }
        }

        [HttpPost("{leadId:guid}/convert-to-deal")]
        [Authorize(Policy = "Leads.Update")]
        [ProducesResponseType(typeof(ConvertLeadToDealResultDto), 200)]
        [ProducesResponseType(400)]
        [ProducesResponseType(404)]
        [ProducesResponseType(500)]
        public async Task<ActionResult<ConvertLeadToDealResultDto>> ConvertToDeal(
           [FromRoute] Guid leadId,
           [FromBody] ConvertLeadToDealDto dto)
        {
            try
            {
                // Ensure route leadId matches DTO leadId
                if (dto.LeadId != leadId)
                {
                    return BadRequest(new { error = "LeadId in route and body must match" });
                }

                _logger.LogInformation("API: Converting lead {LeadId} to deal", leadId);

                var result = await _convertLeadToDealHandler.Handle(dto);

                _logger.LogInformation("API: Lead {LeadId} converted successfully to deal {DealId}",
                    leadId, result.DealId);

                return Ok(result);
            }
            catch (KeyNotFoundException ex)
            {
                _logger.LogWarning(ex, "Lead {LeadId} not found", leadId);
                return NotFound(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Invalid conversion attempt for lead {LeadId}", leadId);
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to convert lead {LeadId} to deal", leadId);
                return StatusCode(500, new { error = "Failed to convert lead to deal" });
            }
        }
        /// <summary>
        /// POST /api/leads/{id}/convert - Convert lead to contact/company/deal
        /// </summary>
        [HttpPost("{id:guid}/convert")]
        [Authorize(Policy = "Leads.Update")]
        [ProducesResponseType(typeof(ConvertLeadResultDto), 200)]
        [ProducesResponseType(400)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> Convert(
            [FromRoute] Guid id,
            [FromBody] ConvertLeadDto dto,
            CancellationToken cancellationToken = default)
        {
            try
            {
                if (id != dto.LeadId)
                    return BadRequest(new { error = "Lead ID mismatch" });

                var currentUser = await _currentUserService.GetCurrentUserAsync();

                var command = new ConvertLeadCommand(
                    TenantId: dto.TenantId,
                    LeadId: dto.LeadId,
                    CreateCompany: dto.CreateCompany,
                    CompanyName: dto.CompanyName,
                    ExistingCompanyId: dto.ExistingCompanyId,
                    CreateDeal: dto.CreateDeal,
                    DealValue: dto.DealValue,
                    ConvertedBy: currentUser.FullName
                );

                var result = await _convertHandler.Handle(command, cancellationToken);

                _logger.LogInformation("Lead converted successfully: {LeadId} -> Contact: {ContactId}",
                    id, result.ContactId);

                return Ok(new ConvertLeadResultDto(
                    ContactId: result.ContactId,
                    CompanyId: result.CompanyId,
                    DealId: null,
                    Success: result.Success,
                    Message: "Lead converted successfully"
                ));
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new { error = $"Lead {id} not found" });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error converting lead {LeadId}", id);
                return StatusCode(500, new { error = "Failed to convert lead" });
            }
        }

        /// GET /api/leads/{leadId}/attachments?tenantId={tenantId}
        [HttpGet("{leadId:guid}/attachments")]
        [Authorize(Policy = "Leads.Read")]
        public async Task<IActionResult> GetAttachments(
            Guid leadId, [FromQuery] Guid tenantId, CancellationToken ct)
        {
            try
            {
                var result = await _getAttachments.Handle(tenantId, leadId, ct);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get attachments for lead {LeadId}", leadId);
                return StatusCode(500, new { error = "Failed to retrieve attachments" });
            }
        }

        /// POST /api/leads/{leadId}/attachments?tenantId={tenantId}
        [HttpPost("{leadId:guid}/attachments")]
        [Authorize(Policy = "Leads.Update")]
        [RequestSizeLimit(10 * 1024 * 1024)] // 10MB
        public async Task<IActionResult> UploadAttachment(
            Guid leadId, [FromQuery] Guid tenantId,
            IFormFile file, CancellationToken ct)
        {
            try
            {
                if (file == null || file.Length == 0)
                    return BadRequest(new { error = "No file provided" });

                var uploadedBy = _currentUserService.GetCurrentUserId().ToString();

                var result = await _uploadAttachment.Handle(
                    new UploadLeadAttachmentDto(tenantId, leadId, file, uploadedBy), ct);

                return Ok(result);
            }
            catch (InvalidOperationException ex) // file type/size validation
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to upload attachment for lead {LeadId}", leadId);
                return StatusCode(500, new { error = "Upload failed" });
            }
        }

        /// DELETE /api/leads/attachments/{attachmentId}?tenantId={tenantId}
        // ✅ Aligned to Leads.Update (was Leads.Delete) — matches
        // Admin.Web's OnPostDeleteAttachmentAsync gate and the other 7
        // Leads sub-action handlers (notes, activities, reminders, status,
        // upload) which are all scoped to "can this user edit this lead's
        // related data", not the record-level Leads.Delete permission.
        // Previously sales_rep (update, no delete) would have the button
        // hidden client-side but still hit a server-side mismatch if the
        // request were ever made directly.
        [HttpDelete("attachments/{attachmentId:guid}")]
        [Authorize(Policy = "Leads.Update")]
        public async Task<IActionResult> DeleteAttachment(
            Guid attachmentId, [FromQuery] Guid tenantId, CancellationToken ct)
        {
            try
            {
                await _deleteAttachment.Handle(tenantId, attachmentId, ct);
                return Ok(new { success = true });
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new { error = "Attachment not found" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete attachment {AttachmentId}", attachmentId);
                return StatusCode(500, new { error = "Delete failed" });
            }
        }
    }
}