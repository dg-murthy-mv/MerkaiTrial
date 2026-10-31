/* =====================================================================
   037_Notifications.sql
   ---------------------------------------------------------------------
   WHAT THIS DOES
     Creates dbo.Notifications and its three indexes. Nothing else — no
     seed, no repair, no backfill. Notifications describe things that
     happen from now on; inventing history for them would be fiction.

   THE THREE INDEXES, AND WHY EACH EXISTS

     IX_Notifications_Feed     (TenantId, RecipientUserId, CreatedAtUtc DESC)
         The notifications page.

     IX_Notifications_Unread   FILTERED on unread AND not dismissed
         The bell count. EVERY signed-in browser asks for this every 45
         seconds, which makes it the busiest query in the application. A
         filtered index holds only the handful of rows that are actually
         unread, so the count stays fast no matter how much history builds
         up behind it — and the index itself stays tiny.

     IX_Notifications_Entity   (TenantId, EntityType, EntityId)
         "Everything raised about this deal", for marking a record's
         notifications read when someone opens it.

   NO TenantId FOREIGN KEY
     Matching the rest of your schema, which scopes by TenantId without an
     FK on every table. The FK that IS here is RecipientUserId → Users,
     with ON DELETE RESTRICT: deleting a user must not silently take their
     notification history with it.

   RETENTION
     This table only grows. At your current size that is irrelevant, but
     Part 3 has the cleanup statement for when it stops being — and a note
     on when to start caring.

   HOW TO RUN
     1. Run as-is. @Commit = 0, so it rolls back. Read Part 2.
     2. Set @Commit = 1 and run again.

   RE-RUN SAFETY
     The table and every index are guarded. Running this twice does
     nothing the second time.
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit BIT = 0;          -- <<<< 0 = rollback (safe). 1 = keep.

DECLARE @sql nvarchar(max);

PRINT '=====================================================';
PRINT ' 037 - in-app notifications';
PRINT ' @Commit = ' + CONVERT(varchar(1), @Commit)
      + CASE WHEN @Commit = 1 THEN '  (CHANGES WILL BE KEPT)'
             ELSE '  (dry run - everything rolls back)' END;
PRINT '=====================================================';


/* =====================================================================
   PRE-FLIGHT
   ===================================================================== */

IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
BEGIN
    RAISERROR('dbo.Users does not exist. Stopping.', 16, 1);
    RETURN;
END


/* =====================================================================
   PART 1 - THE TRANSACTION
   ===================================================================== */

DECLARE @CreatedTable bit = 0;
DECLARE @CreatedIx    int = 0;

BEGIN TRAN;

BEGIN TRY

    /* ---- 1a. the table ---------------------------------------------- */

    IF OBJECT_ID(N'dbo.Notifications', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.Notifications
        (
            Id               uniqueidentifier NOT NULL
                CONSTRAINT PK_Notifications PRIMARY KEY,

            TenantId         uniqueidentifier NOT NULL,

            -- One row PER RECIPIENT. Notifying three people writes three
            -- rows, so each of them reads and dismisses independently.
            RecipientUserId  uniqueidentifier NOT NULL,

            -- NotificationEventType as an int. Members are appended and
            -- never renumbered, because these numbers are the history.
            EventType        int              NOT NULL,

            -- Written once, at dispatch. Never recomputed: "Quote QUO-0042
            -- was accepted" must still say that after the quote is revised,
            -- because it records what happened rather than current state.
            Title            nvarchar(200)    NOT NULL,
            Body             nvarchar(500)    NULL,

            -- What it points at. No URL column on purpose — a stored URL is
            -- a routing decision frozen at write time, and it breaks
            -- silently the first time a page moves. The page builds the link.
            EntityType       nvarchar(50)     NULL,
            EntityId         uniqueidentifier NULL,

            -- Who caused it. Used to make sure nobody is told about their
            -- own click. NULL when a customer acted on a public quote link.
            ActorUserId      uniqueidentifier NULL,
            ActorName        nvarchar(200)    NULL,

            CreatedAtUtc     datetime2(7)     NOT NULL
                CONSTRAINT DF_Notifications_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),

            -- Two timestamps, two different questions.
            --   ReadAtUtc      = "I have seen this"   (clears the bell)
            --   DismissedAtUtc = "take it off my list" (hides, keeps)
            -- One IsRead flag would force "clear all" to be either a lie or
            -- a delete.
            ReadAtUtc        datetime2(7)     NULL,
            DismissedAtUtc   datetime2(7)     NULL,

            CONSTRAINT FK_Notifications_Users_RecipientUserId
                FOREIGN KEY (RecipientUserId) REFERENCES dbo.Users (Id)
                ON DELETE NO ACTION
        );

        SET @CreatedTable = 1;
        PRINT '  created dbo.Notifications';
    END
    ELSE
        PRINT '  dbo.Notifications already exists';

    /* ---- 1b. the indexes -------------------------------------------- */
    -- Through sp_executesql: the table may have been created moments ago
    -- in this same batch.

    IF NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = N'IX_Notifications_Feed'
                     AND object_id = OBJECT_ID(N'dbo.Notifications'))
    BEGIN
        SET @sql = N'
        CREATE INDEX IX_Notifications_Feed
            ON dbo.Notifications (TenantId, RecipientUserId, CreatedAtUtc DESC);';
        EXEC sp_executesql @sql;
        SET @CreatedIx += 1;
        PRINT '  created IX_Notifications_Feed';
    END

    IF NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = N'IX_Notifications_Unread'
                     AND object_id = OBJECT_ID(N'dbo.Notifications'))
    BEGIN
        -- FILTERED. This is the one that matters: it is read by every open
        -- browser every 45 seconds and it only ever contains the rows that
        -- are genuinely outstanding.
        SET @sql = N'
        CREATE INDEX IX_Notifications_Unread
            ON dbo.Notifications (TenantId, RecipientUserId)
            WHERE ReadAtUtc IS NULL AND DismissedAtUtc IS NULL;';
        EXEC sp_executesql @sql;
        SET @CreatedIx += 1;
        PRINT '  created IX_Notifications_Unread (filtered)';
    END

    IF NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = N'IX_Notifications_Entity'
                     AND object_id = OBJECT_ID(N'dbo.Notifications'))
    BEGIN
        SET @sql = N'
        CREATE INDEX IX_Notifications_Entity
            ON dbo.Notifications (TenantId, EntityType, EntityId);';
        EXEC sp_executesql @sql;
        SET @CreatedIx += 1;
        PRINT '  created IX_Notifications_Entity';
    END

    IF @CreatedIx = 0
        PRINT '  all three indexes already present';


    /* =================================================================
       PART 2 - VERIFICATION (inside the transaction)
       ================================================================= */

    PRINT '';
    PRINT '--- Part 2a: columns ---';

    SELECT
        ColumnName = c.name,
        DataType   = ty.name,
        MaxChars   = CASE WHEN c.max_length = -1 THEN -1
                          WHEN ty.name LIKE 'n%' AND ty.name NOT IN ('numeric')
                               THEN c.max_length / 2
                          ELSE c.max_length END,
        Nullable   = c.is_nullable
    FROM sys.columns c
    JOIN sys.types   ty ON ty.user_type_id = c.user_type_id
    WHERE c.object_id = OBJECT_ID(N'dbo.Notifications')
    ORDER BY c.column_id;

    PRINT '';
    PRINT '--- Part 2b: indexes (IX_Notifications_Unread must show a filter) ---';

    SELECT
        IndexName   = i.name,
        IsFiltered  = i.has_filter,
        FilterText  = i.filter_definition,
        Columns     = SUBSTRING(
                        (SELECT ', ' + col.name
                                + CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE '' END
                         FROM sys.index_columns ic
                         JOIN sys.columns col
                              ON col.object_id = ic.object_id
                             AND col.column_id = ic.column_id
                         WHERE ic.object_id = i.object_id
                           AND ic.index_id = i.index_id
                         ORDER BY ic.key_ordinal
                         FOR XML PATH('')), 3, 8000)
    FROM sys.indexes i
    WHERE i.object_id = OBJECT_ID(N'dbo.Notifications')
      AND i.name IS NOT NULL
    ORDER BY i.name;

    PRINT '';
    PRINT '--- Part 2c: row count (0 on a first run, and that is correct) ---';

    SELECT
        TotalRows  = COUNT(*),
        UnreadRows = SUM(CASE WHEN ReadAtUtc IS NULL AND DismissedAtUtc IS NULL
                              THEN 1 ELSE 0 END)
    FROM dbo.Notifications;

    PRINT '';
    PRINT '--- summary ---';
    PRINT '  table created   : ' + CASE WHEN @CreatedTable = 1 THEN 'yes' ELSE 'already there' END;
    PRINT '  indexes created : ' + CONVERT(varchar(20), @CreatedIx);


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
    RAISERROR('037 failed at line %d: %s', 16, 1, @ErrLine, @ErrMsg);

END CATCH;


/* =====================================================================
   PART 3 - RETENTION, FOR LATER (not run)
   ---------------------------------------------------------------------
   This table only grows. Rough arithmetic: 20 users each getting 10
   notifications a day is 200 rows a day, 73,000 a year — which is
   nothing. Start caring at a few million rows, or when
   IX_Notifications_Feed stops fitting comfortably in memory.

   When that day comes, delete in BATCHES. A single DELETE of a million
   rows takes a lock escalation and holds it, and on a busy table that is
   an outage:

       DECLARE @Cutoff datetime2(7) = DATEADD(day, -180, SYSUTCDATETIME());
       DECLARE @Batch int = 5000;

       WHILE 1 = 1
       BEGIN
           DELETE TOP (@Batch) FROM dbo.Notifications
           WHERE CreatedAtUtc < @Cutoff
             AND ReadAtUtc IS NOT NULL;      -- never delete something unread

           IF @@ROWCOUNT < @Batch BREAK;
           WAITFOR DELAY '00:00:00.200';     -- let other queries through
       END

   Two things in there are deliberate. It only deletes what has been READ,
   so a notification nobody has looked at survives however old it is. And
   it keeps DISMISSED rows until they age out, because "I cleared it" is
   not "it never happened" — the row is still the only record that the
   person was told.

   That belongs in the background worker from 038 rather than a manual run.
   I will add it there.
   ===================================================================== */
