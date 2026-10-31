/* =====================================================================
   101b_Invoices_StatusDefault.sql — new invoices start as drafts

   THE BUG
     The app's model says a new invoice's Status defaults to Draft (0).
     Because Draft is 0, EF left Status OUT of the INSERT and let the
     table's own DEFAULT decide. On your table that default is 1 — Sent.
     So every new invoice, manual or from a quote, was saved already
     "Sent", still carrying its temporary DRAFT-xxxxxxxx number and with
     no issue date. It never went through Issue, so:
       - a manual invoice over the discount limits was never checked;
       - it never got its real INV-nnnn number.

     The code fix (FlowDbContext.ApplyAlwaysSendValues) makes EF always
     send the status, so the table default no longer decides anything
     the app saves. This script tidies the database side:

   WHAT IT DOES
     1. Shows the current DEFAULT for Invoices.Status and Quotes.Status,
        for the record.
     2. Sets the DEFAULT for dbo.Invoices.Status to 0 (Draft), so a
        hand-written INSERT behaves like the app.
     3. Lists invoices that were saved as Sent but never issued:
          Status = 1 (Sent), IssuedAtUtc empty, number still DRAFT-…,
          and no payment recorded against them.
        and, when @FixData = 1, puts them back to Draft. They were never
        sent to anyone — issuing is what emails and numbers an invoice —
        so Draft is the truth. Nothing else on them changes. Open each
        one afterwards and press Issue invoice if it should go out.

   ─────────────────────────────────────────────────────────────────────
   RUN IT ON BOTH DATABASES — your development database AND
   MerkaiTrial_E2E. Check the database dropdown in the toolbar first.
   ─────────────────────────────────────────────────────────────────────

   HOW TO RUN
     1. Run as-is (@Commit = 0): everything is ROLLED BACK. Read the grids.
        Grid 3 lists the invoices it would put back to Draft.
     2. Set @Commit = 1 and run again. Messages end with COMMITTED.
     3. Switch the dropdown to the other database and do 1–2 again.
     Leave @FixData = 1 unless you want to keep those invoices as they
     are (then only the DEFAULT is changed).

   SAFE TO RE-RUN.
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit  BIT = 0;     -- flip to 1 to commit
DECLARE @FixData BIT = 1;     -- 1 = put never-issued "Sent" invoices back to Draft

IF OBJECT_ID(N'dbo.Invoices', N'U') IS NULL
BEGIN
    RAISERROR('dbo.Invoices is missing — is this the Merkai database?', 16, 1);
    RETURN;
END;

-- ── 1. Current defaults on status columns ──
SELECT DB_NAME() AS [Database],
       OBJECT_NAME(c.object_id) AS [Table], c.name AS [Column],
       ISNULL(dc.definition, N'(no default)') AS [Database default now],
       CASE WHEN OBJECT_NAME(c.object_id) = N'Invoices' AND c.name = N'Status'
            THEN N'→ will be (0) Draft' ELSE N'' END AS [Change]
FROM sys.columns c
LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
WHERE c.object_id IN (OBJECT_ID(N'dbo.Invoices'), OBJECT_ID(N'dbo.Quotes'))
  AND c.name = N'Status';

-- ── 3 (preview). Saved as Sent, never issued ──
DECLARE @Never TABLE (Id UNIQUEIDENTIFIER PRIMARY KEY);
INSERT @Never
SELECT i.Id
FROM dbo.Invoices i
WHERE i.Status = 1
  AND i.IssuedAtUtc IS NULL
  AND i.Number LIKE N'DRAFT-%'
  AND NOT EXISTS (SELECT 1 FROM dbo.Payments p WHERE p.InvoiceId = i.Id);

BEGIN TRY
    BEGIN TRANSACTION;

    -- ── 2. The default ──
    DECLARE @df SYSNAME, @sql NVARCHAR(MAX);
    SELECT @df = dc.name
    FROM sys.default_constraints dc
    JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
    WHERE dc.parent_object_id = OBJECT_ID(N'dbo.Invoices') AND c.name = N'Status';

    IF @df IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = @df AND definition IN (N'((0))', N'(0)'))
    BEGIN
        SET @sql = N'ALTER TABLE dbo.Invoices DROP CONSTRAINT ' + QUOTENAME(@df) + N';';
        PRINT @sql; EXEC sys.sp_executesql @sql;
        SET @df = NULL;
    END;

    IF @df IS NULL
    BEGIN
        SET @sql = N'ALTER TABLE dbo.Invoices ADD CONSTRAINT DF_Invoices_Status DEFAULT ((0)) FOR Status;';
        IF EXISTS (SELECT 1 FROM sys.objects WHERE name = N'DF_Invoices_Status')
            SET @sql = N'ALTER TABLE dbo.Invoices ADD CONSTRAINT DF_Invoices_Status_Draft DEFAULT ((0)) FOR Status;';
        PRINT @sql; EXEC sys.sp_executesql @sql;
    END;

    -- ── 3. Never-issued "Sent" invoices back to Draft ──
    SELECT DB_NAME() AS [Database], i.Number, i.Total, i.Currency, i.CreatedAtUtc,
           CASE WHEN i.QuoteId IS NULL THEN N'manual' ELSE N'from a quote' END AS [Kind],
           CASE WHEN @FixData = 1 THEN N'→ Draft' ELSE N'left as Sent (@FixData = 0)' END AS [Change]
    FROM dbo.Invoices i JOIN @Never n ON n.Id = i.Id
    ORDER BY i.CreatedAtUtc;

    IF @FixData = 1
    BEGIN
        UPDATE i SET i.Status = 0
        FROM dbo.Invoices i JOIN @Never n ON n.Id = i.Id
        WHERE i.Status = 1;
        PRINT CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' invoice(s) put back to Draft.';
    END;

    -- ── Check ──
    SELECT DB_NAME() AS [Database],
           CASE WHEN NOT EXISTS (SELECT 1 FROM sys.default_constraints dc
                                 JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
                                 WHERE dc.parent_object_id = OBJECT_ID(N'dbo.Invoices') AND c.name = N'Status'
                                   AND dc.definition IN (N'((0))', N'(0)'))
                     THEN N'PROBLEM — Invoices.Status default is not 0'
                WHEN @FixData = 1 AND EXISTS (SELECT 1 FROM dbo.Invoices i JOIN @Never n ON n.Id = i.Id WHERE i.Status = 1)
                     THEN N'PROBLEM — an invoice is still Sent'
                ELSE N'OK' END AS [Result];

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
