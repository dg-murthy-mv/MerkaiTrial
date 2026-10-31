/* =====================================================================
   101_Invoices_OptionalColumns.sql — an invoice without a quote can be saved

   THE BUG
     The app treats an invoice's quote as optional (Invoice.QuoteId is
     Guid?, and InvoiceConfiguration says IsRequired(false)). The table in
     the database was created with QuoteId NOT NULL. So every invoice
     raised by hand, with no quote behind it, failed on save with
       Cannot insert the value NULL into column 'QuoteId' ... INSERT fails.
     and the API answered 500 "Failed to create invoice". Invoices raised
     from a quote always carry one, which is why nothing else noticed.

   THE FIX
     Every column the app treats as optional on dbo.Invoices and
     dbo.InvoiceLines is made to allow NULL, if it does not already.
     Columns that already allow NULL are left alone. No data changes:
     loosening NOT NULL to NULL never rewrites or loses a row.

     SQL Server will not change a column that an index or a foreign key
     uses, so those are scripted out first, dropped, and put back exactly
     as they were (same name, columns, filter and delete rule) once the
     column is changed. Foreign keys go back WITH CHECK, so they stay
     trusted.

   ─────────────────────────────────────────────────────────────────────
   RUN IT ON BOTH DATABASES — your development database AND
   MerkaiTrial_E2E. Check the database dropdown in the toolbar before
   pressing Execute; the first column of grid 1 shows which one it ran on.
   ─────────────────────────────────────────────────────────────────────

   HOW TO RUN
     1. Run as-is. @Commit = 0 — everything is ROLLED BACK. Grid 1 lists
        each column it would change (expect at least Invoices.QuoteId);
        grid 2 lists the indexes and keys it would drop and put back.
     2. Change @Commit to 1 and run again. The messages end with
        COMMITTED, and grid 3 says "OK — nothing left to change".
     3. Switch the dropdown to the other database and do 1–2 again.

   SAFE TO RE-RUN. On a database that is already fixed, grid 1 is empty
   and nothing is touched.
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit BIT = 0;      -- flip to 1 to commit

IF OBJECT_ID(N'dbo.Invoices', N'U') IS NULL OR OBJECT_ID(N'dbo.InvoiceLines', N'U') IS NULL
BEGIN
    RAISERROR('dbo.Invoices / dbo.InvoiceLines are missing — is this the Merkai database?', 16, 1);
    RETURN;
END;

-- ── The columns the app treats as optional (from Domain/Entities/Invoice.cs) ──
DECLARE @Optional TABLE (TableName SYSNAME, ColumnName SYSNAME);
INSERT @Optional VALUES
 (N'Invoices', N'QuoteId'), (N'Invoices', N'DealId'), (N'Invoices', N'DueDateUtc'),
 (N'Invoices', N'PromptPayQrImageUrl'), (N'Invoices', N'GatewayRef'), (N'Invoices', N'Provider'),
 (N'Invoices', N'ProviderRef'), (N'Invoices', N'CheckoutUrl'), (N'Invoices', N'PdfUrl'),
 (N'Invoices', N'PublicLinkToken'), (N'Invoices', N'Notes'), (N'Invoices', N'IssuedAtUtc'),
 (N'Invoices', N'IssuedBy'), (N'Invoices', N'VoidedAtUtc'), (N'Invoices', N'VoidedBy'),
 (N'Invoices', N'VoidReason'), (N'Invoices', N'MilestoneId'), (N'Invoices', N'MilestoneName'),
 (N'Invoices', N'MilestoneSequence'), (N'Invoices', N'MilestoneCount'), (N'Invoices', N'MilestonePercent'),
 (N'Invoices', N'CustomFieldsSnapshot'), (N'Invoices', N'CustomFieldsFrozenAtUtc'),
 (N'Invoices', N'CreatedBy'), (N'Invoices', N'UpdatedAtUtc'), (N'Invoices', N'UpdatedBy'),
 (N'InvoiceLines', N'ProductId'), (N'InvoiceLines', N'Description'), (N'InvoiceLines', N'DiscountPercent'),
 (N'InvoiceLines', N'TaxCode'), (N'InvoiceLines', N'CustomFieldsSnapshot'),
 (N'InvoiceLines', N'CreatedBy'), (N'InvoiceLines', N'UpdatedAtUtc'), (N'InvoiceLines', N'UpdatedBy');

-- ── Which of them are NOT NULL here, with their full type ──
DECLARE @Todo TABLE (ObjectId INT, TableName SYSNAME, ColumnId INT, ColumnName SYSNAME, TypeSql NVARCHAR(200));

INSERT @Todo
SELECT c.object_id, o.TableName, c.column_id, c.name,
       CASE
         WHEN t.name IN (N'nvarchar', N'nchar')
           THEN t.name + N'(' + CASE WHEN c.max_length = -1 THEN N'max' ELSE CAST(c.max_length / 2 AS NVARCHAR(10)) END + N')'
                + N' COLLATE ' + c.collation_name
         WHEN t.name IN (N'varchar', N'char', N'varbinary', N'binary')
           THEN t.name + N'(' + CASE WHEN c.max_length = -1 THEN N'max' ELSE CAST(c.max_length AS NVARCHAR(10)) END + N')'
                + CASE WHEN c.collation_name IS NOT NULL THEN N' COLLATE ' + c.collation_name ELSE N'' END
         WHEN t.name IN (N'decimal', N'numeric')
           THEN t.name + N'(' + CAST(c.precision AS NVARCHAR(5)) + N',' + CAST(c.scale AS NVARCHAR(5)) + N')'
         WHEN t.name IN (N'datetime2', N'datetimeoffset', N'time')
           THEN t.name + N'(' + CAST(c.scale AS NVARCHAR(5)) + N')'
         ELSE t.name
       END
FROM @Optional o
JOIN sys.columns c ON c.object_id = OBJECT_ID(N'dbo.' + o.TableName) AND c.name = o.ColumnName
JOIN sys.types   t ON t.user_type_id = c.user_type_id
WHERE c.is_nullable = 0
  AND c.is_computed = 0;

SELECT DB_NAME() AS [Database], TableName AS [Table], ColumnName AS [Column],
       TypeSql + N' NOT NULL  →  NULL' AS [Change]
FROM @Todo ORDER BY TableName, ColumnName;

IF NOT EXISTS (SELECT 1 FROM @Todo)
BEGIN
    SELECT DB_NAME() AS [Database], N'OK — nothing left to change' AS [Result];
    PRINT 'Nothing to do on ' + DB_NAME() + '.';
    RETURN;
END;

-- ── Indexes and foreign keys that use those columns: script, drop, recreate ──
DECLARE @Deps TABLE (Kind NVARCHAR(20), Name SYSNAME, DropSql NVARCHAR(MAX), CreateSql NVARCHAR(MAX));

-- Indexes (not primary keys — none of these columns is one)
INSERT @Deps
SELECT N'Index', i.name,
       N'DROP INDEX ' + QUOTENAME(i.name) + N' ON dbo.' + QUOTENAME(OBJECT_NAME(i.object_id)) + N';',
       CASE WHEN i.is_unique_constraint = 1
         THEN N'ALTER TABLE dbo.' + QUOTENAME(OBJECT_NAME(i.object_id)) + N' ADD CONSTRAINT ' + QUOTENAME(i.name)
              + N' UNIQUE ' + i.type_desc COLLATE DATABASE_DEFAULT + N' ('
         ELSE N'CREATE ' + CASE WHEN i.is_unique = 1 THEN N'UNIQUE ' ELSE N'' END
              + i.type_desc COLLATE DATABASE_DEFAULT + N' INDEX ' + QUOTENAME(i.name)
              + N' ON dbo.' + QUOTENAME(OBJECT_NAME(i.object_id)) + N' ('
       END
       + STUFF((SELECT N', ' + QUOTENAME(c.name) + CASE WHEN ic.is_descending_key = 1 THEN N' DESC' ELSE N' ASC' END
                FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
                ORDER BY ic.key_ordinal FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, N'')
       + N')'
       + ISNULL(N' INCLUDE (' + STUFF((SELECT N', ' + QUOTENAME(c.name)
                FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1
                ORDER BY ic.index_column_id FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, N'') + N')', N'')
       + CASE WHEN i.has_filter = 1 THEN N' WHERE ' + i.filter_definition ELSE N'' END
       + N';'
FROM sys.indexes i
WHERE i.object_id IN (SELECT ObjectId FROM @Todo)
  AND i.is_primary_key = 0
  AND i.type IN (1, 2)
  AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN @Todo t ON t.ObjectId = ic.object_id AND t.ColumnId = ic.column_id
              WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id);

-- Foreign keys on these columns
INSERT @Deps
SELECT N'Foreign key', fk.name,
       N'ALTER TABLE dbo.' + QUOTENAME(OBJECT_NAME(fk.parent_object_id)) + N' DROP CONSTRAINT ' + QUOTENAME(fk.name) + N';',
       N'ALTER TABLE dbo.' + QUOTENAME(OBJECT_NAME(fk.parent_object_id)) + N' WITH CHECK ADD CONSTRAINT ' + QUOTENAME(fk.name)
       + N' FOREIGN KEY ('
       + STUFF((SELECT N', ' + QUOTENAME(COL_NAME(k.parent_object_id, k.parent_column_id))
                FROM sys.foreign_key_columns k WHERE k.constraint_object_id = fk.object_id
                ORDER BY k.constraint_column_id FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, N'')
       + N') REFERENCES ' + QUOTENAME(OBJECT_SCHEMA_NAME(fk.referenced_object_id)) + N'.' + QUOTENAME(OBJECT_NAME(fk.referenced_object_id)) + N' ('
       + STUFF((SELECT N', ' + QUOTENAME(COL_NAME(k.referenced_object_id, k.referenced_column_id))
                FROM sys.foreign_key_columns k WHERE k.constraint_object_id = fk.object_id
                ORDER BY k.constraint_column_id FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, N'')
       + N')'
       + CASE fk.delete_referential_action WHEN 1 THEN N' ON DELETE CASCADE' WHEN 2 THEN N' ON DELETE SET NULL' WHEN 3 THEN N' ON DELETE SET DEFAULT' ELSE N'' END
       + CASE fk.update_referential_action WHEN 1 THEN N' ON UPDATE CASCADE' WHEN 2 THEN N' ON UPDATE SET NULL' WHEN 3 THEN N' ON UPDATE SET DEFAULT' ELSE N'' END
       + N';'
FROM sys.foreign_keys fk
WHERE EXISTS (SELECT 1 FROM sys.foreign_key_columns k JOIN @Todo t ON t.ObjectId = k.parent_object_id AND t.ColumnId = k.parent_column_id
              WHERE k.constraint_object_id = fk.object_id);

SELECT Kind, Name, CreateSql AS [Put back as] FROM @Deps ORDER BY Kind, Name;

-- ── Do it ──
DECLARE @sql NVARCHAR(MAX);

BEGIN TRY
    BEGIN TRANSACTION;

    -- 1. Drop foreign keys, then indexes
    DECLARE c CURSOR LOCAL FAST_FORWARD FOR
        SELECT DropSql FROM @Deps ORDER BY CASE Kind WHEN N'Foreign key' THEN 0 ELSE 1 END, Name;
    OPEN c; FETCH NEXT FROM c INTO @sql;
    WHILE @@FETCH_STATUS = 0 BEGIN PRINT @sql; EXEC sys.sp_executesql @sql; FETCH NEXT FROM c INTO @sql; END;
    CLOSE c; DEALLOCATE c;

    -- 2. Loosen the columns
    DECLARE c2 CURSOR LOCAL FAST_FORWARD FOR
        SELECT N'ALTER TABLE dbo.' + QUOTENAME(TableName) + N' ALTER COLUMN ' + QUOTENAME(ColumnName) + N' ' + TypeSql + N' NULL;'
        FROM @Todo ORDER BY TableName, ColumnName;
    OPEN c2; FETCH NEXT FROM c2 INTO @sql;
    WHILE @@FETCH_STATUS = 0 BEGIN PRINT @sql; EXEC sys.sp_executesql @sql; FETCH NEXT FROM c2 INTO @sql; END;
    CLOSE c2; DEALLOCATE c2;

    -- 3. Put indexes back, then foreign keys
    DECLARE c3 CURSOR LOCAL FAST_FORWARD FOR
        SELECT CreateSql FROM @Deps ORDER BY CASE Kind WHEN N'Index' THEN 0 ELSE 1 END, Name;
    OPEN c3; FETCH NEXT FROM c3 INTO @sql;
    WHILE @@FETCH_STATUS = 0 BEGIN PRINT @sql; EXEC sys.sp_executesql @sql; FETCH NEXT FROM c3 INTO @sql; END;
    CLOSE c3; DEALLOCATE c3;

    -- 4. Check: nothing on the list is NOT NULL any more, and every dropped object is back
    SELECT DB_NAME() AS [Database],
           CASE WHEN EXISTS (SELECT 1 FROM @Optional o
                             JOIN sys.columns c ON c.object_id = OBJECT_ID(N'dbo.' + o.TableName) AND c.name = o.ColumnName
                             WHERE c.is_nullable = 0)
                  THEN N'PROBLEM — a column is still NOT NULL'
                WHEN EXISTS (SELECT 1 FROM @Deps d
                             WHERE NOT EXISTS (SELECT 1 FROM sys.indexes i WHERE i.name = d.Name AND i.object_id IN (SELECT ObjectId FROM @Todo))
                               AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys f WHERE f.name = d.Name))
                  THEN N'PROBLEM — an index or key did not come back'
                ELSE N'OK — nothing left to change' END AS [Result];

    IF @Commit = 1
    BEGIN
        COMMIT TRANSACTION;
        PRINT 'COMMITTED on ' + DB_NAME() + '.';
    END
    ELSE
    BEGIN
        ROLLBACK TRANSACTION;
        PRINT 'ROLLED BACK (dry run). Set @Commit = 1 to apply.';
    END;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT 'FAILED, nothing was changed: ' + ERROR_MESSAGE();
    THROW;
END CATCH;
