-- =====================================================================
-- 069_ProductPrices.sql
--
-- WHAT THIS DOES
--   Creates dbo.ProductPrices — one row per product per ADDITIONAL
--   currency. Nothing is backfilled and nothing existing is touched: the
--   table is empty after this runs, and every price in the system keeps
--   coming from Products.ListPrice exactly as it does today.
--
-- ─────────────────────────────────────────────────────────────────────
-- WHY THIS IS A BUG FIX AND NOT JUST A FEATURE
--
--   A quote's currency comes from its DEAL, not from the workspace:
--
--       // Quotes/Create.cshtml.cs
--       var currency = await ResolveCurrencyAsync(tenantId, DealId.Value);
--       // -> deal.Currency, falling back to the tenant's
--
--   Deals.Currency is a real per-deal column. But the catalogue has one
--   price, in no stated currency, and the line editor was handed the
--   WORKSPACE currency for its labels:
--
--       // Quotes/Create.cshtml
--       CurrencyCode   = Model.Currency,      // the TENANT's
--       ListPricesJson = ...from p.ListPrice  // no currency at all
--
--   So a deal in USD against an INR workspace produced a quote STORED AS
--   USD containing INR prices, with the editor labelling the totals INR
--   while the rep typed. Quotes/Detail then warned "Priced in USD, not
--   your workspace currency" — true, and too late.
--
--   Run the two SELECTs at the end of this script before committing.
--   If any workspace shows more than one currency, it has happened.
-- ─────────────────────────────────────────────────────────────────────
--
-- WHY Products.ListPrice STAYS, AND WHAT IT MEANS NOW
--
--   ListPrice is THE PRICE IN THE WORKSPACE'S OWN CURRENCY. It always
--   was — there has never been a currency column beside it, and every
--   page resolves the symbol from ICurrentTenantService. This round does
--   not move that; it writes the rule down and adds the other
--   currencies beside it.
--
--   ProductPrices therefore holds ONLY currencies OTHER than the
--   workspace's own. Two sources of truth for the home price would be
--   worse than none: the application refuses a row in the workspace
--   currency and tells the person to edit the base price instead.
--
--   One consequence worth knowing: if a workspace's country is ever
--   changed, ListPrice silently becomes a number in the new currency.
--   That is true today and this round does not make it worse — but it is
--   the reason changing a workspace's country is a platform operation
--   and not a settings screen.
--
-- WHY NOT A CONVERSION RATE INSTEAD OF A PRICE PER CURRENCY
--   Because an SME does not sell at the interbank rate. A ₹45,000
--   package is $549 in the US, not $540.62, and next month it is still
--   $549. A rate would also need a source, a refresh and a date, and
--   would silently reprice last week's quote. A typed price per currency
--   is what a business actually decides.
--
-- IS IT SAFE TO RE-RUN
--   Yes. The table, both indexes and the CHECK are guarded, and nothing
--   is inserted or updated.
--
-- HOW TO RUN IT
--   Leave @Commit = 0 the first time, read the grids, then set it to 1.
--
-- ORDER OF OPERATIONS: a table created by this script cannot be
-- referenced by name later in the same batch, so everything after the
-- CREATE goes through sp_executesql.
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
IF OBJECT_ID(N'dbo.ProductPrices', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ProductPrices
    (
        Id            uniqueidentifier NOT NULL
            CONSTRAINT PK_ProductPrices PRIMARY KEY,

        -- NOT NULL, unlike ProductCategories.TenantId. A price is never
        -- shared: Merkai has no business publishing what anybody's
        -- product costs. This one goes in the STRICT filter group in
        -- FlowDbContext, not the shared-or-tenant one.
        TenantId      uniqueidentifier NOT NULL,

        ProductId     uniqueidentifier NOT NULL,

        -- An ISO 4217 code from CurrencyConfiguration.Currencies. 8
        -- rather than 3 to match Deals.Currency and Quotes.Currency,
        -- which are both nvarchar(8) — a narrower column here would be
        -- the one place a currency code could not round-trip.
        CurrencyCode  nvarchar(8)      NOT NULL,

        -- DECIMAL(18,2), matching Products.ListPrice. Explicit rather
        -- than left to EF's default, which is the same thing by accident
        -- — and 062 is the round that explains why relying on that
        -- accident silently truncates values.
        ListPrice     decimal(18,2)    NOT NULL,

        CreatedAtUtc  datetime2(7)     NOT NULL
            CONSTRAINT DF_ProductPrices_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CreatedBy     nvarchar(200)    NULL,
        UpdatedAtUtc  datetime2(7)     NULL,
        UpdatedBy     nvarchar(200)    NULL,

        IsDeleted     bit              NOT NULL
            CONSTRAINT DF_ProductPrices_IsDeleted DEFAULT (0),

        -- A price of zero is legal (a free component of a bundle, a
        -- promotional line). A NEGATIVE price is not, and the quote line
        -- editor already refuses one — this is the backstop for an
        -- import or a hand-written UPDATE.
        CONSTRAINT CK_ProductPrices_ListPrice CHECK (ListPrice >= 0),

        CONSTRAINT CK_ProductPrices_CurrencyCode
            CHECK (LEN(LTRIM(RTRIM(CurrencyCode))) BETWEEN 3 AND 8),

        CONSTRAINT FK_ProductPrices_Products_ProductId
            FOREIGN KEY (ProductId) REFERENCES dbo.Products (Id)
            ON DELETE CASCADE,

        CONSTRAINT FK_ProductPrices_Tenants_TenantId
            FOREIGN KEY (TenantId) REFERENCES dbo.Tenants (Id)
            ON DELETE NO ACTION
    );

    INSERT INTO @Touched (Step, ObjectName, Detail)
        VALUES ('created table', 'ProductPrices', 'one row per product per non-home currency');
    PRINT 'dbo.ProductPrices -> created';
END
ELSE
    PRINT 'dbo.ProductPrices already exists - skipped';

-- ─────────────────────────────────────────────────────────────────────
-- 2. Indexes
--
--    ON DELETE CASCADE on the ProductId FK above is deliberate and is
--    the one hard delete in this schema: a price has no meaning without
--    its product, and Products are SOFT-deleted anyway, so the cascade
--    only ever fires if somebody removes a product row for real.
-- ─────────────────────────────────────────────────────────────────────

-- UNIQUE among live rows. Filtered on IsDeleted so a currency can be
-- removed from a product and added back later.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'UX_ProductPrices_Product_Currency'
                 AND object_id = OBJECT_ID(N'dbo.ProductPrices'))
BEGIN
    SET @sql = N'
    CREATE UNIQUE INDEX UX_ProductPrices_Product_Currency
        ON dbo.ProductPrices (TenantId, ProductId, CurrencyCode)
        WHERE IsDeleted = 0;';
    EXEC sp_executesql @sql;
    INSERT INTO @Touched (Step, ObjectName, Detail)
        VALUES ('created index', 'UX_ProductPrices_Product_Currency', 'unique among live rows');
    PRINT 'UX_ProductPrices_Product_Currency -> created';
END
ELSE
    PRINT 'UX_ProductPrices_Product_Currency already exists - skipped';

-- THE READ THE QUOTE EDITOR DOES: every price for this workspace in ONE
-- currency, for the whole catalogue, in one query. Currency leads the
-- key because that is the equality predicate; ProductId is included so
-- the lookup is covered and never touches the table.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_ProductPrices_Tenant_Currency'
                 AND object_id = OBJECT_ID(N'dbo.ProductPrices'))
BEGIN
    SET @sql = N'
    CREATE INDEX IX_ProductPrices_Tenant_Currency
        ON dbo.ProductPrices (TenantId, CurrencyCode)
        INCLUDE (ProductId, ListPrice)
        WHERE IsDeleted = 0;';
    EXEC sp_executesql @sql;
    INSERT INTO @Touched (Step, ObjectName, Detail)
        VALUES ('created index', 'IX_ProductPrices_Tenant_Currency', 'covers the catalogue read');
    PRINT 'IX_ProductPrices_Tenant_Currency -> created';
END
ELSE
    PRINT 'IX_ProductPrices_Tenant_Currency already exists - skipped';

-- =====================================================================
-- VERIFICATION
-- =====================================================================
PRINT '';
PRINT '=== 0. What this run changed ===';
SELECT Seq, Step, ObjectName, Detail FROM @Touched ORDER BY Seq;

PRINT '';
PRINT '=== 1. The table ===';
PRINT '    Should be EMPTY. Nothing is backfilled: every price in the';
PRINT '    system still comes from Products.ListPrice until somebody';
PRINT '    types a second currency on a product.';

EXEC sp_executesql N'SELECT COUNT(*) AS [Rows] FROM dbo.ProductPrices;';

SELECT c.name AS [Column], ty.name AS [Type],
       CASE WHEN ty.name LIKE N'%char%' THEN c.max_length / 2
            WHEN ty.name = N'decimal'  THEN c.precision
            ELSE c.precision END AS [Size],
       c.scale AS [Scale], c.is_nullable AS [Nullable]
FROM sys.columns c
JOIN sys.types ty ON ty.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID(N'dbo.ProductPrices')
ORDER BY c.column_id;

PRINT '';
PRINT '=== 2. ListPrice must match Products.ListPrice exactly ===';
PRINT '    Both decimal(18,2). A mismatch here is how a price gets';
PRINT '    rounded on its way between the two tables.';

SELECT OBJECT_NAME(c.object_id) AS [Table], c.name AS [Column],
       c.precision AS [Precision], c.scale AS [Scale]
FROM sys.columns c
WHERE c.object_id IN (OBJECT_ID(N'dbo.Products'), OBJECT_ID(N'dbo.ProductPrices'))
  AND c.name = N'ListPrice'
ORDER BY [Table];

PRINT '';
PRINT '=== 3. READ THIS ONE. Deals, by currency, per workspace ===';
PRINT '    TWO OR MORE ROWS FOR ONE WORKSPACE means the defect in the';
PRINT '    header is live: those deals produce quotes in a currency the';
PRINT '    catalogue has no price in, and the rep was shown the';
PRINT '    workspace currency while typing.';
PRINT '    One row per workspace means it is latent - fixed before it';
PRINT '    ever bit.';

SELECT t.Name                                              AS [Workspace],
       ISNULL(NULLIF(LTRIM(RTRIM(d.Currency)), N''), N'(blank)') AS [DealCurrency],
       COUNT(*)                                            AS [Deals]
FROM dbo.Deals d
JOIN dbo.Tenants t ON t.Id = d.TenantId
WHERE d.IsDeleted = 0
GROUP BY t.Name, ISNULL(NULLIF(LTRIM(RTRIM(d.Currency)), N''), N'(blank)')
ORDER BY t.Name, [Deals] DESC;

PRINT '';
PRINT '=== 4. ...and the quotes those deals produced ===';
PRINT '    A (blank) row here is round 029''s bug, already fixed: quotes';
PRINT '    created through the UI before that stored an empty currency.';
PRINT '    Listed because those rows still exist and will print with no';
PRINT '    symbol at all.';

SELECT t.Name                                              AS [Workspace],
       ISNULL(NULLIF(LTRIM(RTRIM(q.Currency)), N''), N'(blank)') AS [QuoteCurrency],
       COUNT(*)                                            AS [Quotes],
       MIN(q.Number)                                       AS [FirstExample]
FROM dbo.Quotes q
JOIN dbo.Tenants t ON t.Id = q.TenantId
WHERE q.IsDeleted = 0
GROUP BY t.Name, ISNULL(NULLIF(LTRIM(RTRIM(q.Currency)), N''), N'(blank)')
ORDER BY t.Name, [Quotes] DESC;

PRINT '';
PRINT '=== 5. Each workspace''s own currency, for comparison ===';
PRINT '    This is the currency Products.ListPrice is denominated in.';
PRINT '    Anything in grids 3 and 4 that does NOT match its row here is';
PRINT '    a document priced from numbers that were never in that';
PRINT '    currency.';

SELECT t.Name            AS [Workspace],
       c.Name            AS [Country],
       c.CurrencyCode    AS [WorkspaceCurrency],
       c.CurrencySymbol  AS [Symbol],
       c.CurrencyDecimals AS [Decimals],
       COUNT(p.Id)       AS [Products]
FROM dbo.Tenants t
LEFT JOIN dbo.Countries c ON c.Id = t.CountryId
LEFT JOIN dbo.Products  p ON p.TenantId = t.Id AND p.IsDeleted = 0
GROUP BY t.Name, c.Name, c.CurrencyCode, c.CurrencySymbol, c.CurrencyDecimals
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
