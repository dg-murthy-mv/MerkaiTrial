// =====================================================================
// TransitionActionConfiguration.cs
// Location: MerkaiTrial.Infrastructure/Persistence/Configurations/
//
// NEW FILE (022).
// =====================================================================

using MerkaiTrial.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerkaiTrial.Infrastructure.Persistence.Configurations;

public class TransitionActionConfiguration : IEntityTypeConfiguration<TransitionAction>
{
    public void Configure(EntityTypeBuilder<TransitionAction> b)
    {
        b.ToTable("TransitionActions");
        b.HasKey(a => a.Id);

        b.Property(a => a.Subject)     .HasMaxLength(200) .IsRequired();
        b.Property(a => a.Description) .HasMaxLength(1000);
        b.Property(a => a.ActivityType).HasMaxLength(50)  .IsRequired();
        b.Property(a => a.CreatedBy)   .HasMaxLength(200);
        b.Property(a => a.UpdatedBy)   .HasMaxLength(200);

        b.Property(a => a.Kind)    .HasConversion<int>();
        b.Property(a => a.AssignTo).HasConversion<int>();

        b.Property(a => a.IsActive) .HasDefaultValue(true);
        b.Property(a => a.DueInDays).HasDefaultValue(1);

        // The runner's only question after a move: what hangs off this
        // step? Everything it then needs is in the index, so the answer
        // costs one seek.
        b.HasIndex(a => new { a.TenantId, a.TransitionId, a.SortOrder })
         .HasDatabaseName("IX_TransitionActions_Transition");

        // Cascade is right here and safe. An action describes what happens
        // when one particular step is taken; delete the step and it has no
        // meaning. It is also the ONLY cascade path into this table —
        // unlike ProcessTransitions' two keys into PipelineStages, which
        // have to be NoAction for exactly that reason.
        b.HasOne(a => a.Transition)
         .WithMany()
         .HasForeignKey(a => a.TransitionId)
         .HasConstraintName("FK_TransitionActions_Transition")
         .OnDelete(DeleteBehavior.Cascade);
    }
}

/* =====================================================================
   ALSO REQUIRED — FlowDbContext.cs

   1. DbSet, alongside the others:

          public DbSet<TransitionAction> TransitionActions
              => Set<TransitionAction>();

   2. Global query filter, in ApplyTenantFilters with the strictly
      tenant-owned group. WITHOUT THIS the startup assertion
      (AssertEveryTenantEntityIsCovered) fails BY NAME at boot — that is
      the guard doing its job, not a fault in this round:

          b.Entity<TransitionAction>()
              .HasQueryFilter(e => e.TenantId == CurrentTenantId);

   A NOTE ON THE CASCADE AND THE FILTER
      A filtered cascade is fine here because both ends carry the same
      TenantId, so a delete can never reach across tenants. If EF warns
      about a required relationship on an entity with a query filter, it
      is pointing at ProcessTransitions being filtered too — which is
      correct and needs no change.

   ===================================================================== */
