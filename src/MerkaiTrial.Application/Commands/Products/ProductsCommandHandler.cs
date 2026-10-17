// =====================================================================
// ProductsCommandHandler.cs — FIXED
// Changes:
//   REMOVED: Product.Currency column (derived from tenant)
//   ADDED:   ICurrentTenantService to provide currency for display
//   NOTE:    ProductListItem and ProductDto no longer carry Currency —
//            update those DTOs to remove the Currency field,
//            or keep it and populate from ICurrentTenantService
//
// ── 080: CUSTOM FIELDS ON PRODUCTS, AND THE LIST FIXES ──────────────
//
//   • Create / Update stage the product's custom field values with
//     CustomFieldValueWriter.ApplyAsync BEFORE SaveChangesAsync, so the
//     product, its prices, its bundle contents and its custom values
//     commit together or not at all. GetById reads them back
//     (CustomFieldValueWriter.ReadAsync) for the product page and Edit.
//
//   • The list searches custom text values and dropdown choices too,
//     takes ?cf= filters, and returns the "Show on the list" values for
//     the rows on the page only — the same shared CustomFieldListQuery
//     the Contacts, Deals, Companies and Leads lists use.
//
//   • The list CLAMPS page and size here, where the data is: page 0 used
//     to ask SQL for a negative OFFSET, and any page size was accepted.
//     The cap is 1000, not 100, ON PURPOSE: the bundle picker on the
//     product form and the quote line editor load the whole catalogue in
//     one call (pageSize 1000), and a lower cap would silently cut their
//     dropdowns short. The Products list itself only offers 10–100.
//
//   • The order is STABLE (Category, Name, then Id). Two products with
//     the same category and name could swap places between pages, so one
//     showed twice and the other never. A page past the end (after a
//     delete, say) becomes the last page instead of an empty one.
//
//   • Search ignores case explicitly rather than relying on the
//     database collation.
//
//   • VALIDATION SENTENCES. The category guard, the price grid and the
//     bundle check all throw InvalidOperationException with a sentence
//     meant for the person. Create and Update now wrap exactly those
//     steps and rethrow them as ProductValidationException, so the
//     controller can return them as a 400 — and ONLY them: an
//     InvalidOperationException from EF during the save is still a
//     logged 500 whose internal wording never reaches the page.
//
//   • AUDIT. ProductCreated { name, type, category } and ProductUpdated
//     { name, changed: [...] } — field NAMES only, "customFields" when
//     any custom value changed (see AuditChanges). ProductPriceChanged
//     is still written on a price or tax change, with the figures, as
//     before. Every audit write now sits AFTER the save inside its own
//     try/catch: a saved product must never report failure because its
//     audit row could not be written (Delete included).
//
//   • The stray "using DocumentFormat.OpenXml.Presentation;" is gone.
//
// ── 095 ─────────────────────────────────────────────────────────────
//
//   The list price (0 or more, and not absurd — InputRules.MaxMoney) and
//   the tax rate (0 to 100) are checked on Create and Update, beside the
//   name, as ProductValidationException sentences — a 400 the page shows,
//   like the category and price-grid checks. A price of 0 is allowed: a
//   free site visit or a complimentary item is a real line on a quote.
//
//   The SKU is REQUIRED (RequireSku). The database column never allowed
//   NULL, but the form didn't say so: a product saved without one failed
//   inside SaveChanges as a 500. Now it is a sentence, trimmed, on both
//   Create and Update. The SKU (50) and the description (4000) are held
//   to their column sizes too, for the same reason: too long was a 500.
//   SKUs are NOT checked for duplicates — the database index on
//   (Sku, TenantId) is not unique, so two products may share one.
// =====================================================================

using MerkaiTrial.Application.Commands.CustomFields;   // 080
using MerkaiTrial.Application.Configuration;   // 051: ProductKinds, UnitsOfMeasure
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Security;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using MerkaiTrial.Application.Validation;              // 095
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;                    // 080


namespace MerkaiTrial.Application.Commands.Products;

/// <summary>
/// 080. A product could not be saved for a reason the PERSON can fix — an
/// unknown category, a bad price row, a bundle that contains itself, a
/// missing name. The message is a sentence written for them, and the
/// controller returns it as a 400.
///
/// Derived from InvalidOperationException so any older catch still
/// catches it.
/// </summary>
public class ProductValidationException : InvalidOperationException
{
    public ProductValidationException(string message) : base(message) { }
}

public class GetProductsHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly ICurrentTenantService _tenant;

    /// <summary>
    /// 080. The most rows one call may ask for. 1000, not 100, because the
    /// bundle picker and the quote line editor load the whole catalogue
    /// in one call; see the header.
    /// </summary>
    public const int MaxPageSize = 1000;

    public GetProductsHandler(FlowDbContext db, ICurrentTenantService tenant)
    {
        _db     = db;
        _tenant = tenant;
    }

    /// <summary>
    /// 069. <paramref name="currency"/> is new and TRAILING, so every
    /// existing call still compiles and still means "the workspace's own
    /// currency".
    ///
    /// WHAT IT IS FOR. The quote line editor builds a quote in the DEAL's
    /// currency, which is not always the workspace's — see
    /// ProductPricing.cs. Passing it here makes every returned
    /// ListPrice a price IN THAT CURRENCY, and sets
    /// HasPriceInCurrency=false on the products that have none, so the
    /// editor can say so instead of showing a number that was never in
    /// that currency.
    ///
    /// Pass null (or the workspace's own code) and nothing changes:
    /// prices come from Product.ListPrice and HasPriceInCurrency is true
    /// everywhere, which is the single-currency workspace that most of
    /// them are. No extra query is run in that case either —
    /// ProductPricing.LookupAsync returns early.
    /// </summary>
    public Task<PaginatedResult<ProductListItem>> Handle(
        Guid tenantId,
        int pageNumber        = 1,
        int pageSize          = 25,
        string? category      = null,
        bool? isActive        = null,
        string? searchTerm    = null,
        // BEFORE the CancellationToken, not after it. An optional
        // parameter following a CancellationToken trips CA1068
        // ("CancellationToken parameters must come last"), which is a
        // warning normally and a build failure under
        // TreatWarningsAsErrors. ProductsController is the only caller
        // and passes both explicitly.
        string? currency      = null,
        CancellationToken cancellationToken = default)
        => Handle(tenantId, pageNumber, pageSize, category, isActive, searchTerm,
                  currency, customFilters: null, cancellationToken);

    /// <summary>
    /// 080. The same read with custom field filters (decoded ?cf= values).
    ///
    /// A SEPARATE OVERLOAD with every parameter required, rather than one
    /// more optional parameter on the method above: any existing caller
    /// that passes its CancellationToken positionally (eighth argument)
    /// keeps compiling against the original signature, and a call with
    /// all nine arguments can only mean this one — so the two never
    /// become ambiguous.
    /// </summary>
    public async Task<PaginatedResult<ProductListItem>> Handle(
        Guid tenantId,
        int pageNumber,
        int pageSize,
        string? category,
        bool? isActive,
        string? searchTerm,
        string? currency,
        IReadOnlyList<CustomFieldFilter>? customFilters,
        CancellationToken cancellationToken)
    {
        // 080 — clamped HERE. See the header for why the cap is 1000.
        pageSize   = Math.Clamp(pageSize, 1, MaxPageSize);
        pageNumber = Math.Max(pageNumber, 1);

        // Currency is tenant-level — fetch once, apply to all items
        var currencySymbol = _tenant.GetCurrencySymbol();
        var currencyCode   = _tenant.GetCurrencyCode();

        // 069. The currency this read is denominated in: the one asked
        // for, or the workspace's.
        var wantedCurrency = CurrencyConfiguration.NormaliseCode(currency)
                             ?? CurrencyConfiguration.NormaliseCode(currencyCode)
                             ?? string.Empty;

        // 080 — the ACTIVE Product custom fields: search, filters, columns.
        var defs = await CustomFieldListQuery.ActiveDefinitionsAsync(
            _db, tenantId, CustomFieldEntityTypes.Product, cancellationToken);

        var query = _db.Products
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId && !p.IsDeleted);

        if (!string.IsNullOrWhiteSpace(category))
        {
            var cat = category.Trim();
            query = query.Where(p => p.Category == cat);
        }

        if (isActive.HasValue)
            query = query.Where(p => p.IsActive == isActive.Value);

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            // 080 — case ignored explicitly, and custom text values and
            // dropdown choice names searched too. Null = no custom field
            // could match, so the plain search runs on its own.
            var s = searchTerm.Trim().ToLower();
            var cfMatches = CustomFieldListQuery.SearchMatches(_db, tenantId, defs, s);

            if (cfMatches is null)
            {
                query = query.Where(p =>
                    p.Name.ToLower().Contains(s) ||
                    (p.Sku != null && p.Sku.ToLower().Contains(s)) ||
                    (p.Description != null && p.Description.ToLower().Contains(s)));
            }
            else
            {
                query = query.Where(p =>
                    p.Name.ToLower().Contains(s) ||
                    (p.Sku != null && p.Sku.ToLower().Contains(s)) ||
                    (p.Description != null && p.Description.ToLower().Contains(s)) ||
                    cfMatches.Contains(p.Id));
            }
        }

        // 080 — custom field filters, as id subqueries (shared helper).
        foreach (var clause in CustomFieldListQuery.FilterClauses(_db, tenantId, defs, customFilters))
        {
            var ids = clause.EntityIds;   // a local, so EF inlines the subquery
            query = clause.Exclude
                ? query.Where(p => !ids.Contains(p.Id))
                : query.Where(p => ids.Contains(p.Id));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        // 080 — a page past the end becomes the last page.
        var totalPages = totalCount == 0 ? 1 : (int)Math.Ceiling(totalCount / (double)pageSize);
        if (pageNumber > totalPages) pageNumber = totalPages;

        // 069. The page's rows first, THEN the prices.
        //
        // Two queries rather than one projection with a sub-select, on
        // purpose: the price rule has three branches (home currency,
        // a priced other currency, no price at all) and it lives in
        // ProductPricing.Resolve. An EF projection cannot call it, so
        // writing it inline here would be a second copy of the rule —
        // which is exactly how the quote editor and the products list
        // came to disagree about what a price meant in the first place.
        var rows = await query
            .OrderBy(p => p.Category)
            .ThenBy(p => p.Name)
            .ThenBy(p => p.Id)              // 080 — stable: paging never repeats or skips a row
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new
            {
                p.Id, p.Name, p.Description, p.Sku, p.Category, p.Type,
                p.ListPrice, p.TaxRate, p.IsActive, p.CreatedAtUtc,
                p.UnitOfMeasure, p.TaxCode
            })
            .ToListAsync(cancellationToken);

        var pageIds = rows.Select(r => r.Id).ToList();

        // Prices in the wanted currency. Returns an empty map — with no
        // query at all — when the wanted currency IS the workspace's.
        var priced = await ProductPricing.LookupAsync(
            _db, tenantId, currencyCode, wantedCurrency, pageIds, cancellationToken);

        // How many OTHER currencies each product on this page has, for
        // the badge on the list. One grouped query for the page, not one
        // per row.
        // 070. The contents of every BUNDLE on this page, in one query.
        // Nothing is read when the page holds no bundles, which is every
        // page until somebody makes one.
        var bundleIds = rows
            .Where(r => ProductKinds.IsBundle(r.Type))
            .Select(r => r.Id)
            .ToList();

        var bundles = bundleIds.Count == 0
            ? new Dictionary<Guid, List<ProductBundleItemDto>>()
            : await ProductBundles.LookupAsync(_db, tenantId, bundleIds, cancellationToken);

        var otherCounts = await _db.ProductPrices
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted && pageIds.Contains(x.ProductId))
            .GroupBy(x => x.ProductId)
            .Select(g => new { ProductId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.ProductId, g => g.Count, cancellationToken);

        var items = rows.Select(p =>
        {
            var price = ProductPricing.Resolve(
                p.Id, p.ListPrice, currencyCode, wantedCurrency, priced);

            return new ProductListItem(
                p.Id,
                p.Name,
                p.Description,
                p.Sku,
                p.Category,
                p.Type,
                price.Price,       // 069: in wantedCurrency, 0 when unpriced
                currencyCode,      // ← the WORKSPACE's, unchanged. See below.
                p.TaxRate,
                p.IsActive,
                p.CreatedAtUtc,
                p.UnitOfMeasure,   // 051
                p.TaxCode,         // 051
                // 069. PriceCurrency, not Currency, is what ListPrice is
                // denominated in. Currency above keeps meaning "the
                // workspace's currency" because the products list prints
                // it as the workspace's and changing it would move that
                // page's meaning as a side effect of a quote-editor fix.
                wantedCurrency,
                price.HasPrice,
                otherCounts.TryGetValue(p.Id, out var n) ? n : 0,

                // ── 070 ──────────────────────────────────────────────
                // The breakdown is built HERE, server-side, and travels
                // as a string. The quote line editor snapshots it
                // straight into a line's description, so building it in
                // the browser would mean the sentence existing twice —
                // once in C# for the product page and once in
                // JavaScript for the editor — and the two drifting.
                ProductKinds.IsBundle(p.Type),
                bundles.TryGetValue(p.Id, out var parts)
                    ? ProductBundles.BuildBreakdown(parts)
                    : null,
                bundles.TryGetValue(p.Id, out var cnt) ? cnt.Count : 0
            );
        }).ToList();

        // 080 — the "Show on the list" custom values, this page only, one
        // query. Empty (and no query) when no field is on the list.
        var listValues = await CustomFieldListQuery.ListValuesAsync(
            _db, tenantId, defs, pageIds, cancellationToken);

        foreach (var item in items)
            if (listValues.TryGetValue(item.Id, out var map))
                item.CustomFieldValues = map;

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
                UnitOfMeasure = p.UnitOfMeasure,   // 051
                ListPrice   = p.ListPrice,
                TaxRate     = p.TaxRate,
                TaxCode     = p.TaxCode,           // 051
                IsActive    = p.IsActive,
                CreatedAtUtc = p.CreatedAtUtc,
                CreatedBy   = p.CreatedBy
            })
            .FirstOrDefaultAsync();

        if (product == null)
            throw new KeyNotFoundException($"Product {productId} not found");

        // Populate currency from tenant — not stored on product row
        product.Currency = _tenant.GetCurrencyCode();

        // 069. The other-currency prices, in CurrencyConfiguration's
        // order so two products never list their currencies differently.
        // A separate read rather than an Include: ProductPricing owns the
        // ordering and the DTO shape, and this is the only place the
        // product page and the edit form's grid get them from.
        product.Prices = await ProductPricing.GetForProductAsync(_db, tenantId, productId);

        // 070. The contents, when it is a bundle, and how many bundles
        // it is PART OF whatever kind it is — the second one is the
        // warning the product page shows before somebody deactivates it.
        if (ProductKinds.IsBundle(product.Type))
            product.BundleItems = await ProductBundles.GetForBundleAsync(_db, tenantId, productId);

        var usedIn = await ProductBundles.UsedInBundleCountsAsync(
            _db, tenantId, new[] { productId });
        product.UsedInBundleCount = usedIn.TryGetValue(productId, out var n) ? n : 0;

        // 080. Every stored custom value, retired fields included — the
        // page decides what to show; Edit needs them all so a save does
        // not drop a value it never displayed.
        product.CustomFieldValues = await CustomFieldValueWriter.ReadAsync(
            _db, tenantId, CustomFieldEntityTypes.Product, productId, default);

        return product;
    }
}

public class CreateProductHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly ICurrentUserService _currentUserService;
    private readonly ICurrentTenantService _tenant;   // 069
    private readonly IAuditService _audit;            // 080
    private readonly ILogger<CreateProductHandler> _logger;   // 080

    public CreateProductHandler(
        FlowDbContext db,
        ICurrentUserService currentUserService,
        ICurrentTenantService tenant,                 // 069
        IAuditService audit,                          // 080
        ILogger<CreateProductHandler> logger)         // 080
    {
        _db                = db;
        _currentUserService = currentUserService;
        _tenant            = tenant;
        _audit             = audit;
        _logger            = logger;
    }

    public async Task<ProductDto> Handle(CreateProductDto dto)
    {
        var currentUser = await _currentUserService.GetCurrentUserAsync();

        // 080 — the person-fixable checks first, as sentences (see
        // ProductValidation). Nothing has been staged yet.
        var name = ProductValidation.RequireName(dto.Name);
        var sku  = ProductValidation.RequireSku(dto.Sku);             // 095
        ProductValidation.CheckFigures(dto.ListPrice, dto.TaxRate, dto.Description);   // 095

        // 068: checked against this workspace's own category list,
        // not stored as posted. See ProductCategoryGuard.
        var category = await ProductValidation.Run(
            () => ProductCategoryGuard.ResolveAsync(_db, dto.TenantId, dto.Category));

        var product = new Product
        {
            Id          = Guid.NewGuid(),
            TenantId    = dto.TenantId,
            Name        = name,
            Description = dto.Description,
            Sku         = sku,
            Category    = category,
            // 051: normalised, never taken as typed. A posted form can
            // carry anything; an unrecognised kind or unit would render as
            // a blank dropdown and print as nothing on the quote line.
            Type        = ProductKinds.Normalise(dto.Type),
            UnitOfMeasure = UnitsOfMeasure.Normalise(dto.UnitOfMeasure),
            ListPrice   = dto.ListPrice,
            // Currency removed — no longer stored on Product
            TaxRate      = dto.TaxRate ?? 0,
            TaxCode      = string.IsNullOrWhiteSpace(dto.TaxCode) ? null : dto.TaxCode.Trim(),
            IsActive     = dto.IsActive,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy    = dto.CreatedBy ?? currentUser.FullName
        };

        _db.Products.Add(product);

        // 069. BEFORE SaveChangesAsync, so the product and its prices
        // commit together. A price grid that failed validation must not
        // leave a product behind, and a product that failed must not
        // leave prices.
        //
        // The home currency is passed so a row in it can be refused —
        // the base price is ListPrice above, and two places to store it
        // would disagree.
        await ProductValidation.Run(() => ProductPricing.SaveAsync(
            _db, dto.TenantId, product.Id,
            _tenant.GetCurrencyCode(), dto.Prices,
            product.CreatedBy ?? currentUser.FullName));

        // ── 070: THE BUNDLE'S CONTENTS ───────────────────────────────
        //
        // On CREATE the contents are REQUIRED when the kind is Bundle,
        // and a null list is therefore validated as an empty one — which
        // ValidateAsync refuses with "A bundle has to contain something."
        // A bundle created empty is a product with a price and no
        // explanation, and the customer would see "Starter package" with
        // nothing under it.
        //
        // UPDATE treats null differently — see the note there. The
        // asymmetry is deliberate: on create there is nothing to leave
        // alone.
        if (ProductKinds.IsBundle(product.Type))
        {
            var components = await ProductValidation.Run(() => ProductBundles.ValidateAsync(
                _db, dto.TenantId, product.Id,
                dto.BundleItems ?? new List<ProductBundleItemDto>()));

            await ProductBundles.SaveAsync(
                _db, dto.TenantId, product.Id, components,
                product.CreatedBy ?? currentUser.FullName);
        }

        // 080 — the custom values, in the same unit of work. Throws
        // CustomFieldValidationException (every problem at once) before
        // anything is saved.
        await CustomFieldValueWriter.ApplyAsync(
            _db, dto.TenantId, CustomFieldEntityTypes.Product, product.Id,
            dto.CustomFields, product.CreatedBy ?? currentUser.FullName, isNew: true, default);

        await _db.SaveChangesAsync();

        // 080 — after the save; a failure here must not undo it.
        try
        {
            await _audit.WriteAsync(
                AuditAction.ProductCreated, AuditEntityType.Product, product.Id, dto.TenantId,
                new { name = product.Name, type = product.Type, category = product.Category });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Product {Id} was created but its audit row could not be written", product.Id);
        }

        return new ProductDto
        {
            Id          = product.Id,
            TenantId    = product.TenantId,
            Name        = product.Name,
            Description = product.Description,
            Sku         = product.Sku,
            Category    = product.Category,
            Type        = product.Type,
            UnitOfMeasure = product.UnitOfMeasure,   // 051
            ListPrice   = product.ListPrice,
            // Currency populated by caller via ICurrentTenantService
            TaxRate     = product.TaxRate,
            TaxCode     = product.TaxCode,           // 051
            IsActive    = product.IsActive,
            CreatedAtUtc = product.CreatedAtUtc,
            CreatedBy   = product.CreatedBy,

            // 069. Read back rather than echoed from the input: the
            // handler normalises the codes and drops blank rows, so what
            // was stored is not necessarily what arrived.
            Prices      = await ProductPricing.GetForProductAsync(_db, dto.TenantId, product.Id),

            // 070. Likewise — the order is renumbered and the component
            // names come from the products themselves, not from whatever
            // the caller claimed they were called.
            BundleItems = ProductKinds.IsBundle(product.Type)
                ? await ProductBundles.GetForBundleAsync(_db, dto.TenantId, product.Id)
                : new List<ProductBundleItemDto>(),

            // 080. Read back for the same reason as Prices.
            CustomFieldValues = await CustomFieldValueWriter.ReadAsync(
                _db, dto.TenantId, CustomFieldEntityTypes.Product, product.Id, default)
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
    private readonly IAuditService _audit;
    private readonly ICurrentTenantService _tenant;   // 069
    private readonly ILogger<UpdateProductHandler> _logger;   // 080

    public UpdateProductHandler(
        FlowDbContext db,
        ICurrentUserService currentUserService,
        IAuditService audit,
        ICurrentTenantService tenant,                 // 069
        ILogger<UpdateProductHandler> logger)         // 080
    {
        _db                = db;
        _currentUserService = currentUserService;
        _audit             = audit;
        _tenant            = tenant;
        _logger            = logger;
    }

    public async Task Handle(Guid tenantId, Guid productId, UpdateProductDto dto)
    {
        var product = await _db.Products
            .FirstOrDefaultAsync(p => p.Id == productId && p.TenantId == tenantId && !p.IsDeleted);

        if (product == null)
            throw new KeyNotFoundException($"Product {productId} not found");

        var currentUser = await _currentUserService.GetCurrentUserAsync();
        var oldPrice = product.ListPrice;                               // ✅ AUDIT
        var oldTax = product.TaxRate;

        // 080 — the person-fixable checks first, as sentences.
        var name = ProductValidation.RequireName(dto.Name);
        var sku  = ProductValidation.RequireSku(dto.Sku);             // 095
        ProductValidation.CheckFigures(dto.ListPrice, dto.TaxRate, dto.Description);   // 095

        // 068 — see ProductCategoryGuard. Same check as Create, so a
        // product cannot be EDITED into a category that does not exist
        // either, which is the path a stale Edit page would take after
        // somebody removed a category in another tab.
        var category = await ProductValidation.Run(
            () => ProductCategoryGuard.ResolveAsync(_db, tenantId, dto.Category));

        var newType   = ProductKinds.Normalise(dto.Type);              // 051
        var newUnit   = UnitsOfMeasure.Normalise(dto.UnitOfMeasure);   // 051
        var newTax    = dto.TaxRate ?? 0;
        var newCode   = string.IsNullOrWhiteSpace(dto.TaxCode) ? null : dto.TaxCode.Trim(); // 051
        var previousName = product.Name;

        // 080 — what changed, by NAME, for the ProductUpdated audit row.
        var changed = new List<string>();
        AuditChanges.Track(changed, "name",          product.Name,          name);
        AuditChanges.Track(changed, "description",   product.Description,   dto.Description);
        AuditChanges.Track(changed, "sku",           product.Sku,           sku);
        AuditChanges.Track(changed, "category",      product.Category,      category);
        AuditChanges.Track(changed, "type",          product.Type,          newType);
        AuditChanges.Track(changed, "unitOfMeasure", product.UnitOfMeasure, newUnit);
        AuditChanges.Track(changed, "listPrice",     product.ListPrice,     dto.ListPrice);
        AuditChanges.Track(changed, "taxRate",       product.TaxRate,       (decimal?)newTax);
        AuditChanges.Track(changed, "taxCode",       product.TaxCode,       newCode);
        AuditChanges.Track(changed, "isActive",      product.IsActive,      dto.IsActive);

        product.Name        = name;
        product.Description = dto.Description;
        product.Sku         = sku;
        product.Category    = category;
        product.Type          = newType;
        product.UnitOfMeasure = newUnit;
        product.ListPrice   = dto.ListPrice;
        // Currency removed — no longer stored on Product
        product.TaxRate      = newTax;
        product.TaxCode      = newCode;
        product.IsActive     = dto.IsActive;
        product.UpdatedAtUtc = DateTime.UtcNow;
        product.UpdatedBy    = currentUser.FullName;

        // 069. Same transaction as the product itself.
        //
        // dto.Prices being NULL means the caller said nothing about
        // prices and nothing is touched — which is what an API client
        // written before this round sends, and it must not wipe a
        // product's currencies as a side effect of renaming it. An EMPTY
        // list means "no other currencies" and does remove them. The
        // form always sends a list, so clearing the grid clears the
        // prices; see ProductPricing.SaveAsync.
        await ProductValidation.Run(() => ProductPricing.SaveAsync(
            _db, tenantId, product.Id,
            _tenant.GetCurrencyCode(), dto.Prices,
            currentUser.FullName));

        // ── 070: THE BUNDLE'S CONTENTS ───────────────────────────────
        //
        // Three cases, and the third is the one that would otherwise
        // leave rubbish behind:
        //
        //   STILL A BUNDLE, contents sent      → validate and replace.
        //   STILL A BUNDLE, contents NULL      → the caller said nothing;
        //                                        leave them alone. An API
        //                                        client renaming a bundle
        //                                        must not empty it.
        //   NO LONGER A BUNDLE                 → CLEAR them. A Service
        //                                        with components is
        //                                        meaningless, and leaving
        //                                        the rows would make the
        //                                        product a bundle again
        //                                        the moment somebody
        //                                        switched the kind back,
        //                                        with contents they had
        //                                        forgotten about.
        if (ProductKinds.IsBundle(product.Type))
        {
            if (dto.BundleItems is not null)
            {
                var components = await ProductValidation.Run(() => ProductBundles.ValidateAsync(
                    _db, tenantId, product.Id, dto.BundleItems));

                await ProductBundles.SaveAsync(
                    _db, tenantId, product.Id, components, currentUser.FullName);
            }
        }
        else
        {
            // An empty list, not null: null means "leave alone" and that
            // is exactly what must NOT happen here.
            await ProductBundles.SaveAsync(
                _db, tenantId, product.Id,
                new List<ProductBundleItemDto>(), currentUser.FullName);
        }

        // 080 — the custom values, same unit of work. Null = the caller
        // said nothing about them, and nothing is touched.
        await CustomFieldValueWriter.ApplyAsync(
            _db, tenantId, CustomFieldEntityTypes.Product, product.Id,
            dto.CustomFields, currentUser.FullName, isNew: false, default);

        if (AuditChanges.CustomFieldValuesPending(_db))
            changed.Add(AuditChanges.CustomFields);

        await _db.SaveChangesAsync();

        // 080 — after the save, each in its own try/catch: a saved product
        // must never report failure because an audit row could not be
        // written.
        try
        {
            if (oldPrice != product.ListPrice || oldTax != product.TaxRate)
                await _audit.WriteAsync(
                    AuditAction.ProductPriceChanged, AuditEntityType.Product, product.Id, tenantId,
                    new
                    {
                        name = product.Name,
                        price = new { from = oldPrice, to = product.ListPrice },
                        taxRate = new { from = oldTax, to = product.TaxRate }
                    });

            if (changed.Count > 0)
                await _audit.WriteAsync(
                    AuditAction.ProductUpdated, AuditEntityType.Product, product.Id, tenantId,
                    changed.Contains("name")
                        ? new { name = product.Name, changed, previousName }
                        : (object)new { name = product.Name, changed });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Product {Id} was updated but its audit row could not be written", product.Id);
        }
    }
}

public class DeleteProductHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly ICurrentUserService _currentUserService;
    private readonly IAuditService _audit;
    private readonly ILogger<DeleteProductHandler> _logger;   // 080
    public DeleteProductHandler(
        FlowDbContext db, ICurrentUserService currentUserService, IAuditService audit,
        ILogger<DeleteProductHandler> logger)                 // 080
    {
        _db                = db;
        _currentUserService = currentUserService;
        _audit             = audit;
        _logger            = logger;
    }

    public async Task Handle(Guid tenantId, Guid productId)
    {
        var product = await _db.Products
            .FirstOrDefaultAsync(p => p.Id == productId && p.TenantId == tenantId && !p.IsDeleted);

        if (product == null)
            throw new KeyNotFoundException($"Product {productId} not found");

        // 080 — REFUSED while the product is part of a live bundle. A
        // deleted component would otherwise stay inside the bundle: the
        // bundle's contents, its breakdown on new quote lines and its
        // "components total" would all still count a product nobody can
        // open any more. The person decides — take it out of the bundle,
        // or switch it to Inactive, which keeps it in the bundle honestly.
        var inBundles = _db.ProductBundleItems
            .AsNoTracking()
            .Where(i => i.TenantId == tenantId && !i.IsDeleted && i.ComponentProductId == productId)
            .Join(_db.Products.Where(b => b.TenantId == tenantId && !b.IsDeleted),
                  i => i.BundleProductId, b => b.Id, (i, b) => b.Name)
            .Distinct();

        var bundleCount = await inBundles.CountAsync();
        if (bundleCount > 0)
        {
            var names = await inBundles.OrderBy(n => n).Take(3).ToListAsync();
            var list  = string.Join(", ", names.Select(n => $"\"{n}\""));
            var more  = bundleCount > names.Count ? $" and {bundleCount - names.Count} more" : string.Empty;

            throw new ProductValidationException(
                $"\"{product.Name}\" is part of {(bundleCount == 1 ? "a bundle" : bundleCount + " bundles")} " +
                $"({list}{more}). Take it out of {(bundleCount == 1 ? "that bundle" : "those bundles")} first, " +
                "or switch it to Inactive instead.");
        }

        var currentUser = await _currentUserService.GetCurrentUserAsync();

        product.IsDeleted    = true;
        product.DeletedAtUtc = DateTime.UtcNow;
        product.DeletedBy    = currentUser.FullName;

        await _db.SaveChangesAsync();

        // 080 — after the save; a failure here must not undo it, and must
        // not tell the person a delete that happened has failed.
        try
        {
            await _audit.WriteAsync(
                AuditAction.ProductDeleted, AuditEntityType.Product, product.Id, tenantId,
                new { name = product.Name, sku = product.Sku, price = product.ListPrice });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Product {Id} was deleted but its audit row could not be written", product.Id);
        }
    }
}

/// <summary>
/// The category NAMES this workspace can file a product under.
///
/// ── 068: WHAT THIS USED TO DO, AND WHY IT WAS WRONG ──────────────────
///
/// It read DISTINCT Product.Category off the products table:
///
///     _db.Products.Where(...).Select(p => p.Category!).Distinct()
///
/// So it answered "which categories are in use" and was being asked
/// "which categories exist". Three consequences, none of which announced
/// themselves:
///
///   • A workspace with no products got an EMPTY list, so the first
///     product could not be filed under anything at all.
///   • A category nobody had used yet was invisible — including, before
///     this round, all eight of the built-in ones, because the dropdowns
///     on Create and Edit were reading the hardcoded C# list instead and
///     this endpoint was only ever used as a filter.
///   • A category deleted from every product vanished from the list
///     without anybody deciding it should.
///
/// It now reads dbo.ProductCategories through the same resolution rule
/// every other screen uses — own rows if the workspace has any, Merkai
/// defaults otherwise — so "exists" and "in use" are different questions
/// with different answers, which is what they are.
///
/// Active rows only. An inactive category is one somebody switched off;
/// it still prints on old documents, but nothing new should be filed
/// under it. The Settings page asks GetProductCategoriesHandler for the
/// inactive ones directly.
/// </summary>
/// <summary>
/// 068. The category a product may be filed under, checked once so
/// Create and Update cannot disagree.
///
/// WHY THIS IS NEEDED AT ALL, given the form has a dropdown
///   The dropdown is built from the list, so a value outside it means a
///   stale page, a hand-made API call, or an import. Any of those three
///   writes a string into Product.Category that no category row matches —
///   and that product then shows a grey box on the list, a blank dropdown
///   on its own Edit page, and nothing at all under the category filter.
///   It is the exact orphaned state grid 3 of migration 068 exists to
///   catch, and this is where it gets prevented instead of detected.
///
/// Normalises as well as checks: " Software" and "Software" are the same
/// category to a person and two different strings in the column.
/// </summary>
internal static class ProductCategoryGuard
{
    // (080: callers wrap ResolveAsync in ProductValidation.Run, so its
    // sentence reaches the page as a 400 rather than a generic error.)

    /// <summary>
    /// The value to STORE. NULL in, NULL out — Product.Category is
    /// nullable and an uncategorized product is legal. Anything else has
    /// to match a live, ACTIVE category for this workspace.
    /// </summary>
    public static async Task<string?> ResolveAsync(
        FlowDbContext db, Guid tenantId, string? supplied, CancellationToken ct = default)
    {
        var wanted = ProductCategoriesConfiguration.NormaliseName(supplied);
        if (wanted is null) return null;

        var visible = await db.ProductCategories
            .AsNoTracking()
            .Where(c => !c.IsDeleted && (c.TenantId == null || c.TenantId == tenantId))
            .ToListAsync(ct);

        var match = ProductCategoryResolution.Effective(visible)
            .FirstOrDefault(c => string.Equals(c.Name, wanted, StringComparison.OrdinalIgnoreCase));

        if (match is null)
            throw new InvalidOperationException(
                $"\"{wanted}\" is not one of your product categories. " +
                "Add it under Settings → Product Categories, or pick an existing one.");

        // The row's own spelling wins, so the column never ends up holding
        // two casings of the same category.
        return match.Name;
    }
}

public class GetCategoriesHandler : ICommandHandler
{
    private readonly FlowDbContext _db;

    public GetCategoriesHandler(FlowDbContext db) => _db = db;

    public async Task<List<string>> Handle(Guid tenantId)
    {
        var visible = await _db.ProductCategories
            .AsNoTracking()
            .Where(c => !c.IsDeleted && (c.TenantId == null || c.TenantId == tenantId))
            .ToListAsync();

        return ProductCategoryResolution.Effective(visible)
            .Select(c => c.Name)
            .ToList();
    }
}

/// <summary>
/// 080. Turns the person-fixable failures of a product save into
/// ProductValidationException, so the controller can tell them apart from
/// a real fault.
///
/// The category guard, ProductPricing and ProductBundles throw
/// InvalidOperationException with a sentence written for the person.
/// Only the steps wrapped in Run are converted — an
/// InvalidOperationException from EF during SaveChangesAsync is NOT
/// inside a Run and stays a logged 500.
/// </summary>
internal static class ProductValidation
{
    /// <summary>The trimmed name, or a sentence when there is none.</summary>
    public static string RequireName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw new ProductValidationException("Give the product a name.");
        if (trimmed.Length > 200)                                         // 095
            throw new ProductValidationException("A product name can be up to 200 characters.");
        return trimmed;
    }

    /// <summary>
    /// 095. The trimmed SKU, or a sentence when there is none. The Sku
    /// column does not allow NULL, so an empty SKU used to reach the
    /// database and come back as a 500 ("The product could not be
    /// created") with no hint of what was wrong. Up to 50 characters,
    /// the column's size (ProductConfiguration).
    /// </summary>
    public static string RequireSku(string? sku)
    {
        var trimmed = sku?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw new ProductValidationException("Give the product a SKU, a short code such as PROD-001.");
        if (trimmed.Length > SkuMax)
            throw new ProductValidationException($"A SKU can be up to {SkuMax} characters.");
        return trimmed;
    }

    /// <summary>The column sizes in ProductConfiguration.</summary>
    public const int SkuMax = 50;
    public const int DescriptionMax = 4000;

    /// <summary>095. The list price, the tax rate and the description's length, all at once.</summary>
    public static void CheckFigures(decimal listPrice, decimal? taxRate, string? description)
    {
        var errors = new List<string>();
        errors.AddIf(InputRules.Text(description, "the description", DescriptionMax));
        errors.AddIf(InputRules.Money(listPrice, "the list price"));
        errors.AddIf(InputRules.Percent(taxRate, "the tax rate"));
        if (errors.Count > 0) throw new ProductValidationException(string.Join(" ", errors));
    }

    public static async Task<T> Run<T>(Func<Task<T>> step)
    {
        try
        {
            return await step();
        }
        catch (InvalidOperationException ex)
            when (ex is not ProductValidationException && ex is not CustomFieldValidationException)
        {
            throw new ProductValidationException(ex.Message);
        }
    }

    public static async Task Run(Func<Task> step)
    {
        try
        {
            await step();
        }
        catch (InvalidOperationException ex)
            when (ex is not ProductValidationException && ex is not CustomFieldValidationException)
        {
            throw new ProductValidationException(ex.Message);
        }
    }
}
