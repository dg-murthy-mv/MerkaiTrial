using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Application.Commands.TaxRates
{
    // =====================================================================
    // GET TAX RATES QUERY (WITH PAGINATION)
    // =====================================================================
    public class GetTaxRatesQuery
    {
        public Guid TenantId { get; set; }
        public string? CountryCode { get; set; }
        public int PageNumber { get; set; } = 1;
        public int PageSize { get; set; } = 10;
        public string? SearchTerm { get; set; }
    }

    public class GetTaxRatesHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;

        public GetTaxRatesHandler(FlowDbContext context)
        {
            _context = context;
        }

        public async Task<PaginatedResult<TaxRateListItem>> Handle(
            GetTaxRatesQuery query,
            CancellationToken cancellationToken = default)
        {
            var queryable = _context.TaxRates
            .Where(t => !t.IsDeleted && t.IsActive);

            if (query.TenantId != Guid.Empty)
            {
                // Show system rates (NULL) + specific tenant's rates
                queryable = queryable.Where(t => t.TenantId == null || t.TenantId == query.TenantId);
            }

            // Filter by country if specified
            if (!string.IsNullOrWhiteSpace(query.CountryCode))
                queryable = queryable.Where(t => t.CountryCode == query.CountryCode);

            // Search filter
            if (!string.IsNullOrWhiteSpace(query.SearchTerm))
            {
                var search = query.SearchTerm.ToLower();
                queryable = queryable.Where(t =>
                    t.Name.ToLower().Contains(search) ||
                    t.CountryCode.ToLower().Contains(search) ||
                    t.TaxType.ToLower().Contains(search)
                );
            }

            // Get total count before pagination
            var totalCount = await queryable.CountAsync(cancellationToken);

            // Apply sorting and pagination
            var taxRates = await queryable
                .OrderBy(t => t.CountryCode)
                .ThenByDescending(t => t.IsDefault)
                .ThenBy(t => t.Name)
                .Skip((query.PageNumber - 1) * query.PageSize)
                .Take(query.PageSize)
                .Select(t => new TaxRateListItem
                {
                    Id = t.Id,
                    CountryCode = t.CountryCode,
                    Name = t.Name,
                    TaxType = t.TaxType,
                    Rate = t.Rate,
                    IsDefault = t.IsDefault
                })
                .ToListAsync(cancellationToken);

            return new PaginatedResult<TaxRateListItem>
            {
                Items = taxRates,
                Page = query.PageNumber,
                PageSize = query.PageSize,
                TotalCount = totalCount
            };
        }
    }
    public class GetTaxRateStatsHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;

        public GetTaxRateStatsHandler(FlowDbContext context)
        {
            _context = context;
        }

        public async Task<TaxRateStatsDto> Handle(CancellationToken cancellationToken = default)
        {
            var totalRates = await _context.TaxRates
                .CountAsync(t => !t.IsDeleted && t.IsActive, cancellationToken);

            var activeCountries = await _context.TaxRates
                .Where(t => !t.IsDeleted && t.IsActive)
                .Select(t => t.CountryCode)
                .Distinct()
                .CountAsync(cancellationToken);

            var defaultRates = await _context.TaxRates
                .CountAsync(t => !t.IsDeleted && t.IsActive && t.IsDefault, cancellationToken);

            var systemRates = await _context.TaxRates
                .CountAsync(t => !t.IsDeleted && t.IsActive && t.TenantId == null, cancellationToken);

            return new TaxRateStatsDto(
                TotalRates: totalRates,
                ActiveCountries: activeCountries,
                DefaultRates: defaultRates,
                SystemRates: systemRates
            );
        }
    }

    // =====================================================================
    // GET TAX RATE BY ID QUERY
    // =====================================================================
    public class GetTaxRateByIdQuery
    {
        public Guid Id { get; set; }
    }

    public class GetTaxRateByIdHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;

        public GetTaxRateByIdHandler(FlowDbContext context)
        {
            _context = context;
        }

        public async Task<TaxRateDto> Handle(GetTaxRateByIdQuery query, CancellationToken cancellationToken = default)
        {
            var taxRate = await _context.TaxRates
                .Where(t => t.Id == query.Id && !t.IsDeleted)
                .Join(_context.Countries,
                    tr => tr.CountryCode,
                    c => c.Code,
                    (tr, c) => new { TaxRate = tr, Country = c })
                .Select(x => new TaxRateDto
                {
                    Id = x.TaxRate.Id,
                    TenantId = x.TaxRate.TenantId,
                    CountryCode = x.TaxRate.CountryCode,
                    CountryName = x.Country.Name,
                    Name = x.TaxRate.Name,
                    TaxType = x.TaxRate.TaxType,
                    Rate = x.TaxRate.Rate,
                    IsDefault = x.TaxRate.IsDefault,
                    EffectiveFrom = x.TaxRate.EffectiveFrom,
                    EffectiveTo = x.TaxRate.EffectiveTo,
                    IsActive = x.TaxRate.IsActive
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (taxRate == null)
                throw new KeyNotFoundException($"Tax rate with ID '{query.Id}' not found");

            return taxRate;
        }
    }

    // =====================================================================
    // GET DEFAULT TAX RATE FOR COUNTRY QUERY
    // =====================================================================
    public class GetDefaultTaxRateForCountryQuery
    {
        public Guid TenantId { get; set; }
        public string CountryCode { get; set; } = string.Empty;
    }

    public class GetDefaultTaxRateForCountryHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;

        public GetDefaultTaxRateForCountryHandler(FlowDbContext context)
        {
            _context = context;
        }

        public async Task<TaxRateDto> Handle(GetDefaultTaxRateForCountryQuery query, CancellationToken cancellationToken = default)
        {
            // ✅ FIXED: Look for system rates (NULL) or tenant-specific rates
            var queryable = _context.TaxRates
                .Where(t => t.CountryCode == query.CountryCode
                         && t.IsDefault
                         && !t.IsDeleted
                         && t.IsActive);

            if (query.TenantId != Guid.Empty)
            {
                // Prefer tenant-specific, fallback to system
                queryable = queryable.Where(t => t.TenantId == null || t.TenantId == query.TenantId);
            }
            else
            {
                // Only system rates
                queryable = queryable.Where(t => t.TenantId == null);
            }

            var taxRate = await queryable
                .Join(_context.Countries,
                    tr => tr.CountryCode,
                    c => c.Code,
                    (tr, c) => new { TaxRate = tr, Country = c })
                .OrderByDescending(x => x.TaxRate.TenantId != null) // Tenant-specific first
                .Select(x => new TaxRateDto
                {
                    Id = x.TaxRate.Id,
                    TenantId = x.TaxRate.TenantId,
                    CountryCode = x.TaxRate.CountryCode,
                    CountryName = x.Country.Name,
                    Name = x.TaxRate.Name,
                    TaxType = x.TaxRate.TaxType,
                    Rate = x.TaxRate.Rate,
                    IsDefault = x.TaxRate.IsDefault,
                    IsActive = x.TaxRate.IsActive
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (taxRate == null)
                throw new KeyNotFoundException($"No default tax rate found for country '{query.CountryCode}'");

            return taxRate;
        }
    }
}