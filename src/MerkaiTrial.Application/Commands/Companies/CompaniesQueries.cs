// =====================================================================
// COMPANIES QUERIES
// Location: MerkaiTrial.Application/Commands/Companies/CompaniesQueries.cs
//
// FIXES:
//   - ContactCount: real count from Contacts table
//   - DealCount: real count via Contacts → Deals
//   - TotalContacts / TotalDeals in stats: real counts
//   - Removed "coming soon" hardcoded 0s
// =====================================================================

using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Commands.Companies
{
    // ==================== GET COMPANIES (WITH PAGINATION) ====================

    public class GetCompaniesQuery : ICommandHandler
    {
        public Guid TenantId { get; set; }
        public int PageNumber { get; set; } = 1;
        public int PageSize { get; set; } = 10;
        public string? SearchTerm { get; set; }
        public string? Vertical { get; set; }
        public string? Country { get; set; }
    }

    public class GetCompanyByIdQuery : ICommandHandler
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
    }

    public class GetCompaniesHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<GetCompaniesHandler> _logger;

        public GetCompaniesHandler(FlowDbContext context, ILogger<GetCompaniesHandler> logger)
        {
            _context = context;
            _logger  = logger;
        }

        public async Task<PaginatedResult<CompanyListItem>> Handle(
            GetCompaniesQuery request,
            CancellationToken cancellationToken)
        {
            try
            {
                var query = _context.Companies
                    .AsNoTracking()
                    .Where(c => c.TenantId == request.TenantId && !c.IsDeleted);

                if (!string.IsNullOrEmpty(request.SearchTerm))
                {
                    var search = request.SearchTerm.ToLower();
                    query = query.Where(c =>
                        c.Name.ToLower().Contains(search) ||
                        (c.TaxId != null && c.TaxId.ToLower().Contains(search)));
                }

                if (!string.IsNullOrEmpty(request.Vertical))
                    query = query.Where(c => c.Vertical == request.Vertical);

                if (!string.IsNullOrEmpty(request.Country))
                    query = query.Where(c => c.Country == request.Country);

                var totalCount = await query.CountAsync(cancellationToken);

                // ✅ FIX: real ContactCount and DealCount via subqueries
                var companies = await query
                    .OrderBy(c => c.Name)
                    .Skip((request.PageNumber - 1) * request.PageSize)
                    .Take(request.PageSize)
                    .Select(c => new CompanyListItem
                    {
                        Id           = c.Id,
                        TenantId     = c.TenantId,
                        Name         = c.Name,
                        Country      = c.Country,
                        TaxId        = c.TaxId,
                        Vertical     = c.Vertical,
                        // ✅ Real contact count for this company
                        ContactCount = _context.Contacts
                            .Count(ct => ct.CompanyId == c.Id && !ct.IsDeleted),
                        // ✅ Real deal count via contacts linked to this company
                        DealCount    = _context.Deals
                            .Count(d => _context.Contacts
                                .Where(ct => ct.CompanyId == c.Id && !ct.IsDeleted)
                                .Select(ct => ct.Id)
                                .Contains(d.ContactId) && !d.IsDeleted),
                        CreatedAtUtc = c.CreatedAtUtc
                    })
                    .ToListAsync(cancellationToken);

                _logger.LogInformation("Found {Count} companies (Total: {Total})",
                    companies.Count, totalCount);

                return new PaginatedResult<CompanyListItem>
                {
                    Items      = companies,
                    Page       = request.PageNumber,
                    PageSize   = request.PageSize,
                    TotalCount = totalCount
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting companies");
                throw;
            }
        }
    }

    // ==================== GET COMPANY STATS ====================

    public class GetCompanyStatsHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;

        public GetCompanyStatsHandler(FlowDbContext context)
        {
            _context = context;
        }

        public async Task<CompanyStatsDto> Handle(
            Guid tenantId,
            CancellationToken cancellationToken = default)
        {
            var totalCompanies = await _context.Companies
                .CountAsync(c => c.TenantId == tenantId && !c.IsDeleted, cancellationToken);

            // ✅ FIX: real total contacts for this tenant's companies
            var totalContacts = await _context.Contacts
                .CountAsync(c =>
                    c.TenantId == tenantId &&
                    !c.IsDeleted &&
                    c.CompanyId != null,
                    cancellationToken);

            // ✅ FIX: real total deals linked through contacts for this tenant
            var companyIds = await _context.Companies
                .Where(c => c.TenantId == tenantId && !c.IsDeleted)
                .Select(c => c.Id)
                .ToListAsync(cancellationToken);

            var contactIds = await _context.Contacts
                .Where(c => companyIds.Contains(c.CompanyId!.Value) && !c.IsDeleted)
                .Select(c => c.Id)
                .ToListAsync(cancellationToken);

            var totalDeals = await _context.Deals
                .CountAsync(d => contactIds.Contains(d.ContactId) && !d.IsDeleted,
                    cancellationToken);

            var activeVerticals = await _context.Companies
                .Where(c => c.TenantId == tenantId && !c.IsDeleted)
                .Select(c => c.Vertical)
                .Distinct()
                .CountAsync(cancellationToken);

            return new CompanyStatsDto(
                TotalCompanies:  totalCompanies,
                TotalContacts:   totalContacts,
                TotalDeals:      totalDeals,
                ActiveVerticals: activeVerticals
            );
        }
    }

    // ==================== GET COMPANY BY ID ====================

    public class GetCompanyByIdHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<GetCompanyByIdHandler> _logger;

        public GetCompanyByIdHandler(FlowDbContext context, ILogger<GetCompanyByIdHandler> logger)
        {
            _context = context;
            _logger  = logger;
        }

        public async Task<CompanyDto> Handle(
            GetCompanyByIdQuery request,
            CancellationToken cancellationToken)
        {
            try
            {
                var company = await _context.Companies
                    .AsNoTracking()
                    .Where(c => c.Id == request.Id && c.TenantId == request.TenantId && !c.IsDeleted)
                    .FirstOrDefaultAsync(cancellationToken);

                if (company == null)
                    throw new KeyNotFoundException($"Company {request.Id} not found");

                // ✅ FIX: real counts for detail view
                var contactCount = await _context.Contacts
                    .CountAsync(c => c.CompanyId == company.Id && !c.IsDeleted, cancellationToken);

                var primaryContactCount = await _context.Contacts
                    .CountAsync(c => c.CompanyId == company.Id && !c.IsDeleted && c.IsPrimary,
                        cancellationToken);

                var contactIds = await _context.Contacts
                    .Where(c => c.CompanyId == company.Id && !c.IsDeleted)
                    .Select(c => c.Id)
                    .ToListAsync(cancellationToken);

                var dealCount = await _context.Deals
                    .CountAsync(d => contactIds.Contains(d.ContactId) && !d.IsDeleted,
                        cancellationToken);

                return new CompanyDto
                {
                    Id                  = company.Id,
                    TenantId            = company.TenantId,
                    Name                = company.Name,
                    Country             = company.Country,
                    TaxId               = company.TaxId,
                    Vertical            = company.Vertical,
                    ContactCount        = contactCount,         // ✅ real
                    DealCount           = dealCount,            // ✅ real
                    PrimaryContactCount = primaryContactCount,  // ✅ real
                    CreatedAtUtc        = company.CreatedAtUtc,
                    CreatedBy           = company.CreatedBy,
                    UpdatedAtUtc        = company.UpdatedAtUtc,
                    UpdatedBy           = company.UpdatedBy
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting company {Id}", request.Id);
                throw;
            }
        }
    }
}
