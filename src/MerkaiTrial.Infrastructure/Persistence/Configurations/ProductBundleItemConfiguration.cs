// =====================================================================
// ProductBundleItemConfiguration.cs
// Location: MerkaiTrial.Infrastructure/Persistence/Configurations/
//
// NEW FILE (070).
//
// Picked up automatically by
// b.ApplyConfigurationsFromAssembly(typeof(FlowDbContext).Assembly).
//
// ─────────────────────────────────────────────────────────────────────
// THE PART WORTH READING: TWO FOREIGN KEYS TO THE SAME TABLE
//
// ProductBundleItem points at dbo.Products twice — once as the bundle,
// once as the component. EF will not work that out by convention: with
// two Guid properties both referencing Product and two navigation
// properties, it guesses which navigation pairs with which foreign key,
// and when it guesses wrong the symptom is a migration or a query that
// joins the bundle to itself. Both relationships are therefore spelled
// out below, each naming its own foreign key AND its own inverse
// collection on Product.
//
// DeleteBehavior.NoAction on both, which is the one place this schema
// does not cascade. ProductPrices cascades from its product because a
// price has no meaning without it; a bundle ITEM references two
// products, and cascading from the COMPONENT would silently empty a
// bundle when somebody hard-deleted one part of it. Products are
// soft-deleted, so neither fires in practice — but if one ever did,
// failing loudly is right for a definition.
//
// Quantity's precision matches Sql/070_ProductBundles.sql and
// QuoteItem.Quantity: DECIMAL(18,4). Declared here rather than left to
// EF's DECIMAL(18,2) default for the reason the 062 note in
// FlowDbContext sets out at length — the default silently rounded every
// quantity on the way into the database and nothing reported it.
// ─────────────────────────────────────────────────────────────────────
// =====================================================================

using MerkaiTrial.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerkaiTrial.Infrastructure.Persistence.Configurations;

public class ProductBundleItemConfiguration : IEntityTypeConfiguration<ProductBundleItem>
{
    public void Configure(EntityTypeBuilder<ProductBundleItem> b)
    {
        b.ToTable("ProductBundleItems");
        b.HasKey(i => i.Id);

        // 052's shape, and QuoteItem.Quantity's. See the header.
        b.Property(i => i.Quantity).HasPrecision(18, 4);

        b.Property(i => i.CreatedBy).HasMaxLength(200);
        b.Property(i => i.UpdatedBy).HasMaxLength(200);

        // ── The two relationships, both explicit ─────────────────────
        b.HasOne(i => i.BundleProduct)
         .WithMany(p => p.BundleItems)
         .HasForeignKey(i => i.BundleProductId)
         .OnDelete(DeleteBehavior.NoAction);

        b.HasOne(i => i.ComponentProduct)
         .WithMany(p => p.PartOfBundles)
         .HasForeignKey(i => i.ComponentProductId)
         .OnDelete(DeleteBehavior.NoAction);

        // Matches UX_ProductBundleItems_Bundle_Component. A component
        // appears in a bundle ONCE, with a quantity — twice would be two
        // rows saying "2 × Training day" and "3 × Training day" with
        // nothing deciding between them. Filtered on IsDeleted so a
        // component can be removed and added back.
        b.HasIndex(i => new { i.TenantId, i.BundleProductId, i.ComponentProductId })
         .IsUnique()
         .HasFilter("[IsDeleted] = 0")
         .HasDatabaseName("UX_ProductBundleItems_Bundle_Component");

        // Matches IX_ProductBundleItems_Tenant_Bundle_Order — the read
        // the quote line editor does: every bundle's contents, in one
        // query, in display order.
        b.HasIndex(i => new { i.TenantId, i.BundleProductId, i.SortOrder })
         .HasFilter("[IsDeleted] = 0")
         .HasDatabaseName("IX_ProductBundleItems_Tenant_Bundle_Order");
    }
}

/* =====================================================================
   ALSO REQUIRED — FlowDbContext.cs, and it is in this round's copy:

   1. DbSet:

          public DbSet<ProductBundleItem> ProductBundleItems
              => Set<ProductBundleItem>();

   2. Global query filter, with the STRICTLY TENANT-OWNED group.
      WITHOUT IT the startup assertion fails by name:

          b.Entity<ProductBundleItem>()
              .HasQueryFilter(e => e.TenantId == CurrentTenantId);

   AND — Product.cs needs BOTH inverse collections, BundleItems and
   PartOfBundles. They are in this round's Product.cs. Without the
   second one, WithMany(p => p.PartOfBundles) above does not compile,
   which is the good failure: the alternative is WithMany() with no
   argument, where EF invents a shadow navigation and the two foreign
   keys quietly become interchangeable.
   ===================================================================== */
