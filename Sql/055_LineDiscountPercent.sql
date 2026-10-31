-- =====================================================================
-- 055_LineDiscountPercent.sql
--
-- WHAT THIS DOES
--   QuoteItems.DiscountPercent    NEW  DECIMAL(5,2) NULL
--   InvoiceLines.DiscountPercent  NEW  DECIMAL(5,2) NULL
--
-- Two new nullable columns and nothing else. No column is retyped, no
-- constraint is dropped, no existing value is touched. After 052 that is
-- worth saying out loud.
--
-- WHY NULLABLE, AND WHY NOT DEFAULT 0
--   NULL means "the discount on this line was typed as an AMOUNT" — which
--   includes every line that exists today. A default of 0 would make
--   "discounted by a round number" and "not discounted" the same value,
--   and the screens and the PDFs would have no way to tell whether to
--   print "-10%" or a figure. The distinction is the entire point of the
--   column.
--
-- WHY DECIMAL(5,2)
--   0.00 to 100.00 with two places, matching
--   LineDiscounts.PercentDecimals. A CHECK constraint enforces the range
--   at the database as well, because LineDiscount is still what every
--   total is worked out from and a percentage outside 0-100 could only
--   ever mean a bug upstream.
--
-- LineDiscount IS UNCHANGED and is still the only figure any total is
-- built from. When a percentage is present the API recomputes the amount
-- from it before storing either — see LineDiscounts.Resolve — so the two
-- columns cannot drift.
--
-- IS IT SAFE TO RE-RUN
--   Yes. Both adds and both constraints are guarded.
--
-- HOW TO RUN IT
--   Leave @Commit = 0 the first time, read the grids, then set it to 1.
--
-- ORDER OF OPERATIONS: a column added by this script cannot be referenced
-- by name later in the same batch, so every statement that mentions
-- DiscountPercent after the ALTERs goes through sp_executesql.
-- =====================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit BIT = 0;      -- <<<<<< set to 1 when the grids look right

DECLARE @Touched TABLE (
    Seq        INT IDENTITY(1,1),
    Step       NVARCHAR(30),
    [Table]    SYSNAME,
    ObjectName SYSNAME NULL,
    Detail     NVARCHAR(400) NULL
);

BEGIN TRAN;

-- ─────────────────────────────────────────────────────────────────────
-- 1. QuoteItems.DiscountPercent
-- ─────────────────────────────────────────────────────────────────────
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.QuoteItems') AND name = 'DiscountPercent'
)
BEGIN
    PRINT 'QuoteItems.DiscountPercent -> added';
    ALTER TABLE dbo.QuoteItems ADD DiscountPercent DECIMAL(5,2) NULL;
    INSERT INTO @Touched (Step, [Table], ObjectName, Detail)
        VALUES ('added column', 'QuoteItems', 'DiscountPercent', 'DECIMAL(5,2) NULL');
END
ELSE
    PRINT 'QuoteItems.DiscountPercent already exists - skipped';

-- ─────────────────────────────────────────────────────────────────────
-- 2. InvoiceLines.DiscountPercent
-- ─────────────────────────────────────────────────────────────────────
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.InvoiceLines') AND name = 'DiscountPercent'
)
BEGIN
    PRINT 'InvoiceLines.DiscountPercent -> added';
    ALTER TABLE dbo.InvoiceLines ADD DiscountPercent DECIMAL(5,2) NULL;
    INSERT INTO @Touched (Step, [Table], ObjectName, Detail)
        VALUES ('added column', 'InvoiceLines', 'DiscountPercent', 'DECIMAL(5,2) NULL');
END
ELSE
    PRINT 'InvoiceLines.DiscountPercent already exists - skipped';

-- ─────────────────────────────────────────────────────────────────────
-- 3. Range checks -- 0 to 100, or NULL.
--
--    Through sp_executesql: the columns above may have been created by
--    THIS batch, and SQL Server binds column names when it parses.
--
--    Every existing row is NULL, so WITH CHECK is always safe here.
-- ─────────────────────────────────────────────────────────────────────
IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID('dbo.QuoteItems')
      AND name = 'CK_QuoteItems_DiscountPercent'
)
BEGIN
    EXEC sp_executesql N'
        ALTER TABLE dbo.QuoteItems WITH CHECK
            ADD CONSTRAINT CK_QuoteItems_DiscountPercent
            CHECK (DiscountPercent IS NULL OR (DiscountPercent >= 0 AND DiscountPercent <= 100));';
    INSERT INTO @Touched (Step, [Table], ObjectName, Detail)
        VALUES ('created CHECK', 'QuoteItems', 'CK_QuoteItems_DiscountPercent', 'NULL or 0-100');
    PRINT 'QuoteItems: CK_QuoteItems_DiscountPercent created';
END
ELSE
    PRINT 'QuoteItems: CK_QuoteItems_DiscountPercent already exists - skipped';

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID('dbo.InvoiceLines')
      AND name = 'CK_InvoiceLines_DiscountPercent'
)
BEGIN
    EXEC sp_executesql N'
        ALTER TABLE dbo.InvoiceLines WITH CHECK
            ADD CONSTRAINT CK_InvoiceLines_DiscountPercent
            CHECK (DiscountPercent IS NULL OR (DiscountPercent >= 0 AND DiscountPercent <= 100));';
    INSERT INTO @Touched (Step, [Table], ObjectName, Detail)
        VALUES ('created CHECK', 'InvoiceLines', 'CK_InvoiceLines_DiscountPercent', 'NULL or 0-100');
    PRINT 'InvoiceLines: CK_InvoiceLines_DiscountPercent created';
END
ELSE
    PRINT 'InvoiceLines: CK_InvoiceLines_DiscountPercent already exists - skipped';

-- =====================================================================
-- VERIFICATION
-- =====================================================================
PRINT '';
PRINT '=== 0. What this run changed ===';
SELECT Seq, Step, [Table], ObjectName, Detail FROM @Touched ORDER BY Seq;

PRINT '';
PRINT '=== 1. The new columns ===';

SELECT
    OBJECT_NAME(c.object_id) AS [Table],
    c.name                   AS [Column],
    t.name                   AS [Type],
    c.precision              AS [Precision],
    c.scale                  AS [Scale],
    c.is_nullable            AS [Nullable]
FROM sys.columns c
JOIN sys.types t ON t.user_type_id = c.user_type_id
WHERE c.object_id IN (OBJECT_ID('dbo.QuoteItems'), OBJECT_ID('dbo.InvoiceLines'))
  AND c.name IN ('LineDiscount', 'DiscountPercent')
ORDER BY [Table], [Column];

PRINT '';
PRINT '=== 2. CHECK constraints on these two tables ===';

SELECT
    OBJECT_NAME(cc.parent_object_id) AS [Table],
    cc.name                          AS [Constraint],
    cc.definition                    AS [Definition],
    cc.is_not_trusted                AS [NotTrusted]
FROM sys.check_constraints cc
WHERE cc.parent_object_id IN (OBJECT_ID('dbo.QuoteItems'), OBJECT_ID('dbo.InvoiceLines'))
ORDER BY [Table], [Constraint];

PRINT '';
PRINT '=== 3. Every existing line should be NULL (typed as an amount) ===';
PRINT '    WithPercent must be 0 straight after the migration: this script';
PRINT '    adds a column, it never fills one in.';

EXEC sp_executesql N'
    SELECT ''QuoteItems'' AS [Table],
           COUNT(*)                                                       AS [Lines],
           SUM(CASE WHEN DiscountPercent IS NOT NULL THEN 1 ELSE 0 END)   AS [WithPercent],
           SUM(CASE WHEN LineDiscount > 0 THEN 1 ELSE 0 END)              AS [WithDiscount]
      FROM dbo.QuoteItems WHERE IsDeleted = 0
    UNION ALL
    SELECT ''InvoiceLines'',
           COUNT(*),
           SUM(CASE WHEN DiscountPercent IS NOT NULL THEN 1 ELSE 0 END),
           SUM(CASE WHEN LineDiscount > 0 THEN 1 ELSE 0 END)
      FROM dbo.InvoiceLines WHERE IsDeleted = 0;
';

PRINT '';
PRINT '=== 4. Discounted lines, and what percentage they work out to ===';
PRINT '    Nothing is changed here -- this is the figure the editor will';
PRINT '    show if somebody switches one of these lines to % mode.';

EXEC sp_executesql N'
    SELECT TOP 20
        q.Number                AS [Quote],
        qi.Name                 AS [Line],
        qi.UnitPrice,
        qi.Quantity,
        qi.LineDiscount,
        qi.DiscountPercent      AS [StoredPercent],
        CASE WHEN qi.UnitPrice * qi.Quantity > 0
             THEN CAST(ROUND(qi.LineDiscount / (qi.UnitPrice * qi.Quantity) * 100, 2) AS DECIMAL(6,2))
             ELSE NULL END      AS [ImpliedPercent]
      FROM dbo.QuoteItems qi
      JOIN dbo.Quotes q ON q.Id = qi.QuoteId
     WHERE qi.IsDeleted = 0 AND qi.LineDiscount > 0
     ORDER BY q.CreatedAtUtc DESC;
';

-- =====================================================================
IF @Commit = 1
BEGIN
    COMMIT;
    PRINT '';
    PRINT '>>> COMMITTED.';
END
ELSE
BEGIN
    ROLLBACK;
    PRINT '';
    PRINT '>>> ROLLED BACK (@Commit = 0). Check the grids, then set @Commit = 1 and run again.';
END
