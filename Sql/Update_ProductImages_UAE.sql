-- =====================================================================
-- SCRIPT: Update_ProductImages_UAE.sql
-- Updates ImageUrl for UAE store (Sufra / Arabic Restaurant)
-- Store: AAAA0004-0000-0000-0000-000000000000
-- Source: Unsplash (free, no auth required, CDN-served)
-- Run: After seed data is loaded
-- =====================================================================

-- ── STARTERS (EC040001) ──────────────────────────────────────────────

-- Hummus — creamy hummus with olive oil drizzle and pita bread
UPDATE Products
SET    ImageUrl  = 'https://images.unsplash.com/photo-1585937421612-70a008356fbe?w=400&q=80',
       UpdatedAt = GETUTCDATE(),
       UpdatedBy = 'system'
WHERE  Id = 'ED040001-0000-0000-0000-000000000000';

-- Fattoush Salad — colourful Levantine salad with crispy bread chips
UPDATE Products
SET    ImageUrl  = 'https://images.unsplash.com/photo-1512621776951-a57141f2eefd?w=400&q=80',
       UpdatedAt = GETUTCDATE(),
       UpdatedBy = 'system'
WHERE  Id = 'ED040002-0000-0000-0000-000000000000';

-- ── HOT MEZZE (EC040002) ─────────────────────────────────────────────

-- Kibbeh — golden fried lamb and bulgur croquettes on plate
UPDATE Products
SET    ImageUrl  = 'https://images.unsplash.com/photo-1598511726623-d2e9996892f0?w=400&q=80',
       UpdatedAt = GETUTCDATE(),
       UpdatedBy = 'system'
WHERE  Id = 'ED040003-0000-0000-0000-000000000000';

-- Cheese Sambousek — crispy golden pastry parcels with cheese filling
UPDATE Products
SET    ImageUrl  = 'https://images.unsplash.com/photo-1601050690597-df0568f70950?w=400&q=80',
       UpdatedAt = GETUTCDATE(),
       UpdatedBy = 'system'
WHERE  Id = 'ED040004-0000-0000-0000-000000000000';

-- ── MAIN COURSE (EC040003) ───────────────────────────────────────────

-- Lamb Ouzi — slow-roasted lamb on saffron rice, garnished with nuts
UPDATE Products
SET    ImageUrl  = 'https://images.unsplash.com/photo-1544025162-d76694265947?w=400&q=80',
       UpdatedAt = GETUTCDATE(),
       UpdatedBy = 'system'
WHERE  Id = 'ED040005-0000-0000-0000-000000000000';

-- Chicken Mandi — smoked chicken over spiced basmati rice
UPDATE Products
SET    ImageUrl  = 'https://images.unsplash.com/photo-1633945274405-b6c8069047b0?w=400&q=80',
       UpdatedAt = GETUTCDATE(),
       UpdatedBy = 'system'
WHERE  Id = 'ED040006-0000-0000-0000-000000000000';

-- ── GRILL & SEAFOOD (EC040004) ───────────────────────────────────────

-- Grilled Hammour — whole charcoal-grilled fish with herbs and lemon
UPDATE Products
SET    ImageUrl  = 'https://images.unsplash.com/photo-1519708227418-c8fd9a32b7a2?w=400&q=80',
       UpdatedAt = GETUTCDATE(),
       UpdatedBy = 'system'
WHERE  Id = 'ED040007-0000-0000-0000-000000000000';

-- Mixed Grill Platter — lamb chops, kofta, shish tawook on large platter
UPDATE Products
SET    ImageUrl  = 'https://images.unsplash.com/photo-1529694157872-4e0c0f3b238b?w=400&q=80',
       UpdatedAt = GETUTCDATE(),
       UpdatedBy = 'system'
WHERE  Id = 'ED040008-0000-0000-0000-000000000000';

-- ── DESSERTS & DRINKS (EC040005) ─────────────────────────────────────

-- Kunafa — shredded pastry with sweet cheese and golden syrup
UPDATE Products
SET    ImageUrl  = 'https://images.unsplash.com/photo-1619894991209-9f9694be045a?w=400&q=80',
       UpdatedAt = GETUTCDATE(),
       UpdatedBy = 'system'
WHERE  Id = 'ED040009-0000-0000-0000-000000000000';

-- Jallab — deep red rose water grape juice with pine nuts
UPDATE Products
SET    ImageUrl  = 'https://images.unsplash.com/photo-1497534446932-c925b458314e?w=400&q=80',
       UpdatedAt = GETUTCDATE(),
       UpdatedBy = 'system'
WHERE  Id = 'ED040010-0000-0000-0000-000000000000';

-- ── VERIFY ──────────────────────────────────────────────────────────
SELECT Id, Name, SKU, CategoryId, ImageUrl
FROM   Products
WHERE  StoreId = 'AAAA0004-0000-0000-0000-000000000000'
ORDER  BY CategoryId, Name;
