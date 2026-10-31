/* =====================================================================
   075_CustomFields.sql — custom fields, Round A

   Creates:
     dbo.CustomFieldDefinitions   what fields a workspace has defined
     dbo.CustomFieldValues        one row per field per record with a value

   HOW TO RUN
     1. Run as-is. @Commit = 0, so everything is ROLLED BACK at the end —
        read the verification grids and check they look right.
     2. Change @Commit to 1 and run again to keep it.

   SAFE TO RE-RUN. Every CREATE is guarded; a second run changes nothing.

   Anything that touches a table created earlier in this script goes
   through sp_executesql: SQL Server compiles the whole batch before it
   runs, and a direct reference to a table that does not exist yet fails
   compilation.

   THE COLUMN TYPES MUST MATCH CustomFieldConfiguration.cs:
     NumberValue  DECIMAL(18,4)   — round 062: a mismatch is SILENT
     DateValue    DATE            — a calendar date, never a datetime
     TextValue    NVARCHAR(1000)
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit BIT = 0;      -- flip to 1 to commit

BEGIN TRAN;

/* ── 1. CustomFieldDefinitions ────────────────────────────────────── */
IF OBJECT_ID(N'dbo.CustomFieldDefinitions', N'U') IS NULL
BEGIN
    EXEC sp_executesql N'
    CREATE TABLE dbo.CustomFieldDefinitions
    (
        Id             UNIQUEIDENTIFIER NOT NULL
                       CONSTRAINT PK_CustomFieldDefinitions PRIMARY KEY,
        TenantId       UNIQUEIDENTIFIER NOT NULL,
        EntityType     NVARCHAR(32)     NOT NULL,
        Label          NVARCHAR(80)     NOT NULL,
        FieldType      NVARCHAR(20)     NOT NULL,
        HelpText       NVARCHAR(200)    NULL,
        IsRequired     BIT              NOT NULL CONSTRAINT DF_CustomFieldDefinitions_IsRequired DEFAULT (0),
        IsActive       BIT              NOT NULL CONSTRAINT DF_CustomFieldDefinitions_IsActive   DEFAULT (1),
        SortOrder      INT              NOT NULL,
        DecimalPlaces  INT              NOT NULL CONSTRAINT DF_CustomFieldDefinitions_Decimal    DEFAULT (0),
        OptionsJson    NVARCHAR(MAX)    NULL,
        CreatedAtUtc   DATETIME2        NOT NULL,
        CreatedBy      NVARCHAR(64)     NULL,
        UpdatedAtUtc   DATETIME2        NULL,
        UpdatedBy      NVARCHAR(64)     NULL,
        IsDeleted      BIT              NOT NULL CONSTRAINT DF_CustomFieldDefinitions_IsDeleted  DEFAULT (0),

        CONSTRAINT CK_CustomFieldDefinitions_FieldType
            CHECK (FieldType IN (N''text'', N''number'', N''date'', N''dropdown'', N''checkbox'')),
        CONSTRAINT CK_CustomFieldDefinitions_Decimal
            CHECK (DecimalPlaces BETWEEN 0 AND 4)
    );';
    PRINT 'Created dbo.CustomFieldDefinitions';
END
ELSE
    PRINT 'dbo.CustomFieldDefinitions already exists — skipped';

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'UX_CustomFieldDefinitions_Tenant_Entity_Label'
                 AND object_id = OBJECT_ID(N'dbo.CustomFieldDefinitions'))
BEGIN
    -- One "Renewal date" per entity type per workspace. Filtered, so a
    -- deleted field's name can be used again.
    EXEC sp_executesql N'
    CREATE UNIQUE INDEX UX_CustomFieldDefinitions_Tenant_Entity_Label
        ON dbo.CustomFieldDefinitions (TenantId, EntityType, Label)
        WHERE IsDeleted = 0;';
    PRINT 'Created UX_CustomFieldDefinitions_Tenant_Entity_Label';
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_CustomFieldDefinitions_Tenant_Entity_Sort'
                 AND object_id = OBJECT_ID(N'dbo.CustomFieldDefinitions'))
BEGIN
    EXEC sp_executesql N'
    CREATE INDEX IX_CustomFieldDefinitions_Tenant_Entity_Sort
        ON dbo.CustomFieldDefinitions (TenantId, EntityType, SortOrder)
        WHERE IsDeleted = 0;';
    PRINT 'Created IX_CustomFieldDefinitions_Tenant_Entity_Sort';
END

/* ── 2. CustomFieldValues ─────────────────────────────────────────── */
IF OBJECT_ID(N'dbo.CustomFieldValues', N'U') IS NULL
BEGIN
    EXEC sp_executesql N'
    CREATE TABLE dbo.CustomFieldValues
    (
        Id             UNIQUEIDENTIFIER NOT NULL
                       CONSTRAINT PK_CustomFieldValues PRIMARY KEY,
        TenantId       UNIQUEIDENTIFIER NOT NULL,
        DefinitionId   UNIQUEIDENTIFIER NOT NULL,
        EntityId       UNIQUEIDENTIFIER NOT NULL,
        TextValue      NVARCHAR(1000)   NULL,
        NumberValue    DECIMAL(18,4)    NULL,
        DateValue      DATE             NULL,
        BoolValue      BIT              NULL,
        CreatedAtUtc   DATETIME2        NOT NULL,
        UpdatedAtUtc   DATETIME2        NULL,
        UpdatedBy      NVARCHAR(64)     NULL,

        -- NO ACTION (= Restrict): a field with values is retired, never
        -- deleted, so this should never fire. If it ever does, an error
        -- beats silently losing every value.
        CONSTRAINT FK_CustomFieldValues_Definition
            FOREIGN KEY (DefinitionId) REFERENCES dbo.CustomFieldDefinitions (Id)
    );';
    PRINT 'Created dbo.CustomFieldValues';
END
ELSE
    PRINT 'dbo.CustomFieldValues already exists — skipped';

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'UX_CustomFieldValues_Definition_Entity'
                 AND object_id = OBJECT_ID(N'dbo.CustomFieldValues'))
BEGIN
    -- One value per field per record.
    EXEC sp_executesql N'
    CREATE UNIQUE INDEX UX_CustomFieldValues_Definition_Entity
        ON dbo.CustomFieldValues (DefinitionId, EntityId);';
    PRINT 'Created UX_CustomFieldValues_Definition_Entity';
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_CustomFieldValues_Tenant_Entity'
                 AND object_id = OBJECT_ID(N'dbo.CustomFieldValues'))
BEGIN
    -- "Every value on this contact" — the detail and edit pages.
    EXEC sp_executesql N'
    CREATE INDEX IX_CustomFieldValues_Tenant_Entity
        ON dbo.CustomFieldValues (TenantId, EntityId);';
    PRINT 'Created IX_CustomFieldValues_Tenant_Entity';
END

/* ── 3. Verification grids ────────────────────────────────────────── */

-- Grid 1: the columns, and the types that must match the EF configuration.
EXEC sp_executesql N'
SELECT t.name AS [Table], c.name AS [Column], ty.name AS [Type],
       c.max_length AS [MaxBytes], c.precision AS [Precision], c.scale AS [Scale],
       c.is_nullable AS [Nullable]
FROM sys.columns c
JOIN sys.tables  t  ON t.object_id = c.object_id
JOIN sys.types   ty ON ty.user_type_id = c.user_type_id
WHERE t.name IN (N''CustomFieldDefinitions'', N''CustomFieldValues'')
ORDER BY t.name, c.column_id;';

-- Grid 2: indexes. Expect 3 on each table (PK + 2).
EXEC sp_executesql N'
SELECT t.name AS [Table], i.name AS [Index], i.is_unique AS [Unique],
       i.has_filter AS [Filtered], i.filter_definition AS [Filter]
FROM sys.indexes i
JOIN sys.tables t ON t.object_id = i.object_id
WHERE t.name IN (N''CustomFieldDefinitions'', N''CustomFieldValues'')
  AND i.name IS NOT NULL
ORDER BY t.name, i.name;';

-- Grid 3: the checks you must see, as one row each, all ''OK''.
EXEC sp_executesql N'
SELECT N''NumberValue is DECIMAL(18,4)'' AS [Check],
       CASE WHEN EXISTS (SELECT 1 FROM sys.columns
                         WHERE object_id = OBJECT_ID(N''dbo.CustomFieldValues'')
                           AND name = N''NumberValue'' AND precision = 18 AND scale = 4)
            THEN N''OK'' ELSE N''MISMATCH'' END AS [Result]
UNION ALL
SELECT N''DateValue is DATE'',
       CASE WHEN EXISTS (SELECT 1 FROM sys.columns c JOIN sys.types ty ON ty.user_type_id = c.user_type_id
                         WHERE c.object_id = OBJECT_ID(N''dbo.CustomFieldValues'')
                           AND c.name = N''DateValue'' AND ty.name = N''date'')
            THEN N''OK'' ELSE N''MISMATCH'' END
UNION ALL
SELECT N''FK to CustomFieldDefinitions'',
       CASE WHEN EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N''FK_CustomFieldValues_Definition'')
            THEN N''OK'' ELSE N''MISSING'' END
UNION ALL
SELECT N''Rows in CustomFieldDefinitions (0 on first run)'',
       CAST((SELECT COUNT(*) FROM dbo.CustomFieldDefinitions) AS NVARCHAR(20));';

IF @Commit = 1
BEGIN
    COMMIT;
    PRINT '075: COMMITTED.';
END
ELSE
BEGIN
    ROLLBACK;
    PRINT '075: rolled back (dry run). Set @Commit = 1 to keep it.';
END
