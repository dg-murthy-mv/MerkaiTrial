// =====================================================================
// FILE: MerkaiTrial.Infrastructure/Persistence/FlowDbContext.cs
//
// COMPLETE FILE — replaces your existing one.
//
// WHAT CHANGED
//   1. Constructor takes an optional ITenantProvider. Optional so
//      design-time tooling (dotnet ef, scaffolding) still works without
//      a DI container, and so DatabaseWarmup can resolve a context at
//      startup with no HttpContext.
//   2. DbSet<AuditLog> added.
//   3. ApplyTenantFilters — a global WHERE TenantId = @current on every
//      tenant-owned entity, so isolation is automatic rather than
//      something each query must remember.
//   4. AssertEveryTenantEntityIsCovered — startup guard. An entity with
//      a TenantId and no filter fails the build of the model, by name.
//   5. (014) Teams + RoleRecordScopes — record visibility. Both strictly
//      tenant-owned, both filtered.
//   6. (015) TeamManagers — which teams a user manages. Filtered.
//   7. (017) QuoteApprovalSettings + QuoteApprovalRequests — quote
//      approval rules and history. Both filtered.
//
// FAIL CLOSED: with no tenant resolved, CurrentTenantId is Guid.Empty and
// filtered queries return nothing. The legitimately tenant-less queries
// (login, cookie validation, invite links, public quote links, admin
// cross-tenant screens) call .IgnoreQueryFilters() explicitly.
// =====================================================================

using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using System.Reflection.Emit;

namespace MerkaiTrial.Infrastructure.Persistence;

public class FlowDbContext : DbContext
{
    private readonly ITenantProvider _tenantProvider;

    public FlowDbContext(DbContextOptions<FlowDbContext> opt,
                         ITenantProvider? tenantProvider = null)
        : base(opt)
    {
        _tenantProvider = tenantProvider ?? new NoTenantProvider();
    }

    /// <summary>
    /// Read by every query filter. EF parameterises instance-member
    /// access, so this is evaluated per query rather than baked into the
    /// compiled model — one model, correct for every tenant.
    /// </summary>
    private Guid CurrentTenantId => _tenantProvider.TenantId;
    public DbSet<RoleTemplate> RoleTemplates => Set<RoleTemplate>();

    // ── DbSets ───────────────────────────────────────────────────────
    public DbSet<PipelineStage> PipelineStages => Set<PipelineStage>();
    public DbSet<Attachment> Attachments { get; set; }
    public DbSet<Country> Countries { get; set; }
    public DbSet<Activity> Activities { get; set; }
    public DbSet<Plan> Plans { get; set; }
    public DbSet<UserToken> UserTokens { get; set; }
    public DbSet<PlanPricing> PlanPricings { get; set; }
    public DbSet<CompanyVertical> CompanyVerticals { get; set; }
    public DbSet<TaxRate> TaxRates { get; set; }
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<Consent> Consents => Set<Consent>();
    public DbSet<Lead> Leads => Set<Lead>();
    
    
    public DbSet<ChannelMessage> ChannelMessages => Set<ChannelMessage>();
    
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Deal> Deals { get; set; }
    public DbSet<DealActivity> DealActivities { get; set; }
    public DbSet<DealNote> DealNotes { get; set; }
    public DbSet<DealReminder> DealReminders { get; set; }
    public DbSet<Quote> Quotes => Set<Quote>();
    public DbSet<QuoteItem> QuoteItems => Set<QuoteItem>();
    public DbSet<Invoice> Invoices { get; set; }
    public DbSet<InvoiceLine> InvoiceLines { get; set; }
    public DbSet<Payment> Payments { get; set; }
    public DbSet<Product> Products { get; set; }
    public DbSet<User> Users => Set<User>();
    public DbSet<LeadChannel> LeadChannels { get; set; }
    public DbSet<LeadSource> LeadSources { get; set; }
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<TenantSettings> TenantSettings => Set<TenantSettings>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<LeadReminder> LeadReminders => Set<LeadReminder>();
    public DbSet<LeadStatusDefinition> LeadStatusDefinitions => Set<LeadStatusDefinition>();
    public DbSet<DealStageHistory> DealStageHistory { get; set; }
    public DbSet<LeadNote> LeadNotes => Set<LeadNote>();
    public DbSet<LeadActivity> LeadActivities => Set<LeadActivity>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<RoleRecordScope> RoleRecordScopes => Set<RoleRecordScope>();
    public DbSet<TeamManager> TeamManagers => Set<TeamManager>();
    public DbSet<PipelineRuleSettings> PipelineRuleSettings => Set<PipelineRuleSettings>();
    public DbSet<ProcessTransition> ProcessTransitions => Set<ProcessTransition>();
    public DbSet<TransitionAction> TransitionActions => Set<TransitionAction>();
    public DbSet<ApprovalRule> ApprovalRules { get; set; } = null!;
    public DbSet<ApprovalStep> ApprovalSteps { get; set; } = null!;
    public DbSet<QuoteApprovalDecision> QuoteApprovalDecisions { get; set; } = null!;


    // Quote approvals (017)
    public DbSet<QuoteApprovalSettings> QuoteApprovalSettings => Set<QuoteApprovalSettings>();
    public DbSet<QuoteApprovalRequest> QuoteApprovalRequests => Set<QuoteApprovalRequest>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.ApplyConfigurationsFromAssembly(typeof(FlowDbContext).Assembly);
        b.Ignore<Journey>();

        ApplyTenantFilters(b);
        AssertEveryTenantEntityIsCovered(b);
    }

    // =================================================================
    // TENANT FILTERS
    //
    // Explicit rather than reflection over "anything with a TenantId".
    // Two of these entities need DIFFERENT logic (NULL means shared), and
    // a reflection loop would silently apply the wrong rule. Explicit
    // also means adding an entity forces a decision — see the assertion.
    //
    // SOFT DELETE IS NOT FILTERED HERE. Existing queries already filter
    // IsDeleted explicitly, and the SuperAdmin and cleanup paths need
    // deleted rows. Adding it would silently change behaviour in about
    // forty places.
    // =================================================================
    private void ApplyTenantFilters(ModelBuilder b)
    {
        // ── Strictly tenant-owned ────────────────────────────────────
        b.Entity<Lead>()            .HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<LeadNote>()        .HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<LeadActivity>()    .HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<LeadReminder>()    .HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<LeadStatusDefinition>()
            .HasQueryFilter(e => e.TenantId == CurrentTenantId);

        b.Entity<PipelineRuleSettings>().HasQueryFilter(e => e.TenantId == CurrentTenantId);

        b.Entity<Deal>()            .HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<DealNote>()        .HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<DealActivity>()    .HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<DealReminder>()    .HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<DealStageHistory>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<TransitionAction>().HasQueryFilter(e => e.TenantId == CurrentTenantId);

        b.Entity<Quote>()           .HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<QuoteItem>()       .HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<Invoice>()         .HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<InvoiceLine>()     .HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<Payment>()         .HasQueryFilter(e => e.TenantId == CurrentTenantId);

        b.Entity<Company>()         .HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<Contact>()         .HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<Product>()         .HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<Activity>()        .HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<Attachment>()      .HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<TaxRate>()         .HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<TenantSettings>()  .HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<PipelineStage>().HasQueryFilter(e => e.TenantId == CurrentTenantId);

        // Record visibility (014). A tenant's teams and its scope overrides
        // are its own — including overrides of the shared built-in roles.
        b.Entity<Team>()            .HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<RoleRecordScope>() .HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<TeamManager>()     .HasQueryFilter(e => e.TenantId == CurrentTenantId);

        // Quote approvals (017) — a tenant's rules and approval history.
        b.Entity<QuoteApprovalSettings>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<QuoteApprovalRequest>() .HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<ProcessTransition>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<ApprovalRule>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<ApprovalStep>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<QuoteApprovalDecision>().HasQueryFilter(e => e.TenantId == CurrentTenantId);


        // LeadSources and LeadChannels: strict. Both have ZERO null rows,
        // and LeadSourceConfiguration already declares TenantId required.
        // Migration 002 step 5 makes the columns NOT NULL to match.
        b.Entity<LeadSource>()      .HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<LeadChannel>()     .HasQueryFilter(e => e.TenantId == CurrentTenantId);

        // ── Shared-or-tenant: NULL means available to every tenant ───
        // Roles: NULL = system role (tenant_admin), by design.
        // CompanyVerticals: all 19 rows are NULL today, i.e. pure
        // reference data. Permissive anyway, so that if a tenant ever
        // adds its own vertical it is scoped rather than shared with
        // everyone.
        b.Entity<Role>()            .HasQueryFilter(e => e.TenantId == null || e.TenantId == CurrentTenantId);
        b.Entity<CompanyVertical>() .HasQueryFilter(e => e.TenantId == null || e.TenantId == CurrentTenantId);

        // ── Users and UserTokens: deliberately NOT filtered ───────────
        // Login resolves a user by email with no tenant context, and the
        // cookie's OnValidatePrincipal reads Users on EVERY request
        // BEFORE HttpContext.User exists. A filter here rejects every
        // cookie and produces an endless bounce between /Dashboard and
        // /Account/Login with nothing in the logs.
        //
        // Users is scoped explicitly at every call site instead —
        // CurrentUserService and ApiCurrentUserService both already
        // filter on u.TenantId == tenantId.
        //
        // UserTokens likewise: invite and reset links are opened by
        // someone who is not signed in, by definition.

        // ── Deliberately global (reference data) ─────────────────────
        // Countries, Cultures, Plans, PlanPricings: shared by design.
        // Tenants has no TenantId column — it IS the tenant.
        // ChannelMessages and Consents have no TenantId column either;
        // they are reached through Contacts. Adding TenantId to them is
        // phase 2 work.
    }

    // =================================================================
    // COVERAGE ASSERTION
    //
    // Every entity carrying a TenantId must either have a filter or be
    // named in the exemption list. Adding a new entity without deciding
    // fails startup with a message naming it.
    //
    // This is what stops "audit every module and hope" from being an
    // ongoing task: the app will not boot with an unprotected tenant
    // entity in the model, including one added months from now.
    //
    // IF STARTUP THROWS naming Pipeline, Journey, InboxMessage or
    // anything else: those entities have a TenantId I have not seen.
    // Either add a filter above, or add the name here with a comment
    // saying why it is safe. Do not remove the assertion.
    // =================================================================
    private static readonly HashSet<string> TenantFilterExemptions = new()
    {
        nameof(User),       // scoped per call site; see note above
        nameof(UserToken),  // consumed by anonymous requests
        nameof(AuditLog),   // SuperAdmin reads are cross-tenant by design;
                            // read endpoints scope explicitly
    };

    private static void AssertEveryTenantEntityIsCovered(ModelBuilder b)
    {
        var unprotected = new List<string>();

        foreach (var entityType in b.Model.GetEntityTypes())
        {
            var name = entityType.ClrType.Name;

            if (TenantFilterExemptions.Contains(name)) continue;
            if (entityType.FindProperty("TenantId") is null) continue;
            if (entityType.GetQueryFilter() is not null) continue;

            unprotected.Add(name);
        }

        if (unprotected.Count > 0)
        {
            throw new InvalidOperationException(
                "FATAL: these entities have a TenantId but no global query filter, so every " +
                "query against them returns rows from every tenant: " +
                string.Join(", ", unprotected) + ". " +
                "Add a filter in ApplyTenantFilters, or add the name to TenantFilterExemptions " +
                "with a comment explaining why it is safe.");
        }
    }
}

/* =====================================================================
   REGISTRATION — both Program.cs files, before AddDbContext:

       builder.Services.AddScoped<ITenantProvider, HttpTenantProvider>();

   AddHttpContextAccessor() is already registered in both hosts.

   =====================================================================
   IF EF WARNS ABOUT REQUIRED NAVIGATIONS

   EF may warn that a filtered entity has a required navigation to
   another filtered entity — e.g. QuoteItem -> Quote. The warning is
   about a child being reachable when its parent is filtered out. Both
   sides carry the same TenantId here, so they filter identically and the
   case cannot arise. If it becomes noisy:

       optionsBuilder.ConfigureWarnings(w =>
           w.Ignore(CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning));

   Read the warning before silencing it — it is right more often than not.

   =====================================================================
   FIRST RUN CHECKLIST

   1. App starts. If it throws the FATAL message above, follow it.
   2. Log in. If login fails, SignInService is missing
      IgnoreQueryFilters on the user lookup.
   3. Load /Dashboard twice. An endless redirect to /Account/Login means
      OnValidatePrincipal is missing IgnoreQueryFilters.
   4. Check a page denies nothing it should allow. Zero permissions
      everywhere means BuildPrincipalAsync's role query is missing it.
   5. Open /Admin/Tenants, /Admin/Users and the ViewAs picker. EMPTY
      LISTS, not errors, are the symptom of a missing IgnoreQueryFilters
      in the API handler behind them.
   6. Open a public quote link.
   ===================================================================== */
