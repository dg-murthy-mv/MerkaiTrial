// =====================================================================
// FILE: MerkaiTrial.Application/Configuration/CustomFieldsConfiguration.cs
//
// NEW FILE (075 — custom fields, Round A).
//
// Everything about custom fields that BOTH sides need to agree on:
//
//   CustomFieldEntityTypes   which kinds of record can carry fields, and
//                            which permission module guards each one
//   CustomFieldTypes         the five field types, their names and icons
//   CustomFieldLimits        the caps (50 fields, 1000 characters, ...)
//   CustomFieldOptionsJson   reading and writing a dropdown's options
//   CustomFieldValueRules    THE one parser for a submitted value
//   CustomFieldFilterOps     (076) the list filters, and which apply to which type
//   CustomFieldFilterCodec   (076) a filter on the wire: "<id>~<op>~<value>"
//
// 077 — Deal is an enabled entity type. Guarded by deals.* for records,
//   settings.* for the field list, exactly like Contact.
//
// 078 — Company is an enabled entity type, guarded by companies.* for
//   records. Lead joins the Settings tabs as "Coming soon" (Leads are the
//   next round, with the lead → contact/deal field mapping); it is NOT in
//   Enabled, so the API refuses fields for it until then.
//
// 079 — Lead is an enabled entity type, guarded by leads.* for records.
//   Lead fields can also name a Contact field and a Deal field that
//   receive their value on conversion (CustomFieldDefinition.MapTo…).
//
// The value rules live here, in the Application project, because both
// the WebApi (which must refuse a bad value) and the Admin.Web page
// (which shows the error beside the right input before posting) call
// them. Two copies of "what is a valid number" would drift; round 029
// paid for that lesson on the quote line editor.
// =====================================================================

using System.Globalization;
using System.Text.Json;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Application.Configuration
{
    // =================================================================
    // ENTITY TYPES
    // =================================================================

    public static class CustomFieldEntityTypes
    {
        public const string Contact = "Contact";
        public const string Deal    = "Deal";       // 077
        public const string Company = "Company";    // 078
        public const string Lead    = "Lead";       // 079

        /// <summary>
        /// The entity types that can carry custom fields TODAY: Contacts
        /// (075), Deals (077), Companies (078) and Leads (079).
        ///
        /// Anything not in this list is refused by the API, so a hand-made
        /// request for EntityType "Invoice" cannot create fields that no
        /// page will ever render.
        /// </summary>
        public static readonly IReadOnlyList<string> Enabled = new[] { Contact, Deal, Company, Lead };

        /// <summary>Every type the Settings page lists, enabled or not, in order.</summary>
        public static readonly IReadOnlyList<string> Planned = new[] { Contact, Company, Deal, Lead };

        /// <summary>The canonical spelling of an enabled type, or null.</summary>
        public static string? Normalise(string? value)
            => Enabled.FirstOrDefault(e => string.Equals(e, value?.Trim(), StringComparison.OrdinalIgnoreCase));

        public static bool IsEnabled(string? value) => Normalise(value) is not null;

        /// <summary>
        /// The permission module that guards RECORDS of this type. Reading
        /// a contact's custom fields needs contacts.read; writing them needs
        /// contacts.update. Defining the fields is settings.*, separately.
        /// </summary>
        public static string ModuleFor(string entityType) => entityType switch
        {
            Contact => Modules.Contacts,
            Deal    => Modules.Deals,
            Company => Modules.Companies,
            Lead    => Modules.Leads,
            _ => throw new ArgumentOutOfRangeException(nameof(entityType), entityType, "Unknown entity type")
        };

        public static string PluralLabel(string entityType) => entityType switch
        {
            Contact => "Contacts",
            Deal    => "Deals",
            Company => "Companies",
            Lead    => "Leads",
            _ => entityType
        };

        public static string SingularNoun(string entityType) => entityType switch
        {
            Contact => "contact",
            Deal    => "deal",
            Company => "company",
            Lead    => "lead",
            _ => "record"
        };

        public static string Icon(string entityType) => entityType switch
        {
            Contact => "bi-people",
            Deal    => "bi-kanban",
            Company => "bi-building",
            Lead    => "bi-funnel",
            _ => "bi-collection"
        };
    }

    // =================================================================
    // FIELD TYPES
    // =================================================================

    public static class CustomFieldTypes
    {
        public const string Text     = "text";
        public const string Number   = "number";
        public const string Date     = "date";
        public const string Dropdown = "dropdown";
        public const string Checkbox = "checkbox";

        public static readonly IReadOnlyList<string> All = new[] { Text, Number, Date, Dropdown, Checkbox };

        public static string? Normalise(string? value)
            => All.FirstOrDefault(t => string.Equals(t, value?.Trim(), StringComparison.OrdinalIgnoreCase));

        public static string DisplayName(string type) => type switch
        {
            Text     => "Text",
            Number   => "Number",
            Date     => "Date",
            Dropdown => "Dropdown",
            Checkbox => "Checkbox",
            _        => type
        };

        /// <summary>One line, for the type picker on the Settings page.</summary>
        public static string Description(string type) => type switch
        {
            Text     => "A short piece of text — a reference, a name, a note.",
            Number   => "A number, with up to four decimal places.",
            Date     => "A calendar date, such as a renewal or birthday.",
            Dropdown => "One choice from a list you define.",
            Checkbox => "Yes or no.",
            _        => string.Empty
        };

        public static string Icon(string type) => type switch
        {
            Text     => "bi-fonts",
            Number   => "bi-123",
            Date     => "bi-calendar-event",
            Dropdown => "bi-menu-button-wide",
            Checkbox => "bi-check2-square",
            _        => "bi-question-square"
        };

        /// <summary>
        /// "Required" means nothing for a checkbox: an unticked box IS an
        /// answer ("no"), so a required checkbox could only ever mean
        /// "must be ticked", which is a consent mechanism, not a CRM field.
        /// The Settings page hides the switch and the server ignores it.
        /// </summary>
        public static bool SupportsRequired(string type) => type != Checkbox;
    }

    // =================================================================
    // LIMITS
    // =================================================================

    public static class CustomFieldLimits
    {
        /// <summary>
        /// Per entity type, per workspace, counting switched-off fields.
        /// A contact form with fifty extra inputs is already past useful;
        /// the cap is there so a runaway import cannot make it five hundred.
        /// </summary>
        public const int MaxFieldsPerEntity = 50;

        public const int LabelMaxLength      = 80;
        public const int HelpTextMaxLength   = 200;
        public const int TextValueMaxLength  = 1000;
        public const int MaxOptions          = 100;
        public const int OptionLabelMaxLength = 80;
        public const int MaxDecimalPlaces    = 4;

        /// <summary>
        /// 076. Active fields shown as columns on a list page, per entity
        /// type. Four extra columns still fit a laptop beside name, company,
        /// email and phone; a fifth starts wrapping every row.
        /// </summary>
        public const int MaxListColumns = 4;

        /// <summary>076. Custom field conditions one list request will apply.</summary>
        public const int MaxFiltersPerQuery = 20;

        /// <summary>DECIMAL(18,4) holds 14 digits before the point.</summary>
        public const decimal MaxAbsNumber = 99_999_999_999_999.9999m;

        public static readonly DateOnly MinDate = new(1900, 1, 1);
        public static readonly DateOnly MaxDate = new(2199, 12, 31);
    }

    // =================================================================
    // LIST FILTERS (076)
    // =================================================================

    public static class CustomFieldFilterOps
    {
        public const string Contains = "contains";   // text
        public const string Exactly  = "eq";         // dropdown (option key) — not "Equals", which would hide object.Equals
        public const string AtLeast  = "gte";        // number, date
        public const string AtMost   = "lte";        // number, date
        public const string Is       = "is";         // checkbox: "true" | "false"

        /// <summary>
        /// Whether an operator makes sense for a field type. Anything else
        /// is dropped by the API rather than guessed at — a "gte" on a text
        /// field would otherwise compare strings and look like it worked.
        /// </summary>
        public static bool Applies(string fieldType, string op) => fieldType switch
        {
            CustomFieldTypes.Text     => op == Contains,
            CustomFieldTypes.Dropdown => op == Exactly,
            CustomFieldTypes.Number   => op is AtLeast or AtMost,
            CustomFieldTypes.Date     => op is AtLeast or AtMost,
            CustomFieldTypes.Checkbox => op == Is,
            _                         => false
        };
    }

    /// <summary>
    /// A CustomFieldFilter as ONE query-string value:
    ///     cf=3fa85f6457174562b3fc2c963f66afa6~gte~2027-03-01
    ///
    /// Repeated ?cf= values rather than a JSON blob, so the API's own log
    /// line shows a readable request and [FromQuery] string[] binds it
    /// with no custom binder. The value is the LAST part and may itself
    /// contain "~" — the split stops after the second separator.
    /// </summary>
    public static class CustomFieldFilterCodec
    {
        public const char Separator = '~';

        public static string Encode(CustomFieldFilter f)
            => $"{f.FieldId:N}{Separator}{f.Op}{Separator}{f.Value}";

        public static bool TryDecode(string? raw, out CustomFieldFilter filter)
        {
            filter = new CustomFieldFilter();
            if (string.IsNullOrWhiteSpace(raw)) return false;

            var parts = raw.Split(Separator, 3);
            if (parts.Length != 3) return false;
            if (!Guid.TryParse(parts[0], out var id)) return false;

            var op = parts[1].Trim().ToLowerInvariant();
            if (op.Length == 0 || parts[2].Length == 0) return false;

            filter = new CustomFieldFilter { FieldId = id, Op = op, Value = parts[2] };
            return true;
        }
    }

    // =================================================================
    // DROPDOWN OPTIONS ⇄ JSON
    // =================================================================

    public static class CustomFieldOptionsJson
    {
        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>Never throws. Unreadable JSON reads as "no options".</summary>
        public static List<CustomFieldOptionDto> Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<CustomFieldOptionDto>();
            try
            {
                return JsonSerializer.Deserialize<List<CustomFieldOptionDto>>(json, Json)
                       ?? new List<CustomFieldOptionDto>();
            }
            catch (JsonException)
            {
                return new List<CustomFieldOptionDto>();
            }
        }

        public static string? Serialize(IReadOnlyCollection<CustomFieldOptionDto> options)
            => options.Count == 0 ? null : JsonSerializer.Serialize(options, Json);

        /// <summary>"o_" + 8 hex characters. Short enough to read in SSMS.</summary>
        public static string NewKey() => "o_" + Guid.NewGuid().ToString("N")[..8];
    }

    // =================================================================
    // VALUE RULES — the one parser
    // =================================================================

    /// <summary>The result of reading one submitted value.</summary>
    public sealed class CustomFieldParsedValue
    {
        /// <summary>True when nothing was entered — the stored value (if any) is to be removed.</summary>
        public bool IsEmpty { get; init; }

        public string? Text { get; init; }
        public decimal? Number { get; init; }
        public DateOnly? Date { get; init; }
        public bool? Bool { get; init; }

        /// <summary>A sentence for the person, naming the field. Null when the value is fine.</summary>
        public string? Error { get; init; }

        public static CustomFieldParsedValue Empty() => new() { IsEmpty = true };
        public static CustomFieldParsedValue Fail(string message) => new() { Error = message };
    }

    public static class CustomFieldValueRules
    {
        /// <summary>
        /// Read one submitted value against its field.
        ///
        /// <paramref name="currentKey"/> is the dropdown option this record
        /// already holds. A RETIRED option is accepted only when it is the
        /// one already stored — so editing a contact's phone number does not
        /// fail because its "Licence tier" was retired last month — and is
        /// refused when somebody tries to newly pick it.
        /// </summary>
        public static CustomFieldParsedValue Parse(
            string fieldType,
            string label,
            int decimalPlaces,
            IReadOnlyList<CustomFieldOptionDto> options,
            string? raw,
            string? currentKey = null)
        {
            var value = raw?.Trim();

            switch (fieldType)
            {
                case CustomFieldTypes.Checkbox:
                    // An unticked box posts "false" (the hidden input), a
                    // ticked one "true". Unticked is stored as NO ROW.
                    if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(value, "on",   StringComparison.OrdinalIgnoreCase))
                        return new CustomFieldParsedValue { Bool = true };
                    return CustomFieldParsedValue.Empty();
            }

            if (string.IsNullOrEmpty(value))
                return CustomFieldParsedValue.Empty();

            switch (fieldType)
            {
                case CustomFieldTypes.Text:
                    if (value.Length > CustomFieldLimits.TextValueMaxLength)
                        return CustomFieldParsedValue.Fail(
                            $"{label} cannot be longer than {CustomFieldLimits.TextValueMaxLength} characters.");
                    return new CustomFieldParsedValue { Text = value };

                case CustomFieldTypes.Number:
                {
                    // Invariant, and no thousands separators: this is the
                    // shape <input type="number"> posts in every browser
                    // language. "1,250" is refused rather than guessed at —
                    // in en-IN it is 1250, in some European locales 1.25.
                    if (!decimal.TryParse(value,
                            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                            CultureInfo.InvariantCulture, out var n))
                        return CustomFieldParsedValue.Fail($"{label} must be a number.");

                    if (Math.Abs(n) > CustomFieldLimits.MaxAbsNumber)
                        return CustomFieldParsedValue.Fail($"{label} is too large.");

                    var places = Math.Clamp(decimalPlaces, 0, CustomFieldLimits.MaxDecimalPlaces);
                    if (decimal.Round(n, places) != n)
                        return CustomFieldParsedValue.Fail(places == 0
                            ? $"{label} must be a whole number."
                            : $"{label} accepts up to {places} decimal place{(places == 1 ? "" : "s")}.");

                    return new CustomFieldParsedValue { Number = n };
                }

                case CustomFieldTypes.Date:
                {
                    if (!DateOnly.TryParseExact(value, "yyyy-MM-dd",
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                        return CustomFieldParsedValue.Fail($"{label} must be a valid date.");

                    if (d < CustomFieldLimits.MinDate || d > CustomFieldLimits.MaxDate)
                        return CustomFieldParsedValue.Fail(
                            $"{label} must be between {CustomFieldLimits.MinDate.Year} and {CustomFieldLimits.MaxDate.Year}.");

                    return new CustomFieldParsedValue { Date = d };
                }

                case CustomFieldTypes.Dropdown:
                {
                    var option = options.FirstOrDefault(o => string.Equals(o.Key, value, StringComparison.Ordinal));
                    if (option is null)
                        return CustomFieldParsedValue.Fail(
                            $"The choice for {label} is no longer available. Please pick again.");

                    if (!option.IsActive && !string.Equals(option.Key, currentKey, StringComparison.Ordinal))
                        return CustomFieldParsedValue.Fail(
                            $"\"{option.Label}\" has been retired for {label}. Please pick another.");

                    return new CustomFieldParsedValue { Text = option.Key };
                }

                default:
                    return CustomFieldParsedValue.Fail($"{label} has a type this version does not recognise.");
            }
        }

        /// <summary>
        /// A stored value back into the wire shape — what the Edit form puts
        /// in its inputs and what ContactDto carries. Null when there is
        /// nothing stored for that type.
        /// </summary>
        public static string? ToWire(string fieldType, string? text, decimal? number, DateOnly? date, bool? flag)
            => fieldType switch
            {
                CustomFieldTypes.Text     => text,
                CustomFieldTypes.Dropdown => text,
                // "0.####": no trailing zeros, no grouping, '.' always.
                CustomFieldTypes.Number   => number?.ToString("0.####", CultureInfo.InvariantCulture),
                CustomFieldTypes.Date     => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                CustomFieldTypes.Checkbox => flag == true ? "true" : null,
                _                         => null
            };
    }
}
