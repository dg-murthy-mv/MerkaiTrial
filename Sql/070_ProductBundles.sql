-- =====================================================================
-- 070_ProductBundles.sql
--
-- WHAT THIS DOES
--   Creates dbo.ProductBundleItems — what a bundle CONTAINS. Nothing is
--   backfilled; the table is empty afterwards and no existing product
--   becomes a bundle.
--
--   It also adds nothing to dbo.Products. "Bundle" is a value of the
--   existing Type column, beside Product, Service and Subscription —
--   the same column ProductKinds has owned since 051, which is why
--   there is no ALTER in this script at all.
--
-- ─────────────────────────────────────────────────────────────────────
-- WHAT A BUNDLE IS, AND THE ONE DECISION BEHIND THE WHOLE ROUND
--
--   "Starter package — ₹45,000", containing 1 × Onboarding setup,
--   3 × Training day and 1 × Support (12 months).
--
--   ON A QUOTE IT IS **ONE LINE** at the bundle's own price, with its
--   contents printed underneath as a breakdown. NOT one line per
--   component.
--
--   Why: the customer sees a price they can say yes to, and nothing in
--   the line maths, the tax maths, the approval rules, the PDF layout or
--   CreateInvoiceFromQuoteHandler has to change — a bundle line is an
--   ordinary line that happens to carry a longer description.
--
--   The honest cost, and it is worth knowing before you sell one: a
--   quote line has ONE tax rate and ONE tax code. A bundle mixing 5%
--   goods with 18% services cannot be quoted as a single line under
--   Indian GST. The product form says so, and the answer is to quote
--   those components separately.
--
-- WHY THE BUNDLE'S PRICE IS ITS OWN AND NOT THE SUM OF ITS PARTS
--   Because a bundle exists in order to cost less than its parts. If
--   the price were computed, editing a component's price would silently
--   reprice the bundle — and therefore every quote drafted from it
--   afterwards. Products.ListPrice on the bundle row is the price. The
--   component total is shown on the form as a HINT ("components total
--   ₹52,000 — this bundle is 13% below"), which is the number a business
--   actually wants to see, and it is computed, never stored.
--
-- WHY COMPONENTS ARE A LIVE REFERENCE AND NOT A SNAPSHOT
--   The opposite of QuoteItem.UnitOfMeasure and .TaxCode, deliberately.
--   A bundle is a DEFINITION — "what is in the Starter package" — and
--   renaming Training day should change what the catalogue says the
--   package contains. A quote is a DOCUMENT, and the breakdown is
--   snapshotted into the quote line's description at the moment it is
--   added, so a quote already sent never changes. Definition live,
--   document frozen.
--
-- NO BUNDLES INSIDE BUNDLES. Refused by the application, not by this
-- schema — a CHECK cannot see another row's Type. One level keeps the
-- breakdown finite, keeps the rollup arithmetic one query deep, and
-- means a quote line's description cannot nest.
-- ─────────────────────────────────────────────────────────────────────
--
-- IS IT SAFE TO RE-RUN
--   Yes. The table, both indexes and the CHECK are guarded, and nothing
--   is inserted or updated.
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
-- 1. The table
-- ─────────────────────────────────────────────────────────────────────
IF OBJECT_ID(N'dbo.ProductBundleItems', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ProductBundleItems
    (
        Id                 uniqueidentifier NOT NULL
            CONSTRAINT PK_ProductBundleItems PRIMARY KEY,

        -- NOT NULL. A bundle's contents are never shared across
        -- workspaces — this goes in FlowDbContext's STRICT filter group,
        -- like ProductPrices and unlike ProductCategories.
        TenantId           uniqueidentifier NOT NULL,

        -- The product whose Type is 'Bundle'.
        BundleProductId    uniqueidentifier NOT NULL,

        -- What is in it. Must NOT itself be a bundle; the application
        -- refuses that, because a CHECK constraint cannot read another
        -- row's Type column.
        ComponentProductId uniqueidentifier NOT NULL,

        -- DECIMAL(18,4), the same shape as QuoteItem.Quantity since 052.
        -- Four places because a component can be 0.5 days of training or
        -- 12.5 m² of material, and because a bundle that could only hold
        -- whole numbers would re-create the exact problem 052 fixed.
        Quantity           decimal(18,4)    NOT NULL
            CONSTRAINT DF_ProductBundleItems_Quantity DEFAULT (1),

        -- The order the breakdown is printed in. Hand-set: "Onboarding,
        -- then Training, then Support" is the order the work happens in,
        -- and alphabetical would scramble it.
        SortOrder          int              NOT NULL
            CONSTRAINT DF_ProductBundleItems_SortOrder DEFAULT (0),

        CreatedAtUtc       datetime2(7)     NOT NULL
            CONSTRAINT DF_ProductBundleItems_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CreatedBy          nvarchar(200)    NULL,
        UpdatedAtUtc       datetime2(7)     NULL,
        UpdatedBy          nvarchar(200)    NULL,

        IsDeleted          bit              NOT NULL
            CONSTRAINT DF_ProductBundleItems_IsDeleted DEFAULT (0),

        -- Zero would mean "this is in the bundle, none of it", which is
        -- not a thing. Remove the row instead.
        CONSTRAINT CK_ProductBundleItems_Quantity CHECK (Quantity > 0),

        -- The one self-reference a CHECK **can** see. Catches the
        -- simplest cycle; the application catches the rest.
        CONSTRAINT CK_ProductBundleItems_NotSelf
            CHECK (BundleProductId <> ComponentProductId),

        -- NO ACTION on both, deliberately, and it is the one place this
        -- schema does not cascade.
        --
        -- ProductPrices cascades from its product because a price has no
        -- meaning without it. A bundle item references TWO products, and
        -- cascading from the COMPONENT would silently empty a bundle when
        -- somebody hard-deleted one part of it. Products are soft-deleted
        -- anyway, so in practice neither fires — but if one ever did,
        -- failing loudly is the right outcome for a bundle definition.
        CONSTRAINT FK_ProductBundleItems_Bundle
            FOREIGN KEY (BundleProductId) REFERENCES dbo.Products (Id)
            ON DELETE NO ACTION,

        CONSTRAINT FK_ProductBundleItems_Component
            FOREIGN KEY (ComponentProductId) REFERENCES dbo.Products (Id)
            ON DELETE NO ACTION,

        CONSTRAINT FK_ProductBundleItems_Tenants_TenantId
            FOREIGN KEY (TenantId) REFERENCES dbo.Tenants (Id)
            ON DELETE NO ACTION
    );

    INSERT INTO @Touched (Step, ObjectName, Detail)
        VALUES ('created table', 'ProductBundleItems', 'what a bundle contains; one level only');
    PRINT 'dbo.ProductBundleItems -> created';
END
ELSE
    PRINT 'dbo.ProductBundleItems already exists - skipped';

-- ─────────────────────────────────────────────────────────────────────
-- 2. Indexes
-- ─────────────────────────────────────────────────────────────────────

-- UNIQUE among live rows: a component appears in a bundle ONCE, with a
-- quantity. Twice would be two rows saying "2 × Training day" and
-- "3 × Training day" with nothing deciding between them.
--
-- Filtered on IsDeleted so a component can be removed from a bundle and
-- added back later.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'UX_ProductBundleItems_Bundle_Component'
                 AND object_id = OBJECT_ID(N'dbo.ProductBundleItems'))
BEGIN
    SET @sql = N'
    CREATE UNIQUE INDEX UX_ProductBundleItems_Bundle_Component
        ON dbo.ProductBundleItems (TenantId, BundleProductId, ComponentProductId)
        WHERE IsDeleted = 0;';
    EXEC sp_executesql @sql;
    INSERT INTO @Touched (Step, ObjectName, Detail)
        VALUES ('created index', 'UX_ProductBundleItems_Bundle_Component', 'one row per component');
    PRINT 'UX_ProductBundleItems_Bundle_Component -> created';
END
ELSE
    PRINT 'UX_ProductBundleItems_Bundle_Component already exists - skipped';

-- THE READ THE QUOTE EDITOR DOES: the contents of every bundle in the
-- catalogue, in one query, in display order. ComponentProductId is
-- included so the lookup is covered.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_ProductBundleItems_Tenant_Bundle_Order'
                 AND object_id = OBJECT_ID(N'dbo.ProductBundleItems'))
BEGIN
    SET @sql = N'
    CREATE INDEX IX_ProductBundleItems_Tenant_Bundle_Order
        ON dbo.ProductBundleItems (TenantId, BundleProductId, SortOrder)
        INCLUDE (ComponentProductId, Quantity)
        WHERE IsDeleted = 0;';
    EXEC sp_executesql @sql;
    INSERT INTO @Touched (Step, ObjectName, Detail)
        VALUES ('created index', 'IX_ProductBundleItems_Tenant_Bundle_Order', 'covers the catalogue read');
    PRINT 'IX_ProductBundleItems_Tenant_Bundle_Order -> created';
END
ELSE
    PRINT 'IX_ProductBundleItems_Tenant_Bundle_Order already exists - skipped';

-- =====================================================================
-- VERIFICATION
-- =====================================================================
PRINT '';
PRINT '=== 0. What this run changed ===';
SELECT Seq, Step, ObjectName, Detail FROM @Touched ORDER BY Seq;

PRINT '';
PRINT '=== 1. The table ===';
PRINT '    Should be EMPTY. No product becomes a bundle by running this.';

EXEC sp_executesql N'SELECT COUNT(*) AS [Rows] FROM dbo.ProductBundleItems;';

SELECT c.name AS [Column], ty.name AS [Type],
       CASE WHEN ty.name LIKE N'%char%' THEN c.max_length / 2 ELSE c.precision END AS [Size],
       c.scale AS [Scale], c.is_nullable AS [Nullable]
FROM sys.columns c
JOIN sys.types ty ON ty.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID(N'dbo.ProductBundleItems')
ORDER BY c.column_id;

PRINT '';
PRINT '=== 2. Quantity must match QuoteItem.Quantity ===';
PRINT '    Both decimal(18,4). A narrower column here would round a';
PRINT '    half-day of training on its way into the bundle - which is';
PRINT '    the bug 062 describes, in a new place.';

SELECT OBJECT_NAME(c.object_id) AS [Table], c.name AS [Column],
       c.precision AS [Precision], c.scale AS [Scale]
FROM sys.columns c
WHERE (c.object_id = OBJECT_ID(N'dbo.QuoteItems')          AND c.name = N'Quantity')
   OR (c.object_id = OBJECT_ID(N'dbo.ProductBundleItems')  AND c.name = N'Quantity')
ORDER BY [Table];

PRINT '';
PRINT '=== 3. Nothing is a Bundle yet ===';
PRINT '    Products by kind. Bundle should be absent - the value is';
PRINT '    legal from this round on, but no row carries it until';
PRINT '    somebody sets it on the product form.';

SELECT t.Name              AS [Workspace],
       p.Type              AS [Kind],
       COUNT(*)            AS [Products],
       SUM(CASE WHEN p.IsActive = 1 THEN 1 ELSE 0 END) AS [Active]
FROM dbo.Products p
JOIN dbo.Tenants  t ON t.Id = p.TenantId
WHERE p.IsDeleted = 0
GROUP BY t.Name, p.Type
ORDER BY t.Name, [Products] DESC;

PRINT '';
PRINT '=== 4. Anything in Products.Type that ProductKinds will not';
PRINT '        recognise ===';
PRINT '    Should be EMPTY. ProductKinds.Normalise falls back to';
PRINT '    "Product" for an unknown kind, so a row here opens and edits';
PRINT '    normally but shows the wrong kind in the dropdown until';
PRINT '    somebody saves it. Worth knowing about before Bundle joins';
PRINT '    the list - a typo like "Bundel" would read as "Product".';

SELECT t.Name   AS [Workspace],
       p.Type   AS [UnrecognisedKind],
       COUNT(*) AS [Products]
FROM dbo.Products p
JOIN dbo.Tenants  t ON t.Id = p.TenantId
WHERE p.IsDeleted = 0
  AND p.Type NOT IN (N'Product', N'Service', N'Subscription', N'Bundle')
GROUP BY t.Name, p.Type
ORDER BY t.Name;

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
