// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Infrastructure/DbWaitWatch.cs
//
// NEW (095). Watches the _E2E database during the whole run and writes
// down every query that waits a long time, AND WHO MADE IT WAIT.
//
// WHY
//   The first full run of 095 had three tests fail on a plain database
//   read that waited 30 seconds and gave up ("Execution Timeout Expired"),
//   and the WebApi logged a Users lookup by its key taking 35 seconds.
//   A read by primary key does not take 35 seconds on its own. Something
//   was holding a lock on those rows, and by the time the test reports
//   the timeout, the evidence is gone.
//
//   So every 3 seconds this asks SQL Server: "which requests in this
//   database have been waiting 8 seconds or more, and on whom?" Each one
//   is written to the test output as
//
//     [e2e db] 18:13:12 WAIT 11.2s  MerkaiTrial.WebApi (pid 1234) …
//              blocked by session 61, MerkaiTrial.Admin.Web (pid 5678),
//              transaction open since 18:12:58, last statement: …
//
//   and to e2e-artifacts/db-waits.txt. The process names come from the
//   process ids SQL Server reports, looked up on this machine, so it says
//   which app (or the tests themselves) held the lock.
//
// IT NEVER FAILS A TEST. If the account can't read the server's activity
// views (VIEW SERVER STATE), it says so once and stops watching.
// =====================================================================

using System.Diagnostics;
using System.Text;
using Microsoft.Data.SqlClient;

namespace MerkaiTrial.E2E.Infrastructure;

public static class DbWaitWatch
{
    private const int PollSeconds = 3;
    private const int ReportAfterMs = 8_000;

    private static CancellationTokenSource? _stop;
    private static Task? _loop;
    private static readonly HashSet<string> Seen = new();
    private static int _reported;

    // Worked out once, in Start, on the setup thread: the watch loop runs
    // outside any test, where NUnit's current context is not to be relied on.
    private static string LogPath = "db-waits.txt";

    // Requests in THIS database that have waited long enough, with what
    // they wait for, and — when another session blocks them — that
    // session's process, its open transaction's start, and its last SQL.
    private const string Sql = @"
SELECT r.session_id,
       r.start_time,
       r.wait_type,
       r.wait_time,
       r.total_elapsed_time,
       r.blocking_session_id,
       ws.host_process_id                           AS waiter_pid,
       LEFT(wt.text, 300)                           AS waiter_sql,
       bs.host_process_id                           AS blocker_pid,
       bs.status                                    AS blocker_status,
       bs.open_transaction_count                    AS blocker_open_tx,
       (SELECT MIN(at.transaction_begin_time)
          FROM sys.dm_tran_session_transactions st
          JOIN sys.dm_tran_active_transactions at ON at.transaction_id = st.transaction_id
         WHERE st.session_id = r.blocking_session_id) AS blocker_tx_start,
       CASE WHEN r.blocking_session_id > 0
            THEN LEFT(ib.event_info, 300) END       AS blocker_last_sql
FROM sys.dm_exec_requests r
JOIN sys.dm_exec_sessions ws          ON ws.session_id = r.session_id
OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) wt
LEFT JOIN sys.dm_exec_sessions bs     ON bs.session_id = r.blocking_session_id
-- Always called with a live session id (the blocker's, or the waiter's
-- own when nothing blocks it), so the function never sees session 0.
OUTER APPLY sys.dm_exec_input_buffer(
              CASE WHEN r.blocking_session_id > 0 THEN r.blocking_session_id ELSE r.session_id END,
              NULL) ib
WHERE r.database_id = DB_ID()
  AND r.session_id <> @@SPID
  -- SQL Server's own background tasks run for as long as the server has
  -- been up (one showed as ""waiting 278,441 s"" — three days); only
  -- requests from programs count.
  AND ws.is_user_process = 1
  AND r.total_elapsed_time >= @min;";

    public static void Start(string connectionString)
    {
        if (_loop is not null) return;

        try
        {
            LogPath = Path.Combine(E2ESettings.ArtifactsDir, "db-waits.txt");
            Directory.CreateDirectory(E2ESettings.ArtifactsDir);
            File.WriteAllText(LogPath, $"Database waits of {ReportAfterMs / 1000}s or more — run started {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}");
        }
        catch { /* the console line is enough */ }

        _stop = new CancellationTokenSource();
        var ct = _stop.Token;
        _loop = Task.Run(() => LoopAsync(connectionString, ct));
    }

    public static async Task StopAsync()
    {
        if (_stop is null || _loop is null) return;
        _stop.Cancel();
        try { await _loop; } catch { /* stopping */ }

        TestContext.Progress.WriteLine(_reported == 0
            ? "[e2e db] No database query waited 8 seconds or more during the run."
            : $"[e2e db] {_reported} long database wait(s) during the run — listed above and in {LogPath}");
    }

    private static async Task LoopAsync(string connectionString, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(connectionString, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (SqlException ex) when (ex.Number is 297 or 300)   // no VIEW SERVER STATE
            {
                TestContext.Progress.WriteLine(
                    "[e2e db] Can't watch for database waits — this account lacks VIEW SERVER STATE. " +
                    "The tests run as usual.");
                return;
            }
            catch (Exception ex)
            {
                // A hiccup in the watcher is not a test failure. Note it once in a while.
                TestContext.Progress.WriteLine($"[e2e db] Watcher check failed: {ex.Message}");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(PollSeconds), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static async Task PollOnceAsync(string connectionString, CancellationToken ct)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(Sql, conn) { CommandTimeout = 20 };   // the first look at the activity views can be slow
        cmd.Parameters.AddWithValue("@min", ReportAfterMs);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var session  = reader.GetInt16(0);
            var started  = reader.GetDateTime(1);
            var key      = $"{session}|{started:O}";
            if (!Seen.Add(key)) continue;          // one line per waiting request

            var waitType = reader.IsDBNull(2) ? "running" : reader.GetString(2);
            var elapsed  = reader.GetInt32(4) / 1000.0;
            var blocker  = reader.GetInt16(5);
            var waiterPid = reader.IsDBNull(6) ? (int?)null : reader.GetInt32(6);
            var waiterSql = reader.IsDBNull(7) ? "" : OneLine(reader.GetString(7));

            var line = new StringBuilder()
                .Append($"[e2e db] {DateTime.Now:HH:mm:ss} WAIT {elapsed:0.0}s on {waitType}  ")
                .Append($"session {session}, {Who(waiterPid)}: {waiterSql}");

            if (blocker > 0)
            {
                var blockerPid    = reader.IsDBNull(8) ? (int?)null : reader.GetInt32(8);
                var blockerStatus = reader.IsDBNull(9) ? "?" : reader.GetString(9);
                var openTx        = reader.IsDBNull(10) ? 0 : reader.GetInt32(10);
                var txStart       = reader.IsDBNull(11) ? (DateTime?)null : reader.GetDateTime(11);
                var lastSql       = reader.IsDBNull(12) ? "" : OneLine(reader.GetString(12));

                line.Append(Environment.NewLine)
                    .Append($"         blocked by session {blocker}, {Who(blockerPid)}, {blockerStatus}, ")
                    .Append($"{openTx} open transaction(s)")
                    .Append(txStart is null ? "" : $" since {txStart:HH:mm:ss}")
                    .Append(Environment.NewLine)
                    .Append($"         its last statement: {lastSql}");
            }

            var text = line.ToString();
            _reported++;
            TestContext.Progress.WriteLine(text);
            try { await File.AppendAllTextAsync(LogPath, text + Environment.NewLine, ct); } catch { }
        }
    }

    /// <summary>"MerkaiTrial.WebApi (pid 1234)" — the process on this machine with that id.</summary>
    private static string Who(int? pid)
    {
        if (pid is null or 0) return "unknown process";
        try
        {
            using var p = Process.GetProcessById(pid.Value);
            var name = p.ProcessName;
            if (pid.Value == Environment.ProcessId) name += " (the tests)";
            return $"{name} (pid {pid})";
        }
        catch
        {
            return $"pid {pid} (no longer running)";
        }
    }

    private static string OneLine(string s) =>
        string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
