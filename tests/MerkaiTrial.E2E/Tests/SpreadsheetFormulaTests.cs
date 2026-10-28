// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/SpreadsheetFormulaTests.cs
//
// VUL-15 (round 105) — exported text can't become a formula.
//
//   Exported_text_that_looks_like_a_formula_stays_text
//
// A lead whose name is =HYPERLINK("https://evil.example",…), whose company
// is +cmd|' /C calc'!A0 and so on, is just text to the app. Written past
// every check (straight into the database — whatever got in through an
// import or an old version), then exported:
//
//   • the lead export (Leads → Export) and a report export (Reports →
//     Rep performance, whose owner name is such text) hold NO formula
//     anywhere — every such cell is stored as text;
//   • every text cell that starts with = + - @ is marked "treat as text"
//     (quote prefix, like typing ' in front in Excel), so it stays text
//     even after someone edits the cell;
//   • the text itself is unchanged, so the file imports back exactly.
//
// The first run of this test found the rep performance and pipeline
// reports calling every owner "Unassigned" (a capital-letters mismatch on
// the owner's id) — fixed in ReportQueries.cs.
//
// Agreed in 105: a copy someone re-saves as CSV by hand loses the mark,
// as any CSV would. The app's exports are .xlsx only.
// =====================================================================

using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Security")]
public class SpreadsheetFormulaTests : E2ETest
{
    [Test]
    public async Task Exported_text_that_looks_like_a_formula_stays_text()
    {
        var a = World.A;
        var tenantA = await Db.TenantIdAsync(a.Name);
        var apiA = await Api.AsAsync(a.Admin);
        var m = Random.Shared.Next(10000, 99999).ToString();
        var problems = new List<string>();

        var payloads = new Dictionary<string, string>
        {
            ["FullName"]    = $"=HYPERLINK(\"https://evil.example/{m}\",\"Click for details\")",
            ["CompanyName"] = $"+cmd|' /C calc'!A0 {m}",
            ["Address"]     = $"@SUM(1+1)*cmd|' /C calc'!A0 {m}",
            ["Phone"]       = $"-2+3+cmd|' /C calc'!A{m}",
        };

        var email = World.EmailFor("A", "formula" + m);

        await Step("A lead whose name, company, address and phone are formulas (written past every check)", async () =>
        {
            var leadId = await ApiRecords.LeadAsync(apiA, tenantA, "Formula Lead " + m, "formula" + m);
            foreach (var (column, value) in payloads)
                await Db.SetLeadTextAsync(leadId, column, value);
        });

        await Step("The lead export: no formula anywhere, each such cell is text, marked, and unchanged", async () =>
        {
            var r = await apiA.GetAsync("/api/leads/export?search=" + Uri.EscapeDataString(email));
            Assert.That(r.IsSuccess, Is.True, $"The lead export failed: {r}");
            JudgeWorkbook("The lead export", r.Bytes, payloads.Values, problems);
        });

        await Step("A report export whose owner's name is a formula: the same", async () =>
        {
            var rep = await WorldBuilder.AddPersonForTestAsync(a, "formularep" + m, "Rhea", "Sales Representative");
            var repId = await Db.UserIdAsync(rep.Email);

            var contact = await ApiRecords.ContactAsync(apiA, tenantA, "Formula Contact " + m, "formulac" + m);
            await ApiRecords.DealAsync(apiA, tenantA, contact, "Formula Deal " + m, repId.ToString());

            var firstName = $"=1+2+cmd|' /C calc'!A{m}";
            await Db.SetUserFirstNameAsync(repId, firstName);

            var from = DateTime.UtcNow.Date.AddDays(-2).ToString("yyyy-MM-dd");
            var to = DateTime.UtcNow.Date.AddDays(2).ToString("yyyy-MM-dd");
            var r = await apiA.GetAsync($"/api/reports/rep-performance/export?from={from}&to={to}");
            Assert.That(r.IsSuccess, Is.True, $"The rep performance export failed: {r}");

            // The owner cell is "<first name> <last name>".
            JudgeWorkbook("The rep performance export", r.Bytes, new[] { firstName + " E2E" }, problems, startsWith: true);
        });

        Assert.That(problems, Is.Empty, $"{problems.Count} problem(s):\n  • " + string.Join("\n  • ", problems));
    }

    private static void JudgeWorkbook(string what, byte[] bytes, IEnumerable<string> expected, List<string> problems, bool startsWith = false)
    {
        if (XlsxCells.HasAnyFormula(bytes))
            problems.Add($"{what} contains a FORMULA — Excel would run it on opening.");

        var cells = XlsxCells.Read(bytes);

        foreach (var value in expected)
        {
            var cell = cells.FirstOrDefault(c => startsWith
                ? c.Text.StartsWith(value, StringComparison.Ordinal)
                : c.Text == value);

            if (cell is null)
            {
                var near = cells.FirstOrDefault(c => c.Text.Contains(value[1..Math.Min(value.Length, 12)], StringComparison.Ordinal));
                problems.Add(near is null
                    ? $"{what} doesn't hold '{value}' at all, so it can't be checked. Its first column: " +
                      string.Join(" | ", cells.Where(c => c.Ref.StartsWith('A') && !char.IsLetter(c.Ref[1])).Select(c => c.Text).Take(12))
                    : $"{what} changed '{value}' to '{near.Text}' — exports should keep the text exactly.");
                continue;
            }

            if (cell.HasFormula) problems.Add($"{what}: cell {cell.Ref} ('{Short(value)}') is a formula.");
            if (cell.Type is not ("s" or "inlineStr" or "str"))
                problems.Add($"{what}: cell {cell.Ref} ('{Short(value)}') is not stored as text (type '{cell.Type ?? "number"}').");
            if (!cell.QuotePrefix)
                problems.Add($"{what}: cell {cell.Ref} ('{Short(value)}') isn't marked \"treat as text\" — editing it in Excel would make it a formula.");
        }

        // Every other text cell that starts like a formula must be marked too.
        foreach (var c in cells.Where(c => c.Type is "s" or "inlineStr" && c.Text.Length > 0 && "=+-@".Contains(c.Text[0]) && !c.QuotePrefix))
            problems.Add($"{what}: cell {c.Ref} ('{Short(c.Text)}') starts like a formula and isn't marked \"treat as text\".");
    }

    private static string Short(string s) => s.Length <= 40 ? s : s[..40] + "…";
}
