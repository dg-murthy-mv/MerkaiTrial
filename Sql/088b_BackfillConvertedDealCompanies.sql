/* =====================================================================
   088b_BackfillConvertedDealCompanies.sql — give converted deals the
                                             company they should have had

   THE BUG (found by the end-to-end tests, round 088)
     Converting a lead found or created the company and linked it to the
     CONTACT, but saved the deal itself with CompanyId = NULL. Every deal
     screen reads the company through Deals.CompanyId, so a converted deal
     showed no company in its header, its overview, the deals list or a
     search by company name.

     Round 088b fixes ConvertLeadToDealHandler. THIS script fixes the deals
     that were already converted with the bug.

   WHAT IT DOES
     For every live deal with NO company that came from a lead, it fills
     in Deals.CompanyId with (in this order):
       1. the company the conversion recorded on the lead
          (Leads.ConvertedToCompanyId), or
       2. the company the deal's contact belongs to (Contacts.CompanyId).
     Only a company in the SAME workspace that is not deleted is used.
     A deal that already has a company is never touched.

   WHERE TO RUN IT
     • MerkaiTrial            (your development database)
     • MerkaiTrial_E2E        (the test copy, optional — every test run
                               makes new workspaces anyway)
     • the live database, when this round is deployed

   HOW TO RUN
     1. Run as-is. @Commit = 0 — everything is ROLLED BACK. The grid lists
        the deals that WOULD be filled in, and with which company.
     2. Change @Commit to 1 and run again.

   SAFE TO RE-RUN. The second time it finds nothing to do.
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit bit = 0;      -- 0 = preview only (rolled back), 1 = save

BEGIN TRANSACTION;

DECLARE @Fix TABLE
(
    DealId      uniqueidentifier NOT NULL PRIMARY KEY,
    TenantId    uniqueidentifier NOT NULL,
    DealTitle   nvarchar(400)    NULL,
    CompanyId   uniqueidentifier NOT NULL,
    CompanyName nvarchar(400)    NULL,
    Source      varchar(20)      NOT NULL
);

INSERT INTO @Fix (DealId, TenantId, DealTitle, CompanyId, CompanyName, Source)
SELECT d.Id, d.TenantId, d.Title, pick.CompanyId, co.Name, pick.Source
FROM dbo.Deals d
CROSS APPLY
(
    SELECT TOP 1 x.CompanyId, x.Source
    FROM
    (
        SELECT l.ConvertedToCompanyId AS CompanyId, 'lead' AS Source, 1 AS Pref
        FROM dbo.Leads l
        WHERE l.Id = d.LeadId AND l.ConvertedToCompanyId IS NOT NULL

        UNION ALL

        SELECT c.CompanyId, 'contact', 2
        FROM dbo.Contacts c
        WHERE c.Id = d.ContactId AND c.CompanyId IS NOT NULL AND c.IsDeleted = 0
    ) x
    ORDER BY x.Pref
) pick
JOIN dbo.Companies co
  ON co.Id = pick.CompanyId
 AND co.TenantId = d.TenantId          -- never borrow another workspace's company
 AND co.IsDeleted = 0
WHERE d.CompanyId IS NULL
  AND d.LeadId IS NOT NULL
  AND d.IsDeleted = 0;

UPDATE d
   SET d.CompanyId = f.CompanyId
FROM dbo.Deals d
JOIN @Fix f ON f.DealId = d.Id
WHERE d.CompanyId IS NULL;             -- re-checked: never overwrite

SELECT t.Name AS Workspace, f.DealTitle AS Deal, f.CompanyName AS Company, f.Source AS [Taken from]
FROM @Fix f
LEFT JOIN dbo.Tenants t ON t.Id = f.TenantId
ORDER BY t.Name, f.DealTitle;

DECLARE @Count int = (SELECT COUNT(*) FROM @Fix);

IF @Commit = 1
BEGIN
    COMMIT TRANSACTION;
    PRINT CONCAT('Saved: ', @Count, ' deal(s) now have their company.');
END
ELSE
BEGIN
    ROLLBACK TRANSACTION;
    PRINT CONCAT('Preview only — nothing saved. ', @Count, ' deal(s) would get their company. Set @Commit = 1 to save.');
END
