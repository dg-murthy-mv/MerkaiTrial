// =====================================================================
// ImportCustomFields.cs
// Location: MerkaiTrial.Application/Commands/Leads/Import/ImportCustomFields.cs
//
// NEW FILE (092). Custom fields in a lead import: which fields can be
// mapped, and how a spreadsheet cell becomes a stored value.
//
// ─────────────────────────────────────────────────────────────────────
// A CELL IS NOT A FORM INPUT
//
//   The Create and Edit forms post values in one strict shape per type
//   (CustomFieldDtos explains it): "1250.5", "2027-03-01", an option KEY,
//   "true". A spreadsheet holds what people type: "₹1,25,000", "01/03/2027",
//   "Gold", "Yes". ImportCellReader turns the second into the first, and
//   then hands the result to CustomFieldValueRules.Parse — THE one parser
//   every form and the API already use — so an imported value obeys
//   exactly the rules a typed-in one does (length, decimal places, the
//   date range, retired choices). Nothing here decides what is valid; it
//   only translates.
//
//   number    "₹1,25,000", "1,250.50", "(500)" — LeadFileParser.ParseDecimal,
//             the same reading Estimated value has always had
//   date      the WORKSPACE's date format first (dd/MM/yyyy in India and
//             Thailand, MM/dd/yyyy in the Philippines), then ISO
//             2027-03-01, then forms with the month spelt out, which
//             cannot be read two ways (1 Mar 2027, March 1, 2027). A
//             date cell in an .xlsx already arrives as ISO (LeadFileParser).
//   dropdown  the CHOICE NAME, any case ("gold" finds Gold). The stored
//             key also works, for a file written by another tool.
//   checkbox  yes / no, true / false, 1 / 0, y / n, x, ✓. Blank and "no"
//             both mean unticked — which is stored as no value at all.
//   text      as it is.
//
// WHAT A PROBLEM DOES TO THE ROW (decided in ImportValidator.Resolve):
//   • A value that cannot be read is dropped, and the row says so — the
//     lead still imports, exactly as a bad email address always has.
//   • A REQUIRED field whose column is matched but empty or unreadable on
//     a row that would CREATE a lead: that row will not import, because
//     the lead form itself would refuse it.
//   • A required field with NO column matched: the leads import without
//     it and the Review step says so once. Imports that worked before
//     custom fields existed keep working.
// ─────────────────────────────────────────────────────────────────────
// =====================================================================

using System.Globalization;
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Application.Commands.Leads.Import;

/// <summary>One active Lead custom field, as the importer needs it.</summary>
internal sealed class ImportCustomField
{
    public required Guid Id { get; init; }
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required string FieldType { get; init; }
    public required int DecimalPlaces { get; init; }
    public required bool IsRequired { get; init; }
    public required IReadOnlyList<CustomFieldOptionDto> Options { get; init; }

    public static ImportCustomField From(CustomFieldDefinition d) => new()
    {
        Id            = d.Id,
        Key           = ImportFieldKeys.ForCustomField(d.Id),
        Label         = d.Label,
        FieldType     = d.FieldType,
        DecimalPlaces = d.DecimalPlaces,
        IsRequired    = d.IsRequired && CustomFieldTypes.SupportsRequired(d.FieldType),
        Options       = d.FieldType == CustomFieldTypes.Dropdown
                            ? CustomFieldOptionsJson.Parse(d.OptionsJson)
                            : new List<CustomFieldOptionDto>()
    };
}

internal static class ImportCustomFields
{
    /// <summary>The ACTIVE Lead custom fields, in display order. One query.</summary>
    public static async Task<List<ImportCustomField>> LoadAsync(
        FlowDbContext db, Guid tenantId, CancellationToken ct)
    {
        var defs = await db.CustomFieldDefinitions
            .AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.EntityType == CustomFieldEntityTypes.Lead &&
                        !d.IsDeleted && d.IsActive)
            .OrderBy(d => d.SortOrder)
            .ThenBy(d => d.Label)
            .ToListAsync(ct);

        return defs.Select(ImportCustomField.From).ToList();
    }

    /// <summary>
    /// The mapping-screen entries for the custom fields: the label, and a
    /// hint that says what a cell should look like — in the workspace's
    /// own date format, with the real choices of a dropdown.
    /// </summary>
    public static List<ImportField> ToImportFields(IEnumerable<ImportCustomField> fields, string? dateFormat)
        => fields.Select(f => new ImportField(
                Key: f.Key,
                Label: f.Label,
                IsRequired: false,          // the COLUMN is optional; see the header
                Hint: HintFor(f, dateFormat),
                IsCustom: true))
            .ToList();

    private static string HintFor(ImportCustomField f, string? dateFormat)
    {
        var hint = f.FieldType switch
        {
            CustomFieldTypes.Number => f.DecimalPlaces == 0
                ? "A whole number"
                : $"A number, up to {f.DecimalPlaces} decimal place{(f.DecimalPlaces == 1 ? "" : "s")}",
            CustomFieldTypes.Date => $"A date — {ImportCellReader.Example(dateFormat)} or 2026-10-16",
            CustomFieldTypes.Dropdown => ChoicesText(f.Options),
            CustomFieldTypes.Checkbox => "Yes or No",
            _ => "Text"
        };

        return f.IsRequired ? hint + " · required on new leads" : hint;
    }

    private static string ChoicesText(IReadOnlyList<CustomFieldOptionDto> options)
    {
        var active = options.Where(o => o.IsActive).Select(o => o.Label).ToList();
        if (active.Count == 0) return "One of your choices";
        var shown = string.Join(", ", active.Take(6));
        return "One of: " + shown + (active.Count > 6 ? ", …" : "");
    }

    /// <summary>
    /// Stage one value row on a lead. ADDS A ROW; DOES NOT SAVE. The wire
    /// value has already been through CustomFieldValueRules in Resolve, so
    /// a parse failure here would mean the rules changed between preview
    /// and commit — the value is then skipped rather than half-written.
    /// </summary>
    public static bool Stage(
        FlowDbContext db, Guid tenantId, Guid leadId, ImportCustomField field,
        string wire, string? actor, DateTime now)
    {
        var parsed = CustomFieldValueRules.Parse(field.FieldType, field.Label, field.DecimalPlaces, field.Options, wire);
        if (parsed.Error is not null || parsed.IsEmpty) return false;

        db.CustomFieldValues.Add(new CustomFieldValue
        {
            Id           = Guid.NewGuid(),
            TenantId     = tenantId,
            DefinitionId = field.Id,
            EntityId     = leadId,
            TextValue    = parsed.Text,
            NumberValue  = parsed.Number,
            DateValue    = parsed.Date,
            BoolValue    = parsed.Bool,
            CreatedAtUtc = now,
            UpdatedBy    = actor is { Length: > 64 } ? actor[..64] : actor
        });
        return true;
    }
}

/// <summary>What one cell said, as a stored value — or why it could not be read.</summary>
internal readonly record struct ImportCellResult(string? Wire, string? Problem)
{
    /// <summary>Nothing in the cell, or a "no" for a checkbox.</summary>
    public bool IsEmpty => Wire is null && Problem is null;

    public static ImportCellResult Empty => new(null, null);
    public static ImportCellResult Ok(string wire) => new(wire, null);
    public static ImportCellResult Fail(string problem) => new(null, problem);
}

internal static class ImportCellReader
{
    private static readonly HashSet<string> Yes = new(StringComparer.OrdinalIgnoreCase)
        { "yes", "y", "true", "1", "x", "✓", "✔", "ticked", "checked", "on" };

    private static readonly HashSet<string> No = new(StringComparer.OrdinalIgnoreCase)
        { "no", "n", "false", "0", "-", "off", "unticked", "unchecked" };

    /// <summary>
    /// Read one cell for one field. <paramref name="raw"/> is already
    /// trimmed and non-blank — blank cells never reach here.
    /// </summary>
    public static ImportCellResult Read(ImportCustomField f, string raw, string? dateFormat)
    {
        string? wire;

        switch (f.FieldType)
        {
            case CustomFieldTypes.Checkbox:
                if (Yes.Contains(raw)) return ImportCellResult.Ok("true");
                if (No.Contains(raw))  return ImportCellResult.Empty;
                return ImportCellResult.Fail($"{f.Label}: \"{raw}\" isn't Yes or No");

            case CustomFieldTypes.Number:
            {
                var n = LeadFileParser.ParseDecimal(raw);
                if (n is null) return ImportCellResult.Fail($"{f.Label}: \"{raw}\" isn't a number");
                wire = n.Value.ToString(CultureInfo.InvariantCulture);
                break;
            }

            case CustomFieldTypes.Date:
            {
                var d = ReadDate(raw, dateFormat);
                if (d is null)
                    return ImportCellResult.Fail(
                        $"{f.Label}: \"{raw}\" isn't a date — use {Example(dateFormat)} or 2026-10-16");
                wire = d.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                break;
            }

            case CustomFieldTypes.Dropdown:
            {
                var option = f.Options.FirstOrDefault(o =>
                                 string.Equals(o.Label?.Trim(), raw, StringComparison.OrdinalIgnoreCase))
                             ?? f.Options.FirstOrDefault(o => string.Equals(o.Key, raw, StringComparison.Ordinal));

                if (option is null)
                {
                    var choices = f.Options.Where(o => o.IsActive).Select(o => o.Label).Take(6).ToList();
                    return ImportCellResult.Fail(
                        $"{f.Label}: \"{raw}\" isn't one of the choices" +
                        (choices.Count > 0 ? $" ({string.Join(", ", choices)})" : ""));
                }

                if (!option.IsActive)
                    return ImportCellResult.Fail($"{f.Label}: \"{option.Label}\" has been retired");

                wire = option.Key;
                break;
            }

            default:
                wire = raw;
                break;
        }

        // The one set of rules: length, decimal places, range, retired choice.
        var parsed = CustomFieldValueRules.Parse(f.FieldType, f.Label, f.DecimalPlaces, f.Options, wire);
        if (parsed.Error is not null) return ImportCellResult.Fail(parsed.Error.TrimEnd('.'));
        if (parsed.IsEmpty) return ImportCellResult.Empty;

        return ImportCellResult.Ok(
            CustomFieldValueRules.ToWire(f.FieldType, parsed.Text, parsed.Number, parsed.Date, parsed.Bool) ?? wire);
    }

    /// <summary>
    /// A date as THIS workspace writes it, then ISO, then the spelt-out
    /// month forms. Null when none fits. Invariant culture throughout —
    /// th-TH's own calendar would read 2026 as a Buddhist year.
    /// </summary>
    public static DateOnly? ReadDate(string raw, string? dateFormat)
    {
        var text = raw.Trim();

        // "2026-10-16 14:30" (a dated cell with a time) — the date part only.
        if (text.Length > 10 && text[4] == '-' && text[7] == '-' && text[10] == ' ')
            text = text[..10];

        foreach (var format in FormatsFor(dateFormat))
        {
            try
            {
                if (DateTime.TryParseExact(text, format, CultureInfo.InvariantCulture,
                        DateTimeStyles.AllowWhiteSpaces, out var dt))
                    return DateOnly.FromDateTime(dt);
            }
            catch (FormatException)
            {
                // A workspace format .NET cannot use — try the next one.
            }
        }

        return null;
    }

    /// <summary>Today in the workspace's format, for hints and messages.</summary>
    public static string Example(string? dateFormat)
    {
        var sample = new DateTime(2026, 10, 16);
        try
        {
            return sample.ToString(string.IsNullOrWhiteSpace(dateFormat) ? "dd/MM/yyyy" : DateOnlyPart(dateFormat),
                                   CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return sample.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        }
    }

    private static IEnumerable<string> FormatsFor(string? dateFormat)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        IEnumerable<string> All()
        {
            if (!string.IsNullOrWhiteSpace(dateFormat))
            {
                var tenant = DateOnlyPart(dateFormat);
                yield return tenant;

                // dd/MM/yyyy also accepts 1/3/2027: "d" and "M" read one or
                // two digits, so the loose form covers both.
                var loose = tenant.Replace("dd", "d").Replace("MM", "M");
                if (!loose.Contains("MMM")) yield return loose;

                // The same order with another separator: 16-10-2026, 16.10.2026.
                foreach (var sep in new[] { "/", "-", "." })
                {
                    var swapped = loose.Replace("/", sep).Replace("-", sep).Replace(".", sep);
                    yield return swapped;
                }
            }

            yield return "yyyy-MM-dd";
            yield return "yyyy-M-d";
            yield return "yyyy/M/d";
            yield return "d MMM yyyy";
            yield return "d-MMM-yyyy";
            yield return "d MMMM yyyy";
            yield return "MMM d, yyyy";
            yield return "MMMM d, yyyy";
            yield return "d MMM yy";
        }

        foreach (var f in All())
            if (f.Length > 0 && seen.Add(f)) yield return f;
    }

    /// <summary>"dd/MM/yyyy HH:mm" → "dd/MM/yyyy". A workspace date format with a time in it is unusual but possible.</summary>
    private static string DateOnlyPart(string format)
    {
        var f = format.Trim();
        var cut = f.IndexOfAny(new[] { 'H', 'h', 't' });
        return (cut > 0 ? f[..cut] : f).Trim();
    }
}
