// =====================================================================
// FILE: Persistence/Configurations/AiAssistantConfiguration.cs
//
// NEW (122). AiSettings and AiUsage — see Domain/Entities/AiAssistant.cs.
// Both are tenant data: FlowDbContext gives them the tenant query filter.
// Created by Sql/122_AiAssistant.sql.
// =====================================================================

using MerkaiTrial.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerkaiTrial.Infrastructure.Persistence.Configurations;

public class AiSettingConfiguration : IEntityTypeConfiguration<AiSetting>
{
    public void Configure(EntityTypeBuilder<AiSetting> b)
    {
        b.ToTable("AiSettings");
        b.HasKey(s => s.TenantId);
        b.Property(s => s.TenantId).ValueGeneratedNever();
        b.Property(s => s.ChangedBy).HasMaxLength(256);
    }
}

public class AiUsageConfiguration : IEntityTypeConfiguration<AiUsage>
{
    public void Configure(EntityTypeBuilder<AiUsage> b)
    {
        b.ToTable("AiUsage");
        b.HasKey(u => u.Id);
        b.Property(u => u.Provider).HasMaxLength(32).IsRequired();
        b.Property(u => u.Model).HasMaxLength(100);
        b.Property(u => u.Outcome).HasMaxLength(16).IsRequired();
        b.HasIndex(u => new { u.TenantId, u.CreatedAtUtc }).HasDatabaseName("IX_AiUsage_Tenant_Created");
        b.HasIndex(u => new { u.UserId, u.CreatedAtUtc }).HasDatabaseName("IX_AiUsage_User_Created");
    }
}

/// <summary>125. Changes the assistant prepared, waiting for the person to confirm.</summary>
public class AiProposalConfiguration : IEntityTypeConfiguration<AiProposal>
{
    public void Configure(EntityTypeBuilder<AiProposal> b)
    {
        b.ToTable("AiProposals");
        b.HasKey(p => p.Id);
        b.Property(p => p.Id).ValueGeneratedNever();
        b.Property(p => p.Kind).HasMaxLength(16).IsRequired();
        b.Property(p => p.RecordType).HasMaxLength(16).IsRequired();
        b.Property(p => p.Summary).HasMaxLength(500).IsRequired();
        b.Property(p => p.Status).HasMaxLength(16).IsRequired();
        b.HasIndex(p => new { p.TenantId, p.UserId, p.CreatedAtUtc }).HasDatabaseName("IX_AiProposals_Tenant_User_Created");
    }
}
