using MerkaiTrial.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerkaiTrial.Infrastructure.Persistence.Configurations
{
    public class ContactConfiguration : IEntityTypeConfiguration<Contact>
    {
        public void Configure(EntityTypeBuilder<Contact> builder)
        {
            builder.ToTable("Contacts");
            builder.HasKey(c => c.Id);

            builder.Property(c => c.TenantId).IsRequired();
            builder.Property(c => c.FirstName).IsRequired().HasMaxLength(100);
            builder.Property(c => c.LastName).HasMaxLength(100);
            builder.Property(c => c.JobTitle).HasMaxLength(100);
            builder.Property(c => c.Email).HasMaxLength(320);
            builder.Property(c => c.Phone).HasMaxLength(32);
            builder.Property(c => c.Mobile).HasMaxLength(32);
            builder.Property(c => c.Address).HasMaxLength(500);
            builder.Property(c => c.City).HasMaxLength(100);
            builder.Property(c => c.Country).HasMaxLength(100);
            builder.Property(c => c.PostalCode).HasMaxLength(20);
            builder.Property(c => c.LineUserId).HasMaxLength(64);
            builder.Property(c => c.CreatedBy).HasMaxLength(64);
            builder.Property(c => c.UpdatedBy).HasMaxLength(64);

            builder.Property(c => c.IsPrimary).IsRequired().HasDefaultValue(false);
            builder.Property(c => c.IsDeleted).IsRequired().HasDefaultValue(false);
            builder.Property(c => c.CreatedAtUtc).IsRequired();

            builder.HasOne(c => c.Company)
                .WithMany(co => co.Contacts)
                .HasForeignKey(c => c.CompanyId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasMany(c => c.Deals)
                .WithOne(d => d.Contact)
                .HasForeignKey(d => d.ContactId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasIndex(c => c.TenantId).HasFilter("[IsDeleted] = 0");
            builder.HasIndex(c => c.CompanyId).HasFilter("[IsDeleted] = 0");
            builder.HasIndex(c => c.Email).HasFilter("[IsDeleted] = 0 AND [Email] IS NOT NULL");
            builder.HasIndex(c => new { c.TenantId, c.CompanyId }).HasFilter("[IsDeleted] = 0");

            builder.Ignore(c => c.FullName);
            builder.Ignore(c => c.DisplayName);
        }
    }
}
