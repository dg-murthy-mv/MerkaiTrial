-- =====================================================================
-- 048_WhatsAppNamedVariables.sql
--
-- Rewrites the "Approved body" reference text on all eight templates for
-- Meta's named-variable rules.
--
-- WHY
--   The seeded text reads:
--
--       Merkai: {{1}} — {{2}}
--
--   Meta's template editor now rejects that on three counts, and the
--   screen was quietly telling you to build something unbuildable:
--
--     1. {{1}} and {{2}} are gone. Variables must be NAMED — lowercase
--        letters, underscores — and Merkai sends them as
--        {{headline}} and {{detail}}.
--
--     2. A body may not START or END with a variable. "Merkai: {{1}} —
--        {{2}}" ends with one, so it is refused however it is named.
--
--     3. A body that reads like an invitation — "Merkai update… open
--        your workspace" — is classified as MARKETING, which costs more
--        per message and can be blocked entirely for recipients who only
--        agreed to notifications. Naming the event in the fixed text
--        reads as an account notice and passes as UTILITY.
--
--   So each row now carries wording that names its own event, which is
--   both what Meta wants and clearer for the person reading it on their
--   phone.
--
-- THIS COLUMN IS REFERENCE TEXT ONLY. Meta stores and sends its own copy
-- of the approved body; BodyPreview is what the admin screen shows you so
-- you know what you approved. Changing it sends nothing and breaks
-- nothing — but leaving it wrong costs someone an hour and a rejected
-- template, which is why it is worth a migration.
--
-- WHAT IT TOUCHES
--   ONLY rows whose BodyPreview is still EXACTLY the seeded
--   'Merkai: {{1}} — {{2}}'. Anything you have typed yourself is left
--   alone, and the grid at the end shows you which rows were skipped.
--
--   IsActive is not part of the guard — reference text is worth fixing
--   whether or not the row is switched on.
--
-- SAFE TO RE-RUN. A second run finds nothing and says so.
--
-- HOW TO RUN
--   1. Run it as-is. @Commit = 0, so it rolls back and only shows you
--      what it WOULD do.
--   2. Read the grids.
--   3. Set @Commit = 1 and run it again.
-- =====================================================================

SET NOCOUNT ON;

DECLARE @Commit BIT = 0;

BEGIN TRAN;

IF OBJECT_ID(N'dbo.WhatsAppTemplates', N'U') IS NULL
BEGIN
    PRINT 'dbo.WhatsAppTemplates does not exist. Run 044_WhatsAppChannel.sql first.';
    ROLLBACK TRAN;
    RETURN;
END;

-- The tail every template shares. Text after the last variable is not
-- decoration — it is what stops Meta refusing the body.
DECLARE @Tail nvarchar(200) =
    N' {{headline}} — {{detail}}. Sign in to your account to view it.';

DECLARE @OldSeed nvarchar(100) = N'Merkai: {{1}} — {{2}}';

DECLARE @New TABLE (EventType int, BodyPreview nvarchar(2000));

INSERT INTO @New (EventType, BodyPreview)
VALUES
 (0, N'A quote has been sent from your Merkai account.'              + @Tail),
 (1, N'A quote in your Merkai account has been accepted.'            + @Tail),
 (2, N'A quote in your Merkai account has been declined.'            + @Tail),
 (3, N'A quote in your Merkai account needs your approval.'          + @Tail),
 (4, N'An approval request in your Merkai account has been decided.' + @Tail),
 (5, N'A deal in your Merkai account has moved to a new stage.'      + @Tail),
 (6, N'A lead has been assigned to you in Merkai.'                   + @Tail),   -- 6 = LeadAssigned
 (7, N'A deal has been assigned to you in Merkai.'                   + @Tail);   -- 7 = DealAssigned

PRINT '--- BEFORE ---';

SELECT EventType, LanguageCode, TemplateName, BodyPreview, VariableHints, IsActive, UpdatedBy
FROM dbo.WhatsAppTemplates
ORDER BY EventType, LanguageCode;

-- ── The rewrite ──────────────────────────────────────────────────────
DECLARE @Changed int = 0;

UPDATE t
SET t.BodyPreview   = n.BodyPreview,
    t.VariableHints = N'["headline","detail"]',
    t.UpdatedAtUtc  = SYSUTCDATETIME(),
    t.UpdatedBy     = N'048 migration'
FROM dbo.WhatsAppTemplates t
INNER JOIN @New n ON n.EventType = t.EventType
WHERE t.BodyPreview = @OldSeed;

SET @Changed = @@ROWCOUNT;

PRINT '--- CHANGED ---';
PRINT 'Rows rewritten: ' + CONVERT(varchar(10), @Changed);

IF @Changed = 0
    PRINT 'Nothing to change — already rewritten, or edited by hand.';

PRINT '--- AFTER ---';

SELECT
    EventType,
    EventName = CASE EventType
                    WHEN 0 THEN 'QuoteSent'
                    WHEN 1 THEN 'QuoteAccepted'
                    WHEN 2 THEN 'QuoteRejected'
                    WHEN 3 THEN 'QuoteApprovalRequested'
                    WHEN 4 THEN 'QuoteApprovalDecided'
                    WHEN 5 THEN 'DealStageChanged'
                    WHEN 6 THEN 'LeadAssigned'
                    WHEN 7 THEN 'DealAssigned'
                    ELSE CONVERT(varchar(20), EventType)
                END,
    LanguageCode,
    TemplateName,
    BodyPreview,
    Note = CASE
               WHEN BodyPreview = @OldSeed
                   THEN 'STILL THE OLD SEED — Meta will reject this wording'
               WHEN BodyPreview LIKE N'%{{1}}%' OR BodyPreview LIKE N'%{{2}}%'
                   THEN 'Has numbered variables — Meta wants {{headline}} / {{detail}}'
               WHEN BodyPreview LIKE N'%}}'
                   THEN 'Ends with a variable — Meta refuses that'
               ELSE 'OK'
           END,
    VariableCount,
    IsActive,
    UpdatedBy
FROM dbo.WhatsAppTemplates
ORDER BY EventType, LanguageCode;

IF @Commit = 1
BEGIN
    COMMIT TRAN;
    PRINT 'COMMITTED.';
END
ELSE
BEGIN
    ROLLBACK TRAN;
    PRINT 'ROLLED BACK — @Commit was 0. Set it to 1 and run again to keep these changes.';
END;
