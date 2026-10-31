/* =====================================================================
   099_AutomationTime.sql — time-based automation rules

   Adds:
     dbo.AutomationRules.TimeKind   NVARCHAR(30) NULL
     dbo.AutomationRules.TimeDays   INT NULL
         what a time-based rule watches ("noActivity", "closeSoon",
         "closePassed", "stuck") and its N days
     dbo.AutomationTimeFires
         one row each time a time-based rule acts on a record, for the
         "episode" it acted on — so it never acts twice for the same one

   Nothing existing is changed. 098 must already be in place.

   ─────────────────────────────────────────────────────────────────────
   RUN IT ON BOTH DATABASES — your development database AND
   MerkaiTrial_E2E. Last time the first run went to the dev database only:
   check the database dropdown in the toolbar before pressing Execute.
   Grid 1 now shows the database it ran on, so you can see at a glance.
   ─────────────────────────────────────────────────────────────────────

   HOW TO RUN
     1. Run as-is. @Commit = 0 — everything is ROLLED BACK. Grid 1: every
        row should say OK.
     2. Change @Commit to 1 and run again. Grid 1 again: all OK, and the
        messages end with COMMITTED.
     3. Switch the dropdown to the other database and do 1–2 again.

   SAFE TO RE-RUN. Every CREATE / ALTER is guarded.
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit BIT = 0;      -- flip to 1 to commit

IF OBJECT_ID(N'dbo.AutomationRules', N'U') IS NULL
BEGIN
    RAISERROR('dbo.AutomationRules is missing — run 098_Automation.sql on this database first.', 16, 1);
    RETURN;
END

BEGIN TRAN;

-- ── AutomationRules.TimeKind / TimeDays ─────────────────────────────
IF COL_LENGTH(N'dbo.AutomationRules', N'TimeKind') IS NULL
BEGIN
    EXEC sp_executesql N'ALTER TABLE dbo.AutomationRules ADD TimeKind NVARCHAR(30) NULL;';
    PRINT 'Added AutomationRules.TimeKind';
END

IF COL_LENGTH(N'dbo.AutomationRules', N'TimeDays') IS NULL
BEGIN
    EXEC sp_executesql N'ALTER TABLE dbo.AutomationRules ADD TimeDays INT NULL;';
    PRINT 'Added AutomationRules.TimeDays';
END

-- ── AutomationTimeFires ──────────────────────────────────────────────
IF OBJECT_ID(N'dbo.AutomationTimeFires', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AutomationTimeFires
    (
        Id            UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_AutomationTimeFires PRIMARY KEY,
        TenantId      UNIQUEIDENTIFIER NOT NULL,
        RuleId        UNIQUEIDENTIFIER NOT NULL,
        EntityId      UNIQUEIDENTIFIER NOT NULL,
        EpisodeAtUtc  DATETIME2        NOT NULL,     -- when the situation began
        FiredAtUtc    DATETIME2        NOT NULL,
        CONSTRAINT FK_AutomationTimeFires_Rules FOREIGN KEY (RuleId) REFERENCES dbo.AutomationRules (Id)
    );

    -- "Has this rule already acted on this record for this episode?" —
    -- asked for every candidate on every check. Unique, so two app
    -- instances can never both act on the same episode.
    CREATE UNIQUE INDEX UX_AutomationTimeFires_Rule_Entity_Episode
        ON dbo.AutomationTimeFires (RuleId, EntityId, EpisodeAtUtc);

    CREATE INDEX IX_AutomationTimeFires_Tenant_Fired
        ON dbo.AutomationTimeFires (TenantId, FiredAtUtc);

    PRINT 'Created dbo.AutomationTimeFires';
END

-- ── Checks ───────────────────────────────────────────────────────────
SELECT Item, Result FROM (
    SELECT 0 AS N, 'Database' AS Item, DB_NAME() AS Result
    UNION ALL
    SELECT 1, 'AutomationRules.TimeKind column',
           CASE WHEN COL_LENGTH(N'dbo.AutomationRules', N'TimeKind') IS NOT NULL THEN 'OK' ELSE 'MISSING' END
    UNION ALL
    SELECT 2, 'AutomationRules.TimeDays column',
           CASE WHEN COL_LENGTH(N'dbo.AutomationRules', N'TimeDays') IS NOT NULL THEN 'OK' ELSE 'MISSING' END
    UNION ALL
    SELECT 3, 'AutomationTimeFires table',
           CASE WHEN OBJECT_ID(N'dbo.AutomationTimeFires', N'U') IS NOT NULL THEN 'OK' ELSE 'MISSING' END
) x ORDER BY N;

IF @Commit = 1
BEGIN
    COMMIT;
    PRINT 'COMMITTED.';
END
ELSE
BEGIN
    ROLLBACK;
    PRINT 'Rolled back (dry run). Set @Commit = 1 to keep the changes.';
END
