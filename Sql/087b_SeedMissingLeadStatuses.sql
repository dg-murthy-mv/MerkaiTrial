/* =====================================================================
   087b_SeedMissingLeadStatuses.sql — lead statuses for workspaces that
                                       were created without any

   THE BUG (found by the end-to-end tests, round 087)
     Admin → Tenants → Create (TenantProvisioningService) seeded roles,
     tax rates, lead sources, channels and pipeline stages — but NOT lead
     statuses. A workspace created that way cannot add a single lead:

         "This workspace has no lead statuses set up."

     Round 087b fixes provisioning. THIS script fixes the workspaces that
     were already created with the bug.

   WHAT IT DOES
     For every live tenant that has NO rows in dbo.LeadStatusDefinitions,
     adds the five standard statuses (the same list as
     DefaultLeadStatuses in the code):

         New          Open          starting status   score 0
         Working      Open                            score 5
         Qualified    Qualified                       score 15
         Unqualified  Disqualified                    score 0
         Converted    Converted     system (set by conversion only), score 15

     A tenant that already has ANY status is left completely alone, even
     if its list is different — that is the tenant's own setup.

   WHERE TO RUN IT
     • MerkaiTrial            (your development database)
     • MerkaiTrial_E2E        (the test copy)
     • the live database, when this round is deployed

   HOW TO RUN
     1. Run as-is. @Commit = 0 — everything is ROLLED BACK. The grid lists
        the workspaces that WOULD get statuses.
     2. Change @Commit to 1 and run again.

   SAFE TO RE-RUN. The second time it finds nothing to do.
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit BIT = 0;      -- flip to 1 to commit

-- LeadStatusCategory is stored as a number (Open 0, Qualified 1,
-- Disqualified 2, Converted 3) — EF Core's default for an enum. Checked
-- rather than assumed: if this database stores it as text, stop.
IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
           WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'LeadStatusDefinitions'
             AND COLUMN_NAME = 'Category'
             AND DATA_TYPE NOT IN ('int', 'smallint', 'tinyint', 'bigint'))
BEGIN
    RAISERROR(N'LeadStatusDefinitions.Category is not a number column on this database. Nothing was changed — send me the column type.', 16, 1);
    RETURN;
END

DECLARE @Seed TABLE (
    [Key] NVARCHAR(50), Name NVARCHAR(100), SortOrder INT, Score INT,
    Category INT, IsDefault BIT, IsSystem BIT);

INSERT INTO @Seed VALUES
    (N'New',         N'New',         1,  0, 0, 1, 0),
    (N'Working',     N'Working',     2,  5, 0, 0, 0),
    (N'Qualified',   N'Qualified',   3, 15, 1, 0, 0),
    (N'Unqualified', N'Unqualified', 4,  0, 2, 0, 0),
    (N'Converted',   N'Converted',   5, 15, 3, 0, 1);

BEGIN TRAN;

DECLARE @Missing TABLE (TenantId UNIQUEIDENTIFIER, Name NVARCHAR(200));

INSERT INTO @Missing (TenantId, Name)
SELECT t.Id, t.Name
FROM dbo.Tenants t
WHERE t.IsDeleted = 0
  AND NOT EXISTS (SELECT 1 FROM dbo.LeadStatusDefinitions s WHERE s.TenantId = t.Id);

INSERT INTO dbo.LeadStatusDefinitions
    (Id, TenantId, [Key], Name, SortOrder, Score, Category, IsActive, IsDefault, IsSystem,
     Color, Description, CreatedAtUtc, CreatedBy)
SELECT NEWID(), m.TenantId, s.[Key], s.Name, s.SortOrder, s.Score, s.Category, 1,
       s.IsDefault, s.IsSystem, NULL, NULL, SYSUTCDATETIME(), N'087b-seed'
FROM @Missing m CROSS JOIN @Seed s;

-- What happened (or would have).
SELECT m.Name AS Workspace,
       m.TenantId,
       (SELECT COUNT(*) FROM dbo.LeadStatusDefinitions s WHERE s.TenantId = m.TenantId) AS StatusesNow,
       CASE WHEN @Commit = 1 THEN N'Seeded' ELSE N'Would be seeded (dry run)' END AS Result
FROM @Missing m
ORDER BY m.Name;

IF NOT EXISTS (SELECT 1 FROM @Missing)
    PRINT N'Every workspace already has lead statuses. Nothing to do.';

IF @Commit = 1
BEGIN
    COMMIT;
    PRINT N'Committed.';
END
ELSE
BEGIN
    ROLLBACK;
    PRINT N'Dry run — rolled back. Change @Commit to 1 to apply.';
END
