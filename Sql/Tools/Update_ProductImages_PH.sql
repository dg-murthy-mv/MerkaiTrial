-- =====================================================================
-- SCRIPT: Update_ProductImages_PH.sql
-- Updates ImageUrl for Kape Isla (Philippines) seed products
-- Store: AAAA0003-0000-0000-0000-000000000000
-- Source: Unsplash (free, no auth required, CDN-served)
-- Run: After seed data is loaded
-- =====================================================================

-- ── COFFEE (EC030001) ────────────────────────────────────────────────

-- Barako Brewed Coffee — dark black drip coffee in a ceramic cup
UPDATE Products
SET ImageUrl   = 'https://images.unsplash.com/photo-1509042239860-f550ce710b93?w=400&q=80',
    UpdatedAt  = GETUTCDATE(),
    UpdatedBy  = 'system'
WHERE Id = 'ED030001-0000-0000-0000-000000000000';

-- Iced Cafe Latte — iced latte with milk in a clear glass
UPDATE Products
SET ImageUrl   = 'https://images.unsplash.com/photo-1461023058943-07fcbe16d735?w=400&q=80',
    UpdatedAt  = GETUTCDATE(),
    UpdatedBy  = 'system'
WHERE Id = 'ED030002-0000-0000-0000-000000000000';

-- Dirty Matcha — matcha latte with espresso shot, layered green drink
UPDATE Products
SET ImageUrl   = 'https://images.unsplash.com/photo-1536256263959-770b48d82b0a?w=400&q=80',
    UpdatedAt  = GETUTCDATE(),
    UpdatedBy  = 'system'
WHERE Id = 'ED030003-0000-0000-0000-000000000000';

-- ── NON-COFFEE DRINKS (EC030002) ────────────────────────────────────

-- Strawberry Soda — pink sparkling strawberry drink with ice
UPDATE Products
SET ImageUrl   = 'https://images.unsplash.com/photo-1567620905732-2d1ec7ab7445?w=400&q=80',
    UpdatedAt  = GETUTCDATE(),
    UpdatedBy  = 'system'
WHERE Id = 'ED030004-0000-0000-0000-000000000000';

-- ── ALL-DAY FOOD (EC030003) ──────────────────────────────────────────

-- Tapsilog — Filipino breakfast plate with beef tapa, garlic rice, egg
UPDATE Products
SET ImageUrl   = 'https://images.unsplash.com/photo-1512058564366-18510be2db19?w=400&q=80',
    UpdatedAt  = GETUTCDATE(),
    UpdatedBy  = 'system'
WHERE Id = 'ED030005-0000-0000-0000-000000000000';

-- Avocado Toast — sourdough toast with smashed avocado and toppings
UPDATE Products
SET ImageUrl   = 'https://images.unsplash.com/photo-1541519227354-08fa5d50c820?w=400&q=80',
    UpdatedAt  = GETUTCDATE(),
    UpdatedBy  = 'system'
WHERE Id = 'ED030006-0000-0000-0000-000000000000';

-- ── PASTRIES & BREADS (EC030004) ────────────────────────────────────

-- Ensaymada — soft Filipino brioche with butter and cheese on top
UPDATE Products
SET ImageUrl   = 'https://images.unsplash.com/photo-1555507036-ab1f4038808a?w=400&q=80',
    UpdatedAt  = GETUTCDATE(),
    UpdatedBy  = 'system'
WHERE Id = 'ED030007-0000-0000-0000-000000000000';

-- Banana Bread — sliced moist banana loaf with walnuts
UPDATE Products
SET ImageUrl   = 'https://images.unsplash.com/photo-1574085733277-851d9d856a3a?w=400&q=80',
    UpdatedAt  = GETUTCDATE(),
    UpdatedBy  = 'system'
WHERE Id = 'ED030008-0000-0000-0000-000000000000';

-- ── VERIFY ──────────────────────────────────────────────────────────
SELECT Id, Name, SKU, ImageUrl
FROM   Products
WHERE  StoreId = 'AAAA0003-0000-0000-0000-000000000000'
ORDER  BY CategoryId, Name;
