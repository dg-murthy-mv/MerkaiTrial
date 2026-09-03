using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerkaiTrial.Infrastructure.Persistence.Configurations
{
    public class QuoteConfiguration : IEntityTypeConfiguration<Quote>
    {
        public void Configure(EntityTypeBuilder<Quote> builder)
        {
            builder.ToTable("Quotes");

            builder.HasKey(q => q.Id);

            // Properties
            builder.Property(q => q.Number)
                .IsRequired()
                .HasMaxLength(40);

            builder.Property(q => q.Currency)
                .IsRequired()
                .HasMaxLength(8)
                .HasDefaultValue("USD");

            builder.Property(q => q.Status)
                .IsRequired()
                .HasConversion<int>()
                .HasDefaultValue(QuoteStatus.Draft);

            builder.Property(q => q.Subtotal)
                .HasPrecision(18, 2)
                .HasDefaultValue(0);

            builder.Property(q => q.DiscountTotal)
                .HasPrecision(18, 2)
                .HasDefaultValue(0);

            builder.Property(q => q.TaxTotal)
                .HasPrecision(18, 2)
                .HasDefaultValue(0);

            builder.Property(q => q.GrandTotal)
                .HasPrecision(18, 2)
                .HasDefaultValue(0);

            builder.Property(q => q.PaymentLinkUrl)
                .HasMaxLength(512);

            builder.Property(q => q.PdfUrl)
                .HasMaxLength(512);

            // Audit fields
            builder.Property(q => q.CreatedBy).HasMaxLength(64);
            builder.Property(q => q.UpdatedBy).HasMaxLength(64);

            builder.Property(q => q.IsDeleted)
                .IsRequired()
                .HasDefaultValue(false);

            // Relationships
            builder.HasOne(q => q.Deal)
                .WithMany()
                .HasForeignKey(q => q.DealId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasMany(q => q.Items)
                .WithOne(qi => qi.Quote)
                .HasForeignKey(qi => qi.QuoteId)
                .OnDelete(DeleteBehavior.Cascade);

            // Indexes
            builder.HasIndex(q => q.TenantId)
                .HasFilter("IsDeleted = 0");

            builder.HasIndex(q => q.DealId)
                .HasFilter("IsDeleted = 0");

            builder.HasIndex(q => new { q.Status, q.TenantId })
                .HasFilter("IsDeleted = 0");

            builder.HasIndex(q => new { q.Number, q.TenantId });
        }
    }
}
