using MerkaiTrial.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerkaiTrial.Infrastructure.Persistence.Configurations;

public class RoleTemplateConfiguration : IEntityTypeConfiguration<RoleTemplate>
{
    public void Configure(EntityTypeBuilder<RoleTemplate> b)
    {
        b.ToTable("RoleTemplates");
        b.HasKey(t => t.Id);

        b.Property(t => t.Name).HasMaxLength(100).IsRequired();
        b.Property(t => t.DisplayName).HasMaxLength(200).IsRequired();
        b.Property(t => t.Description).HasMaxLength(500);
        b.Property(t => t.CreatedBy).HasMaxLength(100);
        b.Property(t => t.UpdatedBy).HasMaxLength(100);

        b.HasIndex(t => t.Name).IsUnique().HasDatabaseName("UX_RoleTemplates_Name");

        // No query filter — global reference data.
    }
}