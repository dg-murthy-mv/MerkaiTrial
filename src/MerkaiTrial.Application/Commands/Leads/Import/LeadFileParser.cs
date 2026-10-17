// =====================================================================
// LeadFileParser.cs
// Location: MerkaiTrial.Application/Commands/Leads/Import/LeadFileParser.cs
//
// NEW FILE. Turns an uploaded .csv / .xlsx / .xls into a grid of strings,
// and normalises the values the importer needs.
//
// WHY NOT THE OLD ParseCsvHelper
//   It split the file on newlines first, so a quoted field containing a
//   line break ("12 Sukhumvit Rd,\nBangkok") silently became two broken
//   rows. It also treated "" inside a quoted field as two toggles rather
//   than an escaped quote, and it assumed fixed column positions, so any
//   sheet whose columns weren't in its exact order imported garbage into
//   the wrong fields.
//
// 092
//   • EXCEL DATE CELLS ARE READ AS 2026-10-16. GetFormattedString renders
//     a date cell in whatever format the cell carries — very often the
//     US "m/d/yyyy" built-in — so 1 March came out as "3/1/2027", which a
//     workspace that writes dates day-first would read as 3 January. A
//     cell that IS a date is now written in ISO form, which nobody can
//     read two ways. Every other cell is unchanged.
//   • SuggestMapping knows custom fields, and tries the most certain
//     match first: a heading that is exactly a field's OWN name (what the
//     Export button writes), then a custom field's exact label, then the
//     synonyms, and only then "contains" — for built-in fields only, so a
//     custom column called "Account manager" is never grabbed by Company
//     because it happens to contain "account".
//   • ParseDecimal no longer reads a grouping comma as a decimal point:
//     "₹1,25,000" was unreadable and "95,000" imported as 95 — in
//     Estimated value as well as in the new custom number columns.
// =====================================================================

using System.Globalization;
using System.Text;
using ClosedXML.Excel;

namespace MerkaiTrial.Application.Commands.Leads.Import;

public static class LeadFileParser
{
    public const int MaxRows = 5000;
    public const long MaxFileBytes = 10 * 1024 * 1024;   // 10 MB

    /// <summary>A parsed file: header row plus data rows, all as strings.</summary>
    public sealed record Grid(
        IReadOnlyList<string> Headers,
        IReadOnlyList<IReadOnlyList<string>> Rows,
        IReadOnlyList<string> Warnings);

    public static Grid Parse(byte[] bytes, string fileName)
    {
        if (bytes.Length == 0)
            throw new InvalidOperationException("That file is empty.");

        if (bytes.LongLength > MaxFileBytes)
            throw new InvalidOperationException("That file is larger than 10 MB. Split it and import in parts.");

        var ext = Path.GetExtension(fileName).ToLowerInvariant();

        return ext switch
        {
            ".csv" or ".txt" => ParseCsv(bytes),
            ".xlsx" or ".xls" or ".xlsm" => ParseExcel(bytes),
            _ => throw new InvalidOperationException(
                     "Only .csv and .xlsx files can be imported. Save your file in one of those formats and try again.")
        };
    }

    // =================================================================
    // CSV
    // =================================================================

    private static Grid ParseCsv(byte[] bytes)
    {
        // UTF8 with BOM detection. Without this, a BOM at the start makes
        // the first header read as "\ufeffName" and never match a synonym.
        var text = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true)
                        .ReadToEnd();

        var delimiter = DetectDelimiter(text);
        var warnings = new List<string>();

        if (delimiter == ';')
            warnings.Add("Detected a semicolon-separated file (common in European Excel).");
        else if (delimiter == '\t')
            warnings.Add("Detected a tab-separated file.");

        var all = ParseDelimited(text, delimiter);

        if (all.Count == 0)
            throw new InvalidOperationException("That file has no rows.");

        var headers = all[0].Select(h => h.Trim()).ToList();
        var rows = all.Skip(1)
                      .Where(r => r.Any(c => !string.IsNullOrWhiteSpace(c)))   // drop blank lines
                      .ToList();

        return Finish(headers, rows, warnings);
    }

    /// <summary>
    /// Picks the delimiter by counting candidates in the header line only.
    /// A file with commas inside address fields would otherwise win on a
    /// whole-file count.
    /// </summary>
    private static char DetectDelimiter(string text)
    {
        var firstLine = text.Split('\n').FirstOrDefault() ?? "";
        var counts = new[] { ',', ';', '\t', '|' }
            .Select(c => (Char: c, Count: firstLine.Count(x => x == c)))
            .OrderByDescending(x => x.Count)
            .ToList();

        return counts[0].Count > 0 ? counts[0].Char : ',';
    }

    /// <summary>
    /// Character-by-character RFC 4180 parse. Handles quoted fields,
    /// embedded delimiters, embedded newlines, and "" as an escaped quote.
    /// </summary>
    private static List<List<string>> ParseDelimited(string text, char delimiter)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    // "" inside a quoted field is one literal quote.
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
                continue;
            }

            if (c == '"')            { inQuotes = true; }
            else if (c == delimiter) { row.Add(field.ToString().Trim()); field.Clear(); }
            else if (c == '\r')      { /* handled by \n */ }
            else if (c == '\n')
            {
                row.Add(field.ToString().Trim());
                field.Clear();
                rows.Add(row);
                row = new List<string>();
            }
            else field.Append(c);
        }

        // Last field / row when the file doesn't end with a newline.
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString().Trim());
            rows.Add(row);
        }

        return rows;
    }

    // =================================================================
    // EXCEL
    // =================================================================

    private static Grid ParseExcel(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var workbook = new XLWorkbook(stream);

        var warnings = new List<string>();

        var sheet = workbook.Worksheets.FirstOrDefault()
            ?? throw new InvalidOperationException("That workbook has no sheets.");

        if (workbook.Worksheets.Count > 1)
            warnings.Add($"The workbook has {workbook.Worksheets.Count} sheets — only \"{sheet.Name}\" was read.");

        var used = sheet.RangeUsed();
        if (used is null)
            throw new InvalidOperationException("That sheet is empty.");

        var allRows = used.RowsUsed().ToList();
        if (allRows.Count == 0)
            throw new InvalidOperationException("That sheet is empty.");

        var lastCol = used.ColumnCount();

        // GetFormattedString keeps what the user SEES — so a cell formatted
        // as text, a date, or currency reads the way it looks in Excel
        // rather than as an OLE serial number.
        List<string> ReadRow(IXLRangeRow r) =>
            Enumerable.Range(1, lastCol)
                      .Select(i => ReadCell(r.Cell(i)))
                      .ToList();

        var headers = ReadRow(allRows[0]);
        var rows = allRows.Skip(1)
                          .Select(ReadRow)
                          .Where(r => r.Any(c => !string.IsNullOrWhiteSpace(c)))
                          .Cast<IReadOnlyList<string>>()
                          .ToList();

        return Finish(headers, rows.Select(r => r.ToList()).ToList(), warnings);
    }

    /// <summary>
    /// 092. What one cell says. A real date cell is written as ISO
    /// (2026-10-16, or 2026-10-16 14:30 when it carries a time) — see the
    /// header. Anything else reads the way it looks in Excel.
    /// </summary>
    private static string ReadCell(IXLCell cell)
    {
        try
        {
            if (cell.DataType == XLDataType.DateTime)
            {
                var d = cell.GetDateTime();
                return d.TimeOfDay == TimeSpan.Zero
                    ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            }
        }
        catch (Exception)
        {
            // A cell flagged as a date that will not read as one: fall back
            // to what the person sees, exactly as before 092.
        }

        return cell.GetFormattedString().Trim();
    }

    // =================================================================
    // SHARED
    // =================================================================

    private static Grid Finish(
        List<string> headers,
        List<List<string>> rows,
        List<string> warnings)
    {
        if (headers.Count == 0 || headers.All(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException(
                "The first row must contain column headings (Name, Email, Phone...).");

        // Name the blanks so the mapping dropdown isn't full of empty entries.
        for (var i = 0; i < headers.Count; i++)
            if (string.IsNullOrWhiteSpace(headers[i]))
                headers[i] = $"Column {i + 1}";

        if (rows.Count == 0)
            throw new InvalidOperationException("That file has headings but no data rows.");

        if (rows.Count > MaxRows)
        {
            warnings.Add($"The file has {rows.Count:N0} rows — only the first {MaxRows:N0} will be imported.");
            rows = rows.Take(MaxRows).ToList();
        }

        // Pad short rows so column indexes are always safe to read.
        var width = headers.Count;
        var padded = rows.Select(r =>
        {
            if (r.Count == width) return (IReadOnlyList<string>)r;
            var copy = new List<string>(r);
            while (copy.Count < width) copy.Add(string.Empty);
            if (copy.Count > width) copy = copy.Take(width).ToList();
            return copy;
        }).ToList();

        return new Grid(headers, padded, warnings);
    }

    // =================================================================
    // AUTO-MAPPING
    // =================================================================

    /// <summary>Lowercase, strip spaces, underscores, dashes and punctuation.</summary>
    public static string NormaliseHeader(string header)
    {
        var sb = new StringBuilder();
        foreach (var c in header.ToLowerInvariant())
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>
    /// Best-guess field for each column. A field is never suggested twice
    /// and a column never feeds two fields; the leftmost column wins.
    ///
    /// 092 — four passes, most certain first:
    ///   1. a heading that is exactly a built-in field's own name
    ///      ("Lead source", "Estimated value") — what Export writes;
    ///   2. a heading that is exactly a custom field's label;
    ///   3. a heading that is one of a built-in field's synonyms;
    ///   4. a heading that CONTAINS a synonym — built-in fields only, so
    ///      "Lead Owner Email" still finds Owner, but a custom column is
    ///      never pulled into a built-in field by a coincidence of spelling.
    /// </summary>
    /// <param name="customFields">
    /// (mapping key, label) for each active custom field. Optional, so the
    /// old one-argument call still compiles and behaves as before.
    /// </param>
    public static Dictionary<string, int> SuggestMapping(
        IReadOnlyList<string> headers,
        IReadOnlyList<(string Key, string Label)>? customFields = null)
    {
        var result = new Dictionary<string, int>();
        var normalised = headers.Select(NormaliseHeader).ToList();

        void Take(string key, Func<string, bool> matches)
        {
            if (result.ContainsKey(key)) return;
            for (var i = 0; i < normalised.Count; i++)
            {
                if (result.ContainsValue(i)) continue;
                if (string.IsNullOrEmpty(normalised[i])) continue;
                if (!matches(normalised[i])) continue;
                result[key] = i;
                return;
            }
        }

        // 1 — a field's own name.
        foreach (var field in ImportFields.All)
        {
            var own = NormaliseHeader(field.Label);
            Take(field.Key, h => h == own);
        }

        // 2 — a custom field's exact label.
        foreach (var (key, label) in customFields ?? Array.Empty<(string Key, string Label)>())
        {
            var own = NormaliseHeader(label);
            if (own.Length == 0) continue;
            Take(key, h => h == own);
        }

        // 3 — a synonym, exactly.
        foreach (var (field, synonyms) in ImportFields.Synonyms)
            Take(field, h => synonyms.Contains(h));

        // 4 — contains a synonym. Built-in fields only.
        foreach (var (field, synonyms) in ImportFields.Synonyms)
            Take(field, h => synonyms.Any(syn => h.Contains(syn)));

        return result;
    }

    // =================================================================
    // VALUE NORMALISATION
    // =================================================================

    /// <summary>
    /// Digits only, keeping a leading +. Used for duplicate matching, so
    /// "081-234-5678", "081 234 5678" and "0812345678" compare equal.
    /// The original text is what gets stored.
    /// </summary>
    private static readonly Dictionary<string, string> DialCodes =
       new(StringComparer.OrdinalIgnoreCase)
       {
           ["IN"] = "91",
           ["TH"] = "66",
           ["PH"] = "63",
           ["AE"] = "971",
           ["SG"] = "65",
           ["MY"] = "60",
           ["ID"] = "62",
           ["VN"] = "84",
           ["US"] = "1",
           ["GB"] = "44",
       };
    public static string? NormalisePhone(string? phone, string? countryCode = null)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;

        var trimmed = phone.Trim();
        var hasPlus = trimmed.StartsWith('+');

        var sb = new StringBuilder();
        foreach (var c in trimmed)
            if (char.IsDigit(c)) sb.Append(c);

        var digits = sb.ToString();
        if (digits.Length < 6) return null;   // too short to be a number

        // 00 is the international prefix in most of the world.
        if (!hasPlus && digits.StartsWith("00") && digits.Length > 8)
        {
            digits = digits[2..];
            hasPlus = true;
        }

        if (countryCode is null || !DialCodes.TryGetValue(countryCode, out var dial))
            return hasPlus ? "+" + digits : digits;

        // Already international.
        if (hasPlus) return "+" + digits;

        // National trunk form: 089... -> +6689...
        if (digits.StartsWith('0'))
            return "+" + dial + digits.TrimStart('0');

        // Bare local number that already carries the dial code.
        if (digits.StartsWith(dial) && digits.Length > dial.Length + 6)
            return "+" + digits;

        // Bare local number without the trunk zero (common in India).
        return "+" + dial + digits;
    }


    public static bool LooksLikeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;
        var at = email.IndexOf('@');
        if (at <= 0 || at == email.Length - 1) return false;
        var domain = email[(at + 1)..];
        return domain.Contains('.') && !domain.EndsWith('.') && !email.Contains(' ');
    }

    /// <summary>
    /// Money as typed by humans: "฿95,000.00", "95 000", "1,234.56",
    /// "(500)" for negative, "₹1,40,000" with Indian grouping. Returns null
    /// when there is no number in there at all. Also reads custom NUMBER
    /// columns (092).
    /// </summary>
    public static decimal? ParseDecimal(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var text = raw.Trim();
        var negative = text.StartsWith('(') && text.EndsWith(')');

        var sb = new StringBuilder();
        foreach (var c in text)
            if (char.IsDigit(c) || c == '.' || c == ',' || c == '-') sb.Append(c);

        var cleaned = sb.ToString();
        if (cleaned.Length == 0) return null;

        // Decide which separator is the decimal point.
        //
        // 092 — FIXED: "only commas" used to mean "the comma is the decimal
        // point", so "₹1,25,000" became 1.25.000 (unreadable) and "95,000"
        // became 95. In India, Thailand, the Philippines and the UAE a comma
        // groups thousands. Now:
        //   both kinds         the LAST one is the decimal point
        //                      (1,234.56 and 1.234,56)
        //   commas only        grouping — unless there is exactly one and
        //                      it has one or two digits after it (12,5)
        //   dots only          a decimal point — unless there are several
        //                      (1.250.000 is grouping)
        var lastDot = cleaned.LastIndexOf('.');
        var lastComma = cleaned.LastIndexOf(',');
        var commas = cleaned.Count(c => c == ',');
        var dots = cleaned.Count(c => c == '.');

        if (commas > 0 && dots > 0)
        {
            cleaned = lastComma > lastDot
                ? cleaned.Replace(".", "").Replace(',', '.')        // 1.234,56
                : cleaned.Replace(",", "");                         // 1,234.56
        }
        else if (commas > 0)
        {
            var after = cleaned.Length - lastComma - 1;
            cleaned = commas == 1 && after is 1 or 2
                ? cleaned.Replace(',', '.')                         // 12,5
                : cleaned.Replace(",", "");                         // 1,25,000 · 95,000
        }
        else if (dots > 1)
        {
            cleaned = cleaned.Replace(".", "");                     // 1.250.000
        }

        if (!decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
            return null;

        if (negative && value > 0) value = -value;
        return value;
    }

    public static int? ParseInt(string? raw)
    {
        var d = ParseDecimal(raw);
        if (d is null) return null;
        return (int)Math.Round(d.Value);
    }
}
