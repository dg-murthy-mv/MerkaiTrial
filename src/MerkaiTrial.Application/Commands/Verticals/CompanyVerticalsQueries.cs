using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Application.Commands.Verticals
{
    public class GetCompanyVerticalsQuery
    {
        public Guid? TenantId { get; set; }
        public bool IncludeSystem { get; set; } = true;
        public int PageNumber { get; set; } = 1;
        public int PageSize { get; set; } = 10;
        public string? SearchTerm { get; set; }
        public bool ShowSystemOnly { get; set; }
        public bool ShowCustomOnly { get; set; }  // ✅ ADDED
        public bool ShowAllVerticals { get; set; }  // ✅ ADDED
    }

    public class GetCompanyVerticalsHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;

        public GetCompanyVerticalsHandler(FlowDbContext context)
        {
            _context = context;
        }

        public async Task<PaginatedResult<CompanyVerticalListItem>> Handle(
            GetCompanyVerticalsQuery query,
            CancellationToken cancellationToken = default)
        {
            var queryable = _context.CompanyVerticals
                .Where(v => !v.IsDeleted && v.IsActive);

            // ✅ FIXED: Tenant filtering logic
            if (query.ShowSystemOnly)
            {
                // Show ONLY system verticals
                queryable = queryable.Where(v => v.TenantId == null);
            }
            else if (query.ShowCustomOnly)
            {
                // Show ONLY custom verticals
                if (query.TenantId.HasValue && query.TenantId.Value != Guid.Empty)
                {
                    // Specific tenant's custom verticals
                    queryable = queryable.Where(v => v.TenantId == query.TenantId.Value);
                }
                else
                {
                    // All custom verticals (from all tenants)
                    queryable = queryable.Where(v => v.TenantId != null);
                }
            }
            else if (query.ShowAllVerticals)
            {
                // Show ALL verticals (system + all custom from all tenants)
                // No additional filter needed - already have IsDeleted and IsActive
            }
            else if (query.TenantId.HasValue && query.TenantId.Value != Guid.Empty)
            {
                // Specific tenant: show system + that tenant's custom
                if (query.IncludeSystem)
                {
                    queryable = queryable.Where(v => v.TenantId == null || v.TenantId == query.TenantId.Value);
                }
                else
                {
                    queryable = queryable.Where(v => v.TenantId == query.TenantId.Value);
                }
            }
            else
            {
                // Default: Show ALL verticals (system + all custom)
                // No tenant filter - show everything
            }

            // Search filter
            if (!string.IsNullOrWhiteSpace(query.SearchTerm))
            {
                var search = query.SearchTerm.ToLower();
                queryable = queryable.Where(v =>
                    v.Name.ToLower().Contains(search) ||
                    (v.Description != null && v.Description.ToLower().Contains(search))
                );
            }

            // Get total count before pagination
            var totalCount = await queryable.CountAsync(cancellationToken);

            // Apply sorting and pagination with tenant join
            var verticals = await queryable
                .OrderBy(v => v.DisplayOrder)
                .ThenBy(v => v.Name)
                .Skip((query.PageNumber - 1) * query.PageSize)
                .Take(query.PageSize)
                .GroupJoin(
                    _context.Tenants,
                    v => v.TenantId,
                    t => t.Id,
                    (v, tenants) => new { Vertical = v, Tenant = tenants.FirstOrDefault() }
                )
                .Select(x => new CompanyVerticalListItem
                {
                    Id = x.Vertical.Id,
                    TenantId = x.Vertical.TenantId,
                    Name = x.Vertical.Name,
                    TenantName = x.Tenant != null ? x.Tenant.Name : null,
                    Icon = x.Vertical.Icon,
                    Color = x.Vertical.Color,
                    IsSystem = x.Vertical.IsSystem
                })
                .ToListAsync(cancellationToken);

            return new PaginatedResult<CompanyVerticalListItem>
            {
                Items = verticals,
                Page = query.PageNumber,
                PageSize = query.PageSize,
                TotalCount = totalCount
            };
        }
    }

    public class GetVerticalStatsHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;

        public GetVerticalStatsHandler(FlowDbContext context)
        {
            _context = context;
        }

        public async Task<VerticalStatsDto> Handle(CancellationToken cancellationToken = default)
        {
            var totalVerticals = await _context.CompanyVerticals
                .CountAsync(v => !v.IsDeleted && v.IsActive, cancellationToken);

            var systemVerticals = await _context.CompanyVerticals
                .CountAsync(v => !v.IsDeleted && v.IsActive && v.IsSystem, cancellationToken);

            var customVerticals = await _context.CompanyVerticals
                .CountAsync(v => !v.IsDeleted && v.IsActive && !v.IsSystem, cancellationToken);

            // Count unique tenants with custom verticals
            var tenantsWithVerticals = await _context.CompanyVerticals
                .Where(v => !v.IsDeleted && v.IsActive && !v.IsSystem && v.TenantId != null)
                .Select(v => v.TenantId)
                .Distinct()
                .CountAsync(cancellationToken);

            return new VerticalStatsDto(
                TotalVerticals: totalVerticals,
                SystemVerticals: systemVerticals,
                CustomVerticals: customVerticals,
                ActiveTenants: tenantsWithVerticals
            );
        }
    }
    // =====================================================================
    // GET COMPANY VERTICAL BY ID QUERY
    // =====================================================================
    public class GetCompanyVerticalByIdQuery
    {
        public Guid Id { get; set; }
    }

    public class GetCompanyVerticalByIdHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;

        public GetCompanyVerticalByIdHandler(FlowDbContext context)
        {
            _context = context;
        }

        public async Task<CompanyVerticalDto> Handle(GetCompanyVerticalByIdQuery query, CancellationToken cancellationToken = default)
        {
            var vertical = await _context.CompanyVerticals
                .Where(v => v.Id == query.Id && !v.IsDeleted)
                .Select(v => new CompanyVerticalDto
                {
                    Id = v.Id,
                    TenantId = v.TenantId,
                    Name = v.Name,
                    Description = v.Description,
                    Icon = v.Icon,
                    Color = v.Color,
                    IsActive = v.IsActive,
                    IsSystem = v.IsSystem,
                    DisplayOrder = v.DisplayOrder
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (vertical == null)
                throw new KeyNotFoundException($"Vertical with ID '{query.Id}' not found");

            return vertical;
        }
    }
}