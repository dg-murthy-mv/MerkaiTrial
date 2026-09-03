using MerkaiTrial.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerkaiTrial.Infrastructure.Persistence.Configurations
{
    public class JourneyConfiguration : IEntityTypeConfiguration<Journey>
    {
        public void Configure(EntityTypeBuilder<Journey> b)
        {
            b.ToTable("Journeys");

            b.HasKey(x => x.Id);

            // If your DB uses NEWID() default you can omit this; harmless either way.
            b.Property(x => x.Id)
             .ValueGeneratedOnAdd();

            b.Property(x => x.TenantId)
             .IsRequired();

            b.Property(x => x.Name)
             .IsRequired()
             .HasMaxLength(100);

            b.Property(x => x.StepsJson)
             .IsRequired()
             .HasColumnType("nvarchar(max)")
             .HasDefaultValue("[]");

            b.Property(x => x.CreatedUtc)
             .HasDefaultValueSql("SYSUTCDATETIME()")
             .ValueGeneratedOnAdd();

            b.Property(x => x.CreatedBy).HasMaxLength(64);
            b.Property(x => x.UpdatedBy).HasMaxLength(64);

            b.Property(x => x.IsDeleted)
             .HasDefaultValue(false);

            // Unique filtered index: one active journey per tenant (IsDeleted = 0)
            b.HasIndex(x => x.TenantId)
             .IsUnique()
             .HasDatabaseName("UX_Journeys_Tenant_OneActive")
             .HasFilter("[IsDeleted] = 0");

            // Fast list: WHERE TenantId = ... ORDER BY CreatedUtc DESC
            b.HasIndex(x => new { x.TenantId, x.CreatedUtc })
             .HasDatabaseName("IX_Journeys_Tenant_CreatedUtc");

            // Server-side checks (SQL Server)
            b.HasCheckConstraint("CK_Journeys_Name_NotBlank", "LEN(LTRIM(RTRIM([Name]))) > 0");
            b.HasCheckConstraint("CK_Journeys_StepsJson_IsJson", "ISJSON([StepsJson]) = 1");
        }
    }
}
