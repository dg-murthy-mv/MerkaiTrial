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
    public class DealNoteConfiguration : IEntityTypeConfiguration<DealNote>
    {
        public void Configure(EntityTypeBuilder<DealNote> builder)
        {
            builder.ToTable("DealNotes");

            builder.HasKey(n => n.Id);

            builder.Property(n => n.Note)
                .IsRequired();

            builder.Property(n => n.CreatedBy)
                .HasMaxLength(100);

            builder.HasIndex(n => n.DealId);
            builder.HasIndex(n => n.TenantId);
        }
    }
}
