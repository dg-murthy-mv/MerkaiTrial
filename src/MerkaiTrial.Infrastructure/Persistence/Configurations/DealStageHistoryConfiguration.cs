using MerkaiTrial.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerkaiTrial.Infrastructure.Persistence.Configurations;

public class DealStageHistoryConfiguration : IEntityTypeConfiguration<DealStageHistory>
{
    public void Configure(EntityTypeBuilder<DealStageHistory> b)
    {
        b.ToTable("DealStageHistory");
        b.HasKey(x => x.Id);

        b.Property(x => x.TenantId).IsRequired();
        b.Property(x => x.DealId).IsRequired();
        b.Property(x => x.FromStage).IsRequired().HasMaxLength(50);
        b.Property(x => x.ToStage).IsRequired().HasMaxLength(50);
        b.Property(x => x.ChangedBy).HasMaxLength(100);
        b.Property(x => x.Note).HasMaxLength(500);

        // Indexes for funnel/velocity queries
        b.HasIndex(x => x.DealId);
        b.HasIndex(x => new { x.TenantId, x.ChangedAtUtc });
        b.HasIndex(x => new { x.TenantId, x.ToStage });
    }
}