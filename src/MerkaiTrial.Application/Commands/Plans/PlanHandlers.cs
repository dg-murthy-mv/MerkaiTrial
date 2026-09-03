// =====================================================================
// PlanHandlers.cs — Queries + Commands
// Location: MerkaiTrial.Application/Commands/Plans/PlanHandlers.cs
//
// Replaces the hardcoded PlanLimits static class entirely.
// All plan limit lookups go through GetPlanByNameHandler at runtime.
// =====================================================================

using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Text.Json;

namespace MerkaiTrial.Application.Commands.Plans;

// ── Helpers ──────────────────────────────────────────────────────────
file static class PlanHelper
{
    public static string FormatStorage(long bytes)
    {
        if (bytes >= 1_099_511_627_776) return $"{bytes / 1_099_511_627_776.0:F0} TB";
        if (bytes >= 1_073_741_824)     return $"{bytes / 1_073_741_824.0:F0} GB";
        if (bytes >= 1_048_576)         return $"{bytes / 1_048_576.0:F0} MB";
        return $"{bytes / 1024.0:F0} KB";
    }

    public static List<string> ParseFeatures(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>(); }
        catch { return new List<string>(); }
    }

    public static long GbToBytes(int gb) => (long)gb * 1_073_741_824;
    public static int BytesToGb(long bytes) => (int)(bytes / 1_073_741_824);

    public static List<PlanPricingDto> ToPricingDtos(Plan p) =>
        (p.PlanPricings ?? new List<PlanPricing>())
            .OrderBy(pp => pp.SortOrder)
            .ThenBy(pp => pp.CurrencyCode)
            .Select(pp => new PlanPricingDto(
                pp.Id,
                pp.PlanId,
                pp.CurrencyCode,
                pp.CountryCode,
                pp.CountryName,
                pp.CurrencySymbol,
                pp.MonthlyPrice,
                pp.AnnualPrice,
                pp.IsActive,
                pp.SortOrder))
            .ToList();

    /// <summary>
    /// Builds/updates a PlanPricing row from a submitted PlanPricingInput,
    /// resolving CountryCode/CountryName/CurrencySymbol from the known
    /// currency list (the admin form only submits currency + prices).
    /// </summary>
    public static void ApplyInput(PlanPricing row, PlanPricingInput input, int sortOrder)
    {
        var info = PlanCurrencies.Find(input.CurrencyCode);

        row.CurrencyCode   = input.CurrencyCode;
        row.CountryCode    = info?.CountryCode ?? "*";
        row.CountryName    = info?.CountryName ?? input.CurrencyCode;
        row.CurrencySymbol = info?.Symbol ?? "";
        row.MonthlyPrice   = input.MonthlyPrice;
        row.AnnualPrice    = input.AnnualPrice;
        row.SortOrder      = sortOrder;
    }
}

// ======================================================================
// QUERIES
// ======================================================================

// ── GET ALL PLANS (Super Admin list) ──────────────────────────────────
public class GetPlansHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly ILogger<GetPlansHandler> _logger;

    public GetPlansHandler(FlowDbContext db, ILogger<GetPlansHandler> logger)
    {
        _db     = db;
        _logger = logger;
    }

    public async Task<List<PlanListItem>> Handle(
        bool includeInactive = false,
        CancellationToken ct = default)
    {
        try
        {
            IQueryable<Plan> query = _db.Plans
                .AsNoTracking()
                .Include(p => p.PlanPricings);

            if (!includeInactive)
                query = query.Where(p => p.IsActive);

            // Tenant count per plan — subquery
            var plans = await query
                .OrderBy(p => p.SortOrder)
                .ThenBy(p => p.Name)
                .ToListAsync(ct);

            // Get tenant counts in one query
            var planNames   = plans.Select(p => p.Name).ToList();
            var tenantCounts = await _db.Tenants
                .Where(t => !t.IsDeleted && planNames.Contains(t.Plan))
                .GroupBy(t => t.Plan)
                .Select(g => new { Plan = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Plan, x => x.Count, ct);

            return plans.Select(p => new PlanListItem(
                Id:                   p.Id,
                Name:                 p.Name,
                DisplayName:          p.DisplayName,
                MonthlyPrice:         p.MonthlyPrice,
                AnnualPrice:          p.AnnualPrice,
                MaxUsers:             p.MaxUsers,
                MaxLeads:             p.MaxLeads,
                MaxDeals:             p.MaxDeals,
                StorageLimitBytes:    p.StorageLimitBytes,
                StorageLimitDisplay:  PlanHelper.FormatStorage(p.StorageLimitBytes),
                TenantCount:          tenantCounts.GetValueOrDefault(p.Name, 0),
                IsActive:             p.IsActive,
                IsPublic:             p.IsPublic,
                IsHighlighted:        p.IsHighlighted,
                SortOrder:            p.SortOrder,
                IsTrial:              p.IsTrial,
                TrialDurationDays:    p.TrialDurationDays,
                PlanPricings:         PlanHelper.ToPricingDtos(p)
            )).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting plans list");
            throw;
        }
    }
}

// ── GET PLAN DETAIL ───────────────────────────────────────────────────
public class GetPlanDetailHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly ILogger<GetPlanDetailHandler> _logger;

    public GetPlanDetailHandler(FlowDbContext db, ILogger<GetPlanDetailHandler> logger)
    {
        _db     = db;
        _logger = logger;
    }

    public async Task<PlanDto> Handle(Guid planId, CancellationToken ct = default)
    {
        try
        {
            var p = await _db.Plans
                .AsNoTracking()
                .Include(x => x.PlanPricings)
                .FirstOrDefaultAsync(x => x.Id == planId, ct)
                ?? throw new KeyNotFoundException($"Plan {planId} not found");

            var tenantCount = await _db.Tenants
                .CountAsync(t => t.Plan == p.Name && !t.IsDeleted, ct);

            return ToDto(p, tenantCount);
        }
        catch (KeyNotFoundException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting plan detail {PlanId}", planId);
            throw;
        }
    }

    private static PlanDto ToDto(Plan p, int tenantCount) => new(
        Id:                  p.Id,
        Name:                p.Name,
        DisplayName:         p.DisplayName,
        Description:         p.Description,
        MaxUsers:            p.MaxUsers,
        MaxLeads:            p.MaxLeads,
        MaxDeals:            p.MaxDeals,
        MaxContacts:         p.MaxContacts,
        MaxCompanies:        p.MaxCompanies,
        StorageLimitBytes:   p.StorageLimitBytes,
        StorageLimitDisplay: PlanHelper.FormatStorage(p.StorageLimitBytes),
        MonthlyPrice:        p.MonthlyPrice,
        AnnualPrice:         p.AnnualPrice,
        Features:            p.Features,
        FeatureList:         PlanHelper.ParseFeatures(p.Features),
        SortOrder:           p.SortOrder,
        IsHighlighted:       p.IsHighlighted,
        BadgeText:           p.BadgeText,
        IsActive:            p.IsActive,
        IsPublic:            p.IsPublic,
        TenantCount:         tenantCount,
        IsTrial:             p.IsTrial,
        TrialDurationDays:   p.TrialDurationDays,
        PlanPricings:        PlanHelper.ToPricingDtos(p),
        CreatedAtUtc:        p.CreatedAtUtc,
        UpdatedAtUtc:        p.UpdatedAtUtc
    );
}

// ── GET PLAN BY NAME (used internally by CreateTenant, ChangePlan) ────
public class GetPlanByNameHandler : ICommandHandler
{
    private readonly FlowDbContext _db;

    public GetPlanByNameHandler(FlowDbContext db) => _db = db;

    /// <summary>
    /// Returns Plan entity. Throws if not found or inactive.
    /// Used everywhere that old PlanLimits.GetLimits(planName) was called.
    /// </summary>
    public async Task<Plan> Handle(string planName, CancellationToken ct = default)
    {
        var plan = await _db.Plans
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Name == planName.ToLower() && p.IsActive, ct);

        if (plan == null)
        {
            // Fallback: try to load inactive plan (grandfathered tenant)
            plan = await _db.Plans
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Name == planName.ToLower(), ct);
        }

        return plan ?? throw new KeyNotFoundException(
            $"Plan '{planName}' not found. Valid plans must exist in the Plans table.");
    }
}
public class GetPlanTenantsHandler : ICommandHandler
{
    private readonly FlowDbContext _db;

    public GetPlanTenantsHandler(FlowDbContext db) => _db = db;

    public async Task<List<PlanTenantItem>> Handle(Guid planId, CancellationToken ct = default)
    {
        // Get plan name first — Tenants.Plan stores the name string, not the GUID
        var planName = await _db.Plans
            .AsNoTracking()
            .Where(p => p.Id == planId)
            .Select(p => p.Name)
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException($"Plan {planId} not found");

        return await _db.Tenants
            .AsNoTracking()
            .Where(t => t.Plan == planName && !t.IsDeleted)
            .OrderBy(t => t.Name)
            .Select(t => new PlanTenantItem(
                t.Id,
                t.TenantKey,
                t.Name,
                t.DefaultCurrency,
                t.IsActive,
                t.CreatedAtUtc ?? DateTime.MinValue, // Fix: provide default value if null
                t.Country != null ? t.Country.Name : null
            ))
            .ToListAsync(ct);
    }
}

// ── GET PUBLIC PLAN CARDS (pricing page) ─────────────────────────────
public class GetPublicPlansHandler : ICommandHandler
{
    private readonly FlowDbContext _db;

    public GetPublicPlansHandler(FlowDbContext db) => _db = db;

    public async Task<List<PlanCardDto>> Handle(CancellationToken ct = default)
    {
        var plans = await _db.Plans
            .AsNoTracking()
            .Include(p => p.PlanPricings)
            .Where(p => p.IsActive && p.IsPublic)
            .OrderBy(p => p.SortOrder)
            .ToListAsync(ct);

        return plans.Select(p => new PlanCardDto(
            Id:                  p.Id,
            Name:                p.Name,
            DisplayName:         p.DisplayName,
            Description:         p.Description,
            MonthlyPrice:        p.MonthlyPrice,
            AnnualPrice:         p.AnnualPrice,
            FeatureList:         PlanHelper.ParseFeatures(p.Features),
            MaxUsers:            p.MaxUsers,
            MaxLeads:            p.MaxLeads,
            MaxDeals:            p.MaxDeals,
            StorageLimitDisplay: PlanHelper.FormatStorage(p.StorageLimitBytes),
            IsHighlighted:       p.IsHighlighted,
            BadgeText:           p.BadgeText,
            SortOrder:           p.SortOrder,
            IsTrial:             p.IsTrial,
            TrialDurationDays:   p.TrialDurationDays,
            PlanPricings:        PlanHelper.ToPricingDtos(p)
        )).ToList();
    }
}

// ── GET PLAN SUMMARY (dashboard widget) ──────────────────────────────
public class GetPlanSummaryHandler : ICommandHandler
{
    private readonly FlowDbContext _db;

    public GetPlanSummaryHandler(FlowDbContext db) => _db = db;

    public async Task<List<PlanSummaryDto>> Handle(CancellationToken ct = default)
    {
        var plans = await _db.Plans
            .AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.SortOrder)
            .ToListAsync(ct);

        var tenantCounts = await _db.Tenants
            .Where(t => !t.IsDeleted)
            .GroupBy(t => t.Plan)
            .Select(g => new { Plan = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Plan, x => x.Count, ct);

        return plans.Select(p => new PlanSummaryDto(
            Name:           p.Name,
            DisplayName:    p.DisplayName,
            TenantCount:    tenantCounts.GetValueOrDefault(p.Name, 0),
            MonthlyRevenue: tenantCounts.GetValueOrDefault(p.Name, 0) * p.MonthlyPrice
        )).ToList();
    }
}

// ======================================================================
// COMMANDS
// ======================================================================

// ── CREATE PLAN ───────────────────────────────────────────────────────
public class CreatePlanHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly ILogger<CreatePlanHandler> _logger;

    public CreatePlanHandler(FlowDbContext db, ILogger<CreatePlanHandler> logger)
    {
        _db     = db;
        _logger = logger;
    }

    public async Task<PlanDto> Handle(CreatePlanCommand cmd, CancellationToken ct = default)
    {
        try
        {
            var nameLower = cmd.Name.ToLower();

            // Uniqueness check
            if (await _db.Plans.AnyAsync(p => p.Name == nameLower, ct))
                throw new InvalidOperationException(
                    $"A plan with name '{nameLower}' already exists.");

            // Trial plans must specify how many days the trial runs for
            if (cmd.IsTrial && cmd.TrialDurationDays <= 0)
                throw new InvalidOperationException(
                    "Trial duration (days) is required for trial plans.");

            var plan = new Plan
            {
                Id               = Guid.NewGuid(),
                Name             = nameLower,
                DisplayName      = cmd.DisplayName,
                Description      = cmd.Description,
                MaxUsers         = cmd.MaxUsers,
                MaxLeads         = cmd.MaxLeads,
                MaxDeals         = cmd.MaxDeals,
                MaxContacts      = cmd.MaxContacts,
                MaxCompanies     = cmd.MaxCompanies,
                StorageLimitBytes = PlanHelper.GbToBytes(cmd.StorageLimitGB),
                MonthlyPrice     = cmd.MonthlyPrice,
                AnnualPrice      = cmd.AnnualPrice,
                Features         = cmd.Features,
                SortOrder        = cmd.SortOrder,
                IsHighlighted    = cmd.IsHighlighted,
                IsPublic         = cmd.IsPublic,
                IsActive         = cmd.IsActive,
                BadgeText        = cmd.BadgeText,
                IsTrial            = cmd.IsTrial,
                TrialDurationDays  = cmd.IsTrial ? cmd.TrialDurationDays : 0,
                CreatedAtUtc     = DateTime.UtcNow,
                CreatedBy        = cmd.CreatedBy
            };

            _db.Plans.Add(plan);

            // Country pricing — persist every submitted row (including 0-priced
            // markets) so "not priced" is explicit rather than absent.
            if (cmd.PlanPricings is { Count: > 0 })
            {
                var order = 0;
                foreach (var pricing in cmd.PlanPricings)
                {
                    var row = new PlanPricing
                    {
                        Id           = Guid.NewGuid(),
                        PlanId       = plan.Id,
                        IsActive     = true,
                        CreatedAtUtc = DateTime.UtcNow,
                        CreatedBy    = cmd.CreatedBy
                    };
                    PlanHelper.ApplyInput(row, pricing, order++);
                    _db.PlanPricings.Add(row);
                }
            }

            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("Plan created: {PlanName} by {CreatedBy}", plan.Name, cmd.CreatedBy);

            return await new GetPlanDetailHandler(_db, _logger as ILogger<GetPlanDetailHandler>
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GetPlanDetailHandler>.Instance)
                .Handle(plan.Id, ct);
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating plan");
            throw;
        }
    }
}

// ── UPDATE PLAN ───────────────────────────────────────────────────────
public class UpdatePlanHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly ILogger<UpdatePlanHandler> _logger;

    public UpdatePlanHandler(FlowDbContext db, ILogger<UpdatePlanHandler> logger)
    {
        _db     = db;
        _logger = logger;
    }

    public async Task<PlanDto> Handle(UpdatePlanCommand cmd, CancellationToken ct = default)
    {
        try
        {
            var plan = await _db.Plans
                .Include(p => p.PlanPricings)
                .FirstOrDefaultAsync(p => p.Id == cmd.PlanId, ct)
                ?? throw new KeyNotFoundException($"Plan {cmd.PlanId} not found");

            // Trial plans must specify how many days the trial runs for
            if (cmd.IsTrial && cmd.TrialDurationDays <= 0)
                throw new InvalidOperationException(
                    "Trial duration (days) is required for trial plans.");

            // Note: Plan.Name is intentionally NOT updatable — it's the join key
            // to Tenants.Plan. Changing it would break grandfathered tenants.
            plan.DisplayName      = cmd.DisplayName;
            plan.Description      = cmd.Description;
            plan.MaxUsers         = cmd.MaxUsers;
            plan.MaxLeads         = cmd.MaxLeads;
            plan.MaxDeals         = cmd.MaxDeals;
            plan.MaxContacts      = cmd.MaxContacts;
            plan.MaxCompanies     = cmd.MaxCompanies;
            plan.StorageLimitBytes = PlanHelper.GbToBytes(cmd.StorageLimitGB);
            plan.MonthlyPrice     = cmd.MonthlyPrice;
            plan.AnnualPrice      = cmd.AnnualPrice;
            plan.Features         = cmd.Features;
            plan.SortOrder        = cmd.SortOrder;
            plan.IsHighlighted    = cmd.IsHighlighted;
            plan.IsPublic         = cmd.IsPublic;
            plan.IsActive         = cmd.IsActive;
            plan.BadgeText        = cmd.BadgeText;
            plan.IsTrial          = cmd.IsTrial;
            plan.TrialDurationDays = cmd.IsTrial ? cmd.TrialDurationDays : 0;
            plan.UpdatedAtUtc     = DateTime.UtcNow;
            plan.UpdatedBy        = cmd.UpdatedBy;

            // Country pricing — replace-in-place: update existing currencies,
            // add new ones, remove rows that were dropped from the form.
            var incoming = cmd.PlanPricings ?? new List<PlanPricingInput>();
            var existingByCurrency = plan.PlanPricings.ToDictionary(pp => pp.CurrencyCode);

            var order = 0;
            foreach (var pricing in incoming)
            {
                if (existingByCurrency.TryGetValue(pricing.CurrencyCode, out var row))
                {
                    PlanHelper.ApplyInput(row, pricing, order);
                }
                else
                {
                    var newRow = new PlanPricing
                    {
                        Id           = Guid.NewGuid(),
                        PlanId       = plan.Id,
                        IsActive     = true,
                        CreatedAtUtc = DateTime.UtcNow,
                        CreatedBy    = cmd.UpdatedBy
                    };
                    PlanHelper.ApplyInput(newRow, pricing, order);
                    plan.PlanPricings.Add(newRow);
                }
                order++;
            }

            var incomingCurrencies = incoming.Select(p => p.CurrencyCode).ToHashSet();
            var toRemove = plan.PlanPricings
                .Where(pp => !incomingCurrencies.Contains(pp.CurrencyCode))
                .ToList();
            foreach (var stale in toRemove)
                _db.PlanPricings.Remove(stale);

            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("Plan updated: {PlanName} by {UpdatedBy}", plan.Name, cmd.UpdatedBy);

            return await new GetPlanDetailHandler(_db, _logger as ILogger<GetPlanDetailHandler>
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GetPlanDetailHandler>.Instance)
                .Handle(plan.Id, ct);
        }
        catch (KeyNotFoundException) { throw; }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating plan {PlanId}", cmd.PlanId);
            throw;
        }
    }
}

// ── CHANGE TENANT PLAN (Super Admin upgrades/downgrades a tenant) ─────
public class ChangeTenantPlanHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly ILogger<ChangeTenantPlanHandler> _logger;

    public ChangeTenantPlanHandler(FlowDbContext db, ILogger<ChangeTenantPlanHandler> logger)
    {
        _db     = db;
        _logger = logger;
    }

    public async Task<ChangeTenantPlanResult> Handle(
        ChangeTenantPlanCommand cmd,
        CancellationToken ct = default)
    {
        try
        {
            var tenant = await _db.Tenants
                .FirstOrDefaultAsync(t => t.Id == cmd.TenantId && !t.IsDeleted, ct)
                ?? throw new KeyNotFoundException($"Tenant {cmd.TenantId} not found");

            var newPlan = await _db.Plans
                .FirstOrDefaultAsync(p => p.Name == cmd.NewPlanName.ToLower() && p.IsActive, ct)
                ?? throw new InvalidOperationException($"Plan '{cmd.NewPlanName}' not found or inactive");

            var oldPlanName = tenant.Plan;
            tenant.Plan        = newPlan.Name;
            tenant.UpdatedAtUtc = DateTime.UtcNow;
            tenant.UpdatedBy   = cmd.ChangedBy;

            // Optionally sync TenantSettings snapshot with new plan limits
            if (cmd.ApplyLimitsImmediately)
            {
                var settings = await _db.Set<TenantSettings>()
                    .FirstOrDefaultAsync(s => s.TenantId == cmd.TenantId, ct);

                if (settings == null)
                {
                    settings = new TenantSettings { Id = Guid.NewGuid(), TenantId = cmd.TenantId };
                    _db.Set<TenantSettings>().Add(settings);
                }

                settings.MaxUsers     = newPlan.MaxUsers;
                settings.MaxLeads     = newPlan.MaxLeads;
                settings.MaxDeals     = newPlan.MaxDeals;
                settings.StorageLimit = newPlan.StorageLimitBytes;
                settings.UpdatedAtUtc = DateTime.UtcNow;
                settings.UpdatedBy    = cmd.ChangedBy;
            }

            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Tenant {TenantId} plan changed {OldPlan} → {NewPlan} by {ChangedBy}",
                cmd.TenantId, oldPlanName, newPlan.Name, cmd.ChangedBy);

            return new ChangeTenantPlanResult(
                TenantId:      cmd.TenantId,
                OldPlan:       oldPlanName ?? "unknown",
                NewPlan:       newPlan.Name,
                LimitsApplied: cmd.ApplyLimitsImmediately
            );
        }
        catch (KeyNotFoundException) { throw; }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error changing plan for tenant {TenantId}", cmd.TenantId);
            throw;
        }
    }
}

// ── DEACTIVATE PLAN (safe — does not affect existing tenants) ─────────
public class DeactivatePlanHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly ILogger<DeactivatePlanHandler> _logger;

    public DeactivatePlanHandler(FlowDbContext db, ILogger<DeactivatePlanHandler> logger)
    {
        _db     = db;
        _logger = logger;
    }

    public async Task Handle(Guid planId, string deactivatedBy, CancellationToken ct = default)
    {
        try
        {
            var plan = await _db.Plans
                .FirstOrDefaultAsync(p => p.Id == planId, ct)
                ?? throw new KeyNotFoundException($"Plan {planId} not found");

            // Safety: don't deactivate a plan that still has active tenants
            // unless super admin explicitly handles migrations
            var activeTenantCount = await _db.Tenants
                .CountAsync(t => t.Plan == plan.Name && t.IsActive && !t.IsDeleted, ct);

            if (activeTenantCount > 0)
                throw new InvalidOperationException(
                    $"Cannot deactivate plan '{plan.DisplayName}' — " +
                    $"{activeTenantCount} active tenant(s) are on this plan. " +
                    $"Migrate them to another plan first, or set IsPublic=false to hide from new signups.");

            plan.IsActive     = false;
            plan.IsPublic     = false;
            plan.UpdatedAtUtc = DateTime.UtcNow;
            plan.UpdatedBy    = deactivatedBy;

            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("Plan deactivated: {PlanName} by {DeactivatedBy}",
                plan.Name, deactivatedBy);
        }
        catch (KeyNotFoundException) { throw; }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deactivating plan {PlanId}", planId);
            throw;
        }
    }
}
