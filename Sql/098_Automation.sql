/* =====================================================================
   098_Automation.sql — workflow automation

   Adds:
     dbo.AutomationRules     one row per rule: When / If / Then
     dbo.AutomationRuns      the rule log — what each rule did, or why not
     dbo.Plans.MaxAutomationRules  INT NOT NULL DEFAULT 0
                             how many rules a workspace on the plan may have
                             switched ON. 0 = automation not included.

   Starting limits for the plans you already have (change them any time on
   Admin → Plans → a plan → "Automation rules"):
       trial plans            5   (so a prospect can try it in the demo)
       "starter"              3
       "professional"        20
       "enterprise"         100
       any other paid plan    5

   Nothing existing is changed apart from that one new column.

   RUN IT ON BOTH DATABASES — your development database AND
   MerkaiTrial_E2E. The end-to-end tests use the second one, and the app
   will not start against a database without these tables.

   HOW TO RUN
     1. Run as-is. @Commit = 0 — everything is ROLLED BACK. Grid 1: every
        row should say OK.
     2. Change @Commit to 1 and run again. Grid 1 again: all OK.

   SAFE TO RE-RUN. Every CREATE / ALTER is guarded, and the starting
   limits are only written when the column is first added.
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit BIT = 0;      -- flip to 1 to commit

BEGIN TRAN;

-- ── AutomationRules ──────────────────────────────────────────────────
IF OBJECT_ID(N'dbo.AutomationRules', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AutomationRules
    (
        Id                UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_AutomationRules PRIMARY KEY,
        TenantId          UNIQUEIDENTIFIER NOT NULL,
        Name              NVARCHAR(150)    NOT NULL,
        Description       NVARCHAR(500)    NULL,
        EntityType        NVARCHAR(20)     NOT NULL,          -- Lead | Deal
        [Trigger]         INT              NOT NULL,          -- 0 created, 1 field changed, 2 time-based (099)
        TriggerField      NVARCHAR(50)     NULL,
        TriggerFromValue  NVARCHAR(200)    NULL,
        TriggerToValue    NVARCHAR(200)    NULL,
        ConditionsJson    NVARCHAR(MAX)    NOT NULL CONSTRAINT DF_AutomationRules_Conditions DEFAULT (N'[]'),
        ActionsJson       NVARCHAR(MAX)    NOT NULL CONSTRAINT DF_AutomationRules_Actions    DEFAULT (N'[]'),
        IsActive          BIT              NOT NULL CONSTRAINT DF_AutomationRules_IsActive   DEFAULT (0),
        RoundRobinCursor  INT              NOT NULL CONSTRAINT DF_AutomationRules_Cursor     DEFAULT (0),
        RunCount          INT              NOT NULL CONSTRAINT DF_AutomationRules_RunCount   DEFAULT (0),
        LastRunAtUtc      DATETIME2        NULL,
        CreatedAtUtc      DATETIME2        NOT NULL,
        CreatedBy         NVARCHAR(200)    NULL,
        UpdatedAtUtc      DATETIME2        NULL,
        UpdatedBy         NVARCHAR(200)    NULL,
        IsDeleted         BIT              NOT NULL CONSTRAINT DF_AutomationRules_IsDeleted  DEFAULT (0),
        CONSTRAINT FK_AutomationRules_Tenants FOREIGN KEY (TenantId) REFERENCES dbo.Tenants (Id)
    );

    -- Every save of a lead or deal asks "which ACTIVE rules does this
    -- workspace have for this kind of record?" — this is that question.
    CREATE INDEX IX_AutomationRules_Tenant_Entity
        ON dbo.AutomationRules (TenantId, EntityType, IsActive)
        WHERE IsDeleted = 0;

    PRINT 'Created dbo.AutomationRules';
END

-- ── AutomationRuns ───────────────────────────────────────────────────
IF OBJECT_ID(N'dbo.AutomationRuns', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AutomationRuns
    (
        Id            UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_AutomationRuns PRIMARY KEY,
        TenantId      UNIQUEIDENTIFIER NOT NULL,
        RuleId        UNIQUEIDENTIFIER NOT NULL,
        RuleName      NVARCHAR(150)    NOT NULL,
        EntityType    NVARCHAR(20)     NOT NULL,
        EntityId      UNIQUEIDENTIFIER NOT NULL,
        EntityName    NVARCHAR(300)    NULL,
        Status        INT              NOT NULL,          -- 0 done, 1 skipped, 2 failed, 3 partial
        Detail        NVARCHAR(2000)   NULL,
        CreatedAtUtc  DATETIME2        NOT NULL,
        CONSTRAINT FK_AutomationRuns_Rules FOREIGN KEY (RuleId) REFERENCES dbo.AutomationRules (Id)
    );

    CREATE INDEX IX_AutomationRuns_Tenant_Created ON dbo.AutomationRuns (TenantId, CreatedAtUtc DESC);
    CREATE INDEX IX_AutomationRuns_Rule_Created   ON dbo.AutomationRuns (RuleId, CreatedAtUtc DESC);

    PRINT 'Created dbo.AutomationRuns';
END

-- ── Plans.MaxAutomationRules ─────────────────────────────────────────
IF COL_LENGTH(N'dbo.Plans', N'MaxAutomationRules') IS NULL
BEGIN
    EXEC sp_executesql N'ALTER TABLE dbo.Plans ADD MaxAutomationRules INT NOT NULL
                         CONSTRAINT DF_Plans_MaxAutomationRules DEFAULT (0);';

    -- Starting limits, written ONCE, only when the column is new.
    EXEC sp_executesql N'
        UPDATE dbo.Plans SET MaxAutomationRules =
            CASE
                WHEN IsTrial = 1                THEN 5
                WHEN LOWER(Name) = N''starter''      THEN 3
                WHEN LOWER(Name) = N''professional'' THEN 20
                WHEN LOWER(Name) = N''enterprise''   THEN 100
                ELSE 5
            END;';

    PRINT 'Added Plans.MaxAutomationRules with starting limits';
END

-- ── Checks ───────────────────────────────────────────────────────────
SELECT Item, Result FROM (
    SELECT 1 AS N, 'AutomationRules table'        AS Item,
           CASE WHEN OBJECT_ID(N'dbo.AutomationRules', N'U') IS NOT NULL THEN 'OK' ELSE 'MISSING' END AS Result
    UNION ALL
    SELECT 2, 'AutomationRuns table',
           CASE WHEN OBJECT_ID(N'dbo.AutomationRuns', N'U') IS NOT NULL THEN 'OK' ELSE 'MISSING' END
    UNION ALL
    SELECT 3, 'Plans.MaxAutomationRules column',
           CASE WHEN COL_LENGTH(N'dbo.Plans', N'MaxAutomationRules') IS NOT NULL THEN 'OK' ELSE 'MISSING' END
) x ORDER BY N;

-- The limit each plan now has, for a glance.
IF COL_LENGTH(N'dbo.Plans', N'MaxAutomationRules') IS NOT NULL
    EXEC sp_executesql N'SELECT Name, DisplayName, IsTrial, MaxAutomationRules FROM dbo.Plans ORDER BY SortOrder, Name;';

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
