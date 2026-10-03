// =====================================================================
// ProductPriceConfiguration.cs
// Location: MerkaiTrial.Infrastructure/Persistence/Configurations/
//
// NEW FILE (069).
//
// Picked up automatically: FlowDbContext calls
// b.ApplyConfigurationsFromAssembly(typeof(FlowDbContext).Assembly).
//
// The precision and the lengths MATCH Sql/069_ProductPrices.sql exactly.
// Stated in both places on purpose: the SQL is what the database
// enforces, this is what EF believes, and 062 is the round that explains
// what a mismatch between the two costs — EF sent DECIMAL(18,2) for a
// DECIMAL(18,4) column and SQL Server rounded every value on the way in,
// silently.
// =====================================================================

using MerkaiTrial.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerkaiTrial.Infrastructure.Persistence.Configurations;

public class ProductPriceConfiguration : IEntityTypeConfiguration<ProductPrice>
{
    public void Configure(EntityTypeBuilder<ProductPrice> b)
    {
        b.ToTable("ProductPrices");
        b.HasKey(p => p.Id);

        b.Property(p => p.CurrencyCode).HasMaxLength(8).IsRequired();
        b.Property(p => p.ListPrice).HasPrecision(18, 2);

        b.Property(p => p.CreatedBy).HasMaxLength(200);
        b.Property(p => p.UpdatedBy).HasMaxLength(200);

        // Matches FK_ProductPrices_Products_ProductId. CASCADE is
        // deliberate and is the one hard delete in this schema: a price
        // has no meaning without its product. Products are SOFT-deleted,
        // so this only fires if a product row is removed for real.
        b.HasOne(p => p.Product)
         .WithMany(pr => pr.Prices)
         .HasForeignKey(p => p.ProductId)
         .OnDelete(DeleteBehavior.Cascade);

        // Matches UX_ProductPrices_Product_Currency. Filtered on
        // IsDeleted so a currency can be removed from a product and
        // added back later without colliding with the soft-deleted row.
        b.HasIndex(p => new { p.TenantId, p.ProductId, p.CurrencyCode })
         .IsUnique()
         .HasFilter("[IsDeleted] = 0")
         .HasDatabaseName("UX_ProductPrices_Product_Currency");

        // Matches IX_ProductPrices_Tenant_Currency — the read the quote
        // line editor does: every price for this workspace in ONE
        // currency, for the whole catalogue, in one query.
        b.HasIndex(p => new { p.TenantId, p.CurrencyCode })
         .HasFilter("[IsDeleted] = 0")
         .HasDatabaseName("IX_ProductPrices_Tenant_Currency");
    }
}

/* =====================================================================
   ALSO REQUIRED — FlowDbContext.cs, and it is in this round's copy:

   1. DbSet:

          public DbSet<ProductPrice> ProductPrices => Set<ProductPrice>();

   2. Global query filter, with the STRICTLY TENANT-OWNED group — not
      the shared-or-tenant one ProductCategory uses. WITHOUT IT the
      startup assertion fails by name, which is the guard working:

          b.Entity<ProductPrice>()
              .HasQueryFilter(e => e.TenantId == CurrentTenantId);

   3. Nothing in ApplyDecimalPrecision. HasPrecision(18, 2) above is
      the declaration, and it is in the configuration rather than in
      that method because this entity's precision belongs with the rest
      of its mapping. The 062 note in FlowDbContext explains why the
      QuoteItem/InvoiceLine ones live there instead: those properties
      already existed and were being corrected in bulk.
   ===================================================================== */
