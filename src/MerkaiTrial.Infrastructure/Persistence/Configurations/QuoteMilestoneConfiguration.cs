// =====================================================================
// QuoteMilestoneConfiguration.cs
// Location: MerkaiTrial.Infrastructure/Persistence/Configurations/
//
// NEW FILE (071).
//
// Picked up automatically by
// b.ApplyConfigurationsFromAssembly(typeof(FlowDbContext).Assembly).
//
// ─────────────────────────────────────────────────────────────────────
// THE PART WORTH READING: PRECISION, AND TWO RELATIONSHIPS
//
// 1. Percent is DECIMAL(9,4), declared here and not left to EF's
//    DECIMAL(18,2) default. A third of a quote is 33.3333%; at two
//    decimal places it becomes 33.33, three of those come to 99.99, and
//    the quote is 0.01% short for reasons nobody can see from the
//    screen. This is the 062 bug — EF's default silently narrowing a
//    column — and Percent is exactly the shape of column it bites.
//
//    It is also the property whose COLUMN has a different name:
//    SharePercent, because PERCENT is a reserved T-SQL keyword. The
//    mapping is one line below and the reasoning is beside it.
//
// 2. The Invoice → QuoteMilestone relationship is configured FROM THIS
//    SIDE, because QuoteMilestone is the new entity and Invoice.cs
//    already has its own configuration elsewhere. NoAction: deleting a
//    milestone must never delete the invoice raised against it. The
//    application refuses to remove a milestone that has a live invoice,
//    and if a row were ever force-deleted behind its back, failing
//    loudly beats destroying a tax document.
//
// 3. The QuoteMilestone → Quote relationship CASCADES, which matches
//    FK_QuoteMilestones_Quotes_QuoteId in the migration. A schedule has
//    no meaning without its quote — the same reasoning ProductPrices
//    uses for its product. Quotes are soft-deleted in practice, so this
//    fires only on a hard delete.
// ─────────────────────────────────────────────────────────────────────
// =====================================================================

using MerkaiTrial.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerkaiTrial.Infrastructure.Persistence.Configurations;

public class QuoteMilestoneConfiguration : IEntityTypeConfiguration<QuoteMilestone>
{
    public void Configure(EntityTypeBuilder<QuoteMilestone> b)
    {
        b.ToTable("QuoteMilestones");
        b.HasKey(m => m.Id);

        b.Property(m => m.Name).HasMaxLength(120).IsRequired();
        b.Property(m => m.DueCondition).HasMaxLength(200);
        b.Property(m => m.CreatedBy).HasMaxLength(200);
        b.Property(m => m.UpdatedBy).HasMaxLength(200);

        // ⚠ THE COLUMN IS CALLED SharePercent, THE PROPERTY IS CALLED
        // Percent, AND THIS LINE IS THE ONLY THING JOINING THEM.
        //
        // PERCENT is a reserved T-SQL keyword — it is the one in
        // "SELECT TOP 10 PERCENT" — so a column named Percent has to be
        // written [Percent] in every query ever aimed at it, including
        // every ad-hoc SELECT typed into SSMS months from now. That is
        // where it would actually bite, long after EF (which quotes
        // every identifier anyway) had made it look harmless.
        //
        // Removing this line does not fail at startup. It fails at the
        // first query, with "Invalid column name 'Percent'".
        b.Property(m => m.Percent)
         .HasColumnName("SharePercent")
         .HasPrecision(9, 4);                      // see the header: 33.3333, not 33.33

        b.Property(m => m.FixedAmount).HasPrecision(18, 2);

        // ── Relationships ────────────────────────────────────────────
        b.HasOne(m => m.Quote)
         .WithMany(q => q.Milestones)
         .HasForeignKey(m => m.QuoteId)
         .OnDelete(DeleteBehavior.Cascade);

        // Configured from this side — see point 2 in the header.
        b.HasMany(m => m.Invoices)
         .WithOne(i => i.Milestone)
         .HasForeignKey(i => i.MilestoneId)
         .OnDelete(DeleteBehavior.NoAction);

        // Matches UX_QuoteMilestones_Quote_SortOrder. One row per
        // position: two rows claiming position 2 would print in an order
        // nothing decides. Filtered on IsDeleted so a stage can be
        // removed and the positions re-used.
        b.HasIndex(m => new { m.TenantId, m.QuoteId, m.SortOrder })
         .IsUnique()
         .HasFilter("[IsDeleted] = 0")
         .HasDatabaseName("UX_QuoteMilestones_Quote_SortOrder");
    }
}

/* =====================================================================
   ALSO REQUIRED — FlowDbContext.cs, and it is in this round's copy:

   1. DbSet:

          public DbSet<QuoteMilestone> QuoteMilestones
              => Set<QuoteMilestone>();

   2. Global query filter, with the STRICTLY TENANT-OWNED group.
      WITHOUT IT the startup assertion fails by name:

          b.Entity<QuoteMilestone>()
              .HasQueryFilter(e => e.TenantId == CurrentTenantId);

   3. ApplyDecimalPrecision — Invoice.MilestonePercent needs (9,4) for
      the same reason Percent does here. It is in this round's copy.

   AND — Quote.cs needs the Milestones collection and Invoice.cs needs
   both MilestoneId and the Milestone navigation, or the two HasOne /
   HasMany calls above do not compile. Both files are in this round.
   ===================================================================== */
