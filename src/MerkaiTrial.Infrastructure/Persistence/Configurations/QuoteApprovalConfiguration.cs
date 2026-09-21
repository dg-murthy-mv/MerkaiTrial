// =====================================================================
// QuoteApprovalConfiguration.cs
// Location: MerkaiTrial.Infrastructure/Persistence/Configurations/
//
// NEW FILE (017). Picked up automatically by ApplyConfigurationsFromAssembly.
// Column sizes match 017_QuoteApprovals.sql exactly.
// =====================================================================

using MerkaiTrial.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerkaiTrial.Infrastructure.Persistence.Configurations;

public class QuoteApprovalSettingsConfiguration : IEntityTypeConfiguration<QuoteApprovalSettings>
{
    public void Configure(EntityTypeBuilder<QuoteApprovalSettings> b)
    {
        b.ToTable("QuoteApprovalSettings");
        b.HasKey(s => s.Id);

        b.Property(s => s.MaxDiscountPercent).HasPrecision(5, 2);
        b.Property(s => s.MaxQuoteTotal).HasPrecision(18, 2);
        b.Property(s => s.UpdatedBy).HasMaxLength(200);

        b.HasIndex(s => s.TenantId)
         .IsUnique()
         .HasDatabaseName("UX_QuoteApprovalSettings_Tenant");
    }
}

public class QuoteApprovalRequestConfiguration : IEntityTypeConfiguration<QuoteApprovalRequest>
{
    public void Configure(EntityTypeBuilder<QuoteApprovalRequest> b)
    {
        b.ToTable("QuoteApprovalRequests");
        b.HasKey(r => r.Id);

        b.Property(r => r.Status).HasMaxLength(30).IsRequired();
        b.Property(r => r.RequestedByName).HasMaxLength(200).IsRequired();
        b.Property(r => r.RequestComment).HasMaxLength(1000);
        b.Property(r => r.Reasons).HasMaxLength(2000).IsRequired();
        b.Property(r => r.QuoteTotal).HasPrecision(18, 2);
        b.Property(r => r.MaxLineDiscountPercent).HasPrecision(7, 2);
        b.Property(r => r.Currency).HasMaxLength(10).IsRequired();
        b.Property(r => r.DecidedByName).HasMaxLength(200);
        b.Property(r => r.DecisionComment).HasMaxLength(1000);

        b.HasOne(r => r.Quote)
         .WithMany()
         .HasForeignKey(r => r.QuoteId)
         .OnDelete(DeleteBehavior.NoAction);

        b.HasIndex(r => new { r.TenantId, r.Status })
         .HasDatabaseName("IX_QuoteApprovalRequests_Tenant_Status");

        b.HasIndex(r => r.QuoteId)
         .HasDatabaseName("IX_QuoteApprovalRequests_Quote");
    }
}
