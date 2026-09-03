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
    public class CompanyConfiguration : IEntityTypeConfiguration<Company>
    {
        public void Configure(EntityTypeBuilder<Company> builder)
        {
            builder.ToTable("Companies");
            builder.HasKey(c => c.Id);

            builder.Property(c => c.TenantId).IsRequired();
            builder.Property(c => c.Name).IsRequired().HasMaxLength(200);
            builder.Property(c => c.Country).IsRequired().HasMaxLength(5).HasDefaultValue("TH");
            builder.Property(c => c.TaxId).HasMaxLength(64);
            builder.Property(c => c.Vertical).IsRequired().HasMaxLength(32).HasDefaultValue("Generic");
            builder.Property(c => c.CreatedBy).HasMaxLength(64);
            builder.Property(c => c.UpdatedBy).HasMaxLength(64);

            builder.Property(c => c.IsDeleted).IsRequired().HasDefaultValue(false);
            builder.Property(c => c.CreatedAtUtc).IsRequired().HasDefaultValueSql("sysutcdatetime()");

            // Relationships
            builder.HasOne(c => c.Tenant)
                .WithMany()
                .HasForeignKey(c => c.TenantId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasMany(c => c.Contacts)
                .WithOne(ct => ct.Company)
                .HasForeignKey(ct => ct.CompanyId)
                .OnDelete(DeleteBehavior.Restrict);

            // Note: Deals are accessed through Contacts (Deal -> Contact -> Company)
            // No direct Company -> Deals relationship

            // Indexes
            builder.HasIndex(c => c.TenantId).HasFilter("[IsDeleted] = 0");
            builder.HasIndex(c => c.Name).HasFilter("[IsDeleted] = 0");
            builder.HasIndex(c => new { c.TenantId, c.Name }).HasFilter("[IsDeleted] = 0");

            // Check constraint for Vertical (matches your database)
            builder.HasCheckConstraint("CK_Companies_Vertical_Allowed",
                "[Vertical] IN ('Generic', 'Contracting', 'Manufacturing', 'Agency', 'Logistics', 'Finance')");
        }
    }
}
