/* =====================================================================
   088c_ReactivateApprovalRules.sql — switch back ON the quote approval
                                      rules that were saved as OFF

   THE BUG (found by the end-to-end tests, round 088)
     On Settings → Quote Approvals, the "Rule is on" switch posted a hidden
     "false" BEFORE its checkbox. The server reads the first value, so
     EVERY rule ever added or edited on that page was saved switched OFF —
     and an off rule is ignored. A rep's 15%-discount quote went straight
     to the customer although a "discount over 10%" rule existed.

     Round 088c fixes the form (_RuleEditor.cshtml). THIS script deals with
     the rules already saved with the bug.

   WHY THIS NEEDS YOUR DECISION
     Because of the bug, nobody could ever save a rule as ON through the
     page, so an "off" rule today almost always means "the page switched it
     off", not "an admin chose off". But the script cannot tell the two
     apart. So:

       1. Run it as-is (@Commit = 0). Nothing is saved. The grid lists every
          rule that is currently OFF, per workspace.
       2. If they should all be on, set @Commit = 1 and run again.
       3. To do just one workspace, put its exact name in @OnlyWorkspace.

     Afterwards the admin can still switch any rule off on the page — that
     now saves correctly.

   WHERE TO RUN IT
     • MerkaiTrial            (your development database)
     • the live database, when this round is deployed
     (Not needed on MerkaiTrial_E2E: every test run makes new workspaces.)

   SAFE TO RE-RUN. The second time it finds nothing to do.
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit        bit           = 0;      -- 0 = preview only (rolled back), 1 = save
DECLARE @OnlyWorkspace nvarchar(200) = NULL;   -- e.g. N'Sathorn Interiors'; NULL = every workspace

BEGIN TRANSACTION;

DECLARE @Fix TABLE (RuleId uniqueidentifier NOT NULL PRIMARY KEY);

INSERT INTO @Fix (RuleId)
SELECT r.Id
FROM dbo.ApprovalRules r
JOIN dbo.Tenants t ON t.Id = r.TenantId
WHERE r.IsActive = 0
  AND (@OnlyWorkspace IS NULL OR t.Name = @OnlyWorkspace);

SELECT t.Name AS Workspace, r.Name AS [Rule], r.DiscountOverPercent AS [Discount over %],
       r.TotalOverAmount AS [Total over], r.SortOrder AS [Order]
FROM @Fix f
JOIN dbo.ApprovalRules r ON r.Id = f.RuleId
JOIN dbo.Tenants t ON t.Id = r.TenantId
ORDER BY t.Name, r.SortOrder, r.Name;

UPDATE r
   SET r.IsActive = 1
FROM dbo.ApprovalRules r
JOIN @Fix f ON f.RuleId = r.Id
WHERE r.IsActive = 0;

DECLARE @Count int = (SELECT COUNT(*) FROM @Fix);

IF @Commit = 1
BEGIN
    COMMIT TRANSACTION;
    PRINT CONCAT('Saved: ', @Count, ' rule(s) switched on.');
END
ELSE
BEGIN
    ROLLBACK TRANSACTION;
    PRINT CONCAT('Preview only — nothing saved. ', @Count, ' rule(s) are off and would be switched on. Set @Commit = 1 to save.');
END
