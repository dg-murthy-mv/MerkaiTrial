/* =====================================================================
   MerkaiTrial — ROLE SPLIT: RECOVER AND COMPLETE

   WHAT WENT WRONG
     A pre-existing unique index, UQ_Roles_Name, enforces globally unique
     role names. That is the OLD design and it directly forbids one copy
     of "sales_manager" per tenant, so the insert failed with Msg 2601.

     But the script had no transaction, so the LAST statement still ran:
     the three original custom roles are now IsDeleted = 1 while
     UserRoles still points at them and no copies exist. Users holding
     those roles would sign in with NO permissions.

   THIS SCRIPT
     1. Restores the three originals (undoes the partial damage).
     2. Drops UQ_Roles_Name — superseded by UX_Roles_Tenant_Name_Live
        on (TenantId, Name), created in migration 002.
     3. Runs the split properly, inside a transaction this time.
     4. Verifies.

   BACK UP FIRST.
   Run with nothing selected (Ctrl+A, F5).
   ===================================================================== */

USE MerkaiTrial;
GO

SET NOCOUNT ON;

/* ---------------------------------------------------------------------
   0. What unique indexes exist on Roles? Read this before continuing —
      if there is another global-uniqueness index I have not seen, it
      will block the insert the same way.
   --------------------------------------------------------------------- */
SELECT i.name AS IndexName,
       i.is_unique,
       ISNULL(i.filter_definition, '(unfiltered)') AS FilterDefinition,
       STRING_AGG(c.name, ', ') WITHIN GROUP (ORDER BY ic.key_ordinal) AS KeyColumns
FROM sys.indexes i
JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
JOIN sys.columns c        ON c.object_id  = i.object_id AND c.column_id = ic.column_id
WHERE i.object_id = OBJECT_ID('dbo.Roles') AND i.type > 0
GROUP BY i.name, i.is_unique, i.filter_definition
ORDER BY i.name;
GO


/* ---------------------------------------------------------------------
   1. RESTORE the three originals retired by the failed run.

      Safe and targeted: only rows stamped by migration-001 that have no
      tenant-scoped replacement. If the split later succeeds, step 3
      retires them again properly.
   --------------------------------------------------------------------- */
UPDATE dbo.Roles
   SET IsDeleted    = 0,
       UpdatedAtUtc = SYSUTCDATETIME(),
       UpdatedBy    = 'recovery'
 WHERE IsDeleted = 1
   AND UpdatedBy = 'migration-001'
   AND TenantId IS NULL;

SELECT 'originals restored' AS Step, @@ROWCOUNT AS Value;
GO


/* ---------------------------------------------------------------------
   2. DROP the global-uniqueness index.

      UQ_Roles_Name enforces one "sales_manager" in the entire database.
      That was correct when roles were shared by every tenant. It is
      exactly what must go now that each tenant owns its own copy.

      UX_Roles_Tenant_Name_Live on (TenantId, Name) WHERE IsDeleted = 0
      replaces it: unique WITHIN a tenant, which is what you actually
      want, and it still stops one tenant creating two roles of the same
      name.
   --------------------------------------------------------------------- */
IF EXISTS (SELECT 1 FROM sys.indexes
            WHERE name = 'UQ_Roles_Name' AND object_id = OBJECT_ID('dbo.Roles'))
BEGIN
    DROP INDEX UQ_Roles_Name ON dbo.Roles;
    PRINT 'UQ_Roles_Name dropped.';
END
ELSE
    PRINT 'UQ_Roles_Name not present — nothing to drop.';

/* The replacement must exist before continuing. Migration 002 created
   it; this is a safety net in case that step was skipped. */
IF NOT EXISTS (SELECT 1 FROM sys.indexes
                WHERE name = 'UX_Roles_Tenant_Name_Live' AND object_id = OBJECT_ID('dbo.Roles'))
BEGIN
    CREATE UNIQUE INDEX UX_Roles_Tenant_Name_Live
        ON dbo.Roles (TenantId, Name)
        WHERE IsDeleted = 0;
    PRINT 'UX_Roles_Tenant_Name_Live created.';
END
GO


/* ---------------------------------------------------------------------
   3. THE SPLIT — in a transaction, so a failure leaves nothing behind.
   --------------------------------------------------------------------- */
BEGIN TRANSACTION;

DECLARE @map TABLE (OldRoleId UNIQUEIDENTIFIER, TenantId UNIQUEIDENTIFIER, NewRoleId UNIQUEIDENTIFIER);

INSERT INTO @map (OldRoleId, TenantId, NewRoleId)
SELECT DISTINCT r.Id, u.TenantId, NEWID()
FROM dbo.Roles r
JOIN dbo.UserRoles ur ON ur.RoleId = r.Id
JOIN dbo.Users     u  ON u.Id      = ur.UserId
WHERE r.IsSystemRole = 0 AND r.IsDeleted = 0 AND u.IsDeleted = 0;

SELECT 'map rows (expect 12)' AS Step, COUNT(*) AS Value FROM @map;

INSERT INTO dbo.Roles
    (Id, TenantId, Name, DisplayName, Description,
     IsSystemRole, Permissions, CreatedAtUtc, CreatedBy, IsDeleted)
SELECT m.NewRoleId, m.TenantId, r.Name, r.DisplayName, r.Description,
       0, r.Permissions, SYSUTCDATETIME(), 'migration-001', 0
FROM @map m
JOIN dbo.Roles r ON r.Id = m.OldRoleId;

SELECT 'roles inserted (expect 12)' AS Step, @@ROWCOUNT AS Value;

/* Repoint each tenant's assignments at that tenant's own copy. Must run
   BEFORE the originals are retired, or the FK has nowhere valid to point. */
UPDATE ur
   SET ur.RoleId = m.NewRoleId
  FROM dbo.UserRoles ur
  JOIN dbo.Users u ON u.Id = ur.UserId
  JOIN @map      m ON m.OldRoleId = ur.RoleId AND m.TenantId = u.TenantId;

SELECT 'assignments repointed (expect 12)' AS Step, @@ROWCOUNT AS Value;

UPDATE dbo.Roles
   SET IsDeleted = 1, UpdatedAtUtc = SYSUTCDATETIME(), UpdatedBy = 'migration-001'
 WHERE Id IN (SELECT DISTINCT OldRoleId FROM @map);

SELECT 'originals retired (expect 3)' AS Step, @@ROWCOUNT AS Value;

COMMIT TRANSACTION;
GO


/* ---------------------------------------------------------------------
   4. VERIFY — all four must be as stated.
   --------------------------------------------------------------------- */
SELECT
    (SELECT COUNT(*) FROM dbo.Roles WHERE TenantId IS NOT NULL AND IsDeleted = 0) AS TenantScoped_expect_12,
    (SELECT COUNT(*) FROM dbo.Roles WHERE TenantId IS NULL     AND IsDeleted = 0) AS Global_expect_1,
    (SELECT COUNT(*) FROM dbo.Roles WHERE IsDeleted = 1)                          AS Retired_expect_3;

/* No live custom role may span tenants. EXPECT ZERO ROWS. */
SELECT r.Id, r.Name, r.TenantId, COUNT(DISTINCT u.TenantId) AS TenantsUsingIt
FROM dbo.Roles r
JOIN dbo.UserRoles ur ON ur.RoleId = r.Id
JOIN dbo.Users u      ON u.Id = ur.UserId AND u.IsDeleted = 0
WHERE r.IsDeleted = 0 AND r.IsSystemRole = 0
GROUP BY r.Id, r.Name, r.TenantId
HAVING COUNT(DISTINCT u.TenantId) > 1;

/* No assignment may point at a retired role. EXPECT ZERO ROWS.
   Any row here means a user has lost their permissions. */
SELECT ur.UserId, ur.RoleId, r.Name
FROM dbo.UserRoles ur
JOIN dbo.Roles r ON r.Id = ur.RoleId
WHERE r.IsDeleted = 1;

/* Full picture. */
SELECT r.Name, r.TenantId, t.Name AS TenantName, r.IsSystemRole, r.IsDeleted,
       (SELECT COUNT(*) FROM dbo.UserRoles ur WHERE ur.RoleId = r.Id) AS AssignedUsers
FROM dbo.Roles r
LEFT JOIN dbo.Tenants t ON t.Id = r.TenantId
ORDER BY r.IsDeleted, r.Name, t.Name;
