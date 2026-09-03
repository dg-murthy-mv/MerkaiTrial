// Infrastructure/Persistence/Configurations/DealConfiguration.cs
// =====================================================================
// FIXED:   Currency property config removed (column dropped)
//          Priority property config removed (not in entity or DB schema)
// ADDED:   SourceId FK config
//          StageHistory relationship
// =====================================================================

using MerkaiTrial.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerkaiTrial.Infrastructure.Persistence.Configurations
{
    public class DealConfiguration : IEntityTypeConfiguration<Deal>
    {
        public void Configure(EntityTypeBuilder<Deal> builder)
        {
            builder.ToTable("Deals");

            builder.HasKey(d => d.Id);

            builder.Property(d => d.Title)
                .IsRequired()
                .HasMaxLength(200);

            builder.Property(d => d.Description)
                .HasMaxLength(4000);

            // Stage is nvarchar — no enum conversion needed
            builder.Property(d => d.Stage)
                .IsRequired()
                .HasMaxLength(50);

            builder.Property(d => d.ExpectedValue)
                .HasPrecision(18, 2);

            builder.Property(d => d.ActualValue)
                .HasPrecision(18, 2);

            // Currency REMOVED — derived from tenant via ICurrentTenantService

            builder.Property(d => d.OwnerUserId)
                .HasMaxLength(450);

            builder.Property(d => d.LostReason)
                .HasMaxLength(500);

            builder.Property(d => d.Tags)
                .HasMaxLength(500);

            builder.Property(d => d.CreatedBy)
                .HasMaxLength(100);

            builder.Property(d => d.UpdatedBy)
                .HasMaxLength(100);

            builder.Property(d => d.DeletedBy)
                .HasMaxLength(100);

            // ── Relationships ─────────────────────────────────────────

            builder.HasOne(d => d.Company)
                .WithMany()
                .HasForeignKey(d => d.CompanyId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasOne(d => d.Contact)
                .WithMany(c => c.Deals)
                .HasForeignKey(d => d.ContactId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasOne(d => d.Lead)
                .WithMany()
                .HasForeignKey(d => d.LeadId)
                .OnDelete(DeleteBehavior.Restrict);

            // SourceId → LeadSources (optional FK)
            builder.HasOne<LeadSource>()
                .WithMany()
                .HasForeignKey(d => d.SourceId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.SetNull);

            builder.HasMany(d => d.Activities)
                .WithOne(a => a.Deal)
                .HasForeignKey(a => a.DealId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasMany(d => d.Notes)
                .WithOne(n => n.Deal)
                .HasForeignKey(n => n.DealId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasMany(d => d.Reminders)
                .WithOne(r => r.Deal)
                .HasForeignKey(r => r.DealId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasMany(d => d.StageHistory)
                .WithOne(h => h.Deal)
                .HasForeignKey(h => h.DealId)
                .OnDelete(DeleteBehavior.Cascade);

            // ── Indexes ───────────────────────────────────────────────
            builder.HasIndex(d => d.TenantId);
            builder.HasIndex(d => d.ContactId);
            builder.HasIndex(d => d.LeadId);
            builder.HasIndex(d => d.Stage);
            builder.HasIndex(d => d.SourceId);
            builder.HasIndex(d => d.OwnerUserId);
            builder.HasIndex(d => d.CreatedAtUtc);
        }
    }
}
