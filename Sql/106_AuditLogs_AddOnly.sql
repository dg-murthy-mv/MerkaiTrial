/* =====================================================================
   106_AuditLogs_AddOnly.sql — the audit log can only grow (VUL-28)
   (Round106-update: simpler. The first version tested the trigger by
   trying an UPDATE inside the script; that test could leave the SSMS
   window holding an open transaction on dbo.AuditLogs. This one only
   creates the trigger and checks it exists. The E2E test does the real
   check.)

   Adds:
     TR_AuditLogs_AddOnly   a trigger on dbo.AuditLogs that refuses every
                            UPDATE and DELETE, whoever sends it. New rows
                            are added as before. No rows are touched.

   THE AGREED CATCH: a future clean-up of old audit rows has to switch
   the trigger off for that one job:
       DISABLE TRIGGER dbo.TR_AuditLogs_AddOnly ON dbo.AuditLogs;
       ...the clean-up...
       ENABLE  TRIGGER dbo.TR_AuditLogs_AddOnly ON dbo.AuditLogs;

   RUN IT ON BOTH DATABASES — development AND MerkaiTrial_E2E. No dry
   run this time: it is one CREATE OR ALTER, safe to run again.
   Grid 1 must show OK on both rows.
   ===================================================================== */

SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.AuditLogs', N'U') IS NULL
BEGIN
    RAISERROR('dbo.AuditLogs is missing — is this the Merkai database?', 16, 1);
    RETURN;
END
GO

CREATE OR ALTER TRIGGER dbo.TR_AuditLogs_AddOnly
ON dbo.AuditLogs
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM deleted) RETURN;   -- nothing was actually touched
    THROW 51060, N'AuditLogs is add-only: audit records can''t be changed or deleted (106). See Sql/106_AuditLogs_AddOnly.sql.', 1;
END;
GO

SELECT Item, Result FROM (
    SELECT 0 AS N, 'Database' AS Item, DB_NAME() AS Result
    UNION ALL
    SELECT 1, 'TR_AuditLogs_AddOnly trigger, switched on',
           CASE WHEN EXISTS (SELECT 1 FROM sys.triggers
                             WHERE name = N'TR_AuditLogs_AddOnly'
                               AND parent_id = OBJECT_ID(N'dbo.AuditLogs')
                               AND is_disabled = 0)
                THEN 'OK' ELSE 'MISSING' END
    UNION ALL
    SELECT 2, 'No transaction left open in this window',
           CASE WHEN @@TRANCOUNT = 0 THEN 'OK' ELSE 'OPEN — run ROLLBACK' END
) x ORDER BY N;
