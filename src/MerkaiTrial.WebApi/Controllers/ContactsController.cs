// =====================================================================
// CONTACTS CONTROLLER
// Location: MerkaiTrial.WebApi/Controllers/ContactsController.cs
//
// FIXES:
//   Bug 1 — /lookup now returns List<ContactListItem> (was returning companies)
//   Bug 6 — /companies-lookup added as dedicated company dropdown endpoint
// =====================================================================

using MerkaiTrial.Application.Commands.Contacts;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Exceptions;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.WebApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ContactsController : ControllerBase
    {
        private readonly GetContactsHandler _getContacts;
        private readonly GetContactByIdHandler _getContactById;
        private readonly GetContactsByCompanyHandler _getContactsByCompany;
        private readonly GetContactLookupHandler _getContactLookup;      // ✅ BUG 6: real contact lookup
        private readonly GetCompaniesLookupHandler _getCompaniesLookup;
        private readonly GetContactStatsHandler _getStats;
        private readonly CreateContactHandler _createContact;
        private readonly UpdateContactHandler _updateContact;
        private readonly DeleteContactHandler _deleteContact;
        private readonly ICurrentUserService _currentUserService;
        private readonly ILogger<ContactsController> _logger;

        public ContactsController(
            GetContactsHandler getContacts,
            GetContactByIdHandler getContactById,
            GetContactsByCompanyHandler getContactsByCompany,
            GetContactLookupHandler getContactLookup,                    // ✅ BUG 6
            GetCompaniesLookupHandler getCompaniesLookup,
            GetContactStatsHandler getStats,
            CreateContactHandler createContact,
            UpdateContactHandler updateContact,
            DeleteContactHandler deleteContact,
            ICurrentUserService currentUserService,
            ILogger<ContactsController> logger)
        {
            _getContacts          = getContacts;
            _getContactById       = getContactById;
            _getContactsByCompany = getContactsByCompany;
            _getContactLookup     = getContactLookup;                    // ✅ BUG 6
            _getCompaniesLookup   = getCompaniesLookup;
            _getStats             = getStats;
            _createContact        = createContact;
            _updateContact        = updateContact;
            _deleteContact        = deleteContact;
            _currentUserService   = currentUserService;
            _logger               = logger;
        }

        // ── GET ALL (PAGINATED) ───────────────────────────────────────────────

        [HttpGet]
        [Authorize(Policy = "Contacts.read")]
        [ProducesResponseType(typeof(PaginatedResult<ContactListItem>), 200)]
        public async Task<IActionResult> GetAll(
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] Guid? companyId = null,
            [FromQuery] string? searchTerm = null,
            [FromQuery] bool? isPrimary = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();
                var query = new GetContactsQuery
                {
                    TenantId   = tenantId,
                    PageNumber = pageNumber,
                    PageSize   = pageSize,
                    CompanyId  = companyId,
                    SearchTerm = searchTerm,
                    IsPrimary  = isPrimary
                };

                var result = await _getContacts.Handle(query, cancellationToken);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting contacts");
                return StatusCode(500, "An error occurred");
            }
        }

        // ── GET BY ID ─────────────────────────────────────────────────────────

        [HttpGet("{id:guid}")]
        [Authorize(Policy = "Contacts.read")]
        [ProducesResponseType(typeof(ContactDto), 200)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken = default)
        {
            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();
                var query = new GetContactByIdQuery { TenantId = tenantId, Id = id };
                var contact = await _getContactById.Handle(query, cancellationToken);
                return Ok(contact);
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting contact {Id}", id);
                return StatusCode(500, "An error occurred");
            }
        }

        // ── STATS ─────────────────────────────────────────────────────────────

        [HttpGet("stats")]
        [Authorize(Policy = "Contacts.read")]
        [ProducesResponseType(typeof(ContactStatsDto), 200)]
        public async Task<IActionResult> GetStats(CancellationToken cancellationToken = default)
        {
            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();
                var stats = await _getStats.Handle(tenantId, cancellationToken);
                return Ok(stats);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting contact statistics");
                return StatusCode(500, "An error occurred");
            }
        }

        // ── CONTACT LOOKUP (for dropdowns on Deal/Lead/Quote pages) ──────────
        // ✅ BUG 1 FIX: was returning CompanyListItem — now returns ContactListItem
        // ✅ BUG 6 FIX: this is the real contact lookup used by IContactService

        [HttpGet("lookup")]
        [Authorize(Policy = "Contacts.read")]
        [ProducesResponseType(typeof(List<ContactListItem>), 200)]
        public async Task<IActionResult> GetLookup(CancellationToken cancellationToken = default)
        {
            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();
                var query = new GetContactLookupQuery { TenantId = tenantId };
                var contacts = await _getContactLookup.Handle(query, cancellationToken);
                return Ok(contacts);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting contact lookup");
                return StatusCode(500, "An error occurred");
            }
        }

        // ── COMPANIES LOOKUP (for Company dropdown on Create/Edit Contact) ────
        // ✅ BUG 1 FIX: moved company lookup to its own dedicated route

        [HttpGet("companies-lookup")]
        [Authorize(Policy = "Contacts.read")]
        [ProducesResponseType(typeof(List<CompanyListItem>), 200)]
        public async Task<IActionResult> GetCompaniesLookup(CancellationToken cancellationToken = default)
        {
            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();
                var query = new GetCompaniesLookupQuery { TenantId = tenantId };
                var companies = await _getCompaniesLookup.Handle(query, cancellationToken);
                return Ok(companies);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting company lookup");
                return StatusCode(500, "An error occurred");
            }
        }

        // ── BY COMPANY ────────────────────────────────────────────────────────

        [HttpGet("by-company/{companyId:guid}")]
        [Authorize(Policy = "Contacts.read")]
        [ProducesResponseType(typeof(List<ContactListItem>), 200)]
        public async Task<IActionResult> GetByCompany(Guid companyId, CancellationToken cancellationToken = default)
        {
            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();
                var query = new GetContactsByCompanyQuery { TenantId = tenantId, CompanyId = companyId };
                var contacts = await _getContactsByCompany.Handle(query, cancellationToken);
                return Ok(contacts);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting contacts for company {CompanyId}", companyId);
                return StatusCode(500, "An error occurred");
            }
        }

        // ── CREATE ────────────────────────────────────────────────────────────

        [HttpPost]
        [Authorize(Policy = "Contacts.create")]
        [ProducesResponseType(typeof(ContactDto), 200)]
        [ProducesResponseType(400)]
        [ProducesResponseType(422)]
        public async Task<IActionResult> Create(
            [FromBody] CreateContactDto dto,
            CancellationToken cancellationToken = default)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                var tenantId    = _currentUserService.GetCurrentTenantId();
                var currentUser = await _currentUserService.GetCurrentUserAsync();

                var command = new CreateContactCommand
                {
                    TenantId   = tenantId,
                    CompanyId  = dto.CompanyId,
                    FirstName  = dto.FirstName,
                    LastName   = dto.LastName,
                    JobTitle   = dto.JobTitle,
                    Email      = dto.Email,
                    Phone      = dto.Phone,
                    Mobile     = dto.Mobile,
                    Address    = dto.Address,
                    City       = dto.City,
                    Country    = dto.Country,
                    PostalCode = dto.PostalCode,
                    Notes      = dto.Notes,
                    IsPrimary  = dto.IsPrimary,
                    CreatedBy  = currentUser.FullName
                };

                var contact = await _createContact.Handle(command, cancellationToken);
                _logger.LogInformation("Contact created: {Id}", contact.Id);
                return Ok(contact);
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
                _logger.LogError(ex, "Error creating contact");
                return StatusCode(500, "An error occurred");
            }
        }

        // ── UPDATE ────────────────────────────────────────────────────────────

        [HttpPut("{id:guid}")]
        [Authorize(Policy = "Contacts.update")]
        [ProducesResponseType(typeof(ContactDto), 200)]
        [ProducesResponseType(400)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> Update(
            Guid id,
            [FromBody] UpdateContactDto dto,
            CancellationToken cancellationToken = default)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                if (id != dto.Id)
                    return BadRequest("ID mismatch");

                var tenantId    = _currentUserService.GetCurrentTenantId();
                var currentUser = await _currentUserService.GetCurrentUserAsync();

                var command = new UpdateContactCommand
                {
                    Id         = id,
                    TenantId   = tenantId,
                    CompanyId  = dto.CompanyId,
                    FirstName  = dto.FirstName,
                    LastName   = dto.LastName,
                    JobTitle   = dto.JobTitle,
                    Email      = dto.Email,
                    Phone      = dto.Phone,
                    Mobile     = dto.Mobile,
                    Address    = dto.Address,
                    City       = dto.City,
                    Country    = dto.Country,
                    PostalCode = dto.PostalCode,
                    Notes      = dto.Notes,
                    IsPrimary  = dto.IsPrimary,
                    UpdatedBy  = currentUser.FullName
                };

                await _updateContact.Handle(command, cancellationToken);

                var query   = new GetContactByIdQuery { Id = id, TenantId = tenantId };
                var contact = await _getContactById.Handle(query, cancellationToken);

                _logger.LogInformation("Contact updated: {Id}", id);
                return Ok(contact);
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating contact {Id}", id);
                return StatusCode(500, "An error occurred");
            }
        }

        // ── DELETE ────────────────────────────────────────────────────────────

        [HttpDelete("{id:guid}")]
        [Authorize(Policy = "Contacts.delete")]
        [ProducesResponseType(204)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken = default)
        {
            try
            {
                var tenantId    = _currentUserService.GetCurrentTenantId();
                var currentUser = await _currentUserService.GetCurrentUserAsync();

                var command = new DeleteContactCommand
                {
                    Id        = id,
                    TenantId  = tenantId,
                    DeletedBy = currentUser.FullName
                };

                await _deleteContact.Handle(command, cancellationToken);
                _logger.LogInformation("Contact deleted: {Id}", id);
                return NoContent();
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting contact {Id}", id);
                return StatusCode(500, "An error occurred");
            }
        }
    }
}
