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
                      .Select(i => r.Cell(i).GetFormattedString().Trim())
                      .ToList();

        var headers = ReadRow(allRows[0]);
        var rows = allRows.Skip(1)
                          .Select(ReadRow)
                          .Where(r => r.Any(c => !string.IsNullOrWhiteSpace(c)))
                          .Cast<IReadOnlyList<string>>()
                          .ToList();

        return Finish(headers, rows.Select(r => r.ToList()).ToList(), warnings);
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
    /// Best-guess field for each column. Exact synonym match first, then
    /// "contains" — so "Lead Owner Email" still finds Owner. A field is
    /// never suggested twice; the leftmost column wins.
    /// </summary>
    public static Dictionary<string, int> SuggestMapping(IReadOnlyList<string> headers)
    {
        var result = new Dictionary<string, int>();
        var normalised = headers.Select(NormaliseHeader).ToList();

        foreach (var (field, synonyms) in ImportFields.Synonyms)
        {
            for (var i = 0; i < normalised.Count; i++)
            {
                if (result.ContainsValue(i)) continue;
                if (!synonyms.Contains(normalised[i])) continue;
                result[field] = i;
                break;
            }
        }

        foreach (var (field, synonyms) in ImportFields.Synonyms)
        {
            if (result.ContainsKey(field)) continue;

            for (var i = 0; i < normalised.Count; i++)
            {
                if (result.ContainsValue(i)) continue;
                if (string.IsNullOrEmpty(normalised[i])) continue;
                if (!synonyms.Any(s => normalised[i].Contains(s))) continue;
                result[field] = i;
                break;
            }
        }

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
    /// when there is no number in there at all.
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

        // Decide which separator is the decimal point: whichever is last.
        var lastDot = cleaned.LastIndexOf('.');
        var lastComma = cleaned.LastIndexOf(',');

        if (lastComma > lastDot)
            cleaned = cleaned.Replace(".", "").Replace(',', '.');   // 1.234,56
        else
            cleaned = cleaned.Replace(",", "");                     // 1,234.56

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
