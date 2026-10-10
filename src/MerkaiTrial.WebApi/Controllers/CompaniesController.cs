// =====================================================================
// COMPANIES CONTROLLER
// Location: MerkaiTrial.WebApi/Controllers/CompaniesController.cs
//
// FIXES:
//   Security — Create: TenantId now from ICurrentUserService, not client body
//   Security — Update: TenantId now from ICurrentUserService, not client body
//   Stats: removed ?tenantId= query param (controller resolves it)
//
// 078 — CUSTOM FIELDS, AND THE CONTACTS-CONTROLLER FIXES FROM 075/076
//
//   • Policies.* constants instead of string literals ("Companies.read").
//     The literals worked only because policy names are compared
//     case-insensitively; a typo in one would have denied silently.
//
//   • GET /api/companies takes repeated ?cf=<fieldId>~<op>~<value> custom
//     field filters (CustomFieldFilterCodec), capped before decoding so a
//     request with ten thousand ?cf= values costs nothing. Unreadable ones
//     are skipped here; the handler drops any that do not fit the field.
//     The page size is clamped in the handler.
//
//   • GET /api/companies/lookup — every live company for a dropdown
//     (GetCompanyLookupHandler). The paged list is capped at 100 a page.
//
//   • Create and Update pass CustomFields through to the handlers.
//
//   • A refused custom field value comes back as 400 { error: "..." } with
//     the sentence for the person in it, so the page can show "Renewal
//     date must be a valid date." instead of "An error occurred". Only
//     CustomFieldValidationException is turned into a 400 — a general
//     InvalidOperationException from deeper down (EF, for instance) is a
//     fault and stays a logged 500, so internal wording never reaches a
//     browser.
//
//   • The plan-limit warning logged dto.TenantId — whatever the browser
//     sent, usually Guid.Empty. It logs the server-resolved tenant now.
// =====================================================================

using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.Companies;
using MerkaiTrial.Application.Commands.CustomFields;
using MerkaiTrial.Application.Configuration;
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
        private readonly GetCompanyLookupHandler _getLookup;     // 078
        private readonly CreateCompanyHandler   _createCompany;
        private readonly UpdateCompanyHandler   _updateCompany;
        private readonly DeleteCompanyHandler   _deleteCompany;
        private readonly ICurrentUserService    _currentUserService;
        private readonly ILogger<CompaniesController> _logger;

        public CompaniesController(
            GetCompaniesHandler    getCompanies,
            GetCompanyStatsHandler getStats,
            GetCompanyByIdHandler  getCompanyById,
            GetCompanyLookupHandler getLookup,
            CreateCompanyHandler   createCompany,
            UpdateCompanyHandler   updateCompany,
            DeleteCompanyHandler   deleteCompany,
            ICurrentUserService    currentUserService,
            ILogger<CompaniesController> logger)
        {
            _getCompanies       = getCompanies;
            _getStats           = getStats;
            _getCompanyById     = getCompanyById;
            _getLookup          = getLookup;
            _createCompany      = createCompany;
            _updateCompany      = updateCompany;
            _deleteCompany      = deleteCompany;
            _currentUserService = currentUserService;
            _logger             = logger;
        }

        // ── GET ALL ───────────────────────────────────────────────────────────

        [HttpGet]
        [Authorize(Policy = Policies.CompaniesRead)]
        [ProducesResponseType(typeof(PaginatedResult<CompanyListItem>), 200)]
        public async Task<IActionResult> GetAll(
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] string? searchTerm = null,
            [FromQuery] string? vertical = null,
            [FromQuery] string? country = null,
            [FromQuery(Name = "cf")] string[]? cf = null,           // 078
            CancellationToken cancellationToken = default)
        {
            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();

                // 078 — custom field filters, capped before decoding.
                var customFilters = new List<CustomFieldFilter>();
                foreach (var raw in (cf ?? Array.Empty<string>()).Take(CustomFieldLimits.MaxFiltersPerQuery))
                    if (CustomFieldFilterCodec.TryDecode(raw, out var filter))
                        customFilters.Add(filter);

                var query = new GetCompaniesQuery
                {
                    TenantId      = tenantId,
                    PageNumber    = pageNumber,
                    PageSize      = pageSize,
                    SearchTerm    = searchTerm,
                    Vertical      = vertical,
                    Country       = country,
                    CustomFilters = customFilters
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
        [Authorize(Policy = Policies.CompaniesRead)]
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

        // ── LOOKUP (078) ──────────────────────────────────────────────────────

        [HttpGet("lookup")]
        [Authorize(Policy = Policies.CompaniesRead)]
        [ProducesResponseType(typeof(List<CompanyListItem>), 200)]
        public async Task<IActionResult> GetLookup(CancellationToken cancellationToken = default)
        {
            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();
                return Ok(await _getLookup.Handle(tenantId, cancellationToken));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting company lookup");
                return StatusCode(500, "An error occurred");
            }
        }

        // ── STATS ─────────────────────────────────────────────────────────────

        [HttpGet("stats")]
        [Authorize(Policy = Policies.CompaniesRead)]
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
        [Authorize(Policy = Policies.CompaniesCreate)]
        [ProducesResponseType(typeof(CompanyDto), 200)]
        [ProducesResponseType(400)]
        [ProducesResponseType(422)]
        public async Task<IActionResult> Create(
            [FromBody] CreateCompanyDto dto,   // ✅ FIX: DTO not command — TenantId from server
            CancellationToken cancellationToken = default)
        {
            // ✅ SECURITY FIX: TenantId resolved server-side, never trusted from client
            var tenantId = _currentUserService.GetCurrentTenantId();

            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                var currentUser = await _currentUserService.GetCurrentUserAsync();

                var command = new CreateCompanyCommand
                {
                    TenantId     = tenantId,
                    Name         = dto.Name,
                    Country      = dto.Country,
                    TaxId        = dto.TaxId,
                    Vertical     = dto.Vertical,
                    CreatedBy    = currentUser.FullName,
                    CustomFields = dto.CustomFields      // 078 — null = say nothing
                };

                var company = await _createCompany.Handle(command, cancellationToken);
                _logger.LogInformation("Company created: {Id}", company.Id);
                return Ok(company);
            }
            catch (PlanLimitExceededException ex)            // ✅ SPECIFIC BEFORE GENERIC
            {
                _logger.LogWarning("Plan limit exceeded for tenant {TenantId}: {Message}",
                    tenantId, ex.Message);
                return UnprocessableEntity(new
                {
                    error = "plan_limit_exceeded",
                    message = ex.Message,
                    resource = ex.Resource,
                    current = ex.Current,
                    limit = ex.Limit
                });
            }
            catch (CustomFieldValidationException ex)        // 078 — a sentence for the person
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating company");
                return StatusCode(500, "An error occurred");
            }
        }

        // ── UPDATE ────────────────────────────────────────────────────────────

        [HttpPut("{id:guid}")]
        [Authorize(Policy = Policies.CompaniesUpdate)]
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
                    return BadRequest(new { error = "ID mismatch" });

                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                // ✅ SECURITY FIX: TenantId resolved server-side
                var tenantId    = _currentUserService.GetCurrentTenantId();
                var currentUser = await _currentUserService.GetCurrentUserAsync();

                var command = new UpdateCompanyCommand
                {
                    Id           = id,
                    TenantId     = tenantId,
                    Name         = dto.Name,
                    Country      = dto.Country,
                    TaxId        = dto.TaxId,
                    Vertical     = dto.Vertical,
                    UpdatedBy    = currentUser.FullName,
                    CustomFields = dto.CustomFields      // 078 — null = say nothing
                };

                await _updateCompany.Handle(command, cancellationToken);
                _logger.LogInformation("Company updated: {Id}", id);
                return NoContent();
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
            catch (CustomFieldValidationException ex)        // 078 — a sentence for the person
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating company {Id}", id);
                return StatusCode(500, "An error occurred");
            }
        }

        // ── DELETE ────────────────────────────────────────────────────────────

        [HttpDelete("{id:guid}")]
        [Authorize(Policy = Policies.CompaniesDelete)]
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
