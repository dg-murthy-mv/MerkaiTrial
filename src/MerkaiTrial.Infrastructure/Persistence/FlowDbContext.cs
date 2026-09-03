using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

using System.Reflection.Emit;

namespace MerkaiTrial.Infrastructure.Persistence;
public class FlowDbContext : DbContext {
    public FlowDbContext(DbContextOptions<FlowDbContext> opt) : base(opt) {}
    public DbSet<Attachment> Attachments { get; set; }
    public DbSet<Country> Countries { get; set; }
    public DbSet<Activity> Activities { get; set; }
    public DbSet<Plan> Plans { get; set; }
    public DbSet<UserToken> UserTokens { get; set; }
    public DbSet<PlanPricing> PlanPricings { get; set; }
    public DbSet<CompanyVertical> CompanyVerticals { get; set; }
    public DbSet<TaxRate> TaxRates { get; set; }
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<Consent> Consents => Set<Consent>();
    public DbSet<Lead> Leads => Set<Lead>();
    public DbSet<Pipeline> Pipelines => Set<Pipeline>();
    public DbSet<Journey> Journeys => Set<Journey>();
    public DbSet<ChannelMessage> ChannelMessages => Set<ChannelMessage>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();
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
    public DbSet<LeadChannel> LeadChannels { get; set; }  // ✅ ADD THIS
    public DbSet<LeadSource> LeadSources { get; set; }    // ✅ ADD THIS
    public DbSet<Role> Roles => Set<Role>();

    public DbSet<TenantSettings> TenantSettings => Set<TenantSettings>();

    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<LeadReminder> LeadReminders => Set<LeadReminder>();
    public DbSet<DealStageHistory> DealStageHistory { get; set; }
    
    public DbSet<LeadNote> LeadNotes => Set<LeadNote>();
    public DbSet<LeadActivity> LeadActivities => Set<LeadActivity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.ApplyConfigurationsFromAssembly(typeof(FlowDbContext).Assembly);

    }


}
