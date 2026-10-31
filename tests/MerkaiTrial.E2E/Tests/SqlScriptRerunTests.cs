// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/SqlScriptRerunTests.cs
//
// NEW (110). Every Sql\*.sql script can be run again safely.
//
// When you upgrade production you'll run the rounds' scripts, and
// sometimes one twice — a script that was already run, a round re-applied
// after a problem, a list run from the top. Each script must notice
// what's already done and leave it alone.
//
// What this does:
//   1. Backs up the test database (COPY_ONLY) and restores it as a
//      throw-away copy, MerkaiTrial_ScriptCheck_E2E. The test database
//      and your development database are never touched by the scripts.
//   2. Takes a picture of the copy: schema, every table's rows (count
//      and checksum), and which views/procedures compile.
//   3. Runs every script in round order — run 1. The copy already has
//      them all (it's a copy of a database they were run on), so this is
//      already a second run.
//   4. Pictures it, runs every script again — run 2 — and pictures it
//      again.
//
// It FAILS when:
//   • any batch of any script fails, in either run (each failure is listed
//     with the file, the line and SQL Server's message), or a script
//     leaves a transaction open
//   • run 2 changes the schema at all (a duplicate index, a column
//     changed back and forth, a constraint re-made differently)
//   • run 2 changes any data (a table's row count or checksum differs)
//   • a view, procedure or function stops compiling, or a constraint or
//     index ends up disabled / not trusted, that was fine before
//
// What run 1 changed compared with the test database is reported but
// does not fail: that is usually a script the test database never had.
//
// The full report: bin\Debug\net8.0\e2e-artifacts\sql-script-check.txt.
// The copy is dropped when the test passes; when it fails it is left for
// you to look at in SSMS, and the next run replaces it.
// =====================================================================

using System.Text;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Database"), NonParallelizable]
public class SqlScriptRerunTests : E2ETest
{
    private const string Copy = "MerkaiTrial_ScriptCheck_E2E";
    private const int ShowInMessage = 15;

    [Test]
    public async Task Every_Sql_script_runs_twice_without_errors_or_changes()
    {
        var folder = await Step("Find the solution's Sql folder", () => Task.FromResult(SqlScripts.FindFolder()));

        var scripts = await Step("Read the scripts; set aside any that name or change a database", () =>
        {
            var all = SqlScripts.Load(folder);
            Assert.That(all, Is.Not.Empty, $"There are no .sql files in {folder}.");
            return Task.FromResult(all);
        });

        var toRun = scripts.Where(s => s.SkipReason is null).ToList();
        TestContext.Progress.WriteLine($"      {scripts.Count} script(s) in {folder}; {toRun.Count} will run" +
                                       (scripts.Count > toRun.Count ? $", {scripts.Count - toRun.Count} set aside" : ""));
        foreach (var s in scripts.Where(s => s.SkipReason is not null))
            TestContext.Progress.WriteLine($"      set aside: {s.Name} — {s.SkipReason}");
        foreach (var s in scripts.Where(s => s.Notes.Count > 0))
            TestContext.Progress.WriteLine($"      note: {s.Name} — {string.Join("; ", s.Notes)}");

        await Step("This SQL login may back up and restore databases", DatabaseCopies.CheckPermissionsAsync);

        var source = DatabaseCopies.TestDatabase;
        var backup = await Step($"Back up {source} (COPY_ONLY)", () => DatabaseCopies.BackupAsync(source));
        await Step($"Restore it as {Copy}", () => DatabaseCopies.RestoreAsCopyAsync(backup, Copy));

        var cs = DatabaseCopies.ConnectionStringFor(Copy);
        var passed = false;
        try
        {
            var before = await Step("Picture the copy: schema, data, what compiles", () => DbSnapshot.TakeAsync(cs));
            var run1 = await Step($"Run all {toRun.Count} script(s) — run 1", () => SqlScripts.RunAllAsync(cs, toRun, 1));
            var after1 = await Step("Picture it again", () => DbSnapshot.TakeAsync(cs));
            var run2 = await Step($"Run all {toRun.Count} script(s) again — run 2", () => SqlScripts.RunAllAsync(cs, toRun, 2));
            var after2 = await Step("Picture it a third time", () => DbSnapshot.TakeAsync(cs));

            var failures = run1.Concat(run2).ToList();
            var schema2 = DbSnapshot.DiffSchema(after1, after2);
            var data2 = DbSnapshot.DiffData(after1, after2);
            var broken = DbSnapshot.NewlyBroken(before, after2);
            var unenforced = DbSnapshot.NewlyUnenforced(before, after2);
            var schema1 = DbSnapshot.DiffSchema(before, after1);
            var data1 = DbSnapshot.DiffData(before, after1);

            var report = new StringBuilder()
                .AppendLine($"Sql scripts run twice — {DateTime.Now:yyyy-MM-dd HH:mm} — {folder}")
                .AppendLine($"{scripts.Count} script(s), {toRun.Count} run, on a copy of {source} ({Copy}).")
                .AppendLine();
            Section(report, "Set aside (not run)", scripts.Where(s => s.SkipReason is not null).Select(s => $"{s.Name} — {s.SkipReason}"));
            Section(report, "Notes", scripts.Where(s => s.Notes.Count > 0).Select(s => $"{s.Name} — {string.Join("; ", s.Notes)}"));
            Section(report, "FAILED batches", failures.Select(f => f.ToString()));
            Section(report, "Schema changed by run 2 (should be nothing)", schema2);
            Section(report, "Data changed by run 2 (should be nothing)", data2);
            Section(report, "Stopped compiling (worked before the scripts ran)", broken);
            Section(report, "Ended up disabled or not trusted", unenforced);
            Section(report, "For information: what run 1 changed in the copy of the test database", schema1.Concat(data1));
            report.AppendLine("Scripts in the order they ran:");
            foreach (var s in toRun) report.AppendLine($"  {s.Name} ({s.Batches.Count} batch(es))");
            var file = WriteReport(report.ToString());

            if (schema1.Count + data1.Count > 0)
                TestContext.Progress.WriteLine(
                    $"      for information: run 1 changed {schema1.Count} schema line(s) and {data1.Count} table(s) " +
                    $"in the copy — usually a script the test database never had. See {file}");

            var problems = new List<string>();
            Add(problems, "Script batches that failed", failures.Select(f => f.ToString()));
            Add(problems, "Run 2 changed the schema (a script isn't checking what's already there)", schema2);
            Add(problems, "Run 2 changed data (a script changes rows every time it runs)", data2);
            Add(problems, "Views, procedures or functions that no longer compile", broken);
            Add(problems, "Indexes or constraints left disabled or not trusted", unenforced);

            if (problems.Count > 0)
                Assert.Fail("Running the Sql scripts twice isn't safe yet.\n\n" + string.Join("\n\n", problems) +
                            $"\n\nFull report: {file}\nThe copy {Copy} is left on the server to look at; the next run replaces it.");
            passed = true;
        }
        finally
        {
            if (passed)
            {
                try { await DatabaseCopies.DropIfExistsAsync(Copy); }
                catch (Exception ex) { TestContext.Progress.WriteLine($"      Couldn't drop {Copy}: {ex.Message}"); }
            }
        }
    }

    private static void Section(StringBuilder sb, string title, IEnumerable<string> lines)
    {
        var list = lines.ToList();
        sb.AppendLine($"{title}: {(list.Count == 0 ? "none" : list.Count.ToString())}");
        foreach (var l in list) sb.AppendLine("  " + l);
        sb.AppendLine();
    }

    private static void Add(List<string> problems, string title, IEnumerable<string> lines)
    {
        var list = lines.ToList();
        if (list.Count == 0) return;
        var shown = list.Take(ShowInMessage).Select(l => "  • " + l);
        problems.Add($"{title} ({list.Count}):\n{string.Join("\n", shown)}" +
                     (list.Count > ShowInMessage ? $"\n  …and {list.Count - ShowInMessage} more in the report" : ""));
    }

    private static string WriteReport(string text)
    {
        var file = Path.Combine(E2ESettings.ArtifactsDir, "sql-script-check.txt");
        try
        {
            Directory.CreateDirectory(E2ESettings.ArtifactsDir);
            File.WriteAllText(file, text);
        }
        catch (Exception ex)
        {
            TestContext.Progress.WriteLine($"      Couldn't write {file}: {ex.Message}");
        }
        return file;
    }
}
