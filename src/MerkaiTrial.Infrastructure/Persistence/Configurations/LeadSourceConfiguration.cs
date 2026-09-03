// =====================================================================
// LEAD SOURCE CONFIGURATION
// Location: MerkaiTrial.Infrastructure/Persistence/Configurations/LeadSourceConfiguration.cs
// =====================================================================

using MerkaiTrial.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerkaiTrial.Infrastructure.Persistence.Configurations
{
    public class LeadSourceConfiguration : IEntityTypeConfiguration<LeadSource>
    {
        public void Configure(EntityTypeBuilder<LeadSource> builder)
        {
            builder.ToTable("LeadSources");

            builder.HasKey(ls => ls.Id);

            builder.Property(ls => ls.Id)
                .HasDefaultValueSql("NEWID()");

            builder.Property(ls => ls.TenantId)
                .IsRequired();

            builder.Property(ls => ls.Name)
                .IsRequired()
                .HasMaxLength(100);

            builder.Property(ls => ls.IsActive)
                .IsRequired()
                .HasDefaultValue(true);

            builder.Property(ls => ls.CreatedAtUtc)
                .IsRequired()
                .HasDefaultValueSql("SYSUTCDATETIME()");

            builder.Property(ls => ls.CreatedBy)
                .HasMaxLength(64);

            builder.Property(ls => ls.IsDeleted)
                .IsRequired()
                .HasDefaultValue(false);

            // Indexes
            builder.HasIndex(ls => new { ls.TenantId, ls.Name })
                .IsUnique()
                .HasDatabaseName("UQ_LeadSources_TenantName");

            builder.HasIndex(ls => ls.TenantId)
                .HasDatabaseName("IX_LeadSources_TenantId");

            // Relationships
            //builder.HasMany(ls => ls.Leads)
            //    .WithOne(l => l.LeadSource)
            //    .HasForeignKey(l => l.SourceId)
            //    .OnDelete(DeleteBehavior.Restrict);
        }
    }
}