/* =====================================================================
   076_CustomFieldListColumns.sql — custom fields, Round B

   Adds:
     dbo.CustomFieldDefinitions.ShowInList   BIT NOT NULL DEFAULT 0
     IX_CustomFieldValues_Definition_Number  (DefinitionId, NumberValue)
                                             INCLUDE (EntityId), filtered
     IX_CustomFieldValues_Definition_Date    (DefinitionId, DateValue)
                                             INCLUDE (EntityId), filtered

   Requires 075_CustomFields.sql to have been committed first.

   HOW TO RUN
     1. Run as-is. @Commit = 0 — everything is ROLLED BACK. Read grid 3.
     2. Change @Commit to 1 and run again.

   SAFE TO RE-RUN. Every ALTER and CREATE is guarded.

   The new column is referenced only through sp_executesql — SQL Server
   compiles the whole batch first, and a direct reference to a column
   added earlier in the same batch fails to compile.

   WHY NO INDEX ON TextValue: at NVARCHAR(1000) a key can exceed SQL
   Server's 1,700-byte limit for a nonclustered index, which would make
   SOME inserts fail. Text and dropdown filters narrow by DefinitionId
   through UX_CustomFieldValues_Definition_Entity instead.
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit BIT = 0;      -- flip to 1 to commit

IF OBJECT_ID(N'dbo.CustomFieldDefinitions', N'U') IS NULL
   OR OBJECT_ID(N'dbo.CustomFieldValues', N'U') IS NULL
BEGIN
    RAISERROR('076 needs 075_CustomFields.sql committed first. Nothing was changed.', 16, 1);
    RETURN;
END

BEGIN TRAN;

/* ── 1. ShowInList ────────────────────────────────────────────────── */
IF COL_LENGTH(N'dbo.CustomFieldDefinitions', N'ShowInList') IS NULL
BEGIN
    EXEC sp_executesql N'
    ALTER TABLE dbo.CustomFieldDefinitions
        ADD ShowInList BIT NOT NULL
            CONSTRAINT DF_CustomFieldDefinitions_ShowInList DEFAULT (0);';
    PRINT 'Added CustomFieldDefinitions.ShowInList';
END
ELSE
    PRINT 'CustomFieldDefinitions.ShowInList already exists — skipped';

/* ── 2. Range-filter indexes ──────────────────────────────────────── */
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_CustomFieldValues_Definition_Number'
                 AND object_id = OBJECT_ID(N'dbo.CustomFieldValues'))
BEGIN
    EXEC sp_executesql N'
    CREATE INDEX IX_CustomFieldValues_Definition_Number
        ON dbo.CustomFieldValues (DefinitionId, NumberValue)
        INCLUDE (EntityId)
        WHERE NumberValue IS NOT NULL;';
    PRINT 'Created IX_CustomFieldValues_Definition_Number';
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_CustomFieldValues_Definition_Date'
                 AND object_id = OBJECT_ID(N'dbo.CustomFieldValues'))
BEGIN
    EXEC sp_executesql N'
    CREATE INDEX IX_CustomFieldValues_Definition_Date
        ON dbo.CustomFieldValues (DefinitionId, DateValue)
        INCLUDE (EntityId)
        WHERE DateValue IS NOT NULL;';
    PRINT 'Created IX_CustomFieldValues_Definition_Date';
END

/* ── 3. Verification grids ────────────────────────────────────────── */

-- Grid 1: the definitions, with the new column. Every existing field
-- starts with ShowInList = 0, so nothing appears on the list until you
-- switch it on in Settings → Custom Fields.
EXEC sp_executesql N'
SELECT EntityType, Label, FieldType, IsActive, ShowInList, SortOrder
FROM dbo.CustomFieldDefinitions
WHERE IsDeleted = 0
ORDER BY TenantId, EntityType, SortOrder;';

-- Grid 2: indexes on CustomFieldValues. Expect 5 (PK + 4).
EXEC sp_executesql N'
SELECT i.name AS [Index], i.is_unique AS [Unique], i.filter_definition AS [Filter]
FROM sys.indexes i
WHERE i.object_id = OBJECT_ID(N''dbo.CustomFieldValues'') AND i.name IS NOT NULL
ORDER BY i.name;';

-- Grid 3: one row each, all ''OK''.
EXEC sp_executesql N'
SELECT N''ShowInList is BIT NOT NULL'' AS [Check],
       CASE WHEN EXISTS (SELECT 1 FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
                         WHERE c.object_id = OBJECT_ID(N''dbo.CustomFieldDefinitions'')
                           AND c.name = N''ShowInList'' AND t.name = N''bit'' AND c.is_nullable = 0)
            THEN N''OK'' ELSE N''MISSING'' END AS [Result]
UNION ALL
SELECT N''Number filter index'',
       CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE name = N''IX_CustomFieldValues_Definition_Number'')
            THEN N''OK'' ELSE N''MISSING'' END
UNION ALL
SELECT N''Date filter index'',
       CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE name = N''IX_CustomFieldValues_Definition_Date'')
            THEN N''OK'' ELSE N''MISSING'' END;';

IF @Commit = 1
BEGIN
    COMMIT;
    PRINT '076: COMMITTED.';
END
ELSE
BEGIN
    ROLLBACK;
    PRINT '076: rolled back (dry run). Set @Commit = 1 to keep it.';
END
