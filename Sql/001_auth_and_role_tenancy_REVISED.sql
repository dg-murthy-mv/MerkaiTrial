/* =====================================================================
   MerkaiTrial — Migration 001 (REVISED)
   Real authentication + per-tenant roles.

   FIXES OVER THE FIRST VERSION
   ----------------------------
   1. The transaction no longer spans GO batches. Each batch that changes
      data now owns its own transaction. Previously a failure in a middle
      batch rolled back, the remaining batches ran anyway outside any
      transaction, and the final COMMIT failed with "no corresponding
      BEGIN TRANSACTION" — leaving a half-applied migration.

   2. Duplicate live emails are now DETECTED AND REPORTED before the
      unique index is attempted, instead of failing on a duplicate-key
      error partway through.

   RUN AGAINST THE MerkaiTrial DATABASE ONLY. Back up first.
   Idempotent: safe to re-run.
   ===================================================================== */

SET NOCOUNT ON;
GO

/* ---------------------------------------------------------------------
   0. PRE-FLIGHT — stop before changing anything if the data can't take it
   --------------------------------------------------------------------- */
IF EXISTS (
    SELECT 1 FROM dbo.Users
    WHERE IsDeleted = 0
    GROUP BY Email
    HAVING COUNT(*) > 1)
BEGIN
    /* Login resolves a user by email alone — at the sign-in screen nobody
       knows their tenant id. That requires email to be unique across live
       users. Fix the duplicates (rename the demo accounts, or soft-delete
       the ones you don't need) and re-run.

       Run this to see them:
           SELECT Email, COUNT(*) FROM dbo.Users WHERE IsDeleted = 0
           GROUP BY Email HAVING COUNT(*) > 1;                            */
    RAISERROR (
        'ABORTED: duplicate emails exist among live users. Resolve them before migrating — see the comment above this check.',
        16, 1);
    SET NOEXEC ON;   -- stop the rest of the script from executing
END
GO

/* ---------------------------------------------------------------------
   1. Users — credential columns
   --------------------------------------------------------------------- */
BEGIN TRY
    BEGIN TRANSACTION;

    IF COL_LENGTH('dbo.Users', 'PasswordHash') IS NULL
        ALTER TABLE dbo.Users ADD PasswordHash NVARCHAR(500) NULL;

    IF COL_LENGTH('dbo.Users', 'SecurityStamp') IS NULL
        ALTER TABLE dbo.Users ADD SecurityStamp NVARCHAR(100) NULL;

    IF COL_LENGTH('dbo.Users', 'MustChangePassword') IS NULL
        ALTER TABLE dbo.Users ADD MustChangePassword BIT NOT NULL
            CONSTRAINT DF_Users_MustChangePassword DEFAULT (0);

    IF COL_LENGTH('dbo.Users', 'EmailConfirmedAtUtc') IS NULL
        ALTER TABLE dbo.Users ADD EmailConfirmedAtUtc DATETIME2(3) NULL;

    IF COL_LENGTH('dbo.Users', 'FailedLoginCount') IS NULL
        ALTER TABLE dbo.Users ADD FailedLoginCount INT NOT NULL
            CONSTRAINT DF_Users_FailedLoginCount DEFAULT (0);

    IF COL_LENGTH('dbo.Users', 'LockoutEndUtc') IS NULL
        ALTER TABLE dbo.Users ADD LockoutEndUtc DATETIME2(3) NULL;

    IF COL_LENGTH('dbo.Users', 'LastPasswordChangeUtc') IS NULL
        ALTER TABLE dbo.Users ADD LastPasswordChangeUtc DATETIME2(3) NULL;

    IF COL_LENGTH('dbo.Users', 'IsSuperAdmin') IS NULL
        ALTER TABLE dbo.Users ADD IsSuperAdmin BIT NOT NULL
            CONSTRAINT DF_Users_IsSuperAdmin DEFAULT (0);

    COMMIT TRANSACTION;
    PRINT 'Step 1 OK — Users credential columns.';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT 'Step 1 FAILED: ' + ERROR_MESSAGE();
    SET NOEXEC ON;
END CATCH
GO

/* Unique email among live users. The pre-flight check above already
   confirmed this can succeed. */
BEGIN TRY
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Users_Email_Live')
        CREATE UNIQUE INDEX UX_Users_Email_Live
            ON dbo.Users (Email)
            WHERE IsDeleted = 0;

    PRINT 'Step 2 OK — unique live email index.';
END TRY
BEGIN CATCH
    PRINT 'Step 2 FAILED: ' + ERROR_MESSAGE();
    SET NOEXEC ON;
END CATCH
GO

/* ---------------------------------------------------------------------
   3. UserTokens — invite + password reset
   Only the SHA-256 hash of the token is stored, so a leaked database
   yields no usable links.
   --------------------------------------------------------------------- */
BEGIN TRY
    IF OBJECT_ID('dbo.UserTokens', 'U') IS NULL
    BEGIN
        CREATE TABLE dbo.UserTokens
        (
            Id            UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_UserTokens PRIMARY KEY,
            UserId        UNIQUEIDENTIFIER NOT NULL,
            TenantId      UNIQUEIDENTIFIER NOT NULL,
            TokenHash     VARBINARY(32)    NOT NULL,
            Purpose       NVARCHAR(40)     NOT NULL,
            ExpiresAtUtc  DATETIME2(3)     NOT NULL,
            ConsumedAtUtc DATETIME2(3)     NULL,
            CreatedAtUtc  DATETIME2(3)     NOT NULL
                CONSTRAINT DF_UserTokens_Created DEFAULT (SYSUTCDATETIME()),
            CreatedBy     NVARCHAR(200)    NULL,
            CONSTRAINT FK_UserTokens_Users FOREIGN KEY (UserId) REFERENCES dbo.Users (Id)
        );

        CREATE UNIQUE INDEX UX_UserTokens_TokenHash ON dbo.UserTokens (TokenHash);
        CREATE INDEX IX_UserTokens_UserId_Purpose  ON dbo.UserTokens (UserId, Purpose);
    END

    PRINT 'Step 3 OK — UserTokens.';
END TRY
BEGIN CATCH
    PRINT 'Step 3 FAILED: ' + ERROR_MESSAGE();
    SET NOEXEC ON;
END CATCH
GO

/* ---------------------------------------------------------------------
   4. Roles — add TenantId
   Must be its own batch: the column has to exist and be visible to the
   parser before step 5 can reference it.
   --------------------------------------------------------------------- */
BEGIN TRY
    IF COL_LENGTH('dbo.Roles', 'TenantId') IS NULL
        ALTER TABLE dbo.Roles ADD TenantId UNIQUEIDENTIFIER NULL;

    PRINT 'Step 4 OK — Roles.TenantId.';
END TRY
BEGIN CATCH
    PRINT 'Step 4 FAILED: ' + ERROR_MESSAGE();
    SET NOEXEC ON;
END CATCH
GO

/* ---------------------------------------------------------------------
   5. Split shared custom roles into one copy per tenant

   Today a single "sales_manager" row is assigned to users across all four
   tenants — the Roles screen showed 4 users on every role, one per tenant.
   A tenant admin editing it rewrites every other tenant's permissions.

   This clones each shared custom role per tenant, repoints that tenant's
   UserRoles at its own copy, and soft-deletes the shared original.
   System roles (IsSystemRole = 1) stay global and read-only.
   --------------------------------------------------------------------- */
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

        INSERT INTO @map (OldRoleId, TenantId, NewRoleId)
        SELECT DISTINCT r.Id, u.TenantId, NEWID()
        FROM dbo.Roles r
        JOIN dbo.UserRoles ur ON ur.RoleId = r.Id
        JOIN dbo.Users     u  ON u.Id      = ur.UserId
        WHERE r.IsSystemRole = 0
          AND r.IsDeleted    = 0
          AND u.IsDeleted    = 0;

        INSERT INTO dbo.Roles
            (Id, TenantId, Name, DisplayName, Description,
             IsSystemRole, Permissions, CreatedAtUtc, CreatedBy, IsDeleted)
        SELECT m.NewRoleId, m.TenantId, r.Name, r.DisplayName, r.Description,
               0, r.Permissions, SYSUTCDATETIME(), 'migration-001', 0
        FROM @map m
        JOIN dbo.Roles r ON r.Id = m.OldRoleId;

        UPDATE ur
           SET ur.RoleId = m.NewRoleId
          FROM dbo.UserRoles ur
          JOIN dbo.Users u ON u.Id = ur.UserId
          JOIN @map      m ON m.OldRoleId = ur.RoleId
                          AND m.TenantId  = u.TenantId;

        UPDATE dbo.Roles
           SET IsDeleted = 1, UpdatedAtUtc = SYSUTCDATETIME(), UpdatedBy = 'migration-001'
         WHERE Id IN (SELECT DISTINCT OldRoleId FROM @map);

        PRINT 'Step 5 OK — split ' + CAST((SELECT COUNT(*) FROM @map) AS varchar(10))
              + ' tenant-specific role copies.';
    END
    ELSE
        PRINT 'Step 5 SKIPPED — tenant-scoped roles already exist.';

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT 'Step 5 FAILED: ' + ERROR_MESSAGE();
    SET NOEXEC ON;
END CATCH
GO

/* A tenant cannot have two live roles with the same Name. System roles
   (TenantId NULL) are unique by Name globally — SQL Server treats NULL as
   a comparable value in a unique index, which is what we want. */
BEGIN TRY
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Roles_Tenant_Name_Live')
        CREATE UNIQUE INDEX UX_Roles_Tenant_Name_Live
            ON dbo.Roles (TenantId, Name)
            WHERE IsDeleted = 0;

    PRINT 'Step 6 OK — unique (TenantId, Name) role index.';
END TRY
BEGIN CATCH
    /* If this fails, two live roles share a name within one tenant. Find them:
         SELECT TenantId, Name, COUNT(*) FROM dbo.Roles WHERE IsDeleted = 0
         GROUP BY TenantId, Name HAVING COUNT(*) > 1;                        */
    PRINT 'Step 6 FAILED: ' + ERROR_MESSAGE();
    SET NOEXEC ON;
END CATCH
GO

/* ---------------------------------------------------------------------
   7. Trial lifecycle on Tenants
   --------------------------------------------------------------------- */
BEGIN TRY
    BEGIN TRANSACTION;

    IF COL_LENGTH('dbo.Tenants', 'TrialStatus') IS NULL
        ALTER TABLE dbo.Tenants ADD TrialStatus NVARCHAR(20) NULL;

    IF COL_LENGTH('dbo.Tenants', 'IsSuspended') IS NULL
        ALTER TABLE dbo.Tenants ADD IsSuspended BIT NOT NULL
            CONSTRAINT DF_Tenants_IsSuspended DEFAULT (0);

    IF COL_LENGTH('dbo.Tenants', 'ConvertedToPlan') IS NULL
        ALTER TABLE dbo.Tenants ADD ConvertedToPlan NVARCHAR(50) NULL;

    COMMIT TRANSACTION;
    PRINT 'Step 7 OK — Tenants trial lifecycle columns.';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT 'Step 7 FAILED: ' + ERROR_MESSAGE();
    SET NOEXEC ON;
END CATCH
GO

SET NOEXEC OFF;
GO

/* ---------------------------------------------------------------------
   8. VERIFY — every row should read OK
   --------------------------------------------------------------------- */
SELECT 'Users.PasswordHash'     AS Item, CASE WHEN COL_LENGTH('dbo.Users','PasswordHash')     IS NULL THEN 'MISSING' ELSE 'OK' END AS Status
UNION ALL SELECT 'Users.SecurityStamp',   CASE WHEN COL_LENGTH('dbo.Users','SecurityStamp')   IS NULL THEN 'MISSING' ELSE 'OK' END
UNION ALL SELECT 'Users.IsSuperAdmin',    CASE WHEN COL_LENGTH('dbo.Users','IsSuperAdmin')    IS NULL THEN 'MISSING' ELSE 'OK' END
UNION ALL SELECT 'Users.LockoutEndUtc',   CASE WHEN COL_LENGTH('dbo.Users','LockoutEndUtc')   IS NULL THEN 'MISSING' ELSE 'OK' END
UNION ALL SELECT 'Roles.TenantId',        CASE WHEN COL_LENGTH('dbo.Roles','TenantId')        IS NULL THEN 'MISSING' ELSE 'OK' END
UNION ALL SELECT 'Tenants.TrialStatus',   CASE WHEN COL_LENGTH('dbo.Tenants','TrialStatus')   IS NULL THEN 'MISSING' ELSE 'OK' END
UNION ALL SELECT 'Tenants.IsSuspended',   CASE WHEN COL_LENGTH('dbo.Tenants','IsSuspended')   IS NULL THEN 'MISSING' ELSE 'OK' END
UNION ALL SELECT 'UserTokens table',      CASE WHEN OBJECT_ID('dbo.UserTokens','U')           IS NULL THEN 'MISSING' ELSE 'OK' END;
GO

/* =====================================================================
   POST-MIGRATION — run manually, do not automate.

   Every user now has PasswordHash NULL and CANNOT log in, including you.
   That is deliberate — the demo users were never meant to hold credentials.

   Seed your own super-admin. Generate the hash in the app, never by hand:

       var hash = new PasswordHasher<User>().HashPassword(null, "your-password");

   Then:

       UPDATE dbo.Users
          SET PasswordHash          = '<paste hash>',
              SecurityStamp         = CONVERT(nvarchar(50), NEWID()),
              IsSuperAdmin          = 1,
              EmailConfirmedAtUtc   = SYSUTCDATETIME(),
              LastPasswordChangeUtc = SYSUTCDATETIME(),
              FailedLoginCount      = 0,
              LockoutEndUtc         = NULL
        WHERE Email = 'admin@madeevision.com';   -- your real address

   Confirm exactly one row was affected. If zero, check the email value in
   dbo.Users — you will otherwise have no way into the application.
   ===================================================================== */
