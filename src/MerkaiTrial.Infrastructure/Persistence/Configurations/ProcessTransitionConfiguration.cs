// =====================================================================
// ProcessTransitionConfiguration.cs
// Location: MerkaiTrial.Infrastructure/Persistence/Configurations/
//
// NEW FILE (020).
// =====================================================================

using MerkaiTrial.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerkaiTrial.Infrastructure.Persistence.Configurations;

public class ProcessTransitionConfiguration : IEntityTypeConfiguration<ProcessTransition>
{
    public void Configure(EntityTypeBuilder<ProcessTransition> b)
    {
        b.ToTable("ProcessTransitions");
        b.HasKey(t => t.Id);

        // Same length as PipelineStage.Key — these are foreign keys onto
        // it, and a mismatch would stop SQL Server using the index.
        b.Property(t => t.FromStageKey).HasMaxLength(50).IsRequired();
        b.Property(t => t.ToStageKey)  .HasMaxLength(50).IsRequired();

        b.Property(t => t.Label)     .HasMaxLength(100).IsRequired();
        b.Property(t => t.NotePrompt).HasMaxLength(200);
        b.Property(t => t.CreatedBy) .HasMaxLength(200);
        b.Property(t => t.UpdatedBy) .HasMaxLength(200);

        b.Property(t => t.Actor).HasConversion<int>();

        b.Property(t => t.IsActive)             .HasDefaultValue(true);
        b.Property(t => t.RequiresQuote)        .HasDefaultValue(false);
        b.Property(t => t.RequiresAcceptedQuote).HasDefaultValue(false);
        b.Property(t => t.RequiresValue)        .HasDefaultValue(false);
        b.Property(t => t.RequiresCloseDate)    .HasDefaultValue(false);
        b.Property(t => t.RequiresNote)         .HasDefaultValue(false);
        b.Property(t => t.RequiresAttachment)   .HasDefaultValue(false);

        // Computed in C# only — there are no such columns.
        b.Ignore(t => t.HasRequirements);
        b.Ignore(t => t.EffectiveNotePrompt);

        // One row per from→to pair. Two rows for the same move would put
        // two buttons on the deal page doing the same thing, and the guard
        // would have to pick one.
        b.HasIndex(t => new { t.TenantId, t.FromStageKey, t.ToStageKey })
         .IsUnique()
         .HasDatabaseName("UX_ProcessTransitions_Tenant_From_To");

        // The deal page's only question: what can this deal do from here?
        b.HasIndex(t => new { t.TenantId, t.FromStageKey, t.SortOrder })
         .HasDatabaseName("IX_ProcessTransitions_Tenant_From_Order");

        // Composite FKs onto PipelineStages(TenantId, Key) — the same
        // relationship Deal.Stage already has. They guarantee a transition
        // cannot point at a stage that does not exist, which matters more
        // here than anywhere else: an orphaned transition is a button that
        // throws when clicked.
        //
        // NoAction on both. Two cascade paths into one table is a
        // multiple-cascade-path error, and a stage with transitions should
        // not be silently deletable anyway — DeletePipelineStageHandler
        // clears them first and says so.
        b.HasOne<PipelineStage>()
         .WithMany()
         .HasForeignKey(t => new { t.TenantId, t.FromStageKey })
         .HasPrincipalKey(s => new { s.TenantId, s.Key })
         .HasConstraintName("FK_ProcessTransitions_FromStage")
         .OnDelete(DeleteBehavior.NoAction);

        b.HasOne<PipelineStage>()
         .WithMany()
         .HasForeignKey(t => new { t.TenantId, t.ToStageKey })
         .HasPrincipalKey(s => new { s.TenantId, s.Key })
         .HasConstraintName("FK_ProcessTransitions_ToStage")
         .OnDelete(DeleteBehavior.NoAction);
    }
}

/* =====================================================================
   ALSO REQUIRED — FlowDbContext.cs

   1. DbSet, alongside the others:

          public DbSet<ProcessTransition> ProcessTransitions
              => Set<ProcessTransition>();

   2. Global query filter, in ApplyTenantFilters with the strictly
      tenant-owned group. WITHOUT THIS the startup assertion
      (AssertEveryTenantEntityIsCovered) fails BY NAME at boot — that is
      the guard doing its job, not a fault in this round:

          b.Entity<ProcessTransition>()
              .HasQueryFilter(e => e.TenantId == CurrentTenantId);

   A NOTE ON THE PRINCIPAL KEY
      HasPrincipalKey on (TenantId, Key) needs a unique index there, which
      UX_PipelineStages_Tenant_Key already provides. If EF complains that
      it cannot find an alternate key, it is because that index was
      created by the SQL script rather than declared in
      PipelineStageConfiguration — it IS declared there (.IsUnique()), so
      this should resolve. If it does not, add an explicit
      b.HasAlternateKey(s => new { s.TenantId, s.Key }) to
      PipelineStageConfiguration.

   ===================================================================== */
