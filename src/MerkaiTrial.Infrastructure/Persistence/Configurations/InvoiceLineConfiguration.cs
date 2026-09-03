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
    public class InvoiceLineConfiguration : IEntityTypeConfiguration<InvoiceLine>
    {
        public void Configure(EntityTypeBuilder<InvoiceLine> builder)
        {
            builder.ToTable("InvoiceLines");

            builder.HasKey(x => x.Id);

            // Required fields
            builder.Property(x => x.TenantId)
                .IsRequired();

            builder.Property(x => x.InvoiceId)
                .IsRequired();

            builder.Property(x => x.Name)
                .IsRequired()
                .HasMaxLength(200);

            builder.Property(x => x.Description)
                .HasMaxLength(400)
                .IsRequired(false);

            // Decimal fields with precision
            builder.Property(x => x.UnitPrice)
                .HasPrecision(18, 2)
                .IsRequired();

            builder.Property(x => x.Quantity)
                .IsRequired();

            builder.Property(x => x.LineDiscount)
                .HasPrecision(18, 2)
                .HasDefaultValue(0m);

            builder.Property(x => x.TaxRate)
                .HasPrecision(9, 4)
                .HasDefaultValue(0m);

            builder.Property(x => x.Amount)
                .HasPrecision(18, 2)
                .HasDefaultValue(0m);

            // Nullable fields
            builder.Property(x => x.ProductId)
                .IsRequired(false);

            // Audit fields
            builder.Property(x => x.CreatedAtUtc)
                .IsRequired()
                .HasDefaultValueSql("SYSUTCDATETIME()");

            builder.Property(x => x.CreatedBy)
                .HasMaxLength(64)
                .IsRequired(false);

            builder.Property(x => x.UpdatedAtUtc)
                .IsRequired(false);

            builder.Property(x => x.UpdatedBy)
                .HasMaxLength(64)
                .IsRequired(false);

            builder.Property(x => x.IsDeleted)
                .IsRequired()
                .HasDefaultValue(false);

            // Relationships
            builder.HasOne(x => x.Product)
                .WithMany()
                .HasForeignKey(x => x.ProductId)
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired(false);

            // Indexes
            builder.HasIndex(x => x.TenantId)
                .HasFilter("[IsDeleted] = 0");

            builder.HasIndex(x => x.InvoiceId)
                .HasFilter("[IsDeleted] = 0");

            builder.HasIndex(x => x.ProductId)
                .HasFilter("[IsDeleted] = 0 AND [ProductId] IS NOT NULL");

            // Check constraints (will be added via migration)
            // CK_InvoiceLines_Qty_Positive
            // CK_InvoiceLines_Prices_NonNeg

            // Ignore calculated properties
            builder.Ignore(x => x.LineTotal);
            builder.Ignore(x => x.LineTax);
            builder.Ignore(x => x.LineGrandTotal);
        }
    }
}
