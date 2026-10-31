/* =====================================================================
   081_DocumentCustomFields.sql — custom fields on QUOTES and INVOICES

   Adds:
     dbo.CustomFieldDefinitions
       PrintOnDocuments      BIT NOT NULL DEFAULT 0
           "Print on quotes & invoices". Used by Quote and Invoice
           fields now (and by Product fields from Round 082). 0 on every
           existing row, so nothing new prints anywhere until a field is
           switched on in Settings → Custom Fields.
       MapToInvoiceFieldId   UNIQUEIDENTIFIER NULL
           On a QUOTE field: the Invoice field that receives its value
           when an invoice is raised from the quote. Not a foreign key,
           for the reason 079 gives: deleting the Invoice field clears
           the pointer (DeleteCustomFieldHandler) instead of being blocked.

     dbo.Quotes
       CustomFieldsSnapshot     NVARCHAR(MAX) NULL
       CustomFieldsFrozenAtUtc  DATETIME2 NULL
           The printed custom fields as they were when the quote was SENT
           — label, type and value — so the PDF and the customer's link
           keep saying what was sent even if a field is renamed or a
           choice relabelled later. NULL on every existing quote: a quote
           sent before 081 prints its current values, as it always did.

     dbo.Invoices
       CustomFieldsSnapshot     NVARCHAR(MAX) NULL
       CustomFieldsFrozenAtUtc  DATETIME2 NULL
           The same, taken when the invoice is ISSUED. An issued tax
           invoice is a filed document and must not change.

   The VALUES themselves live in dbo.CustomFieldValues like every other
   record type's (EntityType 'Quote' / 'Invoice'). No other table changes.

   Requires 075, 076 and 079 committed first.

   HOW TO RUN
     1. Run as-is. @Commit = 0 — everything is ROLLED BACK. Read grid 2:
        every row should say OK.
     2. Change @Commit to 1 and run again.

   SAFE TO RE-RUN. Every ALTER is guarded. New columns are referenced
   only through sp_executesql, because SQL Server compiles the whole batch
   first and a direct reference to a column added in the same batch fails
   to compile.
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit BIT = 0;      -- flip to 1 to commit

IF OBJECT_ID(N'dbo.CustomFieldDefinitions', N'U') IS NULL
   OR COL_LENGTH(N'dbo.CustomFieldDefinitions', N'MapToDealFieldId') IS NULL
BEGIN
    RAISERROR('081 needs 075, 076 and 079 committed first. Nothing was changed.', 16, 1);
    RETURN;
END

BEGIN TRAN;

/* ── 1. CustomFieldDefinitions ────────────────────────────────────── */
IF COL_LENGTH(N'dbo.CustomFieldDefinitions', N'PrintOnDocuments') IS NULL
BEGIN
    EXEC sp_executesql N'
    ALTER TABLE dbo.CustomFieldDefinitions
        ADD PrintOnDocuments BIT NOT NULL
            CONSTRAINT DF_CustomFieldDefinitions_PrintOnDocuments DEFAULT (0);';
    PRINT 'Added CustomFieldDefinitions.PrintOnDocuments';
END
ELSE
    PRINT 'CustomFieldDefinitions.PrintOnDocuments already exists — skipped';

IF COL_LENGTH(N'dbo.CustomFieldDefinitions', N'MapToInvoiceFieldId') IS NULL
BEGIN
    EXEC sp_executesql N'
    ALTER TABLE dbo.CustomFieldDefinitions
        ADD MapToInvoiceFieldId UNIQUEIDENTIFIER NULL;';
    PRINT 'Added CustomFieldDefinitions.MapToInvoiceFieldId';
END
ELSE
    PRINT 'CustomFieldDefinitions.MapToInvoiceFieldId already exists — skipped';

/* ── 2. Quotes ────────────────────────────────────────────────────── */
IF COL_LENGTH(N'dbo.Quotes', N'CustomFieldsSnapshot') IS NULL
BEGIN
    EXEC sp_executesql N'ALTER TABLE dbo.Quotes ADD CustomFieldsSnapshot NVARCHAR(MAX) NULL;';
    PRINT 'Added Quotes.CustomFieldsSnapshot';
END
ELSE
    PRINT 'Quotes.CustomFieldsSnapshot already exists — skipped';

IF COL_LENGTH(N'dbo.Quotes', N'CustomFieldsFrozenAtUtc') IS NULL
BEGIN
    EXEC sp_executesql N'ALTER TABLE dbo.Quotes ADD CustomFieldsFrozenAtUtc DATETIME2 NULL;';
    PRINT 'Added Quotes.CustomFieldsFrozenAtUtc';
END
ELSE
    PRINT 'Quotes.CustomFieldsFrozenAtUtc already exists — skipped';

/* ── 3. Invoices ──────────────────────────────────────────────────── */
IF COL_LENGTH(N'dbo.Invoices', N'CustomFieldsSnapshot') IS NULL
BEGIN
    EXEC sp_executesql N'ALTER TABLE dbo.Invoices ADD CustomFieldsSnapshot NVARCHAR(MAX) NULL;';
    PRINT 'Added Invoices.CustomFieldsSnapshot';
END
ELSE
    PRINT 'Invoices.CustomFieldsSnapshot already exists — skipped';

IF COL_LENGTH(N'dbo.Invoices', N'CustomFieldsFrozenAtUtc') IS NULL
BEGIN
    EXEC sp_executesql N'ALTER TABLE dbo.Invoices ADD CustomFieldsFrozenAtUtc DATETIME2 NULL;';
    PRINT 'Added Invoices.CustomFieldsFrozenAtUtc';
END
ELSE
    PRINT 'Invoices.CustomFieldsFrozenAtUtc already exists — skipped';

/* ── 4. Verification grids ────────────────────────────────────────── */

-- Grid 1: definitions per record type, with the new settings.
EXEC sp_executesql N'
SELECT EntityType, COUNT(*) AS Fields,
       SUM(CASE WHEN PrintOnDocuments = 1 THEN 1 ELSE 0 END)          AS PrintOnDocuments,
       SUM(CASE WHEN MapToInvoiceFieldId IS NOT NULL THEN 1 ELSE 0 END) AS MappedToInvoice
FROM dbo.CustomFieldDefinitions
WHERE IsDeleted = 0
GROUP BY EntityType
ORDER BY EntityType;';

-- Grid 2: one row per column, all ''OK''.
EXEC sp_executesql N'
WITH expected AS (
    SELECT N''CustomFieldDefinitions'' AS T, N''PrintOnDocuments''        AS C, N''bit''              AS Ty, 0 AS Nul
    UNION ALL SELECT N''CustomFieldDefinitions'', N''MapToInvoiceFieldId'',     N''uniqueidentifier'', 1
    UNION ALL SELECT N''Quotes'',                 N''CustomFieldsSnapshot'',    N''nvarchar'',         1
    UNION ALL SELECT N''Quotes'',                 N''CustomFieldsFrozenAtUtc'', N''datetime2'',        1
    UNION ALL SELECT N''Invoices'',               N''CustomFieldsSnapshot'',    N''nvarchar'',         1
    UNION ALL SELECT N''Invoices'',               N''CustomFieldsFrozenAtUtc'', N''datetime2'',        1
)
SELECT e.T + N''.'' + e.C AS [Column],
       CASE WHEN EXISTS (SELECT 1 FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
                         WHERE c.object_id = OBJECT_ID(N''dbo.'' + e.T) AND c.name = e.C
                           AND t.name = e.Ty AND c.is_nullable = e.Nul)
            THEN N''OK'' ELSE N''MISSING'' END AS [Result]
FROM expected e;';

IF @Commit = 1
BEGIN
    COMMIT;
    PRINT '081: COMMITTED.';
END
ELSE
BEGIN
    ROLLBACK;
    PRINT '081: rolled back (dry run). Set @Commit = 1 to keep it.';
END
