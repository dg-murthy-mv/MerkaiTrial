/* =====================================================================
   MerkaiTrial — schema extraction for review

   Run each query separately against the MerkaiTrial database and paste the
   grid back. Select all (Ctrl+A in the results grid) → right-click → Copy
   with Headers.

   Query 2 is the one that matters most for the trial work — run it first
   if you only run one.
   ===================================================================== */


/* ---------------------------------------------------------------------
   1. COMPACT COLUMN DUMP — one row per table
   --------------------------------------------------------------------- */
SELECT
    t.name AS TableName,
    STRING_AGG(
        CAST(c.name + ' ' + ty.name
            + CASE
                WHEN ty.name IN ('nvarchar','varchar','char','nchar','varbinary','binary')
                    THEN '(' + CASE WHEN c.max_length = -1 THEN 'max'
                                    WHEN ty.name LIKE 'n%' THEN CAST(c.max_length/2 AS varchar(10))
                                    ELSE CAST(c.max_length AS varchar(10)) END + ')'
                WHEN ty.name IN ('decimal','numeric')
                    THEN '(' + CAST(c.precision AS varchar(5)) + ',' + CAST(c.scale AS varchar(5)) + ')'
                ELSE '' END
            + CASE WHEN c.is_nullable = 1 THEN ' NULL' ELSE ' NOT NULL' END
        AS NVARCHAR(MAX)),
        ' | '
    ) WITHIN GROUP (ORDER BY c.column_id) AS Columns
FROM sys.tables t
JOIN sys.columns c ON c.object_id = t.object_id
JOIN sys.types  ty ON ty.user_type_id = c.user_type_id
WHERE t.is_ms_shipped = 0
GROUP BY t.name
ORDER BY t.name;


/* ---------------------------------------------------------------------
   2. TENANT ISOLATION AUDIT  ← the important one

   For every table: does it have a TenantId column, is it nullable, and is
   there an index that LEADS with TenantId?

   This drives phase 2 directly. Global query filters can only be applied
   to entities that carry TenantId, and a filter without a leading-TenantId
   index turns every query into a scan once several tenants share a table.

   A NULLABLE TenantId on a tenant-scoped table is a bug waiting to happen:
   'WHERE TenantId = @x' silently excludes NULL rows, so orphaned records
   become invisible rather than erroring.
   --------------------------------------------------------------------- */
SELECT
    t.name AS TableName,
    CASE WHEN tc.name IS NULL THEN 'NO TenantId' ELSE 'has TenantId' END AS TenantColumn,
    CASE WHEN tc.is_nullable = 1 THEN 'NULLABLE  <-- check' ELSE '' END AS Nullability,
    ISNULL((
        SELECT STRING_AGG(i.name, ', ')
        FROM sys.indexes i
        JOIN sys.index_columns ic
              ON ic.object_id = i.object_id
             AND ic.index_id  = i.index_id
             AND ic.key_ordinal = 1
        WHERE i.object_id = t.object_id
          AND ic.column_id = tc.column_id
    ), '(none)') AS IndexesLeadingWithTenantId,
    (SELECT SUM(p.rows) FROM sys.partitions p
      WHERE p.object_id = t.object_id AND p.index_id IN (0,1)) AS ApproxRows
FROM sys.tables t
LEFT JOIN sys.columns tc
       ON tc.object_id = t.object_id AND tc.name = 'TenantId'
WHERE t.is_ms_shipped = 0
ORDER BY
    CASE WHEN tc.name IS NULL THEN 0 ELSE 1 END,   -- tables without TenantId first
    t.name;


/* ---------------------------------------------------------------------
   3. FOREIGN KEYS
   Cross-tenant leaks often hide here: a FK that lets a row in tenant A
   reference a parent in tenant B. FKs cannot express "same tenant", so
   these are the joins to check by hand.
   --------------------------------------------------------------------- */
SELECT
    fk.name           AS ForeignKey,
    tp.name           AS ParentTable,
    cp.name           AS ParentColumn,
    tr.name           AS ReferencedTable,
    cr.name           AS ReferencedColumn,
    fk.delete_referential_action_desc AS OnDelete
FROM sys.foreign_keys fk
JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
JOIN sys.tables  tp ON tp.object_id = fk.parent_object_id
JOIN sys.columns cp ON cp.object_id = tp.object_id AND cp.column_id = fkc.parent_column_id
JOIN sys.tables  tr ON tr.object_id = fk.referenced_object_id
JOIN sys.columns cr ON cr.object_id = tr.object_id AND cr.column_id = fkc.referenced_column_id
ORDER BY tp.name, fk.name;


/* ---------------------------------------------------------------------
   4. INDEXES
   --------------------------------------------------------------------- */
SELECT
    t.name AS TableName,
    i.name AS IndexName,
    i.type_desc AS IndexType,
    CASE WHEN i.is_unique = 1 THEN 'UNIQUE' ELSE '' END AS Uniqueness,
    ISNULL(i.filter_definition, '') AS FilterDefinition,
    STRING_AGG(CASE WHEN ic.is_included_column = 0 THEN c.name END, ', ')
        WITHIN GROUP (ORDER BY ic.key_ordinal) AS KeyColumns,
    STRING_AGG(CASE WHEN ic.is_included_column = 1 THEN c.name END, ', ') AS IncludedColumns
FROM sys.indexes i
JOIN sys.tables t        ON t.object_id = i.object_id
JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
JOIN sys.columns c        ON c.object_id = i.object_id AND c.column_id = ic.column_id
WHERE t.is_ms_shipped = 0 AND i.type > 0
GROUP BY t.name, i.name, i.type_desc, i.is_unique, i.filter_definition
ORDER BY t.name, i.name;


/* ---------------------------------------------------------------------
   5. DID MIGRATION 001 APPLY CLEANLY?
   Every row should say OK.
   --------------------------------------------------------------------- */
SELECT 'Users.PasswordHash'     AS Item, CASE WHEN COL_LENGTH('dbo.Users','PasswordHash')     IS NULL THEN 'MISSING' ELSE 'OK' END AS Status
UNION ALL SELECT 'Users.SecurityStamp',   CASE WHEN COL_LENGTH('dbo.Users','SecurityStamp')   IS NULL THEN 'MISSING' ELSE 'OK' END
UNION ALL SELECT 'Users.IsSuperAdmin',    CASE WHEN COL_LENGTH('dbo.Users','IsSuperAdmin')    IS NULL THEN 'MISSING' ELSE 'OK' END
UNION ALL SELECT 'Users.LockoutEndUtc',   CASE WHEN COL_LENGTH('dbo.Users','LockoutEndUtc')   IS NULL THEN 'MISSING' ELSE 'OK' END
UNION ALL SELECT 'Roles.TenantId',        CASE WHEN COL_LENGTH('dbo.Roles','TenantId')        IS NULL THEN 'MISSING' ELSE 'OK' END
UNION ALL SELECT 'Tenants.TrialStatus',   CASE WHEN COL_LENGTH('dbo.Tenants','TrialStatus')   IS NULL THEN 'MISSING' ELSE 'OK' END
UNION ALL SELECT 'UserTokens table',      CASE WHEN OBJECT_ID('dbo.UserTokens','U')           IS NULL THEN 'MISSING' ELSE 'OK' END
UNION ALL SELECT 'Users with a password', CAST(COUNT(*) AS varchar(10)) FROM dbo.Users WHERE PasswordHash IS NOT NULL AND IsDeleted = 0
UNION ALL SELECT 'SuperAdmin accounts',   CAST(COUNT(*) AS varchar(10)) FROM dbo.Users WHERE IsSuperAdmin = 1 AND IsDeleted = 0;


/* ---------------------------------------------------------------------
   6. ROLE SPLIT VERIFICATION
   After migration 001, no live custom role should be shared. Every row
   here is a role still assigned to users in more than one tenant — the
   result set should be EMPTY.
   --------------------------------------------------------------------- */
SELECT
    r.Id, r.Name, r.TenantId, r.IsSystemRole,
    COUNT(DISTINCT u.TenantId) AS TenantsUsingIt
FROM dbo.Roles r
JOIN dbo.UserRoles ur ON ur.RoleId = r.Id
JOIN dbo.Users u      ON u.Id = ur.UserId AND u.IsDeleted = 0
WHERE r.IsDeleted = 0 AND r.IsSystemRole = 0
GROUP BY r.Id, r.Name, r.TenantId, r.IsSystemRole
HAVING COUNT(DISTINCT u.TenantId) > 1;


/* ---------------------------------------------------------------------
   7. PLAN vs TENANTSETTINGS DRIFT
   The Manila dashboard showed 11/1000 leads while the Professional plan
   defines 2000. Every row returned is a tenant whose enforced limits
   disagree with its own plan.
   --------------------------------------------------------------------- */
SELECT
    t.Name AS Tenant, t.Plan,
    ts.MaxUsers AS SettingsUsers, p.MaxUsers AS PlanUsers,
    ts.MaxLeads AS SettingsLeads, p.MaxLeads AS PlanLeads,
    ts.MaxDeals AS SettingsDeals, p.MaxDeals AS PlanDeals
FROM dbo.Tenants t
LEFT JOIN dbo.TenantSettings ts ON ts.TenantId = t.Id
LEFT JOIN dbo.Plans p           ON p.Name = t.Plan
WHERE t.IsDeleted = 0
  AND (ts.Id IS NULL
    OR ts.MaxUsers <> p.MaxUsers
    OR ts.MaxLeads <> p.MaxLeads
    OR ts.MaxDeals <> p.MaxDeals);
