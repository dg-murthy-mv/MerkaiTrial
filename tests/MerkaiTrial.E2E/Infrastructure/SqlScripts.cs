// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Infrastructure/SqlScripts.cs
//
// NEW (110). Reads the solution's Sql\*.sql upgrade scripts and runs them
// the way SSMS does, for SqlScriptRerunTests.
//
//   • Folder: <solution folder>\Sql (the folder each round's script goes
//     in). Set E2E_SQL_DIR to use another folder.
//   • Order: by the round number at the front of the name, as numbers —
//     99_ before 100_, and 101_ before 101b_ before 101c_.
//   • Batches: split on GO lines, as SSMS does ("GO 5" runs it 5 times).
//     A failed batch is recorded and the next one runs, as in SSMS.
//   • Each file gets its own connection with SSMS's settings
//     (ARITHABORT ON and the ANSI settings), so SET NOEXEC ON or an
//     open transaction can't leak into the next file. A transaction left
//     open at the end of a file is rolled back and reported.
//
// Safety — the scripts only ever run on the throw-away copy:
//   • USE lines are removed (the connection is already on the copy) and
//     listed, so "USE MerkaiTrial" can never send a script to your
//     development database.
//   • A script that names a database (MerkaiTrial.dbo.X), creates, drops,
//     backs up or restores one, or changes logins or server settings is
//     NOT run — it is listed as skipped, with the reason.
// =====================================================================

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace MerkaiTrial.E2E.Infrastructure;

public static class SqlScripts
{
    public sealed record Batch(int Number, int StartLine, string Text, int Repeat);

    public sealed record Script(string Name, string Path, IReadOnlyList<Batch> Batches, string? SkipReason,
        IReadOnlyList<string> Notes);

    public sealed record Failure(int Run, string File, int Batch, int Line, string Message, string Excerpt)
    {
        public override string ToString() =>
            $"run {Run}: {File}, batch {Batch} (near line {Line}): {Message}" +
            (Excerpt.Length > 0 ? $"\n        {Excerpt}" : "");
    }

    // ── finding and reading ───────────────────────────────────────────

    public static string FindFolder()
    {
        if (Environment.GetEnvironmentVariable("E2E_SQL_DIR") is { Length: > 0 } custom)
        {
            if (Directory.Exists(custom)) return custom;
            throw new AssertionException($"E2E_SQL_DIR is set to '{custom}', which isn't a folder.");
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (dir.GetFiles("*.sln").Length == 0) continue;
            var sql = Path.Combine(dir.FullName, "Sql");
            if (Directory.Exists(sql)) return sql;
            throw new AssertionException(
                $"Found the solution in {dir.FullName}, but no Sql folder next to it. " +
                "Put the rounds' scripts in <solution>\\Sql, or set E2E_SQL_DIR to the folder they are in.");
        }
        throw new AssertionException($"Couldn't find the solution (.sln) in any folder above {AppContext.BaseDirectory}.");
    }

    private static readonly Regex GoLine =
        new(@"^[ \t]*GO(?:[ \t]+(?<n>\d+))?[ \t]*(?:--[^\r\n]*)?\r?$", RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static readonly Regex UseLine =
        new(@"^[ \t]*USE[ \t]+\[?[A-Za-z0-9_]+\]?[ \t]*;?[ \t]*(?:--[^\r\n]*)?\r?$", RegexOptions.IgnoreCase | RegexOptions.Multiline);

    // Checked on the text with comments and strings removed.
    private static readonly (Regex Pattern, string Why)[] Refused =
    {
        (new(@"\b(CREATE|DROP)\s+DATABASE\b", RegexOptions.IgnoreCase), "it creates or drops a database"),
        (new(@"\bALTER\s+DATABASE\s+(?!CURRENT\b)", RegexOptions.IgnoreCase), "it changes a database by name (ALTER DATABASE CURRENT is fine)"),
        (new(@"\b(BACKUP|RESTORE)\s+(DATABASE|LOG)\b", RegexOptions.IgnoreCase), "it backs up or restores a database"),
        (new(@"\b(CREATE|ALTER|DROP)\s+LOGIN\b", RegexOptions.IgnoreCase), "it changes a server login"),
        (new(@"\bsp_configure\b|\bRECONFIGURE\b|\bSHUTDOWN\b|\bxp_cmdshell\b", RegexOptions.IgnoreCase), "it changes server settings"),
        (new(@"\[?\b\w+\b\]?\s*\.\s*\[?dbo\]?\s*\.\s*\[?\w+", RegexOptions.IgnoreCase), "it names a database (Database.dbo.Table)"),
        (new(@"^\s*:(setvar|r|connect|on)\b", RegexOptions.IgnoreCase | RegexOptions.Multiline), "it uses sqlcmd commands (:setvar, :r, :connect)"),
    };

    private static readonly Regex Comments = new(@"/\*.*?\*/|--[^\r\n]*", RegexOptions.Singleline);
    private static readonly Regex Strings = new(@"N?'(?:[^']|'')*'", RegexOptions.Singleline);

    /// <summary>Every Sql\*.sql in round order, split into batches, with anything unsafe marked skipped.</summary>
    public static List<Script> Load(string folder)
    {
        var files = Directory.GetFiles(folder, "*.sql", SearchOption.TopDirectoryOnly)
            .OrderBy(f => Path.GetFileName(f), RoundOrder.Instance)
            .ToList();

        var scripts = new List<Script>();
        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            var text = File.ReadAllText(path);
            var notes = new List<string>();

            // The database's name never matters here: the connection is the copy.
            var uses = UseLine.Matches(text).Select(m => m.Value.Trim()).ToList();
            if (uses.Count > 0)
            {
                notes.Add($"ignored {string.Join(", ", uses.Distinct())} — scripts run on the copy");
                text = UseLine.Replace(text, "");
            }

            var bare = Strings.Replace(Comments.Replace(text, " "), "''");
            string? skip = null;
            foreach (var (pattern, why) in Refused)
            {
                // "sys.dm_x" or "INFORMATION_SCHEMA.x.y" are not database names.
                var m = pattern.Match(bare);
                while (m.Success && why.StartsWith("it names a database") && IsSystemName(m.Value))
                    m = m.NextMatch();
                if (m.Success) { skip = $"{why}: \"{OneLine(m.Value)}\""; break; }
            }

            scripts.Add(new Script(name, path, Split(text), skip, notes));
        }
        return scripts;
    }

    private static bool IsSystemName(string match)
    {
        var first = match.TrimStart('[').Split('.', ']')[0].Trim();
        return first.Equals("tempdb", StringComparison.OrdinalIgnoreCase)
               || first.Equals("msdb", StringComparison.OrdinalIgnoreCase)
               || first.Equals("master", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The batches between GO lines, with the line each starts on.</summary>
    public static List<Batch> Split(string text)
    {
        var batches = new List<Batch>();
        var start = 0;
        var startLine = 1;
        var number = 0;

        void Add(int end, int repeat)
        {
            var body = text[start..end];
            if (body.Trim().Length > 0) batches.Add(new Batch(++number, startLine, body, repeat));
        }

        foreach (Match go in GoLine.Matches(text))
        {
            var repeat = go.Groups["n"].Success ? int.Parse(go.Groups["n"].Value) : 1;
            Add(go.Index, repeat);
            startLine += CountLines(text, start, go.Index + go.Length);
            start = go.Index + go.Length;
        }
        Add(text.Length, 1);
        return batches;
    }

    private static int CountLines(string text, int from, int to)
    {
        var n = 0;
        for (var i = from; i < to && i < text.Length; i++) if (text[i] == '\n') n++;
        return n;
    }

    /// <summary>"99_x" before "100_x"; "101_x" before "101b_x" before "101c_x"; names without a number last.</summary>
    private sealed class RoundOrder : IComparer<string>
    {
        public static readonly RoundOrder Instance = new();
        private static readonly Regex Lead = new(@"^(\d+)(.*)$");

        public int Compare(string? a, string? b)
        {
            var ma = Lead.Match(a ?? "");
            var mb = Lead.Match(b ?? "");
            if (ma.Success && mb.Success)
            {
                var c = decimal.Parse(ma.Groups[1].Value).CompareTo(decimal.Parse(mb.Groups[1].Value));
                if (c != 0) return c;
                // "_x" (no letter) before "b_x": compare what follows, with "_" lowest.
                return string.Compare(Key(ma.Groups[2].Value), Key(mb.Groups[2].Value), StringComparison.OrdinalIgnoreCase);
            }
            if (ma.Success) return -1;
            if (mb.Success) return 1;
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static string Key(string rest) => rest.Replace('_', '\u0001').Replace('-', '\u0001').Replace(' ', '\u0001');
    }

    // ── running ───────────────────────────────────────────────────────

    private const string SsmsSettings =
        "SET ARITHABORT ON; SET ANSI_NULLS ON; SET QUOTED_IDENTIFIER ON; SET ANSI_PADDING ON; " +
        "SET ANSI_WARNINGS ON; SET CONCAT_NULL_YIELDS_NULL ON; SET NUMERIC_ROUNDABORT OFF; SET NOCOUNT ON;";

    /// <summary>Runs every script that isn't skipped. Returns what failed.</summary>
    public static async Task<List<Failure>> RunAllAsync(string connectionString, IEnumerable<Script> scripts, int run)
    {
        var failures = new List<Failure>();
        foreach (var script in scripts.Where(s => s.SkipReason is null))
        {
            var clock = Stopwatch.StartNew();
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();
            await ExecAsync(conn, SsmsSettings);

            foreach (var batch in script.Batches)
            {
                for (var i = 0; i < batch.Repeat; i++)
                {
                    try
                    {
                        await using var cmd = new SqlCommand(batch.Text, conn) { CommandTimeout = 0 };
                        await cmd.ExecuteNonQueryAsync();
                    }
                    catch (SqlException ex)
                    {
                        var errors = ex.Errors.Cast<SqlError>().Where(e => e.Class > 10).ToList();
                        if (errors.Count == 0) errors = ex.Errors.Cast<SqlError>().ToList();
                        var first = errors.FirstOrDefault();
                        var line = batch.StartLine + Math.Max(0, (first?.LineNumber ?? 1) - 1);
                        var message = string.Join(" | ", errors.Select(e => $"Msg {e.Number}: {e.Message}").Distinct());
                        failures.Add(new Failure(run, script.Name, batch.Number, line, message, Excerpt(batch.Text, first?.LineNumber ?? 1)));

                        if (conn.State != System.Data.ConnectionState.Open)
                        {
                            await conn.OpenAsync();
                            await ExecAsync(conn, SsmsSettings);
                        }
                    }
                }
            }

            // A transaction the script left open would hold locks for ever in production.
            try
            {
                await using var tc = new SqlCommand("SELECT @@TRANCOUNT", conn);
                if (Convert.ToInt32(await tc.ExecuteScalarAsync()) > 0)
                {
                    await ExecAsync(conn, "ROLLBACK");
                    failures.Add(new Failure(run, script.Name, 0, 0,
                        "left a transaction open at the end (it was rolled back here; in production it would hold its locks)", ""));
                }
            }
            catch (SqlException) { /* the connection went with an error already recorded */ }

            if (clock.Elapsed.TotalSeconds >= 10)
                TestContext.Progress.WriteLine($"      run {run}: {script.Name} took {clock.Elapsed.TotalSeconds:0}s");
        }
        return failures;
    }

    private static async Task ExecAsync(SqlConnection conn, string sql)
    {
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
        await cmd.ExecuteNonQueryAsync();
    }

    private static string Excerpt(string batch, int line)
    {
        var lines = batch.Replace("\r", "").Split('\n');
        var i = Math.Clamp(line - 1, 0, Math.Max(0, lines.Length - 1));
        var text = lines.Length == 0 ? "" : lines[i].Trim();
        if (text.Length == 0)
            text = lines.Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("--")) ?? "";
        return text.Length > 150 ? text[..150] + "…" : text;
    }

    private static string OneLine(string s)
    {
        var t = Regex.Replace(s, @"\s+", " ").Trim();
        return t.Length > 80 ? t[..80] + "…" : t;
    }
}
