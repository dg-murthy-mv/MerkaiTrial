// =====================================================================
// TenantNotificationDefaultConfiguration.cs
// Location: MerkaiTrial.Infrastructure/Persistence/Configurations/TenantNotificationDefaultConfiguration.cs
//
// NEW FILE (040). A separate file rather than an addition to
// OutboundMessageConfiguration.cs, so that file stays exactly as you
// applied it in 038.
// =====================================================================

using MerkaiTrial.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerkaiTrial.Infrastructure.Persistence.Configurations
{
    public class TenantNotificationDefaultConfiguration
        : IEntityTypeConfiguration<TenantNotificationDefault>
    {
        public void Configure(EntityTypeBuilder<TenantNotificationDefault> builder)
        {
            builder.ToTable("TenantNotificationDefaults");

            builder.HasKey(d => d.Id);

            builder.Property(d => d.TenantId).IsRequired();
            builder.Property(d => d.EventType).HasConversion<int>().IsRequired();
            builder.Property(d => d.UpdatedBy).HasMaxLength(200);

            // One row per tenant per event, enforced rather than trusted.
            // Two rows would make "what does this workspace want" ambiguous,
            // and the resolution in NotificationResolution would silently
            // depend on which one EF happened to return first.
            builder.HasIndex(d => new { d.TenantId, d.EventType })
                   .IsUnique()
                   .HasDatabaseName("UX_TenantNotificationDefaults_Tenant_Event");
        }
    }
}
