// =====================================================================
// PipelineStageConfiguration.cs
// Location: MerkaiTrial.Infrastructure/Persistence/Configurations/
//
// COMPLETE FILE — replaces the existing one.
//
// CHANGES (019)
//   ✅ The five entry-requirement flags mapped with an explicit default
//      of false, so EF's generated SQL matches what 019_PipelineTransition
//      Rules.sql actually created.
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

        // ── Entry requirements (019) ──────────────────────────────────
        // Every one defaults to false in the database as well as in C#.
        // A stage inserted by any route — EF, a script, a support fix —
        // must start out asking nothing of a deal.
        b.Property(s => s.RequiresQuote)        .HasDefaultValue(false);
        b.Property(s => s.RequiresAcceptedQuote).HasDefaultValue(false);
        b.Property(s => s.RequiresCloseDate)    .HasDefaultValue(false);
        b.Property(s => s.RequiresValue)        .HasDefaultValue(false);
        b.Property(s => s.RequiresLostReason)   .HasDefaultValue(false);

        // Computed in C# only — there are no such columns.
        b.Ignore(s => s.IsTerminal);
        b.Ignore(s => s.HasEntryRequirements);

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
   ALSO REQUIRED — FlowDbContext.cs  (unchanged from the stage round,
   repeated here so this file still stands on its own)

   1. DbSet, alongside the others:

          public DbSet<PipelineStage> PipelineStages => Set<PipelineStage>();

   2. Global query filter, in ApplyTenantFilters with the strictly
      tenant-owned group. WITHOUT THIS the startup assertion
      (AssertEveryTenantEntityIsCovered) fails by name, which is the
      guard working as designed:

          b.Entity<PipelineStage>().HasQueryFilter(e => e.TenantId == CurrentTenantId);

   ===================================================================== */
