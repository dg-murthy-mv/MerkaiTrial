-- ══════════════════════════════════════════════════════════════════════
-- FIX: Currency symbols stored as '?' instead of ฿ / ₹ / ₱
--
-- ROOT CAUSE: the original seed script used plain string literals
-- ('฿', '₹', '₱') instead of Unicode literals (N'฿', N'₹', N'₱').
-- SQL Server converts a non-N-prefixed literal to the database's
-- default collation codepage BEFORE the INSERT even runs — so the
-- glyph is already lost by the time it reaches the column. This is
-- unrelated to whether PlanPricings.CurrencySymbol is nvarchar or
-- varchar; it happens at literal-parsing time either way.
-- ══════════════════════════════════════════════════════════════════════

-- ── STEP 0: Diagnostic — confirm the column can actually hold Unicode ──
-- Run this first. DATA_TYPE should be 'nvarchar', not 'varchar'.
-- If it comes back as 'varchar', the ALTER TABLE below is required —
-- the UPDATE statements alone won't be enough (varchar can't store
-- these characters no matter how they're inserted).
SELECT
    COLUMN_NAME,
    DATA_TYPE,
    CHARACTER_MAXIMUM_LENGTH
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME = 'PlanPricings'
  AND COLUMN_NAME IN ('CurrencySymbol', 'CountryName');
GO

-- ── STEP 0b: Only run this if STEP 0 showed 'varchar' above ────────────
-- Uncomment and run if needed. Safe to run even if already nvarchar
-- (just re-declares it), but skip it if you're not sure — no harm in
-- asking first.
--
-- ALTER TABLE [dbo].[PlanPricings]
--     ALTER COLUMN [CurrencySymbol] NVARCHAR(10) NOT NULL;
-- GO

-- ══════════════════════════════════════════════════════════════════════
-- STEP 1: Repair existing corrupted rows
-- $ (USD) and AED are untouched — they were never corrupted.
-- Only THB/INR/PHP symbols need fixing.
-- ══════════════════════════════════════════════════════════════════════
UPDATE [dbo].[PlanPricings] SET CurrencySymbol = N'฿' WHERE CurrencyCode = 'THB';
UPDATE [dbo].[PlanPricings] SET CurrencySymbol = N'₹' WHERE CurrencyCode = 'INR';
UPDATE [dbo].[PlanPricings] SET CurrencySymbol = N'₱' WHERE CurrencyCode = 'PHP';
GO

-- ── STEP 2: Verify the fix ──────────────────────────────────────────────
SELECT PlanId, CurrencyCode, CountryName, CurrencySymbol, MonthlyPrice, AnnualPrice
FROM [dbo].[PlanPricings]
ORDER BY PlanId, SortOrder;
GO

-- ══════════════════════════════════════════════════════════════════════
-- STEP 3 (reference only): corrected seed script for future/fresh
-- environments. Same data as before — every Unicode literal now has
-- the N prefix. Safe to keep as your source-of-truth migration script
-- going forward. Not meant to be re-run against a DB that already has
-- these rows (would duplicate them) — Steps 1–2 above already fixed
-- the live data.
-- ══════════════════════════════════════════════════════════════════════

-- ── TRIAL (always free everywhere) ────────────────────────────────
-- INSERT INTO [dbo].[PlanPricings] (PlanId, CurrencyCode, CountryCode, CountryName, CurrencySymbol, MonthlyPrice, AnnualPrice, SortOrder, CreatedBy)
-- SELECT Id, 'USD', '*',  N'International', N'$',    0, 0, 1, 'Migration' FROM Plans WHERE Name = 'trial'
-- UNION ALL
-- SELECT Id, 'THB', 'TH', N'Thailand',      N'฿',   0, 0, 2, 'Migration' FROM Plans WHERE Name = 'trial'
-- UNION ALL
-- SELECT Id, 'INR', 'IN', N'India',         N'₹',   0, 0, 3, 'Migration' FROM Plans WHERE Name = 'trial'
-- UNION ALL
-- SELECT Id, 'PHP', 'PH', N'Philippines',   N'₱',   0, 0, 4, 'Migration' FROM Plans WHERE Name = 'trial'
-- UNION ALL
-- SELECT Id, 'AED', 'AE', N'UAE',           N'AED', 0, 0, 5, 'Migration' FROM Plans WHERE Name = 'trial';

-- ── STARTER ───────────────────────────────────────────────────────
-- INSERT INTO [dbo].[PlanPricings] (PlanId, CurrencyCode, CountryCode, CountryName, CurrencySymbol, MonthlyPrice, AnnualPrice, SortOrder, CreatedBy)
-- SELECT Id, 'USD', '*',  N'International', N'$',    9.00,   90.00,  1, 'Migration' FROM Plans WHERE Name = 'starter'
-- UNION ALL
-- SELECT Id, 'THB', 'TH', N'Thailand',      N'฿',   299.00, 2990.00,2, 'Migration' FROM Plans WHERE Name = 'starter'
-- UNION ALL
-- SELECT Id, 'INR', 'IN', N'India',         N'₹',   749.00, 7490.00,3, 'Migration' FROM Plans WHERE Name = 'starter'
-- UNION ALL
-- SELECT Id, 'PHP', 'PH', N'Philippines',   N'₱',   499.00, 4990.00,4, 'Migration' FROM Plans WHERE Name = 'starter'
-- UNION ALL
-- SELECT Id, 'AED', 'AE', N'UAE',           N'AED', 33.00,  330.00, 5, 'Migration' FROM Plans WHERE Name = 'starter';

-- ── PROFESSIONAL ──────────────────────────────────────────────────
-- INSERT INTO [dbo].[PlanPricings] (PlanId, CurrencyCode, CountryCode, CountryName, CurrencySymbol, MonthlyPrice, AnnualPrice, SortOrder, CreatedBy)
-- SELECT Id, 'USD', '*',  N'International', N'$',    29.00,   290.00,  1, 'Migration' FROM Plans WHERE Name = 'professional'
-- UNION ALL
-- SELECT Id, 'THB', 'TH', N'Thailand',      N'฿',   990.00,  9900.00, 2, 'Migration' FROM Plans WHERE Name = 'professional'
-- UNION ALL
-- SELECT Id, 'INR', 'IN', N'India',         N'₹',   2490.00, 24900.00,3, 'Migration' FROM Plans WHERE Name = 'professional'
-- UNION ALL
-- SELECT Id, 'PHP', 'PH', N'Philippines',   N'₱',   1690.00, 16900.00,4, 'Migration' FROM Plans WHERE Name = 'professional'
-- UNION ALL
-- SELECT Id, 'AED', 'AE', N'UAE',           N'AED', 107.00,  1070.00, 5, 'Migration' FROM Plans WHERE Name = 'professional';

-- ── ENTERPRISE ────────────────────────────────────────────────────
-- INSERT INTO [dbo].[PlanPricings] (PlanId, CurrencyCode, CountryCode, CountryName, CurrencySymbol, MonthlyPrice, AnnualPrice, SortOrder, CreatedBy)
-- SELECT Id, 'USD', '*',  N'International', N'$',    79.00,   790.00,  1, 'Migration' FROM Plans WHERE Name = 'enterprise'
-- UNION ALL
-- SELECT Id, 'THB', 'TH', N'Thailand',      N'฿',   2790.00, 27900.00,2, 'Migration' FROM Plans WHERE Name = 'enterprise'
-- UNION ALL
-- SELECT Id, 'INR', 'IN', N'India',         N'₹',   6490.00, 64900.00,3, 'Migration' FROM Plans WHERE Name = 'enterprise'
-- UNION ALL
-- SELECT Id, 'PHP', 'PH', N'Philippines',   N'₱',   4490.00, 44900.00,4, 'Migration' FROM Plans WHERE Name = 'enterprise'
-- UNION ALL
-- SELECT Id, 'AED', 'AE', N'UAE',           N'AED', 290.00,  2900.00, 5, 'Migration' FROM Plans WHERE Name = 'enterprise';
-- GO
