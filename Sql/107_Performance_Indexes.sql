/* =====================================================================
   107_Performance_Indexes.sql

   Indexes for the lists, counts and dashboards the Round 107 performance
   tests found slow in a big workspace (50,000 leads, 20,000 deals ...).

   WHY. Every list and count starts with "this workspace's records that
   are not deleted" (TenantId + IsDeleted). Leads had NO index at all, and
   Deals had only one-column indexes, so SQL Server read the WHOLE table —
   every workspace's rows, every column, the long text ones included — to
   answer "how many deals does this workspace have". With 8 GB of RAM
   shared with everything else, those reads queued behind each other and
   hit the 30-second limit. SQL Server's own suggestions list (the
   perf-missing-indexes.txt report) asked for most of these.

   Each index starts with (TenantId, IsDeleted, ...) and carries the
   columns the lists show, so a list or a count reads a small, narrow
   index instead of the table.

   ─────────────────────────────────────────────────────────────────────
   RUN IT ON BOTH DATABASES — your development database AND
   MerkaiTrial_E2E. Check the database dropdown in the toolbar first.
   Later: production too, at a quiet time (creating an index briefly
   locks the table; on these sizes it takes seconds).
   ─────────────────────────────────────────────────────────────────────

   SAFE TO RE-RUN: each index is created only when it is missing.
   Nothing else changes; no data is touched.
   ===================================================================== */

SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.Leads', N'U') IS NULL OR OBJECT_ID(N'dbo.Deals', N'U') IS NULL
BEGIN
    RAISERROR('dbo.Leads / dbo.Deals are missing — is this the Merkai database?', 16, 1);
    RETURN;
END;

PRINT 'Database: ' + DB_NAME();

-- ── Leads ────────────────────────────────────────────────────────────
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Leads') AND name = N'IX_Leads_Tenant_Created')
BEGIN
    PRINT 'Creating IX_Leads_Tenant_Created (the leads list, newest first, and its search)...';
    CREATE NONCLUSTERED INDEX IX_Leads_Tenant_Created
        ON dbo.Leads (TenantId, IsDeleted, CreatedAtUtc DESC)
        INCLUDE (OwnerUserId, Status, IsConverted, Score, EstimatedValue, Currency,
                 FullName, Email, Phone, CompanyName, DealId, ChannelId, SourceId, Channel, Source);
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Leads') AND name = N'IX_Leads_Tenant_Owner')
BEGIN
    PRINT 'Creating IX_Leads_Tenant_Owner (a rep''s own leads, lead statistics)...';
    CREATE NONCLUSTERED INDEX IX_Leads_Tenant_Owner
        ON dbo.Leads (TenantId, OwnerUserId, IsDeleted)
        INCLUDE (Status, IsConverted, CreatedAtUtc);
END;

-- ── Deals ────────────────────────────────────────────────────────────
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Deals') AND name = N'IX_Deals_Tenant_Created')
BEGIN
    PRINT 'Creating IX_Deals_Tenant_Created (pipeline board and table, dashboard numbers)...';
    CREATE NONCLUSTERED INDEX IX_Deals_Tenant_Created
        ON dbo.Deals (TenantId, IsDeleted, CreatedAtUtc DESC)
        INCLUDE (Stage, ContactId, CompanyId, OwnerUserId, Title, ExpectedValue, Probability,
                 Currency, ExpectedCloseDateUtc, ActualCloseDateUtc);
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Deals') AND name = N'IX_Deals_Tenant_Owner')
BEGIN
    PRINT 'Creating IX_Deals_Tenant_Owner (a rep''s own deals)...';
    CREATE NONCLUSTERED INDEX IX_Deals_Tenant_Owner
        ON dbo.Deals (TenantId, OwnerUserId, IsDeleted)
        INCLUDE (Stage, ContactId, CompanyId, ExpectedValue, CreatedAtUtc, ExpectedCloseDateUtc, ActualCloseDateUtc);
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Deals') AND name = N'IX_Deals_Tenant_Company')
BEGIN
    PRINT 'Creating IX_Deals_Tenant_Company (a company''s deals)...';
    CREATE NONCLUSTERED INDEX IX_Deals_Tenant_Company
        ON dbo.Deals (TenantId, CompanyId, IsDeleted);
END;

-- ── Contacts and companies ───────────────────────────────────────────
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Contacts') AND name = N'IX_Contacts_Tenant_List')
BEGIN
    PRINT 'Creating IX_Contacts_Tenant_List (the contacts list and its search)...';
    CREATE NONCLUSTERED INDEX IX_Contacts_Tenant_List
        ON dbo.Contacts (TenantId, IsDeleted)
        INCLUDE (CompanyId, FirstName, LastName, Email, Phone, Mobile, JobTitle, IsPrimary, CreatedAtUtc);
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Companies') AND name = N'IX_Companies_Tenant_List')
BEGIN
    PRINT 'Creating IX_Companies_Tenant_List (the companies list)...';
    CREATE NONCLUSTERED INDEX IX_Companies_Tenant_List
        ON dbo.Companies (TenantId, IsDeleted)
        INCLUDE (Name, Country, Vertical, CreatedAtUtc);
END;

-- ── Quotes and invoices ──────────────────────────────────────────────
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Quotes') AND name = N'IX_Quotes_Tenant_Issued')
BEGIN
    PRINT 'Creating IX_Quotes_Tenant_Issued (the quotes list, statistics, recent quotes)...';
    CREATE NONCLUSTERED INDEX IX_Quotes_Tenant_Issued
        ON dbo.Quotes (TenantId, IsDeleted, IssueDateUtc DESC)
        INCLUDE (DealId, Number, Status, GrandTotal, Currency, ExpiresAtUtc, CreatedAtUtc);
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Invoices') AND name = N'IX_Invoices_Tenant_Issued')
BEGIN
    PRINT 'Creating IX_Invoices_Tenant_Issued (the invoices list and statistics)...';
    CREATE NONCLUSTERED INDEX IX_Invoices_Tenant_Issued
        ON dbo.Invoices (TenantId, IsDeleted, IssueDateUtc DESC)
        INCLUDE (DealId, QuoteId, Number, Status, Subtotal, TaxTotal, DiscountTotal, Balance,
                 DueDateUtc, Currency, CreatedAtUtc);
END;

-- ── Check ────────────────────────────────────────────────────────────
SELECT OBJECT_NAME(i.object_id) AS [Table], i.name AS [Index],
       CASE WHEN i.name IS NOT NULL THEN N'OK' END AS [Status]
FROM sys.indexes i
WHERE i.name IN (N'IX_Leads_Tenant_Created', N'IX_Leads_Tenant_Owner',
                 N'IX_Deals_Tenant_Created', N'IX_Deals_Tenant_Owner', N'IX_Deals_Tenant_Company',
                 N'IX_Contacts_Tenant_List', N'IX_Companies_Tenant_List',
                 N'IX_Quotes_Tenant_Issued', N'IX_Invoices_Tenant_Issued')
ORDER BY [Table], [Index];

PRINT 'Done. The grid should list 9 indexes, each OK.';
