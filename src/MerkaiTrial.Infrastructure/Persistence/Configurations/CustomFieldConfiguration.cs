// =====================================================================
// FILE: MerkaiTrial.Infrastructure/Persistence/Configurations/CustomFieldConfiguration.cs
//
// NEW FILE (075). Discovered automatically by ApplyConfigurationsFromAssembly.
//
// THE COLUMN TYPES HERE MUST MATCH Sql/075_CustomFields.sql EXACTLY.
// Round 062 is the reason that sentence exists: EF's default decimal is
// (18,2), and a mismatch between what EF sends and what the column holds
// is silent. NumberValue is (18,4) in both places.
//
// 076 — ShowInList, and two filtered indexes for the list page's number
// and date filters (Sql/076_CustomFieldListColumns.sql). TextValue is NOT
// indexed: at NVARCHAR(1000) it can exceed SQL Server's 1,700-byte index
// key limit, and an index that refuses some inserts is worse than none.
// Text and dropdown filters narrow by DefinitionId through the existing
// UX_CustomFieldValues_Definition_Entity index instead.
//
// 079 — MapToContactFieldId / MapToDealFieldId (Sql/079_LeadFieldMapping.sql):
// plain nullable GUID columns, NOT relationships. A self-referencing FK
// would block deleting a Contact or Deal field that a Lead field points
// at; the delete handler clears the pointer instead.
// =====================================================================

using MerkaiTrial.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerkaiTrial.Infrastructure.Persistence.Configurations
{
    public class CustomFieldDefinitionConfiguration : IEntityTypeConfiguration<CustomFieldDefinition>
    {
        public void Configure(EntityTypeBuilder<CustomFieldDefinition> builder)
        {
            builder.ToTable("CustomFieldDefinitions");
            builder.HasKey(d => d.Id);

            builder.Property(d => d.TenantId).IsRequired();
            builder.Property(d => d.EntityType).IsRequired().HasMaxLength(32);
            builder.Property(d => d.Label).IsRequired().HasMaxLength(80);
            builder.Property(d => d.FieldType).IsRequired().HasMaxLength(20);
            builder.Property(d => d.HelpText).HasMaxLength(200);
            builder.Property(d => d.OptionsJson);                     // NVARCHAR(MAX)
            builder.Property(d => d.IsRequired).IsRequired().HasDefaultValue(false);
            // NO HasDefaultValue(true) here, deliberately. EF treats a bool's
            // CLR default (false) as "not set" when a store default exists,
            // leaves it out of the INSERT, and the database fills in TRUE —
            // so a field created switched OFF would be saved switched ON.
            // The SQL column keeps its DEFAULT (1) for hand-written inserts;
            // EF always sends the value it has.
            builder.Property(d => d.IsActive).IsRequired();
            builder.Property(d => d.IsDeleted).IsRequired().HasDefaultValue(false);
            builder.Property(d => d.SortOrder).IsRequired();

            // 076. No HasDefaultValue — the same bool-sentinel trap as
            // IsActive: a field saved with ShowInList = false would come
            // back true if EF left the column to a store default of 1.
            // (The SQL default is 0 anyway; EF always sends the value.)
            builder.Property(d => d.ShowInList).IsRequired();
            builder.Property(d => d.DecimalPlaces).IsRequired().HasDefaultValue(0);

            // 079 — lead → contact / deal mapping. Plain columns; see header.
            builder.Property(d => d.MapToContactFieldId);
            builder.Property(d => d.MapToDealFieldId);
            builder.Property(d => d.CreatedAtUtc).IsRequired();
            builder.Property(d => d.CreatedBy).HasMaxLength(64);
            builder.Property(d => d.UpdatedBy).HasMaxLength(64);

            // One "Renewal date" per entity type per workspace. Filtered on
            // IsDeleted so a deleted label can be used again.
            builder.HasIndex(d => new { d.TenantId, d.EntityType, d.Label })
                   .IsUnique()
                   .HasDatabaseName("UX_CustomFieldDefinitions_Tenant_Entity_Label")
                   .HasFilter("[IsDeleted] = 0");

            builder.HasIndex(d => new { d.TenantId, d.EntityType, d.SortOrder })
                   .HasDatabaseName("IX_CustomFieldDefinitions_Tenant_Entity_Sort")
                   .HasFilter("[IsDeleted] = 0");
        }
    }

    public class CustomFieldValueConfiguration : IEntityTypeConfiguration<CustomFieldValue>
    {
        public void Configure(EntityTypeBuilder<CustomFieldValue> builder)
        {
            builder.ToTable("CustomFieldValues");
            builder.HasKey(v => v.Id);

            builder.Property(v => v.TenantId).IsRequired();
            builder.Property(v => v.DefinitionId).IsRequired();
            builder.Property(v => v.EntityId).IsRequired();

            builder.Property(v => v.TextValue).HasMaxLength(1000);
            builder.Property(v => v.NumberValue).HasPrecision(18, 4);
            builder.Property(v => v.DateValue).HasColumnType("date");
            builder.Property(v => v.BoolValue);

            builder.Property(v => v.CreatedAtUtc).IsRequired();
            builder.Property(v => v.UpdatedBy).HasMaxLength(64);

            // Restrict, not cascade: a definition with values is retired,
            // never deleted, so this should never fire — and if it ever
            // does, an error is better than silently losing every value.
            builder.HasOne(v => v.Definition)
                   .WithMany()
                   .HasForeignKey(v => v.DefinitionId)
                   .OnDelete(DeleteBehavior.Restrict);

            // One value per field per record.
            builder.HasIndex(v => new { v.DefinitionId, v.EntityId })
                   .IsUnique()
                   .HasDatabaseName("UX_CustomFieldValues_Definition_Entity");

            // "Every value on this contact" — the detail and edit pages.
            builder.HasIndex(v => new { v.TenantId, v.EntityId })
                   .HasDatabaseName("IX_CustomFieldValues_Tenant_Entity");

            // 076 — the list page's "between" filters on numbers and dates.
            builder.HasIndex(v => new { v.DefinitionId, v.NumberValue })
                   .HasDatabaseName("IX_CustomFieldValues_Definition_Number")
                   .HasFilter("[NumberValue] IS NOT NULL")
                   .IncludeProperties(v => v.EntityId);

            builder.HasIndex(v => new { v.DefinitionId, v.DateValue })
                   .HasDatabaseName("IX_CustomFieldValues_Definition_Date")
                   .HasFilter("[DateValue] IS NOT NULL")
                   .IncludeProperties(v => v.EntityId);
        }
    }
}
