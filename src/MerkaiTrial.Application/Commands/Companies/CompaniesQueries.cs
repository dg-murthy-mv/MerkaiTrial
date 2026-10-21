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
//
// 096 — THE DEAL COUNT, AND SORTING
//
//  10. A COMPANY'S DEALS INCLUDE THE ONES LINKED TO IT DIRECTLY. A deal
//      was counted only when its CONTACT worked at the company, so a deal
//      whose own Company was set — while its contact sat elsewhere, or
//      had no company — counted under no company at all: not on the list,
//      not on the company page, not in the Deals tile. The invoice code
//      already treats the deal's own company as the customer. All three
//      counts now use one rule (DealsOf): the deal's company is this one,
//      OR its contact works here. A deal matching both counts once.
//
//  11. SORTING: GetCompaniesQuery.Sort / Dir — name, country, vertical,
//      contacts, deals or created, either way, always ending with Id.
//      Anything else is the original order (name). See ListSort.
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

        /// <summary>096. A key from GetCompaniesHandler.SortKeys; anything else = by name.</summary>
        public string? Sort { get; set; }

        /// <summary>096. "asc" or "desc".</summary>
        public string? Dir { get; set; }
    }

    /// <summary>
    /// 096 — note 10. The one rule for "this company's deals", used by the
    /// list, the stats tile and the company page so the three always agree.
    /// </summary>
    internal static class CompanyDeals
    {
        /// <summary>Live deals whose own company is <paramref name="companyId"/>, or whose contact works there.</summary>
        public static IQueryable<MerkaiTrial.Domain.Entities.Deal> Of(FlowDbContext db, Guid companyId)
            => db.Deals.Where(d => !d.IsDeleted &&
                   (d.CompanyId == companyId ||
                    db.Contacts.Any(ct => ct.Id == d.ContactId && ct.CompanyId == companyId && !ct.IsDeleted)));
    }

    public class GetCompanyByIdQuery : ICommandHandler
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
    }

    public class GetCompaniesHandler : ICommandHandler
    {
        public const int MaxPageSize = 100;

        /// <summary>096. The column keys the list can be sorted by.</summary>
        public static readonly string[] SortKeys = { "name", "country", "vertical", "contacts", "deals", "created" };

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
                var companies = await Ordered(query, request.Sort, request.Dir)   // 096 — note 11
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
                        // 096 — note 10: the deal's own company, or its contact's.
                        // Written out (not CompanyDeals.Of) because EF cannot
                        // translate a method call inside a projection.
                        DealCount    = _context.Deals
                            .Count(d => !d.IsDeleted &&
                                (d.CompanyId == c.Id ||
                                 _context.Contacts.Any(ct => ct.Id == d.ContactId && ct.CompanyId == c.Id && !ct.IsDeleted))),
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

        /// <summary>
        /// 096 — note 11. The requested order, always ending with Id (note 5).
        /// No sort, or one this list does not offer, is by name as before.
        /// </summary>
        private IOrderedQueryable<MerkaiTrial.Domain.Entities.Company> Ordered(
            IQueryable<MerkaiTrial.Domain.Entities.Company> companies, string? sort, string? dir)
        {
            var key  = ListSort.Key(sort, SortKeys);
            var desc = ListSort.IsDescending(dir, fallback: key is "contacts" or "deals" or "created");

            IOrderedQueryable<MerkaiTrial.Domain.Entities.Company> ordered = key switch
            {
                "country"  => companies.OrderByDir(c => c.Country, desc).ThenBy(c => c.Name),
                "vertical" => companies.OrderByDir(c => c.Vertical, desc).ThenBy(c => c.Name),
                "contacts" => companies.OrderByDir(c => _context.Contacts.Count(ct => ct.CompanyId == c.Id && !ct.IsDeleted), desc)
                                       .ThenBy(c => c.Name),
                "deals"    => companies.OrderByDir(c => _context.Deals.Count(d => !d.IsDeleted &&
                                            (d.CompanyId == c.Id ||
                                             _context.Contacts.Any(ct => ct.Id == d.ContactId && ct.CompanyId == c.Id && !ct.IsDeleted))), desc)
                                       .ThenBy(c => c.Name),
                "created"  => companies.OrderByDir(c => c.CreatedAtUtc, desc),
                "name"     => companies.OrderByDir(c => c.Name, desc),
                _          => companies.OrderBy(c => c.Name)
            };

            return ordered.ThenBy(c => c.Id);
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

            // 096 — note 10: a deal linked to a live company directly
            // counts too. Each deal at most once.
            var totalDeals = await _context.Deals
                .CountAsync(d => !d.IsDeleted &&
                                 ((d.CompanyId != null && liveCompanyIds.Contains(d.CompanyId.Value)) ||
                                  companyContactIds.Contains(d.ContactId)),
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

                // 096 — note 10: the same rule as the list and the tile.
                var dealCount = await CompanyDeals.Of(_context, company.Id)
                    .CountAsync(cancellationToken);

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
