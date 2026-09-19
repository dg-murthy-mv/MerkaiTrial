// =====================================================================
// LeadStatusDefinitionConfiguration.cs
// Location: MerkaiTrial.Infrastructure/Persistence/Configurations/
//
// NEW FILE.
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
   ALSO REQUIRED — FlowDbContext.cs

   1. DbSet:

          public DbSet<LeadStatusDefinition> LeadStatusDefinitions
              => Set<LeadStatusDefinition>();

   2. Global query filter, with the strictly tenant-owned group. WITHOUT
      THIS the startup assertion fails by name — the guard working:

          b.Entity<LeadStatusDefinition>()
              .HasQueryFilter(e => e.TenantId == CurrentTenantId);

   3. Lead.Status changes type from LeadStatus (enum) to string. If there
      is an explicit configuration for it (HasConversion, HasColumnType),
      remove that — it is a plain nvarchar(50) now.
   ===================================================================== */
