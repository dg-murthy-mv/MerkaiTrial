// =====================================================================
// LEAD QUERIES - Read Operations
// Location: MerkaiTrial.Application/Commands/Leads/LeadQueries.cs
//
// FIXES APPLIED:
//   1. GetLeadsPaginatedHandler  — Currency fallback from ICurrentTenantService
//   2. GetLeadDetailHandler      — Currency fallback + Guid comparison (index-safe)
//   3. GetLeadStatsHandler       — DB-side counts (no memory load)
//   4. GetLeadsPaginatedHandler  — Status accepts a comma-separated key
//                                  list (Leads page "Active" tab)
//   5. RECORD VISIBILITY (014)   — list, detail and stats only return the
//                                  leads the current user's role may see
//                                  (Own / Team / All). Detail of a lead
//                                  outside scope → KeyNotFound → 404.
//                                  Stats.QuotaUsed stays tenant-wide: the
//                                  plan limit counts every lead.
//   6. (079) CUSTOM FIELDS ON LEADS
//        • List: custom search (text values, dropdown choice names),
//          ?cf= filters and the "On list" column values, through the
//          shared CustomFieldListQuery — the same code Contacts, Deals and
//          Companies use. Page size clamped to 1–100 here; a stable order
//          (CreatedAtUtc desc, then Id); a page past the end is pulled back.
//        • Detail: LeadDetailDto.CustomFieldValues, retired fields included.
//   7. (079) ★ /api/leads/stats NO LONGER 500s ON A NEW TENANT.
//        SqlException 8117 "Operand data type NULL is invalid for count".
//        The old query counted with conditions like
//            g.Count(l => openKeys.Contains(l.Status))
//        and when a workspace had NO status in a category the key list was
//        EMPTY. EF folds "contains in an empty list" to constant FALSE,
//        collapses the CASE, and emits COUNT(NULL) — which SQL Server
//        refuses outright. The dashboard swallowed the error and showed
//        zeros. Now: ONE query grouped by Status, then the buckets are
//        added up in memory. No conditional aggregates, nothing to fold,
//        and it cannot break again when a status is renamed or a category
//        is emptied.
// NOTE: Date formatting is NOT done in handlers — UTC always returned.
//       Call _tenantService.FormatDate(utc) in your Razor Page models.
// =====================================================================

using MerkaiTrial.Application.Commands.CustomFields;    // 079
using MerkaiTrial.Application.Configuration;            // 079
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services.Tenants;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Domain.Enums;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MerkaiTrial.Application.Commands.Activities;
using MerkaiTrial.Application.Commands.LeadStatuses;
using MerkaiTrial.Application.Security;

namespace MerkaiTrial.Application.Commands.Leads
{
    // ==================== GET LEADS PAGINATED ====================

    /// <summary>
    /// 079: CustomFilters is optional and TRAILING, so every existing
    /// `new GetLeadsPaginatedQuery(...)` still compiles and means the same.
    /// </summary>
    public record GetLeadsPaginatedQuery(
        Guid TenantId,
        int PageNumber,
        int PageSize,
        string? SearchTerm = null,
        string? Status = null,
        string? AssignedTo = null,
        IReadOnlyList<CustomFieldFilter>? CustomFilters = null
    );

    public class GetLeadsPaginatedHandler : ICommandHandler
    {
        /// <summary>079. The most rows one page can ask for.</summary>
        public const int MaxPageSize = 100;

        private readonly FlowDbContext _context;
        private readonly ICurrentTenantService _tenantService; // FIX 1
        private readonly IRecordScopeService _scope;
        private readonly ILogger<GetLeadsPaginatedHandler> _logger;

        public GetLeadsPaginatedHandler(
            FlowDbContext context,
            ICurrentTenantService tenantService,
            IRecordScopeService scope,
            ILogger<GetLeadsPaginatedHandler> logger)
        {
            _context = context;
            _tenantService = tenantService;
            _scope = scope;
            _logger = logger;
        }

        public async Task<PaginatedResult<LeadListItem>> Handle(
            GetLeadsPaginatedQuery query,
            CancellationToken cancellationToken = default)
        {
            try
            {
                // 079 — clamped HERE, where the data is, not only on the
                // caller: a direct API call asked for as many rows as it
                // liked, and page 0 asked SQL for a negative OFFSET.
                var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);
                var page     = Math.Max(query.PageNumber, 1);
                var tenantId = query.TenantId;

                // FIX 1: Tenant currency resolved once — not hardcoded
                var tenantCurrency = _tenantService.GetCurrencyCode();

                // Record visibility — Own / Team / All for this user.
                var access = await _scope.GetAsync(RecordModules.Leads, cancellationToken);

                // 079 — the ACTIVE Lead custom fields: search, filters, columns.
                var defs = await CustomFieldListQuery.ActiveDefinitionsAsync(
                    _context, tenantId, CustomFieldEntityTypes.Lead, cancellationToken);

                var leadsQuery = _context.Leads
                    .AsNoTracking()
                    .Where(l => l.TenantId == tenantId && !l.IsDeleted)
                    .VisibleTo(access);

                if (!string.IsNullOrWhiteSpace(query.SearchTerm))
                {
                    var s = query.SearchTerm.Trim().ToLower();

                    // 079 — custom text values and dropdown choice names too.
                    // Null = no custom field could match; keep the plain search.
                    var cfMatches = CustomFieldListQuery.SearchMatches(_context, tenantId, defs, s);

                    if (cfMatches is null)
                    {
                        leadsQuery = leadsQuery.Where(l =>
                            l.FullName.ToLower().Contains(s) ||
                            (l.Email != null && l.Email.ToLower().Contains(s)) ||
                            (l.Phone != null && l.Phone.ToLower().Contains(s)) ||
                            (l.CompanyName != null && l.CompanyName.ToLower().Contains(s)));
                    }
                    else
                    {
                        leadsQuery = leadsQuery.Where(l =>
                            l.FullName.ToLower().Contains(s) ||
                            (l.Email != null && l.Email.ToLower().Contains(s)) ||
                            (l.Phone != null && l.Phone.ToLower().Contains(s)) ||
                            (l.CompanyName != null && l.CompanyName.ToLower().Contains(s)) ||
                            cfMatches.Contains(l.Id));
                    }
                }

                // Status: one key ("Working") or several ("New,Working,Qualified").
                // The Leads page's Active tab sends several — every Open and
                // Qualified status for this tenant.
                if (!string.IsNullOrWhiteSpace(query.Status))
                {
                    var keys = query.Status.Split(',',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                    if (keys.Length == 1)
                    {
                        var key = keys[0];
                        leadsQuery = leadsQuery.Where(l => l.Status == key);
                    }
                    else if (keys.Length > 1)
                    {
                        leadsQuery = leadsQuery.Where(l => keys.Contains(l.Status));
                    }
                }

                if (!string.IsNullOrWhiteSpace(query.AssignedTo))
                {
                    var owner = query.AssignedTo.Trim();
                    leadsQuery = leadsQuery.Where(l => l.OwnerUserId == owner);
                }

                // 079 — custom field filters, as id subqueries (shared helper).
                foreach (var clause in CustomFieldListQuery.FilterClauses(_context, tenantId, defs, query.CustomFilters))
                {
                    var ids = clause.EntityIds;   // a local, so EF inlines the subquery
                    leadsQuery = clause.Exclude
                        ? leadsQuery.Where(l => !ids.Contains(l.Id))
                        : leadsQuery.Where(l => ids.Contains(l.Id));
                }

                var totalCount = await leadsQuery.CountAsync(cancellationToken);

                // 079 — a page past the end becomes the last page.
                var totalPages = totalCount == 0 ? 1 : (int)Math.Ceiling(totalCount / (double)pageSize);
                if (page > totalPages) page = totalPages;

                var items = await leadsQuery
                    .OrderByDescending(l => l.CreatedAtUtc)
                    .ThenBy(l => l.Id)              // 079 — stable: paging never repeats or skips a row
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .Select(l => new LeadListItem(
                        l.Id,
                        l.FullName,
                        l.Email ?? "",
                        l.Phone ?? "",
                        l.LeadChannel != null ? l.LeadChannel.Name : l.Channel.ToString(),
                        l.LeadSource != null ? l.LeadSource.Name : l.Source,
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

                // 079 — the "On list" custom values, this page only, one query.
                var listValues = await CustomFieldListQuery.ListValuesAsync(
                    _context, tenantId, defs, items.Select(i => i.Id).ToList(), cancellationToken);

                foreach (var item in items)
                    if (listValues.TryGetValue(item.Id, out var map))
                        item.CustomFieldValues = map;

                return new PaginatedResult<LeadListItem>
                {
                    Items = items,
                    TotalCount = totalCount,
                    Page = page,
                    PageSize = pageSize
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
        private readonly FlowDbContext _context;
        private readonly ICurrentTenantService _tenantService; // FIX 2
        private readonly ILeadStatusResolver _statuses;
        private readonly IRecordScopeService _scope;
        private readonly ILogger<GetLeadDetailHandler> _logger;

        public GetLeadDetailHandler(
            FlowDbContext context,
            ICurrentTenantService tenantService,
            ILeadStatusResolver statuses,
            IRecordScopeService scope,
            ILogger<GetLeadDetailHandler> logger)
        {
            _context = context;
            _tenantService = tenantService;
            _statuses = statuses;
            _scope = scope;
            _logger = logger;
        }

        public async Task<LeadDetailDto> Handle(
            GetLeadDetailQuery query,
            CancellationToken cancellationToken = default)
        {
            try
            {
                // Outside the user's scope reads exactly like "does not exist".
                var access = await _scope.GetAsync(RecordModules.Leads, cancellationToken);

                var row = await _context.Leads
                    .AsNoTracking()
                    .Where(l => l.Id == query.LeadId && l.TenantId == query.TenantId && !l.IsDeleted)
                    .VisibleTo(access)
                    .Select(l => new
                    {
                        Lead = l,
                        ChannelName = l.ChannelId.HasValue
                            ? _context.LeadChannels.Where(c => c.Id == l.ChannelId).Select(c => c.Name).FirstOrDefault()
                            : null,
                        SourceName = l.SourceId.HasValue
                            ? _context.LeadSources.Where(s => s.Id == l.SourceId).Select(s => s.Name).FirstOrDefault()
                            : null,
                        CountryName = l.CountryId.HasValue
                            ? _context.Countries.Where(c => c.Id == l.CountryId).Select(c => c.Name).FirstOrDefault()
                            : null,
                        CountryCurrency = l.CountryId.HasValue
                            ? _context.Countries.Where(c => c.Id == l.CountryId).Select(c => c.CurrencyCode).FirstOrDefault()
                            : null,
                        VerticalName = l.VerticalId.HasValue
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
                string? ownerName = null;
                string? ownerJobTitle = null;
                if (Guid.TryParse(row.Lead.OwnerUserId, out var ownerGuid))
                {
                    var owner = await _context.Users
                        .Where(u => u.Id == ownerGuid)
                        .Select(u => new { u.FullName, u.JobTitle })
                        .FirstOrDefaultAsync(cancellationToken);
                    ownerName = owner?.FullName;
                    ownerJobTitle = owner?.JobTitle;
                }
                var statuses = await _statuses.GetAsync(query.TenantId);

                // 079 — every stored custom value, retired fields included.
                var customFieldValues = await CustomFieldValueWriter.ReadAsync(
                    _context, query.TenantId, CustomFieldEntityTypes.Lead, row.Lead.Id, cancellationToken);

                // NOTE: Dates returned as UTC — format in page model via:
                //   _tenantService.FormatDate(lead.CreatedAtUtc)
                //   _tenantService.FormatDateTime(lead.UpdatedAtUtc)
                return new LeadDetailDto(
                    Id: row.Lead.Id,
                    TenantId: row.Lead.TenantId,
                    ContactId: row.Lead.ContactId ?? Guid.Empty,
                    FullName: row.Lead.FullName,
                    Email: row.Lead.Email ?? "",
                    Phone: row.Lead.Phone ?? "",
                    CompanyName: row.Lead.CompanyName,
                    Address: row.Lead.Address,
                    CountryId: row.Lead.CountryId,
                    CountryName: row.CountryName,
                    Currency: currency,
                    ChannelId: row.Lead.ChannelId,
                    SourceId: row.Lead.SourceId,
                    Channel: row.ChannelName ?? row.Lead.Channel.ToString(),
                    Source: row.SourceName ?? row.Lead.Source,
                    VerticalId: row.Lead.VerticalId,
                    VerticalName: row.VerticalName,
                    Status: row.Lead.Status.ToString(),
                    Score: row.Lead.Score,
                    OwnerUserId: row.Lead.OwnerUserId,
                    OwnerName: ownerName,
                    OwnerJobTitle: ownerJobTitle,
                    CreatedAtUtc: row.Lead.CreatedAtUtc,
                    UpdatedAtUtc: row.Lead.UpdatedAtUtc,
                    HasDeal: row.Lead.DealId.HasValue,
                    IsConverted: statuses.IsConverted(row.Lead.Status),
                    DealId: row.Lead.DealId,
                    DealStage: "",
                    ExpectedValue: row.Lead.EstimatedValue ?? 0m
                )
                {
                    CustomFieldValues = customFieldValues      // 079
                };
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

    /// <summary>
    /// 043. The two date parameters are OPTIONAL and TRAILING, so every
    /// existing `new GetLeadStatsQuery(tenantId)` still compiles and still
    /// means "all time". Nothing that works today changes behaviour.
    ///
    /// Half-open, always: >= FromUtc && < ToExclusiveUtc. The fiscal
    /// service hands out exactly this shape (UtcRange), and it is the only
    /// way to include the last day of a period without also catching the
    /// first instant of the next one.
    ///
    /// These bound CreatedAtUtc — a TIMESTAMP — so the caller must pass the
    /// range from IFiscalYearService.TimestampRangeAsync, which shifts by
    /// the tenant's timezone. DateOnlyRange is for date-only columns like
    /// IssueDateUtc and ExpectedCloseDateUtc, and mixing the two up is the
    /// bug round 034 spent a whole migration repairing.
    /// </summary>
    public record GetLeadStatsQuery(
        Guid TenantId,
        DateTime? FromUtc = null,
        DateTime? ToExclusiveUtc = null);

    public class GetLeadStatsHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ICurrentTenantService _tenant;
        private readonly ILeadStatusResolver _statuses;
        private readonly IRecordScopeService _scope;
        private readonly ILogger<GetLeadStatsHandler> _logger;

        public GetLeadStatsHandler(
            FlowDbContext context,
            ICurrentTenantService tenant,
            ILeadStatusResolver statuses,
            IRecordScopeService scope,
            ILogger<GetLeadStatsHandler> logger)
        {
            _context = context;
            _tenant = tenant;
            _statuses = statuses;
            _scope = scope;
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

                var statuses = await _statuses.GetAsync(query.TenantId, cancellationToken);

                var openKeys = statuses.All.Where(s => s.Category == LeadStatusCategory.Open).Select(s => s.Key).ToList();
                var qualifiedKeys = statuses.All.Where(s => s.Category == LeadStatusCategory.Qualified).Select(s => s.Key).ToList();
                var disqKeys = statuses.All.Where(s => s.Category == LeadStatusCategory.Disqualified).Select(s => s.Key).ToList();
                var defaultKey = statuses.Default?.Key ?? "";

                var access = await _scope.GetAsync(RecordModules.Leads, cancellationToken);

                var tenantLeads = _context.Leads
                    .Where(l => l.TenantId == query.TenantId && !l.IsDeleted);

                // What THIS user may see — every count below uses it.
                var visibleLeads = tenantLeads.VisibleTo(access);

                // The plan limit counts every lead in the workspace, whoever
                // owns it. Kept separate so a rep's quota bar is not "3 of
                // 2000" when the workspace is actually at 1998.
                //
                // 043: and it is NEVER narrowed by the period. The plan limit
                // is about how many leads exist, not how many were added this
                // financial year — a quota bar that empties every April would
                // be worse than no quota bar.
                var quotaUsed = await tenantLeads.CountAsync(cancellationToken);

                // ── 043: the period, if one was asked for ─────────────────
                // Applied to the COUNTS only. CreatedAtUtc is a timestamp, so
                // the caller's range must already be timezone-shifted.
                var periodLeads = visibleLeads;

                if (query.FromUtc.HasValue)
                    periodLeads = periodLeads.Where(l => l.CreatedAtUtc >= query.FromUtc.Value);

                if (query.ToExclusiveUtc.HasValue)
                    periodLeads = periodLeads.Where(l => l.CreatedAtUtc < query.ToExclusiveUtc.Value);

                // 079 — ONE row per status, added up below. See note 7: the
                // old conditional counts became COUNT(NULL) on a tenant with
                // an empty status category, and SQL Server refused them.
                var byStatus = await periodLeads
                    .GroupBy(l => l.Status)
                    .Select(g => new
                    {
                        Status    = g.Key,
                        Count     = g.Count(),
                        Converted = g.Sum(l => l.IsConverted ? 1 : 0)
                    })
                    .ToListAsync(cancellationToken);

                var openSet      = new HashSet<string>(openKeys, StringComparer.Ordinal);
                var qualifiedSet = new HashSet<string>(qualifiedKeys, StringComparer.Ordinal);
                var disqSet      = new HashSet<string>(disqKeys, StringComparer.Ordinal);

                var counts = new
                {
                    TotalLeads       = byStatus.Sum(b => b.Count),
                    // "New" is the starting status, whatever it is called.
                    NewLeads         = byStatus.Where(b => b.Status == defaultKey).Sum(b => b.Count),
                    // Everything else still in play (every Open status but the starting one).
                    WorkingLeads     = byStatus.Where(b => openSet.Contains(b.Status) && b.Status != defaultKey).Sum(b => b.Count),
                    QualifiedLeads   = byStatus.Where(b => qualifiedSet.Contains(b.Status)).Sum(b => b.Count),
                    UnqualifiedLeads = byStatus.Where(b => disqSet.Contains(b.Status)).Sum(b => b.Count),
                    ConvertedLeads   = byStatus.Sum(b => b.Converted)
                };

                // Tasks and activities count only on leads the user can see.
                //
                // 043: deliberately visibleLeads, not periodLeads. "Overdue"
                // and "today" are facts about RIGHT NOW — an overdue task on
                // a lead created last year is still overdue today, and hiding
                // it because the dashboard is showing FY25 would quietly drop
                // the one number on this page that asks someone to act.
                var visibleLeadIds = visibleLeads.Select(l => l.Id);

                // Open lead tasks past their due date (reminders are tasks now).
                var overdueTasks = await _context.Activities
                    .CountAsync(a =>
                        a.TenantId == query.TenantId &&
                        a.EntityType == ActivityEntityType.Lead &&
                        visibleLeadIds.Contains(a.EntityId) &&
                        a.IsTask && !a.IsCompleted && !a.IsDeleted &&
                        a.DueDate < nowUtc, cancellationToken);

                // Lead activity that happened today: logs + tasks completed today.
                var todayActivities = await _context.Activities
                    .CountAsync(a =>
                        a.TenantId == query.TenantId &&
                        a.EntityType == ActivityEntityType.Lead &&
                        visibleLeadIds.Contains(a.EntityId) &&
                        !a.IsDeleted &&
                        (!a.IsTask || a.IsCompleted) &&
                        a.ActivityDate >= todayStartUtc &&
                        a.ActivityDate < todayEndUtc, cancellationToken);

                return new LeadStatsDto(
                    TotalLeads: counts.TotalLeads,
                    NewLeads: counts.NewLeads,
                    WorkingLeads: counts.WorkingLeads,
                    QualifiedLeads: counts.QualifiedLeads,
                    UnqualifiedLeads: counts.UnqualifiedLeads,
                    ConvertedLeads: counts.ConvertedLeads,
                    OverdueReminders: overdueTasks,
                    TodayActivities: todayActivities,
                    QuotaUsed: quotaUsed
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
            _logger = logger;
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
            _logger = logger;
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
            _logger = logger;
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
                        (c.Code ?? "").Trim().ToUpper(),
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
            _logger = logger;
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
