-- =====================================================================
-- 068_ProductCategories.sql
--
-- WHAT THIS DOES
--   1. Creates dbo.ProductCategories — a SHARED-OR-TENANT table, the
--      same shape Roles, CompanyVerticals and TaxRates already use:
--      TenantId NULL means "a Merkai default, visible to every
--      workspace", and a row with a TenantId belongs to that workspace
--      alone.
--   2. Seeds the eight defaults that have been hardcoded in
--      ProductCategoriesConfiguration.cs since the catalogue was built.
--   3. ADOPTS the list for any workspace that already has a product in
--      a category outside those eight — see the long note at step 4,
--      because that step is the one with a judgement call in it.
--
-- WHY THE TABLE EXISTS
--   A C# static list cannot be edited by the people who need to edit it.
--   An interiors firm has no use for "Subscriptions"; a tuition centre
--   wants "Exam prep" and "Course material". Until now the answer was a
--   rebuild and a redeploy, which means the eight categories were really
--   eight guesses about four countries' worth of SMEs.
--
-- WHY Product.Category STAYS A STRING
--   It is an nvarchar today and every quote, invoice, report and filter
--   reads it as one. Turning it into a CategoryId would be a foreign key
--   across six read paths and a migration that cannot fail halfway. The
--   rename handler updates every affected product in the same
--   transaction instead, so the string stays true. The cost is that the
--   two can drift if somebody edits dbo.Products by hand — which is the
--   same cost Product.Category has had all along.
--
-- IS IT SAFE TO RE-RUN
--   Yes. The table, every index, the seed and the adoption are all
--   guarded, and nothing is ever updated in place — the seed inserts
--   only names that are missing, and the adoption skips a workspace that
--   already owns rows.
--
-- HOW TO RUN IT
--   Leave @Commit = 0 the first time, read the grids, then set it to 1
--   and run it again.
--
-- ORDER OF OPERATIONS: a table created by this script cannot be
-- referenced by name later in the same batch — SQL Server binds names
-- when it parses — so everything after the CREATE goes through
-- sp_executesql.
-- =====================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit BIT = 0;      -- <<<<<< set to 1 when the grids look right
DECLARE @Adopt  BIT = 1;      -- 0 = seed the defaults, adopt nobody

DECLARE @Touched TABLE (
    Seq        INT IDENTITY(1,1),
    Step       NVARCHAR(30),
    ObjectName SYSNAME NULL,
    Detail     NVARCHAR(400) NULL
);

DECLARE @sql NVARCHAR(MAX);
DECLARE @Rows INT;

BEGIN TRAN;

-- ─────────────────────────────────────────────────────────────────────
-- 1. The table
-- ─────────────────────────────────────────────────────────────────────
IF OBJECT_ID(N'dbo.ProductCategories', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ProductCategories
    (
        Id            uniqueidentifier NOT NULL
            CONSTRAINT PK_ProductCategories PRIMARY KEY,

        -- NULL = a Merkai default, visible to every workspace.
        -- Set    = owned by that workspace and invisible to others.
        -- The query filter in FlowDbContext is
        --   e.TenantId == null || e.TenantId == CurrentTenantId
        -- exactly as it is for Role, CompanyVertical and TaxRate.
        TenantId      uniqueidentifier NULL,

        -- THIS IS THE VALUE STORED IN dbo.Products.Category. Not an id —
        -- see the note at the top. 80 rather than 50 because "Course
        -- material and workbooks" is a category somebody will type.
        Name          nvarchar(80)     NOT NULL,

        -- A Bootstrap Icons class, e.g. "bi-cpu". Picked from a list in
        -- the UI rather than typed, so a broken class cannot be saved;
        -- the column is wide enough for any of them.
        Icon          nvarchar(60)     NOT NULL
            CONSTRAINT DF_ProductCategories_Icon DEFAULT (N'bi-box'),

        -- #rrggbb. 9 characters leaves room for #rrggbbaa if transparency
        -- is ever wanted; the app validates and stores 7.
        Color         nvarchar(9)      NOT NULL
            CONSTRAINT DF_ProductCategories_Color DEFAULT (N'#6b7280'),

        -- The order the dropdowns show them in. Hand-set, not
        -- alphabetical: the two or three categories a business sells most
        -- of belong at the top of a list somebody uses forty times a day.
        SortOrder     int              NOT NULL
            CONSTRAINT DF_ProductCategories_SortOrder DEFAULT (0),

        -- Inactive = keep the row, stop offering it. A category that has
        -- been used on a quote cannot simply vanish: the products still
        -- carry the name and old documents still print it.
        IsActive      bit              NOT NULL
            CONSTRAINT DF_ProductCategories_IsActive DEFAULT (1),

        CreatedAtUtc  datetime2(7)     NOT NULL
            CONSTRAINT DF_ProductCategories_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CreatedBy     nvarchar(200)    NULL,
        UpdatedAtUtc  datetime2(7)     NULL,
        UpdatedBy     nvarchar(200)    NULL,

        IsDeleted     bit              NOT NULL
            CONSTRAINT DF_ProductCategories_IsDeleted DEFAULT (0),

        CONSTRAINT CK_ProductCategories_Name
            CHECK (LEN(LTRIM(RTRIM(Name))) > 0),

        CONSTRAINT FK_ProductCategories_Tenants_TenantId
            FOREIGN KEY (TenantId) REFERENCES dbo.Tenants (Id)
            ON DELETE NO ACTION
    );

    INSERT INTO @Touched (Step, ObjectName, Detail)
        VALUES ('created table', 'ProductCategories', 'shared-or-tenant, TenantId NULL = Merkai default');
    PRINT 'dbo.ProductCategories -> created';
END
ELSE
    PRINT 'dbo.ProductCategories already exists - skipped';

-- ─────────────────────────────────────────────────────────────────────
-- 2. Indexes
--
--    Through sp_executesql: the table may have been created moments ago
--    in this same batch.
-- ─────────────────────────────────────────────────────────────────────

-- UNIQUE on (TenantId, Name) among live rows.
--
-- FILTERED on IsDeleted = 0 so a name can be reused after the old
-- category is removed — without the filter, deleting "Hardware" and
-- adding it back again would collide with the soft-deleted row forever.
--
-- NULL counts as a value in a SQL Server unique index, which is what we
-- want here: it stops two Merkai defaults both being called "Software".
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'UX_ProductCategories_Tenant_Name'
                 AND object_id = OBJECT_ID(N'dbo.ProductCategories'))
BEGIN
    SET @sql = N'
    CREATE UNIQUE INDEX UX_ProductCategories_Tenant_Name
        ON dbo.ProductCategories (TenantId, Name)
        WHERE IsDeleted = 0;';
    EXEC sp_executesql @sql;
    INSERT INTO @Touched (Step, ObjectName, Detail)
        VALUES ('created index', 'UX_ProductCategories_Tenant_Name', 'unique among live rows');
    PRINT 'UX_ProductCategories_Tenant_Name -> created';
END
ELSE
    PRINT 'UX_ProductCategories_Tenant_Name already exists - skipped';

-- The read every dropdown does: this workspace's live, active rows in
-- display order.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_ProductCategories_Tenant_Sort'
                 AND object_id = OBJECT_ID(N'dbo.ProductCategories'))
BEGIN
    SET @sql = N'
    CREATE INDEX IX_ProductCategories_Tenant_Sort
        ON dbo.ProductCategories (TenantId, SortOrder, Name)
        INCLUDE (Icon, Color, IsActive)
        WHERE IsDeleted = 0;';
    EXEC sp_executesql @sql;
    INSERT INTO @Touched (Step, ObjectName, Detail)
        VALUES ('created index', 'IX_ProductCategories_Tenant_Sort', 'covers the dropdown read');
    PRINT 'IX_ProductCategories_Tenant_Sort -> created';
END
ELSE
    PRINT 'IX_ProductCategories_Tenant_Sort already exists - skipped';

-- ─────────────────────────────────────────────────────────────────────
-- 3. Seed the eight Merkai defaults (TenantId NULL)
--
--    Name, icon and colour are copied verbatim from
--    ProductCategoriesConfiguration.Categories, so nothing any existing
--    product renders as changes colour the day this runs.
--
--    Inserts only what is missing, by name. A default somebody has since
--    recoloured by hand is left exactly as it is.
-- ─────────────────────────────────────────────────────────────────────
SET @sql = N'
MERGE dbo.ProductCategories AS t
USING (VALUES
        (N''Electronics'',   N''bi-lightning'',   N''#3b82f6'', 10),
        (N''Software'',      N''bi-code-square'', N''#8b5cf6'', 20),
        (N''Services'',      N''bi-tools'',       N''#10b981'', 30),
        (N''Hardware'',      N''bi-cpu'',         N''#6366f1'', 40),
        (N''Subscriptions'', N''bi-arrow-repeat'',N''#f59e0b'', 50),
        (N''Consulting'',    N''bi-people'',      N''#06b6d4'', 60),
        (N''Training'',      N''bi-book'',        N''#ec4899'', 70),
        (N''Support'',       N''bi-headset'',     N''#84cc16'', 80)
      ) AS s (Name, Icon, Color, SortOrder)
   ON t.Name = s.Name AND t.TenantId IS NULL AND t.IsDeleted = 0
 WHEN NOT MATCHED BY TARGET THEN
      INSERT (Id, TenantId, Name, Icon, Color, SortOrder, IsActive, CreatedAtUtc, CreatedBy, IsDeleted)
      VALUES (NEWID(), NULL, s.Name, s.Icon, s.Color, s.SortOrder, 1, SYSUTCDATETIME(), N''migration 068'', 0);
SET @OutRows = @@ROWCOUNT;';

EXEC sp_executesql @sql, N'@OutRows INT OUTPUT', @OutRows = @Rows OUTPUT;

INSERT INTO @Touched (Step, ObjectName, Detail)
    VALUES ('seeded defaults', 'ProductCategories',
            CAST(ISNULL(@Rows, 0) AS NVARCHAR(20)) + N' Merkai default(s) inserted (TenantId NULL)');
PRINT 'Merkai defaults -> ' + CAST(ISNULL(@Rows, 0) AS VARCHAR(20)) + ' inserted';

-- ─────────────────────────────────────────────────────────────────────
-- 4. ADOPTION — and this is the step worth reading
--
-- HOW THE APP RESOLVES THE LIST
--   If a workspace owns NO rows of its own, it sees the Merkai defaults.
--   The moment it owns ONE, it sees only its own. One rule, no
--   per-category override logic, and "my list is my list" is a thing an
--   SME owner can hold in their head.
--
-- WHAT THAT MEANS HERE
--   Some workspaces already have products in a category that is not one
--   of the eight — typed before this table existed, or carried in by an
--   import. Those values have to become rows, or the Settings page would
--   show a list that does not contain a category its own products are
--   using.
--
--   But inserting ONLY the stray value would flip that workspace to
--   "owns rows" and hide all eight defaults — so a workspace with one
--   odd category would open Settings and find a list of exactly one.
--
--   So adoption copies the WHOLE list: all eight defaults as
--   tenant-owned rows, plus one row per stray value. From then on the
--   workspace owns every category it can see and can rename, recolour,
--   reorder or remove any of them. This is the same operation the
--   Settings page performs the first time somebody edits anything, so
--   the migration and the app do not have two different ideas of it.
--
--   Workspaces whose categories all fall inside the eight are left
--   alone. They keep seeing the defaults and adopt later, if ever.
--
-- Set @Adopt = 0 to seed the defaults and adopt nobody. The stray values
-- stay on the products and the Settings page then shows the defaults
-- only — which is survivable, but the stray categories will not appear
-- in any dropdown.
-- ─────────────────────────────────────────────────────────────────────
IF @Adopt = 1
BEGIN
    -- 4a. Which workspaces need adopting, and their stray values.
    SET @sql = N'
    DECLARE @Need TABLE (TenantId uniqueidentifier PRIMARY KEY);

    INSERT INTO @Need (TenantId)
    SELECT DISTINCT p.TenantId
      FROM dbo.Products p
     WHERE p.IsDeleted = 0
       AND NULLIF(LTRIM(RTRIM(p.Category)), N'''') IS NOT NULL
       -- a value outside the Merkai defaults
       AND NOT EXISTS (
            SELECT 1 FROM dbo.ProductCategories d
             WHERE d.TenantId IS NULL AND d.IsDeleted = 0
               AND d.Name = LTRIM(RTRIM(p.Category)))
       -- and this workspace has not already adopted
       AND NOT EXISTS (
            SELECT 1 FROM dbo.ProductCategories o
             WHERE o.TenantId = p.TenantId AND o.IsDeleted = 0);

    -- 4b. Copy all eight defaults to each of them.
    INSERT INTO dbo.ProductCategories
        (Id, TenantId, Name, Icon, Color, SortOrder, IsActive, CreatedAtUtc, CreatedBy, IsDeleted)
    SELECT NEWID(), n.TenantId, d.Name, d.Icon, d.Color, d.SortOrder, d.IsActive,
           SYSUTCDATETIME(), N''migration 068 (adopted)'', 0
      FROM @Need n
      CROSS JOIN dbo.ProductCategories d
     WHERE d.TenantId IS NULL AND d.IsDeleted = 0;

    SET @OutRows = @@ROWCOUNT;

    -- 4c. Then one row per stray value they actually use. SortOrder puts
    --     them after the eight, in the order a human would read them.
    ;WITH stray AS (
        SELECT p.TenantId,
               LTRIM(RTRIM(p.Category)) AS Name,
               ROW_NUMBER() OVER (PARTITION BY p.TenantId
                                  ORDER BY LTRIM(RTRIM(p.Category))) AS rn
          FROM dbo.Products p
          JOIN @Need n ON n.TenantId = p.TenantId
         WHERE p.IsDeleted = 0
           AND NULLIF(LTRIM(RTRIM(p.Category)), N'''') IS NOT NULL
         GROUP BY p.TenantId, LTRIM(RTRIM(p.Category))
    )
    INSERT INTO dbo.ProductCategories
        (Id, TenantId, Name, Icon, Color, SortOrder, IsActive, CreatedAtUtc, CreatedBy, IsDeleted)
    SELECT NEWID(), s.TenantId, s.Name, N''bi-box'', N''#6b7280'', 100 + (s.rn * 10), 1,
           SYSUTCDATETIME(), N''migration 068 (from products)'', 0
      FROM stray s
     WHERE NOT EXISTS (
            SELECT 1 FROM dbo.ProductCategories e
             WHERE e.TenantId = s.TenantId AND e.IsDeleted = 0 AND e.Name = s.Name);

    SET @OutRows = @OutRows + @@ROWCOUNT;';

    EXEC sp_executesql @sql, N'@OutRows INT OUTPUT', @OutRows = @Rows OUTPUT;

    INSERT INTO @Touched (Step, ObjectName, Detail)
        VALUES ('adopted', 'ProductCategories',
                CAST(ISNULL(@Rows, 0) AS NVARCHAR(20)) + N' tenant-owned row(s) created');
    PRINT 'Adoption -> ' + CAST(ISNULL(@Rows, 0) AS VARCHAR(20)) + ' tenant-owned row(s)';
END
ELSE
    PRINT 'Adoption skipped (@Adopt = 0) - defaults seeded, no workspace adopted';

-- =====================================================================
-- VERIFICATION
-- =====================================================================
PRINT '';
PRINT '=== 0. What this run changed ===';
SELECT Seq, Step, ObjectName, Detail FROM @Touched ORDER BY Seq;

PRINT '';
PRINT '=== 1. The Merkai defaults ===';
PRINT '    Should be exactly 8, all with TenantId NULL, colours matching';
PRINT '    ProductCategoriesConfiguration.cs so nothing changes shade.';

EXEC sp_executesql N'
    SELECT Name, Icon, Color, SortOrder, IsActive, CreatedBy
      FROM dbo.ProductCategories
     WHERE TenantId IS NULL AND IsDeleted = 0
     ORDER BY SortOrder, Name;';

PRINT '';
PRINT '=== 2. Which workspaces now own their own list ===';
PRINT '    A workspace listed here sees ONLY these rows. One that is not';
PRINT '    listed sees the 8 defaults until somebody edits something.';

EXEC sp_executesql N'
    SELECT t.Name                                              AS [Workspace],
           COUNT(*)                                            AS [Categories],
           SUM(CASE WHEN c.IsActive = 1 THEN 1 ELSE 0 END)     AS [Active],
           MIN(c.CreatedBy)                                    AS [Origin]
      FROM dbo.ProductCategories c
      JOIN dbo.Tenants t ON t.Id = c.TenantId
     WHERE c.IsDeleted = 0
     GROUP BY t.Name
     ORDER BY t.Name;';

PRINT '';
PRINT '=== 3. THE ONE THAT MUST BE EMPTY ===';
PRINT '    Every product category value that no longer appears in the';
PRINT '    list its own workspace will see. Rows here mean a product is';
PRINT '    filed under something the Settings page cannot show, so the';
PRINT '    dropdown on its Edit page would come up blank. Do NOT commit.';

EXEC sp_executesql N'
    SELECT t.Name                      AS [Workspace],
           LTRIM(RTRIM(p.Category))    AS [OrphanedCategory],
           COUNT(*)                    AS [Products]
      FROM dbo.Products p
      JOIN dbo.Tenants  t ON t.Id = p.TenantId
     WHERE p.IsDeleted = 0
       AND NULLIF(LTRIM(RTRIM(p.Category)), N'''') IS NOT NULL
       AND NOT EXISTS (
            SELECT 1 FROM dbo.ProductCategories c
             WHERE c.IsDeleted = 0
               AND c.Name = LTRIM(RTRIM(p.Category))
               AND (c.TenantId = p.TenantId
                    OR (c.TenantId IS NULL
                        AND NOT EXISTS (SELECT 1 FROM dbo.ProductCategories o
                                         WHERE o.TenantId = p.TenantId AND o.IsDeleted = 0))))
     GROUP BY t.Name, LTRIM(RTRIM(p.Category))
     ORDER BY t.Name, [OrphanedCategory];';

PRINT '';
PRINT '=== 4. How products are spread across categories ===';
PRINT '    Uncategorized is not an error — Product.Category is nullable';
PRINT '    and always has been.';

SELECT t.Name                                      AS [Workspace],
       ISNULL(NULLIF(LTRIM(RTRIM(p.Category)), N''), N'(uncategorized)') AS [Category],
       COUNT(*)                                    AS [Products],
       SUM(CASE WHEN p.IsActive = 1 THEN 1 ELSE 0 END) AS [Active]
FROM dbo.Products p
JOIN dbo.Tenants  t ON t.Id = p.TenantId
WHERE p.IsDeleted = 0
GROUP BY t.Name, ISNULL(NULLIF(LTRIM(RTRIM(p.Category)), N''), N'(uncategorized)')
ORDER BY t.Name, [Products] DESC;

PRINT '';
PRINT '=== 5. The table and its indexes ===';

SELECT c.name AS [Column], ty.name AS [Type],
       CASE WHEN ty.name LIKE N'%char%' THEN c.max_length / 2 ELSE c.precision END AS [Size],
       c.is_nullable AS [Nullable]
FROM sys.columns c
JOIN sys.types ty ON ty.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID(N'dbo.ProductCategories')
ORDER BY c.column_id;

SELECT i.name AS [Index], i.type_desc AS [Kind], i.is_unique AS [Unique],
       i.has_filter AS [Filtered], i.filter_definition AS [Filter]
FROM sys.indexes i
WHERE i.object_id = OBJECT_ID(N'dbo.ProductCategories') AND i.name IS NOT NULL
ORDER BY i.name;

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
