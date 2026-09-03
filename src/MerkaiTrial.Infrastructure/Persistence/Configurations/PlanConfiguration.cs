// =====================================================================
// PlanConfiguration.cs — EF Core Configuration
// Location: MerkaiTrial.Infrastructure/Persistence/Configurations/PlanConfiguration.cs
// =====================================================================

using MerkaiTrial.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerkaiTrial.Infrastructure.Persistence.Configurations;

public class PlanConfiguration : IEntityTypeConfiguration<Plan>
{
    public void Configure(EntityTypeBuilder<Plan> builder)
    {
        builder.ToTable("Plans");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Name)
            .IsRequired()
            .HasMaxLength(50);

        builder.HasIndex(p => p.Name)
            .IsUnique();  // "starter", "professional", "enterprise" must be unique keys

        builder.Property(p => p.DisplayName)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(p => p.Description)
            .HasMaxLength(500);

        builder.Property(p => p.MonthlyPrice)
            .HasPrecision(10, 2);

        builder.Property(p => p.AnnualPrice)
            .HasPrecision(10, 2);

        builder.Property(p => p.Features)
            .HasMaxLength(2000);   // JSON array of feature keys

        builder.Property(p => p.BadgeText)
            .HasMaxLength(50);

        builder.Property(p => p.CreatedBy)
            .HasMaxLength(100);

        builder.Property(p => p.UpdatedBy)
            .HasMaxLength(100);

        // Relationship: one Plan → many Tenants (loose, via Plan.Name string)
        // We do NOT add a hard FK from Tenants.Plan → Plans.Id intentionally.
        // Tenants.Plan stores the Plan.Name string for grandfathering flexibility.
        // The navigation here is informational only (for stats queries).
        builder.HasMany(p => p.Tenants)
            .WithOne()
            .HasPrincipalKey(p => p.Name)      // Plans.Name is the join key
            .HasForeignKey(t => t.Plan)         // Tenants.Plan is the string column
            .IsRequired(false)
            .OnDelete(DeleteBehavior.NoAction); // Never cascade-delete tenants if plan is removed

        builder.HasIndex(p => p.SortOrder);
        builder.HasIndex(p => p.IsActive);
    }
}
