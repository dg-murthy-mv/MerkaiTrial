// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Infrastructure/XlsxCells.cs
//
// NEW (105 — VUL-15). Every cell of an .xlsx's first sheet with what a
// formula check needs, which Xlsx.ReadFirstSheet (text only) leaves out:
//
//   • HasFormula   the cell holds a formula (<f>) — Excel would RUN it;
//   • Type         how the value is stored: "s" (shared text), "inlineStr",
//                  "str" (the text RESULT of a formula), or null (number);
//   • QuotePrefix  the cell's style says "treat as text even if it starts
//                  with =" — the apostrophe in front, in Excel's terms.
// =====================================================================

using System.IO.Compression;
using System.Xml.Linq;

namespace MerkaiTrial.E2E.Infrastructure;

public sealed record XlsxCell(string Ref, string? Type, bool HasFormula, string Text, bool QuotePrefix);

public static class XlsxCells
{
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    public static List<XlsxCell> Read(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);

        var shared = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") is { } sst)
        {
            using var s = sst.Open();
            foreach (var si in XDocument.Load(s).Root!.Elements(Ns + "si"))
                shared.Add(string.Concat(si.Descendants(Ns + "t").Select(t => t.Value)));
        }

        // Which cell styles (cellXfs, by position) carry quotePrefix.
        var quoted = new HashSet<int>();
        if (zip.GetEntry("xl/styles.xml") is { } styles)
        {
            using var s = styles.Open();
            var xfs = XDocument.Load(s).Root!.Element(Ns + "cellXfs")?.Elements(Ns + "xf").ToList() ?? new();
            for (var i = 0; i < xfs.Count; i++)
            {
                var q = (string?)xfs[i].Attribute("quotePrefix");
                if (q is "1" or "true") quoted.Add(i);
            }
        }

        var sheet = zip.GetEntry("xl/worksheets/sheet1.xml")
            ?? zip.Entries.FirstOrDefault(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase))
            ?? throw new AssertionException("The file is not a spreadsheet: it has no worksheet.");

        var cells = new List<XlsxCell>();
        using (var s = sheet.Open())
        {
            foreach (var c in XDocument.Load(s).Descendants(Ns + "c"))
            {
                var type = (string?)c.Attribute("t");
                var value = (string?)c.Element(Ns + "v") ?? "";
                var text = type switch
                {
                    "s" => int.TryParse(value, out var i) && i >= 0 && i < shared.Count ? shared[i] : "",
                    "inlineStr" => string.Concat(c.Descendants(Ns + "t").Select(t => t.Value)),
                    _ => value,
                };
                var style = int.TryParse((string?)c.Attribute("s"), out var si) ? si : 0;
                cells.Add(new XlsxCell((string?)c.Attribute("r") ?? "", type, c.Element(Ns + "f") is not null, text, quoted.Contains(style)));
            }
        }
        return cells;
    }

    /// <summary>True when the workbook holds a formula anywhere — in any sheet.</summary>
    public static bool HasAnyFormula(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase)
                                                    && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
        {
            using var s = entry.Open();
            if (XDocument.Load(s).Descendants(Ns + "f").Any()) return true;
        }
        return false;
    }
}
