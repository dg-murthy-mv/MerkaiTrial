using MerkaiTrial.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MerkaiTrial.Infrastructure.Persistence.Configurations
{
    public class DealActivityConfiguration : IEntityTypeConfiguration<DealActivity>
    {
        public void Configure(EntityTypeBuilder<DealActivity> builder)
        {
            builder.ToTable("DealActivities");

            builder.HasKey(a => a.Id);

            builder.Property(a => a.ActivityType)
                .IsRequired()
                .HasMaxLength(50);

            builder.Property(a => a.Subject)
                .HasMaxLength(200);

            builder.Property(a => a.CreatedBy)
                .HasMaxLength(100);

            builder.HasIndex(a => a.DealId);
            builder.HasIndex(a => a.TenantId);
        }
    }
}
