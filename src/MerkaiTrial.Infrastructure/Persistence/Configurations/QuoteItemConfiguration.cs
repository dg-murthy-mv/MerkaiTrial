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
    public class QuoteItemConfiguration : IEntityTypeConfiguration<QuoteItem>
    {
        public void Configure(EntityTypeBuilder<QuoteItem> builder)
        {
            builder.ToTable("QuoteItems");

            builder.HasKey(qi => qi.Id);

            // Properties
            builder.Property(qi => qi.Name)
                .HasMaxLength(200);

            builder.Property(qi => qi.Description)
                .IsRequired()
                .HasMaxLength(400);

            builder.Property(qi => qi.UnitPrice)
                .HasPrecision(18, 2)
                .IsRequired();

            builder.Property(qi => qi.Quantity)
                .IsRequired();

            builder.Property(qi => qi.LineDiscount)
                .HasPrecision(18, 2)
                .HasDefaultValue(0);

            builder.Property(qi => qi.TaxRate)
                .HasPrecision(9, 4)
                .HasDefaultValue(0);

            // Audit fields
            builder.Property(qi => qi.CreatedBy).HasMaxLength(64);
            builder.Property(qi => qi.UpdatedBy).HasMaxLength(64);

            builder.Property(qi => qi.IsDeleted)
                .IsRequired()
                .HasDefaultValue(false);

            // Relationships
            builder.HasOne(qi => qi.Product)
                .WithMany()
                .HasForeignKey(qi => qi.ProductId)
                .OnDelete(DeleteBehavior.Restrict);

            // Indexes
            builder.HasIndex(qi => qi.TenantId)
                .HasFilter("IsDeleted = 0");

            builder.HasIndex(qi => qi.QuoteId)
                .HasFilter("IsDeleted = 0");

            builder.HasIndex(qi => qi.ProductId)
                .HasFilter("IsDeleted = 0 AND ProductId IS NOT NULL");

            // Check constraints
            builder.HasCheckConstraint("CK_QuoteItems_Qty_Positive", "[Quantity] > 0");
            builder.HasCheckConstraint("CK_QuoteItems_Prices_NonNeg",
                "[UnitPrice] >= 0 AND [LineDiscount] >= 0 AND [TaxRate] >= 0 AND [TaxRate] <= 1");

            // Ignore calculated properties
            builder.Ignore(qi => qi.LineTotal);
            builder.Ignore(qi => qi.LineTax);
            builder.Ignore(qi => qi.LineGrandTotal);
        }
    }
}
