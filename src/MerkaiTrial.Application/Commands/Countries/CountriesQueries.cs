using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Application.Commands.Countries
{
    // =====================================================================
    // GET COUNTRIES QUERY (WITH PAGINATION)
    // =====================================================================
    public class GetCountriesQuery
    {
        public bool ActiveOnly { get; set; } = true;
        public int PageNumber { get; set; } = 1;
        public int PageSize { get; set; } = 10;
        public string? SearchTerm { get; set; }
        public string? CurrencyFilter { get; set; }
        public bool ShowInactiveOnly { get; set; }
    }

    public class GetCountriesHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;

        public GetCountriesHandler(FlowDbContext context)
        {
            _context = context;
        }

        public async Task<PaginatedResult<CountryListItem>> Handle(
            GetCountriesQuery query,
            CancellationToken cancellationToken = default)
        {
            var queryable = _context.Countries.AsQueryable();

            // Active/Inactive filter
            if (query.ShowInactiveOnly)
            {
                queryable = queryable.Where(c => !c.IsActive);
            }
            else if (query.ActiveOnly)
            {
                queryable = queryable.Where(c => c.IsActive);
            }

            // Search filter
            if (!string.IsNullOrWhiteSpace(query.SearchTerm))
            {
                var search = query.SearchTerm.ToLower();
                queryable = queryable.Where(c =>
                    c.Name.ToLower().Contains(search) ||
                    c.Code.ToLower().Contains(search) ||
                    (c.CurrencyCode != null && c.CurrencyCode.ToLower().Contains(search)) ||
                    (c.DialCode != null && c.DialCode.Contains(search))
                );
            }

            // Currency filter
            if (!string.IsNullOrWhiteSpace(query.CurrencyFilter))
            {
                queryable = queryable.Where(c => c.CurrencyCode == query.CurrencyFilter);
            }

            // Get total count before pagination
            var totalCount = await queryable.CountAsync(cancellationToken);

            // Apply sorting and pagination
            var countries = await queryable
                .OrderBy(c => c.DisplayOrder)
                .ThenBy(c => c.Name)
                .Skip((query.PageNumber - 1) * query.PageSize)
                .Take(query.PageSize)
                .Select(c => new CountryListItem
                {
                    Code = c.Code,
                    Name = c.Name,
                    DialCode = c.DialCode,
                    CurrencyCode = c.CurrencyCode,
                    TaxLabel = c.TaxLabel,
                    DefaultTaxRate = c.DefaultTaxRate,
                    IsActive = c.IsActive
                })
                .ToListAsync(cancellationToken);

            return new PaginatedResult<CountryListItem>
            {
                Items = countries,
                Page = query.PageNumber,  // ✅ NEW - Use Page instead of Page
                PageSize = query.PageSize,
                TotalCount = totalCount
            };
        }
    }
    // ==================== GET COUNTRY STATISTICS ====================
    public class GetCountryStatsHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;

        public GetCountryStatsHandler(FlowDbContext context)
        {
            _context = context;
        }

        public async Task<CountryStatsDto> Handle(CancellationToken cancellationToken = default)
        {
            var totalCountries = await _context.Countries
                .CountAsync(c => !c.IsDeleted, cancellationToken);

            var activeCountries = await _context.Countries
                .CountAsync(c => c.IsActive && !c.IsDeleted, cancellationToken);

            var inactiveCountries = totalCountries - activeCountries;

            // Count unique currencies
            var totalCurrencies = await _context.Countries
                .Where(c => !c.IsDeleted && c.CurrencyCode != null)
                .Select(c => c.CurrencyCode)
                .Distinct()
                .CountAsync(cancellationToken);

            return new CountryStatsDto(
                TotalCountries: totalCountries,
                ActiveCountries: activeCountries,
                InactiveCountries: inactiveCountries,
                TotalCurrencies: totalCurrencies
            );
        }
    }
    // =====================================================================
    // GET COUNTRY BY CODE QUERY
    // =====================================================================
    public class GetCountryByCodeQuery
    {
        public string Code { get; set; } = string.Empty;
    }

    public class GetCountryByCodeHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;

        public GetCountryByCodeHandler(FlowDbContext context)
        {
            _context = context;
        }

        public async Task<CountryDto> Handle(GetCountryByCodeQuery query, CancellationToken cancellationToken = default)
        {
            var country = await _context.Countries
                .Where(c => c.Code == query.Code)
                .Select(c => new CountryDto
                {
                    Id = c.Id,
                    Code = c.Code,
                    Name = c.Name,
                    DialCode = c.DialCode,
                    CurrencyCode = c.CurrencyCode,
                    TaxLabel = c.TaxLabel,
                    DefaultTaxRate = c.DefaultTaxRate,
                    IsActive = c.IsActive,
                    DisplayOrder = c.DisplayOrder
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (country == null)
                throw new KeyNotFoundException($"Country with code '{query.Code}' not found");

            return country;
        }
    }
}