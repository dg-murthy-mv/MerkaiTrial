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
    public class DealReminderConfiguration : IEntityTypeConfiguration<DealReminder>
    {
        public void Configure(EntityTypeBuilder<DealReminder> builder)
        {
            builder.ToTable("DealReminders");

            builder.HasKey(r => r.Id);

            builder.Property(r => r.Title)
                .IsRequired()
                .HasMaxLength(200);

            builder.Property(r => r.CreatedBy)
                .HasMaxLength(100);

            builder.HasIndex(r => r.DealId);
            builder.HasIndex(r => r.TenantId);
            builder.HasIndex(r => r.ReminderDate);
        }
    }
}
