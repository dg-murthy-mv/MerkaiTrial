-- =====================================================================
-- 051_ProductCatalog.sql
--
-- Adds two columns to dbo.Products:
--
--   UnitOfMeasure  nvarchar(20)  NOT NULL  default 'unit'
--   TaxCode        nvarchar(20)  NULL
--
-- WHY
--
--   UnitOfMeasure is what a QUANTITY MEANS. Until now a quote line's
--   quantity was a bare integer with no unit attached, so an interiors
--   firm quoting 12.5 m² of flooring had nowhere to put the 12.5 OR the
--   m²: the real quantity went into the line description as prose and the
--   unit price was fudged so the line total came out right.
--
--   The line total was correct. Everything else about WHAT was sold was
--   not — every per-unit margin, every "how much flooring did we quote
--   this quarter", every report that counts things.
--
--   TaxCode is the classification an invoice must carry in some markets —
--   HSN for goods and SAC for services under Indian GST. It lives on the
--   product because that is where the classification belongs, and it is
--   free text because the code sets are long, they change, and they
--   differ by country.
--
-- WHAT THIS DOES NOT DO
--
--   It does NOT change QuoteItem.Quantity or InvoiceLine.Quantity, which
--   are still `int`. Those come in the next round, together, because a
--   quote that can say 12.5 against an invoice that rounds it to 12 would
--   be worse than neither.
--
-- EVERY EXISTING ROW becomes 'unit', which is exactly what it already
-- meant and which prints as nothing at all beside a number.
--
-- SAFE TO RE-RUN. Both ALTERs are guarded; a second run finds the columns
-- and does nothing.
--
-- HOW TO RUN
--   1. Run it as-is. @Commit = 0, so it rolls back and only shows you
--      what it WOULD do.
--   2. Read the grids.
--   3. Set @Commit = 1 and run it again.
-- =====================================================================

SET NOCOUNT ON;

DECLARE @Commit BIT = 0;

BEGIN TRAN;

IF OBJECT_ID(N'dbo.Products', N'U') IS NULL
BEGIN
    PRINT 'dbo.Products does not exist.';
    ROLLBACK TRAN;
    RETURN;
END;

PRINT '--- BEFORE ---';

SELECT
    ColumnName = c.name,
    TypeName   = t.name,
    MaxLength  = c.max_length,
    IsNullable = c.is_nullable
FROM sys.columns c
JOIN sys.types   t ON t.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID(N'dbo.Products')
  AND c.name IN (N'Type', N'UnitOfMeasure', N'TaxRate', N'TaxCode')
ORDER BY c.name;

-- ── UnitOfMeasure ────────────────────────────────────────────────────
--
-- Added WITH a default so the NOT NULL constraint can go on in one step
-- without a separate UPDATE pass. On a large table that also avoids
-- rewriting every page: SQL Server 2012+ adds a NOT NULL column with a
-- constant default as a metadata-only operation.
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.Products') AND name = N'UnitOfMeasure')
BEGIN
    ALTER TABLE dbo.Products
        ADD UnitOfMeasure nvarchar(20) NOT NULL
            CONSTRAINT DF_Products_UnitOfMeasure DEFAULT (N'unit');

    PRINT 'Added Products.UnitOfMeasure (NOT NULL, default ''unit'').';
END
ELSE
    PRINT 'Products.UnitOfMeasure already exists — left alone.';

-- ── TaxCode ──────────────────────────────────────────────────────────
--
-- Nullable on purpose. A Thai or Philippine tenant will never fill this
-- in, and NULL says "not applicable" where an empty string would not.
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.Products') AND name = N'TaxCode')
BEGIN
    ALTER TABLE dbo.Products ADD TaxCode nvarchar(20) NULL;

    PRINT 'Added Products.TaxCode (nullable).';
END
ELSE
    PRINT 'Products.TaxCode already exists — left alone.';

-- ── Tidy any unrecognised Type ───────────────────────────────────────
--
-- Type was a free string with no constants behind it and two separate
-- copies of the allowed list in the page models, so a value outside the
-- three is possible. ProductKinds.Normalise() would render such a row as
-- "Product" anyway; this makes the stored value agree with what the
-- screen shows.
--
-- Anything through sp_executesql because UnitOfMeasure is referenced in
-- the same batch it was added in.
DECLARE @FixedTypes int = 0;

EXEC sp_executesql N'
    UPDATE dbo.Products
    SET Type = N''Product''
    WHERE Type IS NULL
       OR Type NOT IN (N''Product'', N''Service'', N''Subscription'');

    SELECT @out = @@ROWCOUNT;',
    N'@out int OUTPUT', @out = @FixedTypes OUTPUT;

PRINT 'Rows whose Type was not one of the three: ' + CONVERT(varchar(10), @FixedTypes);

-- ── AFTER ────────────────────────────────────────────────────────────
PRINT '--- AFTER ---';

SELECT
    ColumnName = c.name,
    TypeName   = t.name,
    MaxLength  = c.max_length,
    IsNullable = c.is_nullable
FROM sys.columns c
JOIN sys.types   t ON t.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID(N'dbo.Products')
  AND c.name IN (N'Type', N'UnitOfMeasure', N'TaxRate', N'TaxCode')
ORDER BY c.name;

PRINT '--- THE CATALOGUE AS IT STANDS ---';

EXEC sp_executesql N'
    SELECT TOP (50)
        Name,
        Sku,
        Category,
        Type,
        UnitOfMeasure,
        ListPrice,
        TaxRate,
        TaxCode,
        IsActive
    FROM dbo.Products
    WHERE IsDeleted = 0
    ORDER BY Category, Name;';

PRINT '--- UNITS IN USE ---';

EXEC sp_executesql N'
    SELECT UnitOfMeasure, Products = COUNT(*)
    FROM dbo.Products
    WHERE IsDeleted = 0
    GROUP BY UnitOfMeasure
    ORDER BY COUNT(*) DESC;';

IF @Commit = 1
BEGIN
    COMMIT TRAN;
    PRINT 'COMMITTED.';
END
ELSE
BEGIN
    ROLLBACK TRAN;
    PRINT 'ROLLED BACK — @Commit was 0. Set it to 1 and run again to keep these changes.';
END;
