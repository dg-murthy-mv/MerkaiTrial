-- =====================================================================
-- 046a_FixAssignedTemplateNames.sql
--
-- Swaps the template names on the two "assigned to me" events, which 044
-- seeded against the wrong enum values.
--
-- WHY THIS EXISTS — MY MISTAKE AGAIN, AND THE SAME FILE
--
--   NotificationEventType is:
--
--       DealStageChanged = 5,
--       LeadAssigned     = 6,      <-- LEAD is 6
--       DealAssigned     = 7       <-- DEAL is 7
--
--   044 seeded:
--
--       (6, N'deal_assigned_v1', ...)     <-- wrong, 6 is LeadAssigned
--       (7, N'lead_assigned_v1', ...)     <-- wrong, 7 is DealAssigned
--
--   I had the pair the other way round, and the comment block I wrote at
--   the top of that same seed repeated the mistake ("6 DealAssigned
--   7 LeadAssigned"), so the file agreed with itself and nothing caught
--   it. What caught it was the page built in 046 putting the event's
--   label and the row's template name side by side, where the crossing
--   is obvious:
--
--       "A deal is assigned to me"   ->  lead_assigned_v1
--       "A lead is assigned to me"   ->  deal_assigned_v1
--
--   Left alone, assigning a LEAD would send the DEAL template — Meta
--   would deliver "... assigned you the deal ..." for a lead — and the
--   other way round.
--
--   Caught before anything was submitted to Meta and before any template
--   was switched on, so this is a name swap and nothing else. No
--   re-approval, no messages sent wrongly.
--
-- WHAT IT TOUCHES
--   ONLY the two rows for EventType 6 and 7, and ONLY while they still
--   hold the exact crossed values from the seed. If you have already
--   renamed either one by hand, that row is left completely alone and
--   the verification grid at the bottom will say so.
--
--   IsActive is NOT part of the guard. A crossed name is wrong whether
--   the switch is on or off, and fixing it is more urgent if it is on.
--
-- VariableHints is corrected in the same pass. It is display-only — the
-- small print under the name — but a hint reading "lead name" on a deal
-- row is the same mistake wearing different clothes.
--
-- SAFE TO RE-RUN. The second run finds nothing to change and says so.
--
-- HOW TO RUN
--   1. Run it as-is. @Commit = 0, so it rolls back and only shows you
--      what it WOULD do.
--   2. Read the two grids at the bottom.
--   3. Set @Commit = 1 and run it again.
-- =====================================================================

SET NOCOUNT ON;

DECLARE @Commit BIT = 0;

BEGIN TRAN;

-- ── Guard: the table must exist ──────────────────────────────────────
IF OBJECT_ID(N'dbo.WhatsAppTemplates', N'U') IS NULL
BEGIN
    PRINT 'dbo.WhatsAppTemplates does not exist. Run 044_WhatsAppChannel.sql first.';
    ROLLBACK TRAN;
    RETURN;
END;

-- ── What it looks like before ────────────────────────────────────────
PRINT '--- BEFORE ---';

SELECT
    EventType,
    EventName = CASE EventType
                    WHEN 6 THEN 'LeadAssigned'
                    WHEN 7 THEN 'DealAssigned'
                    ELSE CONVERT(varchar(20), EventType)
                END,
    LanguageCode,
    TemplateName,
    VariableHints,
    IsActive,
    UpdatedBy
FROM dbo.WhatsAppTemplates
WHERE EventType IN (6, 7)
ORDER BY EventType, LanguageCode;

-- ── The swap ─────────────────────────────────────────────────────────
--
-- Done as two guarded UPDATEs rather than one clever statement. Each
-- only fires when that row still holds the exact wrong value, so a row
-- you have already fixed by hand, or a second run of this script,
-- changes nothing.
--
-- There is no intermediate-name dance needed: the two rows are matched
-- on EventType and written independently, so neither can collide with
-- the other.

DECLARE @FixedLead int = 0;
DECLARE @FixedDeal int = 0;

-- EventType 6 is LeadAssigned. It should be lead_assigned_v1.
UPDATE dbo.WhatsAppTemplates
SET TemplateName  = N'lead_assigned_v1',
    VariableHints = N'["headline","detail"]',
    UpdatedAtUtc  = SYSUTCDATETIME(),
    UpdatedBy     = N'046a migration'
WHERE EventType = 6
  AND TemplateName = N'deal_assigned_v1';

SET @FixedLead = @@ROWCOUNT;

-- EventType 7 is DealAssigned. It should be deal_assigned_v1.
UPDATE dbo.WhatsAppTemplates
SET TemplateName  = N'deal_assigned_v1',
    VariableHints = N'["headline","detail"]',
    UpdatedAtUtc  = SYSUTCDATETIME(),
    UpdatedBy     = N'046a migration'
WHERE EventType = 7
  AND TemplateName = N'lead_assigned_v1';

SET @FixedDeal = @@ROWCOUNT;

PRINT '--- CHANGED ---';
PRINT 'LeadAssigned (6) rows corrected: ' + CONVERT(varchar(10), @FixedLead);
PRINT 'DealAssigned (7) rows corrected: ' + CONVERT(varchar(10), @FixedDeal);

IF @FixedLead = 0 AND @FixedDeal = 0
    PRINT 'Nothing to change — either already corrected, or renamed by hand.';

-- ── What it looks like after ─────────────────────────────────────────
PRINT '--- AFTER ---';

SELECT
    EventType,
    EventName = CASE EventType
                    WHEN 6 THEN 'LeadAssigned'
                    WHEN 7 THEN 'DealAssigned'
                    ELSE CONVERT(varchar(20), EventType)
                END,
    LanguageCode,
    TemplateName,
    Verdict = CASE
                  WHEN EventType = 6 AND TemplateName = N'lead_assigned_v1' THEN 'OK'
                  WHEN EventType = 7 AND TemplateName = N'deal_assigned_v1' THEN 'OK'
                  ELSE 'CHECK THIS ROW'
              END,
    VariableCount,
    VariableHints,
    IsActive,
    UpdatedBy
FROM dbo.WhatsAppTemplates
WHERE EventType IN (6, 7)
ORDER BY EventType, LanguageCode;

-- ── All eight, so you can see the whole set is sane ──────────────────
PRINT '--- ALL EIGHT ---';

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
    VariableCount,
    IsActive,
    UpdatedBy
FROM dbo.WhatsAppTemplates
ORDER BY EventType, LanguageCode;

-- ── Commit or roll back ──────────────────────────────────────────────
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
