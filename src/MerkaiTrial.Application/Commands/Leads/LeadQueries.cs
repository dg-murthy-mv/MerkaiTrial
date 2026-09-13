// =====================================================================
// LEAD QUERIES - Read Operations
// Location: MerkaiTrial.Application/Commands/Leads/LeadQueries.cs
//
// FIXES APPLIED:
//   1. GetLeadsPaginatedHandler  — Currency fallback from ICurrentTenantService
//   2. GetLeadDetailHandler      — Currency fallback + Guid comparison (index-safe)
//   3. GetLeadStatsHandler       — DB-side counts (no memory load)
// NOTE: Date formatting is NOT done in handlers — UTC always returned.
//       Call _tenantService.FormatDate(utc) in your Razor Page models.
// =====================================================================

using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services.Tenants;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Domain.Enums;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MerkaiTrial.Application.Commands.Activities;

namespace MerkaiTrial.Application.Commands.Leads
{
    // ==================== GET LEADS PAGINATED ====================
    public record GetLeadsPaginatedQuery(
        Guid    TenantId,
        int     PageNumber,
        int     PageSize,
        string? SearchTerm  = null,
        string? Status      = null,
        string? AssignedTo  = null
    );

    public class GetLeadsPaginatedHandler : ICommandHandler
    {
        private readonly FlowDbContext         _context;
        private readonly ICurrentTenantService _tenantService; // FIX 1
        private readonly ILogger<GetLeadsPaginatedHandler> _logger;

        public GetLeadsPaginatedHandler(
            FlowDbContext         context,
            ICurrentTenantService tenantService,
            ILogger<GetLeadsPaginatedHandler> logger)
        {
            _context       = context;
            _tenantService = tenantService;
            _logger        = logger;
        }

        public async Task<PaginatedResult<LeadListItem>> Handle(
            GetLeadsPaginatedQuery query,
            CancellationToken cancellationToken = default)
        {
            try
            {
                // FIX 1: Tenant currency resolved once — not hardcoded
                var tenantCurrency = _tenantService.GetCurrencyCode();

                var leadsQuery = _context.Leads
                    .AsNoTracking()
                    .Where(l => l.TenantId == query.TenantId && !l.IsDeleted);

                if (!string.IsNullOrWhiteSpace(query.SearchTerm))
                {
                    var s = query.SearchTerm.ToLower();
                    leadsQuery = leadsQuery.Where(l =>
                        l.FullName.ToLower().Contains(s) ||
                        (l.Email       != null && l.Email.ToLower().Contains(s))       ||
                        (l.Phone       != null && l.Phone.ToLower().Contains(s))       ||
                        (l.CompanyName != null && l.CompanyName.ToLower().Contains(s)));
                }

                if (!string.IsNullOrWhiteSpace(query.Status) &&
                    Enum.TryParse<LeadStatus>(query.Status, out var statusEnum))
                    leadsQuery = leadsQuery.Where(l => l.Status == statusEnum);

                if (!string.IsNullOrWhiteSpace(query.AssignedTo))
                    leadsQuery = leadsQuery.Where(l => l.OwnerUserId == query.AssignedTo);

                var totalCount = await leadsQuery.CountAsync(cancellationToken);

                var items = await leadsQuery
                    .OrderByDescending(l => l.CreatedAtUtc)
                    .Skip((query.PageNumber - 1) * query.PageSize)
                    .Take(query.PageSize)
                    .Select(l => new LeadListItem(
                        l.Id,
                        l.FullName,
                        l.Email ?? "",
                        l.Phone ?? "",
                        l.LeadChannel != null ? l.LeadChannel.Name : l.Channel.ToString(),
                        l.LeadSource  != null ? l.LeadSource.Name  : l.Source,
                        l.Status.ToString(),
                        l.Score,
                        l.CreatedAtUtc,
                        l.DealId.HasValue,
                        l.DealId,
                        "",
                        l.EstimatedValue ?? 0m,
                        l.Currency ?? tenantCurrency, // FIX 1: tenant fallback, not "INR"
                        l.OwnerUserId,
                        null
                    ))
                    .ToListAsync(cancellationToken);

                return new PaginatedResult<LeadListItem>
                {
                    Items      = items,
                    TotalCount = totalCount,
                    Page       = query.PageNumber,
                    PageSize   = query.PageSize
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting paginated leads for tenant {TenantId}", query.TenantId);
                throw;
            }
        }
    }

    // ==================== GET LEAD DETAIL ====================
    public record GetLeadDetailQuery(Guid TenantId, Guid LeadId);

    public class GetLeadDetailHandler : ICommandHandler
    {
        private readonly FlowDbContext         _context;
        private readonly ICurrentTenantService _tenantService; // FIX 2
        private readonly ILogger<GetLeadDetailHandler> _logger;

        public GetLeadDetailHandler(
            FlowDbContext         context,
            ICurrentTenantService tenantService,
            ILogger<GetLeadDetailHandler> logger)
        {
            _context       = context;
            _tenantService = tenantService;
            _logger        = logger;
        }

        public async Task<LeadDetailDto> Handle(
            GetLeadDetailQuery query,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var row = await _context.Leads
                    .AsNoTracking()
                    .Where(l => l.Id == query.LeadId && l.TenantId == query.TenantId && !l.IsDeleted)
                    .Select(l => new
                    {
                        Lead            = l,
                        ChannelName     = l.ChannelId.HasValue
                            ? _context.LeadChannels.Where(c => c.Id == l.ChannelId).Select(c => c.Name).FirstOrDefault()
                            : null,
                        SourceName      = l.SourceId.HasValue
                            ? _context.LeadSources.Where(s => s.Id == l.SourceId).Select(s => s.Name).FirstOrDefault()
                            : null,
                        CountryName     = l.CountryId.HasValue
                            ? _context.Countries.Where(c => c.Id == l.CountryId).Select(c => c.Name).FirstOrDefault()
                            : null,
                        CountryCurrency = l.CountryId.HasValue
                            ? _context.Countries.Where(c => c.Id == l.CountryId).Select(c => c.CurrencyCode).FirstOrDefault()
                            : null,
                        VerticalName    = l.VerticalId.HasValue
                            ? _context.CompanyVerticals
                                .Where(v => v.Id == l.VerticalId.Value)
                                .Select(v => v.Name)
                                .FirstOrDefault()
                            : null
                    })
                    .FirstOrDefaultAsync(cancellationToken);

                if (row == null)
                    throw new KeyNotFoundException($"Lead {query.LeadId} not found");

                // FIX 2: Currency — lead → country → tenant (never hardcoded)
                var currency = !string.IsNullOrWhiteSpace(row.Lead.Currency)
                    ? row.Lead.Currency!.Trim().ToUpperInvariant()
                    : !string.IsNullOrWhiteSpace(row.CountryCurrency)
                        ? row.CountryCurrency!.Trim().ToUpperInvariant()
                        : _tenantService.GetCurrencyCode();

                // FIX 2: Guid comparison — not string cast (index-safe)
                string? ownerName     = null;
                string? ownerJobTitle = null;
                if (Guid.TryParse(row.Lead.OwnerUserId, out var ownerGuid))
                {
                    var owner = await _context.Users
                        .Where(u => u.Id == ownerGuid)
                        .Select(u => new { u.FullName, u.JobTitle })
                        .FirstOrDefaultAsync(cancellationToken);
                    ownerName     = owner?.FullName;
                    ownerJobTitle = owner?.JobTitle;
                }
                

                // NOTE: Dates returned as UTC — format in page model via:
                //   _tenantService.FormatDate(lead.CreatedAtUtc)
                //   _tenantService.FormatDateTime(lead.UpdatedAtUtc)
                return new LeadDetailDto(
                    Id:            row.Lead.Id,
                    TenantId:      row.Lead.TenantId,
                    ContactId:     row.Lead.ContactId ?? Guid.Empty,
                    FullName:      row.Lead.FullName,
                    Email:         row.Lead.Email ?? "",
                    Phone:         row.Lead.Phone ?? "",
                    CompanyName:   row.Lead.CompanyName,
                    Address:       row.Lead.Address,
                    CountryId:     row.Lead.CountryId,
                    CountryName:   row.CountryName,
                    Currency:      currency,
                    ChannelId:     row.Lead.ChannelId,
                    SourceId:      row.Lead.SourceId,
                    Channel:       row.ChannelName ?? row.Lead.Channel.ToString(),
                    Source:        row.SourceName  ?? row.Lead.Source,
                    VerticalId:    row.Lead.VerticalId,
                    VerticalName:  row.VerticalName ,
                    Status:        row.Lead.Status.ToString(),
                    Score:         row.Lead.Score,
                    OwnerUserId:   row.Lead.OwnerUserId,
                    OwnerName:     ownerName,
                    OwnerJobTitle: ownerJobTitle,
                    CreatedAtUtc:  row.Lead.CreatedAtUtc,
                    UpdatedAtUtc:  row.Lead.UpdatedAtUtc,
                    HasDeal:       row.Lead.DealId.HasValue,
                    IsConverted: row.Lead.Status == LeadStatus.Converted,
                    DealId:        row.Lead.DealId,
                    DealStage:     "",
                    ExpectedValue: row.Lead.EstimatedValue ?? 0m
                );
            }
            catch (KeyNotFoundException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting lead detail {LeadId}", query.LeadId);
                throw;
            }
        }
    }

    // ==================== GET LEAD STATS ====================
    public record GetLeadStatsQuery(Guid TenantId);

    public class GetLeadStatsHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ICurrentTenantService _tenant;
        private readonly ILogger<GetLeadStatsHandler> _logger;

        public GetLeadStatsHandler(
            FlowDbContext context,
            ICurrentTenantService tenant,
            ILogger<GetLeadStatsHandler> logger)
        {
            _context = context;
            _tenant = tenant;
            _logger = logger;
        }

        public async Task<LeadStatsDto> Handle(
            GetLeadStatsQuery query,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var nowUtc = DateTime.UtcNow;

                // "Today" is the TENANT's calendar day, expressed as a UTC range.
                var localToday = _tenant.UtcToLocal(nowUtc).Date;
                var todayStartUtc = _tenant.LocalToUtc(localToday);
                var todayEndUtc = todayStartUtc.AddDays(1);

                // One round-trip for all status counts (unchanged).
                var counts = await _context.Leads
                    .Where(l => l.TenantId == query.TenantId && !l.IsDeleted)
                    .GroupBy(l => 1)
                    .Select(g => new
                    {
                        TotalLeads = g.Count(),
                        NewLeads = g.Count(l => l.Status == LeadStatus.New),
                        WorkingLeads = g.Count(l => l.Status == LeadStatus.Working),
                        QualifiedLeads = g.Count(l => l.Status == LeadStatus.Qualified),
                        UnqualifiedLeads = g.Count(l => l.Status == LeadStatus.Unqualified),
                        ConvertedLeads = g.Count(l => l.IsConverted)
                    })
                    .FirstOrDefaultAsync(cancellationToken);

                // Open lead tasks past their due date (reminders are tasks now).
                var overdueTasks = await _context.Activities
                    .CountAsync(a =>
                        a.TenantId == query.TenantId &&
                        a.EntityType == ActivityEntityType.Lead &&
                        a.IsTask && !a.IsCompleted && !a.IsDeleted &&
                        a.DueDate < nowUtc, cancellationToken);

                // Lead activity that happened today: logs + tasks completed today.
                var todayActivities = await _context.Activities
                    .CountAsync(a =>
                        a.TenantId == query.TenantId &&
                        a.EntityType == ActivityEntityType.Lead &&
                        !a.IsDeleted &&
                        (!a.IsTask || a.IsCompleted) &&
                        a.ActivityDate >= todayStartUtc &&
                        a.ActivityDate < todayEndUtc, cancellationToken);

                return new LeadStatsDto(
                    TotalLeads: counts?.TotalLeads ?? 0,
                    NewLeads: counts?.NewLeads ?? 0,
                    WorkingLeads: counts?.WorkingLeads ?? 0,
                    QualifiedLeads: counts?.QualifiedLeads ?? 0,
                    UnqualifiedLeads: counts?.UnqualifiedLeads ?? 0,
                    ConvertedLeads: counts?.ConvertedLeads ?? 0,
                    OverdueReminders: overdueTasks,
                    TodayActivities: todayActivities
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting lead stats for tenant {TenantId}", query.TenantId);
                throw;
            }
        }
    }


    // ==================== DROPDOWN QUERIES ====================
    // (No changes needed — these are already correct)

    public record GetLeadChannelsQuery(Guid TenantId);

    public class GetLeadChannelsHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<GetLeadChannelsHandler> _logger;

        public GetLeadChannelsHandler(FlowDbContext context, ILogger<GetLeadChannelsHandler> logger)
        {
            _context = context;
            _logger  = logger;
        }

        public async Task<List<LeadChannelDto>> Handle(
            GetLeadChannelsQuery query,
            CancellationToken cancellationToken = default)
        {
            try
            {
                return await _context.Set<LeadChannel>()
                    .AsNoTracking()
                    .Where(c => c.TenantId == query.TenantId && c.IsActive && !c.IsDeleted)
                    .OrderBy(c => c.Name)
                    .Select(c => new LeadChannelDto(c.Id, c.Name))
                    .ToListAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting lead channels for tenant {TenantId}", query.TenantId);
                throw;
            }
        }
    }

    public record GetLeadSourcesQuery(Guid TenantId);

    public class GetLeadSourcesHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<GetLeadSourcesHandler> _logger;

        public GetLeadSourcesHandler(FlowDbContext context, ILogger<GetLeadSourcesHandler> logger)
        {
            _context = context;
            _logger  = logger;
        }

        public async Task<List<LeadSourceDto>> Handle(
            GetLeadSourcesQuery query,
            CancellationToken cancellationToken = default)
        {
            try
            {
                return await _context.Set<LeadSource>()
                    .AsNoTracking()
                    .Where(s => s.TenantId == query.TenantId && s.IsActive && !s.IsDeleted)
                    .OrderBy(s => s.Name)
                    .Select(s => new LeadSourceDto(s.Id, s.Name))
                    .ToListAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting lead sources for tenant {TenantId}", query.TenantId);
                throw;
            }
        }
    }

    public record GetCountriesForDropdownQuery();

    public class GetCountriesForDropdownHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<GetCountriesForDropdownHandler> _logger;

        public GetCountriesForDropdownHandler(FlowDbContext context, ILogger<GetCountriesForDropdownHandler> logger)
        {
            _context = context;
            _logger  = logger;
        }

        public async Task<List<CountryDropdownDto>> Handle(
            GetCountriesForDropdownQuery query,
            CancellationToken cancellationToken = default)
        {
            try
            {
                return await _context.Countries
                    .AsNoTracking()
                    .Where(c => c.IsActive)
                    .OrderBy(c => c.Name)
                    .Select(c => new CountryDropdownDto(
                        c.Id,
                        c.Name,
                        (c.Code         ?? "").Trim().ToUpper(),
                        (c.CurrencyCode ?? "").Trim().ToUpper()
                    ))
                    .ToListAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting countries for dropdown");
                throw;
            }
        }
    }

    public record GetCurrenciesForDropdownQuery();

    public class GetCurrenciesForDropdownHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<GetCurrenciesForDropdownHandler> _logger;

        public GetCurrenciesForDropdownHandler(FlowDbContext context, ILogger<GetCurrenciesForDropdownHandler> logger)
        {
            _context = context;
            _logger  = logger;
        }

        public async Task<List<CurrencyDropdownDto>> Handle(
            GetCurrenciesForDropdownQuery query,
            CancellationToken cancellationToken = default)
        {
            try
            {
                return await _context.Countries
                    .AsNoTracking()
                    .Select(c => (c.CurrencyCode ?? "").Trim().ToUpper())
                    .Where(code => code != "")
                    .Distinct()
                    .OrderBy(code => code)
                    .Select(code => new CurrencyDropdownDto(code))
                    .ToListAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting currencies for dropdown");
                throw;
            }
        }
    }
}
