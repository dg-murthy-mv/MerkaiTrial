// =====================================================================
// LeadConfiguration.cs
// Location: MerkaiTrial.Infrastructure/Persistence/Configurations/LeadConfiguration.cs
//
// FIXES:
//   1. LeadChannel nav → HasForeignKey(ChannelId)  stops EF creating LeadChannelId shadow prop
//   2. LeadSource  nav → HasForeignKey(SourceId)   stops EF creating LeadSourceId shadow prop
//   3. All other nav props explicitly mapped to their existing FK columns
//   4. Channel (enum), Source, Currency mapped to correct DB column types
// =====================================================================

using MerkaiTrial.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerkaiTrial.Infrastructure.Persistence.Configurations;

public class LeadConfiguration : IEntityTypeConfiguration<Lead>
{
    public void Configure(EntityTypeBuilder<Lead> entity)
    {
        entity.ToTable("Leads");
        entity.HasKey(l => l.Id);

        // ── Scalar properties ─────────────────────────────────────────
        entity.Property(l => l.FullName).HasMaxLength(200).IsRequired();
        entity.Property(l => l.Email).HasMaxLength(200);
        entity.Property(l => l.Phone).HasMaxLength(50);
        entity.Property(l => l.CompanyName).HasMaxLength(200);
        entity.Property(l => l.Address).HasMaxLength(500);
        entity.Property(l => l.OwnerUserId).HasMaxLength(100);
        entity.Property(l => l.CustomFieldsJson).HasMaxLength(4000);
        entity.Property(l => l.CreatedBy).HasMaxLength(100);
        entity.Property(l => l.UpdatedBy).HasMaxLength(100);
        entity.Property(l => l.ConvertedBy).HasMaxLength(100);

        // FIX: Channel enum stored as int, Source and Currency as strings
        entity.Property(l => l.Channel)
         .HasColumnName("Channel")
         .HasConversion<int>();

        entity.Property(l => l.Source)
            .HasColumnName("Source")
            .HasMaxLength(100);

        entity.Property(l => l.Currency)
            .HasColumnName("Currency")
            .HasMaxLength(10);

        entity.Property(l => l.EstimatedValue)
            .HasPrecision(18, 2);

        // ── Navigation: Contact (ContactId FK) ───────────────────────
        entity.HasOne(l => l.Contact)
            .WithMany()
            .HasForeignKey(l => l.ContactId)
            .OnDelete(DeleteBehavior.SetNull);

        // ── Navigation: Company (CompanyId FK) ───────────────────────
        entity.HasOne(l => l.Company)
            .WithMany()
            .HasForeignKey(l => l.CompanyId)
            .OnDelete(DeleteBehavior.SetNull);

        // ── Navigation: ConvertedContact (ConvertedToContactId FK) ───
        entity.HasOne(l => l.ConvertedContact)
            .WithMany()
            .HasForeignKey(l => l.ConvertedToContactId)
            .OnDelete(DeleteBehavior.SetNull);

        // ── Navigation: ConvertedCompany (ConvertedToCompanyId FK) ───
        entity.HasOne(l => l.ConvertedCompany)
            .WithMany()
            .HasForeignKey(l => l.ConvertedToCompanyId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.HasOne(l => l.Vertical)
           .WithMany().HasForeignKey(l => l.VerticalId).OnDelete(DeleteBehavior.SetNull);

        // ── FIX 1: LeadChannel nav → ChannelId (stops shadow LeadChannelId) ──
        entity.HasOne(l => l.LeadChannel)
            .WithMany()
            .HasForeignKey(l => l.ChannelId)      // ← explicit — no shadow prop
            .OnDelete(DeleteBehavior.SetNull);

        // ── FIX 2: LeadSource nav → SourceId (stops shadow LeadSourceId) ──
        entity.HasOne(l => l.LeadSource)
            .WithMany()
            .HasForeignKey(l => l.SourceId)        // ← explicit — no shadow prop
            .OnDelete(DeleteBehavior.SetNull);

        // ── Navigation: Country (CountryId FK) ───────────────────────
        entity.HasOne(l => l.Country)
            .WithMany()
            .HasForeignKey(l => l.CountryId)
            .OnDelete(DeleteBehavior.SetNull);

        // ── Collections ───────────────────────────────────────────────
        entity.HasMany(l => l.Notes)
            .WithOne(n => n.Lead)          // ← tells EF it's the SAME relationship
            .HasForeignKey(n => n.LeadId)  // ← use lambda, not string
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasMany(l => l.Activities)
            .WithOne(a => a.Lead)
            .HasForeignKey(a => a.LeadId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasMany(l => l.Reminders)
            .WithOne(r => r.Lead)
            .HasForeignKey(r => r.LeadId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
