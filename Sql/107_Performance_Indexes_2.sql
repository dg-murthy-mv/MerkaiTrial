-- =====================================================================
-- 107_Performance_Indexes_2.sql   (Round 107 — update 3)
--
-- Run on BOTH MerkaiTrial_E2E and your development database.
-- Safe to run more than once.
--
-- Two of the 107 indexes are rebuilt with a better shape:
--
--   IX_Contacts_Tenant_List   now kept IN NAME ORDER (first name, last
--                             name). The contacts list's normal order is by
--                             name, so SQL Server reads the page straight
--                             from the index — no sorting, and no block of
--                             memory to wait for. A search walks the same
--                             order and stops once the page is full.
--
--   IX_Companies_Tenant_List  now kept in name order too, and carries the
--                             tax id (the companies search looks at it;
--                             SQL Server asked for it 514 times last run).
-- =====================================================================
SET NOCOUNT ON;

-- ── Contacts ──────────────────────────────────────────────────────────
IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Contacts') AND name = N'IX_Contacts_Tenant_List')
   AND NOT EXISTS (SELECT 1 FROM sys.index_columns ic
                   JOIN sys.indexes i   ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                   JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
                   WHERE i.object_id = OBJECT_ID(N'dbo.Contacts') AND i.name = N'IX_Contacts_Tenant_List'
                     AND col.name = N'FirstName' AND ic.is_included_column = 0)
BEGIN
    PRINT 'Rebuilding IX_Contacts_Tenant_List in name order...';
    CREATE NONCLUSTERED INDEX IX_Contacts_Tenant_List
        ON dbo.Contacts (TenantId, IsDeleted, FirstName, LastName)
        INCLUDE (CompanyId, Email, Phone, Mobile, JobTitle, IsPrimary, CreatedAtUtc)
        WITH (DROP_EXISTING = ON);
END
ELSE IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Contacts') AND name = N'IX_Contacts_Tenant_List')
BEGIN
    PRINT 'Creating IX_Contacts_Tenant_List in name order...';
    CREATE NONCLUSTERED INDEX IX_Contacts_Tenant_List
        ON dbo.Contacts (TenantId, IsDeleted, FirstName, LastName)
        INCLUDE (CompanyId, Email, Phone, Mobile, JobTitle, IsPrimary, CreatedAtUtc);
END;

-- ── Companies ─────────────────────────────────────────────────────────
IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Companies') AND name = N'IX_Companies_Tenant_List')
   AND NOT EXISTS (SELECT 1 FROM sys.index_columns ic
                   JOIN sys.indexes i   ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                   JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
                   WHERE i.object_id = OBJECT_ID(N'dbo.Companies') AND i.name = N'IX_Companies_Tenant_List'
                     AND col.name = N'TaxId')
BEGIN
    PRINT 'Rebuilding IX_Companies_Tenant_List in name order, with the tax id...';
    CREATE NONCLUSTERED INDEX IX_Companies_Tenant_List
        ON dbo.Companies (TenantId, IsDeleted, Name)
        INCLUDE (Country, Vertical, TaxId, CreatedAtUtc)
        WITH (DROP_EXISTING = ON);
END
ELSE IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Companies') AND name = N'IX_Companies_Tenant_List')
BEGIN
    PRINT 'Creating IX_Companies_Tenant_List in name order, with the tax id...';
    CREATE NONCLUSTERED INDEX IX_Companies_Tenant_List
        ON dbo.Companies (TenantId, IsDeleted, Name)
        INCLUDE (Country, Vertical, TaxId, CreatedAtUtc);
END;

UPDATE STATISTICS dbo.Contacts;
UPDATE STATISTICS dbo.Companies;
UPDATE STATISTICS dbo.Deals;

-- ── Check ─────────────────────────────────────────────────────────────
SELECT t.name AS [Table], i.name AS [Index],
       STUFF((SELECT ', ' + c.name FROM sys.index_columns ic
              JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
              WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
              ORDER BY ic.key_ordinal FOR XML PATH('')), 1, 2, '') AS [In order of],
       CASE WHEN EXISTS (SELECT 1 FROM sys.index_columns ic
                         JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                         WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                           AND c.name IN (N'FirstName', N'TaxId'))
            THEN 'OK' ELSE 'MISSING' END AS [Status]
FROM sys.indexes i
JOIN sys.tables t ON t.object_id = i.object_id
WHERE i.name IN (N'IX_Contacts_Tenant_List', N'IX_Companies_Tenant_List')
ORDER BY [Table];

PRINT 'Done. The grid should list 2 indexes, each OK.';
