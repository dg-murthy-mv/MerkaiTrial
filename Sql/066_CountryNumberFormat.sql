-- =====================================================================
-- 066_CountryNumberFormat.sql
--
-- Countries.NumberFormat: PATTERNS -> CULTURE NAMES.
--
-- THE BUG THIS FINALLY CLOSES, from the 033 notes.
--
--   CurrentTenantService.FormatCurrency reads this column and handles
--   both conventions:
--
--     "en-IN"     -> CultureInfo -> amount.ToString("N2", culture)
--     "#,##0.00"  -> pattern     -> amount.ToString(pattern, Invariant)
--
--   The pattern branch cannot express Indian lakh grouping AT ALL. In a
--   .NET custom numeric format string a ',' between digit placeholders
--   only ENABLES grouping; the GROUP SIZES come from the culture's
--   NumberFormatInfo.NumberGroupSizes. InvariantCulture is {3}, so
--   "#,##,##0.00" renders 332,155.84 and never 3,32,155.84.
--
--   So every tenant whose country still holds a pattern gets western
--   grouping on every figure in the product, no matter what the code
--   does. An Indian customer reads 332,155.84 where their accountant,
--   their bank and their tax return all say 3,32,155.84.
--
-- THE MAPPING. By country code, because a pattern carries no record of
-- which culture it was meant to be:
--
--     IN -> en-IN      lakh grouping {3,2}
--     TH -> th-TH
--     PH -> en-PH
--     AE -> en-AE
--     everything else -> en-US
--
--   en-US for the rest is a deliberate, agreed default rather than a
--   claim about those countries. They are markets you do not sell in; if
--   one becomes real, set its row properly then. Western grouping is
--   what those rows get today from the pattern branch anyway, so this
--   changes nothing for them except making it honest and queryable.
--
-- ONLY ROWS THAT HOLD A PATTERN, OR NOTHING. A row already holding a
-- culture name is left alone — including one somebody set by hand to
-- something better than this script would choose. A pattern is detected
-- by the presence of '#' or '0', neither of which appears in any .NET
-- culture name.
--
-- SAFE TO RE-RUN. The WHERE clause excludes rows already converted.
--
-- ⚠ @Commit IS 0. Run it, read the three grids — especially the first,
-- which shows what you have BEFORE anything changes — then set it to 1
-- and run again.
-- =====================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit BIT = 0;

BEGIN TRAN;

BEGIN TRY

    IF OBJECT_ID('dbo.Countries') IS NULL
        THROW 51066, 'dbo.Countries does not exist. Wrong database?', 1;

    -- =================================================================
    -- BEFORE — what is actually in the column right now
    --
    -- Read this one first. If it shows every row already holding a
    -- culture name, there is nothing to do and the UPDATE below will
    -- report 0 rows, which is the correct outcome, not a failure.
    -- =================================================================
    PRINT '=== BEFORE: what Countries.NumberFormat holds today ===';

    SELECT  c.Code,
            c.Name,
            c.CurrencyCode,
            c.NumberFormat,
            CASE
                WHEN c.NumberFormat IS NULL                      THEN 'NULL - falls back to InvariantCulture'
                WHEN c.NumberFormat LIKE '%[#0]%'                THEN 'PATTERN - western grouping only'
                ELSE                                                  'culture name - already correct'
            END AS Verdict
      FROM  dbo.Countries c
     ORDER BY
            CASE WHEN c.Code IN ('IN','TH','PH','AE') THEN 0 ELSE 1 END,
            c.Code;

    -- =================================================================
    -- THE CONVERSION
    --
    -- One UPDATE with a CASE rather than five UPDATEs, so a row can only
    -- be touched once and the row count below means what it says.
    -- =================================================================

    DECLARE @Changed INT = 0;

    UPDATE  c
       SET  c.NumberFormat =
                CASE c.Code
                    WHEN 'IN' THEN 'en-IN'
                    WHEN 'TH' THEN 'th-TH'
                    WHEN 'PH' THEN 'en-PH'
                    WHEN 'AE' THEN 'en-AE'
                    ELSE           'en-US'
                END,
            c.UpdatedAtUtc = SYSUTCDATETIME(),
            c.UpdatedBy    = 'migration-066'
      FROM  dbo.Countries c
     WHERE  c.NumberFormat IS NULL
        OR  c.NumberFormat LIKE '%[#0]%';   -- a pattern; culture names have neither

    SET @Changed = @@ROWCOUNT;

    PRINT '';
    PRINT 'Rows converted: ' + CAST(@Changed AS NVARCHAR(10));

    -- =================================================================
    -- AFTER
    -- =================================================================
    PRINT '';
    PRINT '=== AFTER: your four markets ===';

    SELECT  c.Code,
            c.Name,
            c.CurrencyCode,
            c.NumberFormat,
            CASE c.Code
                WHEN 'IN' THEN '1,00,000.00  (lakh grouping {3,2})'
                WHEN 'TH' THEN '100,000.00'
                WHEN 'PH' THEN '100,000.00'
                WHEN 'AE' THEN '100,000.00'
                ELSE           '100,000.00'
            END AS HowOneHundredThousandNowPrints
      FROM  dbo.Countries c
     WHERE  c.Code IN ('IN','TH','PH','AE')
     ORDER BY c.Code;

    PRINT '';
    PRINT '=== ANYTHING STILL NOT A CULTURE NAME (should be empty) ===';

    SELECT  c.Code, c.Name, c.NumberFormat
      FROM  dbo.Countries c
     WHERE  c.NumberFormat IS NULL
        OR  c.NumberFormat LIKE '%[#0]%';

    PRINT '';
    PRINT 'If that last grid has rows, they were not matched by the WHERE';
    PRINT 'clause and want looking at by hand.';
    PRINT '';
    PRINT 'NOTE: FormatCurrency caches resolved cultures per PROCESS, so';
    PRINT 'restart both hosts after committing or the old values stay in';
    PRINT 'memory until they do.';

END TRY
BEGIN CATCH
    PRINT '=== FAILED ===';
    PRINT ERROR_MESSAGE();
    IF @@TRANCOUNT > 0 ROLLBACK;
    THROW;
END CATCH

IF @Commit = 1
BEGIN
    COMMIT;
    PRINT '>>> COMMITTED';
END
ELSE
BEGIN
    ROLLBACK;
    PRINT '>>> ROLLED BACK (set @Commit = 1 at the top and run again)';
END
GO
