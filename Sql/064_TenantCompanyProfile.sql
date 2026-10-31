-- =====================================================================
-- 064_TenantCompanyProfile.sql
--
-- WHO THE QUOTE IS FROM.
--
-- dbo.Tenants has Name, Phone, FromEmail, ReplyToEmail, Domain and
-- CountryId. It has no address and no tax number, so the public quote
-- page shows the customer their own company and never names the seller,
-- and a Thai VAT or Indian GST document cannot carry the number it is
-- legally expected to carry.
--
-- NINE NULLABLE COLUMNS. Nothing is backfilled, nothing is made NOT
-- NULL, no existing row changes. A workspace that never fills these in
-- behaves exactly as it does today.
--
--   LegalName        NVARCHAR(200)   "Sathorn Prestige Interiors Co., Ltd."
--   AddressLine1     NVARCHAR(200)
--   AddressLine2     NVARCHAR(200)
--   City             NVARCHAR(100)
--   State            NVARCHAR(100)
--   PostalCode       NVARCHAR(20)
--   Website          NVARCHAR(255)
--   TaxNumber        NVARCHAR(50)
--   TaxNumberLabel   NVARCHAR(50)
--
-- WHY ONE TaxNumber AND NOT Gstin + VatNumber + Tin + Trn
--   You sell into India, Thailand, the Philippines and the UAE. A column
--   per country is three empty columns for every workspace plus a fourth
--   the day you add a market. One column holds the number; TaxNumberLabel
--   holds what that country calls it, defaulted from Country.TaxLabel at
--   the UI, so the quote prints "GSTIN" or "เลขประจำตัวผู้เสียภาษี"
--   without the schema knowing anything about countries.
--
-- WHY LegalName IS SEPARATE FROM Name
--   Name is what the workspace is called in the app and in the sidebar.
--   A tax document needs the registered entity — "… Co., Ltd." — and the
--   two are usually not the same string. NULL means "they are the same",
--   which is the common case, and the reader falls back to Name.
--
-- NO INDEXES. Nothing queries on any of these; they are read with the
-- tenant row and printed.
--
-- SAFE TO RE-RUN. Every ALTER is guarded by a sys.columns check.
--
-- ⚠ @Commit IS 0. Read the verification grids at the bottom, then set it
-- to 1 and run again.
-- =====================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit BIT = 0;

BEGIN TRAN;

BEGIN TRY

    -- A log of what this script actually did, printed at the end.
    DECLARE @Changes TABLE (
        Action      NVARCHAR(40),
        TableName   NVARCHAR(128),
        ColumnName  NVARCHAR(128),
        Detail      NVARCHAR(200)
    );

    DECLARE @Tenants INT = OBJECT_ID('dbo.Tenants');

    IF @Tenants IS NULL
        THROW 51064, 'dbo.Tenants does not exist. Wrong database?', 1;

    -- =================================================================
    -- 1. THE COLUMNS
    --
    -- One guarded ALTER each rather than a single ALTER with nine ADDs,
    -- so a half-applied script (someone ran it, it failed on column six)
    -- picks up exactly where it stopped instead of failing on column one
    -- and doing nothing.
    -- =================================================================

    IF NOT EXISTS (SELECT 1 FROM sys.columns
                   WHERE object_id = @Tenants AND name = 'LegalName')
    BEGIN
        ALTER TABLE dbo.Tenants ADD LegalName NVARCHAR(200) NULL;
        INSERT INTO @Changes VALUES ('added column', 'Tenants', 'LegalName', 'NVARCHAR(200) NULL');
        PRINT 'Tenants.LegalName -> added';
    END
    ELSE PRINT 'Tenants.LegalName already exists - skipped';

    IF NOT EXISTS (SELECT 1 FROM sys.columns
                   WHERE object_id = @Tenants AND name = 'AddressLine1')
    BEGIN
        ALTER TABLE dbo.Tenants ADD AddressLine1 NVARCHAR(200) NULL;
        INSERT INTO @Changes VALUES ('added column', 'Tenants', 'AddressLine1', 'NVARCHAR(200) NULL');
        PRINT 'Tenants.AddressLine1 -> added';
    END
    ELSE PRINT 'Tenants.AddressLine1 already exists - skipped';

    IF NOT EXISTS (SELECT 1 FROM sys.columns
                   WHERE object_id = @Tenants AND name = 'AddressLine2')
    BEGIN
        ALTER TABLE dbo.Tenants ADD AddressLine2 NVARCHAR(200) NULL;
        INSERT INTO @Changes VALUES ('added column', 'Tenants', 'AddressLine2', 'NVARCHAR(200) NULL');
        PRINT 'Tenants.AddressLine2 -> added';
    END
    ELSE PRINT 'Tenants.AddressLine2 already exists - skipped';

    IF NOT EXISTS (SELECT 1 FROM sys.columns
                   WHERE object_id = @Tenants AND name = 'City')
    BEGIN
        ALTER TABLE dbo.Tenants ADD City NVARCHAR(100) NULL;
        INSERT INTO @Changes VALUES ('added column', 'Tenants', 'City', 'NVARCHAR(100) NULL');
        PRINT 'Tenants.City -> added';
    END
    ELSE PRINT 'Tenants.City already exists - skipped';

    IF NOT EXISTS (SELECT 1 FROM sys.columns
                   WHERE object_id = @Tenants AND name = 'State')
    BEGIN
        ALTER TABLE dbo.Tenants ADD State NVARCHAR(100) NULL;
        INSERT INTO @Changes VALUES ('added column', 'Tenants', 'State', 'NVARCHAR(100) NULL');
        PRINT 'Tenants.State -> added';
    END
    ELSE PRINT 'Tenants.State already exists - skipped';

    IF NOT EXISTS (SELECT 1 FROM sys.columns
                   WHERE object_id = @Tenants AND name = 'PostalCode')
    BEGIN
        ALTER TABLE dbo.Tenants ADD PostalCode NVARCHAR(20) NULL;
        INSERT INTO @Changes VALUES ('added column', 'Tenants', 'PostalCode', 'NVARCHAR(20) NULL');
        PRINT 'Tenants.PostalCode -> added';
    END
    ELSE PRINT 'Tenants.PostalCode already exists - skipped';

    IF NOT EXISTS (SELECT 1 FROM sys.columns
                   WHERE object_id = @Tenants AND name = 'Website')
    BEGIN
        ALTER TABLE dbo.Tenants ADD Website NVARCHAR(255) NULL;
        INSERT INTO @Changes VALUES ('added column', 'Tenants', 'Website', 'NVARCHAR(255) NULL');
        PRINT 'Tenants.Website -> added';
    END
    ELSE PRINT 'Tenants.Website already exists - skipped';

    IF NOT EXISTS (SELECT 1 FROM sys.columns
                   WHERE object_id = @Tenants AND name = 'TaxNumber')
    BEGIN
        ALTER TABLE dbo.Tenants ADD TaxNumber NVARCHAR(50) NULL;
        INSERT INTO @Changes VALUES ('added column', 'Tenants', 'TaxNumber', 'NVARCHAR(50) NULL');
        PRINT 'Tenants.TaxNumber -> added';
    END
    ELSE PRINT 'Tenants.TaxNumber already exists - skipped';

    IF NOT EXISTS (SELECT 1 FROM sys.columns
                   WHERE object_id = @Tenants AND name = 'TaxNumberLabel')
    BEGIN
        ALTER TABLE dbo.Tenants ADD TaxNumberLabel NVARCHAR(50) NULL;
        INSERT INTO @Changes VALUES ('added column', 'Tenants', 'TaxNumberLabel', 'NVARCHAR(50) NULL');
        PRINT 'Tenants.TaxNumberLabel -> added';
    END
    ELSE PRINT 'Tenants.TaxNumberLabel already exists - skipped';

    -- =================================================================
    -- 2. SEED TaxNumberLabel FROM THE COUNTRY
    --
    -- Through sp_executesql, because TaxNumberLabel may have been added
    -- by THIS batch. SQL Server parses the whole batch before running
    -- any of it, so a direct reference to a column that does not exist
    -- yet is a compile error even though the ALTER above would have
    -- created it by the time the statement ran.
    --
    -- Only rows where it is still NULL, so re-running never overwrites a
    -- label somebody has edited. The NUMBER itself is never guessed —
    -- only what the country calls it.
    -- =================================================================

    DECLARE @Seeded INT = 0;

    EXEC sp_executesql N'
        UPDATE t
           SET t.TaxNumberLabel = c.TaxLabel
          FROM dbo.Tenants t
          JOIN dbo.Countries c ON c.Id = t.CountryId
         WHERE t.TaxNumberLabel IS NULL
           AND c.TaxLabel IS NOT NULL
           AND LTRIM(RTRIM(c.TaxLabel)) <> '''';
        SELECT @rows = @@ROWCOUNT;',
        N'@rows INT OUTPUT', @rows = @Seeded OUTPUT;

    IF @Seeded > 0
        INSERT INTO @Changes
        VALUES ('seeded', 'Tenants', 'TaxNumberLabel',
                CAST(@Seeded AS NVARCHAR(10)) + ' row(s) from Countries.TaxLabel');

    PRINT 'Tenants.TaxNumberLabel -> seeded ' + CAST(@Seeded AS NVARCHAR(10)) + ' row(s) from Countries.TaxLabel';

    -- =================================================================
    -- VERIFICATION
    -- =================================================================

    PRINT '';
    PRINT '=== WHAT THIS RUN CHANGED ===';
    SELECT Action, TableName, ColumnName, Detail FROM @Changes;

    PRINT '=== THE NINE COLUMNS, AS THEY NOW STAND ===';
    SELECT  c.name                AS ColumnName,
            t.name                AS DataType,
            c.max_length / 2      AS MaxChars,     -- NVARCHAR: bytes / 2
            c.is_nullable         AS IsNullable
      FROM  sys.columns c
      JOIN  sys.types   t ON t.user_type_id = c.user_type_id
     WHERE  c.object_id = @Tenants
       AND  c.name IN ('LegalName','AddressLine1','AddressLine2','City','State',
                       'PostalCode','Website','TaxNumber','TaxNumberLabel')
     ORDER BY c.column_id;

    PRINT '=== EVERY WORKSPACE, AND HOW MUCH OF ITS PROFILE IS FILLED IN ===';
    EXEC sp_executesql N'
        SELECT  t.Name,
                c.Code                AS Country,
                t.TaxNumberLabel,
                CASE WHEN t.TaxNumber    IS NULL THEN ''no''  ELSE ''yes'' END AS HasTaxNumber,
                CASE WHEN t.AddressLine1 IS NULL THEN ''no''  ELSE ''yes'' END AS HasAddress,
                CASE WHEN t.LegalName    IS NULL THEN ''(uses Name)'' ELSE t.LegalName END AS LegalName
          FROM  dbo.Tenants t
          LEFT  JOIN dbo.Countries c ON c.Id = t.CountryId
         WHERE  t.IsDeleted = 0
         ORDER BY t.Name;';

    PRINT '';
    PRINT 'Nothing is required. A workspace with none of this filled in behaves';
    PRINT 'exactly as it does today — the quote simply does not name the seller,';
    PRINT 'which is what it has been doing all along.';

END TRY
BEGIN CATCH
    PRINT '=== FAILED ===';
    PRINT ERROR_MESSAGE();
    IF @@TRANCOUNT > 0 ROLLBACK;
    THROW;
END CATCH

IF @Commit = 1
BEGIN
    COMMIT;
    PRINT '>>> COMMITTED';
END
ELSE
BEGIN
    ROLLBACK;
    PRINT '>>> ROLLED BACK (set @Commit = 1 at the top and run again)';
END
GO
