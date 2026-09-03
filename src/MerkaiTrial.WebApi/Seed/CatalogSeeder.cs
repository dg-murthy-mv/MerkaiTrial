using MerkaiTrial.Infrastructure.Persistence;
using MerkaiTrial.Domain.Entities;  // adjust if your namespaces differ
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.WebApi.Seed;

public static class CatalogSeeder
{
    //public static async Task EnsureAsync(FlowDbContext db)
    //{
    //    // assumes you have Products & Prices tables similar to below
    //    if (await db.Products.AnyAsync(p => p.Sku == "PLAN-GROWTH")) return;

    //    var prod = new Product { Sku = "PLAN-GROWTH", Name = "Growth Subscription (12 mo)", Description = "Growth plan, 12 months" };
    //    db.Products.Add(prod);

    //    db.Prices.AddRange(
    //        new Price { Product = prod, Currency = "PHP", ListPrice = 5500m },
    //        new Price { Product = prod, Currency = "THB", ListPrice = 3900m } // example
    //    );

    //    await db.SaveChangesAsync();
    //}

    // helpers
    public static decimal VatForCountry(string countryOrLang)
        => countryOrLang.Equals("fil-PH", StringComparison.OrdinalIgnoreCase) || countryOrLang.Equals("PH", StringComparison.OrdinalIgnoreCase)
           ? 0.12m : 0.07m; // PH 12%, TH 7%

    public static string CurrencyForLang(string lang)
        => lang.StartsWith("fil", StringComparison.OrdinalIgnoreCase) ? "PHP" : "THB";
}
