// =====================================================================
// FILE: MerkaiTrial.Application/Commands/Tenants/TenantProvisioningService.cs
//
// WHAT THIS FIXES
//
// CreateTenantHandler creates the tenant row and a TenantSettings
// snapshot — and stops there. For a trial that leaves five gaps, the
// first of which defeats the whole 30-day limit:
//
//   1. TrialStartedAt / TrialExpiresAt are never set. Tenant.IsOnTrial()
//      and IsTrialExpired() BOTH require TrialExpiresAt.HasValue, so with
//      NULL they both return false: IsAccountActive() is true forever, no
//      banner shows, and the read-only filter never fires. A trial made
//      through the UI never expires. Sathorn only works because we set
//      those columns by hand in SQL.
//
//   2. TenantSettings.FeatureFlags is left null, so HasFeature() returns
//      false for everything — a new tenant gets no Reports nav at all.
//
//   3. No admin user. The tenant exists and nobody can sign in.
//
//   4. No reference data. LeadSources and LeadChannels are now NOT NULL
//      on TenantId, so a new tenant has zero of each and the lead form's
//      dropdowns are empty. No TaxRates either.
//
//   5. No roles beyond the global tenant_admin, no invite link, no audit.
//
// EVERYTHING RUNS IN ONE TRANSACTION. A half-provisioned tenant is worse
// than a failed one — MadeeVision's missing TenantSettings row is exactly
// that failure, already sitting in your database.
// =====================================================================

using System.Text.Json;
using MerkaiTrial.Application.Commands.Plans;
using MerkaiTrial.Application.Security;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Commands.Tenants;

public record ProvisionTenantRequest(
    string Name,
    string FromEmail,
    string? Phone,
    Guid? CountryId,
    string DefaultCurrency,
    string TimeZone,
    string PreferredLanguage,
    string Plan,
    string? Domain,
    string? ReplyToEmail,

    // The client's administrator — the person who receives the invite.
    string AdminEmail,
    string AdminFirstName,
    string AdminLastName,

    bool SeedSampleData = false);

public record ProvisionTenantResult(
    Guid TenantId,
    string TenantName,
    Guid AdminUserId,
    string AdminEmail,
    string InviteToken,          // RAW — shown once, never stored
    DateTime InviteExpiresAtUtc,
    DateTime? TrialExpiresAtUtc,
    IReadOnlyList<string> Warnings);

public interface ITenantProvisioningService
{
    Task<IReadOnlyList<string>> CheckForDuplicateTrialsAsync(
        string tenantName, string fromEmail, CancellationToken ct = default);

    Task<ProvisionTenantResult> ProvisionAsync(
        ProvisionTenantRequest request, string provisionedBy, CancellationToken ct = default);
}

public class TenantProvisioningService : ITenantProvisioningService
{
    private readonly FlowDbContext _db;
    private readonly GetPlanByNameHandler _getPlan;
    private readonly IUserTokenService _tokens;
    private readonly IAuditService _audit;
    private readonly ILogger<TenantProvisioningService> _logger;

    private static readonly TimeSpan InviteLifetime = TimeSpan.FromHours(72);

    public TenantProvisioningService(
        FlowDbContext db,
        GetPlanByNameHandler getPlan,
        IUserTokenService tokens,
        IAuditService audit,
        ILogger<TenantProvisioningService> logger)
    {
        _db = db; _getPlan = getPlan; _tokens = tokens; _audit = audit; _logger = logger;
    }

    // =================================================================
    // DUPLICATE TRIAL CHECK
    //
    // The realistic bypass of a 30-day limit is not a forged token. It is
    // the same company asking for a second trial under a slightly
    // different name. Warnings, not a hard block — sometimes a second
    // trial is the right commercial answer. The point is that it becomes
    // a decision rather than an accident.
    // =================================================================
    public async Task<IReadOnlyList<string>> CheckForDuplicateTrialsAsync(
        string tenantName, string fromEmail, CancellationToken ct = default)
    {
        var warnings = new List<string>();

        var domain = fromEmail.Contains('@')
            ? fromEmail[(fromEmail.IndexOf('@') + 1)..].Trim().ToLowerInvariant()
            : null;

        if (!string.IsNullOrEmpty(domain))
        {
            // IgnoreQueryFilters throughout: provisioning is cross-tenant by
            // definition, and the caller's own tenant is MadeeVision.
            var sameDomain = await _db.Tenants.AsNoTracking().IgnoreQueryFilters()
                .Where(t => t.FromEmail != null && t.FromEmail.EndsWith("@" + domain))
                .Select(t => new { t.Name, t.Plan, t.TrialExpiresAt, t.IsDeleted })
                .ToListAsync(ct);

            foreach (var t in sameDomain)
            {
                var state = t.IsDeleted ? "deleted"
                          : t.TrialExpiresAt.HasValue && t.TrialExpiresAt <= DateTime.UtcNow ? "expired trial"
                          : t.Plan ?? "unknown plan";

                warnings.Add($"'{t.Name}' already uses the domain {domain} ({state}).");
            }
        }

        // Loose name match — catches "Acme Ltd" against an existing "Acme".
        var firstWord = tenantName.Trim().Split(' ').FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(firstWord) && firstWord.Length >= 4)
        {
            var similar = await _db.Tenants.AsNoTracking().IgnoreQueryFilters()
                .Where(t => t.Name.StartsWith(firstWord))
                .Select(t => t.Name)
                .ToListAsync(ct);

            foreach (var name in similar.Where(n =>
                        !string.Equals(n, tenantName, StringComparison.OrdinalIgnoreCase)))
            {
                warnings.Add($"A tenant named '{name}' already exists — possible repeat trial.");
            }
        }

        return warnings;
    }

    // =================================================================
    // PROVISION
    // =================================================================
    public async Task<ProvisionTenantResult> ProvisionAsync(
        ProvisionTenantRequest req, string provisionedBy, CancellationToken ct = default)
    {
        var plan = await _getPlan.Handle(req.Plan, ct);

        // Checked before the transaction so the failure is clean and the
        // message is specific.
        if (await _db.Tenants.IgnoreQueryFilters()
                .AnyAsync(t => t.FromEmail == req.FromEmail && !t.IsDeleted, ct))
            throw new InvalidOperationException(
                $"A workspace with the email '{req.FromEmail}' already exists.");

        // Email is globally unique among live users (UX_Users_Email_Live),
        // so a clash here would fail at SaveChanges with a raw index error.
        if (await _db.Users.IgnoreQueryFilters()
                .AnyAsync(u => u.Email == req.AdminEmail && !u.IsDeleted, ct))
            throw new InvalidOperationException(
                $"A user with the email '{req.AdminEmail}' already exists. " +
                "Each person can belong to only one workspace.");

        var warnings = await CheckForDuplicateTrialsAsync(req.Name, req.FromEmail, ct);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        try
        {
            var now = DateTime.UtcNow;

            // ── 1. TENANT ────────────────────────────────────────────
            var tenant = new Tenant
            {
                Id = Guid.NewGuid(),
                Name = req.Name.Trim(),
                TenantKey = await GenerateTenantKeyAsync(req.Name, ct),
                FromEmail = req.FromEmail.Trim(),
                ReplyToEmail = req.ReplyToEmail?.Trim(),
                Phone = req.Phone?.Trim(),
                CountryId = req.CountryId,
                DefaultCurrency = req.DefaultCurrency.Trim().ToUpperInvariant(),
                Timezone = req.TimeZone,
                PreferredLanguage = req.PreferredLanguage,
                Domain = req.Domain?.Trim(),
                Plan = plan.Name,
                IsActive = true,
                PublicLinkSecret = Guid.NewGuid().ToString("N"),
                CreatedAtUtc = now,
                CreatedBy = provisionedBy,
            };

            // ── THE FIX THAT MATTERS ─────────────────────────────────
            // Computed from Plan.TrialDurationDays, never hardcoded, so
            // extending a client to 45 days is a data change. Without
            // these three columns the trial never expires — IsOnTrial()
            // and IsTrialExpired() both require TrialExpiresAt.HasValue.
            if (plan.IsTrial && plan.TrialDurationDays > 0)
            {
                tenant.TrialStartedAt = now;
                tenant.TrialExpiresAt = now.AddDays(plan.TrialDurationDays);
                tenant.TrialActivatedBy = provisionedBy;
                tenant.TrialStatus = "Active";
            }

            _db.Tenants.Add(tenant);

            // ── 2. TENANT SETTINGS ───────────────────────────────────
            // FeatureFlags included. CreateTenantHandler omits it, so
            // HasFeature() returns false for everything and the new
            // tenant sees no Reports section at all.
            _db.Set<TenantSettings>().Add(new TenantSettings
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                MaxUsers = plan.MaxUsers,
                MaxLeads = plan.MaxLeads,
                MaxDeals = plan.MaxDeals,
                StorageLimit = plan.StorageLimitBytes,
                FeatureFlags = plan.Features,
                UpdatedAtUtc = now,
                UpdatedBy = provisionedBy,
            });

            // ── 3. ROLES ─────────────────────────────────────────────
            // Their own copies, matching what Sathorn has. tenant_admin
            // stays global (TenantId null) and is not copied.
            var roles = await BuildSeedRolesAsync(tenant.Id, now, provisionedBy, ct);
                _db.Roles.AddRange(roles);

            // ── 4. COUNTRY REFERENCE DATA ────────────────────────────
            var country = req.CountryId.HasValue
                ? await _db.Countries.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == req.CountryId.Value, ct)
                : null;

            SeedTaxRates(tenant.Id, country, now, provisionedBy);
            SeedLeadSourcesAndChannels(tenant.Id, country?.Code, now, provisionedBy);

            // ── 5. ADMIN USER ────────────────────────────────────────
            // No password. They set one through the invite link, which
            // also proves the email address.
            var adminUser = new User
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                FirstName = req.AdminFirstName.Trim(),
                LastName = req.AdminLastName.Trim(),
                Email = req.AdminEmail.Trim(),
                Phone = req.Phone?.Trim(),
                JobTitle = "Administrator",
                IsActive = true,
                IsTenantAdmin = true,
                IsSuperAdmin = false,
                PasswordHash = null,
                SecurityStamp = Guid.NewGuid().ToString("N"),
                MustChangePassword = false,
                CreatedAtUtc = now,
                CreatedBy = provisionedBy,
            };

            _db.Users.Add(adminUser);

            // tenant_admin is the shared system role — bypasses every
            // permission check, which is what a workspace owner needs.
            var tenantAdminRole = await _db.Roles.AsNoTracking().IgnoreQueryFilters()
                .FirstOrDefaultAsync(r => r.TenantId == null
                                       && r.Name == "tenant_admin"
                                       && !r.IsDeleted, ct)
                ?? throw new InvalidOperationException(
                    "The global 'tenant_admin' role is missing. Provisioning cannot continue.");

            _db.UserRoles.Add(new UserRole
            {
                Id = Guid.NewGuid(),
                UserId = adminUser.Id,
                RoleId = tenantAdminRole.Id,
                AssignedAtUtc = now,
                AssignedBy = provisionedBy,
            });

            await _db.SaveChangesAsync(ct);

            // ── 6. INVITE TOKEN ──────────────────────────────────────
            // Issued after SaveChanges so the FK to Users resolves. Only
            // the SHA-256 hash is stored; the raw value returned here is
            // the only copy that will ever exist.
            var inviteToken = await _tokens.IssueAsync(
                adminUser.Id, tenant.Id, TokenPurpose.Invite,
                InviteLifetime, provisionedBy, ct);

            await tx.CommitAsync(ct);

            _logger.LogInformation(
                "Provisioned tenant {TenantId} '{Name}' on plan {Plan}, admin {AdminEmail}, expires {Expiry}",
                tenant.Id, tenant.Name, plan.Name, adminUser.Email, tenant.TrialExpiresAt);

            // Audit AFTER commit — a failed provision should not leave an
            // audit row claiming it succeeded.
            await _audit.WriteAsync(
                AuditAction.TrialProvisioned, "Tenant", tenant.Id, tenant.Id,
                new
                {
                    tenant.Name,
                    Plan = plan.Name,
                    tenant.TrialExpiresAt,
                    AdminEmail = adminUser.Email,
                    WarningCount = warnings.Count
                }, ct);

            return new ProvisionTenantResult(
                TenantId: tenant.Id,
                TenantName: tenant.Name,
                AdminUserId: adminUser.Id,
                AdminEmail: adminUser.Email,
                InviteToken: inviteToken,
                InviteExpiresAtUtc: now.Add(InviteLifetime),
                TrialExpiresAtUtc: tenant.TrialExpiresAt,
                Warnings: warnings);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // =================================================================
    // SEED HELPERS
    // =================================================================

    /// <summary>
    /// Tax rates derived from the Country row — never hardcoded per
    /// market. Thailand gets VAT 7%, India GST 18%, UAE VAT 5%, and any
    /// country you add later works without a code change.
    ///
    /// The 0% row exists because every one of your four markets has
    /// zero-rated exports, and a client invoicing abroad needs it on day
    /// one rather than discovering it is missing.
    /// </summary>
    private void SeedTaxRates(Guid tenantId, Country? country, DateTime now, string by)
    {
        if (country is null) return;

        var label = string.IsNullOrWhiteSpace(country.TaxLabel) ? "Tax" : country.TaxLabel;
        var rate = country.DefaultTaxRate ?? 0m;

        _db.TaxRates.Add(new TaxRate
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CountryCode = country.Code,
            Name = $"{label} {rate:0.##}%",
            Rate = rate,
            IsDefault = true,
            IsActive = true,
            CreatedAtUtc = now,
            CreatedBy = by,
        });

        if (rate > 0)
        {
            _db.TaxRates.Add(new TaxRate
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                CountryCode = country.Code,
                Name = $"{label} 0% (Export)",
                Rate = 0m,
                IsDefault = false,
                IsActive = true,
                CreatedAtUtc = now,
                CreatedBy = by,
            });
        }
    }

    /// <summary>
    /// Country-aware defaults. Sathorn's list is genuinely Thai — LINE
    /// Official, Facebook Ads — and a Dubai tenant seeded with LINE would
    /// look careless in the first five minutes of a trial. WhatsApp in
    /// India and the UAE, Messenger and Viber in the Philippines.
    ///
    /// These are starting points a tenant edits, not a fixed taxonomy.
    /// </summary>
    private void SeedLeadSourcesAndChannels(Guid tenantId, string? countryCode, DateTime now, string by)
    {
        var (sources, channels) = (countryCode ?? "").ToUpperInvariant() switch
        {
            "TH" => (new[] { "Website", "LINE Official", "Facebook Ads", "Referral", "Walk-In" },
                     new[] { "LINE", "Email", "Phone", "Facebook", "In-Person" }),

            "IN" => (new[] { "Website", "WhatsApp", "Google Ads", "Referral", "Cold Call" },
                     new[] { "WhatsApp", "Email", "Phone", "LinkedIn", "In-Person" }),

            "PH" => (new[] { "Website", "Facebook Page", "Messenger", "Referral", "Walk-In" },
                     new[] { "Messenger", "Viber", "Email", "Phone", "In-Person" }),

            "AE" => (new[] { "Website", "WhatsApp", "Google Ads", "Referral", "Exhibition" },
                     new[] { "WhatsApp", "Email", "Phone", "LinkedIn", "In-Person" }),

            _ => (new[] { "Website", "Referral", "Email Campaign", "Cold Call", "Event" },
                     new[] { "Email", "Phone", "Website Form", "Social", "In-Person" }),
        };

        foreach (var name in sources)
        {
            _db.LeadSources.Add(new LeadSource
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Name = name,
                IsActive = true,
                IsDeleted = false,
                CreatedAtUtc = now,
                CreatedBy = by,
            });
        }

        foreach (var name in channels)
        {
            _db.LeadChannels.Add(new LeadChannel
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Name = name,
                IsActive = true,
                IsDeleted = false,
                CreatedAtUtc = now,
                CreatedBy = by,
            });
        }
    }

    /// <summary>
    /// The three roles every tenant starts with, matching Sathorn.
    ///
    /// MODULE KEY CASING: these use PascalCase ("Leads") because that is
    /// what the existing role JSON uses, and SignInService builds
    /// permission claims straight from these keys. PermissionHandler
    /// compares case-insensitively now, so either would work — but
    /// matching the existing data keeps everything consistent.
    ///
    /// VERIFY THESE against a real role before relying on them:
    ///   SELECT Name, Permissions FROM dbo.Roles
    ///    WHERE TenantId = 'BB100002-AAAA-0000-0000-000000000002';
    /// If Sathorn's grants differ, copy those instead — a seeded role that
    /// grants too much is a permission problem in every future tenant.
    /// </summary>
    private async Task<List<Role>> BuildSeedRolesAsync(
       Guid tenantId, DateTime now, string by, CancellationToken ct)
    {
        var templates = await _db.Set<RoleTemplate>()
            .AsNoTracking()
            .Where(t => t.IsActive)
            .OrderBy(t => t.SortOrder)
            .ToListAsync(ct);

        if (templates.Count == 0)
        {
            // Deliberately loud rather than silent. A tenant provisioned
            // with only tenant_admin looks fine until the client adds
            // their second user and has nothing to assign them.
            _logger.LogWarning(
                "No active RoleTemplates found — tenant {TenantId} will be created with " +
                "only the global tenant_admin role. Run migration 003.", tenantId);
        }

        return templates.Select(t => new Role
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = t.Name,
            DisplayName = t.DisplayName,
            Description = t.Description,
            IsSystemRole = false,          // the tenant owns and may edit these
            Permissions = t.Permissions,
            CreatedAtUtc = now,
            CreatedBy = by,
            IsDeleted = false,
        }).ToList();
    }

    private async Task<string> GenerateTenantKeyAsync(string name, CancellationToken ct)
    {
        var key = name.ToLowerInvariant()
            .Replace(" ", "-").Replace("&", "and")
            .Replace(".", "").Replace(",", "");

        var baseKey = key;
        var counter = 1;

        while (await _db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.TenantKey == key, ct))
            key = $"{baseKey}-{counter++}";

        return key;
    }
}

/* =====================================================================
   REGISTRATION — WebApi Program.cs (and Admin.Web if called directly)

       builder.Services.AddScoped<ITenantProvisioningService, TenantProvisioningService>();

   =====================================================================
   ONE THING I COULD NOT VERIFY

   I do not have the CreateTenantCommand record definition, so this
   service takes its own ProvisionTenantRequest rather than extending
   that command. That is deliberate anyway: provisioning needs the admin
   user's name and email, which tenant creation does not.

   CreateTenantHandler stays as-is for the SuperAdmin "create a tenant
   without a trial" path. If you would rather have one route, delete it
   and point everything here — but then every caller must supply admin
   details.

   =====================================================================
   ENTITY FIELDS TO CHECK

   I inferred these from usage; correct any that differ:

     TaxRate     — Id, TenantId, CountryCode, Name, Rate, IsDefault,
                   IsActive, CreatedAtUtc, CreatedBy
     LeadSource  — Id, TenantId, Name, IsActive, IsDeleted,
                   CreatedAtUtc, CreatedBy   (confirmed from its
                   configuration file)
     LeadChannel — assumed to mirror LeadSource

   If TaxRate has EffectiveFrom/EffectiveTo (CurrentTenantService filters
   on them), leaving both null is correct — that filter treats null as
   "always in effect".

   =====================================================================
   NOT DONE HERE

   Sample data (SeedSampleData is accepted and ignored). Tagged demo rows
   plus one-click purge is worth having, but it is a separate piece and
   provisioning is more urgent.
   ===================================================================== */
