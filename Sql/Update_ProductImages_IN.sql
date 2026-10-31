-- =====================================================================
-- SCRIPT: Update_ProductImages_IN.sql
-- Updates ImageUrl for India store (BrewVision / MadeeVision India)
-- Store: AAAA0001-0000-0000-0000-000000000000
-- Source: Unsplash (free, no auth required, CDN-served)
-- Run: After seed data is loaded
-- =====================================================================

-- ── HOT DRINKS (EC010001) ────────────────────────────────────────────

-- Espresso — dark rich espresso shot in small ceramic cup
UPDATE Products
SET    ImageUrl  = 'https://images.unsplash.com/photo-1510707577719-ae7c14805e3a?w=400&q=80',
       UpdatedAt = GETUTCDATE(),
       UpdatedBy = 'system'
WHERE  Id = 'ED010001-0000-0000-0000-000000000000';

-- Cappuccino — classic cappuccino with latte art foam
UPDATE Products
SET    ImageUrl  = 'https://images.unsplash.com/photo-1572442388796-11668a67e53d?w=400&q=80',
       UpdatedAt = GETUTCDATE(),
       UpdatedBy = 'system'
WHERE  Id = 'ED010002-0000-0000-0000-000000000000';

-- Masala Chai — spiced Indian chai in glass with ginger
UPDATE Products
SET    ImageUrl  = 'https://images.unsplash.com/photo-1571934811356-5cc061b6821f?w=400&q=80',
       UpdatedAt = GETUTCDATE(),
       UpdatedBy = 'system'
WHERE  Id = 'ED010003-0000-0000-0000-000000000000';

-- ── COLD DRINKS (EC010002) ───────────────────────────────────────────

-- Cold Coffee — iced cold coffee with milk in tall glass
UPDATE Products
SET    ImageUrl  = 'https://images.unsplash.com/photo-1461023058943-07fcbe16d735?w=400&q=80',
       UpdatedAt = GETUTCDATE(),
       UpdatedBy = 'system'
WHERE  Id = 'ED010004-0000-0000-0000-000000000000';

-- Iced Matcha Latte — green matcha layered with oat milk over ice
UPDATE Products
SET    ImageUrl  = 'https://images.unsplash.com/photo-1536256263959-770b48d82b0a?w=400&q=80',
       UpdatedAt = GETUTCDATE(),
       UpdatedBy = 'system'
WHERE  Id = 'ED010005-0000-0000-0000-000000000000';

-- ── FOOD & SNACKS (EC010003) ─────────────────────────────────────────

-- Veg Sandwich — grilled multigrain sandwich with colourful veggies
UPDATE Products
SET    ImageUrl  = 'https://images.unsplash.com/photo-1528735602780-2552fd46c7af?w=400&q=80',
       UpdatedAt = GETUTCDATE(),
       UpdatedBy = 'system'
WHERE  Id = 'ED010006-0000-0000-0000-000000000000';

-- Butter Croissant — golden flaky French-style croissant
UPDATE Products
SET    ImageUrl  = 'https://images.unsplash.com/photo-1555507036-ab1f4038808a?w=400&q=80',
       UpdatedAt = GETUTCDATE(),
       UpdatedBy = 'system'
WHERE  Id = 'ED010007-0000-0000-0000-000000000000';

-- ── DESSERTS (EC010004) ──────────────────────────────────────────────

-- Blueberry Cheesecake — NY-style baked cheesecake with blueberry compote
UPDATE Products
SET    ImageUrl  = 'https://images.unsplash.com/photo-1533134242443-d4fd215305ad?w=400&q=80',
       UpdatedAt = GETUTCDATE(),
       UpdatedBy = 'system'
WHERE  Id = 'ED010008-0000-0000-0000-000000000000';

-- ── VERIFY ──────────────────────────────────────────────────────────
SELECT Id, Name, SKU, CategoryId, ImageUrl
FROM   Products
WHERE  StoreId = 'AAAA0001-0000-0000-0000-000000000000'
ORDER  BY CategoryId, Name;
