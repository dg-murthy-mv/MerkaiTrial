// =====================================================================
// TENANT QUERIES - All Read Operations
// Location: MerkaiTrial.Application/Commands/Tenants/TenantQueries.cs
// =====================================================================

using DocumentFormat.OpenXml.InkML;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Commands.Tenants
{
    // ==================== GET TENANTS PAGINATED ====================
    public class GetTenantsPaginatedHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<GetTenantsPaginatedHandler> _logger;

        public GetTenantsPaginatedHandler(FlowDbContext context, ILogger<GetTenantsPaginatedHandler> logger)
        {
            _context = context;
            _logger = logger;
        }

       

        public async Task<PaginatedTenantsResponse> Handle(
            int page,
            int pageSize,
            string? search,
            bool? isActive,
            CancellationToken cancellationToken = default)
            {
                try
                {
                    var query = _context.Tenants
                        .AsNoTracking()
                        .Where(t => !t.IsDeleted);

                    // Search (avoid ToLower on columns; SQL Server is case-insensitive by default)
                    if (!string.IsNullOrWhiteSpace(search))
                    {
                        var term = search.Trim();
                        query = query.Where(t =>
                            EF.Functions.Like(t.Name, $"%{term}%") ||
                            EF.Functions.Like(t.FromEmail, $"%{term}%"));
                    }

                    // Status filter
                    if (isActive.HasValue)
                    {
                        query = query.Where(t => t.IsActive == isActive.Value);
                    }

                    var totalCount = await query.CountAsync(cancellationToken);

                    // Page slice with country info via navigation, and counts as scalar subqueries
                    var pageItems = await query
                        .OrderByDescending(t => t.CreatedAtUtc ?? DateTime.MinValue)
                        .ThenBy(t => t.Name)
                        .Select(t => new
                        {
                            t.Id,
                            t.Name,
                            t.FromEmail,
                            t.DefaultCurrency,                      // string like "THB"
                            t.IsActive,
                            t.CreatedAtUtc,
                            CountryCode = t.Country != null ? t.Country.Code : null,
                            CountryName = t.Country != null ? t.Country.Name : null,

                            UserCount = _context.Users.Count(u => u.TenantId == t.Id && !u.IsDeleted),
                            LeadCount = _context.Leads.Count(l => l.TenantId == t.Id && !l.IsDeleted)
                        })
                        .Skip((page - 1) * pageSize)
                        .Take(pageSize)
                        .ToListAsync(cancellationToken);

                    // Map to your DTO; if TenantListItem expects a *string* Country, pass CountryCode (or build a display string)
                    var tenantItems = pageItems.Select(x => new TenantListItem(
                        Id: x.Id,
                        Name: x.Name,
                        FromEmail: x.FromEmail,
                        DefaultCurrency: x.DefaultCurrency,
                        CountryCode: x.CountryCode,
                        CountryName: x.CountryName,
                        IsActive: x.IsActive,
                        UserCount: x.UserCount,
                        LeadCount: x.LeadCount,
                        CreatedAtUtc: x.CreatedAtUtc ?? DateTime.UtcNow
                    )).ToList();

                    var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);

                    return new PaginatedTenantsResponse(
                        Items: tenantItems,
                        TotalCount: totalCount,
                        Page: page,
                        PageSize: pageSize,
                        TotalPages: totalPages
                    );
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error getting paginated tenants");
                    throw;
                }
            }

}

// ==================== GET TENANT DETAIL ====================
public class GetTenantDetailHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<GetTenantDetailHandler> _logger;

        public GetTenantDetailHandler(FlowDbContext context, ILogger<GetTenantDetailHandler> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<TenantDto> Handle(Guid tenantId, CancellationToken cancellationToken = default)
        {
            try
            {
                var dto = await _context.Tenants
                    .AsNoTracking()
                    .Where(t => t.Id == tenantId && !t.IsDeleted)
                    .Select(t => new TenantDto(
                        t.Id,                                        // Id
                        t.Name,                                      // Name
                        t.FromEmail,                                 // FromEmail
                        t.Phone,                                     // Phone
                        t.DefaultCurrency ?? string.Empty,           // DefaultCurrency (string)
                        t.Timezone ?? "UTC",                         // TimeZone
                        t.CountryId,                                 // CountryId (Guid?)
                        t.Country != null ? t.Country.Code : null,   // CountryCode
                        t.Country != null ? t.Country.Name : null,   // CountryName
                        t.IsActive,                                  // IsActive
                        t.CreatedAtUtc ?? DateTime.UtcNow,           // CreatedAtUtc
                        t.UpdatedAtUtc ?? DateTime.UtcNow,           // UpdatedAtUtc
                        t.Plan ?? "Starter"                          // Plan
                    ))
                    .FirstOrDefaultAsync(cancellationToken);

                if (dto is null)
                    throw new KeyNotFoundException($"Tenant {tenantId} not found");

                return dto;
            }
            catch (KeyNotFoundException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting tenant detail {TenantId}", tenantId);
                throw;
            }
        }

    }

    // ==================== GET TENANT SETTINGS ====================
    public class GetTenantSettingsHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<GetTenantSettingsHandler> _logger;

        public GetTenantSettingsHandler(FlowDbContext context, ILogger<GetTenantSettingsHandler> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<TenantSettingsDto> Handle(Guid tenantId, CancellationToken cancellationToken = default)
        {
            try
            {
                var settings = await _context.Set<TenantSettings>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);

                if (settings == null)
                {
                    // Return default settings if not found
                    return new TenantSettingsDto(
                        TenantId: tenantId,
                        MaxUsers: 5,
                        MaxLeads: 100,
                        MaxDeals: 50,
                        StorageLimit: 5368709120, // 5GB
                        FeatureFlags: null
                    );
                }

                return new TenantSettingsDto(
                    TenantId: settings.TenantId,
                    MaxUsers: settings.MaxUsers,
                    MaxLeads: settings.MaxLeads,
                    MaxDeals: settings.MaxDeals,
                    StorageLimit: settings.StorageLimit,
                    FeatureFlags: settings.FeatureFlags
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting tenant settings {TenantId}", tenantId);
                throw;
            }
        }
    }

    // ==================== GET TENANT STATISTICS ====================
    public class GetTenantStatsHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<GetTenantStatsHandler> _logger;

        public GetTenantStatsHandler(FlowDbContext context, ILogger<GetTenantStatsHandler> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<TenantStatsDto> Handle(Guid tenantId, CancellationToken cancellationToken = default)
        {
            try
            {
                var totalUsers = await _context.Users
                    .CountAsync(u => u.TenantId == tenantId && !u.IsDeleted, cancellationToken);

                var activeUsers = await _context.Users
                    .CountAsync(u => u.TenantId == tenantId && u.IsActive && !u.IsDeleted, cancellationToken);

                var totalLeads = await _context.Leads
                    .CountAsync(l => l.TenantId == tenantId && !l.IsDeleted, cancellationToken);

                var totalDeals = await _context.Deals
                    .CountAsync(d => d.TenantId == tenantId && !d.IsDeleted, cancellationToken);

                return new TenantStatsDto(
                    TotalUsers: totalUsers,
                    ActiveUsers: activeUsers,
                    InactiveUsers: totalUsers - activeUsers,
                    TotalLeads: totalLeads,
                    TotalDeals: totalDeals
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting tenant stats {TenantId}", tenantId);
                throw;
            }
        }
    }

    // ==================== GET ALL TENANTS STATISTICS (AGGREGATE) ====================
    public class GetAllTenantsStatsHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<GetAllTenantsStatsHandler> _logger;

        public GetAllTenantsStatsHandler(FlowDbContext context, ILogger<GetAllTenantsStatsHandler> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<TenantStatsDto> Handle(CancellationToken cancellationToken = default)
        {
            try
            {
                // FIXED: Count TENANTS, not Users
                var totalTenants = await _context.Tenants.CountAsync(t => !t.IsDeleted, cancellationToken);
                var activeTenants = await _context.Tenants.CountAsync(t => t.IsActive && !t.IsDeleted, cancellationToken);
                var inactiveTenants = totalTenants - activeTenants;

                // Aggregate counts across ALL tenants
                var totalLeads = await _context.Leads.CountAsync(l => !l.IsDeleted, cancellationToken);
                var totalDeals = await _context.Deals.CountAsync(d => !d.IsDeleted, cancellationToken);

                return new TenantStatsDto(
                    TotalUsers: activeTenants,      // Repurposing: Active Tenants
                    ActiveUsers: activeTenants,      // Active Tenants
                    InactiveUsers: inactiveTenants,  // Inactive Tenants
                    TotalLeads: totalLeads,          // Total Leads across all tenants
                    TotalDeals: totalDeals           // Total Deals across all tenants
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting all tenants stats");
                throw;
            }
        }
    }

    // ==================== GET TENANT USERS ====================
    public class GetTenantUsersHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<GetTenantUsersHandler> _logger;

        public GetTenantUsersHandler(FlowDbContext context, ILogger<GetTenantUsersHandler> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<List<UserListItem>> Handle(Guid tenantId, CancellationToken cancellationToken = default)
        {
            try
            {
                // 1) Pull plain values from EF (no DTO construction here)
                var rows = await _context.Users
                    .AsNoTracking()
                    .Where(u => u.TenantId == tenantId && !u.IsDeleted)
                    .OrderBy(u => u.FirstName)
                    .ThenBy(u => u.LastName)
                    .Select(u => new
                    {
                        u.Id,
                        FullName = u.FirstName + " " + u.LastName,
                        Email = u.Email,
                        u.Phone,
                        u.Department,
                        u.JobTitle,
                        u.IsActive,
                        u.IsTenantAdmin,
                        u.LastLoginUtc,
                        u.CreatedAtUtc
                    })
                    .ToListAsync(cancellationToken);

                // 2) Construct your record on the client side (OK to use lists / named args here)
                var users = rows.Select(u => new UserListItem(
                    u.Id,
                    u.FullName,
                    u.Email ?? "",
                    u.Phone,
                    u.Department,
                    u.JobTitle,
                    u.IsActive,
                    u.IsTenantAdmin,
                    u.LastLoginUtc,
                    u.CreatedAtUtc,
                    new List<string>()  // Roles placeholder
                )).ToList();

                return users;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting users for tenant {TenantId}", tenantId);
                throw;
            }
        }

    }

    // ==================== GET TENANTS LOOKUP ====================
    public class GetTenantsLookupHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<GetTenantsLookupHandler> _logger;

        public GetTenantsLookupHandler(FlowDbContext context, ILogger<GetTenantsLookupHandler> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<List<TenantLookupDto>> Handle(CancellationToken cancellationToken = default)
        {
            try
            {
                return await _context.Tenants
                    .AsNoTracking()
                    .Where(t => t.IsActive && !t.IsDeleted)
                    .OrderBy(t => t.Name)
                    .Select(t => new TenantLookupDto(t.Id, t.Name))
                    .ToListAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting tenants lookup");
                throw;
            }
        }
    }
}
