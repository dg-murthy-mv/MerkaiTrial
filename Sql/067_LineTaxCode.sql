-- =====================================================================
-- 067_LineTaxCode.sql
--
-- WHAT THIS DOES
--   QuoteItems.TaxCode    NEW  nvarchar(20) NULL
--   InvoiceLines.TaxCode  NEW  nvarchar(20) NULL
--
--   ...and then fills that column in for lines on documents that have NOT
--   yet left the building. Nothing else is touched: no column is retyped,
--   no constraint is dropped, no existing value is overwritten.
--
-- WHY THE COLUMN EXISTS
--   Products.TaxCode has been there since 051 — the HSN / SAC code in
--   India, the equivalent classification elsewhere. It was collected on
--   the product and then went nowhere: a GST invoice has to print the
--   code against EVERY LINE, and the line had no column to print from.
--   This is the missing half.
--
-- WHY IT IS SNAPSHOTTED ONTO THE LINE INSTEAD OF JOINED TO THE PRODUCT
--   Same reason Name, UnitPrice and UnitOfMeasure are snapshotted. An
--   issued tax invoice is a filed document. If somebody corrects a
--   product's HSN code next March, a join would silently reprint last
--   year's invoice with the new code — and what was filed with the tax
--   authority would no longer match what the system shows. A line with no
--   product at all (a custom line, typed by hand) still gets a code.
--
-- WHY nvarchar(20)
--   Exactly matching Products.TaxCode, added by 051. An Indian HSN is 4,
--   6 or 8 digits; a SAC is 6. 20 leaves room for the markets that write
--   theirs with separators.
--
-- WHY NULLABLE AND NOT DEFAULT ''
--   NULL means "this line carries no classification", which is the honest
--   state for every line that exists today and the permanent state in
--   markets that do not ask for one. The screens and both PDFs show the
--   column ONLY when at least one line on the document has a code, so a
--   Thai or Philippine quote looks exactly as it does now.
--
-- ─────────────────────────────────────────────────────────────────────
-- THE BACKFILL, AND WHY IT IS DELIBERATELY NARROW
--
--   Step 3 copies Products.TaxCode onto lines that have a ProductId, but
--   ONLY where the document has not yet gone out or been filed:
--
--     • QuoteItems   — quotes in Draft (0), Revised (6),
--                      PendingApproval (7) or Approved (8).
--                      NOT Sent, Viewed, Accepted, Rejected or Expired.
--     • InvoiceLines — invoices in Draft (0) only.
--                      NOT Sent, Viewed, PartiallyPaid, Paid, Overdue,
--                      Cancelled or Unpaid.
--
--   The reason is the one above, turned around. A quote the customer is
--   holding, or an invoice that has been issued, is a document somebody
--   already has a copy of. Adding a tax code to it now would make the
--   stored version differ from the printed one that went out — and for an
--   invoice, from what was filed. A draft has gone nowhere, so filling it
--   in is the same as the rep having picked the product today.
--
--   Lines with no ProductId are left NULL everywhere. There is nothing to
--   copy from, and inventing a classification is not this script's job.
--
--   If you would rather nothing at all were filled in, set @Backfill = 0
--   below. The column is still added; every line simply starts NULL and
--   fills in as documents are edited.
--
-- IS IT SAFE TO RE-RUN
--   Yes. Both ALTERs are guarded, and the backfill only ever writes where
--   TaxCode IS NULL — so a second run finds nothing to do, and a code
--   somebody has since changed by hand is never overwritten.
--
-- HOW TO RUN IT
--   Leave @Commit = 0 the first time, read the grids, then set it to 1
--   and run it again.
--
-- ORDER OF OPERATIONS: a column added by this script cannot be referenced
-- by name later in the same batch — SQL Server binds column names when it
-- parses — so every statement that mentions TaxCode after the ALTERs goes
-- through sp_executesql.
-- =====================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit   BIT = 0;    -- <<<<<< set to 1 when the grids look right
DECLARE @Backfill BIT = 1;    -- 0 = add the columns, fill nothing in

DECLARE @Touched TABLE (
    Seq        INT IDENTITY(1,1),
    Step       NVARCHAR(30),
    [Table]    SYSNAME,
    ObjectName SYSNAME NULL,
    Detail     NVARCHAR(400) NULL
);

DECLARE @Rows INT;

BEGIN TRAN;

-- ─────────────────────────────────────────────────────────────────────
-- 0. Sanity: Products.TaxCode must already be there (051).
--    Without it the backfill has nothing to read and the whole round is
--    pointless, so say so loudly rather than half-applying.
-- ─────────────────────────────────────────────────────────────────────
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.Products') AND name = 'TaxCode'
)
BEGIN
    ROLLBACK;
    RAISERROR (
        'Products.TaxCode is missing. Run 051_ProductCatalog.sql first, then this script.',
        16, 1);
    RETURN;
END

-- ─────────────────────────────────────────────────────────────────────
-- 1. QuoteItems.TaxCode
-- ─────────────────────────────────────────────────────────────────────
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.QuoteItems') AND name = 'TaxCode'
)
BEGIN
    PRINT 'QuoteItems.TaxCode -> added';
    ALTER TABLE dbo.QuoteItems ADD TaxCode nvarchar(20) NULL;
    INSERT INTO @Touched (Step, [Table], ObjectName, Detail)
        VALUES ('added column', 'QuoteItems', 'TaxCode', 'nvarchar(20) NULL');
END
ELSE
    PRINT 'QuoteItems.TaxCode already exists - skipped';

-- ─────────────────────────────────────────────────────────────────────
-- 2. InvoiceLines.TaxCode
-- ─────────────────────────────────────────────────────────────────────
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.InvoiceLines') AND name = 'TaxCode'
)
BEGIN
    PRINT 'InvoiceLines.TaxCode -> added';
    ALTER TABLE dbo.InvoiceLines ADD TaxCode nvarchar(20) NULL;
    INSERT INTO @Touched (Step, [Table], ObjectName, Detail)
        VALUES ('added column', 'InvoiceLines', 'TaxCode', 'nvarchar(20) NULL');
END
ELSE
    PRINT 'InvoiceLines.TaxCode already exists - skipped';

-- ─────────────────────────────────────────────────────────────────────
-- 3. Backfill — drafts only. See the long note at the top.
--
--    Through sp_executesql for the reason given there: the columns above
--    may have been created by THIS batch.
--
--    The joins carry TenantId as well as the key. A ProductId on a line
--    is data, and a product belonging to another workspace must never
--    answer for one of ours — the same predicate the application code
--    uses when it reads the catalogue.
--
--    NULLIF(LTRIM(RTRIM(...)), '') so a product whose code is a few
--    spaces leaves the line NULL rather than carrying whitespace onto a
--    tax document.
-- ─────────────────────────────────────────────────────────────────────
--    The row count comes back through an OUTPUT parameter, not @@ROWCOUNT.
--    @@ROWCOUNT read after EXEC sp_executesql describes the EXEC, not the
--    UPDATE inside it, so it would report 0 every time and the grid would
--    say nothing was filled in when it had been.
-- ─────────────────────────────────────────────────────────────────────
IF @Backfill = 1
BEGIN
    EXEC sp_executesql
        N'UPDATE qi
             SET qi.TaxCode = NULLIF(LTRIM(RTRIM(p.TaxCode)), N'''')
            FROM dbo.QuoteItems qi
            JOIN dbo.Quotes     q ON q.Id = qi.QuoteId
            JOIN dbo.Products   p ON p.Id = qi.ProductId
                                 AND p.TenantId = qi.TenantId
           WHERE qi.IsDeleted = 0
             AND q.IsDeleted  = 0
             AND qi.TaxCode IS NULL
             AND NULLIF(LTRIM(RTRIM(p.TaxCode)), N'''') IS NOT NULL
             AND q.Status IN (0, 6, 7, 8);   -- Draft, Revised, PendingApproval, Approved
           SET @OutRows = @@ROWCOUNT;',
        N'@OutRows INT OUTPUT',
        @OutRows = @Rows OUTPUT;

    INSERT INTO @Touched (Step, [Table], ObjectName, Detail)
        VALUES ('backfilled', 'QuoteItems', 'TaxCode',
                CAST(ISNULL(@Rows, 0) AS NVARCHAR(20)) + N' line(s) on quotes not yet sent');
    PRINT 'QuoteItems.TaxCode -> backfilled ' + CAST(ISNULL(@Rows, 0) AS VARCHAR(20)) + ' line(s)';

    EXEC sp_executesql
        N'UPDATE il
             SET il.TaxCode = NULLIF(LTRIM(RTRIM(p.TaxCode)), N'''')
            FROM dbo.InvoiceLines il
            JOIN dbo.Invoices     i ON i.Id = il.InvoiceId
            JOIN dbo.Products     p ON p.Id = il.ProductId
                                   AND p.TenantId = il.TenantId
           WHERE il.IsDeleted = 0
             AND i.IsDeleted  = 0
             AND il.TaxCode IS NULL
             AND NULLIF(LTRIM(RTRIM(p.TaxCode)), N'''') IS NOT NULL
             AND i.Status = 0;              -- Draft only
           SET @OutRows = @@ROWCOUNT;',
        N'@OutRows INT OUTPUT',
        @OutRows = @Rows OUTPUT;

    INSERT INTO @Touched (Step, [Table], ObjectName, Detail)
        VALUES ('backfilled', 'InvoiceLines', 'TaxCode',
                CAST(ISNULL(@Rows, 0) AS NVARCHAR(20)) + N' line(s) on draft invoices');
    PRINT 'InvoiceLines.TaxCode -> backfilled ' + CAST(ISNULL(@Rows, 0) AS VARCHAR(20)) + ' line(s)';
END
ELSE
    PRINT 'Backfill skipped (@Backfill = 0) - columns added, every line left NULL';

-- =====================================================================
-- VERIFICATION
-- =====================================================================
PRINT '';
PRINT '=== 0. What this run changed ===';
SELECT Seq, Step, [Table], ObjectName, Detail FROM @Touched ORDER BY Seq;

PRINT '';
PRINT '=== 1. The new columns, beside the one they were modelled on ===';
PRINT '    All three should read nvarchar / 20 / nullable=1.';

SELECT
    OBJECT_NAME(c.object_id) AS [Table],
    c.name                   AS [Column],
    t.name                   AS [Type],
    c.max_length / 2         AS [Chars],
    c.is_nullable            AS [Nullable]
FROM sys.columns c
JOIN sys.types t ON t.user_type_id = c.user_type_id
WHERE c.object_id IN (OBJECT_ID('dbo.QuoteItems'),
                      OBJECT_ID('dbo.InvoiceLines'),
                      OBJECT_ID('dbo.Products'))
  AND c.name = 'TaxCode'
ORDER BY [Table];

PRINT '';
PRINT '=== 2. How many lines now carry a code ===';
PRINT '    WithCode counts lines that have one. WithProductButNoCode is';
PRINT '    the number still blank because the PRODUCT has no code set --';
PRINT '    those fill in by themselves once somebody edits the product';
PRINT '    and the document, and are not a failure of this script.';

EXEC sp_executesql N'
    SELECT ''QuoteItems'' AS [Table],
           COUNT(*)                                                     AS [Lines],
           SUM(CASE WHEN qi.TaxCode IS NOT NULL THEN 1 ELSE 0 END)      AS [WithCode],
           SUM(CASE WHEN qi.ProductId IS NOT NULL
                     AND qi.TaxCode IS NULL      THEN 1 ELSE 0 END)     AS [WithProductButNoCode],
           SUM(CASE WHEN qi.ProductId IS NULL    THEN 1 ELSE 0 END)     AS [CustomLines]
      FROM dbo.QuoteItems qi WHERE qi.IsDeleted = 0
    UNION ALL
    SELECT ''InvoiceLines'',
           COUNT(*),
           SUM(CASE WHEN il.TaxCode IS NOT NULL THEN 1 ELSE 0 END),
           SUM(CASE WHEN il.ProductId IS NOT NULL AND il.TaxCode IS NULL THEN 1 ELSE 0 END),
           SUM(CASE WHEN il.ProductId IS NULL THEN 1 ELSE 0 END)
      FROM dbo.InvoiceLines il WHERE il.IsDeleted = 0;
';

PRINT '';
PRINT '=== 3. Documents that have ALREADY gone out were left alone ===';
PRINT '    Every one of these must read 0. A number above zero means the';
PRINT '    status filter in step 3 did not do its job -- do NOT commit.';

EXEC sp_executesql N'
    SELECT ''Sent/accepted quote lines with a code'' AS [Check],
           COUNT(*) AS [MustBeZero]
      FROM dbo.QuoteItems qi
      JOIN dbo.Quotes q ON q.Id = qi.QuoteId
     WHERE qi.IsDeleted = 0 AND q.IsDeleted = 0
       AND qi.TaxCode IS NOT NULL
       AND q.Status IN (1, 2, 3, 4, 5)
    UNION ALL
    SELECT ''Issued invoice lines with a code'',
           COUNT(*)
      FROM dbo.InvoiceLines il
      JOIN dbo.Invoices i ON i.Id = il.InvoiceId
     WHERE il.IsDeleted = 0 AND i.IsDeleted = 0
       AND il.TaxCode IS NOT NULL
       AND i.Status <> 0;
';

PRINT '';
PRINT '=== 4. A sample of what was filled in ===';
PRINT '    The line''s code and the product''s code should be identical --';
PRINT '    this run copied one to the other and changed nothing else.';

EXEC sp_executesql N'
    SELECT TOP 25
        q.Number        AS [Quote],
        q.Status        AS [QuoteStatus],
        qi.Name         AS [Line],
        qi.TaxCode      AS [LineCode],
        p.TaxCode       AS [ProductCode],
        p.UnitOfMeasure AS [ProductUnit]
      FROM dbo.QuoteItems qi
      JOIN dbo.Quotes   q ON q.Id = qi.QuoteId
      JOIN dbo.Products p ON p.Id = qi.ProductId AND p.TenantId = qi.TenantId
     WHERE qi.IsDeleted = 0 AND qi.TaxCode IS NOT NULL
     ORDER BY q.CreatedAtUtc DESC;
';

PRINT '';
PRINT '=== 5. Products that still have no code ===';
PRINT '    Nothing to fix in the database -- this is the list to hand the';
PRINT '    person who maintains the catalogue. Until a product has a code,';
PRINT '    no quote line built from it can carry one.';

SELECT TOP 50
    t.Name       AS [Workspace],
    p.Sku        AS [SKU],
    p.Name       AS [Product],
    p.Type       AS [Kind],
    p.Category   AS [Category]
FROM dbo.Products p
JOIN dbo.Tenants  t ON t.Id = p.TenantId
WHERE p.IsDeleted = 0
  AND p.IsActive  = 1
  AND NULLIF(LTRIM(RTRIM(p.TaxCode)), N'') IS NULL
ORDER BY t.Name, p.Name;

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
