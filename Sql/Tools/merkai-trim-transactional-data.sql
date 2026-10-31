/* =====================================================================
   MerkaiTrial — TRIM TRANSACTIONAL DATA
   Keeps all four demo tenants and their master data.
   Keeps a small, coherent slice of transactional records per tenant.
   Deletes the rest.

   ─────────────────────────────────────────────────────────────────────
   TAKE A FULL BACKUP FIRST. This hard-deletes rows.

       BACKUP DATABASE MerkaiTrial
       TO DISK = 'C:\backup\MerkaiTrial_pre_trim.bak' WITH INIT;

   ─────────────────────────────────────────────────────────────────────
   DRY RUN BY DEFAULT. @Commit = 0 rolls everything back and just prints
   the counts. Read them, then set @Commit = 1 and run again.

   ─────────────────────────────────────────────────────────────────────
   WHY NO CASCADES ARE USED

   Your schema has DUPLICATE foreign keys on the same columns with
   conflicting delete rules — e.g. QuoteItems.QuoteId carries both
   FK_QuoteItems_Quote (CASCADE) and FK_QuoteItems_Quotes (NO_ACTION).
   SQL Server enforces the most restrictive, so NO_ACTION wins and the
   cascades never fire. Every child is therefore deleted explicitly,
   deepest first. (Side effect worth checking in the app: deleting a
   quote or an invoice through the UI almost certainly fails today for
   the same reason.)

   ─────────────────────────────────────────────────────────────────────
   WHAT IS KEPT

     Untouched  Companies, Contacts, Products, Users, Roles, UserRoles,
                TenantSettings, Pipelines, TaxRates, LeadChannels,
                LeadSources, Countries, Plans, CompanyVerticals
     Trimmed    Leads, Deals, Quotes, Invoices (newest N per tenant,
                kept CONSISTENT — a kept deal keeps its lead, a kept
                invoice keeps its quote)
     Cleared    Activities, notes, reminders, stage history, channel and
                inbox messages, journeys, consents, attachments

   Companies and Contacts are deliberately left alone: they behave as
   master data, they are what leads and deals point at, and removing them
   creates most of the FK risk for very little benefit.
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit      BIT = 0;   -- ← 0 = dry run (rolls back). 1 = apply.

DECLARE @KeepLeads   INT = 10;  -- newest per tenant
DECLARE @KeepDeals   INT = 5;
DECLARE @KeepQuotes  INT = 3;
DECLARE @KeepInvoices INT = 3;

BEGIN TRANSACTION;

BEGIN TRY

    /* =================================================================
       1. DECIDE WHAT SURVIVES — top down, so references stay valid.

       Order matters: deals are chosen first, then the leads they point
       at are force-kept even if they are not among the newest 10.
       Choosing leads first would let a kept deal reference a deleted
       lead and the delete would fail on FK_Deals_Leads.
       ================================================================= */

    -- ── Deals: newest N per tenant ───────────────────────────────────
    CREATE TABLE #KeepDeals (Id UNIQUEIDENTIFIER PRIMARY KEY);

    INSERT INTO #KeepDeals (Id)
    SELECT Id FROM (
        SELECT d.Id,
               ROW_NUMBER() OVER (PARTITION BY d.TenantId ORDER BY d.CreatedAtUtc DESC) AS rn
        FROM dbo.Deals d
    ) x WHERE rn <= @KeepDeals;

    -- ── Leads: newest N per tenant, PLUS any lead a kept deal needs ──
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

    -- ── Quotes: newest N per tenant, but only ones whose deal survives
    CREATE TABLE #KeepQuotes (Id UNIQUEIDENTIFIER PRIMARY KEY);

    INSERT INTO #KeepQuotes (Id)
    SELECT Id FROM (
        SELECT q.Id,
               ROW_NUMBER() OVER (PARTITION BY q.TenantId ORDER BY q.CreatedAtUtc DESC) AS rn
        FROM dbo.Quotes q
        WHERE q.DealId IS NULL OR q.DealId IN (SELECT Id FROM #KeepDeals)
    ) x WHERE rn <= @KeepQuotes;

    -- ── Invoices: newest N per tenant, deal AND quote must survive ───
    CREATE TABLE #KeepInvoices (Id UNIQUEIDENTIFIER PRIMARY KEY);

    INSERT INTO #KeepInvoices (Id)
    SELECT Id FROM (
        SELECT i.Id,
               ROW_NUMBER() OVER (PARTITION BY i.TenantId ORDER BY i.CreatedAtUtc DESC) AS rn
        FROM dbo.Invoices i
        WHERE (i.DealId  IS NULL OR i.DealId  IN (SELECT Id FROM #KeepDeals))
          AND (i.QuoteId IS NULL OR i.QuoteId IN (SELECT Id FROM #KeepQuotes))
    ) x WHERE rn <= @KeepInvoices;

    PRINT '--- SURVIVING RECORDS ---';
    PRINT 'Leads kept   : ' + CAST((SELECT COUNT(*) FROM #KeepLeads)    AS varchar(10));
    PRINT 'Deals kept   : ' + CAST((SELECT COUNT(*) FROM #KeepDeals)    AS varchar(10));
    PRINT 'Quotes kept  : ' + CAST((SELECT COUNT(*) FROM #KeepQuotes)   AS varchar(10));
    PRINT 'Invoices kept: ' + CAST((SELECT COUNT(*) FROM #KeepInvoices) AS varchar(10));


    /* =================================================================
       2. CLEAR THE NOISE ENTIRELY

       Activity streams, notes and reminders regenerate as soon as you
       use the app. Keeping a slice of them adds nothing to testing and
       complicates every delete below.
       ================================================================= */
    DECLARE @n INT;

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

    -- Attachment ROWS go; the FILES on disk do not. See the note at the
    -- bottom of this script — they must be cleared by hand.
    DELETE FROM dbo.Attachments;       SET @n=@@ROWCOUNT; PRINT 'Attachments      : ' + CAST(@n AS varchar(10));


    /* =================================================================
       3. TRIM THE TRANSACTIONAL CHAIN — deepest children first.

       No cascade is relied on anywhere here; see the header.
       ================================================================= */

    -- Payments → Invoices
    DELETE p FROM dbo.Payments p
     WHERE p.InvoiceId NOT IN (SELECT Id FROM #KeepInvoices);
    SET @n=@@ROWCOUNT; PRINT 'Payments         : ' + CAST(@n AS varchar(10));

    -- InvoiceLines → Invoices
    DELETE il FROM dbo.InvoiceLines il
     WHERE il.InvoiceId NOT IN (SELECT Id FROM #KeepInvoices);
    SET @n=@@ROWCOUNT; PRINT 'InvoiceLines     : ' + CAST(@n AS varchar(10));

    DELETE i FROM dbo.Invoices i
     WHERE i.Id NOT IN (SELECT Id FROM #KeepInvoices);
    SET @n=@@ROWCOUNT; PRINT 'Invoices         : ' + CAST(@n AS varchar(10));

    -- QuoteItems → Quotes
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
       ================================================================= */
    SELECT
        t.Name AS Tenant,
        t.Plan,
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

    /* Orphan check — all three counts must be 0. */
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
    PRINT 'Error  : ' + ERROR_MESSAGE();
    PRINT 'Line   : ' + CAST(ERROR_LINE() AS varchar(10));
END CATCH

/* =====================================================================
   AFTERWARDS — two things this script cannot do

   1. UPLOADED FILES live on disk under {ContentRoot}\uploads\{tenantId}\,
      not in the database. The Attachments rows are gone; the files are
      not. Clear those folders by hand, keeping the directory itself.

   2. QUOTA COUNTS may look odd until you check TenantSettings against
      Plans. The Manila tenant showed an 1,100-lead quota where the
      Professional plan defines 2,000 — that drift is unrelated to this
      trim, but the numbers on the dashboard will now be small enough
      that you will notice it.
   ===================================================================== */
