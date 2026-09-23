// =====================================================================
// PipelineStageConfiguration.cs
// Location: MerkaiTrial.Infrastructure/Persistence/Configurations/
//
// COMPLETE FILE — replaces the existing one.
//
// CHANGES (023)
//   ✅ Color and Description mapped with the lengths 023 actually
//      created. Without this EF sends them as nvarchar(4000) parameters
//      against an nvarchar(7) column, which works right up until someone
//      pastes something long into the colour field and gets a truncation
//      error instead of a validation message.
//
// CHANGES (019 — unchanged, repeated so the file stands on its own)
//   ✅ The five entry-requirement flags mapped with an explicit default
//      of false, so EF's generated SQL matches what
//      019_PipelineTransitionRules.sql actually created.
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

        // ── Presentation (023) ────────────────────────────────────────
        // Both nullable. NULL is a real state meaning "nobody chose one",
        // resolved at display time by StageColors.Resolve — which is why
        // there is no HasDefaultValue here. A database default would only
        // reach rows inserted after the migration and would leave every
        // other insertion route to reinvent the same rule.
        b.Property(s => s.Color)      .HasMaxLength(7);
        b.Property(s => s.Description).HasMaxLength(500);

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
   NO FlowDbContext CHANGE THIS ROUND.

   PipelineStage already has its DbSet and its tenant query filter — 023
   only adds two columns to an entity that is already registered. If the
   app boots today it will boot after this round.

   (For reference, the two lines that were already there:

        public DbSet<PipelineStage> PipelineStages => Set<PipelineStage>();

        b.Entity<PipelineStage>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
   )
   ===================================================================== */
