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
//   8. (037) Notifications — in-app notifications. Strictly tenant-owned
//      and filtered. AssertEveryTenantEntityIsCovered would have refused
//      to start without this, which is exactly what that guard is for.
//   9. (038) OutboundMessages + UserNotificationPreferences. Both strictly
//      tenant-owned and filtered.
//  17. (083) InboundEmail + InboundEmailAttachment + EmailReplyToken —
//      email that came IN. All three STRICTLY tenant-owned.
//
//      ⚠ TWO PLACES READ THESE WITH NO TENANT AT ALL: the Resend webhook
//      (an anonymous call from Resend — there is no signed-in user) and
//      InboundEmailWorker (a background service). Both see Guid.Empty here,
//      exactly like OutboundMessageWorker, so both call IgnoreQueryFilters()
//      and put an explicit TenantId on every query they run after the
//      workspace is known. That is the ONLY place those calls are allowed;
//      every page-facing handler goes through the filter.
//  20. (105) UserSessions — one row per sign-in (VUL-11). NO TenantId:
//      a session belongs to a person and is read by the cookie check
//      before there is a tenant on the request. Nothing to filter.
//  19. (098) AutomationRule + AutomationRun — workflow automation. Both
//      STRICTLY tenant-owned. Round 099's background check reads them
//      with no tenant (IgnoreQueryFilters + an explicit TenantId on every
//      query, the OutboundMessageWorker rule); nothing page-facing does.
//  21. (101) ApplyAlwaysSendValues — EF always sends a number, yes/no or
//      status it has, instead of leaving "the default" to the database.
//      Every manual invoice was being saved as Sent because of this.
//  20. (099) AutomationTimeFire — which episode a time-based rule has
//      already acted on, per record. Tenant-owned, filtered like the rest.
//      The automation worker sets the workspace for its own scope
//      (WorkerTenantProvider), so the filters see the right tenant there.
//  18. (085) OutboundMessageAttachment + InvoiceSentCopy — the invoice
//      PDF on its way out, and the copy the customer can download. Both
//      STRICTLY tenant-owned.
//
//      ⚠ TWO MORE ANONYMOUS READERS, same rule as 17: the Resend
//      DELIVERY webhook (EmailDeliveryEvents — finds the OutboundMessage
//      by Resend's id) and the customer's invoice page (GetInvoiceBy-
//      TokenHandler — finds the Invoice by its public token). Both call
//      IgnoreQueryFilters() for that one lookup and put the TenantId of
//      the row they found on everything after it.
//  16. (075) CustomFieldDefinition + CustomFieldValue — tenant-defined
//      extra fields on records (Contacts first). BOTH STRICTLY
//      tenant-owned: there is no Merkai default field, and a value is
//      as private as the record it sits on. If either ever ends up in
//      the shared-or-tenant group, one workspace can read another's
//      field names and values. See CustomField.cs for why values live
//      in their own table rather than a JSON column.
//  15. (071) QuoteMilestone — the billing schedule on a quote, "30% on
//      signing, 40% on delivery, 30% on completion". Strictly
//      tenant-owned: a schedule is never shared, so TenantId is not
//      nullable and there is no Merkai default row. ZERO ROWS IS THE
//      NORMAL STATE and means "invoice the whole quote once", which is
//      what every quote raised before that round still does.
//  14. (070) ProductBundleItem — what a bundle contains. Strictly
//      tenant-owned, like ProductPrice. Two foreign keys to Products
//      (the bundle and the component), both spelled out in
//      ProductBundleItemConfiguration because EF pairs navigations with
//      foreign keys by convention and gets it wrong with two.
//  13. (069) ProductPrice — what a product costs in a currency that is
//      NOT the workspace's own. STRICTLY tenant-owned, unlike
//      ProductCategory above: TenantId is not nullable and there is no
//      shared row, because Merkai has no business publishing what
//      anybody's product costs.
//  12. (068) ProductCategory — product categories, which were a static
//      C# list until this round. Added to the SHARED list, not the
//      strict one: TenantId NULL is a Merkai default visible to every
//      workspace, and a row with a TenantId belongs to that workspace
//      alone. Under the strict filter no workspace could ever see a
//      default, which is precisely the bug item 11 below describes
//      having had for TaxRate.
//  11. (056) TaxRate MOVED from the strict list to the shared list —
//      NULL now means "system rate, every tenant in that country". See
//      the note beside it; it is the difference between that table
//      working and being decorative.
//  10. (040) TenantNotificationDefaults — the workspace's starting
//      position for each notification event, and the lock. Strictly
//      tenant-owned and filtered.
//
//      READ THE NOTE ON OutboundMessages BELOW BEFORE WRITING ANY QUERY
//      AGAINST IT FROM A BACKGROUND SERVICE. A worker has no HttpContext,
//      so CurrentTenantId is Guid.Empty and a filtered query returns
//      NOTHING — silently. The worker uses IgnoreQueryFilters and scopes
//      explicitly. This is the single easiest way to ship a queue that
//      never sends anything and logs no error at all.
//
// FAIL CLOSED: with no tenant resolved, CurrentTenantId is Guid.Empty and
// filtered queries return nothing. The legitimately tenant-less queries
// (login, cookie validation, invite links, public quote links, admin
// cross-tenant screens) call .IgnoreQueryFilters() explicitly.
// =====================================================================

using Microsoft.EntityFrameworkCore.Metadata;
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
    public DbSet<UserSession> UserSessions => Set<UserSession>();   // 105
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

    /// <summary>068. Shared-or-tenant: TenantId NULL = Merkai default.</summary>
    public DbSet<ProductCategory> ProductCategories => Set<ProductCategory>();

    /// <summary>
    /// 069. Strictly tenant-owned. Holds prices in currencies OTHER than
    /// the workspace's own; the home-currency price is Product.ListPrice.
    /// </summary>
    public DbSet<ProductPrice> ProductPrices => Set<ProductPrice>();

    /// <summary>
    /// 070. Strictly tenant-owned. What a bundle contains — one row per
    /// component, with a quantity. One level only: the application
    /// refuses a bundle inside a bundle.
    /// </summary>
    public DbSet<ProductBundleItem> ProductBundleItems => Set<ProductBundleItem>();

    /// <summary>
    /// 071. Strictly tenant-owned. The billing schedule on a quote — one
    /// row per stage, each a percentage OR a fixed amount. NO ROWS means
    /// "invoice the whole quote once", which is the default and what
    /// every quote raised before 071 does.
    /// </summary>
    public DbSet<QuoteMilestone> QuoteMilestones => Set<QuoteMilestone>();

    /// <summary>
    /// 075. Strictly tenant-owned. What extra fields a workspace records
    /// on a kind of record (EntityType = "Contact" in round A).
    /// </summary>
    public DbSet<CustomFieldDefinition> CustomFieldDefinitions => Set<CustomFieldDefinition>();

    /// <summary>
    /// 075. Strictly tenant-owned. One row per field per record that has
    /// a value; no row means "not filled in".
    /// </summary>
    public DbSet<CustomFieldValue> CustomFieldValues => Set<CustomFieldValue>();

    /// <summary>083. Email received for a workspace. Strictly tenant-owned.</summary>
    public DbSet<InboundEmail> InboundEmails => Set<InboundEmail>();

    /// <summary>083. Files that came with an InboundEmail. Strictly tenant-owned.</summary>
    public DbSet<InboundEmailAttachment> InboundEmailAttachments => Set<InboundEmailAttachment>();

    /// <summary>083. reply+&lt;token&gt; addresses → the record the email was about.</summary>
    public DbSet<EmailReplyToken> EmailReplyTokens => Set<EmailReplyToken>();
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


    // In-app notifications (037)
    public DbSet<Notification> Notifications => Set<Notification>();

    // Email / SMS outbox and per-user channel preferences (038)
    public DbSet<OutboundMessage> OutboundMessages => Set<OutboundMessage>();

    /// <summary>085. Files sent with an outbound message (the invoice PDF). Strictly tenant-owned.</summary>
    public DbSet<OutboundMessageAttachment> OutboundMessageAttachments => Set<OutboundMessageAttachment>();

    /// <summary>085. The invoice PDF as last emailed — for the customer's page. Strictly tenant-owned.</summary>
    public DbSet<InvoiceSentCopy> InvoiceSentCopies => Set<InvoiceSentCopy>();
    public DbSet<UserNotificationPreference> UserNotificationPreferences
        => Set<UserNotificationPreference>();

    // Workspace-level notification defaults and locks (040)
    public DbSet<TenantNotificationDefault> TenantNotificationDefaults
        => Set<TenantNotificationDefault>();

    /// <summary>
    /// 044. The eight notification events mapped to Meta-approved WhatsApp
    /// template names.
    ///
    /// NO TenantId, so the coverage assertion below ignores it — correctly.
    /// These are OUR templates on OUR WhatsApp number, for messages to the
    /// CRM's own users. A tenant messaging their own customers from their
    /// own WhatsApp account is phase 2 and gets its own table; see the note
    /// in WhatsAppTemplate.cs for why that is a separate table and not a
    /// nullable column here.
    /// </summary>
    public DbSet<WhatsAppTemplate> WhatsAppTemplates => Set<WhatsAppTemplate>();

    /// <summary>098. Workflow automation rules. Strictly tenant-owned.</summary>
    public DbSet<AutomationRule> AutomationRules => Set<AutomationRule>();

    /// <summary>098. One row per rule run — the rule log. Strictly tenant-owned.</summary>
    public DbSet<AutomationRun> AutomationRuns => Set<AutomationRun>();
    public DbSet<AutomationTimeFire> AutomationTimeFires => Set<AutomationTimeFire>();   // 099

    // Quote approvals (017)
    public DbSet<QuoteApprovalSettings> QuoteApprovalSettings => Set<QuoteApprovalSettings>();
    public DbSet<QuoteApprovalRequest> QuoteApprovalRequests => Set<QuoteApprovalRequest>();

    // =================================================================
    // RECORD CHANGED BY SOMEONE ELSE (102)
    //
    // Leads, Deals, Contacts and Companies carry a rowversion. EF adds
    // "AND RowVersion = <what was loaded>" to every UPDATE and DELETE of
    // them, and when another save got there first the row isn't found and
    // EF throws DbUpdateConcurrencyException — a type no page or
    // controller knew, so it surfaced as "Failed to update" or a 500.
    //
    // Here it becomes RecordChangedException: an InvalidOperationException
    // with a sentence for the person. Every controller already turns an
    // InvalidOperationException into a 400 with its message, and the four
    // edit endpoints turn this one into a 409 Conflict. Nothing was saved.
    // =================================================================
    //
    // 106 — TWO CHECKS BEFORE ANYTHING IS SENT (CheckBeforeSave)
    //   • AUDIT ROWS ARE ADD-ONLY. An AuditLog that is being changed or
    //     deleted stops the whole save (AuditLogChangeException). Nothing in
    //     the app does it today; this makes sure nothing ever can by
    //     accident. Sql/106_AuditLogs_AddOnly.sql says the same to anyone
    //     working on the database directly.
    //   • TEXT TOO LONG FOR ITS COLUMN is refused with a sentence the person
    //     can act on ("Full name can be at most 200 characters.") instead of
    //     SQL Server's "String or binary data would be truncated", which
    //     every controller answered with a 500. If SQL Server says it anyway
    //     (a column the model doesn't know the length of), that becomes the
    //     same exception with a general sentence.
    // Both are InvalidOperationExceptions, which every controller already
    // answers with a 400 and the message.
    // =================================================================
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        CheckBeforeSave();
        try { return base.SaveChanges(acceptAllChangesOnSuccess); }
        catch (DbUpdateConcurrencyException ex) { throw RecordChangedException.From(ex); }
        catch (DbUpdateException ex) when (ValueTooLongException.IsTruncation(ex)) { throw ValueTooLongException.General(ex); }
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        CheckBeforeSave();
        try { return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken); }
        catch (DbUpdateConcurrencyException ex) { throw RecordChangedException.From(ex); }
        catch (DbUpdateException ex) when (ValueTooLongException.IsTruncation(ex)) { throw ValueTooLongException.General(ex); }
    }

    /// <summary>106. Audit rows are add-only; text must fit its column. See above.</summary>
    private void CheckBeforeSave()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is AuditLog && entry.State is EntityState.Modified or EntityState.Deleted)
                throw new AuditLogChangeException();

            if (entry.State is not (EntityState.Added or EntityState.Modified)) continue;

            foreach (var property in entry.Properties)
            {
                if (property.Metadata.ClrType != typeof(string)) continue;
                if (entry.State == EntityState.Modified && !property.IsModified) continue;
                if (property.CurrentValue is not string text) continue;

                var max = property.Metadata.GetMaxLength();
                if (max is int limit && text.Length > limit)
                    throw ValueTooLongException.For(property.Metadata.Name, limit);
            }
        }
    }

    /// <summary>
    /// 107. Every context counts its database commands for the current
    /// request (Diagnostics/RequestCost.cs) — only while the test servers
    /// are counting one; otherwise the interceptor returns at once.
    /// </summary>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.AddInterceptors(MerkaiTrial.Infrastructure.Diagnostics.RequestCostInterceptor.Instance);
        base.OnConfiguring(optionsBuilder);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.ApplyConfigurationsFromAssembly(typeof(FlowDbContext).Assembly);
        b.Ignore<Journey>();

        ApplyDecimalPrecision(b);          // 062 — BEFORE the filters, see below
        ApplyAlwaysSendValues(b);          // 101 — see below
        ApplyTenantFilters(b);
        AssertEveryTenantEntityIsCovered(b);
    }

    // =================================================================
    // ALWAYS SEND VALUES (101)
    //
    // THE BUG. InvoiceConfiguration says Status.HasDefaultValue(Draft).
    // EF reads that as "the database fills this in", and when the C# value
    // is the type's zero — and Draft IS 0 — it leaves the column out of the
    // INSERT altogether. The database then used ITS default, which on the
    // real table is 1: Sent. So every manual invoice was saved already
    // Sent, never a draft, and never went past the issue step where the
    // approval limits are checked. Round 101's test 82 caught it.
    //
    // The same trap is on every value-type property with a fixed default:
    // a product saved switched OFF (false) would come out switched ON if
    // the column says DEFAULT 1; a "due in 0 days" action would become 1.
    // CustomFieldConfiguration already worked around it for one bool
    // (075); this closes it everywhere.
    //
    // THE FIX. For a non-nullable value type (enum, bool, int, decimal…)
    // with a fixed HasDefaultValue — not a SQL default like GETUTCDATE() —
    // EF is told the value is never generated by the database, so it
    // always sends what the object holds. Every such property in Domain
    // already starts at the same value in C# (= true, = 1, = Draft), so
    // nothing that leaves it alone changes. The SQL DEFAULTs stay, for
    // hand-written INSERTs.
    // =================================================================
    private static void ApplyAlwaysSendValues(ModelBuilder b)
    {
        foreach (var entity in b.Model.GetEntityTypes())
        {
            foreach (var p in entity.GetProperties())
            {
                if (p.IsKey() || p.IsConcurrencyToken) continue;
                if (!p.ClrType.IsValueType || Nullable.GetUnderlyingType(p.ClrType) is not null) continue;
                if (p.FindAnnotation(RelationalAnnotationNames.DefaultValue) is null) continue;
                if (p.GetDefaultValueSql() is not null || p.GetComputedColumnSql() is not null) continue;
                if (p.ValueGenerated != ValueGenerated.OnAdd) continue;

                p.ValueGenerated = ValueGenerated.Never;
            }
        }
    }

    // =================================================================
    // DECIMAL PRECISION (062)
    //
    // EF Core's default for a `decimal` on SQL Server is DECIMAL(18,2),
    // and it applies that to any property nothing has configured. It says
    // so at startup, once per property:
    //
    //   [WRN] No store type was specified for the decimal property
    //   'Quantity' on entity type 'QuoteItem'. This will cause values to
    //   be SILENTLY TRUNCATED if they do not fit in the default precision
    //   and scale.
    //
    // THE BUG THAT WARNING WAS DESCRIBING. 052 retyped Quantity to
    // DECIMAL(18,4) in SQL and taught QuoteLineChecks to allow four
    // decimal places. The column can hold 12.5678 and the validator lets
    // it through — but EF sent the parameter as DECIMAL(18,2), so SQL
    // Server rounded it to 12.57 on the way in and stored 12.5700. The
    // whole point of 052 — 12.5 m² of flooring, 3.5 consulting days —
    // happened to work only because those have one decimal place.
    //
    // Nothing reported it. No exception, no warning at write time, no
    // difference on screen until somebody checked a figure.
    //
    // DiscountPercent is the mirror image: DECIMAL(5,2) in SQL, so it
    // tops out at 999.99, while EF believed it had eighteen digits to
    // play with. The scale matches, so nothing was truncated — but a
    // nonsense percentage would have reached SQL Server and come back as
    // "Arithmetic overflow error converting numeric to data type
    // numeric", which names no column and no row.
    //
    // THE NUMBERS BELOW MUST MATCH THE COLUMNS, not each other:
    //   QuoteItems.Quantity          DECIMAL(18,4)   052
    //   InvoiceLines.Quantity        DECIMAL(18,4)   052
    //   QuoteItems.DiscountPercent   DECIMAL(5,2)    055
    //   InvoiceLines.DiscountPercent DECIMAL(5,2)    055
    //
    // If you ever ALTER one of those columns, change it here in the same
    // round. A mismatch in either direction is silent.
    //
    // Called BEFORE ApplyTenantFilters only for readability — precision
    // and filters are independent. It runs AFTER
    // ApplyConfigurationsFromAssembly, so these win over anything an
    // IEntityTypeConfiguration might set later; today none of them
    // configure these four, which is why EF was warning at all.
    // =================================================================
    private static void ApplyDecimalPrecision(ModelBuilder b)
    {
        // 052 — a quantity can be 12.5 m² or 3.5 days. Four places.
        b.Entity<QuoteItem>()  .Property(e => e.Quantity).HasPrecision(18, 4);
        b.Entity<InvoiceLine>().Property(e => e.Quantity).HasPrecision(18, 4);

        // 055 — a percentage. Five digits total, two after the point:
        // 0.00 to 999.99. The 0–100 range itself is enforced in
        // LineDiscounts and on the editor, not here.
        b.Entity<QuoteItem>()  .Property(e => e.DiscountPercent).HasPrecision(5, 2);
        b.Entity<InvoiceLine>().Property(e => e.DiscountPercent).HasPrecision(5, 2);

        // 071 — the milestone share, as a percent. (9,4), matching
        // QuoteMilestone.Percent and dbo.Invoices.MilestonePercent.
        //
        // This is the one line in this round that the 062 note is about.
        // A third of a quote is 33.3333%; at EF's default two places it
        // becomes 33.33, three of those come to 99.99, and the schedule
        // is 0.01% short for a reason nothing on the screen explains.
        // QuoteMilestone.Percent itself is declared in
        // QuoteMilestoneConfiguration; this is the snapshot copy that
        // lands on the invoice, and it needs the same shape or the
        // snapshot disagrees with the schedule it came from.
        b.Entity<Invoice>()    .Property(e => e.MilestonePercent).HasPrecision(9, 4);
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

        // QuoteMilestones (071). Strict. A billing schedule is one
        // workspace's commercial terms with one customer; there is no
        // version of this table where a Merkai default row makes sense,
        // so it is deliberately NOT in the shared-or-tenant group with
        // Role, CompanyVertical, TaxRate and ProductCategory.
        b.Entity<QuoteMilestone>()  .HasQueryFilter(e => e.TenantId == CurrentTenantId);

        // Custom fields (075). Strict, both of them. A field definition is
        // one workspace's private schema ("Credit limit", "Account
        // manager's mobile"), and a value is as private as the contact it
        // sits on. Neither has any business in the shared-or-tenant group.
        b.Entity<CustomFieldDefinition>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<CustomFieldValue>()     .HasQueryFilter(e => e.TenantId == CurrentTenantId);

        // Inbound email (083). Strict, all three: a customer's email is as
        // private as the record it is filed on, and an unmatched one is
        // more private still. See item 17 in the header for the two
        // tenant-less readers (webhook and worker) and why they are the
        // only callers allowed to IgnoreQueryFilters.
        b.Entity<InboundEmail>()          .HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<InboundEmailAttachment>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<EmailReplyToken>()       .HasQueryFilter(e => e.TenantId == CurrentTenantId);

        b.Entity<Invoice>()         .HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<InvoiceLine>()     .HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<Payment>()         .HasQueryFilter(e => e.TenantId == CurrentTenantId);

        b.Entity<Company>()         .HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<Contact>()         .HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<Product>()         .HasQueryFilter(e => e.TenantId == CurrentTenantId);

        // ProductPrices (069). STRICT, and that is the whole point of the
        // distinction from ProductCategory two sections down: a category
        // can sensibly be a Merkai default shared with everybody, a price
        // never can. If this ever ends up in the shared group, every
        // workspace will be able to read every other workspace's prices.
        b.Entity<ProductPrice>()    .HasQueryFilter(e => e.TenantId == CurrentTenantId);

        // ProductBundleItems (070). Strict, like the prices above: what
        // is inside somebody's package is their business.
        b.Entity<ProductBundleItem>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<Activity>()        .HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<Attachment>()      .HasQueryFilter(e => e.TenantId == CurrentTenantId);
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


        // In-app notifications (037). Strictly tenant-owned. Note that the
        // filter is on TenantId only, NOT on RecipientUserId: a per-USER
        // filter here would be wrong, because the context has no reliable
        // notion of "the current user" at model-building time, and because
        // one person's notification about a deal is legitimately readable by
        // the code that raised it. Scoping to the recipient is done at every
        // call site in the handlers, the same way Users is.
        b.Entity<Notification>()    .HasQueryFilter(e => e.TenantId == CurrentTenantId);

        // Outbox and channel preferences (038). Both strictly tenant-owned.
        //
        // ⚠ OutboundMessages IS READ BY A BACKGROUND WORKER, which has no
        // HttpContext and therefore no tenant: CurrentTenantId is Guid.Empty
        // there, so this filter would make every claim query return zero
        // rows and the queue would sit full for ever WITHOUT AN ERROR.
        //
        // The filter stays, because the log page and every in-request query
        // need it and because the coverage assertion is right to demand it.
        // OutboundMessageWorker calls IgnoreQueryFilters() — it never
        // returns rows to a user, it only sends them to the address already
        // stored on the row. 085: so does EmailDeliveryEvents (Resend's
        // delivery webhook, anonymous), for the single lookup by Resend's
        // id; it writes the status back and returns nothing to anybody.
        b.Entity<OutboundMessage>() .HasQueryFilter(e => e.TenantId == CurrentTenantId);

        // 085 — see item 18 in the header.
        b.Entity<OutboundMessageAttachment>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<InvoiceSentCopy>()          .HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<UserNotificationPreference>()
                                    .HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<TenantNotificationDefault>()
                                    .HasQueryFilter(e => e.TenantId == CurrentTenantId);

        // Automation (098). Strict, both — one workspace's rules and the
        // log of what they did to its records. See item 19 in the header.
        b.Entity<AutomationRule>()  .HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<AutomationRun>()   .HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<AutomationTimeFire>().HasQueryFilter(e => e.TenantId == CurrentTenantId);   // 099

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

        // TaxRates (056). MOVED HERE from the strict list above, where it
        // had always been — and that was the bug. A system rate is exactly
        // the shared-reference case this section exists for: the standard
        // VAT or GST for a country, published once for every tenant that
        // sells there. Under the strict filter no tenant could ever see one,
        // so GetDefaultTaxRateAsync fell through to Country.DefaultTaxRate
        // on every single call and the whole table was decoration.
        //
        // A SuperAdmin has no tenant, so CurrentTenantId is Guid.Empty and
        // this resolves to "system rates only" for them — which is exactly
        // what the /Admin/TaxRates screens should see.
        //
        // The write side does NOT rely on this: a filter that admits system
        // rows would let a tenant admin edit one. TaxRateCommandHelper
        // checks ownership explicitly on every update and delete.
        b.Entity<TaxRate>()         .HasQueryFilter(e => e.TenantId == null || e.TenantId == CurrentTenantId);

        // ProductCategories (068). Here from the start rather than being
        // moved here later like TaxRate was, for the same reason: a
        // Merkai default IS the shared-reference case this section
        // exists for. Under the strict filter every workspace would open
        // the products page to an empty category dropdown.
        //
        // How the EFFECTIVE list is resolved on top of this filter —
        // "own rows if you have any, defaults otherwise" — lives in
        // ProductCategoryResolution (Application/Configuration/
        // ProductCategoriesConfiguration.cs), not here. This filter only
        // decides what a workspace is ALLOWED to see.
        //
        // A SuperAdmin has no tenant, so CurrentTenantId is Guid.Empty
        // and this resolves to "Merkai defaults only" for them, which is
        // the correct thing for a platform-level screen to show.
        //
        // The write side does NOT rely on this filter. It admits system
        // rows, so on its own it would let a tenant admin rename a Merkai
        // default for every workspace at once. Ownership is checked
        // explicitly in ProductCategoryHandlers on every update, delete
        // and reorder — the same arrangement TaxRateCommandHelper uses.
        b.Entity<ProductCategory>() .HasQueryFilter(e => e.TenantId == null || e.TenantId == CurrentTenantId);

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

/// <summary>
/// 102. Someone else saved this record after it was loaded, so this save
/// was refused rather than overwrite their change. Nothing was saved.
/// Thrown by FlowDbContext (a stale rowversion at save time) and by
/// RecordVersion.Expect (an edit form posting an older version).
/// </summary>
public sealed class RecordChangedException : InvalidOperationException
{
    public RecordChangedException(string message, Exception? inner = null) : base(message, inner) { }

    /// <summary>The person-facing sentence for a record kind ("lead", "deal"…).</summary>
    public static string MessageFor(string kind) =>
        $"Someone else changed this {kind} while you were editing it, so your changes were not saved. " +
        $"Reload the {kind} to see what they changed, then make your changes again.";

    public static RecordChangedException From(DbUpdateConcurrencyException ex)
    {
        var type = ex.Entries.FirstOrDefault()?.Metadata.ClrType.Name;
        var kind = type switch
        {
            "Lead" => "lead",
            "Deal" => "deal",
            "Contact" => "contact",
            "Company" => "company",
            _ => "record",
        };
        return new RecordChangedException(MessageFor(kind), ex);
    }
}

/// <summary>
/// 106. Text longer than its database column allows. Nothing was saved.
/// The message names the field in words ("Full name can be at most 200
/// characters.").
/// </summary>
public sealed class ValueTooLongException : InvalidOperationException
{
    private ValueTooLongException(string message, Exception? inner = null) : base(message, inner) { }

    public static ValueTooLongException For(string propertyName, int max) =>
        new($"{Words(propertyName)} can be at most {max:N0} characters.");

    public static ValueTooLongException General(Exception inner) =>
        new("One of the values is too long to save. Shorten the longest text and try again.", inner);

    /// <summary>SQL Server 2628 / 8152: "String or binary data would be truncated".</summary>
    public static bool IsTruncation(DbUpdateException ex) =>
        ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2628 or 8152 };

    /// <summary>"FullName" → "Full name", "ReplyToEmail" → "Reply to email".</summary>
    private static string Words(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1])) sb.Append(' ').Append(char.ToLowerInvariant(c));
            else sb.Append(i == 0 ? char.ToUpperInvariant(c) : c);
        }
        return sb.ToString();
    }
}

/// <summary>106. An audit row was about to be changed or deleted. Nothing was saved.</summary>
public sealed class AuditLogChangeException : InvalidOperationException
{
    public AuditLogChangeException()
        : base("Audit records can't be changed or deleted.") { }
}
