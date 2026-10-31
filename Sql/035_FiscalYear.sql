/* =====================================================================
   035_FiscalYear.sql
   ---------------------------------------------------------------------
   WHAT THIS DOES

     1. Countries.FiscalYearStartMonth      int NOT NULL, default 1
     2. TenantSettings.FiscalYearStartMonth int NULL  (per-tenant override)
     3. Seeds the four markets: India = 4 (April-March), Thailand,
        Philippines and UAE = 1 (calendar year)
     4. Backfills Countries.Timezone and Tenants.Timezone where they are
        blank, because a workspace with no timezone gets its financial
        year boundaries wrong by that country's offset
     5. Reports on the localization columns' data types, with optional
        tightening statements you can run later if you want them

   WHY A NULLABLE OVERRIDE ON TENANTSETTINGS
     NULL means "use my country's value". That keeps ONE source of truth:
     changing India's fiscal year would otherwise mean updating every
     Indian tenant row. Only a workspace whose financial year genuinely
     differs from its country gets a value here — an Indian group's Thai
     subsidiary reporting April-March, or a Philippine company that has
     elected a non-calendar accounting period.

   WHAT THIS DOES **NOT** DO
     It adds no financial year column to Leads, Deals, Quotes or Invoices,
     and there is no year-end rollover. Nothing carries forward: an open
     lead on 31 March is the same row on 1 April. Which financial year a
     record belongs to is DERIVED from its date, because a deal created in
     March and won in May belongs to different years depending on the
     question being asked. Store one answer and every report you have not
     written yet inherits the wrong one.

     The one exception is invoice numbering, which needs a stored year code
     because Indian GST requires a serial unique within the financial year.
     That comes with the invoice numbering round, not this one.

   HOW TO RUN
     1. Run as-is. @Commit = 0, so it rolls back. Read the grids.
     2. Change @Commit to 1 and run again.

   RE-RUN SAFETY
     Every ALTER and every constraint is guarded. The seeds only set rows
     that do not already hold the value, and the timezone backfill only
     touches rows that are NULL or blank — it never overwrites a value you
     have set by hand.
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit BIT = 0;          -- <<<< 0 = rollback (safe). 1 = keep.

DECLARE @sql nvarchar(max);

PRINT '=====================================================';
PRINT ' 035 - fiscal year';
PRINT ' @Commit = ' + CONVERT(varchar(1), @Commit)
      + CASE WHEN @Commit = 1 THEN '  (CHANGES WILL BE KEPT)'
             ELSE '  (dry run - everything rolls back)' END;
PRINT '=====================================================';


/* =====================================================================
   PRE-FLIGHT - both tables must exist before we touch anything
   ===================================================================== */

IF OBJECT_ID(N'dbo.Countries', N'U') IS NULL
BEGIN
    RAISERROR('dbo.Countries does not exist. Stopping.', 16, 1);
    RETURN;
END

IF OBJECT_ID(N'dbo.TenantSettings', N'U') IS NULL
BEGIN
    RAISERROR('dbo.TenantSettings does not exist. If your table is named differently, tell me and I will reissue this script. Stopping.', 16, 1);
    RETURN;
END

IF OBJECT_ID(N'dbo.Tenants', N'U') IS NULL
BEGIN
    RAISERROR('dbo.Tenants does not exist. Stopping.', 16, 1);
    RETURN;
END


/* =====================================================================
   PART 1 - what it looks like now (read-only, before the transaction)
   ===================================================================== */

PRINT '';
PRINT '--- Part 1: countries as they stand ---';

SELECT
    c.Code,
    c.Name,
    c.CurrencyCode,
    c.NumberFormat,
    c.Timezone,
    c.IsActive,
    c.IsDeleted
FROM dbo.Countries c
ORDER BY c.DisplayOrder, c.Code;

-- FiscalYearStartMonth is deliberately NOT selected above: on a first run
-- the column does not exist yet, and a plain SELECT naming a missing column
-- fails at execution. Part 3a shows it, through sp_executesql, once it does.
PRINT '  (fiscal start month is shown in Part 3a, after it has been added)';


/* =====================================================================
   PART 2 - THE TRANSACTION
   ===================================================================== */

DECLARE @AddedCountryCol  bit = 0;
DECLARE @AddedSettingsCol bit = 0;
DECLARE @SeededCountries  int = 0;
DECLARE @FixedCountryTz   int = 0;
DECLARE @FixedTenantTz    int = 0;

BEGIN TRAN;

BEGIN TRY

    /* ---- 2a. Countries.FiscalYearStartMonth ------------------------- */

    IF COL_LENGTH('dbo.Countries', 'FiscalYearStartMonth') IS NULL
    BEGIN
        ALTER TABLE dbo.Countries
            ADD FiscalYearStartMonth int NOT NULL
                CONSTRAINT DF_Countries_FiscalYearStartMonth DEFAULT (1);

        SET @AddedCountryCol = 1;
        PRINT '  added Countries.FiscalYearStartMonth';
    END
    ELSE
        PRINT '  Countries.FiscalYearStartMonth already present';

    /* ---- 2b. TenantSettings.FiscalYearStartMonth -------------------- */

    IF COL_LENGTH('dbo.TenantSettings', 'FiscalYearStartMonth') IS NULL
    BEGIN
        -- NULL means "inherit from my country". No default, on purpose:
        -- a default here would duplicate the country's value into every
        -- tenant row and then drift from it.
        ALTER TABLE dbo.TenantSettings
            ADD FiscalYearStartMonth int NULL;

        SET @AddedSettingsCol = 1;
        PRINT '  added TenantSettings.FiscalYearStartMonth';
    END
    ELSE
        PRINT '  TenantSettings.FiscalYearStartMonth already present';

    /* ---- 2c. CHECK constraints -------------------------------------- */
    -- Through sp_executesql: these reference columns that may have been
    -- added moments ago in this same batch.

    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
                   WHERE name = N'CK_Countries_FiscalYearStartMonth'
                     AND parent_object_id = OBJECT_ID(N'dbo.Countries'))
    BEGIN
        SET @sql = N'ALTER TABLE dbo.Countries
                        ADD CONSTRAINT CK_Countries_FiscalYearStartMonth
                        CHECK ([FiscalYearStartMonth] BETWEEN 1 AND 12);';
        EXEC sp_executesql @sql;
        PRINT '  added CK_Countries_FiscalYearStartMonth';
    END

    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
                   WHERE name = N'CK_TenantSettings_FiscalYearStartMonth'
                     AND parent_object_id = OBJECT_ID(N'dbo.TenantSettings'))
    BEGIN
        -- NULL passes a CHECK in SQL Server, which is exactly what we want:
        -- null is the normal "inherit" case, 0 and 13 are not.
        SET @sql = N'ALTER TABLE dbo.TenantSettings
                        ADD CONSTRAINT CK_TenantSettings_FiscalYearStartMonth
                        CHECK ([FiscalYearStartMonth] IS NULL
                            OR [FiscalYearStartMonth] BETWEEN 1 AND 12);';
        EXEC sp_executesql @sql;
        PRINT '  added CK_TenantSettings_FiscalYearStartMonth';
    END

    /* ---- 2d. seed the four markets ---------------------------------- */
    -- India is the only one of the four with a statutory non-calendar
    -- year: 1 April to 31 March. Thailand, the Philippines and the UAE
    -- all default to the calendar year, while permitting a company to
    -- elect otherwise - which is what the TenantSettings override is for.

    -- @n1 / @n2 are declared BEFORE the updates and assigned with SET
    -- immediately after each one. DECLARE ... = @@ROWCOUNT would be
    -- reading a value the DECLARE statement itself has already reset.
    SET @sql = N'
    DECLARE @n1 int = 0, @n2 int = 0;

    UPDATE dbo.Countries
    SET FiscalYearStartMonth = 4
    WHERE Code = ''IN'' AND FiscalYearStartMonth <> 4;
    SET @n1 = @@ROWCOUNT;

    UPDATE dbo.Countries
    SET FiscalYearStartMonth = 1
    WHERE Code IN (''TH'', ''PH'', ''AE'') AND FiscalYearStartMonth <> 1;
    SET @n2 = @@ROWCOUNT;

    SET @Seeded = @n1 + @n2;';

    EXEC sp_executesql @sql, N'@Seeded int OUTPUT', @Seeded = @SeededCountries OUTPUT;

    PRINT '  seeded fiscal start month on ' + CONVERT(varchar(20), @SeededCountries) + ' country row(s)';

    /* ---- 2e. timezone backfill -------------------------------------- */
    -- Only where blank. A tenant with no timezone has its financial year
    -- boundaries computed in UTC, which for India is a 5h30m error: a deal
    -- closed at 00:30 IST on 1 April lands in the PREVIOUS financial year.
    -- IANA identifiers - .NET 6 and later accept these on Windows too.

    UPDATE dbo.Countries
    SET Timezone = CASE Code
                     WHEN 'IN' THEN 'Asia/Kolkata'
                     WHEN 'TH' THEN 'Asia/Bangkok'
                     WHEN 'PH' THEN 'Asia/Manila'
                     WHEN 'AE' THEN 'Asia/Dubai'
                   END
    WHERE Code IN ('IN', 'TH', 'PH', 'AE')
      AND (Timezone IS NULL OR LTRIM(RTRIM(Timezone)) = '');

    SET @FixedCountryTz = @@ROWCOUNT;
    PRINT '  filled Countries.Timezone on ' + CONVERT(varchar(20), @FixedCountryTz) + ' row(s)';

    UPDATE t
    SET t.Timezone = c.Timezone
    FROM dbo.Tenants t
    JOIN dbo.Countries c ON c.Id = t.CountryId
    WHERE (t.Timezone IS NULL OR LTRIM(RTRIM(t.Timezone)) = '')
      AND c.Timezone IS NOT NULL
      AND LTRIM(RTRIM(c.Timezone)) <> '';

    SET @FixedTenantTz = @@ROWCOUNT;
    PRINT '  filled Tenants.Timezone on ' + CONVERT(varchar(20), @FixedTenantTz) + ' row(s)';


    /* =================================================================
       PART 3 - VERIFICATION (inside the transaction, visible on a dry run)
       ================================================================= */

    PRINT '';
    PRINT '--- Part 3a: verify - countries ---';

    SET @sql = N'
    SELECT
        c.Code,
        c.Name,
        c.FiscalYearStartMonth,
        FiscalYearRuns = CASE c.FiscalYearStartMonth
                           WHEN 1 THEN ''Jan - Dec (calendar year)''
                           WHEN 4 THEN ''Apr - Mar''
                           ELSE ''month '' + CONVERT(varchar(2), c.FiscalYearStartMonth)
                                + '' onwards'' END,
        c.Timezone,
        TimezoneOk = CASE WHEN c.Timezone IS NULL
                           OR LTRIM(RTRIM(c.Timezone)) = ''''
                          THEN ''MISSING - fiscal boundaries will use UTC''
                          ELSE ''ok'' END
    FROM dbo.Countries c
    ORDER BY c.DisplayOrder, c.Code;';

    EXEC sp_executesql @sql;

    PRINT '';
    PRINT '--- Part 3b: verify - tenants and their effective fiscal year ---';
    PRINT '    (this is exactly what IFiscalYearService will resolve)';

    SET @sql = N'
    SELECT
        t.Name                AS Tenant,
        Country               = c.Code,
        OverrideOnTenant      = s.FiscalYearStartMonth,
        CountryDefault        = c.FiscalYearStartMonth,
        EffectiveStartMonth   = COALESCE(s.FiscalYearStartMonth, c.FiscalYearStartMonth, 1),
        EffectiveTimezone     = COALESCE(NULLIF(LTRIM(RTRIM(t.Timezone)), ''''),
                                         NULLIF(LTRIM(RTRIM(c.Timezone)), ''''),
                                         ''UTC (fallback - check this)''),
        HasSettingsRow        = CASE WHEN s.TenantId IS NULL THEN ''NO'' ELSE ''yes'' END
    FROM dbo.Tenants t
    LEFT JOIN dbo.Countries      c ON c.Id = t.CountryId
    LEFT JOIN dbo.TenantSettings s ON s.TenantId = t.Id
    WHERE t.IsDeleted = 0
    ORDER BY t.Name;';

    EXEC sp_executesql @sql;

    PRINT '';
    PRINT '--- Part 3c: anything that will silently fall back ---';
    PRINT '    (every row here reports on the wrong year or the wrong day';
    PRINT '     boundary - fix these before you trust a fiscal report)';

    SET @sql = N'
    SELECT
        t.Name AS Tenant,
        Problem = CASE
            WHEN t.CountryId IS NULL
                THEN ''No country - fiscal year defaults to Jan-Dec''
            WHEN s.TenantId IS NULL
                THEN ''No TenantSettings row - override cannot be stored''
            WHEN COALESCE(NULLIF(LTRIM(RTRIM(t.Timezone)), ''''),
                          NULLIF(LTRIM(RTRIM(c.Timezone)), '''')) IS NULL
                THEN ''No timezone - day boundaries computed in UTC''
        END
    FROM dbo.Tenants t
    LEFT JOIN dbo.Countries      c ON c.Id = t.CountryId
    LEFT JOIN dbo.TenantSettings s ON s.TenantId = t.Id
    WHERE t.IsDeleted = 0
      AND (t.CountryId IS NULL
        OR s.TenantId IS NULL
        OR COALESCE(NULLIF(LTRIM(RTRIM(t.Timezone)), ''''),
                    NULLIF(LTRIM(RTRIM(c.Timezone)), '''')) IS NULL)
    ORDER BY t.Name;';

    EXEC sp_executesql @sql;

    PRINT '';
    PRINT '--- summary ---';
    PRINT '  Countries column added      : ' + CASE WHEN @AddedCountryCol  = 1 THEN 'yes' ELSE 'already there' END;
    PRINT '  TenantSettings column added : ' + CASE WHEN @AddedSettingsCol = 1 THEN 'yes' ELSE 'already there' END;
    PRINT '  Country rows seeded         : ' + CONVERT(varchar(20), @SeededCountries);
    PRINT '  Country timezones filled    : ' + CONVERT(varchar(20), @FixedCountryTz);
    PRINT '  Tenant timezones filled     : ' + CONVERT(varchar(20), @FixedTenantTz);


    /* =================================================================
       COMMIT or ROLLBACK
       ================================================================= */

    IF @Commit = 1
    BEGIN
        COMMIT TRAN;
        PRINT '';
        PRINT '*** COMMITTED. ***';
    END
    ELSE
    BEGIN
        ROLLBACK TRAN;
        PRINT '';
        PRINT '*** ROLLED BACK (dry run). Nothing was changed. ***';
        PRINT '*** Set @Commit = 1 at the top and run again to keep it. ***';
    END

END TRY
BEGIN CATCH

    IF XACT_STATE() <> 0
        ROLLBACK TRAN;

    PRINT '';
    PRINT '*** FAILED - rolled back. Nothing was changed. ***';

    DECLARE @ErrMsg  nvarchar(4000) = ERROR_MESSAGE();
    DECLARE @ErrLine int            = ERROR_LINE();
    RAISERROR('035 failed at line %d: %s', 16, 1, @ErrLine, @ErrMsg);

END CATCH;


/* =====================================================================
   PART 4 - OPTIONAL, NOT RUN: the localization columns' data types
   ---------------------------------------------------------------------
   Only Code and Name were ever configured in CountryConfiguration, so
   the rest were created by EF convention as nvarchar(max). That is not
   breaking anything, but a 3-character currency code in an unlimited
   column cannot be indexed and sorts badly.

   Round 035's CountryConfiguration.cs now declares proper lengths. This
   grid shows what the database actually has. If you want to match them,
   the statements underneath do it - read the note first.
   ===================================================================== */

PRINT '';
PRINT '--- Part 4: Countries column types (read-only, nothing is changed) ---';

SELECT
    ColumnName = c.name,
    DataType   = ty.name,
    MaxChars   = CASE WHEN c.max_length = -1 THEN -1                 -- -1 = MAX
                      WHEN ty.name LIKE 'n%' THEN c.max_length / 2
                      ELSE c.max_length END,
    Nullable   = c.is_nullable,
    WantedByEf = CASE c.name
                   WHEN 'Code'           THEN 5
                   WHEN 'Name'           THEN 100
                   WHEN 'DialCode'       THEN 10
                   WHEN 'CurrencyCode'   THEN 3
                   WHEN 'CurrencySymbol' THEN 10
                   WHEN 'TaxLabel'       THEN 20
                   WHEN 'NumberFormat'   THEN 20
                   WHEN 'DateFormat'     THEN 30
                   WHEN 'TimeFormat'     THEN 30
                   WHEN 'Timezone'       THEN 64
                 END
FROM sys.columns c
JOIN sys.types   ty ON ty.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID(N'dbo.Countries')
  AND c.name IN ('Code','Name','DialCode','CurrencyCode','CurrencySymbol',
                 'TaxLabel','NumberFormat','DateFormat','TimeFormat','Timezone')
ORDER BY c.column_id;

/* ---------------------------------------------------------------------
   TO TIGHTEN THEM (optional, run separately, and read this first)

   Narrowing a column FAILS if any row holds a longer value, so check
   first:

       SELECT MAX(LEN(DialCode))       AS DialCode,
              MAX(LEN(CurrencyCode))   AS CurrencyCode,
              MAX(LEN(CurrencySymbol)) AS CurrencySymbol,
              MAX(LEN(TaxLabel))       AS TaxLabel,
              MAX(LEN(NumberFormat))   AS NumberFormat,
              MAX(LEN(DateFormat))     AS DateFormat,
              MAX(LEN(TimeFormat))     AS TimeFormat,
              MAX(LEN(Timezone))       AS Timezone
       FROM dbo.Countries;

   Every figure must be at or below the target below. Then, in a
   transaction of its own:

       ALTER TABLE dbo.Countries ALTER COLUMN DialCode       nvarchar(10)  NULL;
       ALTER TABLE dbo.Countries ALTER COLUMN CurrencyCode   nvarchar(3)   NULL;
       ALTER TABLE dbo.Countries ALTER COLUMN CurrencySymbol nvarchar(10)  NULL;
       ALTER TABLE dbo.Countries ALTER COLUMN TaxLabel       nvarchar(20)  NULL;
       ALTER TABLE dbo.Countries ALTER COLUMN NumberFormat   nvarchar(20)  NULL;
       ALTER TABLE dbo.Countries ALTER COLUMN DateFormat     nvarchar(30)  NULL;
       ALTER TABLE dbo.Countries ALTER COLUMN TimeFormat     nvarchar(30)  NULL;
       ALTER TABLE dbo.Countries ALTER COLUMN Timezone       nvarchar(64)  NULL;

   There is no hurry. EF works against nvarchar(max) perfectly well; this
   is tidiness, not correctness.
   --------------------------------------------------------------------- */
