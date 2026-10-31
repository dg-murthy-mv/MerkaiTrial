/* =====================================================================
   101_Diagnose_SqlServer.sql — READ ONLY. Changes nothing.

   WHY
   In every full run, one part of test 76 (Invoices) takes about 3 minutes
   instead of a few seconds. Each time:
     • SQL Server waited 25 seconds on PREEMPTIVE_OS_QUERYREGISTRY — SQL
       Server itself waiting on Windows, inside an ordinary invoice-list
       query, and
     • the test's database watcher could not get an answer from SQL Server
       at all for a couple of minutes ("Execution Timeout Expired").
   Merkai's own log shows nothing wrong in that window. That points at the
   SQL Server instance or the PC, not at a slow query: this script
   collects what is needed to tell which.

   HOW TO RUN
     1. Open this file in SSMS, connected to the SQL Server your
        MerkaiTrial and MerkaiTrial_E2E databases are on (any database
        selected — the script names them itself).
     2. Query menu → Results To → Results to Text  (or Ctrl+T).
     3. Execute (F5). It takes a few seconds.
     4. Click in the results, Ctrl+A, Ctrl+C, paste into a .txt file and
        send it to me. Best run straight after a full test run, while the
        numbers since the last restart still include it.

   Some sections need the sysadmin role (the error log, the default
   trace). If you are not sysadmin they say so and the rest still runs.
   ===================================================================== */

SET NOCOUNT ON;

PRINT '=== 1. Server ===';
SELECT
    CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion'))  AS ProductVersion,
    CONVERT(nvarchar(128), SERVERPROPERTY('ProductLevel'))    AS ProductLevel,
    CONVERT(nvarchar(128), SERVERPROPERTY('ProductUpdateLevel')) AS UpdateLevel,
    CONVERT(nvarchar(128), SERVERPROPERTY('Edition'))         AS Edition,
    CONVERT(nvarchar(128), SERVERPROPERTY('InstanceName'))    AS InstanceName,
    (SELECT sqlserver_start_time FROM sys.dm_os_sys_info)     AS StartedAt,
    (SELECT cpu_count FROM sys.dm_os_sys_info)                AS Cpus,
    (SELECT physical_memory_kb / 1024 FROM sys.dm_os_sys_info) AS MachineMemoryMB,
    (SELECT CONVERT(int, value_in_use) FROM sys.configurations WHERE name = 'max server memory (MB)') AS MaxServerMemoryMB;

PRINT '=== 2. Memory right now ===';
SELECT
    m.available_physical_memory_kb / 1024 AS MachineFreeMB,
    m.system_memory_state_desc            AS MachineMemoryState,
    p.physical_memory_in_use_kb / 1024    AS SqlServerUsingMB,
    p.process_physical_memory_low         AS SqlServerToldMemoryIsLow,
    p.page_fault_count                    AS SqlServerPageFaults
FROM sys.dm_os_sys_memory m CROSS JOIN sys.dm_os_process_memory p;

PRINT '=== 3. Database options (Merkai databases) ===';
SELECT
    name,
    state_desc,
    is_auto_close_on,
    is_auto_shrink_on,
    recovery_model_desc,
    compatibility_level,
    is_read_committed_snapshot_on,
    snapshot_isolation_state_desc,
    log_reuse_wait_desc,
    is_auto_update_stats_async_on
FROM sys.databases
WHERE name LIKE N'MerkaiTrial%'
ORDER BY name;

PRINT '=== 4. Database files: size and how they grow ===';
SELECT
    DB_NAME(mf.database_id)                                   AS db,
    mf.name                                                   AS logical_name,
    mf.type_desc,
    CONVERT(decimal(12,1), mf.size * 8 / 1024.0)              AS size_mb,
    CASE WHEN mf.is_percent_growth = 1
         THEN CONVERT(varchar(20), mf.growth) + ' %'
         ELSE CONVERT(varchar(20), mf.growth * 8 / 1024) + ' MB' END AS grows_by,
    CASE WHEN mf.max_size = -1 THEN 'unlimited'
         ELSE CONVERT(varchar(20), CONVERT(bigint, mf.max_size) * 8 / 1024) + ' MB' END AS max_size,
    LEFT(mf.physical_name, 3)                                 AS drive
FROM sys.master_files mf
WHERE DB_NAME(mf.database_id) LIKE N'MerkaiTrial%' OR mf.database_id = 2   -- tempdb too
ORDER BY db, mf.type_desc;

PRINT '=== 5. Instant file initialization (fast file growth) ===';
BEGIN TRY
    EXEC (N'SELECT servicename, instant_file_initialization_enabled FROM sys.dm_server_services;');
END TRY
BEGIN CATCH
    PRINT 'Not available on this version: ' + ERROR_MESSAGE();
END CATCH;

PRINT '=== 6. What SQL Server has waited on most since it started ===';
SELECT TOP (20)
    wait_type,
    waiting_tasks_count,
    CONVERT(decimal(14,1), wait_time_ms / 1000.0)                                    AS total_wait_s,
    CONVERT(decimal(14,1), max_wait_time_ms / 1000.0)                                AS longest_single_wait_s,
    CONVERT(decimal(14,2), wait_time_ms * 1.0 / NULLIF(waiting_tasks_count, 0))      AS avg_wait_ms
FROM sys.dm_os_wait_stats
WHERE waiting_tasks_count > 0
  AND wait_type NOT IN (   -- normal idle waits
      'SLEEP_TASK','BROKER_TASK_STOP','BROKER_TO_FLUSH','SQLTRACE_BUFFER_FLUSH','CLR_AUTO_EVENT','CLR_MANUAL_EVENT',
      'LAZYWRITER_SLEEP','SLEEP_SYSTEMTASK','SLEEP_BPOOL_FLUSH','BROKER_EVENTHANDLER','XE_DISPATCHER_WAIT',
      'FT_IFTSHC_MUTEX','CHECKPOINT_QUEUE','REQUEST_FOR_DEADLOCK_SEARCH','XE_TIMER_EVENT','BROKER_RECEIVE_WAITFOR',
      'ONDEMAND_TASK_QUEUE','LOGMGR_QUEUE','DIRTY_PAGE_POLL','HADR_FILESTREAM_IOMGR_IOCOMPLETION',
      'SP_SERVER_DIAGNOSTICS_SLEEP','SQLTRACE_INCREMENTAL_FLUSH_SLEEP','WAITFOR','QDS_PERSIST_TASK_MAIN_LOOP_SLEEP',
      'QDS_CLEANUP_STALE_QUERIES_TASK_MAIN_LOOP_SLEEP','QDS_ASYNC_QUEUE','WAIT_XTP_OFFLINE_CKPT_NEW_LOG',
      'PREEMPTIVE_XE_DISPATCHER','PWAIT_ALL_COMPONENTS_INITIALIZED','PWAIT_DIRECTLOGCONSUMER_GETNEXT',
      'SOS_WORK_DISPATCHER','KSOURCE_WAKEUP','XE_LIVE_TARGET_TVF')
ORDER BY wait_time_ms DESC;

PRINT '=== 7. Every wait SQL Server spent outside itself (PREEMPTIVE_*) ===';
SELECT
    wait_type,
    waiting_tasks_count,
    CONVERT(decimal(14,1), wait_time_ms / 1000.0)     AS total_wait_s,
    CONVERT(decimal(14,1), max_wait_time_ms / 1000.0) AS longest_single_wait_s
FROM sys.dm_os_wait_stats
WHERE wait_type LIKE 'PREEMPTIVE%' AND wait_time_ms > 0
ORDER BY wait_time_ms DESC;

PRINT '=== 8. Slow reads and writes to the database files since start ===';
SELECT
    DB_NAME(vfs.database_id)                                                   AS db,
    mf.type_desc,
    vfs.num_of_reads,
    CONVERT(decimal(12,1), vfs.io_stall_read_ms  * 1.0 / NULLIF(vfs.num_of_reads, 0))  AS avg_read_ms,
    vfs.num_of_writes,
    CONVERT(decimal(12,1), vfs.io_stall_write_ms * 1.0 / NULLIF(vfs.num_of_writes, 0)) AS avg_write_ms
FROM sys.dm_io_virtual_file_stats(NULL, NULL) vfs
JOIN sys.master_files mf ON mf.database_id = vfs.database_id AND mf.file_id = vfs.file_id
WHERE DB_NAME(vfs.database_id) LIKE N'MerkaiTrial%' OR vfs.database_id = 2
ORDER BY db, mf.type_desc;

PRINT '=== 9. File growths and shrinks in the last 3 days (default trace) ===';
BEGIN TRY
    DECLARE @trace nvarchar(260) = (SELECT TOP (1) path FROM sys.traces WHERE is_default = 1);
    IF @trace IS NULL
        PRINT 'The default trace is switched off on this server.';
    ELSE
        SELECT TOP (50)
            t.StartTime,
            te.name                                   AS event,
            t.DatabaseName,
            t.FileName,
            CONVERT(decimal(12,1), t.Duration / 1000000.0) AS took_s,
            CONVERT(decimal(12,1), t.IntegerData * 8 / 1024.0) AS grew_mb
        FROM sys.fn_trace_gettable(@trace, DEFAULT) t
        JOIN sys.trace_events te ON te.trace_event_id = t.EventClass
        WHERE t.EventClass IN (92, 93, 94, 95)          -- data/log auto-grow, auto-shrink
          AND t.StartTime > DATEADD(day, -3, GETDATE())
        ORDER BY t.StartTime DESC;
END TRY
BEGIN CATCH
    PRINT 'Could not read the default trace (needs sysadmin or ALTER TRACE): ' + ERROR_MESSAGE();
END CATCH;

PRINT '=== 10. SQL Server error log, last 3 days, lines that explain stalls ===';
BEGIN TRY
    CREATE TABLE #log (LogDate datetime, ProcessInfo nvarchar(100), Text nvarchar(max));
    INSERT INTO #log EXEC sys.xp_readerrorlog 0, 1;
    INSERT INTO #log EXEC sys.xp_readerrorlog 1, 1;
    SELECT TOP (60) LogDate, LEFT(Text, 300) AS Text
    FROM #log
    WHERE LogDate > DATEADD(day, -3, GETDATE())
      AND (Text LIKE '%taking longer than%'
        OR Text LIKE '%autogrow%'
        OR Text LIKE '%memory%'
        OR Text LIKE '%paged out%'
        OR Text LIKE '%registry%'
        OR Text LIKE '%non-yielding%'
        OR Text LIKE '%Starting up database%'
        OR Text LIKE '%Error:%'
        OR Text LIKE '%time zone%')
    ORDER BY LogDate DESC;
    DROP TABLE #log;
END TRY
BEGIN CATCH
    PRINT 'Could not read the error log (needs sysadmin): ' + ERROR_MESSAGE();
END CATCH;

PRINT '=== Done ===';
