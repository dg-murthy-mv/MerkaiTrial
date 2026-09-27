// =====================================================================
// CountryConfiguration.cs
// Location: MerkaiTrial.Infrastructure/Persistence/Configurations/CountryConfiguration.cs
//
// 035 — TWO CHANGES
//
// 1. FiscalYearStartMonth is configured with a default of 1 and a CHECK
//    constraint of 1-12. A bad value here would silently shift every
//    report for a whole market, so it is worth the constraint.
//
// 2. The localization columns are given lengths. Only Code and Name were
//    configured before, which means CurrencyCode, CurrencySymbol,
//    NumberFormat, DateFormat, TimeFormat, DialCode, TaxLabel and Timezone
//    were all mapped as nvarchar(max) — none of them can index or sort
//    properly, and a three-letter currency code should not be able to
//    hold a novel.
//
//    NOTE: adding these lengths does not alter the existing table. If the
//    columns in your database really are nvarchar(max), Part 4 of
//    035_FiscalYear.sql shows you and gives you the optional tightening
//    statements. EF is happy either way, so this is not urgent.
// =====================================================================

using MerkaiTrial.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MerkaiTrial.Infrastructure.Persistence.Configurations
{
    public class CountryConfiguration : IEntityTypeConfiguration<Country>
    {
        public void Configure(EntityTypeBuilder<Country> builder)
        {
            builder.ToTable("Countries", t =>
            {
                // 1-12. Guarding this in the database as well as the UI,
                // because a 0 or a 13 would break every fiscal period
                // calculation for every tenant in that market at once.
                t.HasCheckConstraint(
                    "CK_Countries_FiscalYearStartMonth",
                    "[FiscalYearStartMonth] BETWEEN 1 AND 12");
            });

            builder.HasKey(c => c.Id);
            builder.HasIndex(c => c.Code).IsUnique();

            // ── Identity ──────────────────────────────────────────────
            builder.Property(c => c.Code).IsRequired().HasMaxLength(5);
            builder.Property(c => c.Name).IsRequired().HasMaxLength(100);

            // ── Phone ─────────────────────────────────────────────────
            builder.Property(c => c.DialCode).HasMaxLength(10);

            // ── Currency ──────────────────────────────────────────────
            builder.Property(c => c.CurrencyCode).HasMaxLength(3);
            builder.Property(c => c.CurrencySymbol).HasMaxLength(10);

            // ── Tax ───────────────────────────────────────────────────
            builder.Property(c => c.TaxLabel).HasMaxLength(20);
            builder.Property(c => c.DefaultTaxRate).HasPrecision(5, 2);

            // ── Localization ──────────────────────────────────────────
            // NumberFormat holds a culture name such as en-IN. Round 033
            // widened FormatCurrency to accept a legacy .NET format pattern
            // here too, but a culture name is what it wants.
            builder.Property(c => c.NumberFormat).HasMaxLength(20);
            builder.Property(c => c.DateFormat).HasMaxLength(30);
            builder.Property(c => c.TimeFormat).HasMaxLength(30);

            // IANA identifier — Asia/Kolkata, Asia/Bangkok, Asia/Manila,
            // Asia/Dubai. .NET 6 and later accept IANA ids on Windows as
            // well as Linux, so one value works on both.
            builder.Property(c => c.Timezone).HasMaxLength(64);

            // ── Fiscal year (035) ─────────────────────────────────────
            builder.Property(c => c.FiscalYearStartMonth)
                   .IsRequired()
                   .HasDefaultValue(1);
        }
    }
}
