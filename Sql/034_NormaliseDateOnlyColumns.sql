/* =====================================================================
   034_NormaliseDateOnlyColumns.sql
   ---------------------------------------------------------------------
   PURPOSE
       Repair the three date-ONLY columns that were shifted by the
       .ToUniversalTime() bug in the Admin.Web page models:

           Quotes.IssueDateUtc
           Quotes.ExpiresAtUtc
           Deals.ExpectedCloseDateUtc

   WHY THEY ARE WRONG
       <input type="date"> posts midnight with Kind = Unspecified.
       .ToUniversalTime() treats Unspecified as LOCAL time, so it
       subtracted the web server's UTC offset. A date the user picked as
       2026-09-25 was stored as 2026-09-24 18:30 (server at UTC+5:30),
       2026-09-24 17:00 (UTC+7), 2026-09-24 16:00 (UTC+8 / UTC+4... etc.)

       The shift is applied EXACTLY ONCE, not once per save:
         stored -> .ToLocalTime() -> the date box shows the right day ->
         POST -> .ToUniversalTime() -> shifted -> next GET shows the
         right day again. So it does not compound.

       It is applied only in the Razor page models. Verified against the
       API handlers: CreateQuoteHandler, UpdateQuoteHandler,
       CreateDealHandler, UpdateDealHandler and ConvertLeadToDealHandler
       all assign dto.<X> straight through with NO conversion of their
       own. That is what makes this repair safe: there is exactly one
       offset to undo.

   WHAT IS AFFECTED
       Quotes  - only rows saved through the Quote EDIT page.
                 Quotes/Create was already fixed with
                 DateTime.SpecifyKind(..., DateTimeKind.Utc).
       Deals   - much wider. Pipeline/Create, Pipeline/Edit AND
                 Leads/ConvertToDeal all used .ToUniversalTime(), so
                 essentially every deal is a candidate.

   HOW THE REPAIR WORKS
       Round each value to the nearest midnight:

           CAST(CAST(DATEADD(minute, 719, col) AS date) AS datetime2(7))

       Adding 11h59m and then truncating to a date snaps the value to
       whichever midnight it is closest to. This derives the offset
       IMPLICITLY per row - the script never needs to know what timezone
       the web server was in, and it works whether the offset was
       positive or negative, as long as it is inside +/- 12 hours.
       India (+5:30), Thailand (+7), Philippines (+8) and UAE (+4) are
       all comfortably inside that. A value already at midnight rounds
       to itself, which is why this script is safe to run over and over.

   WHAT IS **NOT** REPAIRED
       Deals.ActualCloseDateUtc is deliberately left alone. In
       DealsCommandHandlers it is written as:
           deal.ActualCloseDateUtc ??= dto.ActualCloseDateUtc
                                       ?? DateTime.UtcNow;
       so that column holds a MIXTURE of user-picked dates (which should
       be midnight) and real closing timestamps (which legitimately are
       not). There is no way to tell the two apart from the data, so
       rounding it would destroy genuine timestamps. Part 5 reports on
       it so you can see the situation; it issues no UPDATE.

   HOW TO RUN
       1. Run the whole script as-is. @Commit = 0, so it ROLLS BACK.
          Read every grid, especially Part 1.
       2. Part 1 is the go / no-go check. You should see ONE time-of-day
          value per column (e.g. 18:30:00 only), or a small handful if
          the app has moved between servers. A wide scatter of random
          times means something OTHER than this bug is at work - stop
          and tell me.
       3. When the grids look right, change
                DECLARE @Commit BIT = 0;
          to
                DECLARE @Commit BIT = 1;
          and run it again.

   RE-RUN SAFETY
       Fully idempotent. Rounding midnight to midnight is a no-op, so a
       second run reports 0 rows to change and writes nothing. The
       backup table is created only if it is missing.

   BACKUP
       Every changed value is copied to dbo.DateFixBackup_034 inside the
       same transaction. SETUP.md has the restore statement.
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit BIT = 0;          -- <<<< 0 = rollback (safe). 1 = keep.

DECLARE @ScriptName sysname = N'034_NormaliseDateOnlyColumns';
DECLARE @RunAtUtc   datetime2(7) = SYSUTCDATETIME();
DECLARE @sql        nvarchar(max);

PRINT '=====================================================';
PRINT ' 034 - normalise date-only columns';
PRINT ' @Commit = ' + CONVERT(varchar(1), @Commit)
      + CASE WHEN @Commit = 1 THEN '  (CHANGES WILL BE KEPT)'
             ELSE '  (dry run - everything rolls back)' END;
PRINT '=====================================================';


/* =====================================================================
   PRE-FLIGHT GUARD - make sure every column this script touches exists.
   If any is missing the script stops before it does anything.
   ===================================================================== */

IF OBJECT_ID(N'dbo.Quotes', N'U') IS NULL
BEGIN
    RAISERROR('dbo.Quotes does not exist. Nothing to repair. Stopping.', 16, 1);
    RETURN;
END

IF OBJECT_ID(N'dbo.Deals', N'U') IS NULL
BEGIN
    RAISERROR('dbo.Deals does not exist. Nothing to repair. Stopping.', 16, 1);
    RETURN;
END

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'dbo.Quotes')
                 AND name IN (N'IssueDateUtc', N'ExpiresAtUtc')
               GROUP BY object_id HAVING COUNT(*) = 2)
BEGIN
    RAISERROR('Quotes.IssueDateUtc / Quotes.ExpiresAtUtc not both present. Stopping.', 16, 1);
    RETURN;
END

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'dbo.Deals')
                 AND name = N'ExpectedCloseDateUtc')
BEGIN
    RAISERROR('Deals.ExpectedCloseDateUtc not present. Stopping.', 16, 1);
    RETURN;
END


/* =====================================================================
   PART 1 - THE GO / NO-GO GRID
   ---------------------------------------------------------------------
   Every row that needs repair should share the SAME time-of-day,
   because it is the same server offset subtracted from midnight every
   time. One value (or a couple, if you have moved servers) = this is
   the ToUniversalTime bug and the repair is correct.

   A scatter of many different times = something else is writing real
   timestamps into these columns. Do NOT commit; send me this grid.
   ===================================================================== */

PRINT '';
PRINT '--- Part 1: time-of-day signature (expect ONE value per column) ---';

SELECT
    [Column]        = N'Quotes.IssueDateUtc',
    TimeOfDay       = CAST(IssueDateUtc AS time(0)),
    Rows            = COUNT(*),
    EarliestDate    = MIN(CAST(IssueDateUtc AS date)),
    LatestDate      = MAX(CAST(IssueDateUtc AS date))
FROM dbo.Quotes
WHERE CAST(IssueDateUtc AS time(7)) <> CAST('00:00:00' AS time(7))
GROUP BY CAST(IssueDateUtc AS time(0))

UNION ALL
SELECT
    N'Quotes.ExpiresAtUtc',
    CAST(ExpiresAtUtc AS time(0)),
    COUNT(*),
    MIN(CAST(ExpiresAtUtc AS date)),
    MAX(CAST(ExpiresAtUtc AS date))
FROM dbo.Quotes
WHERE CAST(ExpiresAtUtc AS time(7)) <> CAST('00:00:00' AS time(7))
GROUP BY CAST(ExpiresAtUtc AS time(0))

UNION ALL
SELECT
    N'Deals.ExpectedCloseDateUtc',
    CAST(ExpectedCloseDateUtc AS time(0)),
    COUNT(*),
    MIN(CAST(ExpectedCloseDateUtc AS date)),
    MAX(CAST(ExpectedCloseDateUtc AS date))
FROM dbo.Deals
WHERE CAST(ExpectedCloseDateUtc AS time(7)) <> CAST('00:00:00' AS time(7))
GROUP BY CAST(ExpectedCloseDateUtc AS time(0))

ORDER BY [Column], TimeOfDay;


/* =====================================================================
   PART 2 - how much is affected, in total and per tenant
   ===================================================================== */

PRINT '';
PRINT '--- Part 2a: totals ---';

SELECT
    [Column]      = N'Quotes.IssueDateUtc',
    TotalRows     = COUNT(*),
    NeedRepair    = SUM(CASE WHEN CAST(IssueDateUtc AS time(7)) <> CAST('00:00:00' AS time(7)) THEN 1 ELSE 0 END),
    AlreadyClean  = SUM(CASE WHEN CAST(IssueDateUtc AS time(7))  = CAST('00:00:00' AS time(7)) THEN 1 ELSE 0 END)
FROM dbo.Quotes
UNION ALL
SELECT
    N'Quotes.ExpiresAtUtc',
    COUNT(*),
    SUM(CASE WHEN CAST(ExpiresAtUtc AS time(7)) <> CAST('00:00:00' AS time(7)) THEN 1 ELSE 0 END),
    SUM(CASE WHEN CAST(ExpiresAtUtc AS time(7))  = CAST('00:00:00' AS time(7)) THEN 1 ELSE 0 END)
FROM dbo.Quotes
UNION ALL
SELECT
    N'Deals.ExpectedCloseDateUtc',
    COUNT(*),
    SUM(CASE WHEN CAST(ExpectedCloseDateUtc AS time(7)) <> CAST('00:00:00' AS time(7)) THEN 1 ELSE 0 END),
    SUM(CASE WHEN CAST(ExpectedCloseDateUtc AS time(7))  = CAST('00:00:00' AS time(7)) THEN 1 ELSE 0 END)
FROM dbo.Deals;

PRINT '';
PRINT '--- Part 2b: per tenant ---';

/* Tenant name is looked up only if dbo.Tenants actually has a Name
   column, so this section cannot fail on a schema difference.          */
DECLARE @TenantNameExpr nvarchar(200) = N'CAST(NULL AS nvarchar(200))';
DECLARE @TenantJoin     nvarchar(400) = N'';
DECLARE @TenantGroupBy  nvarchar(200) = N'';   -- must stay empty when the
                                               -- name is a constant: SQL
                                               -- Server rejects GROUP BY on
                                               -- an expression with no column

IF OBJECT_ID(N'dbo.Tenants', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'dbo.Tenants') AND name = N'Name')
BEGIN
    SET @TenantNameExpr = N't.Name';
    SET @TenantJoin     = N'LEFT JOIN dbo.Tenants t ON t.Id = x.TenantId';
    SET @TenantGroupBy  = N', t.Name';
END

SET @sql = N'
SELECT
    x.TenantId,
    TenantName          = ' + @TenantNameExpr + N',
    QuoteIssueToFix     = SUM(x.QuoteIssue),
    QuoteExpiryToFix    = SUM(x.QuoteExpiry),
    DealCloseToFix      = SUM(x.DealClose)
FROM (
    SELECT TenantId,
           QuoteIssue  = CASE WHEN CAST(IssueDateUtc AS time(7)) <> CAST(''00:00:00'' AS time(7)) THEN 1 ELSE 0 END,
           QuoteExpiry = CASE WHEN CAST(ExpiresAtUtc AS time(7)) <> CAST(''00:00:00'' AS time(7)) THEN 1 ELSE 0 END,
           DealClose   = 0
    FROM dbo.Quotes
    UNION ALL
    SELECT TenantId, 0, 0,
           CASE WHEN CAST(ExpectedCloseDateUtc AS time(7)) <> CAST(''00:00:00'' AS time(7)) THEN 1 ELSE 0 END
    FROM dbo.Deals
) x
' + @TenantJoin + N'
GROUP BY x.TenantId' + @TenantGroupBy + N'
HAVING SUM(x.QuoteIssue) + SUM(x.QuoteExpiry) + SUM(x.DealClose) > 0
ORDER BY DealCloseToFix DESC, QuoteIssueToFix DESC;';

EXEC sp_executesql @sql;


/* =====================================================================
   PART 3 - before / after sample, 50 rows per table
   This is the grid to eyeball. "NewValue" is exactly what the UPDATE
   in Part 7 will write.
   ===================================================================== */

PRINT '';
PRINT '--- Part 3a: Quotes sample (up to 50) ---';

SELECT TOP (50)
    q.Id,
    q.TenantId,
    q.Number,
    q.Status,
    IssueStored   = q.IssueDateUtc,
    IssueRepaired = CAST(CAST(DATEADD(minute, 719, q.IssueDateUtc) AS date) AS datetime2(7)),
    IssueMoves    = CASE WHEN CAST(CAST(DATEADD(minute, 719, q.IssueDateUtc) AS date) AS datetime2(7))
                              <> q.IssueDateUtc THEN 'yes' ELSE 'no' END,
    ExpiryStored   = q.ExpiresAtUtc,
    ExpiryRepaired = CAST(CAST(DATEADD(minute, 719, q.ExpiresAtUtc) AS date) AS datetime2(7)),
    ExpiryMoves    = CASE WHEN CAST(CAST(DATEADD(minute, 719, q.ExpiresAtUtc) AS date) AS datetime2(7))
                               <> q.ExpiresAtUtc THEN 'yes' ELSE 'no' END,
    q.IsDeleted
FROM dbo.Quotes q
WHERE CAST(q.IssueDateUtc AS time(7)) <> CAST('00:00:00' AS time(7))
   OR CAST(q.ExpiresAtUtc AS time(7)) <> CAST('00:00:00' AS time(7))
ORDER BY q.CreatedAtUtc DESC;

PRINT '';
PRINT '--- Part 3b: Deals sample (up to 50) ---';

SELECT TOP (50)
    d.Id,
    d.TenantId,
    d.Title,
    d.Stage,
    ExpectedCloseStored   = d.ExpectedCloseDateUtc,
    ExpectedCloseRepaired = CAST(CAST(DATEADD(minute, 719, d.ExpectedCloseDateUtc) AS date) AS datetime2(7)),
    DayShift              = DATEDIFF(day,
                                CAST(d.ExpectedCloseDateUtc AS date),
                                CAST(DATEADD(minute, 719, d.ExpectedCloseDateUtc) AS date)),
    d.IsDeleted
FROM dbo.Deals d
WHERE CAST(d.ExpectedCloseDateUtc AS time(7)) <> CAST('00:00:00' AS time(7))
ORDER BY d.CreatedAtUtc DESC;


/* =====================================================================
   PART 4 - the rows a CUSTOMER can see
   Inside the CRM every screen already looks right, because the page
   model converts back with .ToLocalTime(). The wrong date is only
   visible on the public quote page. These are therefore the rows that
   matter most - fix them and tell whoever received the link.
   ===================================================================== */

PRINT '';
PRINT '--- Part 4: quotes with a live public link and a shifted date ---';

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID(N'dbo.Quotes') AND name = N'PublicLinkToken')
BEGIN
    SELECT
        q.Id,
        q.TenantId,
        q.Number,
        q.Status,
        IssueShownToCustomer   = CAST(q.IssueDateUtc AS date),
        IssueShouldBe          = CAST(DATEADD(minute, 719, q.IssueDateUtc) AS date),
        ExpiryShownToCustomer  = CAST(q.ExpiresAtUtc AS date),
        ExpiryShouldBe         = CAST(DATEADD(minute, 719, q.ExpiresAtUtc) AS date),
        q.GrandTotal,
        q.Currency
    FROM dbo.Quotes q
    WHERE q.PublicLinkToken IS NOT NULL
      AND q.IsDeleted = 0
      AND (CAST(q.IssueDateUtc AS time(7)) <> CAST('00:00:00' AS time(7))
        OR CAST(q.ExpiresAtUtc AS time(7)) <> CAST('00:00:00' AS time(7)))
    ORDER BY q.GrandTotal DESC;
END
ELSE
    PRINT '  (Quotes.PublicLinkToken not present - section skipped)';


/* =====================================================================
   PART 5 - Deals.ActualCloseDateUtc: REPORT ONLY, NO UPDATE
   ---------------------------------------------------------------------
   This column is written as
       deal.ActualCloseDateUtc ??= dto.ActualCloseDateUtc ?? DateTime.UtcNow;
   so it holds BOTH user-picked dates (should be midnight, may be
   shifted) and real closing timestamps (legitimately not midnight).
   The data cannot tell them apart, so this script does not touch it.

   Read the grid like this: rows whose time-of-day matches the single
   offset from Part 1 are almost certainly shifted picked dates; rows
   with scattered times are genuine timestamps. If the first group is
   worth fixing, say so and I will write a targeted follow-up that
   matches on that exact time-of-day only.
   ===================================================================== */

PRINT '';
PRINT '--- Part 5: Deals.ActualCloseDateUtc (report only, never updated) ---';

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID(N'dbo.Deals') AND name = N'ActualCloseDateUtc')
BEGIN
    SELECT
        TimeOfDay = CAST(ActualCloseDateUtc AS time(0)),
        Rows      = COUNT(*),
        Reading   = CASE WHEN CAST(ActualCloseDateUtc AS time(7)) = CAST('00:00:00' AS time(7))
                         THEN 'midnight - clean picked date'
                         ELSE 'either a shifted picked date or a real timestamp' END
    FROM dbo.Deals
    WHERE ActualCloseDateUtc IS NOT NULL
    GROUP BY CAST(ActualCloseDateUtc AS time(0)),
             CASE WHEN CAST(ActualCloseDateUtc AS time(7)) = CAST('00:00:00' AS time(7))
                  THEN 'midnight - clean picked date'
                  ELSE 'either a shifted picked date or a real timestamp' END
    ORDER BY Rows DESC;
END
ELSE
    PRINT '  (Deals.ActualCloseDateUtc not present - section skipped)';


/* =====================================================================
   PART 6 - backup table
   Created OUTSIDE the transaction so that it survives a rollback (an
   empty table is harmless). The rows go in inside the transaction, so
   on a dry run nothing is actually stored.
   Every reference to it goes through sp_executesql, per house rule,
   because the table may not exist when this batch is compiled.
   ===================================================================== */

IF OBJECT_ID(N'dbo.DateFixBackup_034', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.DateFixBackup_034
    (
        BackupId    int IDENTITY(1,1)  NOT NULL
            CONSTRAINT PK_DateFixBackup_034 PRIMARY KEY,
        ScriptName  sysname            NOT NULL,
        RunAtUtc    datetime2(7)       NOT NULL,
        TableName   sysname            NOT NULL,
        ColumnName  sysname            NOT NULL,
        RowId       uniqueidentifier   NOT NULL,
        TenantId    uniqueidentifier   NULL,
        OldValue    datetime2(7)       NOT NULL,
        NewValue    datetime2(7)       NOT NULL
    );
    PRINT '';
    PRINT 'Created dbo.DateFixBackup_034.';
END
ELSE
BEGIN
    PRINT '';
    PRINT 'dbo.DateFixBackup_034 already exists - reusing it.';
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.DateFixBackup_034')
                 AND name = N'IX_DateFixBackup_034_Row')
BEGIN
    -- through sp_executesql: the table may have been created moments ago
    -- in this same batch, so it is not guaranteed to resolve at compile time
    SET @sql = N'CREATE INDEX IX_DateFixBackup_034_Row
                     ON dbo.DateFixBackup_034 (TableName, ColumnName, RowId);';
    EXEC sp_executesql @sql;
END


/* =====================================================================
   PART 7 - THE TRANSACTION
   ===================================================================== */

DECLARE @BackedUp     int = 0;
DECLARE @QuoteIssue   int = 0;
DECLARE @QuoteExpiry  int = 0;
DECLARE @DealClose    int = 0;
DECLARE @TotalUpdated int = 0;

BEGIN TRAN;

BEGIN TRY

    /* ---- 7a. copy the old values into the backup table -------------- */

    SET @sql = N'
    INSERT INTO dbo.DateFixBackup_034
        (ScriptName, RunAtUtc, TableName, ColumnName, RowId, TenantId, OldValue, NewValue)
    SELECT @ScriptName, @RunAtUtc, N''Quotes'', N''IssueDateUtc'', q.Id, q.TenantId,
           q.IssueDateUtc,
           CAST(CAST(DATEADD(minute, 719, q.IssueDateUtc) AS date) AS datetime2(7))
    FROM dbo.Quotes q
    WHERE CAST(q.IssueDateUtc AS time(7)) <> CAST(''00:00:00'' AS time(7))

    UNION ALL
    SELECT @ScriptName, @RunAtUtc, N''Quotes'', N''ExpiresAtUtc'', q.Id, q.TenantId,
           q.ExpiresAtUtc,
           CAST(CAST(DATEADD(minute, 719, q.ExpiresAtUtc) AS date) AS datetime2(7))
    FROM dbo.Quotes q
    WHERE CAST(q.ExpiresAtUtc AS time(7)) <> CAST(''00:00:00'' AS time(7))

    UNION ALL
    SELECT @ScriptName, @RunAtUtc, N''Deals'', N''ExpectedCloseDateUtc'', d.Id, d.TenantId,
           d.ExpectedCloseDateUtc,
           CAST(CAST(DATEADD(minute, 719, d.ExpectedCloseDateUtc) AS date) AS datetime2(7))
    FROM dbo.Deals d
    WHERE CAST(d.ExpectedCloseDateUtc AS time(7)) <> CAST(''00:00:00'' AS time(7));';

    EXEC sp_executesql @sql,
         N'@ScriptName sysname, @RunAtUtc datetime2(7)',
         @ScriptName = @ScriptName, @RunAtUtc = @RunAtUtc;

    SET @BackedUp = @@ROWCOUNT;

    /* ---- 7b. Quotes.IssueDateUtc ------------------------------------ */

    UPDATE dbo.Quotes
    SET IssueDateUtc = CAST(CAST(DATEADD(minute, 719, IssueDateUtc) AS date) AS datetime2(7))
    WHERE CAST(IssueDateUtc AS time(7)) <> CAST('00:00:00' AS time(7));

    SET @QuoteIssue = @@ROWCOUNT;

    /* ---- 7c. Quotes.ExpiresAtUtc ------------------------------------ */

    UPDATE dbo.Quotes
    SET ExpiresAtUtc = CAST(CAST(DATEADD(minute, 719, ExpiresAtUtc) AS date) AS datetime2(7))
    WHERE CAST(ExpiresAtUtc AS time(7)) <> CAST('00:00:00' AS time(7));

    SET @QuoteExpiry = @@ROWCOUNT;

    /* ---- 7d. Deals.ExpectedCloseDateUtc ----------------------------- */

    UPDATE dbo.Deals
    SET ExpectedCloseDateUtc = CAST(CAST(DATEADD(minute, 719, ExpectedCloseDateUtc) AS date) AS datetime2(7))
    WHERE CAST(ExpectedCloseDateUtc AS time(7)) <> CAST('00:00:00' AS time(7));

    SET @DealClose = @@ROWCOUNT;

    PRINT '';
    PRINT '--- rows changed ---';
    PRINT '  backup rows written        : ' + CONVERT(varchar(20), @BackedUp);
    PRINT '  Quotes.IssueDateUtc        : ' + CONVERT(varchar(20), @QuoteIssue);
    PRINT '  Quotes.ExpiresAtUtc        : ' + CONVERT(varchar(20), @QuoteExpiry);
    PRINT '  Deals.ExpectedCloseDateUtc : ' + CONVERT(varchar(20), @DealClose);

    /* The backup must account for every single changed row. If it does
       not, something raced with us - abandon the whole thing.
       (RAISERROR needs plain variables for %d, not an expression.)      */
    SET @TotalUpdated = @QuoteIssue + @QuoteExpiry + @DealClose;

    IF @BackedUp <> @TotalUpdated
    BEGIN
        RAISERROR('Backup row count (%d) does not match updated row count (%d). Rolling back.',
                  16, 1, @BackedUp, @TotalUpdated);
    END


    /* =================================================================
       PART 8 - VERIFICATION (inside the transaction, so it shows the
                post-update state even on a dry run)
       ================================================================= */

    PRINT '';
    PRINT '--- Part 8a: verify - all three columns should now be 0 ---';

    SELECT
        [Column]         = N'Quotes.IssueDateUtc',
        StillNotMidnight = SUM(CASE WHEN CAST(IssueDateUtc AS time(7)) <> CAST('00:00:00' AS time(7)) THEN 1 ELSE 0 END),
        TotalRows        = COUNT(*)
    FROM dbo.Quotes
    UNION ALL
    SELECT N'Quotes.ExpiresAtUtc',
        SUM(CASE WHEN CAST(ExpiresAtUtc AS time(7)) <> CAST('00:00:00' AS time(7)) THEN 1 ELSE 0 END),
        COUNT(*)
    FROM dbo.Quotes
    UNION ALL
    SELECT N'Deals.ExpectedCloseDateUtc',
        SUM(CASE WHEN CAST(ExpectedCloseDateUtc AS time(7)) <> CAST('00:00:00' AS time(7)) THEN 1 ELSE 0 END),
        COUNT(*)
    FROM dbo.Deals;

    PRINT '';
    PRINT '--- Part 8b: sanity - quotes that expire before they were issued ---';
    PRINT '    (these are data-entry mistakes, not caused by this script;';
    PRINT '     the count should be the same before and after)';

    SELECT
        BadDateOrderQuotes = COUNT(*)
    FROM dbo.Quotes
    WHERE ExpiresAtUtc < IssueDateUtc
      AND IsDeleted = 0;

    PRINT '';
    PRINT '--- Part 8c: verify - no value moved by more than one day ---';
    PRINT '    (a correct repair shifts a date by 0 or 1 day, never more)';

    SET @sql = N'
    SELECT
        b.TableName,
        b.ColumnName,
        DaysMoved = DATEDIFF(day, b.OldValue, b.NewValue),
        Rows      = COUNT(*)
    FROM dbo.DateFixBackup_034 b
    WHERE b.ScriptName = @ScriptName
      AND b.RunAtUtc   = @RunAtUtc
    GROUP BY b.TableName, b.ColumnName, DATEDIFF(day, b.OldValue, b.NewValue)
    ORDER BY b.TableName, b.ColumnName, DaysMoved;';

    EXEC sp_executesql @sql,
         N'@ScriptName sysname, @RunAtUtc datetime2(7)',
         @ScriptName = @ScriptName, @RunAtUtc = @RunAtUtc;

    PRINT '';
    PRINT '--- Part 8d: verify - sample of 25 repaired rows, old vs new ---';

    SET @sql = N'
    SELECT TOP (25)
        b.TableName,
        b.ColumnName,
        b.RowId,
        b.TenantId,
        b.OldValue,
        b.NewValue
    FROM dbo.DateFixBackup_034 b
    WHERE b.ScriptName = @ScriptName
      AND b.RunAtUtc   = @RunAtUtc
    ORDER BY b.BackupId;';

    EXEC sp_executesql @sql,
         N'@ScriptName sysname, @RunAtUtc datetime2(7)',
         @ScriptName = @ScriptName, @RunAtUtc = @RunAtUtc;


    /* =================================================================
       PART 9 - COMMIT or ROLLBACK
       ================================================================= */

    IF @Commit = 1
    BEGIN
        COMMIT TRAN;
        PRINT '';
        PRINT '*** COMMITTED. Dates repaired and old values kept in dbo.DateFixBackup_034. ***';
    END
    ELSE
    BEGIN
        ROLLBACK TRAN;
        PRINT '';
        PRINT '*** ROLLED BACK (dry run). Nothing was changed. ***';
        PRINT '*** Set @Commit = 1 at the top and run again to keep the repair. ***';
    END

END TRY
BEGIN CATCH

    IF XACT_STATE() <> 0
        ROLLBACK TRAN;

    PRINT '';
    PRINT '*** FAILED - rolled back. Nothing was changed. ***';

    DECLARE @ErrMsg  nvarchar(4000) = ERROR_MESSAGE();
    DECLARE @ErrLine int            = ERROR_LINE();
    RAISERROR('034 failed at line %d: %s', 16, 1, @ErrLine, @ErrMsg);

END CATCH;
