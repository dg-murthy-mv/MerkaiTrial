/* =====================================================================
   037a_CountryNumberFormat.sql
   ---------------------------------------------------------------------
   WHAT YOUR SELECT SHOWED

       AE  United Arab Emirates  AED  #,##0.00  2
       IN  India                 INR  #,##0.00  2
       PH  Philippines           PHP  #,##0.00  2
       TH  Thailand              THB  #,##0.00  2

   All four hold a .NET FORMAT PATTERN, not a culture name.
   FormatCurrency's LooksLikePattern branch therefore uses the pattern
   verbatim — and `#,##0.00` means "group every three digits".

   ONLY INDIA IS ACTUALLY WRONG
     Thailand, the Philippines and the UAE all group in threes, so
     `#,##0.00` is already producing the right answer for them. There is
     nothing to fix.

     India does not. Indian numbering groups the last three digits and
     then every TWO after that — the lakh/crore system:

         stored pattern #,##0.00  ->  ₹1,234,567.00     WRONG
         culture en-IN            ->  ₹12,34,567.00     RIGHT

     To an Indian customer the first one reads as foreign software. It is
     the single most visible localisation detail in the product.

   SO THIS SCRIPT CHANGES ONE ROW BY DEFAULT.
   The other three are offered as an optional consistency pass, off by
   default — see @AlsoNormaliseOthers.

   WHY NOT th-TH AND ar-AE
     Two traps worth knowing about, since the obvious choice is wrong in
     both cases:

     th-TH   .NET's Thai culture uses the BUDDHIST calendar. The moment
             that culture name reaches any date formatting, 2026 renders
             as 2569 and your Thai customers see dates 543 years out.
             Numbers are identical to en-TH anyway, so there is no upside.

     ar-AE   Fine for digits, but it carries Arabic currency and RTL
             conventions into an English-language UI. en-AE gives the same
             grouping with none of that.

     Hence en-TH / en-PH / en-AE if you want them normalised at all.

   HOW TO RUN
     1. Run as-is. @Commit = 0, so it rolls back. Read Part 1 and Part 3.
     2. Set @Commit = 1 and run again.

   RE-RUN SAFETY
     Idempotent — it only updates rows that do not already hold the target
     value.
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit BIT = 0;                 -- <<<< 0 = rollback. 1 = keep.

-- OFF by default. India is the only row that is wrong; the other three
-- already behave correctly. Turn this on only if you would rather every
-- country carried a culture name so that future markets follow one rule.
DECLARE @AlsoNormaliseOthers BIT = 0;

PRINT '=====================================================';
PRINT ' 037a - Countries.NumberFormat';
PRINT ' @Commit              = ' + CONVERT(varchar(1), @Commit);
PRINT ' @AlsoNormaliseOthers = ' + CONVERT(varchar(1), @AlsoNormaliseOthers);
PRINT '=====================================================';

IF OBJECT_ID(N'dbo.Countries', N'U') IS NULL
BEGIN
    RAISERROR('dbo.Countries does not exist. Stopping.', 16, 1);
    RETURN;
END


/* =====================================================================
   PART 1 - BEFORE, with what each value actually renders
   ===================================================================== */

PRINT '';
PRINT '--- Part 1: before ---';

SELECT
    c.Code,
    c.Name,
    c.CurrencyCode,
    c.CurrencySymbol,
    c.NumberFormat,
    Kind = CASE WHEN c.NumberFormat LIKE '%#%' OR c.NumberFormat LIKE '%0%'
                THEN 'format pattern'
                WHEN c.NumberFormat IS NULL THEN '(null)'
                ELSE 'culture name' END,
    RendersAsToday = CASE c.Code
                       WHEN 'IN' THEN '1,234,567.00   <-- WRONG for India'
                       ELSE '1,234,567.00   (correct)' END,
    ShouldRenderAs = CASE c.Code
                       WHEN 'IN' THEN '12,34,567.00'
                       ELSE '1,234,567.00' END
FROM dbo.Countries c
WHERE c.Code IN ('IN', 'TH', 'PH', 'AE')
ORDER BY c.Code;


/* =====================================================================
   PART 2 - THE TRANSACTION
   ===================================================================== */

DECLARE @FixedIndia int = 0;
DECLARE @FixedOthers int = 0;

BEGIN TRAN;

BEGIN TRY

    -- ── India: the one that matters ──────────────────────────────────
    UPDATE dbo.Countries
    SET NumberFormat = 'en-IN'
    WHERE Code = 'IN'
      AND (NumberFormat IS NULL OR NumberFormat <> 'en-IN');

    SET @FixedIndia = @@ROWCOUNT;
    PRINT '';
    PRINT '  India set to en-IN: ' + CONVERT(varchar(20), @FixedIndia) + ' row(s)';

    -- ── The optional consistency pass ────────────────────────────────
    IF @AlsoNormaliseOthers = 1
    BEGIN
        UPDATE dbo.Countries
        SET NumberFormat = CASE Code
                             WHEN 'TH' THEN 'en-TH'   -- NOT th-TH: Buddhist calendar
                             WHEN 'PH' THEN 'en-PH'
                             WHEN 'AE' THEN 'en-AE'   -- NOT ar-AE: RTL / Arabic conventions
                           END
        WHERE Code IN ('TH', 'PH', 'AE')
          AND (NumberFormat IS NULL
            OR NumberFormat <> CASE Code
                                 WHEN 'TH' THEN 'en-TH'
                                 WHEN 'PH' THEN 'en-PH'
                                 WHEN 'AE' THEN 'en-AE'
                               END);

        SET @FixedOthers = @@ROWCOUNT;
        PRINT '  TH / PH / AE normalised: ' + CONVERT(varchar(20), @FixedOthers) + ' row(s)';
    END
    ELSE
        PRINT '  TH / PH / AE left as-is (@AlsoNormaliseOthers = 0) - they were already correct';


    /* =================================================================
       PART 3 - AFTER
       ================================================================= */

    PRINT '';
    PRINT '--- Part 3: after ---';

    SELECT
        c.Code,
        c.Name,
        c.NumberFormat,
        Kind = CASE WHEN c.NumberFormat LIKE '%#%' OR c.NumberFormat LIKE '%0%'
                    THEN 'format pattern'
                    WHEN c.NumberFormat IS NULL THEN '(null)'
                    ELSE 'culture name' END,
        WillRenderAs = CASE
                         WHEN c.NumberFormat = 'en-IN' THEN '12,34,567.00   (lakh grouping)'
                         ELSE '1,234,567.00' END
    FROM dbo.Countries c
    WHERE c.Code IN ('IN', 'TH', 'PH', 'AE')
    ORDER BY c.Code;

    PRINT '';
    PRINT '--- summary ---';
    PRINT '  India rows changed  : ' + CONVERT(varchar(20), @FixedIndia);
    PRINT '  Other rows changed  : ' + CONVERT(varchar(20), @FixedOthers);


    IF @Commit = 1
    BEGIN
        COMMIT TRAN;
        PRINT '';
        PRINT '*** COMMITTED. ***';
    END
    ELSE
    BEGIN
        ROLLBACK TRAN;
        PRINT '';
        PRINT '*** ROLLED BACK (dry run). Nothing was changed. ***';
        PRINT '*** Set @Commit = 1 at the top and run again to keep it. ***';
    END

END TRY
BEGIN CATCH

    IF XACT_STATE() <> 0
        ROLLBACK TRAN;

    PRINT '';
    PRINT '*** FAILED - rolled back. Nothing was changed. ***';

    DECLARE @ErrMsg  nvarchar(4000) = ERROR_MESSAGE();
    DECLARE @ErrLine int            = ERROR_LINE();
    RAISERROR('037a failed at line %d: %s', 16, 1, @ErrLine, @ErrMsg);

END CATCH;


/* =====================================================================
   HOW TO CHECK IT WORKED, IN THE APP
   ---------------------------------------------------------------------
   Restart the API (CurrentTenantService caches the tenant's formatting
   per request, but the culture lookup itself is cheap and not cached
   across the change), sign in as an Indian tenant, and look at any quote
   or deal value above a lakh.

       before   ₹1,234,567.00
       after    ₹12,34,567.00

   If it still shows the first form, the likely cause is that
   FormatCurrency's LooksLikePattern branch is still matching — check
   that the stored value really is `en-IN` and not `en-IN ` with a
   trailing space, which Part 3's Kind column would show as a culture
   name either way.
   ===================================================================== */
