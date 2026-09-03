// =====================================================================
// LEAD CHANNEL CONFIGURATION
// Location: MerkaiTrial.Infrastructure/Persistence/Configurations/LeadChannelConfiguration.cs
// =====================================================================

using MerkaiTrial.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerkaiTrial.Infrastructure.Persistence.Configurations
{
    public class LeadChannelConfiguration : IEntityTypeConfiguration<LeadChannel>
    {
        public void Configure(EntityTypeBuilder<LeadChannel> builder)
        {
            builder.ToTable("LeadChannels");

            builder.HasKey(lc => lc.Id);

            builder.Property(lc => lc.Id)
                .HasDefaultValueSql("NEWID()");

            builder.Property(lc => lc.TenantId)
                .IsRequired();

            builder.Property(lc => lc.Name)
                .IsRequired()
                .HasMaxLength(100);

            builder.Property(lc => lc.IsActive)
                .IsRequired()
                .HasDefaultValue(true);

            builder.Property(lc => lc.CreatedAtUtc)
                .IsRequired()
                .HasDefaultValueSql("SYSUTCDATETIME()");

            builder.Property(lc => lc.CreatedBy)
                .HasMaxLength(64);

            builder.Property(lc => lc.IsDeleted)
                .IsRequired()
                .HasDefaultValue(false);

            // Indexes
            builder.HasIndex(lc => new { lc.TenantId, lc.Name })
                .IsUnique()
                .HasDatabaseName("UQ_LeadChannels_TenantName");

            builder.HasIndex(lc => lc.TenantId)
                .HasDatabaseName("IX_LeadChannels_TenantId");

            //// Relationships
            //builder.HasMany(lc => lc.Leads)
            //    .WithOne(l => l.LeadChannel)
            //    .HasForeignKey(l => l.ChannelId)
            //    .OnDelete(DeleteBehavior.Restrict);
        }
    }
}