// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Infrastructure/Xlsx.cs
//
// NEW (092). Reads the first sheet of an .xlsx the app produced — the
// Leads export — into rows of text, so a test can check what is in the
// file. No package: an .xlsx is a zip of XML, and the tests only need
// cell text, so this reads the two parts that hold it.
//
//   • shared strings and inline strings come back as their text;
//   • numbers come back as written in the file ("1250000", "46082");
//     a date is a number of days, so DateFromSerial turns one back.
//
// And the other direction, ToCsv: rows back into a CSV file the importer
// reads, with the quoting a spreadsheet uses.
// =====================================================================

using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace MerkaiTrial.E2E.Infrastructure;

public static class Xlsx
{
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    /// <summary>Every row of the first worksheet; short rows are padded to the widest.</summary>
    public static List<List<string>> ReadFirstSheet(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);

        var shared = new List<string>();
        var sst = zip.GetEntry("xl/sharedStrings.xml");
        if (sst is not null)
        {
            using var s = sst.Open();
            var doc = XDocument.Load(s);
            foreach (var si in doc.Root!.Elements(Ns + "si"))
                shared.Add(string.Concat(si.Descendants(Ns + "t").Select(t => t.Value)));
        }

        var sheetEntry = zip.GetEntry("xl/worksheets/sheet1.xml")
            ?? zip.Entries.FirstOrDefault(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase))
            ?? throw new AssertionException("The downloaded file is not a spreadsheet: it has no worksheet.");

        var rows = new List<List<string>>();
        using (var s = sheetEntry.Open())
        {
            var doc = XDocument.Load(s);
            foreach (var row in doc.Descendants(Ns + "row"))
            {
                var cells = new List<string>();
                foreach (var c in row.Elements(Ns + "c"))
                {
                    var col = ColumnIndex((string?)c.Attribute("r") ?? "");
                    while (cells.Count < col) cells.Add("");

                    var type = (string?)c.Attribute("t");
                    string text = type switch
                    {
                        "s"         => int.TryParse(c.Element(Ns + "v")?.Value, out var i) && i < shared.Count ? shared[i] : "",
                        "inlineStr" => string.Concat(c.Descendants(Ns + "t").Select(t => t.Value)),
                        _           => c.Element(Ns + "v")?.Value ?? ""
                    };

                    if (cells.Count == col) cells.Add(text);
                    else cells[col] = text;
                }
                rows.Add(cells);
            }
        }

        var width = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        foreach (var r in rows) while (r.Count < width) r.Add("");
        return rows;
    }

    /// <summary>"A1" → 0, "AB7" → 27. No letters → the next column.</summary>
    private static int ColumnIndex(string reference)
    {
        var n = 0;
        foreach (var ch in reference)
        {
            if (!char.IsLetter(ch)) break;
            n = n * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        }
        return Math.Max(0, n - 1);
    }

    /// <summary>A spreadsheet day number ("46082") as a date; null when it is not one.</summary>
    public static DateOnly? DateFromSerial(string text)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0 && d < 2958466
            ? DateOnly.FromDateTime(DateTime.FromOADate(d))
            : null;

    /// <summary>Rows as CSV text: comma-separated, quoted where needed, CRLF line ends.</summary>
    public static string ToCsv(IEnumerable<IReadOnlyList<string>> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            sb.Append(string.Join(",", row.Select(Quote)));
            sb.Append("\r\n");
        }
        return sb.ToString();

        static string Quote(string v) =>
            v.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }
}
