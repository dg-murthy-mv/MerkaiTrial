/* =====================================================================
   FILE: tests/MerkaiTrial.E2E/Sql/create-e2e-database.sql

   Makes (or remakes) the TEST database, MerkaiTrial_E2E, as a copy of
   your development database. Run it in SSMS, connected to the same SQL
   Server your development database is on.

   Safe for your development database:
     • COPY_ONLY backup — it does not disturb any backup routine.
     • The copy gets its own new file names; the original's files are
       never touched.
     • It only ever DROPS a database whose name ends in _E2E.

   Run it again any time you want a clean test database, and after every
   round that has a SQL script (run that round's script on the copy too,
   or just remake the copy from your updated development database).

   ── Change these two lines if yours differ ──────────────────────────── */
DECLARE @SourceDb sysname = N'MerkaiTrial';        -- your development database
DECLARE @TargetDb sysname = N'MerkaiTrial_E2E';    -- must end in _E2E
/* ───────────────────────────────────────────────────────────────────── */

SET NOCOUNT ON;

IF RIGHT(@TargetDb, 4) <> N'_E2E'
BEGIN
    RAISERROR(N'The test database name must end in _E2E. Nothing was changed.', 16, 1);
    RETURN;
END

IF DB_ID(@SourceDb) IS NULL
BEGIN
    RAISERROR(N'There is no database called %s on this server. Set @SourceDb to your development database name (see Object Explorer).', 16, 1, @SourceDb);
    RETURN;
END

-- Where this server keeps backups and data files.
DECLARE @BackupDir nvarchar(4000) = CONVERT(nvarchar(4000), SERVERPROPERTY('InstanceDefaultBackupPath'));
DECLARE @DataDir   nvarchar(4000) = CONVERT(nvarchar(4000), SERVERPROPERTY('InstanceDefaultDataPath'));
DECLARE @LogDir    nvarchar(4000) = CONVERT(nvarchar(4000), SERVERPROPERTY('InstanceDefaultLogPath'));
IF @BackupDir IS NULL OR @BackupDir = N'' SET @BackupDir = @DataDir;
IF RIGHT(@BackupDir, 1) <> N'\' SET @BackupDir += N'\';
IF RIGHT(@DataDir, 1)   <> N'\' SET @DataDir   += N'\';
IF RIGHT(@LogDir, 1)    <> N'\' SET @LogDir    += N'\';

DECLARE @BackupFile nvarchar(4000) = @BackupDir + @SourceDb + N'_for_E2E.bak';

PRINT N'1/3  Backing up ' + @SourceDb + N' to ' + @BackupFile;
BACKUP DATABASE @SourceDb TO DISK = @BackupFile WITH COPY_ONLY, INIT, FORMAT;

-- The backup's own file list, to give each file a new name.
DECLARE @Files TABLE (
    LogicalName nvarchar(128), PhysicalName nvarchar(260), [Type] char(1), FileGroupName nvarchar(128),
    Size numeric(20,0), MaxSize numeric(20,0), FileId bigint, CreateLSN numeric(25,0), DropLSN numeric(25,0),
    UniqueId uniqueidentifier, ReadOnlyLSN numeric(25,0), ReadWriteLSN numeric(25,0), BackupSizeInBytes bigint,
    SourceBlockSize int, FileGroupId int, LogGroupGUID uniqueidentifier, DifferentialBaseLSN numeric(25,0),
    DifferentialBaseGUID uniqueidentifier, IsReadOnly bit, IsPresent bit, TDEThumbprint varbinary(32),
    SnapshotUrl nvarchar(360));

-- SQL Server 2016 and later return the columns above; older versions
-- return one fewer (no SnapshotUrl). If this INSERT fails with a column
-- count error, delete the SnapshotUrl line above and run again.
INSERT INTO @Files EXEC (N'RESTORE FILELISTONLY FROM DISK = N''' + @BackupFile + N'''');

DECLARE @Move nvarchar(max) = N'';
SELECT @Move += N', MOVE N''' + LogicalName + N''' TO N'''
              + CASE WHEN [Type] = 'L' THEN @LogDir ELSE @DataDir END
              + @TargetDb + N'_' + CAST(FileId AS nvarchar(10))
              + CASE WHEN [Type] = 'L' THEN N'.ldf' ELSE N'.mdf' END + N''''
FROM @Files;

IF DB_ID(@TargetDb) IS NOT NULL
BEGIN
    PRINT N'2/3  Dropping the old ' + @TargetDb;
    EXEC (N'ALTER DATABASE ' + QUOTENAME(@TargetDb) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE');
    EXEC (N'DROP DATABASE ' + QUOTENAME(@TargetDb));
END
ELSE
    PRINT N'2/3  No old ' + @TargetDb + N' to drop';

PRINT N'3/3  Restoring the copy as ' + @TargetDb;
DECLARE @Restore nvarchar(max) =
    N'RESTORE DATABASE ' + QUOTENAME(@TargetDb) + N' FROM DISK = N''' + @BackupFile + N''' WITH RECOVERY, REPLACE' + @Move;
EXEC (@Restore);

-- Anything your development outbox had queued is copied too. Mark it
-- Cancelled so the test WebApi doesn't spend its time retrying it. (It
-- could never send it anyway: the test servers run without an email key.)
-- 0 Pending, 1 Sending, 3 Failed  →  5 Cancelled.
DECLARE @Tidy nvarchar(max) = N'
    UPDATE ' + QUOTENAME(@TargetDb) + N'.dbo.OutboundMessages
       SET Status = 5, LastError = N''Copied into the E2E database; not sent.''
     WHERE Status IN (0, 1, 3);';
BEGIN TRY
    EXEC (@Tidy);
END TRY
BEGIN CATCH
    PRINT N'(Skipped tidying the copied outbox: ' + ERROR_MESSAGE() + N')';
END CATCH

PRINT N'Done. ' + @TargetDb + N' is ready.';
