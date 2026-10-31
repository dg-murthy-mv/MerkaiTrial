-- =====================================================================
-- 056_TaxRateTenantScope.sql
--
-- WHAT THIS DOES
--   TaxRates.TenantId  ->  NULLABLE
--   Existing rows holding 00000000-0000-0000-0000-000000000000 become NULL
--
-- WHY IT MATTERS MORE THAN IT LOOKS
--   NULL means "a SYSTEM rate, available to every tenant in that
--   country". That is the convention Roles and CompanyVerticals already
--   use, and the convention every query in the tax module was WRITTEN
--   for:
--
--       .Where(t => t.TenantId == null || t.TenantId == query.TenantId)
--
--   TenantId was a non-nullable Guid, so `t.TenantId == null` is always
--   FALSE — the compiler says so, CS8073 — and that expression appeared
--   in three handlers doing nothing at all. Meanwhile the admin screen
--   stored Guid.Empty for a system rate. The code wrote one value and
--   looked for another, so:
--
--     * the "System Rates" tile counted 0, always
--     * a tenant could never see a system rate
--     * GetDefaultTaxRateForCountry's system path could only ever 404
--
--   This script is what lets the two halves meet.
--
-- IS IT SAFE TO RE-RUN
--   Yes. The ALTER is guarded on the column still being NOT NULL, and
--   the UPDATE only touches rows still holding Guid.Empty.
--
-- HOW TO RUN IT
--   Leave @Commit = 0 the first time. Grid 0 lists everything it would
--   change and grid 3 shows the rows about to be converted. Then set
--   @Commit = 1 and run it again.
--
--   Deploy the code and this script TOGETHER. The C# expects NULL; until
--   this runs, a system rate still holds Guid.Empty and no tenant will
--   see it. Nothing breaks in between — the resolver falls back to
--   Country.DefaultTaxRate, exactly as it has been doing all along.
--
-- BLOCKERS
--   Retyping a column is refused when an index, a CHECK, a user
--   statistic or a computed column depends on it. The sweep below finds
--   all of them BEFORE the transaction opens. CHECK and DEFAULT
--   constraints are handled; the other three stop the script and name
--   what they found, because rebuilding an index from metadata is how a
--   filtered or included-column index comes back subtly different.
-- =====================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit BIT = 0;      -- <<<<<< set to 1 when the grids look right

DECLARE @sql     NVARCHAR(MAX);
DECLARE @blocked NVARCHAR(MAX);
DECLARE @TaxRates INT = OBJECT_ID('dbo.TaxRates');

DECLARE @Touched TABLE (
    Seq        INT IDENTITY(1,1),
    Step       NVARCHAR(30),
    ObjectName SYSNAME NULL,
    Detail     NVARCHAR(MAX) NULL
);

DECLARE @Redo TABLE (
    OldName    SYSNAME,
    NewName    SYSNAME,
    Definition NVARCHAR(MAX)
);

-- ─────────────────────────────────────────────────────────────────────
-- 0. SWEEP, before the transaction, so a stop leaves nothing half-done
-- ─────────────────────────────────────────────────────────────────────
IF @TaxRates IS NULL
BEGIN
    PRINT 'STOPPED. dbo.TaxRates does not exist in this database.';
    THROW 51056, 'dbo.TaxRates not found. Nothing was changed.', 1;
END

-- 0a. Indexes on TenantId
SET @blocked = NULL;
SELECT @blocked = ISNULL(@blocked + N', ', N'') + i.name
FROM sys.index_columns ic
JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE ic.object_id = @TaxRates AND c.name = 'TenantId' AND i.name IS NOT NULL;

IF @blocked IS NOT NULL
BEGIN
    PRINT 'STOPPED. An index sits on TaxRates.TenantId, and this script will not';
    PRINT 'drop and rebuild one for you — a filtered or included-column index can';
    PRINT 'come back subtly different. Send me these names and I will write the';
    PRINT 'right drop/recreate around them:';
    PRINT '    ' + @blocked;
    THROW 51056, 'Index on TaxRates.TenantId - see the messages above. Nothing was changed.', 1;
END

-- 0b. User-created statistics
SET @blocked = NULL;
SELECT @blocked = ISNULL(@blocked + N', ', N'') + s.name
FROM sys.stats_columns sc
JOIN sys.stats s   ON s.object_id = sc.object_id AND s.stats_id = sc.stats_id
JOIN sys.columns c ON c.object_id = sc.object_id AND c.column_id = sc.column_id
WHERE sc.object_id = @TaxRates AND c.name = 'TenantId' AND s.user_created = 1;

IF @blocked IS NOT NULL
BEGIN
    PRINT 'STOPPED. Hand-created statistics reference TaxRates.TenantId: ' + @blocked;
    THROW 51056, 'User statistics on TaxRates.TenantId. Nothing was changed.', 1;
END

-- 0c. Computed columns
SET @blocked = NULL;
SELECT @blocked = ISNULL(@blocked + N', ', N'') + cc.name
FROM sys.computed_columns cc
WHERE cc.object_id = @TaxRates AND cc.definition LIKE '%TenantId%';

IF @blocked IS NOT NULL
BEGIN
    PRINT 'STOPPED. A computed column is built from TaxRates.TenantId: ' + @blocked;
    THROW 51056, 'Computed column on TaxRates.TenantId. Nothing was changed.', 1;
END

PRINT 'Blocker sweep: no index, statistic or computed column on TaxRates.TenantId.';

-- =====================================================================
BEGIN TRAN;

-- ─────────────────────────────────────────────────────────────────────
-- 1. CHECK and DEFAULT constraints on TenantId -- record, then drop
-- ─────────────────────────────────────────────────────────────────────
INSERT INTO @Touched (Step, ObjectName, Detail)
SELECT 'dropped CHECK', cc.name, cc.definition
FROM sys.check_constraints cc
WHERE cc.parent_object_id = @TaxRates
  AND (cc.parent_column_id = (SELECT c.column_id FROM sys.columns c
                              WHERE c.object_id = @TaxRates AND c.name = 'TenantId')
       OR cc.definition LIKE '%TenantId%');

INSERT INTO @Touched (Step, ObjectName, Detail)
SELECT 'dropped DEFAULT', dc.name, dc.definition
FROM sys.default_constraints dc
JOIN sys.columns c
  ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
WHERE dc.parent_object_id = @TaxRates AND c.name = 'TenantId';

INSERT INTO @Redo (OldName, NewName, Definition)
SELECT ObjectName, 'DF_TaxRates_TenantId', Detail
FROM @Touched WHERE Step = 'dropped DEFAULT';

SELECT @sql = STRING_AGG(
                  CAST(N'ALTER TABLE dbo.TaxRates DROP CONSTRAINT ' + QUOTENAME(ObjectName) + N';'
                       AS NVARCHAR(MAX)),
                  CHAR(13) + CHAR(10)) WITHIN GROUP (ORDER BY Seq)
FROM @Touched
WHERE Step IN ('dropped CHECK', 'dropped DEFAULT');

IF LEN(ISNULL(@sql, N'')) > 0
BEGIN
    PRINT 'Dropping the constraints on TenantId:';
    PRINT @sql;
    EXEC sp_executesql @sql;
END
ELSE
    PRINT 'Nothing sits on TaxRates.TenantId - nothing to drop.';

-- ─────────────────────────────────────────────────────────────────────
-- 2. TenantId -> NULLABLE
-- ─────────────────────────────────────────────────────────────────────
IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = @TaxRates AND name = 'TenantId' AND is_nullable = 0
)
BEGIN
    PRINT 'TaxRates.TenantId -> UNIQUEIDENTIFIER NULL';
    ALTER TABLE dbo.TaxRates ALTER COLUMN TenantId UNIQUEIDENTIFIER NULL;
    INSERT INTO @Touched (Step, ObjectName, Detail)
        VALUES ('retyped', 'TenantId', 'NOT NULL -> NULL');
END
ELSE
    PRINT 'TaxRates.TenantId is already nullable - skipped';

-- ─────────────────────────────────────────────────────────────────────
-- 3. Put the DEFAULT back, verbatim, under a stable name.
--
--    Verbatim because a default is not a rule about what is allowed.
--    A default of Guid.Empty on a column where Guid.Empty no longer has
--    a meaning would be odd, so grid 0 prints it -- if one turns up,
--    tell me and we will drop it rather than restore it.
-- ─────────────────────────────────────────────────────────────────────
DELETE r FROM @Redo r
 WHERE EXISTS (SELECT 1 FROM sys.default_constraints d
               WHERE d.parent_object_id = @TaxRates AND d.name = r.NewName);

SELECT @sql = STRING_AGG(
                  CAST(N'ALTER TABLE dbo.TaxRates ADD CONSTRAINT ' + QUOTENAME(NewName) +
                       N' DEFAULT ' + Definition + N' FOR TenantId;' AS NVARCHAR(MAX)),
                  CHAR(13) + CHAR(10))
FROM @Redo;

IF LEN(ISNULL(@sql, N'')) > 0
BEGIN
    PRINT 'Putting the DEFAULT constraint back:';
    PRINT @sql;
    EXEC sp_executesql @sql;

    INSERT INTO @Touched (Step, ObjectName, Detail)
    SELECT 'recreated DEFAULT', NewName, Definition + ' (unchanged; was ' + OldName + ')'
    FROM @Redo;
END
ELSE
    PRINT 'No DEFAULT constraint to put back.';

-- ─────────────────────────────────────────────────────────────────────
-- 4. Guid.Empty  ->  NULL
--
--    THE POINT OF THE WHOLE SCRIPT. Every rate created through
--    /Admin/TaxRates was written with 00000000-0000-0000-0000-000000000000
--    and meant to be a system rate. The code has always looked for NULL.
--
--    Guid.Empty is not a real tenant id — Tenants.Id is a generated Guid
--    and can never be all zeroes — so there is no row this could convert
--    by mistake.
-- ─────────────────────────────────────────────────────────────────────
DECLARE @converted INT = 0;

UPDATE dbo.TaxRates
   SET TenantId = NULL
 WHERE TenantId = '00000000-0000-0000-0000-000000000000';

SET @converted = @@ROWCOUNT;

IF @converted > 0
    INSERT INTO @Touched (Step, ObjectName, Detail)
        VALUES ('converted rows', 'TenantId',
                CONCAT(@converted, ' row(s): Guid.Empty -> NULL (system rates)'));

PRINT CONCAT('Rows converted from Guid.Empty to NULL: ', @converted);

-- =====================================================================
-- VERIFICATION
-- =====================================================================
PRINT '';
PRINT '=== 0. Everything this run changed ===';
SELECT Seq, Step, ObjectName, Detail FROM @Touched ORDER BY Seq;

PRINT '';
PRINT '=== 1. The column ===';

SELECT
    c.name          AS [Column],
    t.name          AS [Type],
    c.is_nullable   AS [Nullable],
    ISNULL(dc.name, '(none)')       AS [DefaultName],
    ISNULL(dc.definition, '(none)') AS [Default]
FROM sys.columns c
JOIN sys.types t ON t.user_type_id = c.user_type_id
LEFT JOIN sys.default_constraints dc
  ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
WHERE c.object_id = @TaxRates AND c.name = 'TenantId';

PRINT '';
PRINT '=== 2. Rates by scope ===';
PRINT '    Scope must be only "system (NULL)" or a real workspace.';
PRINT '    A row still reading "Guid.Empty" means the UPDATE above did not run.';

SELECT
    CASE WHEN tr.TenantId IS NULL THEN 'system (NULL)'
         WHEN tr.TenantId = '00000000-0000-0000-0000-000000000000' THEN '*** Guid.Empty - NOT CONVERTED ***'
         ELSE ISNULL(t.Name, CONVERT(NVARCHAR(50), tr.TenantId)) END AS [Scope],
    COUNT(*)                                        AS [Rates],
    SUM(CASE WHEN tr.IsDefault = 1 THEN 1 ELSE 0 END) AS [Defaults]
FROM dbo.TaxRates tr
LEFT JOIN dbo.Tenants t ON t.Id = tr.TenantId
WHERE tr.IsDeleted = 0
GROUP BY tr.TenantId, t.Name
ORDER BY [Scope];

PRINT '';
PRINT '=== 3. Every rate, and who it belongs to ===';

SELECT
    tr.CountryCode,
    tr.Name,
    tr.TaxType,
    tr.Rate,
    tr.IsDefault,
    tr.IsActive,
    tr.EffectiveFrom,
    tr.EffectiveTo,
    CASE WHEN tr.TenantId IS NULL THEN 'SYSTEM' ELSE ISNULL(t.Name, 'unknown workspace') END AS [Owner]
FROM dbo.TaxRates tr
LEFT JOIN dbo.Tenants t ON t.Id = tr.TenantId
WHERE tr.IsDeleted = 0
ORDER BY tr.CountryCode, [Owner], tr.Name;

PRINT '';
PRINT '=== 4. Countries with no default rate at all ===';
PRINT '    These fall back to Countries.DefaultTaxRate, which is fine —';
PRINT '    it is worth seeing which ones, and whether that number is right.';

SELECT
    c.Code,
    c.Name,
    c.TaxLabel,
    c.DefaultTaxRate  AS [CountryFallbackRate]
FROM dbo.Countries c
WHERE c.IsActive = 1
  AND NOT EXISTS (
        SELECT 1 FROM dbo.TaxRates tr
        WHERE tr.CountryCode = c.Code
          AND tr.TenantId IS NULL
          AND tr.IsDefault = 1
          AND tr.IsActive = 1
          AND tr.IsDeleted = 0)
ORDER BY c.Code;

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
