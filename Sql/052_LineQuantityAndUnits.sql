-- =====================================================================
-- 052_LineQuantityAndUnits.sql   (revision b)
--
-- REPLACES revision a, which cleared the CHECK constraints and then hit
-- the NEXT thing sitting on the column:
--
--     Msg 5074 - The object 'DF__InvoiceLi__Quant__74444068' is
--                dependent on column 'Quantity'.
--
-- That is a DEFAULT constraint on InvoiceLines.Quantity, and I should
-- have swept for it the first time instead of fixing one blocker and
-- waiting to see what the next one was. Note the name: SQL Server
-- generated it, and the hash on the end is different in every database
-- -- so it cannot be written into a script, it has to be looked up.
-- MerkaiCRM_DEV, _UAT and _PROD will each have their own.
--
-- This revision sweeps for EVERY kind of object that can block an
-- ALTER COLUMN, in one pass, before touching anything:
--
--     CHECK constraints    -> recorded, dropped, replaced with the rule
--                             the application now enforces (Quantity > 0)
--     DEFAULT constraints  -> recorded, dropped, put back VERBATIM under
--                             a stable name after the retype
--     indexes              -> detected and the script STOPS, naming them
--     user statistics      -> detected and the script STOPS, naming them
--     computed columns     -> detected and the script STOPS, naming them
--
-- The last three stop rather than guess: dropping an index quietly and
-- rebuilding it from metadata is how a filtered or included-column index
-- comes back subtly different. There should be none on Quantity. If the
-- script does stop, it prints exactly what it found -- send me that and
-- I will handle it properly.
--
-- WHAT THIS DOES
--   QuoteItems.Quantity   INT  ->  DECIMAL(18,4)
--   InvoiceLines.Quantity INT  ->  DECIMAL(18,4)
--   QuoteItems.UnitOfMeasure    NEW  NVARCHAR(16) NOT NULL DEFAULT 'unit'
--   InvoiceLines.UnitOfMeasure  NEW  NVARCHAR(16) NOT NULL DEFAULT 'unit'
--   Existing catalogue lines are backfilled with their PRODUCT's unit.
--
-- WHY THE CHECK CONSTRAINT DOES NOT COME BACK AS IT WAS
--   If the old rule was Quantity >= 1 -- which is what the C# enforced
--   before this round -- restoring it verbatim would forbid 0.5 days and
--   12.5 m2, i.e. forbid the whole point of the round, and the failure
--   would surface weeks later as a save that mysteriously will not go
--   through. Grid 0 prints the old definition so nothing disappears
--   silently. The DEFAULT constraints DO come back verbatim: a default
--   is not a rule about what is allowed, so there is nothing to bring
--   into line.
--
-- IS IT SAFE TO RE-RUN
--   Yes. Every step is guarded and every drop is conditional.
--
-- HOW TO RUN IT
--   Leave @Commit = 0 the first time. Read grid 0 -- it lists every
--   object dropped, retyped, created and put back. Then set @Commit = 1
--   and run it again.
--
--   Run 051's script first if you have not. This one checks for
--   Products.UnitOfMeasure and SKIPS the backfill rather than failing,
--   so if you do them in the wrong order just re-run this one after.
--
-- ORDER OF OPERATIONS -- the part that matters:
--   A column added by this script cannot be referenced by name later in
--   the same batch (SQL Server binds names when it parses, not when it
--   runs), so every statement that mentions UnitOfMeasure goes through
--   sp_executesql. Statements about Quantity do not need it -- that
--   column already exists.
-- =====================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit BIT = 0;      -- <<<<<< set to 1 when the grids look right

DECLARE @sql     NVARCHAR(MAX);
DECLARE @blocked NVARCHAR(MAX);

DECLARE @QuoteItems   INT = OBJECT_ID('dbo.QuoteItems');
DECLARE @InvoiceLines INT = OBJECT_ID('dbo.InvoiceLines');

-- Bookkeeping. A table variable on purpose: it is NOT transactional, so
-- the dry run with @Commit = 0 still shows you everything it would have
-- done after the rollback throws the work away.
DECLARE @Touched TABLE (
    Seq         INT IDENTITY(1,1),
    Step        NVARCHAR(30),
    [Table]     SYSNAME,
    ObjectName  SYSNAME NULL,
    Detail      NVARCHAR(MAX) NULL
);

-- The DEFAULT constraints to put back after the retype, captured before
-- they are dropped so step 5 is not left guessing.
DECLARE @Redo TABLE (
    [Table]     SYSNAME,
    OldName     SYSNAME,
    NewName     SYSNAME,
    Definition  NVARCHAR(MAX)
);

-- ─────────────────────────────────────────────────────────────────────
-- 0. SWEEP FOR BLOCKERS -- before opening the transaction, so a stop
--    here leaves the database untouched rather than mid-migration.
-- ─────────────────────────────────────────────────────────────────────

-- 0a. Indexes on Quantity. These the script will NOT touch.
SET @blocked = NULL;
SELECT @blocked = ISNULL(@blocked + N', ', N'') +
                  OBJECT_NAME(i.object_id) + N'.' + i.name
FROM sys.index_columns ic
JOIN sys.indexes i
  ON i.object_id = ic.object_id AND i.index_id = ic.index_id
JOIN sys.columns c
  ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE ic.object_id IN (@QuoteItems, @InvoiceLines)
  AND c.name = 'Quantity'
  AND i.name IS NOT NULL;

IF @blocked IS NOT NULL
BEGIN
    PRINT 'STOPPED. An index sits on Quantity, and this script will not drop and rebuild one';
    PRINT 'for you -- a filtered or included-column index can come back subtly different.';
    PRINT 'Send me these names and I will write the right drop/recreate around them:';
    PRINT '    ' + @blocked;
    THROW 51052, 'Index on Quantity - see the messages above. Nothing was changed.', 1;
END

-- 0b. User-created statistics on Quantity. (Auto-created _WA_Sys_ ones
--     are handled by SQL Server itself and do not block anything.)
SET @blocked = NULL;
SELECT @blocked = ISNULL(@blocked + N', ', N'') +
                  OBJECT_NAME(s.object_id) + N'.' + s.name
FROM sys.stats_columns sc
JOIN sys.stats s
  ON s.object_id = sc.object_id AND s.stats_id = sc.stats_id
JOIN sys.columns c
  ON c.object_id = sc.object_id AND c.column_id = sc.column_id
WHERE sc.object_id IN (@QuoteItems, @InvoiceLines)
  AND c.name = 'Quantity'
  AND s.user_created = 1;

IF @blocked IS NOT NULL
BEGIN
    PRINT 'STOPPED. Hand-created statistics reference Quantity:';
    PRINT '    ' + @blocked;
    THROW 51052, 'User statistics on Quantity - see the messages above. Nothing was changed.', 1;
END

-- 0c. Computed columns whose expression mentions Quantity.
SET @blocked = NULL;
SELECT @blocked = ISNULL(@blocked + N', ', N'') +
                  OBJECT_NAME(cc.object_id) + N'.' + cc.name
FROM sys.computed_columns cc
WHERE cc.object_id IN (@QuoteItems, @InvoiceLines)
  AND cc.definition LIKE '%Quantity%';

IF @blocked IS NOT NULL
BEGIN
    PRINT 'STOPPED. A computed column is built from Quantity:';
    PRINT '    ' + @blocked;
    THROW 51052, 'Computed column on Quantity - see the messages above. Nothing was changed.', 1;
END

PRINT 'Blocker sweep: no index, statistic or computed column on Quantity. Proceeding.';

-- =====================================================================
BEGIN TRAN;

-- ─────────────────────────────────────────────────────────────────────
-- 1. CHECK constraints on Quantity -- record, then drop.
-- ─────────────────────────────────────────────────────────────────────
INSERT INTO @Touched (Step, [Table], ObjectName, Detail)
SELECT 'dropped CHECK',
       OBJECT_NAME(cc.parent_object_id),
       cc.name,
       cc.definition
FROM sys.check_constraints cc
WHERE cc.parent_object_id IN (@QuoteItems, @InvoiceLines)
  AND (
        cc.parent_column_id = (
            SELECT c.column_id FROM sys.columns c
            WHERE c.object_id = cc.parent_object_id AND c.name = 'Quantity')
        OR cc.definition LIKE '%Quantity%'
      );

-- ─────────────────────────────────────────────────────────────────────
-- 2. DEFAULT constraints on Quantity -- record, then drop.
--
--    THE ONE REVISION a MISSED. Auto-named (DF__InvoiceLi__Quant__...),
--    with a hash that differs per database, so it can only be looked up.
-- ─────────────────────────────────────────────────────────────────────
INSERT INTO @Touched (Step, [Table], ObjectName, Detail)
SELECT 'dropped DEFAULT',
       OBJECT_NAME(dc.parent_object_id),
       dc.name,
       dc.definition
FROM sys.default_constraints dc
JOIN sys.columns c
  ON c.object_id = dc.parent_object_id
 AND c.column_id = dc.parent_column_id
WHERE dc.parent_object_id IN (@QuoteItems, @InvoiceLines)
  AND c.name = 'Quantity';

-- Remember what to put back, before anything is dropped.
INSERT INTO @Redo ([Table], OldName, NewName, Definition)
SELECT [Table], ObjectName, 'DF_' + [Table] + '_Quantity', Detail
FROM @Touched
WHERE Step = 'dropped DEFAULT';

-- STRING_AGG, not SELECT @sql = @sql + ... : the assignment-concatenation
-- trick has no defined row order and is documented as unreliable. This is
-- SQL Server 2022; there is no reason to use it.
SELECT @sql = STRING_AGG(
                  CAST(N'ALTER TABLE dbo.' + QUOTENAME([Table]) +
                       N' DROP CONSTRAINT ' + QUOTENAME(ObjectName) + N';' AS NVARCHAR(MAX)),
                  CHAR(13) + CHAR(10)) WITHIN GROUP (ORDER BY Seq)
FROM @Touched
WHERE Step IN ('dropped CHECK', 'dropped DEFAULT');

IF LEN(ISNULL(@sql, N'')) > 0
BEGIN
    PRINT 'Dropping the constraints that sit on Quantity:';
    PRINT @sql;
    EXEC sp_executesql @sql;
END
ELSE
    PRINT 'Nothing sits on Quantity - nothing to drop.';

-- ─────────────────────────────────────────────────────────────────────
-- 3. QuoteItems.Quantity  ->  DECIMAL(18,4)
-- ─────────────────────────────────────────────────────────────────────
IF EXISTS (
    SELECT 1 FROM sys.columns c
    JOIN sys.types t ON t.user_type_id = c.user_type_id
    WHERE c.object_id = @QuoteItems AND c.name = 'Quantity' AND t.name <> 'decimal'
)
BEGIN
    PRINT 'QuoteItems.Quantity -> DECIMAL(18,4)';
    -- NOT NULL is kept: every existing row has a quantity, and a line with
    -- no quantity has no meaning.
    ALTER TABLE dbo.QuoteItems ALTER COLUMN Quantity DECIMAL(18,4) NOT NULL;
    INSERT INTO @Touched (Step, [Table], ObjectName, Detail)
        VALUES ('retyped', 'QuoteItems', 'Quantity', 'INT -> DECIMAL(18,4)');
END
ELSE
    PRINT 'QuoteItems.Quantity is already decimal - skipped';

-- ─────────────────────────────────────────────────────────────────────
-- 4. InvoiceLines.Quantity  ->  DECIMAL(18,4)
-- ─────────────────────────────────────────────────────────────────────
IF EXISTS (
    SELECT 1 FROM sys.columns c
    JOIN sys.types t ON t.user_type_id = c.user_type_id
    WHERE c.object_id = @InvoiceLines AND c.name = 'Quantity' AND t.name <> 'decimal'
)
BEGIN
    PRINT 'InvoiceLines.Quantity -> DECIMAL(18,4)';
    ALTER TABLE dbo.InvoiceLines ALTER COLUMN Quantity DECIMAL(18,4) NOT NULL;
    INSERT INTO @Touched (Step, [Table], ObjectName, Detail)
        VALUES ('retyped', 'InvoiceLines', 'Quantity', 'INT -> DECIMAL(18,4)');
END
ELSE
    PRINT 'InvoiceLines.Quantity is already decimal - skipped';

-- ─────────────────────────────────────────────────────────────────────
-- 5. Put the DEFAULTs back -- VERBATIM, under a stable name.
--
--    Verbatim because a default is not a rule about what is allowed, so
--    unlike the CHECK there is nothing to bring into line with the new
--    C#. A stable name (DF_<Table>_Quantity) so the next script that
--    needs to find it can, instead of hunting a hash.
-- ─────────────────────────────────────────────────────────────────────
DELETE r
  FROM @Redo r
 WHERE EXISTS (
        SELECT 1 FROM sys.default_constraints d
        WHERE d.parent_object_id = OBJECT_ID('dbo.' + r.[Table])
          AND d.name = r.NewName);

SELECT @sql = STRING_AGG(
                  CAST(N'ALTER TABLE dbo.' + QUOTENAME([Table]) +
                       N' ADD CONSTRAINT ' + QUOTENAME(NewName) +
                       N' DEFAULT ' + Definition + N' FOR Quantity;' AS NVARCHAR(MAX)),
                  CHAR(13) + CHAR(10))
FROM @Redo;

IF LEN(ISNULL(@sql, N'')) > 0
BEGIN
    PRINT 'Putting the DEFAULT constraints back:';
    PRINT @sql;
    EXEC sp_executesql @sql;

    INSERT INTO @Touched (Step, [Table], ObjectName, Detail)
    SELECT 'recreated DEFAULT', [Table], NewName,
           Definition + ' (unchanged; was ' + OldName + ')'
    FROM @Redo;
END
ELSE
    PRINT 'No DEFAULT constraint to put back.';

-- ─────────────────────────────────────────────────────────────────────
-- 6. Put a positive-quantity CHECK back -- as the NEW rule.
--
--    Quantity > 0, which is what QuoteLineChecks.Validate enforces in the
--    application. Not >= 1: half a day is a real quantity now.
--
--    WITH CHECK when every existing row passes, so the constraint is
--    trusted. If some old row has a zero or negative quantity the script
--    says so loudly and adds it WITH NOCHECK instead -- the bad row is
--    left for you to look at (grid 5) and nothing new can be written
--    like it.
-- ─────────────────────────────────────────────────────────────────────
DECLARE @badQ INT;

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = @QuoteItems AND name = 'CK_QuoteItems_Qty_Positive'
)
BEGIN
    SELECT @badQ = COUNT(*) FROM dbo.QuoteItems WHERE Quantity <= 0;

    IF @badQ = 0
    BEGIN
        ALTER TABLE dbo.QuoteItems WITH CHECK
            ADD CONSTRAINT CK_QuoteItems_Qty_Positive CHECK (Quantity > 0);
        INSERT INTO @Touched (Step, [Table], ObjectName, Detail)
            VALUES ('created CHECK', 'QuoteItems', 'CK_QuoteItems_Qty_Positive', '(Quantity > 0) - trusted');
        PRINT 'QuoteItems: CK_QuoteItems_Qty_Positive created as (Quantity > 0)';
    END
    ELSE
    BEGIN
        ALTER TABLE dbo.QuoteItems WITH NOCHECK
            ADD CONSTRAINT CK_QuoteItems_Qty_Positive CHECK (Quantity > 0);
        INSERT INTO @Touched (Step, [Table], ObjectName, Detail)
            VALUES ('created CHECK', 'QuoteItems', 'CK_QuoteItems_Qty_Positive',
                    CONCAT('(Quantity > 0) - NOT trusted: ', @badQ, ' existing row(s) violate it'));
        PRINT '*** QuoteItems: ' + CAST(@badQ AS VARCHAR(20)) +
              ' existing line(s) have a quantity of zero or less. Constraint added WITH NOCHECK,';
        PRINT '*** so they are left alone. Grid 5 lists them. Nothing new can be written that way.';
    END
END
ELSE
    PRINT 'QuoteItems: CK_QuoteItems_Qty_Positive already exists - skipped';

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = @InvoiceLines AND name = 'CK_InvoiceLines_Qty_Positive'
)
BEGIN
    SELECT @badQ = COUNT(*) FROM dbo.InvoiceLines WHERE Quantity <= 0;

    IF @badQ = 0
    BEGIN
        ALTER TABLE dbo.InvoiceLines WITH CHECK
            ADD CONSTRAINT CK_InvoiceLines_Qty_Positive CHECK (Quantity > 0);
        INSERT INTO @Touched (Step, [Table], ObjectName, Detail)
            VALUES ('created CHECK', 'InvoiceLines', 'CK_InvoiceLines_Qty_Positive', '(Quantity > 0) - trusted');
        PRINT 'InvoiceLines: CK_InvoiceLines_Qty_Positive created as (Quantity > 0)';
    END
    ELSE
    BEGIN
        ALTER TABLE dbo.InvoiceLines WITH NOCHECK
            ADD CONSTRAINT CK_InvoiceLines_Qty_Positive CHECK (Quantity > 0);
        INSERT INTO @Touched (Step, [Table], ObjectName, Detail)
            VALUES ('created CHECK', 'InvoiceLines', 'CK_InvoiceLines_Qty_Positive',
                    CONCAT('(Quantity > 0) - NOT trusted: ', @badQ, ' existing row(s) violate it'));
        PRINT '*** InvoiceLines: ' + CAST(@badQ AS VARCHAR(20)) +
              ' existing line(s) have a quantity of zero or less. Constraint added WITH NOCHECK.';
    END
END
ELSE
    PRINT 'InvoiceLines: CK_InvoiceLines_Qty_Positive already exists - skipped';

-- ─────────────────────────────────────────────────────────────────────
-- 7. QuoteItems.UnitOfMeasure
--
--    NOT NULL with a default rather than nullable: the C# property is a
--    non-nullable string defaulting to "unit", and a NULL arriving from
--    the database into a non-nullable property is how you get an
--    unexplained NullReferenceException three screens away.
-- ─────────────────────────────────────────────────────────────────────
IF NOT EXISTS (
    SELECT 1 FROM sys.columns WHERE object_id = @QuoteItems AND name = 'UnitOfMeasure'
)
BEGIN
    PRINT 'QuoteItems.UnitOfMeasure -> added';
    ALTER TABLE dbo.QuoteItems
        ADD UnitOfMeasure NVARCHAR(16) NOT NULL
            CONSTRAINT DF_QuoteItems_UnitOfMeasure DEFAULT ('unit');
    INSERT INTO @Touched (Step, [Table], ObjectName, Detail)
        VALUES ('added column', 'QuoteItems', 'UnitOfMeasure', 'NVARCHAR(16) NOT NULL DEFAULT ''unit''');
END
ELSE
    PRINT 'QuoteItems.UnitOfMeasure already exists - skipped';

-- ─────────────────────────────────────────────────────────────────────
-- 8. InvoiceLines.UnitOfMeasure
-- ─────────────────────────────────────────────────────────────────────
IF NOT EXISTS (
    SELECT 1 FROM sys.columns WHERE object_id = @InvoiceLines AND name = 'UnitOfMeasure'
)
BEGIN
    PRINT 'InvoiceLines.UnitOfMeasure -> added';
    ALTER TABLE dbo.InvoiceLines
        ADD UnitOfMeasure NVARCHAR(16) NOT NULL
            CONSTRAINT DF_InvoiceLines_UnitOfMeasure DEFAULT ('unit');
    INSERT INTO @Touched (Step, [Table], ObjectName, Detail)
        VALUES ('added column', 'InvoiceLines', 'UnitOfMeasure', 'NVARCHAR(16) NOT NULL DEFAULT ''unit''');
END
ELSE
    PRINT 'InvoiceLines.UnitOfMeasure already exists - skipped';

-- ─────────────────────────────────────────────────────────────────────
-- 9. BACKFILL -- through sp_executesql, because the columns above may
--    have been created by THIS batch.
--
--    Only rows still holding 'unit' are touched, and only where the
--    product says something different. So re-running changes nothing, a
--    line somebody has since set by hand is left alone, and a custom
--    line with no product correctly keeps 'unit'.
-- ─────────────────────────────────────────────────────────────────────
IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.Products') AND name = 'UnitOfMeasure'
)
BEGIN
    EXEC sp_executesql N'
        UPDATE qi
           SET qi.UnitOfMeasure = p.UnitOfMeasure
          FROM dbo.QuoteItems qi
          JOIN dbo.Products   p
            ON p.Id       = qi.ProductId
           AND p.TenantId = qi.TenantId
         WHERE qi.UnitOfMeasure = N''unit''
           AND p.UnitOfMeasure IS NOT NULL
           AND p.UnitOfMeasure <> N''''
           AND p.UnitOfMeasure <> N''unit'';

        PRINT CONCAT(''QuoteItems backfilled from the catalogue: '', @@ROWCOUNT);

        UPDATE il
           SET il.UnitOfMeasure = p.UnitOfMeasure
          FROM dbo.InvoiceLines il
          JOIN dbo.Products     p
            ON p.Id       = il.ProductId
           AND p.TenantId = il.TenantId
         WHERE il.UnitOfMeasure = N''unit''
           AND p.UnitOfMeasure IS NOT NULL
           AND p.UnitOfMeasure <> N''''
           AND p.UnitOfMeasure <> N''unit'';

        PRINT CONCAT(''InvoiceLines backfilled from the catalogue: '', @@ROWCOUNT);
    ';
END
ELSE
    PRINT 'Products.UnitOfMeasure not found - apply 051 first, then re-run this script to backfill.';

-- =====================================================================
-- VERIFICATION
-- =====================================================================
PRINT '';
PRINT '=== 0. Everything this run touched (read this one first) ===';

SELECT Seq, Step, [Table], ObjectName, Detail FROM @Touched ORDER BY Seq;

PRINT '';
PRINT '=== 1. Column types and defaults ===';

SELECT
    OBJECT_NAME(c.object_id)            AS [Table],
    c.name                              AS [Column],
    t.name                              AS [Type],
    c.precision                         AS [Precision],
    c.scale                             AS [Scale],
    c.max_length                        AS [MaxLength],
    c.is_nullable                       AS [Nullable],
    ISNULL(dc.name, '(none)')           AS [DefaultName],
    ISNULL(dc.definition, '(none)')     AS [Default]
FROM sys.columns c
JOIN sys.types t
  ON t.user_type_id = c.user_type_id
LEFT JOIN sys.default_constraints dc
  ON dc.parent_object_id = c.object_id
 AND dc.parent_column_id = c.column_id
WHERE c.object_id IN (@QuoteItems, @InvoiceLines)
  AND c.name IN ('Quantity', 'UnitOfMeasure')
ORDER BY [Table], [Column];

PRINT '';
PRINT '=== 2. CHECK constraints now on these two tables ===';
PRINT '    NotTrusted = 1 means existing rows were NOT verified (see grid 5)';

SELECT
    OBJECT_NAME(cc.parent_object_id) AS [Table],
    cc.name                          AS [Constraint],
    cc.definition                    AS [Definition],
    cc.is_disabled                   AS [Disabled],
    cc.is_not_trusted                AS [NotTrusted]
FROM sys.check_constraints cc
WHERE cc.parent_object_id IN (@QuoteItems, @InvoiceLines)
ORDER BY [Table], [Constraint];

PRINT '';
PRINT '=== 3. Units in use, per table ===';

EXEC sp_executesql N'
    SELECT ''QuoteItems'' AS [Table], UnitOfMeasure, COUNT(*) AS [Lines]
      FROM dbo.QuoteItems
     WHERE IsDeleted = 0
     GROUP BY UnitOfMeasure
    UNION ALL
    SELECT ''InvoiceLines'', UnitOfMeasure, COUNT(*)
      FROM dbo.InvoiceLines
     WHERE IsDeleted = 0
     GROUP BY UnitOfMeasure
     ORDER BY [Table], [Lines] DESC;
';

PRINT '';
PRINT '=== 4. Nothing lost: every quantity should still be a whole number ===';
PRINT '    (this migration converts the TYPE, it never edits a value, so a';
PRINT '     non-zero WithDecimals here would mean something else wrote them)';

SELECT
    'QuoteItems'                                                    AS [Table],
    COUNT(*)                                                        AS [TotalLines],
    SUM(CASE WHEN Quantity <> ROUND(Quantity, 0) THEN 1 ELSE 0 END) AS [WithDecimals],
    MIN(Quantity)                                                   AS [MinQty],
    MAX(Quantity)                                                   AS [MaxQty]
FROM dbo.QuoteItems
WHERE IsDeleted = 0
UNION ALL
SELECT
    'InvoiceLines',
    COUNT(*),
    SUM(CASE WHEN Quantity <> ROUND(Quantity, 0) THEN 1 ELSE 0 END),
    MIN(Quantity),
    MAX(Quantity)
FROM dbo.InvoiceLines
WHERE IsDeleted = 0;

PRINT '';
PRINT '=== 5. Any line with a quantity of zero or less (should be empty) ===';

SELECT 'QuoteItems' AS [Table], Id, TenantId, Name, Quantity, IsDeleted
  FROM dbo.QuoteItems  WHERE Quantity <= 0
UNION ALL
SELECT 'InvoiceLines', Id, TenantId, Name, Quantity, IsDeleted
  FROM dbo.InvoiceLines WHERE Quantity <= 0;

PRINT '';
PRINT '=== 6. A sample of catalogue lines and the unit they now carry ===';

IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.Products') AND name = 'UnitOfMeasure'
)
EXEC sp_executesql N'
    SELECT TOP 20
        q.Number                AS [Quote],
        qi.Name                 AS [Line],
        qi.Quantity,
        qi.UnitOfMeasure        AS [LineUnit],
        p.Name                  AS [Product],
        p.UnitOfMeasure         AS [ProductUnit]
      FROM dbo.QuoteItems qi
      JOIN dbo.Quotes q   ON q.Id = qi.QuoteId
      LEFT JOIN dbo.Products p ON p.Id = qi.ProductId AND p.TenantId = qi.TenantId
     WHERE qi.IsDeleted = 0
     ORDER BY q.CreatedAtUtc DESC, qi.Name;
';
ELSE
    PRINT '(skipped - Products.UnitOfMeasure not present yet)';

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
