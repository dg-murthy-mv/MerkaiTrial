// =====================================================================
// LeadStatusDefinitionConfiguration.cs
// Location: MerkaiTrial.Infrastructure/Persistence/Configurations/
//
// COMPLETE FILE — replaces the existing one.
//
// CHANGES (025)
//   ✅ Color and Description mapped with the lengths 025 actually
//      created. Without this EF sends them as nvarchar(4000) parameters
//      against an nvarchar(7) column, which works right up until someone
//      pastes something long into the colour field and gets a truncation
//      error instead of a validation message.
// =====================================================================

using MerkaiTrial.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerkaiTrial.Infrastructure.Persistence.Configurations;

public class LeadStatusDefinitionConfiguration : IEntityTypeConfiguration<LeadStatusDefinition>
{
    public void Configure(EntityTypeBuilder<LeadStatusDefinition> b)
    {
        b.ToTable("LeadStatusDefinitions");
        b.HasKey(s => s.Id);

        b.Property(s => s.Key) .HasMaxLength(50) .IsRequired();
        b.Property(s => s.Name).HasMaxLength(100).IsRequired();
        b.Property(s => s.CreatedBy).HasMaxLength(200);
        b.Property(s => s.UpdatedBy).HasMaxLength(200);

        b.Property(s => s.Category).HasConversion<int>();

        // ── Presentation (025) ────────────────────────────────────────
        // Both nullable. NULL is a real state meaning "nobody chose one",
        // resolved at display time by StatusColors.Resolve — which is why
        // there is no HasDefaultValue here. A database default would only
        // reach rows inserted after the migration and would leave every
        // other insertion route to reinvent the same rule.
        b.Property(s => s.Color)      .HasMaxLength(7);
        b.Property(s => s.Description).HasMaxLength(500);

        // Lead.Status resolves against this, and the composite FK from
        // Leads depends on this index existing.
        b.HasIndex(s => new { s.TenantId, s.Key })
         .IsUnique()
         .HasDatabaseName("UX_LeadStatusDefinitions_Tenant_Key");

        b.HasIndex(s => new { s.TenantId, s.SortOrder })
         .HasDatabaseName("IX_LeadStatusDefinitions_Tenant_Order");
    }
}

/* =====================================================================
   NO FlowDbContext CHANGE THIS ROUND.

   LeadStatusDefinition already has its DbSet and its tenant query
   filter — 025 only adds two columns to an entity that is already
   registered. If the app boots today it will boot after this round.
   ===================================================================== */
