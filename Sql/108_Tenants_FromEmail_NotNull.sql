-- =====================================================================
-- 108_Tenants_FromEmail_NotNull.sql                         (Round 108)
--
-- Run on BOTH MerkaiTrial_E2E and your development database (and, later,
-- production — it is safe there too). Safe to run more than once.
--
-- WHY
--   Every server start printed:
--     [SchemaCheck] NULL IN DB Tenants.FromEmail — the app expects a value
--     every time, but the column allows NULL; a row with NULL there will
--     crash when it is read.
--   The workspace forms always ask for a "From" email and the app treats it
--   as required, but the column itself still allowed NULL. One NULL row
--   would break the screens that list or show workspaces.
--
-- WHAT IT DOES
--   1. Lists any workspace that has no From email yet (normally none).
--   2. Fills each one with its workspace admin's email address — the
--      earliest active admin — or, if it has none, a clearly-made-up
--      placeholder "workspace-<id>@invalid.example" (the .example domain
--      never receives mail), so nothing is ever sent to a stranger.
--      Change it afterwards in Super Admin → Workspaces → Edit.
--   3. Makes the column NOT NULL. If an index covers the column it is
--      dropped and re-created exactly as it was around the change.
--   4. Shows the result.
-- =====================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF COL_LENGTH('dbo.Tenants', 'FromEmail') IS NULL
BEGIN
    RAISERROR('dbo.Tenants.FromEmail does not exist in this database — wrong database?', 16, 1);
    RETURN;
END;

-- ── 1. Who is missing one ────────────────────────────────────────────
SELECT t.Id, t.Name, 'No From email — will be filled' AS [Before]
FROM dbo.Tenants t
WHERE t.FromEmail IS NULL OR LTRIM(RTRIM(t.FromEmail)) = '';

BEGIN TRANSACTION;

-- ── 2. Fill them ─────────────────────────────────────────────────────
UPDATE t
SET t.FromEmail = COALESCE(
        (SELECT TOP (1) u.Email
         FROM dbo.Users u
         WHERE u.TenantId = t.Id AND u.IsTenantAdmin = 1 AND u.IsActive = 1
               AND u.Email IS NOT NULL AND u.Email <> ''
         ORDER BY u.CreatedAtUtc, u.Id),
        'workspace-' + LOWER(CONVERT(varchar(36), t.Id)) + '@invalid.example')
FROM dbo.Tenants t
WHERE t.FromEmail IS NULL OR LTRIM(RTRIM(t.FromEmail)) = '';

-- ── 3. NOT NULL (indexes on the column are put back as they were) ────
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('dbo.Tenants') AND name = 'FromEmail' AND is_nullable = 1)
BEGIN
    DECLARE @drop nvarchar(max) = N'', @create nvarchar(max) = N'';

    SELECT
        @drop = @drop + N'DROP INDEX ' + QUOTENAME(i.name) + N' ON dbo.Tenants;' + CHAR(10),
        @create = @create
            + N'CREATE ' + CASE WHEN i.is_unique = 1 THEN N'UNIQUE ' ELSE N'' END
            + N'NONCLUSTERED INDEX ' + QUOTENAME(i.name) + N' ON dbo.Tenants ('
            + STUFF((SELECT N', ' + QUOTENAME(c.name) + CASE WHEN ic.is_descending_key = 1 THEN N' DESC' ELSE N'' END
                     FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                     WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
                     ORDER BY ic.key_ordinal FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, N'')
            + N')'
            + ISNULL(N' INCLUDE (' + STUFF((SELECT N', ' + QUOTENAME(c.name)
                     FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                     WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1
                     FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, N'') + N')', N'')
            + ISNULL(N' WHERE ' + i.filter_definition, N'')
            + N';' + CHAR(10)
    FROM sys.indexes i
    WHERE i.object_id = OBJECT_ID('dbo.Tenants')
      AND i.type = 2                        -- nonclustered
      AND i.is_primary_key = 0 AND i.is_unique_constraint = 0
      AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c
                    ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                  WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND c.name = 'FromEmail');

    IF @drop <> N'' BEGIN PRINT 'Indexes on FromEmail, put aside for the change:'; PRINT @drop; EXEC sp_executesql @drop; END;

    -- Same type, length and collation as now — only NULL changes.
    DECLARE @type nvarchar(200) = (
        SELECT TYPE_NAME(c.user_type_id)
             + CASE WHEN TYPE_NAME(c.user_type_id) IN ('nvarchar', 'nchar')
                    THEN '(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CONVERT(varchar(10), c.max_length / 2) END + ')'
                    WHEN TYPE_NAME(c.user_type_id) IN ('varchar', 'char')
                    THEN '(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CONVERT(varchar(10), c.max_length) END + ')'
                    ELSE '' END
             + ISNULL(' COLLATE ' + c.collation_name, '')
        FROM sys.columns c
        WHERE c.object_id = OBJECT_ID('dbo.Tenants') AND c.name = 'FromEmail');
    DECLARE @alter nvarchar(max) = N'ALTER TABLE dbo.Tenants ALTER COLUMN FromEmail ' + @type + N' NOT NULL;';
    PRINT @alter;
    EXEC sp_executesql @alter;
    PRINT 'Tenants.FromEmail is now NOT NULL.';

    IF @create <> N'' BEGIN PRINT 'Putting them back:'; PRINT @create; EXEC sp_executesql @create; END;
END
ELSE
    PRINT 'Tenants.FromEmail was already NOT NULL — nothing to change.';

COMMIT TRANSACTION;

-- ── 4. Check ─────────────────────────────────────────────────────────
SELECT 'Tenants.FromEmail' AS [Column],
       CASE WHEN is_nullable = 0 THEN 'OK — NOT NULL' ELSE 'STILL ALLOWS NULL' END AS [Status],
       TYPE_NAME(user_type_id) AS [Type]
FROM sys.columns
WHERE object_id = OBJECT_ID('dbo.Tenants') AND name = 'FromEmail';

SELECT t.Id, t.Name, t.FromEmail AS [From email now]
FROM dbo.Tenants t
WHERE t.FromEmail LIKE 'workspace-%@invalid.example';   -- placeholders to replace, if any
