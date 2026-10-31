/* =====================================================================
   MerkaiTrial — TRIM TRANSACTIONAL DATA  (CORRECTED)

   FIXES OVER THE PREVIOUS VERSION
     1. PRINT no longer wraps a subquery. Counts go into scalar variables
        first. (Same mistake as migration 001 step 5 — PRINT takes a
        scalar expression, never a SELECT.)
     2. "Plan" is a RESERVED KEYWORD in T-SQL, so t.Plan broke the parser
        and every "Incorrect syntax near AS" after it was fallout from
        that single token. Now bracketed: t.[Plan]. "Rows" is reserved
        too and is bracketed for the same reason.

   Both were compile errors, so NOTHING ran — the database is untouched.

   ─────────────────────────────────────────────────────────────────────
   BACK UP FIRST:
       BACKUP DATABASE MerkaiTrial
       TO DISK = 'C:\backup\MerkaiTrial_pre_trim.bak' WITH INIT;

   DRY RUN BY DEFAULT — @Commit = 0 rolls back and only reports.

   ─────────────────────────────────────────────────────────────────────
   NO CASCADES ARE RELIED ON. Your schema has duplicate foreign keys on
   the same columns with conflicting rules (QuoteItems.QuoteId has both
   CASCADE and NO_ACTION). SQL Server enforces the most restrictive, so
   the cascades never fire. Children are deleted explicitly, deepest
   first.
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit       BIT = 0;   -- ← 0 = dry run. 1 = apply.

DECLARE @KeepLeads    INT = 10;  -- newest per tenant
DECLARE @KeepDeals    INT = 5;
DECLARE @KeepQuotes   INT = 3;
DECLARE @KeepInvoices INT = 3;

DECLARE @n INT;                  -- per-statement row count
DECLARE @c INT;                  -- keep-set count for PRINT

BEGIN TRANSACTION;

BEGIN TRY

    /* =================================================================
       1. DECIDE WHAT SURVIVES — top down, so references stay valid.

       Deals are chosen FIRST, then any lead a kept deal points at is
       force-kept even if it is not among the newest 10. Choosing leads
       first would leave a kept deal referencing a deleted lead, and
       FK_Deals_Leads is NO_ACTION, so the delete would fail.
       ================================================================= */

    CREATE TABLE #KeepDeals (Id UNIQUEIDENTIFIER PRIMARY KEY);

    INSERT INTO #KeepDeals (Id)
    SELECT Id FROM (
        SELECT d.Id,
               ROW_NUMBER() OVER (PARTITION BY d.TenantId ORDER BY d.CreatedAtUtc DESC) AS rn
        FROM dbo.Deals d
    ) x WHERE rn <= @KeepDeals;

    CREATE TABLE #KeepLeads (Id UNIQUEIDENTIFIER PRIMARY KEY);

    INSERT INTO #KeepLeads (Id)
    SELECT DISTINCT Id FROM (
        SELECT l.Id
        FROM (
            SELECT l.Id,
                   ROW_NUMBER() OVER (PARTITION BY l.TenantId ORDER BY l.CreatedAtUtc DESC) AS rn
            FROM dbo.Leads l
        ) l WHERE l.rn <= @KeepLeads

        UNION

        SELECT d.LeadId
        FROM dbo.Deals d
        JOIN #KeepDeals k ON k.Id = d.Id
        WHERE d.LeadId IS NOT NULL
    ) y;

    CREATE TABLE #KeepQuotes (Id UNIQUEIDENTIFIER PRIMARY KEY);

    INSERT INTO #KeepQuotes (Id)
    SELECT Id FROM (
        SELECT q.Id,
               ROW_NUMBER() OVER (PARTITION BY q.TenantId ORDER BY q.CreatedAtUtc DESC) AS rn
        FROM dbo.Quotes q
        WHERE q.DealId IS NULL OR q.DealId IN (SELECT Id FROM #KeepDeals)
    ) x WHERE rn <= @KeepQuotes;

    CREATE TABLE #KeepInvoices (Id UNIQUEIDENTIFIER PRIMARY KEY);

    INSERT INTO #KeepInvoices (Id)
    SELECT Id FROM (
        SELECT i.Id,
               ROW_NUMBER() OVER (PARTITION BY i.TenantId ORDER BY i.CreatedAtUtc DESC) AS rn
        FROM dbo.Invoices i
        WHERE (i.DealId  IS NULL OR i.DealId  IN (SELECT Id FROM #KeepDeals))
          AND (i.QuoteId IS NULL OR i.QuoteId IN (SELECT Id FROM #KeepQuotes))
    ) x WHERE rn <= @KeepInvoices;

    /* Counts into variables — PRINT cannot take a subquery. */
    PRINT '--- SURVIVING RECORDS ---';
    SELECT @c = COUNT(*) FROM #KeepLeads;    PRINT 'Leads kept    : ' + CAST(@c AS varchar(10));
    SELECT @c = COUNT(*) FROM #KeepDeals;    PRINT 'Deals kept    : ' + CAST(@c AS varchar(10));
    SELECT @c = COUNT(*) FROM #KeepQuotes;   PRINT 'Quotes kept   : ' + CAST(@c AS varchar(10));
    SELECT @c = COUNT(*) FROM #KeepInvoices; PRINT 'Invoices kept : ' + CAST(@c AS varchar(10));
    PRINT '';


    /* =================================================================
       2. CLEAR THE NOISE ENTIRELY
       Activity streams, notes and reminders regenerate as soon as you
       use the app.
       ================================================================= */
    PRINT '--- CLEARED ---';

    DELETE FROM dbo.DealStageHistory;  SET @n=@@ROWCOUNT; PRINT 'DealStageHistory : ' + CAST(@n AS varchar(10));
    DELETE FROM dbo.DealActivities;    SET @n=@@ROWCOUNT; PRINT 'DealActivities   : ' + CAST(@n AS varchar(10));
    DELETE FROM dbo.DealNotes;         SET @n=@@ROWCOUNT; PRINT 'DealNotes        : ' + CAST(@n AS varchar(10));
    DELETE FROM dbo.DealReminders;     SET @n=@@ROWCOUNT; PRINT 'DealReminders    : ' + CAST(@n AS varchar(10));
    DELETE FROM dbo.LeadActivities;    SET @n=@@ROWCOUNT; PRINT 'LeadActivities   : ' + CAST(@n AS varchar(10));
    DELETE FROM dbo.LeadNotes;         SET @n=@@ROWCOUNT; PRINT 'LeadNotes        : ' + CAST(@n AS varchar(10));
    DELETE FROM dbo.LeadReminders;     SET @n=@@ROWCOUNT; PRINT 'LeadReminders    : ' + CAST(@n AS varchar(10));
    DELETE FROM dbo.Activities;        SET @n=@@ROWCOUNT; PRINT 'Activities       : ' + CAST(@n AS varchar(10));
    DELETE FROM dbo.ChannelMessages;   SET @n=@@ROWCOUNT; PRINT 'ChannelMessages  : ' + CAST(@n AS varchar(10));
    DELETE FROM dbo.InboxMessages;     SET @n=@@ROWCOUNT; PRINT 'InboxMessages    : ' + CAST(@n AS varchar(10));
    DELETE FROM dbo.Journeys;          SET @n=@@ROWCOUNT; PRINT 'Journeys         : ' + CAST(@n AS varchar(10));
    DELETE FROM dbo.Consents;          SET @n=@@ROWCOUNT; PRINT 'Consents         : ' + CAST(@n AS varchar(10));

    -- Attachment ROWS go; the FILES on disk do not. See the closing note.
    DELETE FROM dbo.Attachments;       SET @n=@@ROWCOUNT; PRINT 'Attachments      : ' + CAST(@n AS varchar(10));
    PRINT '';


    /* =================================================================
       3. TRIM THE TRANSACTIONAL CHAIN — deepest children first.
       ================================================================= */
    PRINT '--- TRIMMED ---';

    DELETE p FROM dbo.Payments p
     WHERE p.InvoiceId NOT IN (SELECT Id FROM #KeepInvoices);
    SET @n=@@ROWCOUNT; PRINT 'Payments         : ' + CAST(@n AS varchar(10));

    DELETE il FROM dbo.InvoiceLines il
     WHERE il.InvoiceId NOT IN (SELECT Id FROM #KeepInvoices);
    SET @n=@@ROWCOUNT; PRINT 'InvoiceLines     : ' + CAST(@n AS varchar(10));

    DELETE i FROM dbo.Invoices i
     WHERE i.Id NOT IN (SELECT Id FROM #KeepInvoices);
    SET @n=@@ROWCOUNT; PRINT 'Invoices         : ' + CAST(@n AS varchar(10));

    DELETE qi FROM dbo.QuoteItems qi
     WHERE qi.QuoteId NOT IN (SELECT Id FROM #KeepQuotes);
    SET @n=@@ROWCOUNT; PRINT 'QuoteItems       : ' + CAST(@n AS varchar(10));

    DELETE q FROM dbo.Quotes q
     WHERE q.Id NOT IN (SELECT Id FROM #KeepQuotes);
    SET @n=@@ROWCOUNT; PRINT 'Quotes           : ' + CAST(@n AS varchar(10));

    DELETE d FROM dbo.Deals d
     WHERE d.Id NOT IN (SELECT Id FROM #KeepDeals);
    SET @n=@@ROWCOUNT; PRINT 'Deals            : ' + CAST(@n AS varchar(10));

    -- Leads last: FK_Deals_Leads is NO_ACTION, so every referencing deal
    -- must already be gone or kept alongside its lead.
    DELETE l FROM dbo.Leads l
     WHERE l.Id NOT IN (SELECT Id FROM #KeepLeads);
    SET @n=@@ROWCOUNT; PRINT 'Leads            : ' + CAST(@n AS varchar(10));


    /* =================================================================
       4. RESULT PER TENANT
       [Plan] and [Rows] are bracketed — both are reserved keywords.
       ================================================================= */
    SELECT
        t.Name AS Tenant,
        t.[Plan] AS PlanName,
        t.DefaultCurrency,
        (SELECT COUNT(*) FROM dbo.Leads     x WHERE x.TenantId = t.Id) AS Leads,
        (SELECT COUNT(*) FROM dbo.Deals     x WHERE x.TenantId = t.Id) AS Deals,
        (SELECT COUNT(*) FROM dbo.Quotes    x WHERE x.TenantId = t.Id) AS Quotes,
        (SELECT COUNT(*) FROM dbo.Invoices  x WHERE x.TenantId = t.Id) AS Invoices,
        (SELECT COUNT(*) FROM dbo.Companies x WHERE x.TenantId = t.Id) AS Companies,
        (SELECT COUNT(*) FROM dbo.Contacts  x WHERE x.TenantId = t.Id) AS Contacts,
        (SELECT COUNT(*) FROM dbo.Products  x WHERE x.TenantId = t.Id) AS Products,
        (SELECT COUNT(*) FROM dbo.Users     x WHERE x.TenantId = t.Id AND x.IsDeleted = 0) AS Users
    FROM dbo.Tenants t
    WHERE t.IsDeleted = 0
    ORDER BY t.Name;

    /* Orphan check — all three must be 0. If not, DO NOT COMMIT. */
    SELECT
        (SELECT COUNT(*) FROM dbo.Deals d
          WHERE d.LeadId IS NOT NULL
            AND NOT EXISTS (SELECT 1 FROM dbo.Leads l WHERE l.Id = d.LeadId))    AS DealsWithMissingLead,
        (SELECT COUNT(*) FROM dbo.Quotes q
          WHERE q.DealId IS NOT NULL
            AND NOT EXISTS (SELECT 1 FROM dbo.Deals d WHERE d.Id = q.DealId))    AS QuotesWithMissingDeal,
        (SELECT COUNT(*) FROM dbo.Invoices i
          WHERE i.QuoteId IS NOT NULL
            AND NOT EXISTS (SELECT 1 FROM dbo.Quotes q WHERE q.Id = i.QuoteId))  AS InvoicesWithMissingQuote;

    IF @Commit = 1
    BEGIN
        COMMIT TRANSACTION;
        PRINT '';
        PRINT '=== COMMITTED ===';
    END
    ELSE
    BEGIN
        ROLLBACK TRANSACTION;
        PRINT '';
        PRINT '=== DRY RUN — everything rolled back. Set @Commit = 1 to apply. ===';
    END

END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT 'FAILED — nothing changed.';
    PRINT 'Error : ' + ERROR_MESSAGE();
    PRINT 'Line  : ' + CAST(ERROR_LINE() AS varchar(10));
END CATCH

/* =====================================================================
   IF YOU GET "Invalid column name 'CreatedAtUtc'"

   The keep-sets order by CreatedAtUtc on Leads, Deals, Quotes and
   Invoices. If any of those tables uses a different audit column name,
   tell me which and I will adjust — do not guess, since ordering by the
   wrong column silently keeps the wrong rows.

   AFTERWARDS — two things this script cannot do

   1. UPLOADED FILES live at {ContentRoot}\uploads\{tenantId}\, not in
      the database. The Attachments rows are gone; the files are not.
      Clear those folders by hand.

   2. Quota figures on the dashboard come from TenantSettings, which has
      drifted from Plans. Unrelated to this trim, but the smaller numbers
      will make it obvious.
   ===================================================================== */
