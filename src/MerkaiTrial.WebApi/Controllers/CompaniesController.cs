// =====================================================================
// COMPANIES CONTROLLER
// Location: MerkaiTrial.WebApi/Controllers/CompaniesController.cs
//
// FIXES:
//   Security — Create: TenantId now from ICurrentUserService, not client body
//   Security — Update: TenantId now from ICurrentUserService, not client body
//   Stats: removed ?tenantId= query param (controller resolves it)
// =====================================================================

using MerkaiTrial.Application.Commands.Companies;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Exceptions;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.WebApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class CompaniesController : ControllerBase
    {
        private readonly GetCompaniesHandler    _getCompanies;
        private readonly GetCompanyByIdHandler  _getCompanyById;
        private readonly GetCompanyStatsHandler _getStats;
        private readonly CreateCompanyHandler   _createCompany;
        private readonly UpdateCompanyHandler   _updateCompany;
        private readonly DeleteCompanyHandler   _deleteCompany;
        private readonly ICurrentUserService    _currentUserService;
        private readonly ILogger<CompaniesController> _logger;

        public CompaniesController(
            GetCompaniesHandler    getCompanies,
            GetCompanyStatsHandler getStats,
            GetCompanyByIdHandler  getCompanyById,
            CreateCompanyHandler   createCompany,
            UpdateCompanyHandler   updateCompany,
            DeleteCompanyHandler   deleteCompany,
            ICurrentUserService    currentUserService,
            ILogger<CompaniesController> logger)
        {
            _getCompanies       = getCompanies;
            _getStats           = getStats;
            _getCompanyById     = getCompanyById;
            _createCompany      = createCompany;
            _updateCompany      = updateCompany;
            _deleteCompany      = deleteCompany;
            _currentUserService = currentUserService;
            _logger             = logger;
        }

        // ── GET ALL ───────────────────────────────────────────────────────────

        [HttpGet]
        [Authorize(Policy = "Companies.read")]
        [ProducesResponseType(typeof(PaginatedResult<CompanyListItem>), 200)]
        public async Task<IActionResult> GetAll(
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] string? searchTerm = null,
            [FromQuery] string? vertical = null,
            [FromQuery] string? country = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();
                var query = new GetCompaniesQuery
                {
                    TenantId   = tenantId,
                    PageNumber = pageNumber,
                    PageSize   = pageSize,
                    SearchTerm = searchTerm,
                    Vertical   = vertical,
                    Country    = country
                };

                var result = await _getCompanies.Handle(query, cancellationToken);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting companies");
                return StatusCode(500, "An error occurred");
            }
        }

        // ── GET BY ID ─────────────────────────────────────────────────────────

        [HttpGet("{id:guid}")]
        [Authorize(Policy = "Companies.read")]
        [ProducesResponseType(typeof(CompanyDto), 200)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken = default)
        {
            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();
                var query = new GetCompanyByIdQuery { Id = id, TenantId = tenantId };
                var company = await _getCompanyById.Handle(query, cancellationToken);
                return Ok(company);
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting company {Id}", id);
                return StatusCode(500, "An error occurred");
            }
        }

        // ── STATS ─────────────────────────────────────────────────────────────

        [HttpGet("stats")]
        [Authorize(Policy = "Companies.read")]
        [ProducesResponseType(typeof(CompanyStatsDto), 200)]
        public async Task<IActionResult> GetStats(CancellationToken cancellationToken = default)
        {
            try
            {
                // ✅ FIX: always resolve from current user — never from query param
                var tenantId = _currentUserService.GetCurrentTenantId();
                var stats = await _getStats.Handle(tenantId, cancellationToken);
                return Ok(stats);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting company statistics");
                return StatusCode(500, "An error occurred");
            }
        }

        // ── CREATE ────────────────────────────────────────────────────────────

        [HttpPost]
        [Authorize(Policy = "Companies.create")]
        [ProducesResponseType(typeof(CompanyDto), 200)]
        [ProducesResponseType(422)]
        public async Task<IActionResult> Create(
            [FromBody] CreateCompanyDto dto,   // ✅ FIX: DTO not command — TenantId from server
            CancellationToken cancellationToken = default)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);
                // ✅ SECURITY FIX: TenantId resolved server-side, never trusted from client
                var tenantId    = _currentUserService.GetCurrentTenantId();
                var currentUser = await _currentUserService.GetCurrentUserAsync();

                var command = new CreateCompanyCommand
                {
                    TenantId  = tenantId,
                    Name      = dto.Name,
                    Country   = dto.Country,
                    TaxId     = dto.TaxId,
                    Vertical  = dto.Vertical,
                    CreatedBy = currentUser.FullName
                };

                var company = await _createCompany.Handle(command, cancellationToken);
                _logger.LogInformation("Company created: {Id}", company.Id);
                return Ok(company);
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
                _logger.LogError(ex, "Error creating company");
                return StatusCode(500, "An error occurred");
            }
        }

        // ── UPDATE ────────────────────────────────────────────────────────────

        [HttpPut("{id:guid}")]
        [Authorize(Policy = "Companies.update")]
        [ProducesResponseType(204)]
        [ProducesResponseType(400)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> Update(
            Guid id,
            [FromBody] UpdateCompanyDto dto,   // ✅ FIX: DTO not command
            CancellationToken cancellationToken = default)
        {
            try
            {
                if (id != dto.Id)
                    return BadRequest("ID mismatch");

                // ✅ SECURITY FIX: TenantId resolved server-side
                var tenantId    = _currentUserService.GetCurrentTenantId();
                var currentUser = await _currentUserService.GetCurrentUserAsync();

                var command = new UpdateCompanyCommand
                {
                    Id        = id,
                    TenantId  = tenantId,
                    Name      = dto.Name,
                    Country   = dto.Country,
                    TaxId     = dto.TaxId,
                    Vertical  = dto.Vertical,
                    UpdatedBy = currentUser.FullName
                };

                await _updateCompany.Handle(command, cancellationToken);
                _logger.LogInformation("Company updated: {Id}", id);
                return NoContent();
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating company {Id}", id);
                return StatusCode(500, "An error occurred");
            }
        }

        // ── DELETE ────────────────────────────────────────────────────────────

        [HttpDelete("{id:guid}")]
        [Authorize(Policy = "Companies.delete")]
        [ProducesResponseType(204)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken = default)
        {
            try
            {
                var tenantId    = _currentUserService.GetCurrentTenantId();
                var currentUser = await _currentUserService.GetCurrentUserAsync();

                var command = new DeleteCompanyCommand
                {
                    Id        = id,
                    TenantId  = tenantId,
                    DeletedBy = currentUser.FullName
                };

                await _deleteCompany.Handle(command, cancellationToken);
                _logger.LogInformation("Company deleted: {Id}", id);
                return NoContent();
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting company {Id}", id);
                return StatusCode(500, "An error occurred");
            }
        }
    }
}
