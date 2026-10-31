-- =====================================================================
-- SCRIPT: Update_ProductImages_TH.sql
-- Updates ImageUrl for MadeeBakers Thailand
-- Store: AAAA0002-0000-0000-0000-000000000000
-- Source: Unsplash (free, no auth required, CDN-served)
-- Run: After seed data is loaded
-- =====================================================================

-- ── BREADS (EC020001) ────────────────────────────────────────────────

-- Sourdough Loaf — scored whole wheat sourdough with open crumb
UPDATE Products
SET    ImageUrl  = 'https://images.unsplash.com/photo-1586444248902-2f64eddc13df?w=400&q=80',
       UpdatedAt = GETUTCDATE(),
       UpdatedBy = 'system'
WHERE  Id = 'ED020001-0000-0000-0000-000000000000';

-- Baguette — classic French baguette on wooden board
UPDATE Products
SET    ImageUrl  = 'https://images.unsplash.com/photo-1549931319-a545dcf3bc73?w=400&q=80',
       UpdatedAt = GETUTCDATE(),
       UpdatedBy = 'system'
WHERE  Id = 'ED020002-0000-0000-0000-000000000000';

-- ── PASTRIES (EC020002) ──────────────────────────────────────────────

-- Butter Croissant — golden laminated croissant with visible layers
UPDATE Products
SET    ImageUrl  = 'https://images.unsplash.com/photo-1555507036-ab1f4038808a?w=400&q=80',
       UpdatedAt = GETUTCDATE(),
       UpdatedBy = 'system'
WHERE  Id = 'ED020003-0000-0000-0000-000000000000';

-- Pain au Chocolat — dark chocolate-filled croissant pastry
UPDATE Products
SET    ImageUrl  = 'https://images.unsplash.com/photo-1623334044303-241021148842?w=400&q=80',
       UpdatedAt = GETUTCDATE(),
       UpdatedBy = 'system'
WHERE  Id = 'ED020004-0000-0000-0000-000000000000';

-- ── CAKES & TARTS (EC020003) ─────────────────────────────────────────

-- Lemon Tart — classic French tart with lemon curd and meringue peaks
UPDATE Products
SET    ImageUrl  = 'https://images.unsplash.com/photo-1519915028121-7d3463d20b13?w=400&q=80',
       UpdatedAt = GETUTCDATE(),
       UpdatedBy = 'system'
WHERE  Id = 'ED020005-0000-0000-0000-000000000000';

-- Mango Entremet — elegant layered mousse cake with mirror glaze
UPDATE Products
SET    ImageUrl  = 'https://images.unsplash.com/photo-1565958011703-44f9829ba187?w=400&q=80',
       UpdatedAt = GETUTCDATE(),
       UpdatedBy = 'system'
WHERE  Id = 'ED020006-0000-0000-0000-000000000000';

-- ── DRINKS (EC020004) ────────────────────────────────────────────────

-- Drip Coffee — pour-over filter coffee in glass carafe
UPDATE Products
SET    ImageUrl  = 'https://images.unsplash.com/photo-1495474472287-4d71bcdd2085?w=400&q=80',
       UpdatedAt = GETUTCDATE(),
       UpdatedBy = 'system'
WHERE  Id = 'ED020007-0000-0000-0000-000000000000';

-- Fresh Juice — vibrant fresh-pressed orange/fruit juice in glass
UPDATE Products
SET    ImageUrl  = 'https://images.unsplash.com/photo-1622597467836-f3285f2131b8?w=400&q=80',
       UpdatedAt = GETUTCDATE(),
       UpdatedBy = 'system'
WHERE  Id = 'ED020008-0000-0000-0000-000000000000';

-- ── VERIFY ──────────────────────────────────────────────────────────
SELECT Id, Name, SKU, CategoryId, ImageUrl
FROM   Products
WHERE  StoreId = 'AAAA0002-0000-0000-0000-000000000000'
ORDER  BY CategoryId, Name;
