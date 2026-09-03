// =====================================================================
// CONTACT QUERIES
// Location: MerkaiTrial.Application/Commands/Contacts/ContactsQueries.cs
//
// FIXES:
//   Bug 3 — DealCount now queries Deals table (was hardcoded 0)
//   Bug 4 — ActiveCompanies counts companies that have contacts (was all companies)
//   Bug 6 — GetContactLookupHandler added (flat list for dropdowns)
// =====================================================================

using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Application.Commands.Contacts
{
    // ==================== GET CONTACTS (PAGINATED) ====================

    public class GetContactsQuery : ICommandHandler
    {
        public Guid TenantId { get; set; }
        public int PageNumber { get; set; } = 1;
        public int PageSize { get; set; } = 10;
        public Guid? CompanyId { get; set; }
        public string? SearchTerm { get; set; }
        public bool? IsPrimary { get; set; }
    }

    public class GetContactsHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<GetContactsHandler> _logger;

        public GetContactsHandler(FlowDbContext context, ILogger<GetContactsHandler> logger)
        {
            _context = context;
            _logger  = logger;
        }

        public async Task<PaginatedResult<ContactListItem>> Handle(
            GetContactsQuery request,
            CancellationToken cancellationToken)
        {
            try
            {
                var query = _context.Contacts
                    .AsNoTracking()
                    .Where(c => c.TenantId == request.TenantId && !c.IsDeleted);

                if (request.CompanyId.HasValue)
                    query = query.Where(c => c.CompanyId == request.CompanyId.Value);

                if (request.IsPrimary.HasValue)
                    query = query.Where(c => c.IsPrimary == request.IsPrimary.Value);

                if (!string.IsNullOrEmpty(request.SearchTerm))
                {
                    var search = request.SearchTerm.ToLower();
                    query = query.Where(c =>
                        c.FirstName.ToLower().Contains(search) ||
                        (c.LastName  != null && c.LastName.ToLower().Contains(search)) ||
                        (c.Email     != null && c.Email.ToLower().Contains(search)) ||
                        (c.Phone     != null && c.Phone.Contains(search)) ||
                        (c.Mobile    != null && c.Mobile.Contains(search)));
                }

                var totalCount = await query.CountAsync(cancellationToken);

                // ✅ BUG 3 FIX: subquery for real DealCount per contact
                var contacts = await query
                    .Include(c => c.Company)
                    .OrderBy(c => c.FirstName)
                    .ThenBy(c => c.LastName)
                    .Skip((request.PageNumber - 1) * request.PageSize)
                    .Take(request.PageSize)
                    .Select(c => new ContactListItem
                    {
                        Id          = c.Id,
                        TenantId    = c.TenantId,
                        CompanyId   = c.CompanyId,
                        FirstName   = c.FirstName,
                        LastName    = c.LastName,
                        JobTitle    = c.JobTitle,
                        Email       = c.Email,
                        Phone       = c.Phone,
                        Mobile      = c.Mobile,
                        CompanyName = c.Company != null ? c.Company.Name : null,
                        IsPrimary   = c.IsPrimary,
                        // ✅ BUG 3: real count from Deals table
                        DealCount   = _context.Deals
                                        .Count(d => d.ContactId == c.Id && !d.IsDeleted),
                        CreatedAtUtc = c.CreatedAtUtc
                    })
                    .ToListAsync(cancellationToken);

                _logger.LogInformation("Found {Count} contacts (Total: {Total})",
                    contacts.Count, totalCount);

                return new PaginatedResult<ContactListItem>
                {
                    Items      = contacts,
                    Page       = request.PageNumber,
                    PageSize   = request.PageSize,
                    TotalCount = totalCount
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting contacts");
                throw;
            }
        }
    }

    // ==================== GET CONTACT STATS ====================

    public class GetContactStatsHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;

        public GetContactStatsHandler(FlowDbContext context)
        {
            _context = context;
        }

        public async Task<ContactStatsDto> Handle(
            Guid tenantId,
            CancellationToken cancellationToken = default)
        {
            var totalContacts = await _context.Contacts
                .CountAsync(c => c.TenantId == tenantId && !c.IsDeleted, cancellationToken);

            var primaryContacts = await _context.Contacts
                .CountAsync(c => c.TenantId == tenantId && !c.IsDeleted && c.IsPrimary, cancellationToken);

            var contactsWithCompany = await _context.Contacts
                .CountAsync(c => c.TenantId == tenantId && !c.IsDeleted && c.CompanyId != null, cancellationToken);

            // ✅ BUG 4 FIX: count only companies that have at least one active contact
            // (was counting all non-deleted companies regardless of contacts)
            var activeCompanies = await _context.Companies
                .Where(co => co.TenantId == tenantId && !co.IsDeleted)
                .Where(co => _context.Contacts
                    .Any(c => c.CompanyId == co.Id && !c.IsDeleted))
                .CountAsync(cancellationToken);

            return new ContactStatsDto(
                TotalContacts:       totalContacts,
                PrimaryContacts:     primaryContacts,
                ContactsWithCompany: contactsWithCompany,
                ActiveCompanies:     activeCompanies
            );
        }
    }

    // ==================== GET CONTACT BY ID ====================

    public class GetContactByIdQuery : ICommandHandler
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
    }

    public class GetContactByIdHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<GetContactByIdHandler> _logger;

        public GetContactByIdHandler(FlowDbContext context, ILogger<GetContactByIdHandler> logger)
        {
            _context = context;
            _logger  = logger;
        }

        public async Task<ContactDto> Handle(GetContactByIdQuery request, CancellationToken cancellationToken)
        {
            try
            {
                var contact = await _context.Contacts
                    .AsNoTracking()
                    .Where(c => c.Id == request.Id && c.TenantId == request.TenantId && !c.IsDeleted)
                    .Include(c => c.Company)
                    .FirstOrDefaultAsync(cancellationToken);

                if (contact == null)
                    throw new KeyNotFoundException($"Contact {request.Id} not found");

                // ✅ BUG 3 FIX: real DealCount for the detail page "Active Deals" badge
                var dealCount = await _context.Deals
                    .CountAsync(d => d.ContactId == contact.Id && !d.IsDeleted, cancellationToken);

                return new ContactDto
                {
                    Id           = contact.Id,
                    TenantId     = contact.TenantId,
                    CompanyId    = contact.CompanyId,
                    FirstName    = contact.FirstName,
                    LastName     = contact.LastName,
                    JobTitle     = contact.JobTitle,
                    Email        = contact.Email,
                    Phone        = contact.Phone,
                    Mobile       = contact.Mobile,
                    Address      = contact.Address,
                    City         = contact.City,
                    Country      = contact.Country,
                    PostalCode   = contact.PostalCode,
                    Notes        = contact.Notes,
                    IsPrimary    = contact.IsPrimary,
                    LineUserId   = contact.LineUserId,
                    CompanyName  = contact.Company?.Name,
                    DealCount    = dealCount,              // ✅ BUG 3
                    CreatedAtUtc = contact.CreatedAtUtc,
                    CreatedBy    = contact.CreatedBy,
                    UpdatedAtUtc = contact.UpdatedAtUtc,
                    UpdatedBy    = contact.UpdatedBy
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting contact {Id}", request.Id);
                throw;
            }
        }
    }

    // ==================== GET CONTACT LOOKUP (flat list for dropdowns) ====================
    // ✅ BUG 6: new handler — returns lightweight list for Deal/Lead/Quote dropdowns

    public class GetContactLookupQuery : ICommandHandler
    {
        public Guid TenantId { get; set; }
    }

    public class GetContactLookupHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<GetContactLookupHandler> _logger;

        public GetContactLookupHandler(FlowDbContext context, ILogger<GetContactLookupHandler> logger)
        {
            _context = context;
            _logger  = logger;
        }

        public async Task<List<ContactListItem>> Handle(
            GetContactLookupQuery request,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var contacts = await _context.Contacts
                    .AsNoTracking()
                    .Where(c => c.TenantId == request.TenantId && !c.IsDeleted)
                    .Include(c => c.Company)
                    .OrderBy(c => c.FirstName)
                    .ThenBy(c => c.LastName)
                    .Select(c => new ContactListItem
                    {
                        Id          = c.Id,
                        TenantId    = c.TenantId,
                        CompanyId   = c.CompanyId,
                        FirstName   = c.FirstName,
                        LastName    = c.LastName,
                        JobTitle    = c.JobTitle,
                        Email       = c.Email,
                        Phone       = c.Phone,
                        Mobile      = c.Mobile,
                        CompanyName = c.Company != null ? c.Company.Name : null,
                        IsPrimary   = c.IsPrimary,
                        DealCount   = 0,   // intentionally 0 — lookup only needs identity fields
                        CreatedAtUtc = c.CreatedAtUtc
                    })
                    .ToListAsync(cancellationToken);

                _logger.LogInformation("Contact lookup returned {Count} for tenant {TenantId}",
                    contacts.Count, request.TenantId);

                return contacts;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting contact lookup");
                throw;
            }
        }
    }

    // ==================== GET CONTACTS BY COMPANY ====================

    public class GetContactsByCompanyQuery : ICommandHandler
    {
        public Guid TenantId { get; set; }
        public Guid CompanyId { get; set; }
    }

    public class GetContactsByCompanyHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<GetContactsByCompanyHandler> _logger;

        public GetContactsByCompanyHandler(FlowDbContext context, ILogger<GetContactsByCompanyHandler> logger)
        {
            _context = context;
            _logger  = logger;
        }

        public async Task<List<ContactListItem>> Handle(
            GetContactsByCompanyQuery request,
            CancellationToken cancellationToken)
        {
            try
            {
                var contacts = await _context.Contacts
                    .AsNoTracking()
                    .Where(c => c.TenantId == request.TenantId &&
                                c.CompanyId == request.CompanyId &&
                                !c.IsDeleted)
                    .Include(c => c.Company)
                    .OrderByDescending(c => c.IsPrimary)
                    .ThenBy(c => c.FirstName)
                    .Select(c => new ContactListItem
                    {
                        Id          = c.Id,
                        TenantId    = c.TenantId,
                        CompanyId   = c.CompanyId,
                        FirstName   = c.FirstName,
                        LastName    = c.LastName,
                        JobTitle    = c.JobTitle,
                        Email       = c.Email,
                        Phone       = c.Phone,
                        Mobile      = c.Mobile,
                        CompanyName = c.Company != null ? c.Company.Name : null,
                        IsPrimary   = c.IsPrimary,
                        // ✅ BUG 3 FIX: real DealCount
                        DealCount   = _context.Deals
                                        .Count(d => d.ContactId == c.Id && !d.IsDeleted),
                        CreatedAtUtc = c.CreatedAtUtc
                    })
                    .ToListAsync(cancellationToken);

                return contacts;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting contacts for company {CompanyId}", request.CompanyId);
                throw;
            }
        }
    }

    // ==================== GET COMPANIES LOOKUP (for Contact Create/Edit dropdown) ====================

    public class GetCompaniesLookupQuery : ICommandHandler
    {
        public Guid TenantId { get; set; }
    }

    public class GetCompaniesLookupHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<GetCompaniesLookupHandler> _logger;

        public GetCompaniesLookupHandler(FlowDbContext context, ILogger<GetCompaniesLookupHandler> logger)
        {
            _context = context;
            _logger  = logger;
        }

        public async Task<List<CompanyListItem>> Handle(
            GetCompaniesLookupQuery request,
            CancellationToken cancellationToken)
        {
            try
            {
                var companies = await _context.Companies
                    .AsNoTracking()
                    .Where(c => c.TenantId == request.TenantId && !c.IsDeleted)
                    .OrderBy(c => c.Name)
                    .Select(c => new CompanyListItem
                    {
                        Id           = c.Id,
                        TenantId     = c.TenantId,
                        Name         = c.Name,
                        Vertical     = c.Vertical,
                        Country      = c.Country,
                        TaxId        = c.TaxId,
                        ContactCount = 0,
                        DealCount    = 0,
                        CreatedAtUtc = c.CreatedAtUtc
                    })
                    .ToListAsync(cancellationToken);

                _logger.LogInformation("Companies lookup returned {Count} for tenant {TenantId}",
                    companies.Count, request.TenantId);

                return companies;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting companies lookup");
                throw;
            }
        }
    }
}
