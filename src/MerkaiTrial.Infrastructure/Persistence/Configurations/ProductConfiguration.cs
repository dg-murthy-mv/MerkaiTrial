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
    public class ProductConfiguration : IEntityTypeConfiguration<Product>
    {
        public void Configure(EntityTypeBuilder<Product> builder)
        {
            builder.ToTable("Products");

            builder.HasKey(p => p.Id);

            // Properties
            builder.Property(p => p.Name)
                .IsRequired()
                .HasMaxLength(200);

            builder.Property(p => p.Description)
                .HasMaxLength(4000);

            builder.Property(p => p.Sku)
                .HasMaxLength(50);

            builder.Property(p => p.Category)
                .HasMaxLength(100);

            builder.Property(p => p.Type)
                .IsRequired()
                .HasMaxLength(50)
                .HasDefaultValue("Product");

            builder.Property(p => p.ListPrice)
                .HasPrecision(18, 2)
                .IsRequired();

            //builder.Property(p => p.Currency)
            //    .IsRequired()
            //    .HasMaxLength(3)
            //    .HasDefaultValue("USD");

            builder.Property(p => p.TaxRate)
                .HasPrecision(5, 2);

            builder.Property(p => p.IsActive)
                .IsRequired()
                .HasDefaultValue(true);

            // Audit fields
            builder.Property(p => p.CreatedBy).HasMaxLength(100);
            builder.Property(p => p.UpdatedBy).HasMaxLength(100);
            builder.Property(p => p.DeletedBy).HasMaxLength(100);
            

            // Indexes
            builder.HasIndex(p => p.TenantId)
                .HasFilter("IsDeleted = 0");

            builder.HasIndex(p => new { p.TenantId, p.IsActive })
                .HasFilter("IsDeleted = 0");

            builder.HasIndex(p => p.Category)
                .HasFilter("IsDeleted = 0");

            builder.HasIndex(p => new { p.Sku, p.TenantId });
        }
    }
}
