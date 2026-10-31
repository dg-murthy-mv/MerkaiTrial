/* =====================================================================
   079_LeadFieldMapping.sql — custom fields, Round C part 3 (Leads)

   Adds two columns to dbo.CustomFieldDefinitions:
     MapToContactFieldId  UNIQUEIDENTIFIER NULL
     MapToDealFieldId     UNIQUEIDENTIFIER NULL

   Used ONLY on Lead fields: "when this lead is converted, copy this
   field's value into that Contact field / that Deal field". NULL on
   every other row, and NULL on every existing row — nothing is copied
   anywhere until you set a mapping in Settings → Custom Fields → Leads.

   NOT foreign keys, on purpose. A self-referencing FK on this table
   would block deleting a Contact or Deal field that a Lead field points
   at; the application clears the pointer instead when that field is
   deleted (DeleteCustomFieldHandler), and conversion skips a target
   that is switched off or gone. The values themselves live in
   dbo.CustomFieldValues, unchanged.

   Requires 075 and 076 committed first. No other table changes:
   Lead custom field VALUES go in dbo.CustomFieldValues like every other
   record type's, with the definition's EntityType = 'Lead'.

   HOW TO RUN
     1. Run as-is. @Commit = 0 — everything is ROLLED BACK. Read grid 3.
     2. Change @Commit to 1 and run again.

   SAFE TO RE-RUN. Every ALTER is guarded. New columns are referenced
   only through sp_executesql, because SQL Server compiles the whole batch
   first and a direct reference to a column added in the same batch
   fails to compile.
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit BIT = 0;      -- flip to 1 to commit

IF OBJECT_ID(N'dbo.CustomFieldDefinitions', N'U') IS NULL
   OR COL_LENGTH(N'dbo.CustomFieldDefinitions', N'ShowInList') IS NULL
BEGIN
    RAISERROR('079 needs 075_CustomFields.sql and 076_CustomFieldListColumns.sql committed first. Nothing was changed.', 16, 1);
    RETURN;
END

BEGIN TRAN;

/* ── 1. The two mapping columns ───────────────────────────────────── */
IF COL_LENGTH(N'dbo.CustomFieldDefinitions', N'MapToContactFieldId') IS NULL
BEGIN
    EXEC sp_executesql N'
    ALTER TABLE dbo.CustomFieldDefinitions
        ADD MapToContactFieldId UNIQUEIDENTIFIER NULL;';
    PRINT 'Added CustomFieldDefinitions.MapToContactFieldId';
END
ELSE
    PRINT 'CustomFieldDefinitions.MapToContactFieldId already exists — skipped';

IF COL_LENGTH(N'dbo.CustomFieldDefinitions', N'MapToDealFieldId') IS NULL
BEGIN
    EXEC sp_executesql N'
    ALTER TABLE dbo.CustomFieldDefinitions
        ADD MapToDealFieldId UNIQUEIDENTIFIER NULL;';
    PRINT 'Added CustomFieldDefinitions.MapToDealFieldId';
END
ELSE
    PRINT 'CustomFieldDefinitions.MapToDealFieldId already exists — skipped';

/* ── 2. Verification grids ────────────────────────────────────────── */

-- Grid 1: definitions per record type. Lead rows appear once you add
-- Lead fields in Settings; until then this shows Contact / Deal / Company.
EXEC sp_executesql N'
SELECT EntityType, COUNT(*) AS Fields,
       SUM(CASE WHEN MapToContactFieldId IS NOT NULL THEN 1 ELSE 0 END) AS MappedToContact,
       SUM(CASE WHEN MapToDealFieldId    IS NOT NULL THEN 1 ELSE 0 END) AS MappedToDeal
FROM dbo.CustomFieldDefinitions
WHERE IsDeleted = 0
GROUP BY EntityType
ORDER BY EntityType;';

-- Grid 2: FOR INFORMATION. The old free-text column Leads.CustomFieldsJson
-- is not read by the new custom fields. Before 079 the convert-to-deal
-- handler copied it into a new deal''s Description when none was typed;
-- that is removed. If this count is above 0, those leads hold text from
-- an older version — nothing is lost, it just is not shown.
IF COL_LENGTH(N'dbo.Leads', N'CustomFieldsJson') IS NOT NULL
    EXEC sp_executesql N'
    SELECT COUNT(*) AS LeadsWithOldCustomFieldsJson
    FROM dbo.Leads
    WHERE IsDeleted = 0 AND CustomFieldsJson IS NOT NULL AND LTRIM(RTRIM(CustomFieldsJson)) <> N'''';';

-- Grid 3: one row each, all ''OK''.
EXEC sp_executesql N'
SELECT N''MapToContactFieldId is UNIQUEIDENTIFIER NULL'' AS [Check],
       CASE WHEN EXISTS (SELECT 1 FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
                         WHERE c.object_id = OBJECT_ID(N''dbo.CustomFieldDefinitions'')
                           AND c.name = N''MapToContactFieldId'' AND t.name = N''uniqueidentifier'' AND c.is_nullable = 1)
            THEN N''OK'' ELSE N''MISSING'' END AS [Result]
UNION ALL
SELECT N''MapToDealFieldId is UNIQUEIDENTIFIER NULL'',
       CASE WHEN EXISTS (SELECT 1 FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
                         WHERE c.object_id = OBJECT_ID(N''dbo.CustomFieldDefinitions'')
                           AND c.name = N''MapToDealFieldId'' AND t.name = N''uniqueidentifier'' AND c.is_nullable = 1)
            THEN N''OK'' ELSE N''MISSING'' END;';

IF @Commit = 1
BEGIN
    COMMIT;
    PRINT '079: COMMITTED.';
END
ELSE
BEGIN
    ROLLBACK;
    PRINT '079: rolled back (dry run). Set @Commit = 1 to keep it.';
END
