using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

// =====================================================================
// TaxRatesQueries.cs
// COMPLETE FILE — 056.
//
// Every `t.TenantId == null` in here was DEAD. TenantId was a
// non-nullable Guid, so that comparison is always false and the compiler
// says so (CS8073). It appeared three times and did nothing in any of
// them:
//
//   • the list's "system rates plus mine" clause showed only mine
//   • the stats' SystemRates count was always 0
//   • GetDefaultTaxRateForCountry's system path could only ever throw
//
// TenantId is nullable now, so the same expressions finally mean what
// they were written to mean — and most of them are no longer needed at
// all, because FlowDbContext's query filter does the work: a tenant sees
// NULL plus their own, a super admin (CurrentTenantId = Guid.Empty) sees
// NULL only. Hand-written tenant clauses on top of that filter were
// where the drift came from, so they are gone.
// =====================================================================

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

        /// <summary>
        /// 057. A MANAGEMENT screen must see inactive and expired rates —
        /// otherwise a rate switched off is invisible to the only page that
        /// could switch it back on, which is a one-way door.
        ///
        /// Defaults to FALSE so a picker or a lookup still gets only rates
        /// worth choosing. The list screens pass true.
        /// </summary>
        public bool IncludeInactive { get; set; }
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
            // 056. No tenant clause here on purpose. The global query filter
            // already resolves to "system rates plus this tenant's" for a
            // tenant, and "system rates only" for a super admin. The clause
            // that used to sit here duplicated that rule and got it wrong.
            //
            // IsActive stays in the predicate, but see the note on the list
            // screen: nothing can currently set it false, so an inactive rate
            // would be invisible to the one screen that could reactivate it.
            var queryable = _context.TaxRates.Where(t => !t.IsDeleted);

            // 057. Was an unconditional `&& t.IsActive`, which hid a
            // switched-off rate from the management screen as well as from
            // the pickers.
            if (!query.IncludeInactive)
                queryable = queryable.Where(t => t.IsActive);

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
                    IsDefault = t.IsDefault,
                    TenantId = t.TenantId,         // 056: NULL = system
                    IsActive = t.IsActive,         // 057
                    EffectiveFrom = t.EffectiveFrom,
                    EffectiveTo = t.EffectiveTo
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

            // 056: this finally counts something. TenantId was non-nullable,
            // so the predicate was always false and this tile read 0 on every
            // deployment since it was written.
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
            // 056. The query filter scopes this: a tenant can fetch a system
            // rate or one of their own and nothing else. It used to be Id
            // alone against a strict filter, which meant a tenant could not
            // read a system rate at all.
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
            // 056. The WHERE is the filter's job; what this handler owns is
            // the PREFERENCE — a tenant's own rate beats the system one — and
            // the EFFECTIVE DATES, which this query never applied even though
            // the columns exist and CurrentTenantService honours them.
            var now = DateTime.UtcNow;

            var queryable = _context.TaxRates
                .Where(t => t.CountryCode == query.CountryCode
                         && t.IsDefault
                         && !t.IsDeleted
                         && t.IsActive
                         && (t.EffectiveFrom == null || t.EffectiveFrom <= now)
                         && (t.EffectiveTo == null || t.EffectiveTo >= now));

            var taxRate = await queryable
                .Join(_context.Countries,
                    tr => tr.CountryCode,
                    c => c.Code,
                    (tr, c) => new { TaxRate = tr, Country = c })
                // 056: tenant rate first, system second. The old expression
                // read the same way but could never fire, because
                // `TenantId != null` was always TRUE on a non-nullable Guid —
                // so the ordering was constant and the winner was whichever
                // row the database felt like returning.
                .OrderBy(x => x.TaxRate.TenantId == null ? 1 : 0)
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
                    EffectiveFrom = x.TaxRate.EffectiveFrom,   // 056
                    EffectiveTo = x.TaxRate.EffectiveTo,       // 056
                    IsActive = x.TaxRate.IsActive
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (taxRate == null)
                throw new KeyNotFoundException($"No default tax rate found for country '{query.CountryCode}'");

            return taxRate;
        }
    }
}