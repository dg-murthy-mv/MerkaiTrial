/* =====================================================================
   100_PaymentIdempotency.sql — a payment is recorded once

   Adds:
     dbo.Payments.IdempotencyKey   UNIQUEIDENTIFIER NULL
         the one-time key the request that recorded the payment carried
         (the payment form makes a fresh one each time it is drawn)
     UX_Payments_Invoice_IdempotencyKey
         unique on (InvoiceId, IdempotencyKey) where the key is set — the
         same key on the same invoice can never be two payments, even if
         two requests get past every other check at once

   Nothing existing is changed. Payments already recorded keep a NULL key,
   which the index ignores.

   ─────────────────────────────────────────────────────────────────────
   RUN IT ON BOTH DATABASES — your development database AND
   MerkaiTrial_E2E. Check the database dropdown in the toolbar before
   pressing Execute; the first row of grid 1 shows which one it ran on.
   ─────────────────────────────────────────────────────────────────────

   HOW TO RUN
     1. Run as-is. @Commit = 0 — everything is ROLLED BACK. Grid 1: every
        row should say OK.
     2. Change @Commit to 1 and run again. Grid 1 again: all OK, and the
        messages end with COMMITTED.
     3. Switch the dropdown to the other database and do 1–2 again.

   SAFE TO RE-RUN. Every ALTER / CREATE is guarded.
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit BIT = 0;      -- flip to 1 to commit

IF OBJECT_ID(N'dbo.Payments', N'U') IS NULL
BEGIN
    RAISERROR('dbo.Payments is missing — is this the Merkai database?', 16, 1);
    RETURN;
END

BEGIN TRAN;

-- ── Payments.IdempotencyKey ──────────────────────────────────────────
IF COL_LENGTH(N'dbo.Payments', N'IdempotencyKey') IS NULL
BEGIN
    EXEC sp_executesql N'ALTER TABLE dbo.Payments ADD IdempotencyKey UNIQUEIDENTIFIER NULL;';
    PRINT 'Added Payments.IdempotencyKey';
END

-- ── One payment per key per invoice ──────────────────────────────────
-- Dynamic SQL: the column was added in this same batch, so a plain
-- CREATE INDEX here would not compile on a first run.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'UX_Payments_Invoice_IdempotencyKey'
                 AND object_id = OBJECT_ID(N'dbo.Payments'))
BEGIN
    EXEC sp_executesql N'
        CREATE UNIQUE INDEX UX_Payments_Invoice_IdempotencyKey
            ON dbo.Payments (InvoiceId, IdempotencyKey)
            WHERE IdempotencyKey IS NOT NULL;';
    PRINT 'Created UX_Payments_Invoice_IdempotencyKey';
END

-- ── Checks ───────────────────────────────────────────────────────────
SELECT Item, Result FROM (
    SELECT 0 AS N, 'Database' AS Item, DB_NAME() AS Result
    UNION ALL
    SELECT 1, 'Payments.IdempotencyKey column',
           CASE WHEN COL_LENGTH(N'dbo.Payments', N'IdempotencyKey') IS NOT NULL THEN 'OK' ELSE 'MISSING' END
    UNION ALL
    SELECT 2, 'UX_Payments_Invoice_IdempotencyKey index',
           CASE WHEN EXISTS (SELECT 1 FROM sys.indexes
                             WHERE name = N'UX_Payments_Invoice_IdempotencyKey'
                               AND object_id = OBJECT_ID(N'dbo.Payments')
                               AND is_unique = 1 AND has_filter = 1)
                THEN 'OK' ELSE 'MISSING' END
) x ORDER BY N;

IF @Commit = 1
BEGIN
    COMMIT;
    PRINT 'COMMITTED.';
END
ELSE
BEGIN
    ROLLBACK;
    PRINT 'Rolled back (dry run). Set @Commit = 1 to keep the changes.';
END
