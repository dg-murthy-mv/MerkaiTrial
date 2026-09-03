using MerkaiTrial.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerkaiTrial.Infrastructure.Persistence.Configurations
{
    public sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
    {
        public void Configure(EntityTypeBuilder<Tenant> b)
        {
            b.ToTable("Tenants", "dbo");
            b.HasKey(x => x.Id);
           
            // Core columns
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.Property(x => x.FromEmail).HasMaxLength(320).IsRequired();
            b.Property(x => x.Timezone).HasMaxLength(100);
            b.Property(x => x.Phone).HasMaxLength(50);
            b.Property(x => x.Domain).HasMaxLength(255);
            b.Property(x => x.ReplyToEmail).HasMaxLength(320);
            b.Property(x => x.PreferredLanguage).HasMaxLength(10);
            b.Property(x => x.Plan).HasMaxLength(32);

            // Currency is a 3-letter ISO code string (e.g., "THB")
            b.Property(x => x.DefaultCurrency)
             .HasMaxLength(3);

            //// Country FK + navigation (no enum conversion!)
            //b.HasOne(t => t.Country)
            // .WithMany()
            // .HasForeignKey(t => t.CountryId)
            // .OnDelete(DeleteBehavior.Restrict);

            // Legacy enum property on the entity: do NOT map it
            // (You also can decorate the property with [NotMapped] on the entity.)
            //b.Ignore(t => t.CountryCode);

            // Optional: if these columns do NOT exist in DB, keep them ignored.
            // If they DO exist, comment these out and map with proper .HasColumnName/.HasMaxLength.
            //b.Ignore(t => t.TaxProfileId);
            //b.Ignore(t => t.PaymentProviders);
            //b.Ignore(t => t.PublicLinkSecret);

            // Common indexes (tune as needed)
            b.HasIndex(x => x.Name).IsUnique(false);
            b.HasIndex(x => new { x.IsActive, x.CreatedAtUtc });
        }
    }
}
