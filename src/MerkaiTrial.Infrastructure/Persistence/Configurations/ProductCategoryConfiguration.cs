// =====================================================================
// ProductCategoryConfiguration.cs
// Location: MerkaiTrial.Infrastructure/Persistence/Configurations/
//
// NEW FILE (068).
//
// Picked up automatically: FlowDbContext calls
// b.ApplyConfigurationsFromAssembly(typeof(FlowDbContext).Assembly),
// so there is nothing to register anywhere.
//
// The lengths here MATCH Sql/068_ProductCategories.sql exactly. They are
// stated in both places on purpose — the SQL is what the database
// enforces, this is what EF believes, and a mismatch between the two is
// how a value gets silently truncated by SQL Server instead of refused
// by the application.
// =====================================================================

using MerkaiTrial.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerkaiTrial.Infrastructure.Persistence.Configurations;

public class ProductCategoryConfiguration : IEntityTypeConfiguration<ProductCategory>
{
    public void Configure(EntityTypeBuilder<ProductCategory> b)
    {
        b.ToTable("ProductCategories");
        b.HasKey(c => c.Id);

        b.Property(c => c.Name) .HasMaxLength(80).IsRequired();
        b.Property(c => c.Icon) .HasMaxLength(60).IsRequired();
        b.Property(c => c.Color).HasMaxLength(9) .IsRequired();

        b.Property(c => c.CreatedBy).HasMaxLength(200);
        b.Property(c => c.UpdatedBy).HasMaxLength(200);

        // IsSystem is computed from TenantId — not a column.
        b.Ignore(c => c.IsSystem);

        // Matches UX_ProductCategories_Tenant_Name. FILTERED on IsDeleted
        // so a name can be used again after the old category is removed;
        // without the filter, deleting "Hardware" and adding it back would
        // collide with the soft-deleted row forever.
        b.HasIndex(c => new { c.TenantId, c.Name })
         .IsUnique()
         .HasFilter("[IsDeleted] = 0")
         .HasDatabaseName("UX_ProductCategories_Tenant_Name");

        // Matches IX_ProductCategories_Tenant_Sort — the read every
        // dropdown does.
        b.HasIndex(c => new { c.TenantId, c.SortOrder, c.Name })
         .HasFilter("[IsDeleted] = 0")
         .HasDatabaseName("IX_ProductCategories_Tenant_Sort");
    }
}

/* =====================================================================
   ALSO REQUIRED — FlowDbContext.cs, and it is in this round's copy:

   1. DbSet:

          public DbSet<ProductCategory> ProductCategories => Set<ProductCategory>();

   2. Global query filter, with the SHARED-OR-TENANT group — not the
      strict one. WITHOUT IT the startup assertion fails by name, which
      is the guard doing its job:

          b.Entity<ProductCategory>()
              .HasQueryFilter(e => e.TenantId == null || e.TenantId == CurrentTenantId);
   ===================================================================== */
