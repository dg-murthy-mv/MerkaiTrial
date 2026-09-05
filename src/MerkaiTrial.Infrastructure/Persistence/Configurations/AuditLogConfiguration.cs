using MerkaiTrial.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerkaiTrial.Infrastructure.Persistence.Configurations;

public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> b)
    {
        b.ToTable("AuditLogs");
        b.HasKey(a => a.Id);

        b.Property(a => a.EntityType).HasMaxLength(128).IsRequired();
        b.Property(a => a.Action).HasMaxLength(32).IsRequired();
        b.Property(a => a.By).HasMaxLength(64).IsRequired();
        b.Property(a => a.CreatedBy).HasMaxLength(64);
        b.Property(a => a.UpdatedBy).HasMaxLength(64);
        b.Property(a => a.IpAddress).HasMaxLength(64);

        b.HasIndex(a => new { a.TenantId, a.CreatedAtUtc })
         .HasDatabaseName("IX_AuditLogs_Tenant_CreatedAt");

        // NO global query filter — see the exemption note in FlowDbContext.
        // Audit reads are SuperAdmin-only and legitimately cross-tenant;
        // the read endpoints scope by tenant explicitly.
    }
}
