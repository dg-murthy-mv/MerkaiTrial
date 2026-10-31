/* =====================================================================
   101c_AuditLogs_Index_TenantFromEmail.sql

   1. IX_AuditLogs_Tenant_Entity — the timeline's index
      A lead's or deal's timeline asks AuditLogs for one record's history
      (tenant + EntityType + EntityId, often one Action). The only index was
      (TenantId, CreatedAtUtc), so SQL Server read through far more of the
      table than it needed. In the last test run one of those reads waited
      15.2 s on the disk (PAGEIOLATCH_SH) and a lead page took over 20 s to
      open, which failed a test. The E2E database's AuditLogs grows with
      every run, so this got slower each time. Production would see the
      same as its history grows.

   2. Tenants.FromEmail NOT NULL
      SchemaCheck reported it: the app treats a workspace's From address as
      always present (TenantConfiguration: IsRequired), the column allowed
      NULL. A workspace row with NULL there would crash whenever it is read.
      The column is tightened ONLY if no workspace has it empty; otherwise
      grid 2 lists those workspaces and the column is left alone. Fill them
      in (Settings → Company Profile, or the super admin's workspace page)
      and run this again.

   ─────────────────────────────────────────────────────────────────────
   RUN IT ON BOTH DATABASES — your development database AND
   MerkaiTrial_E2E. Check the database dropdown in the toolbar first.
   ─────────────────────────────────────────────────────────────────────

   HOW TO RUN
     1. Run as-is (@Commit = 0) — rolled back. Read the grids.
     2. Set @Commit = 1 and run again. Messages end with COMMITTED.
        Building the index on a big AuditLogs table can take a minute.
     3. Switch the dropdown to the other database and do 1–2 again.

   SAFE TO RE-RUN. Each step is skipped when already done.
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit BIT = 0;      -- flip to 1 to commit

IF OBJECT_ID(N'dbo.AuditLogs', N'U') IS NULL OR OBJECT_ID(N'dbo.Tenants', N'U') IS NULL
BEGIN
    RAISERROR('dbo.AuditLogs / dbo.Tenants are missing — is this the Merkai database?', 16, 1);
    RETURN;
END;

SELECT DB_NAME() AS [Database],
       (SELECT COUNT_BIG(*) FROM dbo.AuditLogs) AS [AuditLogs rows],
       CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.AuditLogs') AND name = N'IX_AuditLogs_Tenant_Entity')
            THEN N'index already there' ELSE N'→ index will be created' END AS [IX_AuditLogs_Tenant_Entity],
       CASE (SELECT is_nullable FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.Tenants') AND name = N'FromEmail')
            WHEN 0 THEN N'already NOT NULL'
            ELSE CASE WHEN EXISTS (SELECT 1 FROM dbo.Tenants WHERE FromEmail IS NULL)
                      THEN N'NOT changed — some workspaces have it empty (grid 2)'
                      ELSE N'→ will be NOT NULL' END END AS [Tenants.FromEmail];

-- Workspaces with no From address (only shown when there are any)
IF EXISTS (SELECT 1 FROM dbo.Tenants WHERE FromEmail IS NULL)
    SELECT Id, Name, CreatedAtUtc FROM dbo.Tenants WHERE FromEmail IS NULL ORDER BY Name;

BEGIN TRY
    BEGIN TRANSACTION;

    -- ── 1. The index ──
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.AuditLogs') AND name = N'IX_AuditLogs_Tenant_Entity')
    BEGIN
        PRINT 'Creating IX_AuditLogs_Tenant_Entity...';
        CREATE NONCLUSTERED INDEX IX_AuditLogs_Tenant_Entity
            ON dbo.AuditLogs (TenantId, EntityType, EntityId, Action)
            INCLUDE (CreatedAtUtc, [By]);
    END;

    -- ── 2. Tenants.FromEmail ──
    DECLARE @nullable BIT, @type NVARCHAR(200), @sql NVARCHAR(MAX);
    SELECT @nullable = c.is_nullable,
           @type = ty.name + N'(' + CASE WHEN c.max_length = -1 THEN N'max'
                                         WHEN ty.name IN (N'nvarchar', N'nchar') THEN CAST(c.max_length / 2 AS NVARCHAR(10))
                                         ELSE CAST(c.max_length AS NVARCHAR(10)) END + N')'
                 + CASE WHEN c.collation_name IS NOT NULL THEN N' COLLATE ' + c.collation_name ELSE N'' END
    FROM sys.columns c JOIN sys.types ty ON ty.user_type_id = c.user_type_id
    WHERE c.object_id = OBJECT_ID(N'dbo.Tenants') AND c.name = N'FromEmail';

    IF @nullable = 1 AND NOT EXISTS (SELECT 1 FROM dbo.Tenants WHERE FromEmail IS NULL)
    BEGIN
        IF EXISTS (SELECT 1 FROM sys.index_columns ic
                   WHERE ic.object_id = OBJECT_ID(N'dbo.Tenants')
                     AND ic.column_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.Tenants'), N'FromEmail', 'ColumnId'))
        BEGIN
            PRINT 'Tenants.FromEmail is used by an index — left as it is. Tell Claude the index name.';
        END
        ELSE
        BEGIN
            SET @sql = N'ALTER TABLE dbo.Tenants ALTER COLUMN FromEmail ' + @type + N' NOT NULL;';
            PRINT @sql; EXEC sys.sp_executesql @sql;
        END;
    END;

    -- ── Check ──
    SELECT DB_NAME() AS [Database],
           CASE WHEN NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.AuditLogs') AND name = N'IX_AuditLogs_Tenant_Entity')
                     THEN N'PROBLEM — the index is missing'
                WHEN (SELECT is_nullable FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.Tenants') AND name = N'FromEmail') = 1
                     THEN N'OK for the index; Tenants.FromEmail still allows NULL (see above)'
                ELSE N'OK' END AS [Result];

    IF @Commit = 1
    BEGIN
        COMMIT TRANSACTION;
        PRINT 'COMMITTED on ' + DB_NAME() + '.';
    END
    ELSE
    BEGIN
        ROLLBACK TRANSACTION;
        PRINT 'ROLLED BACK (dry run). Set @Commit = 1 to apply.';
    END;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT 'FAILED, nothing was changed: ' + ERROR_MESSAGE();
    THROW;
END CATCH;
