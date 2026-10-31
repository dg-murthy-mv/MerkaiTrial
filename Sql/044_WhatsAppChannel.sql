-- =====================================================================
-- 044_WhatsAppChannel.sql
--
-- WhatsApp as a third notification channel, alongside the bell and email.
--
-- WHAT THIS DOES
--   1. Users        + MobileE164, + WhatsAppOptInAtUtc
--   2. UserNotificationPreferences   + WhatsApp  (bit, default 0)
--   3. TenantNotificationDefaults    + WhatsApp  (bit, default 0)
--   4. WhatsAppTemplates             new table, seeded with the eight
--                                    notification events, all INACTIVE
--
-- NOTHING IS SWITCHED ON. Every new flag defaults to 0 and every seeded
-- template row is inactive, so applying this changes no behaviour at all.
-- The channel starts working when 045 adds the sender AND an admin fills
-- in template names AND a user gives a number and opts in — three
-- separate deliberate acts, which is the right number for a channel that
-- costs money per message.
--
-- SAFE TO RE-RUN. Every ALTER and CREATE is guarded.
--
-- HOW TO RUN
--   @Commit = 0 first and read the verification grids at the bottom.
--   Then set @Commit = 1 and run it again.
-- =====================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit BIT = 0;

BEGIN TRAN;

-- ─────────────────────────────────────────────────────────────────────
-- 1. Users — the number to send to, and permission to use it
-- ─────────────────────────────────────────────────────────────────────

IF COL_LENGTH('dbo.Users', 'MobileE164') IS NULL
BEGIN
    ALTER TABLE dbo.Users ADD MobileE164 nvarchar(20) NULL;
    PRINT 'Users.MobileE164 added.';
END
ELSE
    PRINT 'Users.MobileE164 already present — skipped.';

IF COL_LENGTH('dbo.Users', 'WhatsAppOptInAtUtc') IS NULL
BEGIN
    ALTER TABLE dbo.Users ADD WhatsAppOptInAtUtc datetime2(7) NULL;
    PRINT 'Users.WhatsAppOptInAtUtc added.';
END
ELSE
    PRINT 'Users.WhatsAppOptInAtUtc already present — skipped.';

-- Deliberately NOT populated from Users.Phone.
--
-- Phone holds whatever was typed, which across four countries includes
-- extensions, second numbers, landlines and notes. Guessing which of
-- those is a WhatsApp mobile, at scale, unattended, in a migration, is
-- how you message a stranger. Everyone enters their own on the settings
-- page, which also captures the opt-in the same number needs.

-- ─────────────────────────────────────────────────────────────────────
-- 2. Per-user preferences — the third channel
-- ─────────────────────────────────────────────────────────────────────

IF COL_LENGTH('dbo.UserNotificationPreferences', 'WhatsApp') IS NULL
BEGIN
    ALTER TABLE dbo.UserNotificationPreferences
        ADD WhatsApp bit NOT NULL CONSTRAINT DF_UserNotificationPreferences_WhatsApp DEFAULT (0);
    PRINT 'UserNotificationPreferences.WhatsApp added (default 0).';
END
ELSE
    PRINT 'UserNotificationPreferences.WhatsApp already present — skipped.';

-- ─────────────────────────────────────────────────────────────────────
-- 3. Workspace defaults — the third channel
-- ─────────────────────────────────────────────────────────────────────

IF COL_LENGTH('dbo.TenantNotificationDefaults', 'WhatsApp') IS NULL
BEGIN
    ALTER TABLE dbo.TenantNotificationDefaults
        ADD WhatsApp bit NOT NULL CONSTRAINT DF_TenantNotificationDefaults_WhatsApp DEFAULT (0);
    PRINT 'TenantNotificationDefaults.WhatsApp added (default 0).';
END
ELSE
    PRINT 'TenantNotificationDefaults.WhatsApp already present — skipped.';

-- ─────────────────────────────────────────────────────────────────────
-- 4. WhatsAppTemplates
--
-- No TenantId: these are OUR templates on OUR number, for messages to
-- the CRM's own users. A tenant messaging their own customers from their
-- own WhatsApp account is phase 2 and gets its own table.
-- ─────────────────────────────────────────────────────────────────────

IF OBJECT_ID('dbo.WhatsAppTemplates', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.WhatsAppTemplates
    (
        Id            uniqueidentifier NOT NULL
                      CONSTRAINT PK_WhatsAppTemplates PRIMARY KEY,
        EventType     int              NOT NULL,
        LanguageCode  nvarchar(10)     NOT NULL
                      CONSTRAINT DF_WhatsAppTemplates_LanguageCode DEFAULT (N'en'),
        TemplateName  nvarchar(512)    NOT NULL,
        Category      nvarchar(30)     NOT NULL
                      CONSTRAINT DF_WhatsAppTemplates_Category DEFAULT (N'utility'),
        BodyPreview   nvarchar(2000)   NULL,
        VariableCount int              NOT NULL
                      CONSTRAINT DF_WhatsAppTemplates_VariableCount DEFAULT (0),
        VariableHints nvarchar(1000)   NULL,
        IsActive      bit              NOT NULL
                      CONSTRAINT DF_WhatsAppTemplates_IsActive DEFAULT (0),
        UpdatedAtUtc  datetime2(7)     NOT NULL
                      CONSTRAINT DF_WhatsAppTemplates_UpdatedAtUtc DEFAULT (SYSUTCDATETIME()),
        UpdatedBy     nvarchar(200)    NULL
    );

    PRINT 'dbo.WhatsAppTemplates created.';
END
ELSE
    PRINT 'dbo.WhatsAppTemplates already present — skipped.';

-- The unique index goes through sp_executesql: on a first run the table
-- was created moments ago in this same batch, and referencing its columns
-- directly would fail to parse.
IF OBJECT_ID('dbo.WhatsAppTemplates', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = 'UX_WhatsAppTemplates_Event_Language'
                     AND object_id = OBJECT_ID('dbo.WhatsAppTemplates'))
BEGIN
    EXEC sp_executesql N'
        CREATE UNIQUE INDEX UX_WhatsAppTemplates_Event_Language
            ON dbo.WhatsAppTemplates (EventType, LanguageCode);';

    PRINT 'UX_WhatsAppTemplates_Event_Language created.';
END

-- ─────────────────────────────────────────────────────────────────────
-- 5. Seed the eight events — INACTIVE, with suggested names
--
-- The names are suggestions, not facts: nothing is approved until
-- somebody registers them in Meta Business Manager. They are seeded so
-- the settings page has eight rows to edit rather than an empty screen
-- that looks broken, and IsActive = 0 means none of them can send.
--
-- EventType values, from NotificationEventType — append, never renumber:
--   0 QuoteSent  1 QuoteAccepted  2 QuoteRejected  3 QuoteApprovalRequested
--   4 QuoteApprovalDecided  5 DealStageChanged  6 DealAssigned  7 LeadAssigned
-- ─────────────────────────────────────────────────────────────────────

-- Declared BEFORE the insert on purpose: DECLARE resets @@ROWCOUNT, so a
-- `DECLARE @Seeded int = @@ROWCOUNT` after the insert would print 0 every
-- time and quietly tell you nothing was seeded when it was.
DECLARE @Seeded int = 0;

DECLARE @Seed TABLE
(
    EventType     int,
    TemplateName  nvarchar(512),
    VariableCount int,
    VariableHints nvarchar(1000),
    BodyPreview   nvarchar(2000)
);

INSERT INTO @Seed (EventType, TemplateName, VariableCount, VariableHints, BodyPreview)
VALUES
 (0, N'quote_sent_v1',              3, N'["quote number","customer","amount"]',
     N'Quote {{1}} has gone to {{2}} for {{3}}.'),
 (1, N'quote_accepted_v1',          3, N'["quote number","customer","amount"]',
     N'{{2}} accepted quote {{1}} for {{3}}.'),
 (2, N'quote_rejected_v1',          2, N'["quote number","customer"]',
     N'{{2}} turned down quote {{1}}.'),
 (3, N'quote_approval_requested_v1',3, N'["who asked","quote number","amount"]',
     N'{{1}} needs your approval on quote {{2}} for {{3}}.'),
 (4, N'quote_approval_decided_v1',  3, N'["quote number","decision","who decided"]',
     N'Quote {{1}} was {{2}} by {{3}}.'),
 (5, N'deal_stage_changed_v1',      3, N'["deal title","from stage","to stage"]',
     N'{{1}} moved from {{2}} to {{3}}.'),
 (6, N'deal_assigned_v1',           3, N'["who assigned","deal title","amount"]',
     N'{{1}} assigned you the deal {{2}} ({{3}}).'),
 (7, N'lead_assigned_v1',           3, N'["who assigned","lead name","company"]',
     N'{{1}} assigned you the lead {{2}} from {{3}}.');

INSERT INTO dbo.WhatsAppTemplates
    (Id, EventType, LanguageCode, TemplateName, Category,
     BodyPreview, VariableCount, VariableHints, IsActive, UpdatedAtUtc, UpdatedBy)
SELECT
    NEWID(), s.EventType, N'en', s.TemplateName, N'utility',
    s.BodyPreview, s.VariableCount, s.VariableHints, 0, SYSUTCDATETIME(), N'044 migration'
FROM @Seed s
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.WhatsAppTemplates t
    WHERE t.EventType = s.EventType AND t.LanguageCode = N'en');

SET @Seeded = @@ROWCOUNT;
PRINT CONCAT('WhatsAppTemplates seeded: ', @Seeded, ' row(s).');

-- =====================================================================
-- VERIFICATION — read these before setting @Commit = 1
-- =====================================================================

PRINT '';
PRINT '--- 1. New columns ---';

SELECT
    [Table]  = t.name,
    [Column] = c.name,
    [Type]   = ty.name,
    [Null?]  = CASE c.is_nullable WHEN 1 THEN 'NULL' ELSE 'NOT NULL' END
FROM sys.columns c
JOIN sys.tables  t  ON t.object_id = c.object_id
JOIN sys.types   ty ON ty.user_type_id = c.user_type_id
WHERE (t.name = 'Users' AND c.name IN ('MobileE164', 'WhatsAppOptInAtUtc'))
   OR (t.name IN ('UserNotificationPreferences', 'TenantNotificationDefaults')
       AND c.name = 'WhatsApp')
ORDER BY t.name, c.name;

PRINT '';
PRINT '--- 2. The eight templates (all should be inactive) ---';

SELECT
    EventType,
    [Event] = CASE EventType
                WHEN 0 THEN 'QuoteSent'
                WHEN 1 THEN 'QuoteAccepted'
                WHEN 2 THEN 'QuoteRejected'
                WHEN 3 THEN 'QuoteApprovalRequested'
                WHEN 4 THEN 'QuoteApprovalDecided'
                WHEN 5 THEN 'DealStageChanged'
                WHEN 6 THEN 'DealAssigned'
                WHEN 7 THEN 'LeadAssigned'
                ELSE CONCAT('Unknown (', EventType, ')')
              END,
    LanguageCode,
    TemplateName,
    Category,
    VariableCount,
    IsActive,
    BodyPreview
FROM dbo.WhatsAppTemplates
ORDER BY EventType, LanguageCode;

PRINT '';
PRINT '--- 3. Nothing is switched on (every count should be 0) ---';

SELECT
    [Users with a mobile]     = (SELECT COUNT(*) FROM dbo.Users WHERE MobileE164 IS NOT NULL),
    [Users opted in]          = (SELECT COUNT(*) FROM dbo.Users WHERE WhatsAppOptInAtUtc IS NOT NULL),
    [User prefs with WA on]   = (SELECT COUNT(*) FROM dbo.UserNotificationPreferences WHERE WhatsApp = 1),
    [Tenant defaults WA on]   = (SELECT COUNT(*) FROM dbo.TenantNotificationDefaults WHERE WhatsApp = 1),
    [Active templates]        = (SELECT COUNT(*) FROM dbo.WhatsAppTemplates WHERE IsActive = 1);

-- =====================================================================

IF @Commit = 1
BEGIN
    COMMIT TRAN;
    PRINT '';
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRAN;
    PRINT '';
    PRINT '=== ROLLED BACK (set @Commit = 1 to keep these changes) ===';
END
