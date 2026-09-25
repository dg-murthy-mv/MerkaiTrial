// =====================================================================
// QuoteApprovalConfiguration.cs
// Location: MerkaiTrial.Infrastructure/Persistence/Configurations/
//
// COMPLETE FILE — replaces the 017 version.
//
// Picked up automatically by ApplyConfigurationsFromAssembly. Column
// sizes match 017_QuoteApprovals.sql and 027_ApprovalChains.sql exactly.
//
// 027 adds ApprovalRule, ApprovalStep and QuoteApprovalDecision, and the
// four chain columns on QuoteApprovalRequest.
//
// TENANT FILTERS: every entity here carries TenantId, so FlowDbContext's
// AssertEveryTenantEntityIsCovered will fail at startup unless the three
// new ones are added to the global filter set alongside the existing
// quote-approval entities. SETUP.md has the exact lines.
// =====================================================================

using MerkaiTrial.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerkaiTrial.Infrastructure.Persistence.Configurations;

public class QuoteApprovalSettingsConfiguration : IEntityTypeConfiguration<QuoteApprovalSettings>
{
    public void Configure(EntityTypeBuilder<QuoteApprovalSettings> b)
    {
        b.ToTable("QuoteApprovalSettings");
        b.HasKey(s => s.Id);

        b.Property(s => s.MaxDiscountPercent).HasPrecision(5, 2);
        b.Property(s => s.MaxQuoteTotal).HasPrecision(18, 2);
        b.Property(s => s.UpdatedBy).HasMaxLength(200);

        b.HasIndex(s => s.TenantId)
         .IsUnique()
         .HasDatabaseName("UX_QuoteApprovalSettings_Tenant");
    }
}

public class QuoteApprovalRequestConfiguration : IEntityTypeConfiguration<QuoteApprovalRequest>
{
    public void Configure(EntityTypeBuilder<QuoteApprovalRequest> b)
    {
        b.ToTable("QuoteApprovalRequests");
        b.HasKey(r => r.Id);

        b.Property(r => r.Status).HasMaxLength(30).IsRequired();
        b.Property(r => r.RequestedByName).HasMaxLength(200).IsRequired();
        b.Property(r => r.RequestComment).HasMaxLength(1000);
        b.Property(r => r.Reasons).HasMaxLength(2000).IsRequired();
        b.Property(r => r.QuoteTotal).HasPrecision(18, 2);
        b.Property(r => r.MaxLineDiscountPercent).HasPrecision(7, 2);
        b.Property(r => r.Currency).HasMaxLength(10).IsRequired();
        b.Property(r => r.DecidedByName).HasMaxLength(200);
        b.Property(r => r.DecisionComment).HasMaxLength(1000);

        // ── 027 ──
        b.Property(r => r.RuleName).HasMaxLength(150);
        b.Property(r => r.CurrentStepOrder).HasDefaultValue(1);
        b.Property(r => r.TotalSteps).HasDefaultValue(1);

        // Computed properties, not columns.
        b.Ignore(r => r.StepLabel);
        b.Ignore(r => r.IsFinalStep);

        b.HasOne(r => r.Quote)
         .WithMany()
         .HasForeignKey(r => r.QuoteId)
         .OnDelete(DeleteBehavior.NoAction);

        b.HasMany(r => r.Decisions)
         .WithOne(d => d.Request)
         .HasForeignKey(d => d.RequestId)
         .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(r => new { r.TenantId, r.Status })
         .HasDatabaseName("IX_QuoteApprovalRequests_Tenant_Status");

        b.HasIndex(r => r.QuoteId)
         .HasDatabaseName("IX_QuoteApprovalRequests_Quote");
    }
}

// =====================================================================
// 027 — rules and their steps
// =====================================================================

public class ApprovalRuleConfiguration : IEntityTypeConfiguration<ApprovalRule>
{
    public void Configure(EntityTypeBuilder<ApprovalRule> b)
    {
        b.ToTable("ApprovalRules");
        b.HasKey(r => r.Id);

        b.Property(r => r.Name).HasMaxLength(150).IsRequired();
        b.Property(r => r.Description).HasMaxLength(500);
        b.Property(r => r.CreatedBy).HasMaxLength(200);
        b.Property(r => r.UpdatedBy).HasMaxLength(200);

        b.Property(r => r.DiscountOverPercent).HasPrecision(5, 2);
        b.Property(r => r.TotalOverAmount).HasPrecision(18, 2);

        // Stored as the underlying byte, matching TINYINT in the migration.
        b.Property(r => r.ConditionMode).HasConversion<byte>();

        // Computed, not a column.
        b.Ignore(r => r.IsCatchAll);

        b.HasMany(r => r.Steps)
         .WithOne(s => s.Rule)
         .HasForeignKey(s => s.ApprovalRuleId)
         .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(r => new { r.TenantId, r.SortOrder })
         .HasDatabaseName("IX_ApprovalRules_Tenant_Order");

        b.HasIndex(r => new { r.TenantId, r.Name })
         .IsUnique()
         .HasDatabaseName("UX_ApprovalRules_Tenant_Name");
    }
}

public class ApprovalStepConfiguration : IEntityTypeConfiguration<ApprovalStep>
{
    public void Configure(EntityTypeBuilder<ApprovalStep> b)
    {
        b.ToTable("ApprovalSteps");
        b.HasKey(s => s.Id);

        b.Property(s => s.Name).HasMaxLength(150);
        b.Property(s => s.CreatedBy).HasMaxLength(200);
        b.Property(s => s.ApproverKind).HasConversion<byte>();

        // Computed, not a column.
        b.Ignore(s => s.EffectiveName);

        // No navigation to Role or User on purpose — see the note in
        // 027_ApprovalChains.sql. A dangling id is shown as "role no
        // longer exists" rather than breaking the page.
        b.HasIndex(s => new { s.ApprovalRuleId, s.StepOrder })
         .IsUnique()
         .HasDatabaseName("UX_ApprovalSteps_Rule_Order");

        b.HasIndex(s => s.TenantId)
         .HasDatabaseName("IX_ApprovalSteps_Tenant");
    }
}

public class QuoteApprovalDecisionConfiguration : IEntityTypeConfiguration<QuoteApprovalDecision>
{
    public void Configure(EntityTypeBuilder<QuoteApprovalDecision> b)
    {
        b.ToTable("QuoteApprovalDecisions");
        b.HasKey(d => d.Id);

        b.Property(d => d.StepName).HasMaxLength(150);
        b.Property(d => d.Decision).HasMaxLength(30).IsRequired();
        b.Property(d => d.DecidedByName).HasMaxLength(200).IsRequired();
        b.Property(d => d.Comment).HasMaxLength(1000);

        b.HasIndex(d => new { d.RequestId, d.StepOrder })
         .HasDatabaseName("IX_QuoteApprovalDecisions_Request");
    }
}
