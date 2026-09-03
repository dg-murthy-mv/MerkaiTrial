// =====================================================================
// TenantCommandHandlers.cs — REFACTORED
// Location: MerkaiTrial.Application/Commands/Tenants/TenantCommandHandlers.cs
//
// REMOVED: PlanLimits static class (hardcoded limits)
// REPLACED WITH: GetPlanByNameHandler → Plans table (DB-driven limits)
//
// All plan limit lookups now go through the Plans table so Super Admin
// can change limits at runtime without a code deploy.
// =====================================================================

using MerkaiTrial.Application.Commands.Plans;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Commands.Tenants
{
    // ==================== CREATE TENANT ====================
    public class CreateTenantHandler : ICommandHandler
    {
        private readonly FlowDbContext               _context;
        private readonly GetPlanByNameHandler        _getPlan;
        private readonly ILogger<CreateTenantHandler> _logger;

        public CreateTenantHandler(
            FlowDbContext               context,
            GetPlanByNameHandler        getPlan,
            ILogger<CreateTenantHandler> logger)
        {
            _context = context;
            _getPlan  = getPlan;
            _logger   = logger;
        }

        public async Task<TenantDto> Handle(
            CreateTenantCommand cmd,
            CancellationToken   cancellationToken = default)
        {
            try
            {
                // ── Duplicate email check ─────────────────────────────
                var exists = await _context.Tenants
                    .AnyAsync(t => t.FromEmail == cmd.FromEmail && !t.IsDeleted,
                              cancellationToken);

                if (exists)
                    throw new InvalidOperationException(
                        $"A tenant with email '{cmd.FromEmail}' already exists.");

                // ── Resolve plan from Plans table ─────────────────────
                // Throws KeyNotFoundException if plan name is not in DB.
                // This replaces the old: PlanLimits.GetLimits(cmd.Plan)
                var plan = await _getPlan.Handle(cmd.Plan, cancellationToken);

                // ── Build tenant ──────────────────────────────────────
                var tenantKey = await GenerateTenantKeyAsync(cmd.Name, cancellationToken);

                var tenant = new Tenant
                {
                    Id                = Guid.NewGuid(),
                    Name              = cmd.Name,
                    FromEmail         = cmd.FromEmail,
                    Phone             = cmd.Phone,
                    DefaultCurrency   = cmd.DefaultCurrency,
                    Timezone          = cmd.TimeZone,
                    CountryId         = cmd.CountryId,
                    PreferredLanguage = cmd.PreferredLanguage,
                    Plan              = plan.Name,   // canonical lowercase from Plans.Name
                    IsActive          = cmd.IsActive,
                    Domain            = cmd.Domain,
                    ReplyToEmail      = cmd.ReplyToEmail,
                    TenantKey         = tenantKey,
                    PublicLinkSecret  = Guid.NewGuid().ToString("N"),
                    CreatedAtUtc      = DateTime.UtcNow,
                    CreatedBy         = cmd.CreatedBy ?? "System"
                };

                _context.Tenants.Add(tenant);

                // ── TenantSettings snapshot from plan limits ──────────
                // This is the *applied ceiling* for this tenant.
                // Super Admin can override per-tenant via UpdateTenantSettingsHandler
                // with bypassPlanLimits=true for custom enterprise deals.
                var settings = new TenantSettings
                {
                    Id           = Guid.NewGuid(),
                    TenantId     = tenant.Id,
                    MaxUsers     = plan.MaxUsers,
                    MaxLeads     = plan.MaxLeads,
                    MaxDeals     = plan.MaxDeals,
                    StorageLimit = plan.StorageLimitBytes,
                    UpdatedAtUtc = DateTime.UtcNow,
                    UpdatedBy    = cmd.CreatedBy ?? "System"
                };

                _context.Set<TenantSettings>().Add(settings);
                await _context.SaveChangesAsync(cancellationToken);

                _logger.LogInformation(
                    "Tenant created: {TenantId} Name={Name} Plan={Plan}",
                    tenant.Id, tenant.Name, plan.Name);

                // ── Resolve country for return DTO ────────────────────
                string? countryCode = null;
                string  countryName = string.Empty;

                if (tenant.CountryId.HasValue)
                {
                    var countryInfo = await _context.Set<Country>()
                        .Where(c => c.Id == tenant.CountryId.Value)
                        .Select(c => new { c.Code, c.Name })
                        .FirstOrDefaultAsync(cancellationToken);

                    countryCode = countryInfo?.Code;
                    countryName = countryInfo?.Name ?? string.Empty;
                }

                return new TenantDto(
                    Id:              tenant.Id,
                    Name:            tenant.Name,
                    FromEmail:       tenant.FromEmail ?? string.Empty,
                    Phone:           tenant.Phone,
                    DefaultCurrency: tenant.DefaultCurrency,
                    TimeZone:        tenant.Timezone ?? "UTC",
                    CountryId:       tenant.CountryId,
                    CountryCode:     countryCode,
                    CountryName:     countryName,
                    IsActive:        tenant.IsActive,
                    CreatedAtUtc:    tenant.CreatedAtUtc ?? DateTime.UtcNow,
                    UpdatedAtUtc:    tenant.UpdatedAtUtc ?? DateTime.UtcNow,
                    Plan:            tenant.Plan
                );
            }
            catch (InvalidOperationException) { throw; }
            catch (KeyNotFoundException)      { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating tenant");
                throw;
            }
        }

        // Async — avoids sync DB call inside an async method
        private async Task<string> GenerateTenantKeyAsync(
            string name, CancellationToken ct)
        {
            var key = name.ToLower()
                .Replace(" ", "-")
                .Replace("&", "and")
                .Replace(".", "")
                .Replace(",", "");

            var baseKey = key;
            var counter = 1;

            while (await _context.Tenants.AnyAsync(t => t.TenantKey == key, ct))
                key = $"{baseKey}-{counter++}";

            return key;
        }
    }

    // ==================== UPDATE TENANT ====================
    public class UpdateTenantHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<UpdateTenantHandler> _logger;

        public UpdateTenantHandler(
            FlowDbContext context,
            ILogger<UpdateTenantHandler> logger)
        {
            _context = context;
            _logger  = logger;
        }

        public async Task Handle(
            UpdateTenantCommand cmd,
            CancellationToken   cancellationToken = default)
        {
            try
            {
                var tenant = await _context.Tenants
                    .FirstOrDefaultAsync(
                        t => t.Id == cmd.TenantId && !t.IsDeleted,
                        cancellationToken)
                    ?? throw new KeyNotFoundException($"Tenant {cmd.TenantId} not found");

                tenant.Name            = cmd.Name;
                tenant.FromEmail       = cmd.FromEmail;
                tenant.Phone           = cmd.Phone;
                tenant.DefaultCurrency = cmd.DefaultCurrency ?? tenant.DefaultCurrency;
                tenant.Timezone        = cmd.TimeZone;
                tenant.UpdatedAtUtc    = DateTime.UtcNow;
                tenant.UpdatedBy       = "System";

                await _context.SaveChangesAsync(cancellationToken);

                _logger.LogInformation("Tenant updated: {TenantId}", cmd.TenantId);
            }
            catch (KeyNotFoundException)      { throw; }
            catch (InvalidOperationException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating tenant {TenantId}", cmd.TenantId);
                throw;
            }
        }
    }

    // ==================== UPDATE TENANT STATUS ====================
    public class UpdateTenantStatusHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<UpdateTenantStatusHandler> _logger;

        public UpdateTenantStatusHandler(
            FlowDbContext context,
            ILogger<UpdateTenantStatusHandler> logger)
        {
            _context = context;
            _logger  = logger;
        }

        public async Task Handle(
            Guid              tenantId,
            bool              isActive,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var tenant = await _context.Tenants
                    .FirstOrDefaultAsync(
                        t => t.Id == tenantId && !t.IsDeleted,
                        cancellationToken)
                    ?? throw new KeyNotFoundException($"Tenant {tenantId} not found");

                tenant.IsActive     = isActive;
                tenant.UpdatedAtUtc = DateTime.UtcNow;

                await _context.SaveChangesAsync(cancellationToken);

                _logger.LogInformation(
                    "Tenant {TenantId} status → {IsActive}", tenantId, isActive);
            }
            catch (KeyNotFoundException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating status for tenant {TenantId}", tenantId);
                throw;
            }
        }
    }

    // ==================== DELETE TENANT (soft) ====================
    public class DeleteTenantHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<DeleteTenantHandler> _logger;

        public DeleteTenantHandler(
            FlowDbContext context,
            ILogger<DeleteTenantHandler> logger)
        {
            _context = context;
            _logger  = logger;
        }

        public async Task Handle(
            Guid              tenantId,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var tenant = await _context.Tenants
                    .FirstOrDefaultAsync(
                        t => t.Id == tenantId && !t.IsDeleted,
                        cancellationToken)
                    ?? throw new KeyNotFoundException($"Tenant {tenantId} not found");

                tenant.IsDeleted    = true;
                tenant.IsActive     = false;
                tenant.UpdatedAtUtc = DateTime.UtcNow;

                await _context.SaveChangesAsync(cancellationToken);

                _logger.LogInformation("Tenant soft-deleted: {TenantId}", tenantId);
            }
            catch (KeyNotFoundException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting tenant {TenantId}", tenantId);
                throw;
            }
        }
    }

    // ==================== UPDATE TENANT SETTINGS ====================
    // Validates requested settings against the tenant's current plan limits.
    // Super Admin can pass bypassPlanLimits=true for custom enterprise overrides —
    // e.g. a tenant on Professional but with 25 users by special arrangement.
    public class UpdateTenantSettingsHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly GetPlanByNameHandler _getPlan;
        private readonly ILogger<UpdateTenantSettingsHandler> _logger;

        public UpdateTenantSettingsHandler(
            FlowDbContext context,
            GetPlanByNameHandler getPlan,
            ILogger<UpdateTenantSettingsHandler> logger)
        {
            _context = context;
            _getPlan  = getPlan;
            _logger   = logger;
        }

        public async Task Handle(
            UpdateTenantSettingsCommand cmd,
            bool                        bypassPlanLimits  = false,
            CancellationToken           cancellationToken = default)
        {
            try
            {
                // ── Load tenant ───────────────────────────────────────
                var tenant = await _context.Tenants
                    .AsNoTracking()
                    .FirstOrDefaultAsync(
                        t => t.Id == cmd.TenantId && !t.IsDeleted,
                        cancellationToken)
                    ?? throw new KeyNotFoundException($"Tenant {cmd.TenantId} not found");

                // ── Validate against plan limits ──────────────────────
                // Skip validation if Super Admin is making a custom override.
                if (!bypassPlanLimits)
                {
                    var plan = await _getPlan.Handle(
                        tenant.Plan ?? "starter", cancellationToken);

                    var errors = new List<string>();

                    if (cmd.MaxUsers > plan.MaxUsers)
                        errors.Add(
                            $"MaxUsers ({cmd.MaxUsers}) exceeds '{plan.DisplayName}' limit of {plan.MaxUsers}");

                    if (cmd.MaxLeads > plan.MaxLeads)
                        errors.Add(
                            $"MaxLeads ({cmd.MaxLeads}) exceeds '{plan.DisplayName}' limit of {plan.MaxLeads}");

                    if (cmd.MaxDeals > plan.MaxDeals)
                        errors.Add(
                            $"MaxDeals ({cmd.MaxDeals}) exceeds '{plan.DisplayName}' limit of {plan.MaxDeals}");

                    if (cmd.StorageLimit > plan.StorageLimitBytes)
                    {
                        var reqGB = cmd.StorageLimit       / 1_073_741_824.0;
                        var maxGB = plan.StorageLimitBytes  / 1_073_741_824.0;
                        errors.Add(
                            $"Storage ({reqGB:F1} GB) exceeds '{plan.DisplayName}' limit of {maxGB:F1} GB");
                    }

                    if (errors.Any())
                        throw new InvalidOperationException(
                            $"Requested settings exceed the '{plan.DisplayName}' plan limits: " +
                            string.Join("; ", errors) +
                            ". Upgrade the tenant's plan or use the Super Admin bypass.");
                }

                // ── Upsert TenantSettings ─────────────────────────────
                var settings = await _context.Set<TenantSettings>()
                    .FirstOrDefaultAsync(
                        s => s.TenantId == cmd.TenantId,
                        cancellationToken);

                if (settings == null)
                {
                    settings = new TenantSettings
                    {
                        Id       = Guid.NewGuid(),
                        TenantId = cmd.TenantId
                    };
                    _context.Set<TenantSettings>().Add(settings);
                }

                settings.MaxUsers     = cmd.MaxUsers;
                settings.MaxLeads     = cmd.MaxLeads;
                settings.MaxDeals     = cmd.MaxDeals;
                settings.StorageLimit = cmd.StorageLimit;
                settings.FeatureFlags = cmd.FeatureFlags;
                settings.UpdatedAtUtc = DateTime.UtcNow;
                settings.UpdatedBy    = bypassPlanLimits ? "SuperAdmin" : "System";

                await _context.SaveChangesAsync(cancellationToken);

                _logger.LogInformation(
                    "TenantSettings updated: {TenantId} Bypass={Bypass}",
                    cmd.TenantId, bypassPlanLimits);
            }
            catch (KeyNotFoundException)      { throw; }
            catch (InvalidOperationException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating settings for tenant {TenantId}", cmd.TenantId);
                throw;
            }
        }
    }
}
