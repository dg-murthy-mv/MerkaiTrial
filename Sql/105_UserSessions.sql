/* =====================================================================
   105_UserSessions.sql — signing out really ends the session (VUL-11)

   Adds:
     dbo.UserSessions   one row per sign-in. The sign-in cookie carries
                        the row's id, and every request checks the row:
                        signed out, or more than 24 hours after sign-in,
                        and the cookie is refused — even a copy of it.
     IX_UserSessions_UserId_ExpiresAtUtc
                        for clearing a person's old sessions at sign-in

   Nothing existing is changed.

   AFTER THIS ROUND EVERYONE SIGNS IN ONCE MORE. Cookies made before it
   carry no session, so the first click after the new build goes to the
   sign-in page. That happens once.

   ─────────────────────────────────────────────────────────────────────
   RUN IT ON BOTH DATABASES — your development database AND
   MerkaiTrial_E2E. Check the database dropdown in the toolbar before
   pressing Execute; the first row of grid 1 shows which one it ran on.
   ─────────────────────────────────────────────────────────────────────

   HOW TO RUN
     1. Run as-is. @Commit = 0 — everything is ROLLED BACK. Grid 1: every
        row should say OK.
     2. Change @Commit to 1 and run again. Grid 1 again: all OK, and the
        messages end with COMMITTED.
     3. Switch the dropdown to the other database and do 1–2 again.

   SAFE TO RE-RUN. Every CREATE is guarded.
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit BIT = 0;      -- flip to 1 to commit

IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
BEGIN
    RAISERROR('dbo.Users is missing — is this the Merkai database?', 16, 1);
    RETURN;
END

BEGIN TRAN;

-- ── UserSessions ─────────────────────────────────────────────────────
IF OBJECT_ID(N'dbo.UserSessions', N'U') IS NULL
BEGIN
    EXEC sp_executesql N'
        CREATE TABLE dbo.UserSessions
        (
            Id            UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_UserSessions PRIMARY KEY,
            UserId        UNIQUEIDENTIFIER NOT NULL,
            CreatedAtUtc  DATETIME2        NOT NULL,
            ExpiresAtUtc  DATETIME2        NOT NULL,
            EndedAtUtc    DATETIME2        NULL,
            EndReason     NVARCHAR(50)     NULL,
            IpAddress     NVARCHAR(64)     NULL,
            UserAgent     NVARCHAR(256)    NULL
        );';
    PRINT 'Created dbo.UserSessions';
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_UserSessions_UserId_ExpiresAtUtc'
                 AND object_id = OBJECT_ID(N'dbo.UserSessions'))
BEGIN
    EXEC sp_executesql N'
        CREATE INDEX IX_UserSessions_UserId_ExpiresAtUtc
            ON dbo.UserSessions (UserId, ExpiresAtUtc);';
    PRINT 'Created IX_UserSessions_UserId_ExpiresAtUtc';
END

-- ── Checks ───────────────────────────────────────────────────────────
SELECT Item, Result FROM (
    SELECT 0 AS N, 'Database' AS Item, DB_NAME() AS Result
    UNION ALL
    SELECT 1, 'UserSessions table',
           CASE WHEN OBJECT_ID(N'dbo.UserSessions', N'U') IS NOT NULL THEN 'OK' ELSE 'MISSING' END
    UNION ALL
    SELECT 2, 'UserSessions columns (8)',
           CASE WHEN (SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.UserSessions')) = 8
                THEN 'OK' ELSE 'MISSING' END
    UNION ALL
    SELECT 3, 'IX_UserSessions_UserId_ExpiresAtUtc index',
           CASE WHEN EXISTS (SELECT 1 FROM sys.indexes
                             WHERE name = N'IX_UserSessions_UserId_ExpiresAtUtc'
                               AND object_id = OBJECT_ID(N'dbo.UserSessions'))
                THEN 'OK' ELSE 'MISSING' END
) x ORDER BY N;

IF @Commit = 1
BEGIN
    COMMIT;
    PRINT 'COMMITTED';
END
ELSE
BEGIN
    ROLLBACK;
    PRINT 'ROLLED BACK (dry run). Set @Commit = 1 to keep the changes.';
END
