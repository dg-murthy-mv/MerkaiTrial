-- =====================================================================
-- 071_QuoteMilestones.sql
--
-- WHAT THIS DOES
--   1. Creates dbo.QuoteMilestones — the BILLING SCHEDULE on a quote.
--      "30% on signing, 40% on delivery, 30% on completion".
--   2. Adds five nullable columns to dbo.Invoices that say WHICH
--      milestone an invoice is: MilestoneId, MilestoneName,
--      MilestoneSequence, MilestoneCount, MilestonePercent.
--   3. Adds one unique index that replaces an application-level guard:
--      a quote can have at most ONE live invoice per milestone.
--
--   NOTHING IS BACKFILLED. The new table is empty afterwards and every
--   existing invoice keeps MilestoneId = NULL. A workspace that never
--   opens the billing schedule screen behaves exactly as it does today:
--   one quote, one invoice, for the whole amount.
--
-- ─────────────────────────────────────────────────────────────────────
-- WHY THIS ROUND EXISTS
--
--   Until now a quote could be invoiced ONCE, for all of it. That is
--   fine for a ₹12,000 sale and useless for a ₹4,00,000 project, which
--   is billed in stages — and staged billing is how most project work
--   is sold in all four markets this product serves.
--
--   The alternative design was to invoice PART OF EACH LINE (quantity
--   2 of 5 delivered). That was rejected: it suits goods, and the SMEs
--   buying this sell projects and retainers. Milestones also keep every
--   invoice a complete copy of the quote — so every line keeps its own
--   tax rate and its own HSN / SAC code, which after 067 is the whole
--   point of a tax invoice.
--
-- THE DEFAULT IS ONE MILESTONE, 100%
--   A quote with no rows in this table invoices exactly as it does
--   today. There is no migration to run against existing quotes and no
--   behaviour to opt out of.
--
-- A MILESTONE IS A PERCENTAGE **OR** A FIXED AMOUNT
--   SharePercent 30.0000  → 30% of the quote's grand total.
--   FixedAmount  50000.00 → ₹50,000 of it, in the quote's currency.
--
--   SharePercent and not Percent: PERCENT is a reserved T-SQL keyword
--   ("SELECT TOP 10 PERCENT"), so a column of that name would have to be
--   bracketed in every query ever written against it. The C# property is
--   still called Percent — see the note on the column below.
--
--   Exactly one of the two is set; the CHECK below enforces it. Mixing
--   them in one schedule is allowed and normal: "₹50,000 on signing,
--   the rest on completion" is two rows, one of each.
--
-- THE ROUNDING RULE, WRITTEN DOWN ONCE
--   Three milestones of 33.33% do not add up to the quote total. So the
--   LAST milestone is never computed from its own percentage — it is
--   computed as "everything not already allocated", line by line. Three
--   invoices against a ₹1,00,000 quote come to exactly ₹1,00,000, and
--   the remainder lands on the final invoice where somebody is looking
--   at the balance anyway. The schedule screen states this, and shows
--   the real computed amount for every row rather than the percentage,
--   so nobody has to discover a ₹0.02 difference on the last invoice.
--
-- WHY THE INVOICE SNAPSHOTS THE MILESTONE NAME AND POSITION
--   Same reason QuoteItem.TaxCode is a snapshot (067) and a bundle's
--   contents are not (070): a schedule is a DEFINITION and an invoice
--   is a DOCUMENT. Renaming "On delivery" to "On handover" must not
--   rewrite an invoice that is already with the customer and already in
--   somebody's GST return. MilestoneId stays as the live link for the
--   "which milestones are still to bill" rollup; the three snapshot
--   columns are what the invoice and its PDF print.
-- ─────────────────────────────────────────────────────────────────────
--
-- IS IT SAFE TO RE-RUN
--   Yes. Every CREATE, ALTER, CHECK and INDEX is guarded, and nothing
--   is inserted or updated. The ALTER in step 2 and the index in step 4
--   both reference columns created in this same script, so both go
--   through sp_executesql — the batch is parsed before the ALTER runs.
--
-- HOW TO RUN IT
--   Leave @Commit = 0 the first time, read the grids, then set it to 1.
-- =====================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit BIT = 0;      -- <<<<<< set to 1 when the grids look right

DECLARE @Touched TABLE (
    Seq        INT IDENTITY(1,1),
    Step       NVARCHAR(30),
    ObjectName SYSNAME NULL,
    Detail     NVARCHAR(400) NULL
);

DECLARE @sql NVARCHAR(MAX);

BEGIN TRAN;

-- ─────────────────────────────────────────────────────────────────────
-- 1. The schedule table
-- ─────────────────────────────────────────────────────────────────────
IF OBJECT_ID(N'dbo.QuoteMilestones', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.QuoteMilestones
    (
        Id            uniqueidentifier NOT NULL
            CONSTRAINT PK_QuoteMilestones PRIMARY KEY,

        -- NOT NULL. A billing schedule is never shared across
        -- workspaces, so this goes in FlowDbContext's STRICT filter
        -- group beside ProductPrices and ProductBundleItems, and NOT in
        -- the shared-or-tenant group ProductCategories uses.
        TenantId      uniqueidentifier NOT NULL,

        QuoteId       uniqueidentifier NOT NULL,

        -- The order the stages happen in. Hand-set, like
        -- ProductBundleItems.SortOrder: "signing, delivery, completion"
        -- is the order the work happens in and alphabetical would
        -- scramble it.
        SortOrder     int              NOT NULL
            CONSTRAINT DF_QuoteMilestones_SortOrder DEFAULT (0),

        -- What the customer sees on the quote and on the invoice.
        -- "Advance", "On delivery", "Final payment".
        Name          nvarchar(120)    NOT NULL,

        -- EXACTLY ONE of these two is set; CK_QuoteMilestones_OneBasis
        -- below is what makes that true rather than a convention.
        --
        -- ⚠ NAMED SharePercent, NOT Percent. PERCENT IS A RESERVED
        -- T-SQL KEYWORD (it is the one in "SELECT TOP 10 PERCENT"), so
        -- a column called Percent has to be written [Percent] in every
        -- query forever - including every ad-hoc SELECT typed into SSMS
        -- at six months' distance, which is where it would actually
        -- bite. The C# property is still called Percent; the single
        -- .HasColumnName("SharePercent") line in
        -- QuoteMilestoneConfiguration is what joins the two.
        --
        -- decimal(9,4) so 33.3333 survives. Percentages are stored as
        -- PERCENT (30.0000 = 30%), not as a fraction - the same
        -- convention Product.TaxRate uses and the opposite of
        -- QuoteItem.TaxRate. Three conventions in one database is one
        -- too many, but changing either of the existing two is a
        -- migration against live documents, so this one matches the
        -- column a human types into.
        SharePercent  decimal(9,4)     NULL,

        -- In the QUOTE's currency, always. A quote cannot change
        -- currency once it has items, so there is nothing to convert.
        FixedAmount   decimal(18,2)    NULL,

        -- Free text: "On signing", "On delivery of the staging site",
        -- "30 days after handover". Printed on the quote PDF under the
        -- milestone name. Not parsed, not used in any arithmetic.
        DueCondition  nvarchar(200)    NULL,

        -- Optional hard date, when there is one. Used as the invoice's
        -- suggested issue date, nothing more - no job raises invoices
        -- on a schedule, and one that did would need a conversation
        -- about who signs it off.
        DueDateUtc    datetime2(7)     NULL,

        CreatedAtUtc  datetime2(7)     NOT NULL
            CONSTRAINT DF_QuoteMilestones_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CreatedBy     nvarchar(200)    NULL,
        UpdatedAtUtc  datetime2(7)     NULL,
        UpdatedBy     nvarchar(200)    NULL,

        IsDeleted     bit              NOT NULL
            CONSTRAINT DF_QuoteMilestones_IsDeleted DEFAULT (0),

        -- One basis, never both, never neither. A row with both set
        -- would have two answers to "how much is this stage"; a row
        -- with neither would silently invoice nothing.
        CONSTRAINT CK_QuoteMilestones_OneBasis CHECK
        (
            (SharePercent IS NOT NULL AND FixedAmount IS NULL)
         OR (SharePercent IS NULL     AND FixedAmount IS NOT NULL)
        ),

        -- 0% is not a stage. Over 100% is not either, even on its own
        -- row - the schedule total is checked by the application, but a
        -- single row over 100 is wrong on its face.
        CONSTRAINT CK_QuoteMilestones_Percent CHECK
            (SharePercent IS NULL OR (SharePercent > 0 AND SharePercent <= 100)),

        CONSTRAINT CK_QuoteMilestones_FixedAmount CHECK
            (FixedAmount IS NULL OR FixedAmount > 0),

        CONSTRAINT CK_QuoteMilestones_Name CHECK (LEN(LTRIM(RTRIM(Name))) > 0),

        -- CASCADE, unlike ProductBundleItems. A schedule has no meaning
        -- without its quote - the same reasoning ProductPrices uses for
        -- its product. Quotes are soft-deleted in practice, so this
        -- fires only if somebody hard-deletes a row.
        CONSTRAINT FK_QuoteMilestones_Quotes_QuoteId
            FOREIGN KEY (QuoteId) REFERENCES dbo.Quotes (Id)
            ON DELETE CASCADE,

        -- NO ACTION here, because the CASCADE above already covers the
        -- real case and two cascade paths into one table is the error
        -- SQL Server refuses at create time.
        CONSTRAINT FK_QuoteMilestones_Tenants_TenantId
            FOREIGN KEY (TenantId) REFERENCES dbo.Tenants (Id)
            ON DELETE NO ACTION
    );

    INSERT INTO @Touched (Step, ObjectName, Detail)
        VALUES ('created table', 'QuoteMilestones', 'the billing schedule; empty after this run');
    PRINT 'dbo.QuoteMilestones -> created';
END
ELSE
    PRINT 'dbo.QuoteMilestones already exists - skipped';

-- ─────────────────────────────────────────────────────────────────────
-- 2. dbo.Invoices — which milestone is this?
--
--    All five nullable, all added in one ALTER so the table is rebuilt
--    once rather than five times. NULL on every existing row means
--    "this invoice is the whole quote", which is what every existing
--    invoice is.
-- ─────────────────────────────────────────────────────────────────────
IF COL_LENGTH(N'dbo.Invoices', N'MilestoneId') IS NULL
BEGIN
    ALTER TABLE dbo.Invoices ADD
        -- The live link. Kept so "which stages are still to bill" is a
        -- join and not a guess, and so the schedule screen can refuse
        -- to change a stage that has already been invoiced.
        MilestoneId       uniqueidentifier NULL,

        -- SNAPSHOTS. See the header: an issued invoice must keep saying
        -- what it said even after the schedule is renamed or re-cut.
        MilestoneName     nvarchar(120)    NULL,
        MilestoneSequence int              NULL,   -- 1-based: "2 of 3"
        MilestoneCount    int              NULL,   -- how many stages there were
        MilestonePercent  decimal(9,4)     NULL;   -- the share actually applied, as a percent

    INSERT INTO @Touched (Step, ObjectName, Detail)
        VALUES ('added columns', 'Invoices', 'MilestoneId + 4 snapshot columns, all NULL');
    PRINT 'dbo.Invoices -> milestone columns added';
END
ELSE
    PRINT 'dbo.Invoices.MilestoneId already exists - skipped';

-- ─────────────────────────────────────────────────────────────────────
-- 3. The foreign key, and the one CHECK worth having on it
--
--    sp_executesql because MilestoneId may have been created moments
--    ago in step 2 - the whole batch is parsed before any of it runs,
--    so a bare ALTER naming the column would fail to compile on a
--    first run.
--
--    NO ACTION, not CASCADE. Deleting a milestone must NOT delete the
--    invoice raised against it; the application refuses to remove a
--    milestone that has a live invoice, and if a row were ever force-
--    deleted behind its back, failing loudly beats silently destroying
--    a tax document.
-- ─────────────────────────────────────────────────────────────────────
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys
               WHERE name = N'FK_Invoices_QuoteMilestones_MilestoneId')
BEGIN
    SET @sql = N'
    ALTER TABLE dbo.Invoices WITH CHECK
        ADD CONSTRAINT FK_Invoices_QuoteMilestones_MilestoneId
            FOREIGN KEY (MilestoneId) REFERENCES dbo.QuoteMilestones (Id)
            ON DELETE NO ACTION;';
    EXEC sp_executesql @sql;
    INSERT INTO @Touched (Step, ObjectName, Detail)
        VALUES ('added FK', 'FK_Invoices_QuoteMilestones_MilestoneId', 'NO ACTION - never delete an invoice');
    PRINT 'FK_Invoices_QuoteMilestones_MilestoneId -> created';
END
ELSE
    PRINT 'FK_Invoices_QuoteMilestones_MilestoneId already exists - skipped';

-- A milestone belongs to a quote, so an invoice carrying a MilestoneId
-- must also carry the QuoteId. Catches the one wiring mistake that
-- would otherwise produce an orphan: a manual invoice stamped with a
-- milestone it has no quote for.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
               WHERE name = N'CK_Invoices_MilestoneNeedsQuote')
BEGIN
    SET @sql = N'
    ALTER TABLE dbo.Invoices WITH CHECK
        ADD CONSTRAINT CK_Invoices_MilestoneNeedsQuote
            CHECK (MilestoneId IS NULL OR QuoteId IS NOT NULL);';
    EXEC sp_executesql @sql;
    INSERT INTO @Touched (Step, ObjectName, Detail)
        VALUES ('added check', 'CK_Invoices_MilestoneNeedsQuote', 'a milestone invoice has a quote');
    PRINT 'CK_Invoices_MilestoneNeedsQuote -> created';
END
ELSE
    PRINT 'CK_Invoices_MilestoneNeedsQuote already exists - skipped';

-- ─────────────────────────────────────────────────────────────────────
-- 4. ONE LIVE INVOICE PER MILESTONE
--
--    This index is the real version of the guard the application has
--    carried since 018 ("an invoice already exists for this quote").
--    That guard is still there, because it produces a sentence a person
--    can read - but a guard that reads then writes loses a race, and
--    two people clicking Create at the same moment on the same
--    milestone would both win.
--
--    THE FILTER IS WHAT MAKES IT CORRECT, so each clause is spelled out:
--
--      IsDeleted = 0   - a deleted draft frees its milestone again.
--      Status <> 6     - 6 is InvoiceStatus.Cancelled, i.e. VOID. A
--                        voided invoice frees its milestone, which is
--                        exactly how a mistake on an issued invoice is
--                        corrected.
--      QuoteId IS NOT NULL
--                      - WITHOUT THIS CLAUSE THIS INDEX BREAKS MANUAL
--                        INVOICING. SQL Server treats NULLs as equal
--                        for uniqueness, so every manual invoice
--                        (QuoteId NULL, MilestoneId NULL) would collide
--                        with every other manual invoice on the same
--                        tenant, and the second one a workspace ever
--                        raised would fail to save.
--
--    With that filter in place the NULL-equals-NULL behaviour becomes
--    the feature: for a quote WITHOUT a schedule, MilestoneId is NULL
--    on every invoice, so the index allows exactly one live invoice per
--    quote - today's rule, now enforced by the database.
-- ─────────────────────────────────────────────────────────────────────
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'UX_Invoices_Quote_Milestone_Live'
                 AND object_id = OBJECT_ID(N'dbo.Invoices'))
BEGIN
    SET @sql = N'
    CREATE UNIQUE INDEX UX_Invoices_Quote_Milestone_Live
        ON dbo.Invoices (TenantId, QuoteId, MilestoneId)
        WHERE IsDeleted = 0 AND Status <> 6 AND QuoteId IS NOT NULL;';
    EXEC sp_executesql @sql;
    INSERT INTO @Touched (Step, ObjectName, Detail)
        VALUES ('created index', 'UX_Invoices_Quote_Milestone_Live', 'one live invoice per milestone');
    PRINT 'UX_Invoices_Quote_Milestone_Live -> created';
END
ELSE
    PRINT 'UX_Invoices_Quote_Milestone_Live already exists - skipped';

-- ─────────────────────────────────────────────────────────────────────
-- 5. The two reads this feature does
-- ─────────────────────────────────────────────────────────────────────

-- "The schedule for this quote, in order." Every screen in the round
-- does this one.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_QuoteMilestones_Tenant_Quote_Order'
                 AND object_id = OBJECT_ID(N'dbo.QuoteMilestones'))
BEGIN
    SET @sql = N'
    CREATE INDEX IX_QuoteMilestones_Tenant_Quote_Order
        ON dbo.QuoteMilestones (TenantId, QuoteId, SortOrder)
        INCLUDE (Name, SharePercent, FixedAmount, DueCondition, DueDateUtc)
        WHERE IsDeleted = 0;';
    EXEC sp_executesql @sql;
    INSERT INTO @Touched (Step, ObjectName, Detail)
        VALUES ('created index', 'IX_QuoteMilestones_Tenant_Quote_Order', 'covers the schedule read');
    PRINT 'IX_QuoteMilestones_Tenant_Quote_Order -> created';
END
ELSE
    PRINT 'IX_QuoteMilestones_Tenant_Quote_Order already exists - skipped';

-- UNIQUE among live rows: one row per position. Two rows claiming
-- position 2 would print in an order nothing decides.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'UX_QuoteMilestones_Quote_SortOrder'
                 AND object_id = OBJECT_ID(N'dbo.QuoteMilestones'))
BEGIN
    SET @sql = N'
    CREATE UNIQUE INDEX UX_QuoteMilestones_Quote_SortOrder
        ON dbo.QuoteMilestones (TenantId, QuoteId, SortOrder)
        WHERE IsDeleted = 0;';
    EXEC sp_executesql @sql;
    INSERT INTO @Touched (Step, ObjectName, Detail)
        VALUES ('created index', 'UX_QuoteMilestones_Quote_SortOrder', 'one row per position');
    PRINT 'UX_QuoteMilestones_Quote_SortOrder -> created';
END
ELSE
    PRINT 'UX_QuoteMilestones_Quote_SortOrder already exists - skipped';

-- =====================================================================
-- VERIFICATION
-- =====================================================================
PRINT '';
PRINT '=== 0. What this run changed ===';
SELECT Seq, Step, ObjectName, Detail FROM @Touched ORDER BY Seq;

PRINT '';
PRINT '=== 1. The schedule table ===';
PRINT '    Rows should be 0. No quote gets a schedule by running this,';
PRINT '    and a quote without one invoices exactly as it does today.';

EXEC sp_executesql N'SELECT COUNT(*) AS [Rows] FROM dbo.QuoteMilestones;';

SELECT c.name AS [Column], ty.name AS [Type],
       CASE WHEN ty.name LIKE N'%char%' THEN c.max_length / 2 ELSE c.precision END AS [Size],
       c.scale AS [Scale], c.is_nullable AS [Nullable]
FROM sys.columns c
JOIN sys.types ty ON ty.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID(N'dbo.QuoteMilestones')
ORDER BY c.column_id;

PRINT '';
PRINT '=== 2. The five new columns on dbo.Invoices ===';
PRINT '    All five must be NULLABLE. MilestonePercent must be (9,4) -';
PRINT '    the same shape as QuoteMilestones.SharePercent, so 33.3333';
PRINT '    does';
PRINT '    not get rounded on its way onto the invoice. That is the 062';
PRINT '    bug and this is the column it would land on next.';

SELECT c.name AS [Column], ty.name AS [Type],
       CASE WHEN ty.name LIKE N'%char%' THEN c.max_length / 2 ELSE c.precision END AS [Size],
       c.scale AS [Scale], c.is_nullable AS [Nullable]
FROM sys.columns c
JOIN sys.types ty ON ty.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID(N'dbo.Invoices')
  AND c.name IN (N'MilestoneId', N'MilestoneName', N'MilestoneSequence',
                 N'MilestoneCount', N'MilestonePercent')
ORDER BY c.name;

PRINT '';
PRINT '=== 3. Every existing invoice is unmilestoned ===';
PRINT '    Withmilestone should be 0 on every row. Anything else means';
PRINT '    this script has been run before and the feature is in use -';
PRINT '    which is fine, it just is not a fresh install.';

-- sp_executesql, because MilestoneId may have been created moments ago
-- in step 2 and the whole batch is parsed before any of it runs. A bare
-- SELECT naming the column here would fail to COMPILE on a first run —
-- taking the entire script down with it, including the parts that
-- worked.
SET @sql = N'
SELECT t.Name AS [Workspace],
       COUNT(*) AS [Invoices],
       SUM(CASE WHEN i.MilestoneId IS NOT NULL THEN 1 ELSE 0 END) AS [Withmilestone],
       SUM(CASE WHEN i.QuoteId    IS NULL      THEN 1 ELSE 0 END) AS [Manual]
FROM dbo.Invoices i
JOIN dbo.Tenants  t ON t.Id = i.TenantId
WHERE i.IsDeleted = 0
GROUP BY t.Name
ORDER BY t.Name;';
EXEC sp_executesql @sql;

PRINT '';
PRINT '=== 4. The unique index could not have been created ===';
PRINT '    Should be EMPTY. Each row would be a quote that ALREADY has';
PRINT '    more than one live invoice - which the 018 application guard';
PRINT '    should have prevented, so a row here means the guard was';
PRINT '    bypassed at some point (a race, or a direct INSERT).';
PRINT '';
PRINT '    If this grid is not empty the CREATE UNIQUE INDEX in step 4';
PRINT '    will have FAILED and the whole script rolled back. Void or';
PRINT '    delete the duplicates, then run it again.';

SELECT t.Name    AS [Workspace],
       q.Number  AS [Quote],
       COUNT(*)  AS [LiveInvoices]
FROM dbo.Invoices i
JOIN dbo.Quotes   q ON q.Id = i.QuoteId
JOIN dbo.Tenants  t ON t.Id = i.TenantId
WHERE i.IsDeleted = 0
  AND i.Status   <> 6          -- not void
  AND i.QuoteId  IS NOT NULL
GROUP BY t.Name, q.Number
HAVING COUNT(*) > 1
ORDER BY [LiveInvoices] DESC, t.Name;

PRINT '';
PRINT '=== 5. Accepted quotes with nothing invoiced against them ===';
PRINT '    Context, not a problem. These are the quotes that can be';
PRINT '    given a schedule and billed in stages from now on.';

SELECT TOP (20)
       t.Name       AS [Workspace],
       q.Number     AS [Quote],
       q.Currency   AS [Ccy],
       q.GrandTotal AS [Total],
       q.IssueDateUtc AS [Issued]
FROM dbo.Quotes  q
JOIN dbo.Tenants t ON t.Id = q.TenantId
WHERE q.IsDeleted = 0
  AND q.Status    = 3          -- QuoteStatus.Accepted
  AND NOT EXISTS (SELECT 1 FROM dbo.Invoices i
                  WHERE i.QuoteId = q.Id AND i.IsDeleted = 0 AND i.Status <> 6)
ORDER BY q.GrandTotal DESC;

-- =====================================================================
IF @Commit = 1
BEGIN
    COMMIT;
    PRINT '';
    PRINT '>>> COMMITTED.';
END
ELSE
BEGIN
    ROLLBACK;
    PRINT '';
    PRINT '>>> ROLLED BACK (@Commit = 0). Check the grids, then set @Commit = 1 and run again.';
END
