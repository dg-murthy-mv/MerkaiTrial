// =====================================================================
// InvoiceConfiguration.cs
// Location: MerkaiTrial.Infrastructure/Persistence/Configurations/
//
// 085 — PublicLinkToken (nvarchar(64), unique where set: a token names
// exactly one invoice) and InvoiceSentCopy, the PDF as last emailed.
// Columns and table: Sql/085_OutboundTracking.sql.
// =====================================================================

using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerkaiTrial.Infrastructure.Persistence.Configurations
{
    public class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
    {
        public void Configure(EntityTypeBuilder<Invoice> builder)
        {
            builder.ToTable("Invoices");

            builder.HasKey(x => x.Id);

            // Required fields
            builder.Property(x => x.TenantId)
                .IsRequired();

            builder.Property(x => x.Number)
                .IsRequired()
                .HasMaxLength(40);

            builder.Property(x => x.Currency)
                .IsRequired()
                .HasMaxLength(8)
                .HasDefaultValue("USD");

            // Status as int
            builder.Property(x => x.Status)
                .IsRequired()
                .HasConversion<int>()
                .HasDefaultValue(InvoiceStatus.Draft);

            // Decimal fields with precision
            builder.Property(x => x.Subtotal)
                .HasPrecision(18, 2)
                .HasDefaultValue(0m);

            builder.Property(x => x.DiscountTotal)
                .HasPrecision(18, 2)
                .HasDefaultValue(0m);

            builder.Property(x => x.TaxTotal)
                .HasPrecision(18, 2)
                .HasDefaultValue(0m);

            builder.Property(x => x.Total)
                .HasPrecision(18, 2)
                .HasDefaultValue(0m);

            builder.Property(x => x.Balance)
                .HasPrecision(18, 2)
                .HasDefaultValue(0m);

            builder.Property(x => x.Amount)
                .HasPrecision(18, 2)
                .HasDefaultValue(0m);

            // Nullable fields
            builder.Property(x => x.QuoteId)
                .IsRequired(false);

            builder.Property(x => x.DealId)
                .IsRequired(false);

            builder.Property(x => x.DueDateUtc)
                .IsRequired(false);

            builder.Property(x => x.PromptPayQrImageUrl)
                .HasMaxLength(512)
                .IsRequired(false);

            builder.Property(x => x.GatewayRef)
                .HasMaxLength(128)
                .IsRequired(false);

            builder.Property(x => x.Provider)
                .HasMaxLength(64)
                .IsRequired(false);

            builder.Property(x => x.ProviderRef)
                .HasMaxLength(128)
                .IsRequired(false);

            builder.Property(x => x.CheckoutUrl)
                .HasMaxLength(512)
                .IsRequired(false);

            builder.Property(x => x.PdfUrl)
                .HasMaxLength(512)
                .IsRequired(false);

            builder.Property(x => x.Notes)
                .HasMaxLength(1000)
                .IsRequired(false);

            // 085 — the customer's link.
            builder.Property(x => x.PublicLinkToken)
                .HasMaxLength(64)
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
            builder.HasOne(x => x.Quote)
                .WithMany()
                .HasForeignKey(x => x.QuoteId)
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired(false);

            builder.HasOne(x => x.Deal)
                .WithMany()
                .HasForeignKey(x => x.DealId)
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired(false);

            builder.HasMany(x => x.Lines)
                .WithOne(x => x.Invoice)
                .HasForeignKey(x => x.InvoiceId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasMany(x => x.Payments)
                .WithOne(x => x.Invoice)
                .HasForeignKey(x => x.InvoiceId)
                .OnDelete(DeleteBehavior.Cascade);

            // Indexes
            builder.HasIndex(x => x.TenantId)
                .HasFilter("[IsDeleted] = 0");

            builder.HasIndex(x => x.QuoteId)
                .HasFilter("[IsDeleted] = 0 AND [QuoteId] IS NOT NULL");

            builder.HasIndex(x => x.DealId)
                .HasFilter("[IsDeleted] = 0 AND [DealId] IS NOT NULL");

            builder.HasIndex(x => new { x.Number, x.TenantId })
                .HasFilter("[IsDeleted] = 0");

            builder.HasIndex(x => new { x.Status, x.TenantId })
                .HasFilter("[IsDeleted] = 0");

            builder.HasIndex(x => new { x.DueDateUtc, x.Status })
                .HasFilter("[IsDeleted] = 0 AND [DueDateUtc] IS NOT NULL");

            // 085. One invoice per token, looked up by the public page.
            builder.HasIndex(x => x.PublicLinkToken)
                .IsUnique()
                .HasDatabaseName("UX_Invoices_PublicLinkToken")
                .HasFilter("[PublicLinkToken] IS NOT NULL");

            // Ignore calculated properties
            builder.Ignore(x => x.TotalPaid);
            builder.Ignore(x => x.IsFullyPaid);
            builder.Ignore(x => x.IsOverdue);
        }
    }

    /// <summary>
    /// 085. The PDF as last emailed — one row per invoice, keyed by it.
    /// No navigation from Invoice on purpose: nothing that reads an
    /// invoice should be able to drag the bytes along by accident.
    /// </summary>
    public class InvoiceSentCopyConfiguration : IEntityTypeConfiguration<InvoiceSentCopy>
    {
        public void Configure(EntityTypeBuilder<InvoiceSentCopy> builder)
        {
            builder.ToTable("InvoiceSentCopies");
            builder.HasKey(x => x.InvoiceId);
            builder.Property(x => x.InvoiceId).ValueGeneratedNever();

            builder.Property(x => x.TenantId).IsRequired();
            builder.Property(x => x.FileName).IsRequired().HasMaxLength(255);
            builder.Property(x => x.PdfContent).IsRequired();   // varbinary(max)
            builder.Property(x => x.CreatedBy).HasMaxLength(200);

            builder.HasOne<Invoice>()
                .WithMany()
                .HasForeignKey(x => x.InvoiceId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
