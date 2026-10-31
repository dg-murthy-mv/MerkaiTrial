/* =====================================================================
   036_QuoteStageAutomation.sql
   ---------------------------------------------------------------------
   WHAT THIS DOES

     1. Adds three columns to PipelineRuleSettings:
              QuoteSentStageKey       nvarchar(50) NULL
              QuoteAcceptedStageKey   nvarchar(50) NULL
              QuoteRejectedStageKey   nvarchar(50) NULL

     2. Seeds them per tenant, resolved against THAT tenant's own stages,
        so nothing that worked before stops working:
              'Proposal'    -> QuoteSentStageKey       if they have it
              'Negotiation' -> QuoteAcceptedStageKey   if they have it
              'ClosedLost'  -> QuoteRejectedStageKey   if they have it
        A tenant who does not have that stage key gets NULL, which means
        "do not move the deal" — and that is strictly better than what the
        code did to them before, which was to write a stage key that does
        not exist in their pipeline.

     3. Repairs DealStageHistory rows written with TenantId = 0x0 by the
        old quote-created path, which the global query filter has been
        hiding ever since.

     4. Reports on the damage the hardcoded keys left behind:
            - deals sitting in a stage key that is not in their pipeline
            - deals in an OPEN stage carrying a closing ActualValue
        The second of those has an OPTIONAL repair, off by default,
        because it writes to a money column.

   WHY THE COLUMNS ARE NULLABLE WITH NO DEFAULT
     NULL means DO NOT MOVE THE DEAL. Guessing a stage key for a pipeline
     we have never seen is precisely the bug being removed here, so the
     code refuses to guess and the database does not hold a guess either.

   HOW TO RUN
     1. Run as-is. @Commit = 0. Read Part 1 and Part 2 carefully.
     2. Decide about @RepairOpenDealActualValue (see Part 2c).
     3. Set @Commit = 1 and run again.

   RE-RUN SAFETY
     Every ALTER is guarded. The seed only fills keys that are still NULL,
     so it will not overwrite a choice you make later on the settings page.
     The history repair only touches rows whose TenantId is empty.
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit BIT = 0;          -- <<<< 0 = rollback (safe). 1 = keep.

-- Second, separate switch. See Part 2c before turning this on.
DECLARE @RepairOpenDealActualValue BIT = 0;

DECLARE @sql nvarchar(max);

PRINT '=====================================================';
PRINT ' 036 - quote-driven stage moves';
PRINT ' @Commit                    = ' + CONVERT(varchar(1), @Commit)
      + CASE WHEN @Commit = 1 THEN '  (CHANGES WILL BE KEPT)'
             ELSE '  (dry run - everything rolls back)' END;
PRINT ' @RepairOpenDealActualValue = ' + CONVERT(varchar(1), @RepairOpenDealActualValue);
PRINT '=====================================================';


/* =====================================================================
   PRE-FLIGHT
   ===================================================================== */

IF OBJECT_ID(N'dbo.PipelineRuleSettings', N'U') IS NULL
BEGIN
    RAISERROR('dbo.PipelineRuleSettings does not exist. If your table is named differently, tell me and I will reissue this. Stopping.', 16, 1);
    RETURN;
END

IF OBJECT_ID(N'dbo.PipelineStages', N'U') IS NULL
BEGIN
    RAISERROR('dbo.PipelineStages does not exist. Stopping.', 16, 1);
    RETURN;
END

IF OBJECT_ID(N'dbo.DealStageHistory', N'U') IS NULL
BEGIN
    RAISERROR('dbo.DealStageHistory does not exist. Stopping.', 16, 1);
    RETURN;
END


/* =====================================================================
   PART 1 - THE DAMAGE (read-only, before anything changes)
   ===================================================================== */

PRINT '';
PRINT '--- Part 1a: each tenant''s stage keys, and whether the old literals exist ---';
PRINT '    (a "no" means the old code was writing a stage key this tenant';
PRINT '     does not have - those deals are the ghost-stage rows in 1b)';

SELECT
    TenantName        = t.Name,
    t.Id,
    StageCount        = COUNT(ps.Id),
    HasProposal       = MAX(CASE WHEN ps.[Key] = 'Proposal'    THEN 'yes' ELSE 'no' END),
    HasNegotiation    = MAX(CASE WHEN ps.[Key] = 'Negotiation' THEN 'yes' ELSE 'no' END),
    HasClosedLost     = MAX(CASE WHEN ps.[Key] = 'ClosedLost'  THEN 'yes' ELSE 'no' END)
FROM dbo.Tenants t
LEFT JOIN dbo.PipelineStages ps ON ps.TenantId = t.Id
WHERE t.IsDeleted = 0
GROUP BY t.Name, t.Id
ORDER BY t.Name;

PRINT '';
PRINT '--- Part 1a2: the actual stages, per tenant ---';
PRINT '    (this is the list to pick your three keys from)';

-- One row per stage rather than a STRING_AGG, which would need
-- SQL Server 2017 or later. This works on any version.
SELECT
    TenantName  = t.Name,
    ps.SortOrder,
    StageKey    = ps.[Key],
    StageName   = ps.Name,
    Category    = CASE ps.Category WHEN 0 THEN 'Open'
                                  WHEN 1 THEN 'Won'
                                  WHEN 2 THEN 'Lost'
                                  ELSE CONVERT(varchar(11), ps.Category) END,
    ps.Probability,
    ps.IsActive,
    ps.IsDefault
FROM dbo.PipelineStages ps
JOIN dbo.Tenants t ON t.Id = ps.TenantId
WHERE t.IsDeleted = 0
ORDER BY t.Name, ps.SortOrder;

PRINT '';
PRINT '--- Part 1b: GHOST STAGES - deals sitting in a key that is not a stage ---';
PRINT '    (written by the old hardcoded "Proposal" / "Negotiation" /';
PRINT '     "ClosedLost". No kanban column, no probability, not terminal.';
PRINT '     NOT auto-fixed: only you can say where each one belongs.)';

SELECT
    TenantName = t.Name,
    d.Id       AS DealId,
    d.Title,
    GhostStage = d.Stage,
    d.Probability,
    d.ExpectedValue,
    d.ActualValue,
    d.ActualCloseDateUtc,
    d.CreatedAtUtc
FROM dbo.Deals d
JOIN dbo.Tenants t ON t.Id = d.TenantId
WHERE d.IsDeleted = 0
  AND NOT EXISTS (SELECT 1 FROM dbo.PipelineStages ps
                  WHERE ps.TenantId = d.TenantId
                    AND ps.[Key] = d.Stage)
ORDER BY t.Name, d.CreatedAtUtc DESC;

PRINT '';
PRINT '--- Part 1c: INVISIBLE HISTORY - stage history rows with no TenantId ---';
PRINT '    (the old quote-created path forgot TenantId, so these are hidden';
PRINT '     by the global query filter. Part 2d repairs them.)';

SELECT
    Rows            = COUNT(*),
    RepairableRows  = SUM(CASE WHEN d.Id IS NOT NULL THEN 1 ELSE 0 END),
    OrphanRows      = SUM(CASE WHEN d.Id IS NULL     THEN 1 ELSE 0 END),
    Earliest        = MIN(h.ChangedAtUtc),
    Latest          = MAX(h.ChangedAtUtc)
FROM dbo.DealStageHistory h
LEFT JOIN dbo.Deals d ON d.Id = h.DealId
WHERE h.TenantId = CONVERT(uniqueidentifier, '00000000-0000-0000-0000-000000000000');

PRINT '';
PRINT '--- Part 1d: CLOSING FIGURE ON AN OPEN DEAL ---';
PRINT '    (quote accepted set ActualValue while moving the deal to an OPEN';
PRINT '     stage, so these are counted as revenue while still in the';
PRINT '     pipeline. Optional repair in Part 2c.)';

SELECT
    TenantName  = t.Name,
    d.Id        AS DealId,
    d.Title,
    d.Stage,
    StageName   = ps.Name,
    d.ExpectedValue,
    d.ActualValue,
    d.ActualCloseDateUtc,
    Reading     = CASE WHEN d.ActualCloseDateUtc IS NOT NULL
                       THEN 'has a close date too - check this one by hand'
                       ELSE 'value only - almost certainly the accepted-quote bug' END
FROM dbo.Deals d
JOIN dbo.Tenants t ON t.Id = d.TenantId
JOIN dbo.PipelineStages ps
      ON ps.TenantId = d.TenantId AND ps.[Key] = d.Stage
WHERE d.IsDeleted = 0
  AND ps.Category = 0            -- StageCategory.Open
  AND d.ActualValue IS NOT NULL
ORDER BY d.ActualValue DESC;


/* =====================================================================
   PART 2 - THE TRANSACTION
   ===================================================================== */

DECLARE @AddedCols     int = 0;
DECLARE @SeededRows    int = 0;
DECLARE @InsertedRows  int = 0;
DECLARE @FixedHistory  int = 0;
DECLARE @ClearedValues int = 0;

BEGIN TRAN;

BEGIN TRY

    /* ---- 2a. the three columns -------------------------------------- */

    IF COL_LENGTH('dbo.PipelineRuleSettings', 'QuoteSentStageKey') IS NULL
    BEGIN
        ALTER TABLE dbo.PipelineRuleSettings ADD QuoteSentStageKey nvarchar(50) NULL;
        SET @AddedCols += 1;
        PRINT '  added PipelineRuleSettings.QuoteSentStageKey';
    END

    IF COL_LENGTH('dbo.PipelineRuleSettings', 'QuoteAcceptedStageKey') IS NULL
    BEGIN
        ALTER TABLE dbo.PipelineRuleSettings ADD QuoteAcceptedStageKey nvarchar(50) NULL;
        SET @AddedCols += 1;
        PRINT '  added PipelineRuleSettings.QuoteAcceptedStageKey';
    END

    IF COL_LENGTH('dbo.PipelineRuleSettings', 'QuoteRejectedStageKey') IS NULL
    BEGIN
        ALTER TABLE dbo.PipelineRuleSettings ADD QuoteRejectedStageKey nvarchar(50) NULL;
        SET @AddedCols += 1;
        PRINT '  added PipelineRuleSettings.QuoteRejectedStageKey';
    END

    IF @AddedCols = 0
        PRINT '  all three columns already present';

    /* ---- 2b. seed, per tenant, from that tenant's own stages --------
       Through sp_executesql: the columns may have been added moments ago
       in this same batch.

       A key is only written when the tenant REALLY HAS a stage with that
       key, and only when the column is still NULL — so running this again
       after you have set something on the settings page will not undo it.
       ------------------------------------------------------------------ */

    SET @sql = N'
    DECLARE @u int = 0, @i int = 0;

    -- existing rows
    UPDATE r
    SET r.QuoteSentStageKey =
            CASE WHEN r.QuoteSentStageKey IS NULL
                  AND EXISTS (SELECT 1 FROM dbo.PipelineStages ps
                              WHERE ps.TenantId = r.TenantId AND ps.[Key] = ''Proposal'')
                 THEN ''Proposal'' ELSE r.QuoteSentStageKey END,
        r.QuoteAcceptedStageKey =
            CASE WHEN r.QuoteAcceptedStageKey IS NULL
                  AND EXISTS (SELECT 1 FROM dbo.PipelineStages ps
                              WHERE ps.TenantId = r.TenantId AND ps.[Key] = ''Negotiation'')
                 THEN ''Negotiation'' ELSE r.QuoteAcceptedStageKey END,
        r.QuoteRejectedStageKey =
            CASE WHEN r.QuoteRejectedStageKey IS NULL
                  AND EXISTS (SELECT 1 FROM dbo.PipelineStages ps
                              WHERE ps.TenantId = r.TenantId AND ps.[Key] = ''ClosedLost'')
                 THEN ''ClosedLost'' ELSE r.QuoteRejectedStageKey END
    FROM dbo.PipelineRuleSettings r
    WHERE r.QuoteSentStageKey IS NULL
       OR r.QuoteAcceptedStageKey IS NULL
       OR r.QuoteRejectedStageKey IS NULL;
    SET @u = @@ROWCOUNT;

    -- tenants with NO settings row at all.
    --
    -- They need one, because PipelineRuleDefaults.For() now returns NULL
    -- for all three keys - so without a row they would LOSE the automatic
    -- move they have today. The other four values are written to match the
    -- defaults in PipelineRuleDefaults exactly, so behaviour is unchanged.
    INSERT INTO dbo.PipelineRuleSettings
        (Id, TenantId, ForwardOnly, ReopenRequiresReason,
         ReopenRestrictedToManagers, BlockReopenWithIssuedInvoice,
         QuoteSentStageKey, QuoteAcceptedStageKey, QuoteRejectedStageKey,
         UpdatedAtUtc, UpdatedBy)
    SELECT
        NEWID(), t.Id, 0, 1, 1, 1,
        CASE WHEN EXISTS (SELECT 1 FROM dbo.PipelineStages ps
                          WHERE ps.TenantId = t.Id AND ps.[Key] = ''Proposal'')
             THEN ''Proposal'' END,
        CASE WHEN EXISTS (SELECT 1 FROM dbo.PipelineStages ps
                          WHERE ps.TenantId = t.Id AND ps.[Key] = ''Negotiation'')
             THEN ''Negotiation'' END,
        CASE WHEN EXISTS (SELECT 1 FROM dbo.PipelineStages ps
                          WHERE ps.TenantId = t.Id AND ps.[Key] = ''ClosedLost'')
             THEN ''ClosedLost'' END,
        SYSUTCDATETIME(), ''036_QuoteStageAutomation''
    FROM dbo.Tenants t
    WHERE t.IsDeleted = 0
      AND NOT EXISTS (SELECT 1 FROM dbo.PipelineRuleSettings r WHERE r.TenantId = t.Id);
    SET @i = @@ROWCOUNT;

    SET @Updated = @u;
    SET @Inserted = @i;';

    EXEC sp_executesql @sql,
         N'@Updated int OUTPUT, @Inserted int OUTPUT',
         @Updated = @SeededRows OUTPUT, @Inserted = @InsertedRows OUTPUT;

    PRINT '  seeded ' + CONVERT(varchar(20), @SeededRows) + ' existing settings row(s)';
    PRINT '  created ' + CONVERT(varchar(20), @InsertedRows) + ' new settings row(s)';

    /* ---- 2c. OPTIONAL: clear ActualValue on open deals -------------- */
    --
    -- Off unless you set @RepairOpenDealActualValue = 1 at the top.
    --
    -- What it does: sets ActualValue to NULL on deals sitting in an OPEN
    -- stage. ActualValue is a closing figure — StageTransitionGuard
    -- .ApplyToDeal clears it on every move into an open stage, and the only
    -- reason these rows have one is the accepted-quote bug.
    --
    -- Why it is opt-in: it writes to a money column. Read the Part 1d grid
    -- first. Any row there showing "has a close date too" deserves a look by
    -- hand before you switch this on — that combination suggests something
    -- other than this bug touched it.
    --
    -- It deliberately does NOT touch ActualCloseDateUtc. If a row has one on
    -- an open deal, that is a separate question and I would rather you saw it
    -- than had it quietly cleared.

    IF @RepairOpenDealActualValue = 1
    BEGIN
        UPDATE d
        SET d.ActualValue = NULL,
            d.UpdatedAtUtc = SYSUTCDATETIME(),
            d.UpdatedBy = '036_QuoteStageAutomation'
        FROM dbo.Deals d
        JOIN dbo.PipelineStages ps
              ON ps.TenantId = d.TenantId AND ps.[Key] = d.Stage
        WHERE d.IsDeleted = 0
          AND ps.Category = 0            -- StageCategory.Open
          AND d.ActualValue IS NOT NULL;

        SET @ClearedValues = @@ROWCOUNT;
        PRINT '  cleared ActualValue on ' + CONVERT(varchar(20), @ClearedValues) + ' open deal(s)';
    END
    ELSE
        PRINT '  ActualValue repair SKIPPED (@RepairOpenDealActualValue = 0)';

    /* ---- 2d. repair the invisible history rows ---------------------- */

    UPDATE h
    SET h.TenantId = d.TenantId
    FROM dbo.DealStageHistory h
    JOIN dbo.Deals d ON d.Id = h.DealId
    WHERE h.TenantId = CONVERT(uniqueidentifier, '00000000-0000-0000-0000-000000000000')
      AND d.TenantId <> CONVERT(uniqueidentifier, '00000000-0000-0000-0000-000000000000');

    SET @FixedHistory = @@ROWCOUNT;
    PRINT '  repaired TenantId on ' + CONVERT(varchar(20), @FixedHistory) + ' stage history row(s)';


    /* =================================================================
       PART 3 - VERIFICATION (inside the transaction)
       ================================================================= */

    PRINT '';
    PRINT '--- Part 3a: what each tenant will now do on a quote event ---';

    SET @sql = N'
    SELECT
        TenantName   = t.Name,
        OnQuoteRaised   = COALESCE(sent.Name,     r.QuoteSentStageKey,     ''(no move)''),
        OnQuoteAccepted = COALESCE(acc.Name,      r.QuoteAcceptedStageKey, ''(no move)''),
        OnQuoteRejected = COALESCE(rej.Name,      r.QuoteRejectedStageKey, ''(no move)''),
        RaisedKeyValid   = CASE WHEN r.QuoteSentStageKey     IS NULL THEN ''n/a''
                                WHEN sent.Id IS NULL THEN ''BAD KEY'' ELSE ''ok'' END,
        AcceptedKeyValid = CASE WHEN r.QuoteAcceptedStageKey IS NULL THEN ''n/a''
                                WHEN acc.Id  IS NULL THEN ''BAD KEY'' ELSE ''ok'' END,
        RejectedKeyValid = CASE WHEN r.QuoteRejectedStageKey IS NULL THEN ''n/a''
                                WHEN rej.Id  IS NULL THEN ''BAD KEY'' ELSE ''ok'' END,
        AcceptedCloses   = CASE WHEN acc.Category IN (1, 2) THEN ''yes - fills close date and final value''
                                WHEN acc.Id IS NOT NULL     THEN ''no - stays open''
                                ELSE ''n/a'' END
    FROM dbo.Tenants t
    LEFT JOIN dbo.PipelineRuleSettings r ON r.TenantId = t.Id
    LEFT JOIN dbo.PipelineStages sent
           ON sent.TenantId = t.Id AND sent.[Key] = r.QuoteSentStageKey
    LEFT JOIN dbo.PipelineStages acc
           ON acc.TenantId  = t.Id AND acc.[Key]  = r.QuoteAcceptedStageKey
    LEFT JOIN dbo.PipelineStages rej
           ON rej.TenantId  = t.Id AND rej.[Key]  = r.QuoteRejectedStageKey
    WHERE t.IsDeleted = 0
    ORDER BY t.Name;';

    EXEC sp_executesql @sql;

    PRINT '';
    PRINT '--- Part 3b: verify - no stage history rows left without a tenant ---';

    SELECT
        StillMissingTenantId = COUNT(*),
        OfWhichOrphaned      = SUM(CASE WHEN d.Id IS NULL THEN 1 ELSE 0 END)
    FROM dbo.DealStageHistory h
    LEFT JOIN dbo.Deals d ON d.Id = h.DealId
    WHERE h.TenantId = CONVERT(uniqueidentifier, '00000000-0000-0000-0000-000000000000');

    PRINT '';
    PRINT '--- Part 3c: verify - open deals still carrying a closing value ---';

    SELECT
        OpenDealsWithActualValue = COUNT(*)
    FROM dbo.Deals d
    JOIN dbo.PipelineStages ps
          ON ps.TenantId = d.TenantId AND ps.[Key] = d.Stage
    WHERE d.IsDeleted = 0
      AND ps.Category = 0
      AND d.ActualValue IS NOT NULL;

    PRINT '';
    PRINT '--- summary ---';
    PRINT '  columns added                  : ' + CONVERT(varchar(20), @AddedCols);
    PRINT '  settings rows seeded           : ' + CONVERT(varchar(20), @SeededRows);
    PRINT '  settings rows created          : ' + CONVERT(varchar(20), @InsertedRows);
    PRINT '  open deals ActualValue cleared : ' + CONVERT(varchar(20), @ClearedValues);
    PRINT '  history rows repaired          : ' + CONVERT(varchar(20), @FixedHistory);


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
    RAISERROR('036 failed at line %d: %s', 16, 1, @ErrLine, @ErrMsg);

END CATCH;


/* =====================================================================
   PART 4 - SETTING A KEY BY HAND
   ---------------------------------------------------------------------
   There is no UI for these three yet — that is a follow-up round on the
   Pipeline Rules settings page. Until then, this is how to set one.

   First see what a tenant has:

       SELECT ps.[Key], ps.Name, ps.SortOrder, ps.Probability,
              Category = CASE ps.Category WHEN 0 THEN 'Open'
                                          WHEN 1 THEN 'Won'
                                          WHEN 2 THEN 'Lost' END,
              ps.IsActive
       FROM dbo.PipelineStages ps
       JOIN dbo.Tenants t ON t.Id = ps.TenantId
       WHERE t.Name = 'Sathorn'
       ORDER BY ps.SortOrder;

   Then set the keys you want. NULL switches the move off:

       DECLARE @Commit BIT = 0;
       BEGIN TRAN;

       UPDATE r
       SET r.QuoteSentStageKey     = 'Demo',          -- or NULL for no move
           r.QuoteAcceptedStageKey = 'Commercials',
           r.QuoteRejectedStageKey = NULL,
           r.UpdatedAtUtc = SYSUTCDATETIME(),
           r.UpdatedBy = 'manual'
       FROM dbo.PipelineRuleSettings r
       JOIN dbo.Tenants t ON t.Id = r.TenantId
       WHERE t.Name = 'Sathorn';

       -- re-run Part 3a of 036 here to check the keys are valid

       IF @Commit = 1 COMMIT ELSE ROLLBACK;

   A key that is not one of that tenant's stages is refused at runtime and
   logged rather than written, so a typo here costs you a skipped move and
   a log line, not a broken deal. Part 3a's "BAD KEY" column catches it
   before that.
   ===================================================================== */
