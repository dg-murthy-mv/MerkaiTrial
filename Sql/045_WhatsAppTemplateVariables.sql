-- =====================================================================
-- 045_WhatsAppTemplateVariables.sql
--
-- Corrects the eight templates seeded by 044 to the TWO-variable shape
-- the notification pipeline can actually fill.
--
-- WHY THIS EXISTS — MY MISTAKE, NOT YOURS
--
--   044 seeded per-event variable shapes: "quote number", "customer",
--   "amount" for a sent quote; "deal title", "from stage", "to stage" for
--   a stage change. Three slots each, carefully chosen.
--
--   A NotificationRequest does not carry those fields. It carries a Title
--   and a Body — both already written for a person to read — and nothing
--   structured underneath. Every producer in the app builds those two
--   strings and nothing else. Filling three per-event slots would mean
--   changing NotificationRequest and every one of its callers.
--
--   So the templates take two variables:
--
--       {{1}}  the notification's headline
--              "Quote QUO-0012 was accepted"
--       {{2}}  its detail
--              "Acme Ltd — THB 95,000.00"
--
--   That is a better fit than it sounds: those two strings are what the
--   bell and the email already show, so WhatsApp says the same thing the
--   other channels say, which is what you want from three channels
--   carrying one notification.
--
--   Caught before you submitted anything to Meta for approval, which is
--   the only reason this is a small correction rather than eight
--   re-approvals.
--
-- WHAT IT TOUCHES
--   ONLY rows still marked UpdatedBy = '044 migration' AND IsActive = 0 —
--   in other words, still exactly as seeded. Anything you have edited,
--   renamed or switched on is left completely alone.
--
-- SAFE TO RE-RUN.
--
-- HOW TO RUN
--   @Commit = 0 first and read the grid. Then set @Commit = 1.
-- =====================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit BIT = 0;

BEGIN TRAN;

DECLARE @Updated int = 0;

UPDATE t
SET
    t.VariableCount = 2,
    t.VariableHints = N'["the notification headline","the detail line"]',
    t.BodyPreview   = CASE t.EventType
        WHEN 0 THEN N'Merkai: {{1}} — {{2}}'
        WHEN 1 THEN N'Merkai: {{1}} — {{2}}'
        WHEN 2 THEN N'Merkai: {{1}} — {{2}}'
        WHEN 3 THEN N'Merkai: {{1}} — {{2}}'
        WHEN 4 THEN N'Merkai: {{1}} — {{2}}'
        WHEN 5 THEN N'Merkai: {{1}} — {{2}}'
        WHEN 6 THEN N'Merkai: {{1}} — {{2}}'
        WHEN 7 THEN N'Merkai: {{1}} — {{2}}'
        ELSE t.BodyPreview
    END,
    t.UpdatedAtUtc  = SYSUTCDATETIME(),
    t.UpdatedBy     = N'045 migration'
FROM dbo.WhatsAppTemplates t
WHERE t.UpdatedBy = N'044 migration'
  AND t.IsActive = 0;

SET @Updated = @@ROWCOUNT;

PRINT CONCAT('Templates corrected to the two-variable shape: ', @Updated, ' row(s).');
PRINT '';
PRINT 'Rows NOT touched (already edited or switched on):';

SELECT
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
    TemplateName, VariableCount, IsActive, UpdatedBy
FROM dbo.WhatsAppTemplates
WHERE UpdatedBy <> N'045 migration'
ORDER BY EventType;

-- =====================================================================
-- VERIFICATION
-- =====================================================================

PRINT '';
PRINT '--- Every template, after the change ---';

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
    TemplateName,
    LanguageCode,
    Category,
    VariableCount,
    IsActive,
    BodyPreview
FROM dbo.WhatsAppTemplates
ORDER BY EventType, LanguageCode;

PRINT '';
PRINT '--- Anything with the wrong variable count will never send ---';

SELECT [Templates needing attention] = COUNT(*)
FROM dbo.WhatsAppTemplates
WHERE VariableCount <> 2;

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
