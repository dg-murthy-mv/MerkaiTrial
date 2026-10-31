// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Infrastructure/DatabaseCopies.cs
//
// NEW (110). Backing up and restoring the TEST database, for
// SqlScriptRerunTests (a throw-away copy to run the scripts on) and
// BackupRestoreTests (restore over the test database itself).
//
// Safety, the same rules as create-e2e-database.sql:
//   • Every database this touches must have a name ending in _E2E —
//     checked before every statement. Your development database is
//     never backed up, restored, dropped or changed by this class.
//   • Backups are COPY_ONLY, so they never disturb a backup routine.
//   • A copy gets its own new file names; the test database's files are
//     never touched by a copy.
//   • The backup file is the server's default backup folder,
//     MerkaiTrial_E2E_test_copy.bak, overwritten each time.
//
// Needs a SQL login that may back up and restore (sysadmin or dbcreator)
// — the one you run create-e2e-database.sql with. CheckPermissionsAsync
// says so plainly if it can't.
// =====================================================================

using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace MerkaiTrial.E2E.Infrastructure;

public static class DatabaseCopies
{
    public const string BackupFileName = "MerkaiTrial_E2E_test_copy.bak";

    /// <summary>The test database's name (MerkaiTrial_E2E).</summary>
    public static string TestDatabase =>
        new SqlConnectionStringBuilder(E2ESettings.ConnectionString).InitialCatalog;

    /// <summary>The test connection string pointed at another database, without pooling.</summary>
    public static string ConnectionStringFor(string database, bool pooling = false) =>
        new SqlConnectionStringBuilder(E2ESettings.ConnectionString)
        {
            InitialCatalog = database,
            Pooling = pooling,
            ConnectTimeout = 30,
        }.ConnectionString;

    private static string Master => ConnectionStringFor("master");

    private static readonly Regex SafeName = new(@"^[A-Za-z0-9_]+_E2E$", RegexOptions.IgnoreCase);

    /// <summary>Throws unless the name ends in _E2E and is plain letters, digits and _.</summary>
    public static void Guard(string database)
    {
        if (!SafeName.IsMatch(database))
            throw new InvalidOperationException(
                $"Refusing to touch database '{database}': only databases whose names end in _E2E " +
                "(letters, digits and _ only) may be backed up, restored or dropped by the tests.");
    }

    // ── permissions ───────────────────────────────────────────────────

    public static async Task CheckPermissionsAsync()
    {
        await using var conn = new SqlConnection(Master);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT IS_SRVROLEMEMBER('sysadmin'), IS_SRVROLEMEMBER('dbcreator'), " +
            "HAS_PERMS_BY_NAME(NULL, NULL, 'CREATE ANY DATABASE'), SUSER_SNAME()", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        var sysadmin = !r.IsDBNull(0) && r.GetInt32(0) == 1;
        var dbcreator = !r.IsDBNull(1) && r.GetInt32(1) == 1;
        var create = !r.IsDBNull(2) && r.GetInt32(2) == 1;
        var who = r.IsDBNull(3) ? "?" : r.GetString(3);
        if (!sysadmin && !(dbcreator && create))
            Assert.Fail(
                $"The test login ({who}) can't back up and restore databases. Give it the sysadmin or dbcreator " +
                "role on this SQL Server (the login you run create-e2e-database.sql with has it), or run the " +
                "tests with that login's connection string (-ConnectionString).");
    }

    // ── backup ────────────────────────────────────────────────────────

    /// <summary>A COPY_ONLY full backup into the server's backup folder. Returns the file's path (on the server).</summary>
    public static async Task<string> BackupAsync(string database)
    {
        Guard(database);
        await using var conn = new SqlConnection(Master);
        await conn.OpenAsync();

        var dir = await ScalarTextAsync(conn,
            "SELECT COALESCE(NULLIF(CONVERT(nvarchar(4000), SERVERPROPERTY('InstanceDefaultBackupPath')), N''), " +
            "CONVERT(nvarchar(4000), SERVERPROPERTY('InstanceDefaultDataPath')))");
        if (string.IsNullOrWhiteSpace(dir))
            throw new InvalidOperationException("SQL Server didn't say where its backup folder is.");
        var path = dir.TrimEnd('\\', '/') + "\\" + BackupFileName;

        // Express (EngineEdition 4) can't compress backups.
        var edition = await ScalarTextAsync(conn, "SELECT CONVERT(nvarchar(10), SERVERPROPERTY('EngineEdition'))");
        var compress = edition != "4" ? ", COMPRESSION" : "";

        await using var cmd = new SqlCommand(
            $"BACKUP DATABASE [{database}] TO DISK = @path WITH COPY_ONLY, INIT, FORMAT, CHECKSUM{compress}", conn)
        { CommandTimeout = 1800 };
        cmd.Parameters.AddWithValue("@path", path);
        await cmd.ExecuteNonQueryAsync();
        return path;
    }

    // ── restore as a copy ─────────────────────────────────────────────

    /// <summary>Restores a backup as a NEW database with its own files (dropping an old copy of that name first).</summary>
    public static async Task RestoreAsCopyAsync(string backupPath, string target)
    {
        Guard(target);
        if (string.Equals(target, TestDatabase, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A copy must have a different name from the test database.");

        await DropIfExistsAsync(target);

        await using var conn = new SqlConnection(Master);
        await conn.OpenAsync();

        var dataDir = (await ScalarTextAsync(conn, "SELECT CONVERT(nvarchar(4000), SERVERPROPERTY('InstanceDefaultDataPath'))")).TrimEnd('\\', '/') + "\\";
        var logDir = (await ScalarTextAsync(conn, "SELECT CONVERT(nvarchar(4000), SERVERPROPERTY('InstanceDefaultLogPath'))")).TrimEnd('\\', '/') + "\\";

        var moves = new List<string>();
        await using (var list = new SqlCommand("RESTORE FILELISTONLY FROM DISK = @path", conn) { CommandTimeout = 600 })
        {
            list.Parameters.AddWithValue("@path", backupPath);
            await using var r = await list.ExecuteReaderAsync();
            var logical = r.GetOrdinal("LogicalName");
            var type = r.GetOrdinal("Type");
            var fileId = r.GetOrdinal("FileId");
            while (await r.ReadAsync())
            {
                var name = r.GetString(logical);
                var isLog = string.Equals(r.GetString(type), "L", StringComparison.OrdinalIgnoreCase);
                var id = Convert.ToInt64(r.GetValue(fileId));
                var file = (isLog ? logDir : dataDir) + $"{target}_{id}" + (isLog ? ".ldf" : ".mdf");
                moves.Add($"MOVE N'{name.Replace("'", "''")}' TO N'{file.Replace("'", "''")}'");
            }
        }
        if (moves.Count == 0) throw new InvalidOperationException($"The backup {backupPath} lists no files.");

        await using var restore = new SqlCommand(
            $"RESTORE DATABASE [{target}] FROM DISK = @path WITH RECOVERY, REPLACE, " + string.Join(", ", moves), conn)
        { CommandTimeout = 1800 };
        restore.Parameters.AddWithValue("@path", backupPath);
        await restore.ExecuteNonQueryAsync();
    }

    /// <summary>Drops a copy, if it is there.</summary>
    public static async Task DropIfExistsAsync(string database)
    {
        Guard(database);
        if (string.Equals(database, TestDatabase, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The tests never drop the test database itself.");

        await using var conn = new SqlConnection(Master);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            $"IF DB_ID(N'{database}') IS NOT NULL BEGIN " +
            $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
            $"DROP DATABASE [{database}]; END", conn)
        { CommandTimeout = 600 };
        await cmd.ExecuteNonQueryAsync();
    }

    // ── restore in place ──────────────────────────────────────────────

    /// <summary>
    /// Restores a backup OVER the test database, the way you would after
    /// losing data in production: everyone is disconnected (OFFLINE WITH
    /// ROLLBACK IMMEDIATE — no other connection can slip in, as it can
    /// with SINGLE_USER), the backup replaces the database, and it comes
    /// back online. If the restore fails, the database is put back online
    /// as it was, so the rest of the run isn't left without a database.
    /// </summary>
    public static async Task RestoreInPlaceAsync(string backupPath, string database)
    {
        Guard(database);
        await using var conn = new SqlConnection(Master);
        await conn.OpenAsync();
        try
        {
            await ExecAsync(conn, $"ALTER DATABASE [{database}] SET OFFLINE WITH ROLLBACK IMMEDIATE", 600);
            await using var restore = new SqlCommand(
                $"RESTORE DATABASE [{database}] FROM DISK = @path WITH RECOVERY, REPLACE", conn)
            { CommandTimeout = 1800 };
            restore.Parameters.AddWithValue("@path", backupPath);
            await restore.ExecuteNonQueryAsync();
        }
        finally
        {
            // Whatever happened, leave it online and open to everyone.
            try
            {
                await ExecAsync(conn,
                    $"IF EXISTS (SELECT 1 FROM sys.databases WHERE name = N'{database}' AND state_desc <> N'ONLINE') " +
                    $"ALTER DATABASE [{database}] SET ONLINE; " +
                    $"IF EXISTS (SELECT 1 FROM sys.databases WHERE name = N'{database}' AND user_access_desc <> N'MULTI_USER') " +
                    $"ALTER DATABASE [{database}] SET MULTI_USER;", 600);
            }
            catch (Exception ex)
            {
                TestContext.Progress.WriteLine($"[e2e db] Couldn't check {database} is back online: {ex.Message}");
            }
            // The tests' own pooled connections point at the old sessions.
            SqlConnection.ClearAllPools();
        }
    }

    // ── helpers ───────────────────────────────────────────────────────

    private static async Task<string> ScalarTextAsync(SqlConnection conn, string sql)
    {
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
        return Convert.ToString(await cmd.ExecuteScalarAsync()) ?? "";
    }

    private static async Task ExecAsync(SqlConnection conn, string sql, int timeout)
    {
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = timeout };
        await cmd.ExecuteNonQueryAsync();
    }
}
