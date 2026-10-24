// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/LeadImportIntegrityTests.cs
//
// TESTS 85 and 86 (round 102) — the lead import, through its API (upload
// → review → import), the same three calls the Import leads page makes.
//
// 85 Import_with_duplicate_rows_does_not_create_unintended_duplicates
//    • inside one file: the same email (in another case) and the same
//      phone (written another way) are skipped, each naming the first row;
//    • against the workspace: a person who is already a lead is skipped;
//    • the same file imported again adds nobody;
//    • "Import anyway" with a person who is already a lead AND listed
//      twice in the file adds them ONCE (round 102: the file is checked
//      before the database);
//    • one upload sent twice at the same moment (a double-click) imports
//      once — the second is told the file was already imported;
//    • the same list uploaded twice and committed at the same moment
//      imports each person once.
//
// 86 Failed_lead_import_reports_row_errors_without_partial_corruption
//    • a row with no name is reported as an error, by row number, in the
//      review AND in the result — and nothing is written for it;
//    • an email, phone or source longer than a lead can hold is reported
//      on its row and left out, and the rest of the row is imported
//      (before round 102 it made the WHOLE import fail with "nothing was
//      saved" and no row number);
//    • an import refused for the plan limit writes nothing at all.
// =====================================================================

using System.Text;
using System.Text.Json;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture]
public class LeadImportIntegrityTests : E2ETest
{
    private static readonly Dictionary<string, int> Mapping = new()
    {
        ["fullName"] = 0, ["email"] = 1, ["phone"] = 2, ["companyName"] = 3, ["source"] = 4,
    };

    // =================================================================
    // 85
    // =================================================================

    [Test]
    public async Task Import_with_duplicate_rows_does_not_create_unintended_duplicates()
    {
        var admin = await Api.AsAsync(World.A.Admin);
        await Api.EnsureTokensWorkAsync(admin);
        var tenantId = await Db.TenantIdAsync(World.A.Name);
        var m = "dupimp" + Random.Shared.Next(10000, 99999);
        var company = $"Import Dupes {m}";

        string Email(string who) => $"{who}.{m}@a{World.Stamp.Replace("-", "")}.example.com";
        var p1 = Phone(); var p2 = Phone();

        await Step("Cy is already a lead (imported on their own first)", async () =>
        {
            var r = await ImportAsync(admin, Csv(("Cy " + m, Email("cy"), "", company)), "skip");
            Assert.That(r.GetProperty("imported").GetInt32(), Is.EqualTo(1), $"Setting up Cy failed: {r}");
        });

        var file1 = Csv(
            ("Ana " + m, Email("ana"), p1, company),                    // row 2
            ("Ana again " + m, Email("ana").ToUpperInvariant(), "", company),  // row 3 — same email, other case
            ("Bo " + m, Email("bo"), "+91 " + p2[..5] + " " + p2[5..], company), // row 4
            ("Bo by phone " + m, "", "0" + p2, company),                // row 5 — same phone, written another way
            ("Cy " + m, Email("cy"), "", company),                      // row 6 — already a lead
            ("Cy again " + m, Email("cy"), "", company));               // row 7 — and again in the file

        await Step("Review: in-file repeats name their first row, and Cy is already a lead", async () =>
        {
            var session = await UploadAsync(admin, file1);
            var preview = await PreviewAsync(admin, session, "skip");
            var rows = Rows(preview);

            ExpectRow(rows, 3, "duplicate", "row 2");
            ExpectRow(rows, 5, "duplicate", "row 4");
            ExpectRow(rows, 6, "duplicate", "Already in your leads");
            ExpectRow(rows, 7, "duplicate", "row 6");
            Assert.That(preview.GetProperty("willImport").GetInt32(), Is.EqualTo(2), $"Review: {Short(preview)}");
            Assert.That(preview.GetProperty("willSkip").GetInt32(), Is.EqualTo(4), $"Review: {Short(preview)}");
        });

        await Step("Import (skip duplicates): Ana and Bo are added once each, Cy stays one lead", async () =>
        {
            var r = await ImportAsync(admin, file1, "skip");
            Assert.That(r.GetProperty("imported").GetInt32(), Is.EqualTo(2), $"Result: {Short(r)}");
            await ExpectCountsAsync(tenantId, Email("ana"), 1, Email("bo"), 1, Email("cy"), 1);
            Assert.That((await Db.LeadsMatchingAsync(tenantId, company)).Count, Is.EqualTo(3),
                "The file should leave exactly three leads: Ana, Bo and Cy.");
        });

        await Step("The same file imported again adds nobody", async () =>
        {
            var r = await ImportAsync(admin, file1, "skip");
            Assert.That(r.GetProperty("imported").GetInt32(), Is.EqualTo(0), $"Result: {Short(r)}");
            Assert.That((await Db.LeadsMatchingAsync(tenantId, company)).Count, Is.EqualTo(3), "A second import of the same file added leads.");
        });

        await Step("\"Import anyway\" with Cy twice in the file adds Cy once, not twice", async () =>
        {
            var file2 = Csv(("Cy " + m, Email("cy"), "", company), ("Cy again " + m, Email("cy"), "", company));
            var r = await ImportAsync(admin, file2, "import");
            Assert.That(r.GetProperty("imported").GetInt32(), Is.EqualTo(1),
                $"\"Import anyway\" imported a person listed twice in the file twice: {Short(r)}");
            await ExpectCountsAsync(tenantId, Email("cy"), 2);
        });

        await Step("One upload sent twice at the same moment imports once", async () =>
        {
            var file3 = Csv(("Dee " + m, Email("dee"), "", company), ("Eve " + m, Email("eve"), "", company));
            var session = await UploadAsync(admin, file3);
            var body = Body(session, "skip");

            var answers = await Api.AllAtOnceAsync(
                () => admin.PostAsync("/api/leads/import/commit", body),
                () => admin.PostAsync("/api/leads/import/commit", body));
            TestContext.Progress.WriteLine($"    the two commits answered {answers[0].Status}, {answers[1].Status}");

            var ok = answers.Where(a => a.IsSuccess).ToList();
            Assert.That(ok, Has.Count.EqualTo(1), $"Expected exactly one commit to succeed: {answers[0]} / {answers[1]}");
            Assert.That(ok[0].Json().GetProperty("imported").GetInt32(), Is.EqualTo(2));

            var refused = answers.Single(a => !a.IsSuccess);
            Assert.That(refused.Status, Is.LessThan(500), $"The second commit failed with a server error: {refused}");
            Assert.That(refused.Error, Does.Contain("already been imported"), $"The second commit didn't say why: {refused}");

            await ExpectCountsAsync(tenantId, Email("dee"), 1, Email("eve"), 1);
        });

        await Step("The same list uploaded twice and committed at the same moment imports each person once", async () =>
        {
            var file4 = Csv(("Fay " + m, Email("fay"), "", company), ("Gus " + m, Email("gus"), "", company));
            var s1 = await UploadAsync(admin, file4);
            var s2 = await UploadAsync(admin, file4);

            var answers = await Api.AllAtOnceAsync(
                () => admin.PostAsync("/api/leads/import/commit", Body(s1, "skip")),
                () => admin.PostAsync("/api/leads/import/commit", Body(s2, "skip")));
            TestContext.Progress.WriteLine($"    the two commits answered {answers[0].Status}, {answers[1].Status}");

            Assert.That(answers.All(a => a.IsSuccess), Is.True, $"Both commits should succeed: {answers[0]} / {answers[1]}");
            var total = answers.Sum(a => a.Json().GetProperty("imported").GetInt32());
            Assert.That(total, Is.EqualTo(2), $"Two uploads of a two-person list imported {total} people.");
            await ExpectCountsAsync(tenantId, Email("fay"), 1, Email("gus"), 1);
        });
    }

    // =================================================================
    // 86
    // =================================================================

    [Test]
    public async Task Failed_lead_import_reports_row_errors_without_partial_corruption()
    {
        var admin = await Api.AsAsync(World.A.Admin);
        await Api.EnsureTokensWorkAsync(admin);
        var tenantId = await Db.TenantIdAsync(World.A.Name);
        var m = "badimp" + Random.Shared.Next(10000, 99999);
        var company = $"Import Errors {m}";
        string Email(string who) => $"{who}.{m}@a{World.Stamp.Replace("-", "")}.example.com";

        var longEmail = new string('x', 240) + "@long.example.com";       // 257 characters; a lead holds 200
        var longPhone = string.Concat(Enumerable.Repeat("9876543210", 6)); // 60 characters; a lead holds 50
        var longSource = "Trade fair " + new string('s', 150);             // 161 characters; a lead holds 100

        var file = Csv(
            ("Good One " + m, Email("one"), "", company, ""),                  // row 2
            ("", Email("noname"), "", company, ""),                           // row 3 — no name
            ("Long Email " + m, longEmail, "", company, ""),                  // row 4
            ("Long Phone " + m, Email("phone"), longPhone, company, ""),      // row 5
            ("Good Two " + m, Email("two"), "", company, ""),                 // row 6
            ("Long Source " + m, Email("source"), "", company, longSource));  // row 7

        await Step("Review: the nameless row is an error, the over-long values are warned about on their rows", async () =>
        {
            var preview = await PreviewAsync(admin, await UploadAsync(admin, file), "skip");
            var rows = Rows(preview);

            ExpectRow(rows, 3, "error", "Name is missing");
            ExpectRow(rows, 4, "warning", "characters long");
            ExpectRow(rows, 5, "warning", "characters long");
            Assert.That(preview.GetProperty("errorCount").GetInt32(), Is.EqualTo(1), $"Review: {Short(preview)}");
            Assert.That(preview.GetProperty("willImport").GetInt32(), Is.EqualTo(5), $"Review: {Short(preview)}");
        });

        await Step("Import: five rows in, the nameless one reported by row number — not a failed import", async () =>
        {
            var session = await UploadAsync(admin, file);
            var r = await admin.PostAsync("/api/leads/import/commit", Body(session, "skip"));
            Assert.That(r.IsSuccess, Is.True,
                $"The import failed as a whole: {r}. An over-long value must be reported on its row, not stop every row.");

            var result = r.Json();
            Assert.That(result.GetProperty("imported").GetInt32(), Is.EqualTo(5), $"Result: {Short(result)}");
            Assert.That(result.GetProperty("failed").GetInt32(), Is.EqualTo(1), $"Result: {Short(result)}");
            var failedRows = result.GetProperty("failedRows").EnumerateArray().Select(x => x.GetProperty("rowNumber").GetInt32()).ToList();
            Assert.That(failedRows, Is.EquivalentTo(new[] { 3 }), "The result should list row 3, and only row 3, as not imported.");
        });

        await Step("What was written is exactly the five good rows, with over-long values left out", async () =>
        {
            var leads = await Db.LeadsMatchingAsync(tenantId, company);
            Assert.That(leads.Select(l => l.FullName), Is.EquivalentTo(new[]
            {
                "Good One " + m, "Long Email " + m, "Long Phone " + m, "Good Two " + m, "Long Source " + m,
            }), "The leads written are not the five good rows.");

            Assert.That(leads.Single(l => l.FullName.StartsWith("Long Email")).Email, Is.Null, "The over-long email was stored.");
            Assert.That(leads.Single(l => l.FullName.StartsWith("Long Phone")).Phone, Is.Null, "The over-long phone was stored.");
            var source = leads.Single(l => l.FullName.StartsWith("Long Source")).Source ?? "";
            Assert.That(source.Length, Is.LessThanOrEqualTo(100), "The source label was stored longer than a lead holds.");
            Assert.That(leads.Any(l => l.Email == Email("noname")), Is.False, "The nameless row was written.");
        });

        await Step("An import refused for the plan limit writes nothing at all", async () =>
        {
            var original = await Db.MaxLeadsAsync(tenantId);
            Assert.That(original, Is.Not.Null, "Workspace A has no TenantSettings row, so its lead limit can't be tested.");

            var m2 = m + "q";
            var quotaFile = Csv(
                ("Quota One " + m2, Email("q1"), "", company + " quota"),
                ("Quota Two " + m2, Email("q2"), "", company + " quota"),
                ("Quota Three " + m2, Email("q3"), "", company + " quota"));
            try
            {
                var current = await Db.CountLiveLeadsAsync(tenantId);
                await Db.SetMaxLeadsAsync(tenantId, current + 1);      // room for one, the file has three

                var session = await UploadAsync(admin, quotaFile);
                var preview = await PreviewAsync(admin, session, "skip");
                Assert.That(preview.GetProperty("exceedsQuota").GetBoolean(), Is.True, "The review did not warn about the plan limit.");

                var r = await admin.PostAsync("/api/leads/import/commit", Body(session, "skip"));
                Assert.That(r.Status, Is.EqualTo(400), $"An import over the plan limit was not refused: {r}");
                Assert.That(r.Error, Does.Contain("plan limit"), $"The refusal didn't say why: {r}");
                Assert.That((await Db.LeadsMatchingAsync(tenantId, company + " quota")).Count, Is.EqualTo(0),
                    "A refused import still wrote some of its leads.");
            }
            finally
            {
                await Db.SetMaxLeadsAsync(tenantId, original!.Value);
            }
        });
    }

    // ── helpers ──────────────────────────────────────────────────────

    /// <summary>A CSV with the columns Name, Email, Phone, Company, Source.</summary>
    private static byte[] Csv(params (string Name, string Email, string Phone, string Company)[] rows) =>
        Csv(rows.Select(r => (r.Name, r.Email, r.Phone, r.Company, "")).ToArray());

    private static byte[] Csv(params (string Name, string Email, string Phone, string Company, string Source)[] rows)
    {
        static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder("Name,Email,Phone,Company,Source\r\n");
        foreach (var r in rows)
            sb.Append(string.Join(",", Q(r.Name), Q(r.Email), Q(r.Phone), Q(r.Company), Q(r.Source))).Append("\r\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>A ten-digit Indian mobile number, unique enough for one run.</summary>
    private static string Phone() => "9" + Random.Shared.NextInt64(100_000_000, 999_999_999).ToString();

    private static async Task<Guid> UploadAsync(ApiCaller admin, byte[] csv)
    {
        var r = await admin.UploadAsync("/api/leads/import/upload", $"leads-{Guid.NewGuid():N}.csv", csv, "text/csv");
        Assert.That(r.IsSuccess, Is.True, $"Uploading the file failed: {r}");
        return r.Json().GetProperty("sessionId").GetGuid();
    }

    private static object Body(Guid sessionId, string duplicates) =>
        new { sessionId, tenantId = Guid.Empty, mapping = Mapping, duplicates };

    private static async Task<JsonElement> PreviewAsync(ApiCaller admin, Guid sessionId, string duplicates)
    {
        var r = await admin.PostAsync("/api/leads/import/preview", Body(sessionId, duplicates));
        Assert.That(r.IsSuccess, Is.True, $"The review failed: {r}");
        return r.Json();
    }

    /// <summary>Upload + import in one go. Returns the import's result.</summary>
    private static async Task<JsonElement> ImportAsync(ApiCaller admin, byte[] csv, string duplicates)
    {
        var session = await UploadAsync(admin, csv);
        var r = await admin.PostAsync("/api/leads/import/commit", Body(session, duplicates));
        Assert.That(r.IsSuccess, Is.True, $"The import failed: {r}");
        return r.Json();
    }

    private static Dictionary<int, JsonElement> Rows(JsonElement preview) =>
        preview.GetProperty("rows").EnumerateArray().ToDictionary(r => r.GetProperty("rowNumber").GetInt32());

    private static void ExpectRow(Dictionary<int, JsonElement> rows, int rowNumber, string verdict, string messageContains)
    {
        Assert.That(rows.ContainsKey(rowNumber), Is.True, $"The review has no row {rowNumber}.");
        var row = rows[rowNumber];
        var got = row.GetProperty("verdict").GetString();
        var messages = string.Join(" | ", row.GetProperty("messages").EnumerateArray().Select(x => x.GetString()));
        Assert.That(got, Is.EqualTo(verdict), $"Row {rowNumber}: expected \"{verdict}\", got \"{got}\" ({messages}).");
        Assert.That(messages, Does.Contain(messageContains).IgnoreCase,
            $"Row {rowNumber}'s message should mention \"{messageContains}\": {messages}");
    }

    private static async Task ExpectCountsAsync(Guid tenantId, params object[] emailCountPairs)
    {
        for (var i = 0; i < emailCountPairs.Length; i += 2)
        {
            var email = (string)emailCountPairs[i];
            var expected = (int)emailCountPairs[i + 1];
            var got = (await Db.LeadsMatchingAsync(tenantId, email)).Count;
            Assert.That(got, Is.EqualTo(expected), $"Leads with {email}: expected {expected}, found {got}.");
        }
    }

    private static string Short(JsonElement e) => ApiResponse.Short(e.ToString(), 400);
}
