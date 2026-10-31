/* =====================================================================
   MerkaiTrial — Migration 001, STEP 5 ONLY (corrected)

   The original step 5 failed to compile:
       PRINT 'Step 5 OK — split ' + CAST((SELECT COUNT(*) FROM @map) ...)
   PRINT takes a scalar expression; a subquery is not allowed there.

   Because Msg 1046 is a COMPILE-time error, the batch never executed —
   TRY/CATCH cannot catch compile errors raised in its own batch. So no
   part of the role split was applied and there is nothing to undo.

   Steps 1, 2, 3, 4, 6 and 7 are already applied. Run only this.
   ===================================================================== */

SET NOCOUNT ON;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    IF NOT EXISTS (SELECT 1 FROM dbo.Roles WHERE TenantId IS NOT NULL)
    BEGIN
        DECLARE @map TABLE
        (
            OldRoleId UNIQUEIDENTIFIER,
            TenantId  UNIQUEIDENTIFIER,
            NewRoleId UNIQUEIDENTIFIER
        );

        DECLARE @copies INT;   -- computed into a variable, never inlined into PRINT

        /* One new role row per (shared custom role x tenant that uses it). */
        INSERT INTO @map (OldRoleId, TenantId, NewRoleId)
        SELECT DISTINCT r.Id, u.TenantId, NEWID()
        FROM dbo.Roles r
        JOIN dbo.UserRoles ur ON ur.RoleId = r.Id
        JOIN dbo.Users     u  ON u.Id      = ur.UserId
        WHERE r.IsSystemRole = 0
          AND r.IsDeleted    = 0
          AND u.IsDeleted    = 0;

        SELECT @copies = COUNT(*) FROM @map;

        INSERT INTO dbo.Roles
            (Id, TenantId, Name, DisplayName, Description,
             IsSystemRole, Permissions, CreatedAtUtc, CreatedBy, IsDeleted)
        SELECT m.NewRoleId, m.TenantId, r.Name, r.DisplayName, r.Description,
               0, r.Permissions, SYSUTCDATETIME(), 'migration-001', 0
        FROM @map m
        JOIN dbo.Roles r ON r.Id = m.OldRoleId;

        /* Point each tenant's assignments at that tenant's own copy. */
        UPDATE ur
           SET ur.RoleId = m.NewRoleId
          FROM dbo.UserRoles ur
          JOIN dbo.Users u ON u.Id = ur.UserId
          JOIN @map      m ON m.OldRoleId = ur.RoleId
                          AND m.TenantId  = u.TenantId;

        /* Retire the shared originals. Soft delete, so the old rows remain
           auditable and the filtered unique index ignores them. */
        UPDATE dbo.Roles
           SET IsDeleted = 1, UpdatedAtUtc = SYSUTCDATETIME(), UpdatedBy = 'migration-001'
         WHERE Id IN (SELECT DISTINCT OldRoleId FROM @map);

        PRINT 'Step 5 OK — created ' + CAST(@copies AS varchar(10))
              + ' tenant-specific role copies.';
    END
    ELSE
        PRINT 'Step 5 SKIPPED — tenant-scoped roles already exist.';

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT 'Step 5 FAILED: ' + ERROR_MESSAGE();
END CATCH
GO


/* =====================================================================
   VERIFY — run after the above
   ===================================================================== */

/* A. Every live custom role must belong to exactly one tenant.
      EXPECTED: zero rows. Any row here is a role still shared between
      tenants, which is the bug this step exists to remove. */
SELECT r.Id, r.Name, r.TenantId, COUNT(DISTINCT u.TenantId) AS TenantsUsingIt
FROM dbo.Roles r
JOIN dbo.UserRoles ur ON ur.RoleId = r.Id
JOIN dbo.Users u      ON u.Id = ur.UserId AND u.IsDeleted = 0
WHERE r.IsDeleted = 0 AND r.IsSystemRole = 0
GROUP BY r.Id, r.Name, r.TenantId
HAVING COUNT(DISTINCT u.TenantId) > 1;


/* B. What the Roles table looks like now.
      EXPECTED: system roles with TenantId NULL, and one copy of each
      custom role per tenant that had users assigned to it. */
SELECT
    r.Name,
    r.TenantId,
    t.Name AS TenantName,
    r.IsSystemRole,
    r.IsDeleted,
    (SELECT COUNT(*) FROM dbo.UserRoles ur WHERE ur.RoleId = r.Id) AS AssignedUsers
FROM dbo.Roles r
LEFT JOIN dbo.Tenants t ON t.Id = r.TenantId
ORDER BY r.IsDeleted, r.Name, t.Name;


/* C. No assignment may point at a retired role.
      EXPECTED: zero rows. */
SELECT ur.UserId, ur.RoleId, r.Name
FROM dbo.UserRoles ur
JOIN dbo.Roles r ON r.Id = ur.RoleId
WHERE r.IsDeleted = 1;
