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
        public decimal ListPrice { get; set; }
        public string Currency { get; set; } = "USD";
        public decimal? TaxRate { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public string? CreatedBy { get; set; }
    }

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
        DateTime? CreatedAtUtc
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
        public decimal ListPrice { get; set; }
        public string Currency { get; set; } = "USD";
        public decimal? TaxRate { get; set; }
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
        public decimal ListPrice { get; set; }
        public string Currency { get; set; } = "USD";
        public decimal? TaxRate { get; set; }
        public bool IsActive { get; set; } = true;
    }
}
