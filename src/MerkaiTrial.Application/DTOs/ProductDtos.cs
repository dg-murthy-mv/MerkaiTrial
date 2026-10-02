// =====================================================================
// ProductDtos.cs
// Location: MerkaiTrial.Application/DTOs/ProductDtos.cs
//
// 051: every shape gains UnitOfMeasure and TaxCode.
//
//   UnitOfMeasure — what a quantity MEANS (unit, hour, day, sqm, user…).
//                   Codes in UnitsOfMeasure.
//   TaxCode       — HSN / SAC, or whatever the tenant's tax authority
//                   wants printed on the invoice. Nullable.
//
// ProductListItem is a POSITIONAL record and is the one to be careful
// with: it is constructed positionally in GetProductsHandler and consumed
// by the quote line editor, both quote page models and the products list.
// The two new members are TRAILING and have DEFAULTS, so every existing
// construction site still compiles unchanged.
// =====================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MerkaiTrial.Application.DTOs
{
    public class ProductDto
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? Sku { get; set; }
        public string? Category { get; set; }
        public string Type { get; set; } = "Product";

        /// <summary>051. Code from UnitsOfMeasure — "unit", "hour", "sqm"…</summary>
        public string UnitOfMeasure { get; set; } = "unit";

        public decimal ListPrice { get; set; }
        public string Currency { get; set; } = "USD";
        public decimal? TaxRate { get; set; }

        /// <summary>051. HSN / SAC or equivalent. Null where none applies.</summary>
        public string? TaxCode { get; set; }

        public bool IsActive { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public string? CreatedBy { get; set; }
    }

    /// <summary>
    /// 051: UnitOfMeasure and TaxCode added as TRAILING parameters with
    /// defaults. GetProductsHandler constructs this positionally and the
    /// quote editor reads it — trailing defaults mean neither breaks.
    /// </summary>
    public record ProductListItem(
        Guid Id,
        string Name,
        string? Description,
        string? Sku,
        string? Category,
        string Type,
        decimal ListPrice,
        string Currency,
        decimal? TaxRate,
        bool IsActive,
        DateTime? CreatedAtUtc,
        string UnitOfMeasure = "unit",
        string? TaxCode = null
    );

    public record ProductStatsDto(
        int TotalProducts,
        int ActiveProducts,
        decimal TotalValue,
        int CategoriesCount
    );

    public class CreateProductDto
    {
        public Guid TenantId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? Sku { get; set; }
        public string? Category { get; set; }
        public string Type { get; set; } = "Product";

        /// <summary>051.</summary>
        public string UnitOfMeasure { get; set; } = "unit";

        public decimal ListPrice { get; set; }
        public string Currency { get; set; } = "USD";
        public decimal? TaxRate { get; set; }

        /// <summary>051.</summary>
        public string? TaxCode { get; set; }

        public bool IsActive { get; set; } = true;
        public string? CreatedBy { get; set; }
    }

    public class UpdateProductDto
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? Sku { get; set; }
        public string? Category { get; set; }
        public string Type { get; set; } = "Product";

        /// <summary>051.</summary>
        public string UnitOfMeasure { get; set; } = "unit";

        public decimal ListPrice { get; set; }
        public string Currency { get; set; } = "USD";
        public decimal? TaxRate { get; set; }

        /// <summary>051.</summary>
        public string? TaxCode { get; set; }

        public bool IsActive { get; set; } = true;
    }
}
