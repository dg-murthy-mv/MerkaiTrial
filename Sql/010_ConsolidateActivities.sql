-- =====================================================================
-- 010_ConsolidateActivities.sql
--
-- Copies LeadActivities, DealActivities, LeadReminders and DealReminders
-- into Activities. The legacy tables are NOT modified or dropped.
--
-- SAFE TO RE-RUN: every row keeps its original Id and is inserted only
-- if that Id is not already in Activities. Run it again at deploy time
-- to pick up anything written to the legacy tables in the meantime.
--
-- DO NOT COMMIT until the code that stops writing to the legacy tables
-- ships in the same release. Committing early makes lead reminders show
-- twice on the Lead page (Activities tab AND Reminders tab), and new
-- reminders would keep going to LeadReminders only.
--
-- @Commit = 0 is a dry run. The result grids look identical either way:
-- check the Messages tab for === COMMITTED === or === ROLLED BACK ===.
--
-- Expected dry-run output on the dev DB (10-Sep-2026 counts):
--   Step 1:  9   Step 2:  7   Step 3:  1   Step 4: 12   Step 5:  3
--   Activities before 9, after 32. Preflight grid empty.
-- =====================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit BIT = 0;
DECLARE @n INT;

BEGIN TRAN;

-- ── Preflight: legacy rows whose parent is missing or in another tenant.
--    Expect ZERO rows. If any appear, stop and look before committing.
SELECT 'LeadActivities' AS Src, x.Id, x.TenantId, p.TenantId AS ParentTenantId
FROM LeadActivities x LEFT JOIN Leads p ON p.Id = x.LeadId
WHERE p.Id IS NULL OR p.TenantId <> x.TenantId
UNION ALL
SELECT 'LeadReminders', x.Id, x.TenantId, p.TenantId
FROM LeadReminders x LEFT JOIN Leads p ON p.Id = x.LeadId
WHERE p.Id IS NULL OR p.TenantId <> x.TenantId
UNION ALL
SELECT 'DealActivities', x.Id, x.TenantId, p.TenantId
FROM DealActivities x LEFT JOIN Deals p ON p.Id = x.DealId
WHERE p.Id IS NULL OR p.TenantId <> x.TenantId
UNION ALL
SELECT 'DealReminders', x.Id, x.TenantId, p.TenantId
FROM DealReminders x LEFT JOIN Deals p ON p.Id = x.DealId
WHERE p.Id IS NULL OR p.TenantId <> x.TenantId;

SELECT 'Activities before' AS Stage, COUNT(*) AS N FROM Activities;

-- ── Step 1: a LOGGED activity is always complete. Only tasks can be open.
--    Fixes the 9 rows the Lead page has written since March.
UPDATE Activities
SET    IsCompleted    = 1,
       CompletedAtUtc = COALESCE(CompletedAtUtc, ActivityDate)
WHERE  IsTask = 0 AND IsCompleted = 0;
SET @n = @@ROWCOUNT; PRINT CONCAT('Step 1  unified logs marked complete : ', @n);

-- ── Step 2: LeadActivities -> completed logs
INSERT INTO Activities
    (Id, TenantId, EntityType, EntityId, ActivityType, Subject, Description,
     Duration, ActivityDate, IsTask, DueDate, IsCompleted, CompletedAtUtc,
     Outcome, AssignedToUserId, CreatedAtUtc, CreatedBy, UpdatedAtUtc,
     UpdatedBy, IsDeleted, DeletedAtUtc)
SELECT x.Id, x.TenantId, 'Lead', x.LeadId, x.ActivityType,
       LEFT(COALESCE(NULLIF(LTRIM(RTRIM(x.Subject)), ''), x.ActivityType), 200),
       x.Description, x.Duration, x.ActivityDate,
       0, NULL, 1, x.ActivityDate,
       NULL, NULL, x.CreatedAtUtc, x.CreatedBy, x.UpdatedAtUtc,
       x.UpdatedBy, x.IsDeleted, NULL
FROM LeadActivities x
WHERE NOT EXISTS (SELECT 1 FROM Activities a WHERE a.Id = x.Id);
SET @n = @@ROWCOUNT; PRINT CONCAT('Step 2  LeadActivities copied        : ', @n);

-- ── Step 3: DealActivities -> completed logs (this table has Outcome,
--    but no UpdatedAtUtc/UpdatedBy)
INSERT INTO Activities
    (Id, TenantId, EntityType, EntityId, ActivityType, Subject, Description,
     Duration, ActivityDate, IsTask, DueDate, IsCompleted, CompletedAtUtc,
     Outcome, AssignedToUserId, CreatedAtUtc, CreatedBy, UpdatedAtUtc,
     UpdatedBy, IsDeleted, DeletedAtUtc)
SELECT x.Id, x.TenantId, 'Deal', x.DealId, x.ActivityType,
       LEFT(COALESCE(NULLIF(LTRIM(RTRIM(x.Subject)), ''), x.ActivityType), 200),
       x.Description, x.Duration, x.ActivityDate,
       0, NULL, 1, x.ActivityDate,
       x.Outcome, NULL, x.CreatedAtUtc, x.CreatedBy, NULL,
       NULL, x.IsDeleted, NULL
FROM DealActivities x
WHERE NOT EXISTS (SELECT 1 FROM Activities a WHERE a.Id = x.Id);
SET @n = @@ROWCOUNT; PRINT CONCAT('Step 3  DealActivities copied        : ', @n);

-- ── Step 4: LeadReminders -> tasks, assigned to the lead's owner
--    (reminders have no assignee column; without this they would sit in
--    nobody's "My tasks")
INSERT INTO Activities
    (Id, TenantId, EntityType, EntityId, ActivityType, Subject, Description,
     Duration, ActivityDate, IsTask, DueDate, IsCompleted, CompletedAtUtc,
     Outcome, AssignedToUserId, CreatedAtUtc, CreatedBy, UpdatedAtUtc,
     UpdatedBy, IsDeleted, DeletedAtUtc)
SELECT x.Id, x.TenantId, 'Lead', x.LeadId, 'Task',
       LEFT(x.Title, 200), x.Description, NULL, x.CreatedAtUtc,
       1, x.ReminderDate, x.IsCompleted, x.CompletedAtUtc,
       NULL, p.OwnerUserId, x.CreatedAtUtc, x.CreatedBy, x.UpdatedAtUtc,
       x.UpdatedBy, x.IsDeleted, NULL
FROM LeadReminders x
LEFT JOIN Leads p ON p.Id = x.LeadId
WHERE NOT EXISTS (SELECT 1 FROM Activities a WHERE a.Id = x.Id);
SET @n = @@ROWCOUNT; PRINT CONCAT('Step 4  LeadReminders copied         : ', @n);

-- ── Step 5: DealReminders -> tasks, assigned to the deal's owner
INSERT INTO Activities
    (Id, TenantId, EntityType, EntityId, ActivityType, Subject, Description,
     Duration, ActivityDate, IsTask, DueDate, IsCompleted, CompletedAtUtc,
     Outcome, AssignedToUserId, CreatedAtUtc, CreatedBy, UpdatedAtUtc,
     UpdatedBy, IsDeleted, DeletedAtUtc)
SELECT x.Id, x.TenantId, 'Deal', x.DealId, 'Task',
       LEFT(x.Title, 200), x.Description, NULL, x.CreatedAtUtc,
       1, x.ReminderDate, x.IsCompleted, x.CompletedAtUtc,
       NULL, p.OwnerUserId, x.CreatedAtUtc, x.CreatedBy, NULL,
       NULL, x.IsDeleted, NULL
FROM DealReminders x
LEFT JOIN Deals p ON p.Id = x.DealId
WHERE NOT EXISTS (SELECT 1 FROM Activities a WHERE a.Id = x.Id);
SET @n = @@ROWCOUNT; PRINT CONCAT('Step 5  DealReminders copied         : ', @n);

-- ── Verify ────────────────────────────────────────────────────────────
SELECT 'Activities after' AS Stage, COUNT(*) AS N FROM Activities;

SELECT EntityType, IsTask, IsCompleted, IsDeleted, COUNT(*) AS N
FROM Activities
GROUP BY EntityType, IsTask, IsCompleted, IsDeleted
ORDER BY EntityType, IsTask, IsCompleted, IsDeleted;

-- Open tasks nobody owns (lead/deal had no owner). Expect few or none.
SELECT Id, EntityType, EntityId, Subject, DueDate
FROM Activities
WHERE IsTask = 1 AND IsCompleted = 0 AND IsDeleted = 0
  AND (AssignedToUserId IS NULL OR AssignedToUserId = '');

-- Any log that is still open would break the "open = task" rule.
SELECT COUNT(*) AS OpenLogs_ShouldBeZero
FROM Activities WHERE IsTask = 0 AND IsCompleted = 0;

IF @Commit = 1
BEGIN
    COMMIT TRAN;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRAN;
    PRINT '=== ROLLED BACK (dry run) - nothing changed ===';
END
