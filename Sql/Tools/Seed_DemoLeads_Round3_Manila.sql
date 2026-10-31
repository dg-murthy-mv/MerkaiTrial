/* ============================================================================
   Seed_DemoLeads_Round3_Manila.sql
   ----------------------------------------------------------------------------
   Creates TWO demo leads on Manila Coastal Retail Group (Philippines / PHP)
   for a product walkthrough.

   WHY MANILA: the freelancer sees Philippine Peso formatting throughout, so
   the multi-currency story demonstrates itself without narration.

   ROUND 3 GUIDs (rounds 1-2 covered Thailand and India only):
       DDDD0005-3333-0000-0000-000000000005   Lead 1  (fresh / unworked)
       DDDD0006-3333-0000-0000-000000000006   Lead 2  (qualified, ready to convert)

   RE-RUNNABLE: fixed GUIDs, deleted and re-inserted on every run. Running it
   twice is safe and produces the same two rows.

   ----------------------------------------------------------------------------
   RUN PART 1 FIRST. Read its output. Then set the two @Status variables in
   PART 2 before running PART 2. Do not run the whole file blind — the Status
   column is an int enum whose mapping is not knowable from the schema.
   ============================================================================ */


/* ============================================================================
   PART 1 - DISCOVERY (read-only, changes nothing)
   ============================================================================ */

DECLARE @TenantId UNIQUEIDENTIFIER = 'BB100003-AAAA-0000-0000-000000000003';

-- 1a. Confirm the tenant resolves to PHP. If DefaultCurrency is not PHP, or
--     CountryName is NULL, stop: the Tenants -> Countries FK is not resolving
--     and every money value on screen will render wrong.
SELECT  t.Name                AS TenantName,
        t.DefaultCurrency,
        t.Plan,
        c.Name                AS CountryName,
        c.CurrencyCode,
        c.CurrencySymbol
FROM    dbo.Tenants t
LEFT    JOIN dbo.Countries c ON c.Id = t.CountryId
WHERE   t.Id = @TenantId;

-- 1b. THE IMPORTANT ONE: what do the Status int values actually mean?
--     Read this table and map the two you need:
--       - a value with IsConverted = 0 and no ConvertedAtUtc  -> an open status
--       - the value(s) with IsConverted = 1                   -> converted
--     Pick your "new/unworked" value and your "qualified" value from here.
SELECT   l.Status,
         COUNT(*)                              AS LeadCount,
         MIN(CAST(l.IsConverted AS INT))       AS MinIsConverted,
         MAX(CAST(l.IsConverted AS INT))       AS MaxIsConverted,
         MIN(l.Score)                          AS MinScore,
         MAX(l.Score)                          AS MaxScore,
         MAX(l.FullName)                       AS SampleLead
FROM     dbo.Leads l
WHERE    l.TenantId = @TenantId
  AND    l.IsDeleted = 0
GROUP BY l.Status
ORDER BY l.Status;

-- 1c. Same across ALL tenants, in case Manila's seed data does not happen to
--     contain every status value in the enum.
SELECT   l.Status,
         COUNT(*)                        AS LeadCount,
         MAX(CAST(l.IsConverted AS INT)) AS AnyConverted
FROM     dbo.Leads l
WHERE    l.IsDeleted = 0
GROUP BY l.Status
ORDER BY l.Status;

-- 1d. Reference values that will be copied onto the new leads.
SELECT TOP 1
        CountryId, VerticalId, Channel AS ChannelIntValue, Currency
FROM    dbo.Leads
WHERE   TenantId = @TenantId AND IsDeleted = 0 AND CountryId IS NOT NULL
ORDER BY CreatedAtUtc DESC;
GO


/* ============================================================================
   PART 2 - INSERT
   Set the two Status values below from PART 1b/1c, then run.
   ============================================================================ */

SET NOCOUNT ON;

DECLARE @TenantId UNIQUEIDENTIFIER = 'BB100003-AAAA-0000-0000-000000000003';

/* ---------------------------------------------------------------------------
   >>> SET THESE TWO FROM PART 1 OUTPUT <<<

   The defaults below follow the most common convention
   (New=0, Contacted=1, Qualified=2, Unqualified=3, Converted=4) and are
   CONSISTENT with your existing row (Status=4 with IsConverted=1), but they
   are a convention, not a fact read from your code. Change them if PART 1
   says otherwise.
   --------------------------------------------------------------------------- */
DECLARE @StatusNew        INT = 0;   -- fresh, unworked lead
DECLARE @StatusQualified  INT = 2;   -- qualified, ready to convert to a deal

/* --- Owners (OwnerUserId is nvarchar(64) - GUID stored as TEXT, must quote) - */
DECLARE @OwnerSalesRep NVARCHAR(64) = N'DD100003-DD00-0000-0000-000000000011'; -- Angelica Reyes
DECLARE @OwnerManager  NVARCHAR(64) = N'DD100003-DD00-0000-0000-000000000010'; -- Carlo Villanueva

/* --- Lead source / channel lookups (Manila tenant) ------------------------- */
DECLARE @SrcWebsite   UNIQUEIDENTIFIER = 'FF130001-FF00-0000-0000-000000000001'; -- Website
DECLARE @SrcReferral  UNIQUEIDENTIFIER = 'FF130003-FF00-0000-0000-000000000003'; -- Referral
DECLARE @ChanWebForm  UNIQUEIDENTIFIER = 'AB130005-AB00-0000-0000-000000000005'; -- Web Form
DECLARE @ChanViber    UNIQUEIDENTIFIER = 'AB130003-AB00-0000-0000-000000000003'; -- Viber

/* --- Copy FK/enum values from an existing lead rather than hardcoding ------ */
DECLARE @CountryId  UNIQUEIDENTIFIER,
        @VerticalId UNIQUEIDENTIFIER,
        @ChannelInt INT,
        @Currency   NVARCHAR(10);

SELECT TOP 1
        @CountryId  = CountryId,
        @VerticalId = VerticalId,
        @ChannelInt = Channel,
        @Currency   = Currency
FROM    dbo.Leads
WHERE   TenantId = @TenantId AND IsDeleted = 0 AND CountryId IS NOT NULL
ORDER BY CreatedAtUtc DESC;

/* --- Guards: fail loudly rather than inserting something half-formed ------- */
IF @CountryId IS NULL
BEGIN
    RAISERROR('ABORT: could not read CountryId from an existing Manila lead. Check the tenant has seeded leads.', 16, 1);
    RETURN;
END

IF NOT EXISTS (SELECT 1 FROM dbo.Tenants
               WHERE Id = @TenantId AND DefaultCurrency = 'PHP' AND IsDeleted = 0)
BEGIN
    RAISERROR('ABORT: Manila tenant is missing or its DefaultCurrency is not PHP.', 16, 1);
    RETURN;
END

IF NOT EXISTS (SELECT 1 FROM dbo.Users
               WHERE Id = CAST(@OwnerSalesRep AS UNIQUEIDENTIFIER) AND TenantId = @TenantId AND IsDeleted = 0)
   OR NOT EXISTS (SELECT 1 FROM dbo.Users
               WHERE Id = CAST(@OwnerManager AS UNIQUEIDENTIFIER) AND TenantId = @TenantId AND IsDeleted = 0)
BEGIN
    RAISERROR('ABORT: one or both owner users not found on the Manila tenant.', 16, 1);
    RETURN;
END

SET @Currency = ISNULL(@Currency, N'PHP');

/* --- Fixed identities ------------------------------------------------------ */
DECLARE @Lead1 UNIQUEIDENTIFIER = 'DDDD0005-3333-0000-0000-000000000005';
DECLARE @Lead2 UNIQUEIDENTIFIER = 'DDDD0006-3333-0000-0000-000000000006';

BEGIN TRY
    BEGIN TRANSACTION;

    -- Re-runnable: remove prior copies of these two rows only.
    DELETE FROM dbo.Leads WHERE Id IN (@Lead1, @Lead2);

    /* ---------------------------------------------------------------------
       LEAD 1 - fresh, unworked. Demo path:
         open it -> add a note -> log an activity -> set a reminder ->
         move it through status -> qualify it live on screen.
       Deliberately low Score so scoring is visibly different from Lead 2.
       --------------------------------------------------------------------- */
    INSERT INTO dbo.Leads
        (Id, TenantId, FullName, Email, Phone, CompanyName, Address,
         Status, Score, OwnerUserId, EstimatedValue, Currency,
         SourceId, Source, ChannelId, Channel, CountryId, VerticalId,
         IsConverted, IsDeleted, CreatedAtUtc, CreatedBy)
    VALUES
        (@Lead1, @TenantId,
         N'Bianca Mercado',
         N'bianca.mercado@islaverdehospitality.ph',
         N'+63-917-542-8830',
         N'Isla Verde Hospitality Group',
         N'8F Alveo Financial Tower, 6796 Ayala Ave, Makati City 1226',
         @StatusNew,
         35,
         @OwnerSalesRep,
         1850000.00,
         @Currency,
         @SrcWebsite,  N'Website',
         @ChanWebForm, @ChannelInt,
         @CountryId, @VerticalId,
         0, 0,
         DATEADD(DAY, -2, SYSUTCDATETIME()),
         N'DemoSeed');

    /* ---------------------------------------------------------------------
       LEAD 2 - already qualified, high score, ready to convert. Demo path:
         convert to deal -> drag through pipeline -> build quote ->
         send public link -> accept as the customer -> raise invoice.
       This is the unbroken end-to-end run; no setup needed mid-demo.
       --------------------------------------------------------------------- */
    INSERT INTO dbo.Leads
        (Id, TenantId, FullName, Email, Phone, CompanyName, Address,
         Status, Score, OwnerUserId, EstimatedValue, Currency,
         SourceId, Source, ChannelId, Channel, CountryId, VerticalId,
         IsConverted, IsDeleted, CreatedAtUtc, CreatedBy)
    VALUES
        (@Lead2, @TenantId,
         N'Enrique Lazaro',
         N'enrique.lazaro@bayanihanfresh.ph',
         N'+63-928-771-4402',
         N'Bayanihan Fresh Foods Inc',
         N'Warehouse 4, Cavite Export Processing Zone, Rosario, Cavite 4106',
         @StatusQualified,
         78,
         @OwnerManager,
         2400000.00,
         @Currency,
         @SrcReferral, N'Referral',
         @ChanViber,   @ChannelInt,
         @CountryId, @VerticalId,
         0, 0,
         DATEADD(DAY, -12, SYSUTCDATETIME()),
         N'DemoSeed');

    COMMIT TRANSACTION;
    PRINT 'OK: 2 demo leads inserted on Manila Coastal Retail Group.';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT 'FAILED - nothing was inserted.';
    PRINT ERROR_MESSAGE();
    THROW;
END CATCH
GO


/* ============================================================================
   PART 3 - VERIFY
   ============================================================================ */

SELECT  l.FullName,
        l.CompanyName,
        l.Status,
        l.Score,
        l.EstimatedValue,
        l.Currency,
        LEN(l.Currency)                    AS CurrencyLen,   -- must be 3
        l.Source,
        u.FirstName + ' ' + u.LastName     AS Owner,
        c.Name                             AS Country,
        l.CreatedAtUtc
FROM    dbo.Leads l
LEFT    JOIN dbo.Users     u ON CAST(u.Id AS NVARCHAR(64)) = l.OwnerUserId
LEFT    JOIN dbo.Countries c ON c.Id = l.CountryId
WHERE   l.Id IN ('DDDD0005-3333-0000-0000-000000000005',
                 'DDDD0006-3333-0000-0000-000000000006');
-- Expect: 2 rows, CurrencyLen = 3, Owner and Country both non-NULL.
-- A NULL Owner means OwnerUserId casing does not match the Users table.
GO


/* ============================================================================
   PART 4 - CLEANUP (run after the demo, or before a fresh run)
   ============================================================================ */
/*
DELETE FROM dbo.Leads
WHERE  Id IN ('DDDD0005-3333-0000-0000-000000000005',
              'DDDD0006-3333-0000-0000-000000000006');
*/
