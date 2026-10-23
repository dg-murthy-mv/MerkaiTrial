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

        // 101 — "this record's history": the lead/deal timeline asks for
        // one tenant's rows for ONE record (EntityType + EntityId), often of
        // one Action (status changes, merges). With only the index above,
        // SQL Server read through the tenant's whole audit trail — or the
        // whole table — for every timeline. Round 101's run waited 15.2 s on
        // it (PAGEIOLATCH_SH, reading from disk), and a lead page took over
        // 20 s to open. Created by Sql/101c_AuditLogs_EntityIndex.sql.
        b.HasIndex(a => new { a.TenantId, a.EntityType, a.EntityId, a.Action })
         .HasDatabaseName("IX_AuditLogs_Tenant_Entity")
         .IncludeProperties(a => new { a.CreatedAtUtc, a.By });

        // NO global query filter — see the exemption note in FlowDbContext.
        // Audit reads are SuperAdmin-only and legitimately cross-tenant;
        // the read endpoints scope by tenant explicitly.
    }
}
