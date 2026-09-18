// =====================================================================
// PipelineStageConfiguration.cs
// Location: MerkaiTrial.Infrastructure/Persistence/Configurations/
//
// NEW FILE.
// =====================================================================

using MerkaiTrial.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerkaiTrial.Infrastructure.Persistence.Configurations;

public class PipelineStageConfiguration : IEntityTypeConfiguration<PipelineStage>
{
    public void Configure(EntityTypeBuilder<PipelineStage> b)
    {
        b.ToTable("PipelineStages");
        b.HasKey(s => s.Id);

        b.Property(s => s.Key) .HasMaxLength(50) .IsRequired();
        b.Property(s => s.Name).HasMaxLength(100).IsRequired();
        b.Property(s => s.CreatedBy).HasMaxLength(200);
        b.Property(s => s.UpdatedBy).HasMaxLength(200);

        // Stored as int. A string would be one more thing a migration
        // could get wrong, and the category is never shown raw.
        b.Property(s => s.Category).HasConversion<int>();

        // The Key must be unique within a tenant — Deal.Stage resolves
        // against it, and the composite FK from Deals depends on this
        // index existing.
        b.HasIndex(s => new { s.TenantId, s.Key })
         .IsUnique()
         .HasDatabaseName("UX_PipelineStages_Tenant_Key");

        b.HasIndex(s => new { s.TenantId, s.SortOrder })
         .HasDatabaseName("IX_PipelineStages_Tenant_Order");
    }
}

/* =====================================================================
   ALSO REQUIRED — FlowDbContext.cs

   1. DbSet, alongside the others:

          public DbSet<PipelineStage> PipelineStages => Set<PipelineStage>();

   2. Global query filter, in ApplyTenantFilters with the strictly
      tenant-owned group. WITHOUT THIS the startup assertion
      (AssertEveryTenantEntityIsCovered) fails by name, which is the
      guard working as designed:

          b.Entity<PipelineStage>().HasQueryFilter(e => e.TenantId == CurrentTenantId);

   ===================================================================== */
