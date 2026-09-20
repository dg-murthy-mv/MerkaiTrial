// =====================================================================
// RecordVisibilityConfiguration.cs
// Location: MerkaiTrial.Infrastructure/Persistence/Configurations/
//
// Three configurations in one file — Teams, RoleRecordScopes, TeamManagers.
// Picked up automatically by ApplyConfigurationsFromAssembly.
// =====================================================================

using MerkaiTrial.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerkaiTrial.Infrastructure.Persistence.Configurations;

public class TeamConfiguration : IEntityTypeConfiguration<Team>
{
    public void Configure(EntityTypeBuilder<Team> b)
    {
        b.ToTable("Teams");
        b.HasKey(t => t.Id);

        b.Property(t => t.Name).HasMaxLength(100).IsRequired();
        b.Property(t => t.Description).HasMaxLength(500);
        b.Property(t => t.CreatedBy).HasMaxLength(200);
        b.Property(t => t.UpdatedBy).HasMaxLength(200);

        b.HasIndex(t => new { t.TenantId, t.Name })
         .IsUnique()
         .HasDatabaseName("UX_Teams_Tenant_Name");
    }
}

public class RoleRecordScopeConfiguration : IEntityTypeConfiguration<RoleRecordScope>
{
    public void Configure(EntityTypeBuilder<RoleRecordScope> b)
    {
        b.ToTable("RoleRecordScopes");
        b.HasKey(s => s.Id);

        b.Property(s => s.Module).HasMaxLength(50).IsRequired();
        b.Property(s => s.Scope).HasConversion<int>();
        b.Property(s => s.UpdatedBy).HasMaxLength(200);

        b.HasIndex(s => new { s.TenantId, s.RoleId, s.Module })
         .IsUnique()
         .HasDatabaseName("UX_RoleRecordScopes_Tenant_Role_Module");
    }
}

public class TeamManagerConfiguration : IEntityTypeConfiguration<TeamManager>
{
    public void Configure(EntityTypeBuilder<TeamManager> b)
    {
        b.ToTable("TeamManagers");
        b.HasKey(m => m.Id);
        b.Property(m => m.CreatedBy).HasMaxLength(200);

        b.HasIndex(m => new { m.TenantId, m.TeamId, m.UserId })
         .IsUnique()
         .HasDatabaseName("UX_TeamManagers_Tenant_Team_User");

        b.HasIndex(m => new { m.TenantId, m.UserId })
         .HasDatabaseName("IX_TeamManagers_Tenant_User");
    }
}
