// =====================================================================
// ProductsCommandHandler.cs — FIXED
// Changes:
//   REMOVED: Product.Currency column (derived from tenant)
//   ADDED:   ICurrentTenantService to provide currency for display
//   NOTE:    ProductListItem and ProductDto no longer carry Currency —
//            update those DTOs to remove the Currency field,
//            or keep it and populate from ICurrentTenantService
// =====================================================================

using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Application.Commands.Products;

public class GetProductsHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly ICurrentTenantService _tenant;

    public GetProductsHandler(FlowDbContext db, ICurrentTenantService tenant)
    {
        _db     = db;
        _tenant = tenant;
    }

    public async Task<PaginatedResult<ProductListItem>> Handle(
        Guid tenantId,
        int pageNumber        = 1,
        int pageSize          = 25,
        string? category      = null,
        bool? isActive        = null,
        string? searchTerm    = null,
        CancellationToken cancellationToken = default)
    {
        // Currency is tenant-level — fetch once, apply to all items
        var currencySymbol = _tenant.GetCurrencySymbol();
        var currencyCode   = _tenant.GetCurrencyCode();

        var query = _db.Products
            .Where(p => p.TenantId == tenantId && !p.IsDeleted);

        if (!string.IsNullOrEmpty(category))
            query = query.Where(p => p.Category == category);

        if (isActive.HasValue)
            query = query.Where(p => p.IsActive == isActive.Value);

        if (!string.IsNullOrEmpty(searchTerm))
            query = query.Where(p =>
                p.Name.Contains(searchTerm) ||
                (p.Sku != null && p.Sku.Contains(searchTerm)) ||
                (p.Description != null && p.Description.Contains(searchTerm)));

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderBy(p => p.Category)
            .ThenBy(p => p.Name)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new ProductListItem(
                p.Id,
                p.Name,
                p.Description,
                p.Sku,
                p.Category,
                p.Type,
                p.ListPrice,
                currencyCode,    // ← from tenant, not product row
                p.TaxRate,
                p.IsActive,
                p.CreatedAtUtc
            ))
            .ToListAsync(cancellationToken);

        return new PaginatedResult<ProductListItem>
        {
            Items      = items,
            Page       = pageNumber,
            PageSize   = pageSize,
            TotalCount = totalCount
        };
    }
}
public class ToggleProductActiveHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly ICurrentUserService _currentUserService;

    public ToggleProductActiveHandler(FlowDbContext db, ICurrentUserService currentUserService)
    {
        _db = db;
        _currentUserService = currentUserService;
    }

    /// <summary>Flips IsActive and returns the new value.</summary>
    public async Task<bool> Handle(Guid tenantId, Guid productId)
    {
        var product = await _db.Products
            .FirstOrDefaultAsync(p => p.Id == productId && p.TenantId == tenantId && !p.IsDeleted);

        if (product == null)
            throw new KeyNotFoundException($"Product {productId} not found");

        var currentUser = await _currentUserService.GetCurrentUserAsync();

        product.IsActive = !product.IsActive;   // flip
        product.UpdatedAtUtc = DateTime.UtcNow;
        product.UpdatedBy = currentUser.FullName;

        await _db.SaveChangesAsync();

        return product.IsActive;   // return new value so caller can show correct message
    }
}
public class GetProductByIdHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly ICurrentTenantService _tenant;

    public GetProductByIdHandler(FlowDbContext db, ICurrentTenantService tenant)
    {
        _db     = db;
        _tenant = tenant;
    }

    public async Task<ProductDto> Handle(Guid tenantId, Guid productId)
    {
        var product = await _db.Products
            .Where(p => p.Id == productId && p.TenantId == tenantId && !p.IsDeleted)
            .Select(p => new ProductDto
            {
                Id          = p.Id,
                TenantId    = p.TenantId,
                Name        = p.Name,
                Description = p.Description,
                Sku         = p.Sku,
                Category    = p.Category,
                Type        = p.Type,
                ListPrice   = p.ListPrice,
                TaxRate     = p.TaxRate,
                IsActive    = p.IsActive,
                CreatedAtUtc = p.CreatedAtUtc,
                CreatedBy   = p.CreatedBy
            })
            .FirstOrDefaultAsync();

        if (product == null)
            throw new KeyNotFoundException($"Product {productId} not found");

        // Populate currency from tenant — not stored on product row
        product.Currency = _tenant.GetCurrencyCode();

        return product;
    }
}

public class CreateProductHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly ICurrentUserService _currentUserService;

    public CreateProductHandler(FlowDbContext db, ICurrentUserService currentUserService)
    {
        _db                = db;
        _currentUserService = currentUserService;
    }

    public async Task<ProductDto> Handle(CreateProductDto dto)
    {
        var currentUser = await _currentUserService.GetCurrentUserAsync();

        var product = new Product
        {
            Id          = Guid.NewGuid(),
            TenantId    = dto.TenantId,
            Name        = dto.Name,
            Description = dto.Description,
            Sku         = dto.Sku,
            Category    = dto.Category,
            Type        = dto.Type,
            ListPrice   = dto.ListPrice,
            // Currency removed — no longer stored on Product
            TaxRate      = dto.TaxRate ?? 0,
            IsActive     = dto.IsActive,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy    = dto.CreatedBy ?? currentUser.FullName
        };

        _db.Products.Add(product);
        await _db.SaveChangesAsync();

        return new ProductDto
        {
            Id          = product.Id,
            TenantId    = product.TenantId,
            Name        = product.Name,
            Description = product.Description,
            Sku         = product.Sku,
            Category    = product.Category,
            Type        = product.Type,
            ListPrice   = product.ListPrice,
            // Currency populated by caller via ICurrentTenantService
            TaxRate     = product.TaxRate,
            IsActive    = product.IsActive,
            CreatedAtUtc = product.CreatedAtUtc,
            CreatedBy   = product.CreatedBy
        };
    }
}

public class GetProductStatsHandler : ICommandHandler
{
    private readonly FlowDbContext _db;

    public GetProductStatsHandler(FlowDbContext db) => _db = db;

    public async Task<ProductStatsDto> Handle(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var totalProducts = await _db.Products
            .CountAsync(p => p.TenantId == tenantId && !p.IsDeleted, cancellationToken);

        var activeProducts = await _db.Products
            .CountAsync(p => p.TenantId == tenantId && !p.IsDeleted && p.IsActive, cancellationToken);

        var totalValue = await _db.Products
            .Where(p => p.TenantId == tenantId && !p.IsDeleted && p.IsActive)
            .SumAsync(p => p.ListPrice, cancellationToken);

        var categoriesCount = await _db.Products
            .Where(p => p.TenantId == tenantId && !p.IsDeleted && !string.IsNullOrEmpty(p.Category))
            .Select(p => p.Category)
            .Distinct()
            .CountAsync(cancellationToken);

        return new ProductStatsDto(
            TotalProducts:  totalProducts,
            ActiveProducts: activeProducts,
            TotalValue:     totalValue,
            CategoriesCount: categoriesCount
        );
    }
}

public class UpdateProductHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly ICurrentUserService _currentUserService;

    public UpdateProductHandler(FlowDbContext db, ICurrentUserService currentUserService)
    {
        _db                = db;
        _currentUserService = currentUserService;
    }

    public async Task Handle(Guid tenantId, Guid productId, UpdateProductDto dto)
    {
        var product = await _db.Products
            .FirstOrDefaultAsync(p => p.Id == productId && p.TenantId == tenantId && !p.IsDeleted);

        if (product == null)
            throw new KeyNotFoundException($"Product {productId} not found");

        var currentUser = await _currentUserService.GetCurrentUserAsync();

        product.Name        = dto.Name;
        product.Description = dto.Description;
        product.Sku         = dto.Sku;
        product.Category    = dto.Category;
        product.Type        = dto.Type;
        product.ListPrice   = dto.ListPrice;
        // Currency removed — no longer stored on Product
        product.TaxRate      = dto.TaxRate ?? 0;
        product.IsActive     = dto.IsActive;
        product.UpdatedAtUtc = DateTime.UtcNow;
        product.UpdatedBy    = currentUser.FullName;

        await _db.SaveChangesAsync();
    }
}

public class DeleteProductHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly ICurrentUserService _currentUserService;

    public DeleteProductHandler(FlowDbContext db, ICurrentUserService currentUserService)
    {
        _db                = db;
        _currentUserService = currentUserService;
    }

    public async Task Handle(Guid tenantId, Guid productId)
    {
        var product = await _db.Products
            .FirstOrDefaultAsync(p => p.Id == productId && p.TenantId == tenantId && !p.IsDeleted);

        if (product == null)
            throw new KeyNotFoundException($"Product {productId} not found");

        var currentUser = await _currentUserService.GetCurrentUserAsync();

        product.IsDeleted    = true;
        product.DeletedAtUtc = DateTime.UtcNow;
        product.DeletedBy    = currentUser.FullName;

        await _db.SaveChangesAsync();
    }
}

public class GetCategoriesHandler : ICommandHandler
{
    private readonly FlowDbContext _db;

    public GetCategoriesHandler(FlowDbContext db) => _db = db;

    public async Task<List<string>> Handle(Guid tenantId)
    {
        return await _db.Products
            .Where(p => p.TenantId == tenantId && !p.IsDeleted && !string.IsNullOrEmpty(p.Category))
            .Select(p => p.Category!)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync();
    }
}
