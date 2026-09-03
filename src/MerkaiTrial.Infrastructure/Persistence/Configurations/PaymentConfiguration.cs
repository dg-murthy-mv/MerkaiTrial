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
    public class PaymentConfiguration : IEntityTypeConfiguration<Payment>
    {
        public void Configure(EntityTypeBuilder<Payment> builder)
        {
            builder.ToTable("Payments");

            builder.HasKey(x => x.Id);

            // Required fields
            builder.Property(x => x.TenantId)
                .IsRequired();

            builder.Property(x => x.InvoiceId)
                .IsRequired();

            builder.Property(x => x.Amount)
                .HasPrecision(18, 2)
                .IsRequired();

            builder.Property(x => x.Currency)
                .IsRequired()
                .HasMaxLength(8)
                .HasDefaultValue("USD");

            builder.Property(x => x.Method)
                .IsRequired()
                .HasMaxLength(32)
                .HasDefaultValue("BankTransfer");

            builder.Property(x => x.Status)
                .IsRequired()
                .HasMaxLength(32)
                .HasDefaultValue("Captured");

            builder.Property(x => x.PaidAtUtc)
                .IsRequired()
                .HasDefaultValueSql("SYSUTCDATETIME()");

            // Nullable fields
            builder.Property(x => x.ProviderTxnId)
                .HasMaxLength(128)
                .IsRequired(false);

            builder.Property(x => x.Notes)
                .HasMaxLength(500)
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

            // Indexes
            builder.HasIndex(x => x.TenantId)
                .HasFilter("[IsDeleted] = 0");

            builder.HasIndex(x => x.InvoiceId)
                .HasFilter("[IsDeleted] = 0");

            // Check constraints (will be added via migration)
            // CK_Payments_Amount_Positive
        }
    }
}
