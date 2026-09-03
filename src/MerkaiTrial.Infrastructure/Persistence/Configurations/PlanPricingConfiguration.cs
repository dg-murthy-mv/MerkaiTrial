// =====================================================================
// PlanPricingConfiguration.cs — EF Core Configuration
// Location: MerkaiTrial.Infrastructure/Persistence/Configurations/PlanPricingConfiguration.cs
//
// Previously PlanPricing had no explicit configuration and relied on
// EF Core conventions. That's how the CurrencySymbol/CountryName columns
// ended up ambiguous about Unicode support — nothing enforced it. This
// makes the intent explicit so it can't regress silently again.
// =====================================================================

using MerkaiTrial.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerkaiTrial.Infrastructure.Persistence.Configurations;

public class PlanPricingConfiguration : IEntityTypeConfiguration<PlanPricing>
{
    public void Configure(EntityTypeBuilder<PlanPricing> builder)
    {
        builder.ToTable("PlanPricings");

        builder.HasKey(pp => pp.Id);

        builder.Property(pp => pp.CurrencyCode)
            .IsRequired()
            .IsUnicode(false)      // ISO codes are always ASCII — "USD", "THB", etc.
            .HasMaxLength(3);

        builder.Property(pp => pp.CountryCode)
            .IsRequired()
            .IsUnicode(false)      // ISO codes / "*" — always ASCII
            .HasMaxLength(2);

        builder.Property(pp => pp.CountryName)
            .IsRequired()
            .IsUnicode(true)       // explicit nvarchar — country names are ASCII today
            .HasMaxLength(100);    // but no reason to close the door on non-Latin names later

        builder.Property(pp => pp.CurrencySymbol)
            .IsRequired()
            .IsUnicode(true)       // explicit nvarchar — ฿ ₹ ₱ are NOT representable in varchar
            .HasMaxLength(10);

        builder.Property(pp => pp.MonthlyPrice)
            .HasPrecision(10, 2);

        builder.Property(pp => pp.AnnualPrice)
            .HasPrecision(10, 2);

        builder.Property(pp => pp.CreatedBy)
            .HasMaxLength(100);

        // One row per (Plan, Currency) — nothing currently stops a duplicate
        // THB row being added for the same plan; this closes that gap.
        builder.HasIndex(pp => new { pp.PlanId, pp.CurrencyCode })
            .IsUnique();

        builder.HasIndex(pp => pp.PlanId);

        builder.HasOne(pp => pp.Plan)
            .WithMany(p => p.PlanPricings)
            .HasForeignKey(pp => pp.PlanId)
            .OnDelete(DeleteBehavior.Cascade);   // delete a Plan → its pricing rows go with it
    }
}
