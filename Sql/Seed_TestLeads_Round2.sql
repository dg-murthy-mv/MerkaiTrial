/* ==========================================================================
   Seed_TestLeads_Round2.sql
   --------------------------------------------------------------------------
   One qualified lead each for:
     Sathorn Prestige Interiors (Thailand, THB)  BB100002-AAAA-...-002
     Nexora Tech Solutions      (India,    INR)  BB100001-AAAA-...-001

   Purpose: full Lead -> Deal -> Quote -> Invoice pass against the
   2026-08-03 fixes:
       * quote/invoice number generators (max-of-sequence + retry)
       * invoice numbers not reused after a soft delete
       * Quotes/Create currency resolved server-side (was persisting '')
       * Quotes/Create issue & expiry dates actually posting
       * date stored as midnight UTC (no more day rollback in IST)
       * ApiService handling 204 No Content (public quote Accept)
       * Public/QuoteView currency formatting

   Distinct GUIDs and names from round 1 (DDDD0001/DDDD0002) so both sets can
   coexist. Re-runnable: deletes its own rows first.

   Run against: MadeeFlow
   ========================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @TenantTH uniqueidentifier = 'BB100002-AAAA-0000-0000-000000000002';
DECLARE @TenantIN uniqueidentifier = 'BB100001-AAAA-0000-0000-000000000001';

DECLARE @LeadTH  uniqueidentifier = 'DDDD0003-2222-4222-8222-000000000003';
DECLARE @LeadIN  uniqueidentifier = 'DDDD0004-2222-4222-8222-000000000004';

DECLARE @Now datetime2(7) = SYSUTCDATETIME();


/* --------------------------------------------------------------------------
   0. PRE-FLIGHT A — what numbers should the generators produce?
   -------------------------------------------------------------------------- */
PRINT '=== Expected next document numbers ===';

SELECT
    Tenant       = CASE WHEN TenantId = @TenantTH THEN N'Sathorn (TH)' ELSE N'Nexora (IN)' END,
    Kind         = N'Quote',
    MaxNumber    = MAX(Number),
    NextExpected = N'QUO-' + RIGHT(N'0000' + CAST(
                     MAX(TRY_CAST(SUBSTRING(Number, 5, 20) AS int)) + 1 AS nvarchar(10)), 4)
FROM dbo.Quotes
WHERE TenantId IN (@TenantTH, @TenantIN) AND Number LIKE N'QUO-%'
GROUP BY TenantId
UNION ALL
SELECT
    CASE WHEN TenantId = @TenantTH THEN N'Sathorn (TH)' ELSE N'Nexora (IN)' END,
    N'Invoice',
    MAX(Number),
    N'INV-' + RIGHT(N'0000' + CAST(
                     MAX(TRY_CAST(SUBSTRING(Number, 5, 20) AS int)) + 1 AS nvarchar(10)), 4)
FROM dbo.Invoices
WHERE TenantId IN (@TenantTH, @TenantIN) AND Number LIKE N'INV-%'
GROUP BY TenantId;

/* Write these four values down — they are the pass/fail criteria for the
   numbering fixes. Note the generators count soft-deleted rows too, which is
   deliberate: the unique index covers them, so their numbers can never be
   reused. */


/* --------------------------------------------------------------------------
   0b. PRE-FLIGHT B — will the new currency resolution actually find a value?
   --------------------------------------------------------------------------
   Quotes/Create now calls ResolveCurrencyAsync: deal currency first, tenant
   currency as fallback, and it REFUSES to create a quote if both are blank.
   If TenantCurrency is NULL below, the fallback cannot fire and quote creation
   will fail with "Could not determine the currency for this deal."
   -------------------------------------------------------------------------- */
PRINT '=== Currency resolution sources (both should be non-NULL) ===';

SELECT
    TenantName     = t.Name,
    t.Id,
    CountryId      = t.CountryId,
    TenantCurrency = c.CurrencyCode,
    CurrencySymbol = c.CurrencySymbol,
    NumberFormat   = c.NumberFormat
FROM      dbo.Tenants   AS t
LEFT JOIN dbo.Countries AS c ON c.Id = t.CountryId
WHERE t.Id IN (@TenantTH, @TenantIN);


/* --------------------------------------------------------------------------
   1. GUARD — need an existing lead per tenant to copy FK/enum values from
   -------------------------------------------------------------------------- */
IF NOT EXISTS (SELECT 1 FROM dbo.Leads
               WHERE TenantId = @TenantTH AND IsDeleted = 0 AND OwnerUserId IS NOT NULL)
BEGIN
    RAISERROR (N'No usable template lead on Sathorn (TH). Aborting.', 16, 1);
    RETURN;
END

IF NOT EXISTS (SELECT 1 FROM dbo.Leads
               WHERE TenantId = @TenantIN AND IsDeleted = 0 AND OwnerUserId IS NOT NULL)
BEGIN
    RAISERROR (N'No usable template lead on Nexora (IN). Aborting.', 16, 1);
    RETURN;
END


/* --------------------------------------------------------------------------
   2. Clear any previous run of THIS script
   -------------------------------------------------------------------------- */
DELETE FROM dbo.Leads WHERE Id IN (@LeadTH, @LeadIN);


BEGIN TRANSACTION;

/* --------------------------------------------------------------------------
   3. THAILAND — Sathorn Prestige Interiors, THB 1,875,000
      7 digits, non-round: proves Western grouping (B1,875,000.00) and would
      expose lakh grouping immediately if the culture were wrong.
   -------------------------------------------------------------------------- */
INSERT INTO dbo.Leads
(
    Id, TenantId, FullName, Email, Phone, CompanyName, Address,
    Source, SourceId, Channel, ChannelId,
    Status, Score, EstimatedValue, Currency,
    CountryId, VerticalId, CompanyId, ContactId, OwnerUserId,
    IsConverted, ConvertedAtUtc, ConvertedBy,
    ConvertedToCompanyId, ConvertedToContactId, DealId,
    CustomFieldsJson, IsDeleted,
    CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy
)
SELECT
    @LeadTH, @TenantTH,
    N'Anongnat Thepsuwan',
    N'anongnat@siamgrandhotels.co.th',
    N'+66 81 445 2277',
    N'Siam Grand Hotels Group',
    N'999 Rama I Road, Pathum Wan, Bangkok 10330',
    t.Source, t.SourceId, t.Channel, t.ChannelId,
    2,                 -- Qualified
    84,
    1875000.00,        -- THB
    N'THB',
    t.CountryId, t.VerticalId,
    NULL, NULL,        -- fresh inbound lead: no company/contact yet
    t.OwnerUserId,
    0, NULL, NULL, NULL, NULL, NULL,
    NULL, 0,
    @Now, N'SQL test seed - round 2', NULL, NULL
FROM (
    SELECT TOP (1) Source, SourceId, Channel, ChannelId, CountryId, VerticalId, OwnerUserId
    FROM dbo.Leads
    WHERE TenantId = @TenantTH AND IsDeleted = 0 AND OwnerUserId IS NOT NULL
    ORDER BY CreatedAtUtc DESC
) AS t;


/* --------------------------------------------------------------------------
   4. INDIA — Nexora Tech Solutions, INR 4,320,000
      Renders 43,20,000 with lakh grouping vs 4,320,000 without — the
      difference is unmissable, which is the point.
   -------------------------------------------------------------------------- */
INSERT INTO dbo.Leads
(
    Id, TenantId, FullName, Email, Phone, CompanyName, Address,
    Source, SourceId, Channel, ChannelId,
    Status, Score, EstimatedValue, Currency,
    CountryId, VerticalId, CompanyId, ContactId, OwnerUserId,
    IsConverted, ConvertedAtUtc, ConvertedBy,
    ConvertedToCompanyId, ConvertedToContactId, DealId,
    CustomFieldsJson, IsDeleted,
    CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy
)
SELECT
    @LeadIN, @TenantIN,
    N'Ramesh Iyengar',
    N'ramesh.iyengar@kaveritextiles.in',
    N'+91 98450 71124',
    N'Kaveri Textiles Pvt Ltd',
    N'27 Industrial Layout, Peenya, Bengaluru 560058',
    t.Source, t.SourceId, t.Channel, t.ChannelId,
    2,                 -- Qualified
    91,
    4320000.00,        -- INR
    N'INR',
    t.CountryId, t.VerticalId,
    NULL, NULL,
    t.OwnerUserId,
    0, NULL, NULL, NULL, NULL, NULL,
    NULL, 0,
    @Now, N'SQL test seed - round 2', NULL, NULL
FROM (
    SELECT TOP (1) Source, SourceId, Channel, ChannelId, CountryId, VerticalId, OwnerUserId
    FROM dbo.Leads
    WHERE TenantId = @TenantIN AND IsDeleted = 0 AND OwnerUserId IS NOT NULL
    ORDER BY CreatedAtUtc DESC
) AS t;

COMMIT TRANSACTION;


/* --------------------------------------------------------------------------
   5. VERIFY
   -------------------------------------------------------------------------- */
PRINT '=== Seeded leads ===';

SELECT
    Tenant = CASE WHEN TenantId = @TenantTH THEN N'Sathorn (TH)' ELSE N'Nexora (IN)' END,
    Id, FullName, CompanyName, Status, Score, EstimatedValue, Currency, IsConverted
FROM dbo.Leads
WHERE Id IN (@LeadTH, @LeadIN);

GO


/* ==========================================================================
   AFTER THE UI TEST — confirms what the UI cannot show you
   --------------------------------------------------------------------------

DECLARE @TenantTH uniqueidentifier = 'BB100002-AAAA-0000-0000-000000000002';
DECLARE @TenantIN uniqueidentifier = 'BB100001-AAAA-0000-0000-000000000001';

-- Currency must be 3 chars (was '' before the fix).
-- IssueDateUtc must be exactly midnight (was 18:30 the previous day in IST).
SELECT  Number, Currency, LEN(Currency) AS Len,
        IssueDateUtc, ExpiresAtUtc,
        MidnightUtc = CASE WHEN CAST(IssueDateUtc AS time) = '00:00:00'
                           THEN N'YES' ELSE N'NO - still shifted' END,
        GrandTotal
FROM dbo.Quotes
WHERE TenantId IN (@TenantTH, @TenantIN)
ORDER BY TenantId, Number;

SELECT  Number, Currency, LEN(Currency) AS Len, IssueDateUtc, DueDateUtc, GrandTotal
FROM dbo.Invoices
WHERE TenantId IN (@TenantTH, @TenantIN)
ORDER BY TenantId, Number;

   ========================================================================== */


/* ==========================================================================
   CLEANUP — removes round-2 test data. Order matters (FKs).

DECLARE @L1 uniqueidentifier = 'DDDD0003-2222-4222-8222-000000000003';
DECLARE @L2 uniqueidentifier = 'DDDD0004-2222-4222-8222-000000000004';

DECLARE @Deals TABLE (Id uniqueidentifier);
INSERT INTO @Deals SELECT Id FROM dbo.Deals WHERE LeadId IN (@L1, @L2);

DECLARE @Quotes TABLE (Id uniqueidentifier);
INSERT INTO @Quotes SELECT Id FROM dbo.Quotes WHERE DealId IN (SELECT Id FROM @Deals);

DELETE FROM dbo.Payments        WHERE InvoiceId IN (SELECT Id FROM dbo.Invoices WHERE QuoteId IN (SELECT Id FROM @Quotes));
DELETE FROM dbo.InvoiceItems    WHERE InvoiceId IN (SELECT Id FROM dbo.Invoices WHERE QuoteId IN (SELECT Id FROM @Quotes));
DELETE FROM dbo.Invoices        WHERE QuoteId   IN (SELECT Id FROM @Quotes);
DELETE FROM dbo.PublicLinks     WHERE QuoteId   IN (SELECT Id FROM @Quotes);
DELETE FROM dbo.QuoteItems      WHERE QuoteId   IN (SELECT Id FROM @Quotes);
DELETE FROM dbo.Quotes          WHERE Id        IN (SELECT Id FROM @Quotes);
DELETE FROM dbo.DealStageHistory WHERE DealId   IN (SELECT Id FROM @Deals);
DELETE FROM dbo.Deals           WHERE Id        IN (SELECT Id FROM @Deals);
DELETE FROM dbo.Leads           WHERE Id        IN (@L1, @L2);

   NOTE: table names Payments / InvoiceItems / PublicLinks are assumed. If any
   does not exist, drop that line. Deleting the quotes will NOT free their
   numbers — by design.
   ========================================================================== */
