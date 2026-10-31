/* =====================================================================
   082_LineCustomFields.sql — PRODUCT custom fields printed on LINES

   Adds:
     dbo.QuoteItems
       CustomFieldsSnapshot  NVARCHAR(MAX) NULL
           The product's custom fields marked "Print on quotes & invoices",
           as they were when the QUOTE was SENT — label, type and value —
           printed under the line on the PDF and the customer's link.

     dbo.InvoiceLines
       CustomFieldsSnapshot  NVARCHAR(MAX) NULL
           The same for an invoice line: copied from the quote line when
           the invoice is raised from a quote, or taken from the product
           when the invoice is ISSUED.

   NULL on every existing row. A line with no copy prints the product's
   CURRENT values, which is exactly what quotes and invoices sent before
   this round do (they print nothing today, because no product field could
   be marked to print until now).

   The switch itself — CustomFieldDefinitions.PrintOnDocuments — already
   exists (081). Nothing else changes.

   Requires 081 committed first.

   HOW TO RUN
     1. Run as-is. @Commit = 0 — everything is ROLLED BACK. Grid 1: every
        row should say OK.
     2. Change @Commit to 1 and run again.

   SAFE TO RE-RUN. Every ALTER is guarded and runs through sp_executesql.
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit BIT = 0;      -- flip to 1 to commit

IF COL_LENGTH(N'dbo.CustomFieldDefinitions', N'PrintOnDocuments') IS NULL
   OR COL_LENGTH(N'dbo.Quotes', N'CustomFieldsSnapshot') IS NULL
BEGIN
    RAISERROR('082 needs 081 committed first. Nothing was changed.', 16, 1);
    RETURN;
END

BEGIN TRAN;

IF COL_LENGTH(N'dbo.QuoteItems', N'CustomFieldsSnapshot') IS NULL
BEGIN
    EXEC sp_executesql N'ALTER TABLE dbo.QuoteItems ADD CustomFieldsSnapshot NVARCHAR(MAX) NULL;';
    PRINT 'Added QuoteItems.CustomFieldsSnapshot';
END
ELSE
    PRINT 'QuoteItems.CustomFieldsSnapshot already exists — skipped';

IF COL_LENGTH(N'dbo.InvoiceLines', N'CustomFieldsSnapshot') IS NULL
BEGIN
    EXEC sp_executesql N'ALTER TABLE dbo.InvoiceLines ADD CustomFieldsSnapshot NVARCHAR(MAX) NULL;';
    PRINT 'Added InvoiceLines.CustomFieldsSnapshot';
END
ELSE
    PRINT 'InvoiceLines.CustomFieldsSnapshot already exists — skipped';

-- Grid 1: one row per column, all 'OK'.
EXEC sp_executesql N'
WITH expected AS (
    SELECT N''QuoteItems'' AS T, N''CustomFieldsSnapshot'' AS C
    UNION ALL SELECT N''InvoiceLines'', N''CustomFieldsSnapshot''
)
SELECT e.T + N''.'' + e.C AS [Column],
       CASE WHEN EXISTS (SELECT 1 FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
                         WHERE c.object_id = OBJECT_ID(N''dbo.'' + e.T) AND c.name = e.C
                           AND t.name = N''nvarchar'' AND c.max_length = -1 AND c.is_nullable = 1)
            THEN N''OK'' ELSE N''MISSING'' END AS [Result]
FROM expected e;';

-- Grid 2: product fields already marked to print (none until you switch one on).
EXEC sp_executesql N'
SELECT COUNT(*) AS ProductFieldsMarkedToPrint
FROM dbo.CustomFieldDefinitions
WHERE EntityType = N''Product'' AND IsDeleted = 0 AND PrintOnDocuments = 1;';

IF @Commit = 1
BEGIN
    COMMIT;
    PRINT '082: COMMITTED.';
END
ELSE
BEGIN
    ROLLBACK;
    PRINT '082: rolled back (dry run). Set @Commit = 1 to keep it.';
END
