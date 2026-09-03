// =====================================================================
// AttachmentConfiguration.cs
// Location: MerkaiTrial.Infrastructure/Persistence/Configurations/AttachmentConfiguration.cs
// =====================================================================

using MerkaiTrial.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerkaiTrial.Infrastructure.Persistence.Configurations;

public class AttachmentConfiguration : IEntityTypeConfiguration<Attachment>
{
    public void Configure(EntityTypeBuilder<Attachment> entity)
    {
        entity.ToTable("Attachments");
        entity.HasKey(a => a.Id);

        entity.Property(a => a.EntityType).HasMaxLength(50).IsRequired();
        entity.Property(a => a.FileName).HasMaxLength(255).IsRequired();
        entity.Property(a => a.FileUrl).HasMaxLength(1000).IsRequired();
        entity.Property(a => a.MimeType).HasMaxLength(100);
        entity.Property(a => a.CreatedBy).HasMaxLength(100);

        // Index for fast lookup by entity — used on every detail page load
        entity.HasIndex(a => new { a.TenantId, a.EntityType, a.EntityId });
    }
}
