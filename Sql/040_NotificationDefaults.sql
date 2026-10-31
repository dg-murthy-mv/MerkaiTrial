/* =====================================================================
   040_NotificationDefaults.sql
   ---------------------------------------------------------------------
   WHAT THIS DOES
     Creates ONE table:

       dbo.TenantNotificationDefaults
           TenantId, EventType, InApp, Email, IsLocked

     It is the workspace's STARTING POSITION for each notification event,
     and — when IsLocked is set — the final word.

   WHY IT EXISTS
     Preferences were per-user only, which left a workspace admin with no
     lever at all. Ticking a box on your OWN settings page changed nothing
     for anybody else, and nothing in the UI said so. A manager could also
     turn approval emails off and quotes would sit unapproved with nobody
     finding out.

   HOW IT RESOLVES, per user per event
       the tenant row, IF IsLocked = 1
       otherwise the user's UserNotificationPreferences row
       otherwise the tenant row
       otherwise the built-in NotificationDefaults

     That order lives in ONE method in C# (NotificationResolution.For),
     used by both the dispatcher and the settings page — a screen that
     computed it differently would be telling people something untrue
     about their own settings.

   NO SEED. AGAIN, AND FOR THE SAME REASON.
     A tenant with no rows is the NORMAL case and means "use the built-in
     defaults". Seeding eight rows per tenant would look tidier and would
     be a trap: add a ninth event type next year and every existing tenant
     silently has no row for it.

     There IS one optional seed below — Part 3, not run — for turning on
     the approval lock across every workspace in one go. Read it before
     using it.

   HOW TO RUN
     1. Run as-is. @Commit = 0, so it rolls back.
     2. Set @Commit = 1 and run again.

   RE-RUN SAFETY
     Table and index are guarded. A second run does nothing.
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit BIT = 0;          -- <<<< 0 = rollback (safe). 1 = keep.

DECLARE @sql nvarchar(max);

PRINT '=====================================================';
PRINT ' 040 - workspace notification defaults';
PRINT ' @Commit = ' + CONVERT(varchar(1), @Commit)
      + CASE WHEN @Commit = 1 THEN '  (CHANGES WILL BE KEPT)'
             ELSE '  (dry run - everything rolls back)' END;
PRINT '=====================================================';

IF OBJECT_ID(N'dbo.UserNotificationPreferences', N'U') IS NULL
BEGIN
    RAISERROR('dbo.UserNotificationPreferences does not exist - run 038 first. Stopping.', 16, 1);
    RETURN;
END


/* =====================================================================
   PART 1 - THE TRANSACTION
   ===================================================================== */

DECLARE @MadeTable bit = 0;
DECLARE @MadeIx    bit = 0;

BEGIN TRAN;

BEGIN TRY

    IF OBJECT_ID(N'dbo.TenantNotificationDefaults', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.TenantNotificationDefaults
        (
            Id           uniqueidentifier NOT NULL
                CONSTRAINT PK_TenantNotificationDefaults PRIMARY KEY,

            TenantId     uniqueidentifier NOT NULL,

            -- NotificationEventType:
            --   0 QuoteSent            1 QuoteAccepted    2 QuoteRejected
            --   3 ApprovalRequested    4 ApprovalDecided  5 DealStageChanged
            --   6 LeadAssigned         7 DealAssigned
            EventType    int              NOT NULL,

            InApp        bit              NOT NULL
                CONSTRAINT DF_TenantNotificationDefaults_InApp DEFAULT (1),
            Email        bit              NOT NULL
                CONSTRAINT DF_TenantNotificationDefaults_Email DEFAULT (0),

            -- The lever. When 1, a user's own row is IGNORED for this
            -- event and the API refuses to save one.
            IsLocked     bit              NOT NULL
                CONSTRAINT DF_TenantNotificationDefaults_IsLocked DEFAULT (0),

            UpdatedAtUtc datetime2(7)     NOT NULL
                CONSTRAINT DF_TenantNotificationDefaults_UpdatedAtUtc DEFAULT (SYSUTCDATETIME()),
            UpdatedBy    nvarchar(200)    NULL
        );

        SET @MadeTable = 1;
        PRINT '  created dbo.TenantNotificationDefaults';
    END
    ELSE
        PRINT '  dbo.TenantNotificationDefaults already exists';

    IF NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = N'UX_TenantNotificationDefaults_Tenant_Event'
                     AND object_id = OBJECT_ID(N'dbo.TenantNotificationDefaults'))
    BEGIN
        -- UNIQUE: two rows for the same tenant and event would make the
        -- resolution silently depend on which one EF returned first.
        SET @sql = N'
        CREATE UNIQUE INDEX UX_TenantNotificationDefaults_Tenant_Event
            ON dbo.TenantNotificationDefaults (TenantId, EventType);';
        EXEC sp_executesql @sql;
        SET @MadeIx = 1;
        PRINT '  created UX_TenantNotificationDefaults_Tenant_Event';
    END


    /* =================================================================
       PART 2 - VERIFICATION
       ================================================================= */

    PRINT '';
    PRINT '--- Part 2a: the table ---';

    SET @sql = N'
    SELECT ColumnName = c.name, DataType = ty.name, Nullable = c.is_nullable
    FROM sys.columns c
    JOIN sys.types ty ON ty.user_type_id = c.user_type_id
    WHERE c.object_id = OBJECT_ID(N''dbo.TenantNotificationDefaults'')
    ORDER BY c.column_id;';
    EXEC sp_executesql @sql;

    PRINT '';
    PRINT '--- Part 2b: who currently overrides what (the numbers the new tab shows) ---';

    SELECT
        TenantName   = t.Name,
        ActiveUsers  = (SELECT COUNT(*) FROM dbo.Users u
                        WHERE u.TenantId = t.Id AND u.IsActive = 1 AND u.IsDeleted = 0),
        UsersWithNoEmail = (SELECT COUNT(*) FROM dbo.Users u
                            WHERE u.TenantId = t.Id AND u.IsActive = 1 AND u.IsDeleted = 0
                              AND (u.Email IS NULL OR u.Email = '')),
        SavedPreferenceRows = (SELECT COUNT(*) FROM dbo.UserNotificationPreferences p
                               WHERE p.TenantId = t.Id)
    FROM dbo.Tenants t
    WHERE t.IsDeleted = 0
    ORDER BY t.Name;

    PRINT '';
    PRINT '--- summary ---';
    PRINT '  table created : ' + CASE WHEN @MadeTable = 1 THEN 'yes' ELSE 'already there' END;
    PRINT '  index created : ' + CASE WHEN @MadeIx    = 1 THEN 'yes' ELSE 'already there' END;


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
    RAISERROR('040 failed at line %d: %s', 16, 1, @ErrLine, @ErrMsg);

END CATCH;


/* =====================================================================
   PART 3 - OPTIONAL: lock the approval events everywhere (NOT run)
   ---------------------------------------------------------------------
   Normally you set this per workspace on the new Workspace defaults tab.
   This is for doing all of them at once, and it is the ONE lock I would
   apply without being asked:

     QuoteApprovalRequested (3)  an approver who misses this blocks a deal
     QuoteApprovalDecided   (4)  the requester is waiting to send

   Everything else should stay unlocked. Forcing notifications people did
   not ask for is how you teach them to ignore all of them, including
   these two.

       DECLARE @Commit BIT = 0;
       BEGIN TRAN;

       INSERT INTO dbo.TenantNotificationDefaults
           (Id, TenantId, EventType, InApp, Email, IsLocked, UpdatedAtUtc, UpdatedBy)
       SELECT NEWID(), t.Id, e.EventType, 1, 1, 1, SYSUTCDATETIME(), '040_seed'
       FROM   dbo.Tenants t
       CROSS JOIN (VALUES (3), (4)) AS e(EventType)
       WHERE  t.IsDeleted = 0
         AND  NOT EXISTS (SELECT 1 FROM dbo.TenantNotificationDefaults d
                          WHERE d.TenantId = t.Id AND d.EventType = e.EventType);

       SELECT t.Name, d.EventType, d.InApp, d.Email, d.IsLocked
       FROM dbo.TenantNotificationDefaults d
       JOIN dbo.Tenants t ON t.Id = d.TenantId
       ORDER BY t.Name, d.EventType;

       IF @Commit = 1 COMMIT ELSE ROLLBACK;

   Users' own rows are NOT deleted by this. While an event is locked they
   are ignored; unlock it later and everyone's choice comes back. Deleting
   them would make unlocking a silently destructive act.
   ===================================================================== */
