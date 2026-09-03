// =====================================================================
// ActivityConfiguration.cs
// Location: MerkaiTrial.Infrastructure/Persistence/Configurations/ActivityConfiguration.cs
// =====================================================================

using MerkaiTrial.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerkaiTrial.Infrastructure.Persistence.Configurations;

public class ActivityConfiguration : IEntityTypeConfiguration<Activity>
{
    public void Configure(EntityTypeBuilder<Activity> builder)
    {
        builder.ToTable("Activities");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.EntityType)   .HasMaxLength(50)  .IsRequired();
        builder.Property(a => a.ActivityType) .HasMaxLength(50)  .IsRequired();
        builder.Property(a => a.Subject)      .HasMaxLength(500) .IsRequired();
        builder.Property(a => a.Description)  .HasMaxLength(4000);
        builder.Property(a => a.Outcome)      .HasMaxLength(2000);
        builder.Property(a => a.AssignedToUserId).HasMaxLength(100);
        builder.Property(a => a.CreatedBy)    .HasMaxLength(200);
        builder.Property(a => a.UpdatedBy)    .HasMaxLength(200);

        // Indexes for the most common query patterns
        builder.HasIndex(a => new { a.TenantId, a.EntityType, a.EntityId })
               .HasDatabaseName("IX_Activities_Entity");

        builder.HasIndex(a => new { a.TenantId, a.IsTask, a.IsCompleted, a.DueDate })
               .HasDatabaseName("IX_Activities_Tasks");

        builder.HasIndex(a => new { a.TenantId, a.AssignedToUserId })
               .HasDatabaseName("IX_Activities_AssignedTo");
    }
}

