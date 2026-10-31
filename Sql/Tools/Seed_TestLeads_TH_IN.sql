/* ==========================================================================
   Seed_TestLeads_TH_IN.sql
   --------------------------------------------------------------------------
   Creates ONE qualified lead each for:
     - Sathorn Prestige Interiors (Thailand, THB)  BB100002-AAAA-...-002
     - Nexora Tech Solutions      (India,    INR)  BB100001-AAAA-...-001

   Purpose: end-to-end Lead -> Deal -> Quote test after the quote/invoice
   number-generator fix (max-of-sequence + duplicate-key retry).

   Design notes:
     * Lead.Status is stored as INT. 0=New 1=Working 2=Qualified 3=Unqualified.
       Both leads are seeded as 2 (Qualified) so "Convert to Deal" is available
       immediately.
     * Channel, ChannelId, SourceId, VerticalId, CountryId, OwnerUserId and the
       Source string are COPIED from an existing lead on the same tenant. These
       are FKs / enums whose valid values differ per tenant - copying is safer
       than hardcoding and keeps the lead owned by a real tenant user.
     * EstimatedValue is deliberately >= 1,000,000 on both. Lakh and Western
       grouping are identical below 6 digits - a 5-digit test value proves
       nothing. THB 1,250,000 vs INR 27,50,000 will differ visibly.
     * Every string literal is N-prefixed. Non-prefixed literals are what
       turned Thai/Indian currency symbols into '?' previously.
     * Fixed GUIDs (not NEWID()) so the script is idempotent and the cleanup
       block at the bottom is exact.

   Run against: MadeeFlow
   ========================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @TenantTH uniqueidentifier = 'BB100002-AAAA-0000-0000-000000000002';
DECLARE @TenantIN uniqueidentifier = 'BB100001-AAAA-0000-0000-000000000001';

DECLARE @LeadTH  uniqueidentifier = 'DDDD0001-1111-4111-8111-000000000001';
DECLARE @LeadIN  uniqueidentifier = 'DDDD0002-1111-4111-8111-000000000002';

DECLARE @Now datetime2(7) = SYSUTCDATETIME();


/* --------------------------------------------------------------------------
   0. PRE-FLIGHT - what the number generators should produce
   -------------------------------------------------------------------------- */
PRINT '--- Current quote high-water marks (expect next = max + 1) ---';

SELECT
    TenantName = CASE WHEN TenantId = @TenantTH THEN N'Sathorn (TH)'
                      WHEN TenantId = @TenantIN THEN N'Nexora (IN)' END,
    QuoteCount = COUNT(*),
    MaxNumber  = MAX(Number),
    NextExpected = N'QUO-' + RIGHT(N'0000' + CAST(
        MAX(TRY_CAST(SUBSTRING(Number, 5, 20) AS int)) + 1 AS nvarchar(10)), 4)
FROM dbo.Quotes
WHERE TenantId IN (@TenantTH, @TenantIN)
  AND Number LIKE N'QUO-%'
GROUP BY TenantId;

/* NOTE: this MAX(...) is exactly what the fixed GenerateQuoteNumberAsync now
   does. The OLD code took the number of the row with the newest CreatedAtUtc,
   which on Nexora was QUO-0002 -> handed out QUO-0003 -> unique index
   violation on UX_Quotes_TenantId_Number. */


/* --------------------------------------------------------------------------
   1. GUARD - both tenants need at least one existing lead to copy FKs from
   -------------------------------------------------------------------------- */
IF NOT EXISTS (SELECT 1 FROM dbo.Leads WHERE TenantId = @TenantTH AND IsDeleted = 0)
BEGIN
    RAISERROR (N'No existing lead on Sathorn (TH) to use as an FK template. Aborting.', 16, 1);
    RETURN;
END

IF NOT EXISTS (SELECT 1 FROM dbo.Leads WHERE TenantId = @TenantIN AND IsDeleted = 0)
BEGIN
    RAISERROR (N'No existing lead on Nexora (IN) to use as an FK template. Aborting.', 16, 1);
    RETURN;
END


/* --------------------------------------------------------------------------
   2. Remove any previous run of this script (makes it re-runnable)
   -------------------------------------------------------------------------- */
DELETE FROM dbo.Leads WHERE Id IN (@LeadTH, @LeadIN);


BEGIN TRANSACTION;

/* --------------------------------------------------------------------------
   3. THAILAND - Sathorn Prestige Interiors, THB 1,250,000
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
    @LeadTH,
    @TenantTH,
    N'Kittipong Rattanakul',
    N'kittipong@riversidebkk.co.th',
    N'+66 82 114 7790',
    N'Bangkok Riverside Residences',
    N'188 Charoen Nakhon Road, Khlong San, Bangkok 10600',
    t.Source,          -- copied: Source string must stay consistent with SourceId
    t.SourceId,        -- copied FK
    t.Channel,         -- copied enum
    t.ChannelId,       -- copied FK
    2,                 -- Status = Qualified
    78,                -- Score
    1250000.00,        -- 7 digits: proves Western grouping (B1,250,000.00)
    N'THB',
    t.CountryId,       -- copied FK
    t.VerticalId,      -- copied FK
    NULL,              -- CompanyId  - fresh inbound lead
    NULL,              -- ContactId  - fresh inbound lead
    t.OwnerUserId,     -- copied: keeps the lead owned by a real tenant user
    0, NULL, NULL,
    NULL, NULL, NULL,
    NULL,
    0,
    @Now,
    N'SQL test seed - quote numbering',
    NULL, NULL
FROM (
    SELECT TOP (1) Source, SourceId, Channel, ChannelId, CountryId, VerticalId, OwnerUserId
    FROM dbo.Leads
    WHERE TenantId = @TenantTH AND IsDeleted = 0 AND OwnerUserId IS NOT NULL
    ORDER BY CreatedAtUtc DESC
) AS t;


/* --------------------------------------------------------------------------
   4. INDIA - Nexora Tech Solutions, INR 2,750,000
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
    @LeadIN,
    @TenantIN,
    N'Priya Venkatraman',
    N'priya.v@vaishnaviinfotech.in',
    N'+91 98490 33215',
    N'Vaishnavi Infotech Pvt Ltd',
    N'Plot 42, HITEC City, Madhapur, Hyderabad 500081',
    t.Source,
    t.SourceId,
    t.Channel,
    t.ChannelId,
    2,                 -- Status = Qualified
    82,                -- Score
    2750000.00,        -- 7 digits: proves lakh grouping (Rs 27,50,000.00)
    N'INR',
    t.CountryId,
    t.VerticalId,
    NULL,
    NULL,
    t.OwnerUserId,
    0, NULL, NULL,
    NULL, NULL, NULL,
    NULL,
    0,
    @Now,
    N'SQL test seed - quote numbering',
    NULL, NULL
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
PRINT '--- Seeded leads ---';

SELECT
    Tenant = CASE WHEN TenantId = @TenantTH THEN N'Sathorn (TH)' ELSE N'Nexora (IN)' END,
    Id, FullName, CompanyName, Status, Score,
    EstimatedValue, Currency, OwnerUserId, IsConverted
FROM dbo.Leads
WHERE Id IN (@LeadTH, @LeadIN);

GO


/* ==========================================================================
   CLEANUP - run this to remove the test data after the test pass.
   Order matters: QuoteItems -> Quotes -> DealStageHistory -> Deals -> Leads.
   --------------------------------------------------------------------------

DECLARE @LeadTH uniqueidentifier = 'DDDD0001-1111-4111-8111-000000000001';
DECLARE @LeadIN uniqueidentifier = 'DDDD0002-1111-4111-8111-000000000002';

DECLARE @Deals TABLE (Id uniqueidentifier);
INSERT INTO @Deals SELECT Id FROM dbo.Deals WHERE LeadId IN (@LeadTH, @LeadIN);

DELETE FROM dbo.QuoteItems
WHERE QuoteId IN (SELECT Id FROM dbo.Quotes WHERE DealId IN (SELECT Id FROM @Deals));

DELETE FROM dbo.Quotes           WHERE DealId IN (SELECT Id FROM @Deals);
DELETE FROM dbo.DealStageHistory WHERE DealId IN (SELECT Id FROM @Deals);
DELETE FROM dbo.Deals            WHERE Id     IN (SELECT Id FROM @Deals);
DELETE FROM dbo.Leads            WHERE Id     IN (@LeadTH, @LeadIN);

   ========================================================================== */
