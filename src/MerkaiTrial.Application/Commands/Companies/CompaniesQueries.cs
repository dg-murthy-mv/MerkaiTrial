// =====================================================================
// COMPANIES QUERIES
// Location: MerkaiTrial.Application/Commands/Companies/CompaniesQueries.cs
//
// FIXES:
//   - ContactCount: real count from Contacts table
//   - DealCount: real count via Contacts → Deals
//   - TotalContacts / TotalDeals in stats: real counts
//   - Removed "coming soon" hardcoded 0s
//
// 078 — CUSTOM FIELDS ON THE COMPANIES LIST, AND THE LIST FIXES CONTACTS
// GOT IN 076
//
//   1. SEARCH also finds custom TEXT values and DROPDOWN choice labels
//      ("Gold" finds every company whose tier is Gold), through the shared
//      CustomFieldListQuery — the same code the Contacts list and the
//      pipeline use, so the three can never disagree about what a search
//      or a filter means.
//
//   2. CUSTOM FIELD FILTERS (GetCompaniesQuery.CustomFilters), ANDed with
//      the search and the Vertical/Country filters, as id subqueries.
//
//   3. LIST COLUMN VALUES (CompanyListItem.CustomFieldValues) for the
//      fields marked "Show on the list", for THIS page's rows only — one
//      query, not one per row.
//
//   4. THE PAGE SIZE IS CLAMPED to 1–100. Before, ?pageSize=1000000 was
//      honoured, and a page number of 0 or less asked SQL for a negative
//      OFFSET, which throws.
//
//   5. A STABLE ORDER: Name, then Id. Two companies with the same name
//      could swap places between page loads, so paging could show one
//      twice and skip the other.
//
//   6. A PAGE PAST THE END is pulled back to the last page, so deleting
//      the only company on the last page does not leave an empty table
//      under a pager that says "page 4 of 3".
//
//   7. GetCompanyByIdHandler returns the stored custom field values
//      (CompanyDto.CustomFieldValues), read through
//      CustomFieldValueWriter.ReadAsync so the wire format has one author.
//
//   8. The stats and by-id counts no longer pull every company id and
//      every contact id into the web server's memory to send them back as
//      a giant IN list. They are subqueries now: the same numbers, worked
//      out by SQL Server in one round trip each. The "Contacts" tile now
//      counts contacts of LIVE companies only, matching the per-company
//      counts it sits above (before, a contact still pointing at a deleted
//      company was counted in the tile but under no company).
//
//   9. NEW GetCompanyLookupHandler, behind GET /api/companies/lookup:
//      every live company, sorted, without the per-row counts — for
//      dropdowns. ICompanyService.GetLookupAsync used to borrow the paged
//      list with pageSize=1000, and the list is capped at 100 from now on.
// =====================================================================

using MerkaiTrial.Application.Commands.CustomFields;
using MerkaiTrial.Application.Configuration;
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

        /// <summary>078. ANDed custom field conditions. Empty = none.</summary>
        public List<CustomFieldFilter> CustomFilters { get; set; } = new();
    }

    public class GetCompanyByIdQuery : ICommandHandler
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
    }

    public class GetCompaniesHandler : ICommandHandler
    {
        public const int MaxPageSize = 100;

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
                var tenantId = request.TenantId;
                var pageSize = Math.Clamp(request.PageSize, 1, MaxPageSize);   // note 4
                var page     = Math.Max(request.PageNumber, 1);

                // The ACTIVE custom fields for Companies — needed for search,
                // filters and columns. One small query.
                var defs = await CustomFieldListQuery.ActiveDefinitionsAsync(
                    _context, tenantId, CustomFieldEntityTypes.Company, cancellationToken);

                var query = _context.Companies
                    .AsNoTracking()
                    .Where(c => c.TenantId == tenantId && !c.IsDeleted);

                // ── 1. SEARCH ────────────────────────────────────────────
                if (!string.IsNullOrWhiteSpace(request.SearchTerm))
                {
                    var search = request.SearchTerm.Trim().ToLower();

                    // Null = no custom field could possibly match.
                    var cfMatches = CustomFieldListQuery.SearchMatches(_context, tenantId, defs, search);

                    if (cfMatches is null)
                    {
                        query = query.Where(c =>
                            c.Name.ToLower().Contains(search) ||
                            (c.TaxId != null && c.TaxId.ToLower().Contains(search)));
                    }
                    else
                    {
                        query = query.Where(c =>
                            c.Name.ToLower().Contains(search) ||
                            (c.TaxId != null && c.TaxId.ToLower().Contains(search)) ||
                            cfMatches.Contains(c.Id));
                    }
                }

                if (!string.IsNullOrWhiteSpace(request.Vertical))
                {
                    var vertical = request.Vertical.Trim();
                    query = query.Where(c => c.Vertical == vertical);
                }

                if (!string.IsNullOrWhiteSpace(request.Country))
                {
                    var country = request.Country.Trim();
                    query = query.Where(c => c.Country == country);
                }

                // ── 2. CUSTOM FIELD FILTERS ──────────────────────────────
                foreach (var clause in CustomFieldListQuery.FilterClauses(_context, tenantId, defs, request.CustomFilters))
                {
                    // A local, so EF inlines the subquery (see CustomFieldListQuery).
                    var ids = clause.EntityIds;
                    query = clause.Exclude
                        ? query.Where(c => !ids.Contains(c.Id))
                        : query.Where(c => ids.Contains(c.Id));
                }

                var totalCount = await query.CountAsync(cancellationToken);

                // ── 6. a page past the end → the last page ───────────────
                var totalPages = totalCount == 0 ? 1 : (int)Math.Ceiling(totalCount / (double)pageSize);
                if (page > totalPages) page = totalPages;

                // ✅ FIX: real ContactCount and DealCount via subqueries
                var companies = await query
                    .OrderBy(c => c.Name)
                    .ThenBy(c => c.Id)                  // note 5 — a stable order
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
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

                // ── 3. LIST COLUMN VALUES, this page only ────────────────
                var listValues = await CustomFieldListQuery.ListValuesAsync(
                    _context, tenantId, defs, companies.Select(c => c.Id).ToList(), cancellationToken);

                foreach (var c in companies)
                    if (listValues.TryGetValue(c.Id, out var map))
                        c.CustomFieldValues = map;

                _logger.LogInformation("Found {Count} companies (Total: {Total})",
                    companies.Count, totalCount);

                return new PaginatedResult<CompanyListItem>
                {
                    Items      = companies,
                    Page       = page,
                    PageSize   = pageSize,
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

    // ==================== GET COMPANY LOOKUP (078) ====================

    /// <summary>
    /// Every live company, sorted by name, for dropdowns — note 9. No
    /// counts (they are two subqueries a row and no dropdown shows them).
    /// Capped at <see cref="MaxRows"/> as a safety net; a workspace past
    /// that needs a type-ahead, not a longer list.
    /// </summary>
    public class GetCompanyLookupHandler : ICommandHandler
    {
        public const int MaxRows = 5000;

        private readonly FlowDbContext _context;

        public GetCompanyLookupHandler(FlowDbContext context)
        {
            _context = context;
        }

        public Task<List<CompanyListItem>> Handle(Guid tenantId, CancellationToken cancellationToken = default)
            => _context.Companies
                .AsNoTracking()
                .Where(c => c.TenantId == tenantId && !c.IsDeleted)
                .OrderBy(c => c.Name)
                .ThenBy(c => c.Id)
                .Take(MaxRows)
                .Select(c => new CompanyListItem
                {
                    Id           = c.Id,
                    TenantId     = c.TenantId,
                    Name         = c.Name,
                    Country      = c.Country,
                    TaxId        = c.TaxId,
                    Vertical     = c.Vertical,
                    CreatedAtUtc = c.CreatedAtUtc
                })
                .ToListAsync(cancellationToken);
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
            // Subqueries, not lists in memory — note 8.
            var liveCompanies = _context.Companies
                .Where(c => c.TenantId == tenantId && !c.IsDeleted);

            var liveCompanyIds = liveCompanies.Select(c => c.Id);

            var totalCompanies = await liveCompanies.CountAsync(cancellationToken);

            var companyContacts = _context.Contacts
                .Where(c => c.TenantId == tenantId &&
                            !c.IsDeleted &&
                            c.CompanyId != null &&
                            liveCompanyIds.Contains(c.CompanyId.Value));

            var totalContacts = await companyContacts.CountAsync(cancellationToken);

            var companyContactIds = companyContacts.Select(c => c.Id);

            var totalDeals = await _context.Deals
                .CountAsync(d => !d.IsDeleted && companyContactIds.Contains(d.ContactId),
                    cancellationToken);

            var activeVerticals = await liveCompanies
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
                var companyContacts = _context.Contacts
                    .Where(c => c.CompanyId == company.Id && !c.IsDeleted);

                var contactCount = await companyContacts.CountAsync(cancellationToken);

                var primaryContactCount = await companyContacts
                    .CountAsync(c => c.IsPrimary, cancellationToken);

                // A subquery, not a list in memory — note 8.
                var contactIds = companyContacts.Select(c => c.Id);

                var dealCount = await _context.Deals
                    .CountAsync(d => contactIds.Contains(d.ContactId) && !d.IsDeleted,
                        cancellationToken);

                // 078 — every stored custom value, retired fields included.
                var customFieldValues = await CustomFieldValueWriter.ReadAsync(
                    _context, request.TenantId, CustomFieldEntityTypes.Company, company.Id, cancellationToken);

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
                    UpdatedBy           = company.UpdatedBy,
                    CustomFieldValues   = customFieldValues     // 078
                };
            }
            catch (KeyNotFoundException)
            {
                throw;   // expected — the controller returns 404; not an ERROR line
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting company {Id}", request.Id);
                throw;
            }
        }
    }
}
