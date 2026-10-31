/* =====================================================================
   038_EmailOutbox.sql
   ---------------------------------------------------------------------
   WHAT THIS DOES
     Creates two tables and their indexes:

       dbo.OutboundMessages            the email/SMS outbox
       dbo.UserNotificationPreferences per user, per event, per channel

     No seed. A user with no preference rows is the NORMAL case, not an
     unconfigured one — NotificationDefaults decides what they get, and
     only the settings page writes rows.

     Seeding a row per user per event type would look tidier and would be
     a trap: add a ninth event type in six months and every existing user
     silently gets nothing for it, because their row set predates it.

   THE INDEX THAT MATTERS

     IX_OutboundMessages_Claim   FILTERED to Status IN (0, 3)
                                 i.e. Pending and Failed only

     The worker asks "what is due?" every 15 seconds, for ever. Without
     the filter that query walks every message ever sent. With it, the
     index holds only the handful still owed a send and stays that size
     permanently, however much history builds up behind it.

   HOW TO RUN
     1. Run as-is. @Commit = 0, so it rolls back. Read Part 2.
     2. Set @Commit = 1 and run again.

   RE-RUN SAFETY
     Both tables and every index are guarded. A second run does nothing.
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit BIT = 0;          -- <<<< 0 = rollback (safe). 1 = keep.

DECLARE @sql nvarchar(max);

PRINT '=====================================================';
PRINT ' 038 - email outbox + notification preferences';
PRINT ' @Commit = ' + CONVERT(varchar(1), @Commit)
      + CASE WHEN @Commit = 1 THEN '  (CHANGES WILL BE KEPT)'
             ELSE '  (dry run - everything rolls back)' END;
PRINT '=====================================================';

IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
BEGIN
    RAISERROR('dbo.Users does not exist. Stopping.', 16, 1);
    RETURN;
END


/* =====================================================================
   PART 1 - THE TRANSACTION
   ===================================================================== */

DECLARE @MadeOutbound bit = 0;
DECLARE @MadePrefs    bit = 0;
DECLARE @MadeIx       int = 0;

BEGIN TRAN;

BEGIN TRY

    /* ---- 1a. OutboundMessages --------------------------------------- */

    IF OBJECT_ID(N'dbo.OutboundMessages', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.OutboundMessages
        (
            -- Also the PROVIDER IDEMPOTENCY KEY. Every send to Resend
            -- carries it as an Idempotency-Key header, so a retry after a
            -- timeout does not deliver a second copy to a customer.
            Id                uniqueidentifier NOT NULL
                CONSTRAINT PK_OutboundMessages PRIMARY KEY,

            TenantId          uniqueidentifier NOT NULL,

            -- OutboundChannel: 0 Email, 1 Sms
            Channel           int              NOT NULL
                CONSTRAINT DF_OutboundMessages_Channel DEFAULT (0),

            -- OutboundStatus: 0 Pending, 1 Sending, 2 Sent,
            --                 3 Failed, 4 Dead, 5 Cancelled
            Status            int              NOT NULL
                CONSTRAINT DF_OutboundMessages_Status DEFAULT (0),

            -- NotificationEventType, for the log's filter
            EventType         int              NOT NULL,

            -- Null for a message to a customer, who is not a user.
            RecipientUserId   uniqueidentifier NULL,

            -- Resolved at QUEUE time, not send time: if the address changes
            -- between the deal closing and the worker running, the message
            -- still goes where it was addressed.
            ToAddress         nvarchar(320)    NOT NULL,
            ToName            nvarchar(200)    NULL,
            ReplyToAddress    nvarchar(320)    NULL,

            Subject           nvarchar(300)    NOT NULL,

            -- Rendered at queue time. A message records what was SAID;
            -- rendering later would quietly change the wording if the
            -- underlying record moved on.
            BodyHtml          nvarchar(max)    NOT NULL,
            BodyText          nvarchar(max)    NOT NULL,

            EntityType        nvarchar(50)     NULL,
            EntityId          uniqueidentifier NULL,

            AttemptCount      int              NOT NULL
                CONSTRAINT DF_OutboundMessages_AttemptCount DEFAULT (0),

            -- Null means "due now".
            NextAttemptAtUtc  datetime2(7)     NULL,

            -- Set when a worker claims the row. A crash leaves this in the
            -- past and the row becomes claimable again — which is exactly
            -- why the idempotency key above matters.
            LockedUntilUtc    datetime2(7)     NULL,

            ProviderMessageId nvarchar(200)    NULL,

            -- Bounded so a provider returning a wall of text cannot fail
            -- the save that is trying to RECORD the failure.
            LastError         nvarchar(2000)   NULL,

            CreatedAtUtc      datetime2(7)     NOT NULL
                CONSTRAINT DF_OutboundMessages_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
            SentAtUtc         datetime2(7)     NULL,
            UpdatedAtUtc      datetime2(7)     NULL,
            UpdatedBy         nvarchar(200)    NULL
        );

        SET @MadeOutbound = 1;
        PRINT '  created dbo.OutboundMessages';
    END
    ELSE
        PRINT '  dbo.OutboundMessages already exists';

    /* ---- 1b. UserNotificationPreferences ----------------------------- */

    IF OBJECT_ID(N'dbo.UserNotificationPreferences', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.UserNotificationPreferences
        (
            Id           uniqueidentifier NOT NULL
                CONSTRAINT PK_UserNotificationPreferences PRIMARY KEY,

            TenantId     uniqueidentifier NOT NULL,
            UserId       uniqueidentifier NOT NULL,

            -- NotificationEventType
            EventType    int              NOT NULL,

            InApp        bit              NOT NULL
                CONSTRAINT DF_UserNotificationPreferences_InApp DEFAULT (1),
            Email        bit              NOT NULL
                CONSTRAINT DF_UserNotificationPreferences_Email DEFAULT (0),

            UpdatedAtUtc datetime2(7)     NOT NULL
                CONSTRAINT DF_UserNotificationPreferences_UpdatedAtUtc DEFAULT (SYSUTCDATETIME()),
            UpdatedBy    nvarchar(200)    NULL,

            -- CASCADE here, unlike Notifications: a preference is a setting,
            -- not a record of anything. Deleting the user should take it.
            CONSTRAINT FK_UserNotificationPreferences_Users_UserId
                FOREIGN KEY (UserId) REFERENCES dbo.Users (Id)
                ON DELETE CASCADE
        );

        SET @MadePrefs = 1;
        PRINT '  created dbo.UserNotificationPreferences';
    END
    ELSE
        PRINT '  dbo.UserNotificationPreferences already exists';

    /* ---- 1c. indexes ------------------------------------------------- */
    -- Through sp_executesql: the tables may have been created moments ago
    -- in this same batch.

    IF NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = N'IX_OutboundMessages_Claim'
                     AND object_id = OBJECT_ID(N'dbo.OutboundMessages'))
    BEGIN
        -- FILTERED, and the reason is in the header. This is the index the
        -- worker hits every 15 seconds for the life of the product.
        SET @sql = N'
        CREATE INDEX IX_OutboundMessages_Claim
            ON dbo.OutboundMessages (Status, NextAttemptAtUtc)
            WHERE Status IN (0, 3);';
        EXEC sp_executesql @sql;
        SET @MadeIx += 1;
        PRINT '  created IX_OutboundMessages_Claim (filtered)';
    END

    IF NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = N'IX_OutboundMessages_Log'
                     AND object_id = OBJECT_ID(N'dbo.OutboundMessages'))
    BEGIN
        SET @sql = N'
        CREATE INDEX IX_OutboundMessages_Log
            ON dbo.OutboundMessages (TenantId, CreatedAtUtc DESC);';
        EXEC sp_executesql @sql;
        SET @MadeIx += 1;
        PRINT '  created IX_OutboundMessages_Log';
    END

    IF NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = N'IX_OutboundMessages_Entity'
                     AND object_id = OBJECT_ID(N'dbo.OutboundMessages'))
    BEGIN
        SET @sql = N'
        CREATE INDEX IX_OutboundMessages_Entity
            ON dbo.OutboundMessages (TenantId, EntityType, EntityId);';
        EXEC sp_executesql @sql;
        SET @MadeIx += 1;
        PRINT '  created IX_OutboundMessages_Entity';
    END

    IF NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = N'UX_UserNotificationPreferences_User_Event'
                     AND object_id = OBJECT_ID(N'dbo.UserNotificationPreferences'))
    BEGIN
        -- UNIQUE: two rows for the same user and event would make "what
        -- does this person want" ambiguous, and the settings page would
        -- write a duplicate every time it was used.
        SET @sql = N'
        CREATE UNIQUE INDEX UX_UserNotificationPreferences_User_Event
            ON dbo.UserNotificationPreferences (TenantId, UserId, EventType);';
        EXEC sp_executesql @sql;
        SET @MadeIx += 1;
        PRINT '  created UX_UserNotificationPreferences_User_Event';
    END

    IF @MadeIx = 0
        PRINT '  all indexes already present';


    /* =================================================================
       PART 2 - VERIFICATION (inside the transaction)
       ================================================================= */

    PRINT '';
    PRINT '--- Part 2a: indexes (the Claim index MUST show a filter) ---';

    SET @sql = N'
    SELECT
        TableName  = OBJECT_NAME(i.object_id),
        IndexName  = i.name,
        IsUnique   = i.is_unique,
        IsFiltered = i.has_filter,
        FilterText = i.filter_definition
    FROM sys.indexes i
    WHERE i.object_id IN (OBJECT_ID(N''dbo.OutboundMessages''),
                          OBJECT_ID(N''dbo.UserNotificationPreferences''))
      AND i.name IS NOT NULL
    ORDER BY TableName, IndexName;';

    EXEC sp_executesql @sql;

    PRINT '';
    PRINT '--- Part 2b: row counts (0 on a first run, and that is correct) ---';

    SET @sql = N'
    SELECT
        OutboundMessages = (SELECT COUNT(*) FROM dbo.OutboundMessages),
        Preferences      = (SELECT COUNT(*) FROM dbo.UserNotificationPreferences);';

    EXEC sp_executesql @sql;

    PRINT '';
    PRINT '--- summary ---';
    PRINT '  OutboundMessages            : ' + CASE WHEN @MadeOutbound = 1 THEN 'created' ELSE 'already there' END;
    PRINT '  UserNotificationPreferences : ' + CASE WHEN @MadePrefs    = 1 THEN 'created' ELSE 'already there' END;
    PRINT '  indexes created             : ' + CONVERT(varchar(20), @MadeIx);


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
    RAISERROR('038 failed at line %d: %s', 16, 1, @ErrLine, @ErrMsg);

END CATCH;


/* =====================================================================
   PART 3 - USEFUL QUERIES ONCE IT IS RUNNING (not run)
   ---------------------------------------------------------------------
   Is the worker actually draining the queue?

       SELECT Status, Rows = COUNT(*),
              Oldest = MIN(CreatedAtUtc), Newest = MAX(CreatedAtUtc)
       FROM dbo.OutboundMessages
       GROUP BY Status ORDER BY Status;

     Status 0 Pending / 1 Sending / 2 Sent / 3 Failed / 4 Dead / 5 Cancelled.

     Rows stuck at 0 with an Oldest more than a minute or two back means
     the worker is not running. The three usual causes, in order:

       1. "Always On" is off on the App Service, so the host unloads when
          idle and nothing drains until a request wakes it.
       2. Email__ApiKey is not set, so NullEmailSender is registered and
          the worker deliberately leaves the queue alone. Harmless and
          reversible — the backlog goes out when a key appears.
       3. Something threw at startup. Check the log for "Outbound message
          worker started".

   What failed, and why?

       SELECT TOP (50) CreatedAtUtc, ToAddress, Subject, AttemptCount,
              NextAttemptAtUtc, LastError
       FROM dbo.OutboundMessages
       WHERE Status IN (3, 4)
       ORDER BY CreatedAtUtc DESC;

   Anything stuck in Sending? Only possible if an instance died mid-send.
   It clears itself once LockedUntilUtc passes — do not "fix" it by hand
   unless LockedUntilUtc is well in the past:

       SELECT Id, ToAddress, LockedUntilUtc, AttemptCount
       FROM dbo.OutboundMessages
       WHERE Status = 1 AND LockedUntilUtc < SYSUTCDATETIME();
   ===================================================================== */
