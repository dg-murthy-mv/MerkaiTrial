// =====================================================================
// PipelineRuleSettingsConfiguration.cs
// Location: MerkaiTrial.Infrastructure/Persistence/Configurations/
//
// NEW FILE (019).
// =====================================================================

using MerkaiTrial.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerkaiTrial.Infrastructure.Persistence.Configurations;

public class PipelineRuleSettingsConfiguration : IEntityTypeConfiguration<PipelineRuleSettings>
{
    public void Configure(EntityTypeBuilder<PipelineRuleSettings> b)
    {
        b.ToTable("PipelineRuleSettings");
        b.HasKey(r => r.Id);

        b.Property(r => r.UpdatedBy).HasMaxLength(200);

        b.Property(r => r.ForwardOnly)                 .HasDefaultValue(false);
        b.Property(r => r.ReopenRequiresReason)        .HasDefaultValue(true);
        b.Property(r => r.ReopenRestrictedToManagers)  .HasDefaultValue(true);
        b.Property(r => r.BlockReopenWithIssuedInvoice).HasDefaultValue(true);

        // One row per tenant. The save handler reads-then-writes, so
        // without this a double-submit would quietly create a second row
        // and the tenant would get whichever one EF read first.
        b.HasIndex(r => r.TenantId)
         .IsUnique()
         .HasDatabaseName("UX_PipelineRuleSettings_TenantId");

        b.HasOne(r => r.Tenant)
         .WithMany()
         .HasForeignKey(r => r.TenantId)
         .OnDelete(DeleteBehavior.Restrict);
    }
}

/* =====================================================================
   ALSO REQUIRED — FlowDbContext.cs

   1. DbSet, alongside the others:

          public DbSet<PipelineRuleSettings> PipelineRuleSettings
              => Set<PipelineRuleSettings>();

   2. Global query filter, in ApplyTenantFilters with the strictly
      tenant-owned group. WITHOUT THIS the startup assertion
      (AssertEveryTenantEntityIsCovered) fails BY NAME at boot — which is
      that guard doing its job, not a bug in this round:

          b.Entity<PipelineRuleSettings>()
              .HasQueryFilter(e => e.TenantId == CurrentTenantId);

   ===================================================================== */
