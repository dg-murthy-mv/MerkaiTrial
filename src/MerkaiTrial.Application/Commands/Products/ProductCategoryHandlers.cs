// =====================================================================
// ProductCategoryHandlers.cs
// Location: MerkaiTrial.Application/Commands/Products/ProductCategoryHandlers.cs
//
// NEW FILE (068). Read, create, rename, remove and reorder product
// categories.
//
// Registered automatically: the Scrutor scan in ApiServiceRegistration
// picks up every ICommandHandler in this assembly, so there is nothing
// to add anywhere.
//
// ─────────────────────────────────────────────────────────────────────
// THE THREE THINGS THAT MAKE THIS FILE LONGER THAN IT LOOKS
//
// 1. ADOPTION. A workspace with no rows of its own is looking at the
//    Merkai defaults, which are shared rows it must not edit. So the
//    first write of any kind copies the whole default list in as
//    tenant-owned rows first, and THEN does what was asked. Without
//    that, the only honest options are a read-only list or a modal
//    asking the person to "activate categories" before they can rename
//    one — and nobody knows what that means.
//
//    The consequence that catches you out: after adopting, THE ID THE
//    CALLER SENT POINTS AT THE WRONG ROW. They clicked Edit on the
//    default "Hardware"; there is now also a tenant-owned "Hardware",
//    and that is the one to change. Every write therefore re-resolves
//    its target BY NAME after adopting. Get that wrong and the handler
//    edits the shared row — changing it for every workspace in the
//    product.
//
// 2. THE RENAME CASCADE. Product.Category is a string, not a foreign
//    key (ProductCategory.cs explains why). Renaming a category has to
//    rewrite that string on every product carrying the old name, in the
//    same transaction as the rename, or the products are orphaned the
//    moment it saves.
//
// 3. OWNERSHIP IS CHECKED HERE, NOT BY THE QUERY FILTER. The filter is
//    the permissive shared-or-tenant one, so it hands back Merkai
//    defaults quite happily. Every write re-reads its row and refuses
//    anything whose TenantId is not this workspace. Same arrangement as
//    TaxRateCommandHelper in round 056, and for the same reason: a
//    filter that admits shared rows is not an authorization check.
// ─────────────────────────────────────────────────────────────────────
// =====================================================================

using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Security;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Commands.Products
{
    // =================================================================
    // SHARED PLUMBING
    // =================================================================

    /// <summary>
    /// Adoption, and the row lookups every write needs. A static helper
    /// rather than a sixth injected service: it is three operations on a
    /// DbContext the caller already has, and an interface here would mean
    /// a registration line to forget.
    /// </summary>
    internal static class ProductCategoryOps
    {
        /// <summary>
        /// Rows this workspace is ALLOWED to see — its own plus the Merkai
        /// defaults. Tracked or not depending on the caller.
        ///
        /// The TenantId predicate is explicit even though the query filter
        /// says the same thing. The filter reads CurrentTenantId out of the
        /// request; these handlers take tenantId as a parameter, the way
        /// every other Products handler does. When the two ever disagree —
        /// a background job, a SuperAdmin impersonating — the parameter is
        /// the one the caller meant.
        /// </summary>
        public static IQueryable<ProductCategory> Visible(FlowDbContext db, Guid tenantId)
            => db.ProductCategories
                 .Where(c => !c.IsDeleted && (c.TenantId == null || c.TenantId == tenantId));

        /// <summary>
        /// Make sure this workspace owns its category list, copying the
        /// Merkai defaults in if it does not. Returns true when it just
        /// did that, so the caller can tell the person once.
        ///
        /// ADDS ROWS; DOES NOT SAVE. The caller's SaveChangesAsync commits
        /// the adoption and the actual change together — a rename that
        /// failed validation must not leave a half-adopted list behind.
        ///
        /// Idempotent: a workspace that already owns one row is left
        /// alone, because by the resolution rule it already sees only its
        /// own and there is nothing to copy.
        /// </summary>
        public static async Task<bool> EnsureAdoptedAsync(
            FlowDbContext db, Guid tenantId, string actor, CancellationToken ct)
        {
            var ownsAny = await db.ProductCategories
                .AnyAsync(c => c.TenantId == tenantId && !c.IsDeleted, ct);

            if (ownsAny) return false;

            var defaults = await db.ProductCategories
                .AsNoTracking()
                .Where(c => c.TenantId == null && !c.IsDeleted)
                .OrderBy(c => c.SortOrder)
                .ToListAsync(ct);

            // No seeded defaults at all — a database where
            // Sql/068_ProductCategories.sql has not been run, or one where
            // somebody removed them. Fall back to the C# constants rather
            // than adopting an empty list and leaving the workspace with
            // no categories and no way to create a product.
            var rows = defaults.Count > 0
                ? defaults.Select(d => new ProductCategory
                  {
                      Name      = d.Name,
                      Icon      = d.Icon,
                      Color     = d.Color,
                      SortOrder = d.SortOrder,
                      IsActive  = d.IsActive
                  })
                : ProductCategoriesConfiguration.Defaults.Select(d => new ProductCategory
                  {
                      Name      = d.Name,
                      Icon      = d.Icon,
                      Color     = d.Color,
                      SortOrder = d.SortOrder,
                      IsActive  = true
                  });

            foreach (var r in rows)
            {
                r.Id           = Guid.NewGuid();
                r.TenantId     = tenantId;
                r.CreatedAtUtc = DateTime.UtcNow;
                r.CreatedBy    = actor;
                r.IsDeleted    = false;
                db.ProductCategories.Add(r);
            }

            return true;
        }

        /// <summary>
        /// The TENANT-OWNED row to act on, given the id the caller sent.
        ///
        /// Call this AFTER EnsureAdoptedAsync. If the id turns out to be a
        /// Merkai default, the matching tenant row — just created by
        /// adoption, still only in the change tracker — is returned
        /// instead. That redirect is the whole reason this method exists;
        /// see note 1 in the header.
        /// </summary>
        public static async Task<ProductCategory?> ResolveOwnedAsync(
            FlowDbContext db, Guid tenantId, Guid id, CancellationToken ct)
        {
            var sent = await db.ProductCategories
                .FirstOrDefaultAsync(c => c.Id == id && !c.IsDeleted &&
                                          (c.TenantId == null || c.TenantId == tenantId), ct);

            if (sent is null) return null;
            if (sent.TenantId == tenantId) return sent;

            // A default. Find this workspace's copy by name — including
            // the one adoption added a moment ago, which is why the local
            // view is checked first: it is not in the database yet.
            var local = db.ChangeTracker.Entries<ProductCategory>()
                .Select(e => e.Entity)
                .FirstOrDefault(e => e.TenantId == tenantId && !e.IsDeleted &&
                                     string.Equals(e.Name, sent.Name, StringComparison.OrdinalIgnoreCase));

            if (local is not null) return local;

            return await db.ProductCategories
                .FirstOrDefaultAsync(c => c.TenantId == tenantId && !c.IsDeleted &&
                                          c.Name == sent.Name, ct);
        }

        /// <summary>
        /// True when this workspace already has a live category by this
        /// name, ignoring one row (itself, on a rename).
        ///
        /// Checks the change tracker as well as the table for the same
        /// reason ResolveOwnedAsync does: straight after adoption the
        /// eight new rows exist only in memory, and without this an
        /// adopt-then-create of "Hardware" would pass validation here and
        /// then fail on the unique index with a 500 instead of a message.
        /// </summary>
        public static async Task<bool> NameTakenAsync(
            FlowDbContext db, Guid tenantId, string name, Guid? exceptId, CancellationToken ct)
        {
            var pendingClash = db.ChangeTracker.Entries<ProductCategory>()
                .Select(e => e.Entity)
                .Any(e => e.TenantId == tenantId && !e.IsDeleted &&
                          e.Id != exceptId &&
                          string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

            if (pendingClash) return true;

            return await db.ProductCategories
                .AnyAsync(c => c.TenantId == tenantId && !c.IsDeleted &&
                               (exceptId == null || c.Id != exceptId) &&
                               c.Name == name, ct);
        }

        /// <summary>
        /// Products per category name, for this workspace, in one grouped
        /// query. Case-insensitive by the column's own collation, which is
        /// the same comparison Product.Category is filtered by everywhere
        /// else.
        /// </summary>
        public static async Task<Dictionary<string, int>> ProductCountsAsync(
            FlowDbContext db, Guid tenantId, CancellationToken ct)
        {
            var rows = await db.Products
                .AsNoTracking()
                .Where(p => p.TenantId == tenantId && !p.IsDeleted && p.Category != null && p.Category != "")
                .GroupBy(p => p.Category!)
                .Select(g => new { Name = g.Key, Count = g.Count() })
                .ToListAsync(ct);

            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows)
            {
                var key = r.Name.Trim();
                map[key] = map.TryGetValue(key, out var existing) ? existing + r.Count : r.Count;
            }
            return map;
        }

        public static ProductCategoryDto ToDto(ProductCategory c, int productCount) => new()
        {
            Id           = c.Id,
            TenantId     = c.TenantId,
            Name         = c.Name,
            Icon         = c.Icon,
            Color        = c.Color,
            SortOrder    = c.SortOrder,
            IsActive     = c.IsActive,
            ProductCount = productCount
        };

        /// <summary>
        /// Name, icon and colour, validated once so create and update
        /// cannot disagree about what is acceptable. Throws
        /// InvalidOperationException, which IApiService turns into the
        /// message the page shows.
        /// </summary>
        public static (string Name, string Icon, string Color) ValidateShape(
            string? name, string? icon, string? color)
        {
            var cleanName = ProductCategoriesConfiguration.NormaliseName(name);
            if (cleanName is null)
                throw new InvalidOperationException("A category needs a name.");

            // Normalise rather than refuse: an icon arrives from a picker,
            // so an unknown one means a stale page or a hand-made request,
            // and a grey box is a better outcome than a rejected save.
            var cleanIcon = ProductCategoryIcons.Normalise(icon);

            // A colour is NOT normalised away, because a wrong one is
            // almost always a typed hex the person can see and fix.
            if (!ProductCategoriesConfiguration.IsValidColor(color))
                throw new InvalidOperationException(
                    "Pick a colour, or type one as #rrggbb — for example #3b82f6.");

            return (cleanName, cleanIcon, color!);
        }
    }

    // =================================================================
    // READ
    // =================================================================

    public class GetProductCategoriesHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;

        public GetProductCategoriesHandler(FlowDbContext db) => _db = db;

        /// <summary>
        /// The effective list for this workspace.
        ///
        /// <paramref name="includeInactive"/> is for the Settings page,
        /// which has to show a switched-off category in order to switch it
        /// back on. Every dropdown in the product passes false.
        /// </summary>
        public async Task<ProductCategoryListDto> Handle(
            Guid tenantId, bool includeInactive = false, CancellationToken ct = default)
        {
            var visible = await ProductCategoryOps.Visible(_db, tenantId)
                .AsNoTracking()
                .ToListAsync(ct);

            var effective = ProductCategoryResolution.Effective(visible, includeInactive);
            var counts    = await ProductCategoryOps.ProductCountsAsync(_db, tenantId, ct);

            var uncategorized = await _db.Products
                .AsNoTracking()
                .CountAsync(p => p.TenantId == tenantId && !p.IsDeleted &&
                                 (p.Category == null || p.Category == ""), ct);

            return new ProductCategoryListDto
            {
                Categories = effective
                    .Select(c => ProductCategoryOps.ToDto(
                        c, counts.TryGetValue(c.Name, out var n) ? n : 0))
                    .ToList(),
                FollowingDefaults         = ProductCategoryResolution.IsFollowingDefaults(visible),
                UncategorizedProductCount = uncategorized
            };
        }
    }

    // =================================================================
    // CREATE
    // =================================================================

    public class CreateProductCategoryHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUser;
        private readonly IAuditService _audit;
        private readonly ILogger<CreateProductCategoryHandler> _logger;

        public CreateProductCategoryHandler(
            FlowDbContext db,
            ICurrentUserService currentUser,
            IAuditService audit,
            ILogger<CreateProductCategoryHandler> logger)
        {
            _db          = db;
            _currentUser = currentUser;
            _audit       = audit;
            _logger      = logger;
        }

        public async Task<ProductCategoryWriteResult> Handle(
            Guid tenantId, CreateProductCategoryDto dto, CancellationToken ct = default)
        {
            var (name, icon, color) = ProductCategoryOps.ValidateShape(dto.Name, dto.Icon, dto.Color);

            var user    = await _currentUser.GetCurrentUserAsync();
            var actor   = user?.FullName ?? "System";
            var adopted = await ProductCategoryOps.EnsureAdoptedAsync(_db, tenantId, actor, ct);

            if (await ProductCategoryOps.NameTakenAsync(_db, tenantId, name, null, ct))
                throw new InvalidOperationException($"There is already a category called \"{name}\".");

            // A new category goes to the END by default, which is what
            // somebody adding one expects. +10 keeps room to drag things
            // between existing rows without renumbering the lot.
            var order = dto.SortOrder ?? await NextSortOrderAsync(tenantId, ct);

            var row = new ProductCategory
            {
                Id           = Guid.NewGuid(),
                TenantId     = tenantId,
                Name         = name,
                Icon         = icon,
                Color        = color,
                SortOrder    = order,
                IsActive     = dto.IsActive,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy    = actor,
                IsDeleted    = false
            };

            _db.ProductCategories.Add(row);
            await _db.SaveChangesAsync(ct);

            if (adopted)
                await _audit.WriteAsync(
                    AuditAction.ProductCategoriesAdopted, AuditEntityType.ProductCategory,
                    row.Id, tenantId,
                    new { reason = "first category write", source = "create" }, ct);

            await _audit.WriteAsync(
                AuditAction.ProductCategoryCreated, AuditEntityType.ProductCategory,
                row.Id, tenantId,
                new { name = row.Name, icon = row.Icon, color = row.Color, isActive = row.IsActive }, ct);

            _logger.LogInformation(
                "Product category {Name} created for tenant {TenantId}", row.Name, tenantId);

            return new ProductCategoryWriteResult
            {
                Category        = ProductCategoryOps.ToDto(row, 0),
                ProductsUpdated = 0,
                AdoptedDefaults = adopted
            };
        }

        private async Task<int> NextSortOrderAsync(Guid tenantId, CancellationToken ct)
        {
            // MaxAsync over an empty set throws on a non-nullable int, and
            // the set IS empty for a workspace whose adoption is still only
            // in the change tracker. Count the tracked rows too.
            var stored = await _db.ProductCategories
                .Where(c => c.TenantId == tenantId && !c.IsDeleted)
                .Select(c => (int?)c.SortOrder)
                .MaxAsync(ct) ?? 0;

            var pending = _db.ChangeTracker.Entries<ProductCategory>()
                .Select(e => e.Entity)
                .Where(e => e.TenantId == tenantId && !e.IsDeleted)
                .Select(e => (int?)e.SortOrder)
                .Max() ?? 0;

            return Math.Max(stored, pending) + 10;
        }
    }

    // =================================================================
    // UPDATE — this is the one with the cascade in it
    // =================================================================

    public class UpdateProductCategoryHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUser;
        private readonly IAuditService _audit;
        private readonly ILogger<UpdateProductCategoryHandler> _logger;

        public UpdateProductCategoryHandler(
            FlowDbContext db,
            ICurrentUserService currentUser,
            IAuditService audit,
            ILogger<UpdateProductCategoryHandler> logger)
        {
            _db          = db;
            _currentUser = currentUser;
            _audit       = audit;
            _logger      = logger;
        }

        public async Task<ProductCategoryWriteResult> Handle(
            Guid tenantId, Guid id, UpdateProductCategoryDto dto, CancellationToken ct = default)
        {
            var (name, icon, color) = ProductCategoryOps.ValidateShape(dto.Name, dto.Icon, dto.Color);

            var user    = await _currentUser.GetCurrentUserAsync();
            var actor   = user?.FullName ?? "System";
            var adopted = await ProductCategoryOps.EnsureAdoptedAsync(_db, tenantId, actor, ct);

            var row = await ProductCategoryOps.ResolveOwnedAsync(_db, tenantId, id, ct)
                      ?? throw new KeyNotFoundException("That category no longer exists.");

            // Belt and braces after ResolveOwnedAsync. If this ever fires
            // it means the redirect-by-name above failed, and writing to a
            // shared row would change it for every workspace in Merkai.
            if (row.TenantId != tenantId)
                throw new InvalidOperationException(
                    "That is a standard Merkai category and cannot be edited directly.");

            if (await ProductCategoryOps.NameTakenAsync(_db, tenantId, name, row.Id, ct))
                throw new InvalidOperationException($"There is already a category called \"{name}\".");

            var oldName = row.Name;
            var renamed = !string.Equals(oldName, name, StringComparison.Ordinal);

            // Switching off the last active category would leave the
            // workspace unable to create a product at all — the Create
            // form requires one. Refuse with something actionable rather
            // than letting them find out on the next product.
            if (!dto.IsActive && row.IsActive)
            {
                var otherActive = await CountOtherActiveAsync(tenantId, row.Id, ct);
                if (otherActive == 0)
                    throw new InvalidOperationException(
                        "This is the only active category. Add another one before switching this off, " +
                        "or new products will have nothing to file under.");
            }

            // ── THE CASCADE ──────────────────────────────────────────
            //
            // Product.Category holds the NAME (see ProductCategory.cs), so
            // a rename has to rewrite it everywhere. In the same
            // transaction as the rename, not after it: a rename that saved
            // and a cascade that then failed would leave every one of
            // these products filed under a category that no longer exists,
            // and their Edit page's dropdown would come up blank.
            //
            // Tracked entities, not ExecuteUpdate. The set is small (the
            // products of one SME), and this way the audit fields move
            // with the change instead of the rows being updated behind
            // EF's back.
            var productsUpdated = 0;
            if (renamed)
            {
                var affected = await _db.Products
                    .Where(p => p.TenantId == tenantId && !p.IsDeleted && p.Category == oldName)
                    .ToListAsync(ct);

                foreach (var p in affected)
                {
                    p.Category     = name;
                    p.UpdatedAtUtc = DateTime.UtcNow;
                    p.UpdatedBy    = actor;
                }
                productsUpdated = affected.Count;
            }

            row.Name         = name;
            row.Icon         = icon;
            row.Color        = color;
            row.IsActive     = dto.IsActive;
            row.UpdatedAtUtc = DateTime.UtcNow;
            row.UpdatedBy    = actor;

            await _db.SaveChangesAsync(ct);

            if (adopted)
                await _audit.WriteAsync(
                    AuditAction.ProductCategoriesAdopted, AuditEntityType.ProductCategory,
                    row.Id, tenantId,
                    new { reason = "first category write", source = "update" }, ct);

            // The old name is in the data on purpose. After the cascade,
            // nothing else in the database remembers it, and "what used to
            // be called Hardware" is the question this row answers.
            await _audit.WriteAsync(
                AuditAction.ProductCategoryUpdated, AuditEntityType.ProductCategory,
                row.Id, tenantId,
                new
                {
                    name            = row.Name,
                    previousName    = renamed ? oldName : null,
                    icon            = row.Icon,
                    color           = row.Color,
                    isActive        = row.IsActive,
                    productsUpdated
                },
                ct);

            if (renamed)
                _logger.LogInformation(
                    "Product category {Old} renamed to {New} for tenant {TenantId}; {Count} product(s) updated",
                    oldName, row.Name, tenantId, productsUpdated);

            return new ProductCategoryWriteResult
            {
                Category        = ProductCategoryOps.ToDto(row, productsUpdated),
                ProductsUpdated = productsUpdated,
                AdoptedDefaults = adopted
            };
        }

        private async Task<int> CountOtherActiveAsync(Guid tenantId, Guid exceptId, CancellationToken ct)
        {
            var stored = await _db.ProductCategories
                .CountAsync(c => c.TenantId == tenantId && !c.IsDeleted &&
                                 c.IsActive && c.Id != exceptId, ct);

            // Rows adoption added a moment ago are not in the table yet.
            // Entries, not .Select(e => e.Entity), so the Added state is
            // read off the entry rather than looked up again per row.
            var pending = _db.ChangeTracker.Entries<ProductCategory>()
                .Count(e => e.State == EntityState.Added &&
                            e.Entity.TenantId == tenantId && !e.Entity.IsDeleted &&
                            e.Entity.IsActive && e.Entity.Id != exceptId);

            return stored + pending;
        }
    }

    // =================================================================
    // DELETE
    // =================================================================

    public class DeleteProductCategoryHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUser;
        private readonly IAuditService _audit;
        private readonly ILogger<DeleteProductCategoryHandler> _logger;

        public DeleteProductCategoryHandler(
            FlowDbContext db,
            ICurrentUserService currentUser,
            IAuditService audit,
            ILogger<DeleteProductCategoryHandler> logger)
        {
            _db          = db;
            _currentUser = currentUser;
            _audit       = audit;
            _logger      = logger;
        }

        public async Task<ProductCategoryWriteResult> Handle(
            Guid tenantId, Guid id, DeleteProductCategoryDto dto, CancellationToken ct = default)
        {
            var user    = await _currentUser.GetCurrentUserAsync();
            var actor   = user?.FullName ?? "System";
            var adopted = await ProductCategoryOps.EnsureAdoptedAsync(_db, tenantId, actor, ct);

            var row = await ProductCategoryOps.ResolveOwnedAsync(_db, tenantId, id, ct)
                      ?? throw new KeyNotFoundException("That category no longer exists.");

            if (row.TenantId != tenantId)
                throw new InvalidOperationException(
                    "That is a standard Merkai category and cannot be removed directly.");

            // The Create form requires a category, so an empty list means
            // no new products. Refuse the last one.
            var othersLive = await CountOtherLiveAsync(tenantId, row.Id, ct);
            if (othersLive == 0)
                throw new InvalidOperationException(
                    "This is your only category. Add another one before removing it.");

            // ── Where do its products go ─────────────────────────────
            //
            // NULL means uncategorized, which is legal — Product.Category
            // has always been nullable. A NAME has to be one this
            // workspace actually has: reassigning to a category that does
            // not exist is the orphaned state the whole round is about
            // avoiding.
            string? target = null;
            if (!string.IsNullOrWhiteSpace(dto.ReassignTo))
            {
                var wanted = ProductCategoriesConfiguration.NormaliseName(dto.ReassignTo)!;

                if (string.Equals(wanted, row.Name, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "Products cannot be moved into the category being removed.");

                var exists = await _db.ProductCategories
                    .AnyAsync(c => c.TenantId == tenantId && !c.IsDeleted && c.Name == wanted, ct);

                if (!exists)
                {
                    // It may be a row adoption added this request.
                    exists = _db.ChangeTracker.Entries<ProductCategory>()
                        .Select(e => e.Entity)
                        .Any(e => e.TenantId == tenantId && !e.IsDeleted &&
                                  string.Equals(e.Name, wanted, StringComparison.OrdinalIgnoreCase));
                }

                if (!exists)
                    throw new InvalidOperationException(
                        $"There is no category called \"{wanted}\" to move these products into.");

                target = wanted;
            }

            var affected = await _db.Products
                .Where(p => p.TenantId == tenantId && !p.IsDeleted && p.Category == row.Name)
                .ToListAsync(ct);

            foreach (var p in affected)
            {
                p.Category     = target;      // null = uncategorized
                p.UpdatedAtUtc = DateTime.UtcNow;
                p.UpdatedBy    = actor;
            }

            // SOFT delete, like everything else here. A hard delete would
            // take the row out from under UX_ProductCategories_Tenant_Name
            // and lose who created it and when — and the index is filtered
            // on IsDeleted precisely so the name can be used again.
            row.IsDeleted    = true;
            row.IsActive     = false;
            row.UpdatedAtUtc = DateTime.UtcNow;
            row.UpdatedBy    = actor;

            await _db.SaveChangesAsync(ct);

            if (adopted)
                await _audit.WriteAsync(
                    AuditAction.ProductCategoriesAdopted, AuditEntityType.ProductCategory,
                    row.Id, tenantId,
                    new { reason = "first category write", source = "delete" }, ct);

            await _audit.WriteAsync(
                AuditAction.ProductCategoryDeleted, AuditEntityType.ProductCategory,
                row.Id, tenantId,
                new
                {
                    name            = row.Name,
                    reassignedTo    = target ?? "(uncategorized)",
                    productsUpdated = affected.Count
                },
                ct);

            _logger.LogInformation(
                "Product category {Name} removed for tenant {TenantId}; {Count} product(s) moved to {Target}",
                row.Name, tenantId, affected.Count, target ?? "(uncategorized)");

            return new ProductCategoryWriteResult
            {
                Category        = null,
                ProductsUpdated = affected.Count,
                AdoptedDefaults = adopted
            };
        }

        private async Task<int> CountOtherLiveAsync(Guid tenantId, Guid exceptId, CancellationToken ct)
        {
            var stored = await _db.ProductCategories
                .CountAsync(c => c.TenantId == tenantId && !c.IsDeleted && c.Id != exceptId, ct);

            var pending = _db.ChangeTracker.Entries<ProductCategory>()
                .Count(e => e.Entity.TenantId == tenantId && !e.Entity.IsDeleted &&
                            e.Entity.Id != exceptId && e.State == EntityState.Added);

            return stored + pending;
        }
    }

    // =================================================================
    // REORDER
    // =================================================================

    public class ReorderProductCategoriesHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUser;
        private readonly ILogger<ReorderProductCategoriesHandler> _logger;

        public ReorderProductCategoriesHandler(
            FlowDbContext db,
            ICurrentUserService currentUser,
            ILogger<ReorderProductCategoriesHandler> logger)
        {
            _db          = db;
            _currentUser = currentUser;
            _logger      = logger;
        }

        /// <summary>
        /// The whole order at once. Ids not belonging to this workspace are
        /// ignored rather than refused — a stale page is the likely cause,
        /// and the alternative is a reorder that fails entirely because one
        /// row was removed in another tab.
        ///
        /// Not audited. A sort order is a display preference with no effect
        /// on a single product, quote or invoice, and auditing every drag
        /// would bury the three events in this file that matter.
        /// </summary>
        public async Task<int> Handle(
            Guid tenantId, ReorderProductCategoriesDto dto, CancellationToken ct = default)
        {
            if (dto.OrderedIds is null || dto.OrderedIds.Count == 0)
                return 0;

            var user  = await _currentUser.GetCurrentUserAsync();
            var actor = user?.FullName ?? "System";

            // Reordering is a write, so it adopts like any other. Without
            // this, dragging a default row would renumber nothing and the
            // list would spring back on reload with no explanation.
            await ProductCategoryOps.EnsureAdoptedAsync(_db, tenantId, actor, ct);

            // Match on NAME, not id, for the usual reason: after adoption
            // the ids the page is holding are the defaults'.
            var sentNames = await _db.ProductCategories
                .AsNoTracking()
                .Where(c => dto.OrderedIds.Contains(c.Id) && !c.IsDeleted &&
                            (c.TenantId == null || c.TenantId == tenantId))
                .Select(c => new { c.Id, c.Name })
                .ToListAsync(ct);

            var nameByPosition = dto.OrderedIds
                .Select(id => sentNames.FirstOrDefault(s => s.Id == id)?.Name)
                .Where(n => n is not null)
                .Select(n => n!)
                .ToList();

            var owned = await _db.ProductCategories
                .Where(c => c.TenantId == tenantId && !c.IsDeleted)
                .ToListAsync(ct);

            var pending = _db.ChangeTracker.Entries<ProductCategory>()
                .Where(e => e.State == EntityState.Added &&
                            e.Entity.TenantId == tenantId && !e.Entity.IsDeleted)
                .Select(e => e.Entity);

            var all = owned.Concat(pending)
                .GroupBy(c => c.Id)
                .Select(g => g.First())
                .ToList();

            var moved = 0;
            for (var i = 0; i < nameByPosition.Count; i++)
            {
                var row = all.FirstOrDefault(c =>
                    string.Equals(c.Name, nameByPosition[i], StringComparison.OrdinalIgnoreCase));

                if (row is null) continue;

                var newOrder = (i + 1) * 10;
                if (row.SortOrder == newOrder) continue;

                row.SortOrder    = newOrder;
                row.UpdatedAtUtc = DateTime.UtcNow;
                row.UpdatedBy    = actor;
                moved++;
            }

            // Anything the page did not send keeps a position AFTER the
            // ones it did, so a row added in another tab does not silently
            // jump to the front.
            var tail = (nameByPosition.Count + 1) * 10;
            foreach (var row in all
                     .Where(c => !nameByPosition.Any(n =>
                         string.Equals(n, c.Name, StringComparison.OrdinalIgnoreCase)))
                     .OrderBy(c => c.SortOrder)
                     .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (row.SortOrder != tail)
                {
                    row.SortOrder = tail;
                    moved++;
                }
                tail += 10;
            }

            if (moved > 0)
                await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Product categories reordered for tenant {TenantId}; {Count} row(s) moved", tenantId, moved);

            return moved;
        }
    }
}
