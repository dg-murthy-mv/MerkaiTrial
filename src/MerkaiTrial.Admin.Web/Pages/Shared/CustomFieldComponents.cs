// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Shared/CustomFieldComponents.cs
//
// NEW FILE (075 — custom fields, Round A).
//
// 076 — CustomFieldListFilterState: reading the list page's custom field
//   filters out of the query string, checking them, and turning them into
//   (a) the CustomFieldFilter list the API wants, (b) the route values the
//   pager and links must carry, and (c) what the filter inputs show. Used
//   by Contacts/Index now and by every list page in round C, through the
//   shared _CustomFieldFilterPanel partial.
//
// The view models and helpers behind the two shared partials:
//
//   _CustomFieldInputs.cshtml   the inputs, on Create AND Edit
//   _CustomFieldValues.cshtml   the read-only card, on Detail
//
// ONE PARTIAL FOR BOTH FORMS, NOT TWO COPIES. Round 029 paid for this on
// the quote line editor: Create and Edit each had their own copy, they
// drifted, and one grew an XSS hole the other did not have. Round C
// will render these same two partials on Companies.
//
// Field ids are posted as  CustomFields[<guid>] = <wire value>
// which binds to a Dictionary<string, string?> on the page model. String
// keys, not Guid keys, so a tampered key fails our own Guid.TryParse
// quietly instead of leaving a model-binding error somewhere nobody looks
// (the bool-binding trap from round 071b, in a different shape).
// =====================================================================

using System.Globalization;
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.DTOs;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Primitives;

namespace MerkaiTrial.Admin.Web.Pages.Shared
{
    /// <summary>Model for _CustomFieldInputs.cshtml.</summary>
    public class CustomFieldFormVm
    {
        /// <summary>ACTIVE fields only, in display order.</summary>
        public List<CustomFieldDefinitionDto> Fields { get; set; } = new();

        /// <summary>Field id (string) → wire value. What the inputs show.</summary>
        public IDictionary<string, string?> Values { get; set; } = new Dictionary<string, string?>();

        /// <summary>The form-field prefix. Must match the page's bound dictionary name.</summary>
        public string FieldPrefix { get; set; } = CustomFieldForm.DefaultPrefix;

        /// <summary>True when the definitions could not be loaded. The partial says so.</summary>
        public bool LoadFailed { get; set; }

        /// <summary>Show the "Manage fields" link (settings.read).</summary>
        public bool CanConfigure { get; set; }

        /// <summary>"Contact" — for the Manage link's ?entity=.</summary>
        public string EntityType { get; set; } = CustomFieldEntityTypes.Contact;
    }

    /// <summary>Model for _CustomFieldValues.cshtml.</summary>
    public class CustomFieldDisplayVm
    {
        /// <summary>ALL fields, including switched-off ones, in display order.</summary>
        public List<CustomFieldDefinitionDto> Fields { get; set; } = new();

        /// <summary>Field id → wire value, as stored.</summary>
        public IDictionary<Guid, string> Values { get; set; } = new Dictionary<Guid, string>();

        /// <summary>Tenant culture for numbers (Countries.NumberFormat).</summary>
        public CultureInfo Culture { get; set; } = CultureInfo.InvariantCulture;

        /// <summary>Tenant date pattern (Countries.DateFormat), e.g. "dd/MM/yyyy".</summary>
        public string DateFormat { get; set; } = "dd/MM/yyyy";

        public bool LoadFailed { get; set; }
        public bool CanConfigure { get; set; }
        public bool CanEdit { get; set; }
        public Guid RecordId { get; set; }
        public string EntityType { get; set; } = CustomFieldEntityTypes.Contact;

        /// <summary>The record's edit page, for the card's Edit link. Round C sets "/Companies/Edit".</summary>
        public string EditPage { get; set; } = "/Contacts/Edit";

        /// <summary>Active fields, plus retired fields that still hold a value on this record.</summary>
        public IEnumerable<CustomFieldDefinitionDto> Visible =>
            Fields.Where(f => f.IsActive || Values.ContainsKey(f.Id));
    }

    /// <summary>Turning a posted form into what the API wants, and checking it first.</summary>
    public static class CustomFieldForm
    {
        public const string DefaultPrefix = "CustomFields";

        /// <summary>
        /// The map the API receives. Only ACTIVE fields the form actually
        /// rendered are sent; anything else in the post — a stale or forged
        /// key — is left out here, and the API refuses unknown ids anyway.
        /// </summary>
        public static Dictionary<Guid, string?> ToSubmission(
            IDictionary<string, string?>? posted,
            IEnumerable<CustomFieldDefinitionDto> activeFields)
        {
            var active = activeFields.Where(f => f.IsActive).Select(f => f.Id).ToHashSet();
            var result = new Dictionary<Guid, string?>();

            if (posted is null) return result;

            foreach (var (key, value) in posted)
            {
                if (!Guid.TryParse(key, out var id)) continue;
                if (!active.Contains(id)) continue;
                result[id] = value;
            }
            return result;
        }

        /// <summary>
        /// Check every rendered field before the API is called, putting each
        /// error on its own input's ModelState key — so the message appears
        /// under "Renewal date", not in a banner at the top. The API checks
        /// again; this is the friendly copy, not the guard.
        /// </summary>
        public static void Validate(
            IEnumerable<CustomFieldDefinitionDto> activeFields,
            IDictionary<string, string?>? posted,
            ModelStateDictionary modelState,
            string prefix = DefaultPrefix)
        {
            foreach (var f in activeFields.Where(f => f.IsActive))
            {
                var key = f.Id.ToString();
                string? raw = null;
                posted?.TryGetValue(key, out raw);

                // currentKey = raw: a retired dropdown choice that is already
                // selected is accepted here, and the API — which knows what is
                // actually stored — makes the strict decision.
                var parsed = CustomFieldValueRules.Parse(f.FieldType, f.Label, f.DecimalPlaces, f.Options, raw, currentKey: raw);

                var modelKey = $"{prefix}[{key}]";

                if (parsed.Error is not null)
                    modelState.AddModelError(modelKey, parsed.Error);
                else if (parsed.IsEmpty && f.IsRequired && CustomFieldTypes.SupportsRequired(f.FieldType))
                    modelState.AddModelError(modelKey, $"{f.Label} is required.");
            }
        }

        /// <summary>Stored values (Guid keys) → what the inputs show (string keys).</summary>
        public static Dictionary<string, string?> FromStored(IDictionary<Guid, string>? stored)
            => stored is null
                ? new Dictionary<string, string?>()
                : stored.ToDictionary(p => p.Key.ToString(), p => (string?)p.Value);

        /// <summary>The error for one field's input, or null.</summary>
        public static string? ErrorFor(ModelStateDictionary modelState, string prefix, Guid fieldId)
            => modelState.TryGetValue($"{prefix}[{fieldId}]", out var entry) && entry.Errors.Count > 0
                ? entry.Errors[0].ErrorMessage
                : null;

        /// <summary>
        /// The step attribute for a number input: "1" for whole numbers,
        /// "0.01" for two places. With the wrong step a browser refuses a
        /// valid value with its own unhelpful bubble.
        /// </summary>
        public static string StepFor(int decimalPlaces)
        {
            var places = Math.Clamp(decimalPlaces, 0, CustomFieldLimits.MaxDecimalPlaces);
            return places == 0 ? "1" : "0." + new string('0', places - 1) + "1";
        }
    }

    // =================================================================
    // 076 — LIST FILTERS
    // =================================================================

    /// <summary>
    /// The custom field filters on a list page, read from the query string.
    ///
    /// QUERY KEYS, one or two per field, all prefixed "cf_" + the field id
    /// (32 hex characters, no dashes):
    ///
    ///   text      cf_&lt;id&gt;              contains this text
    ///   dropdown  cf_&lt;id&gt;              this choice (the option KEY)
    ///   checkbox  cf_&lt;id&gt;              "yes" | "no"
    ///   number    cf_&lt;id&gt;_min / _max   at least / at most
    ///   date      cf_&lt;id&gt;_from / _to   on or after / on or before
    ///
    /// Readable, bookmarkable, and independent of the order fields are
    /// listed in. A value that cannot be read is NOT applied, and a warning
    /// names the field — silently ignoring "12,5" would show a list that
    /// looks filtered and is not. The raw text stays in the input so it
    /// can be corrected.
    /// </summary>
    public sealed class CustomFieldListFilterState
    {
        public const string Prefix = "cf_";

        /// <summary>The fields the panel offers: ACTIVE fields, in display order.</summary>
        public List<CustomFieldDefinitionDto> Fields { get; init; } = new();

        /// <summary>Usable filters, for the API.</summary>
        public List<CustomFieldFilter> Filters { get; } = new();

        /// <summary>The usable filters as query values — carried by the pager and every list link.</summary>
        public Dictionary<string, string> Route { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Everything typed, valid or not, so the inputs show it back.</summary>
        public Dictionary<string, string> Raw { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>One sentence per filter that could not be applied.</summary>
        public List<string> Warnings { get; } = new();

        /// <summary>Fields with at least one usable condition — the number on the panel's button.</summary>
        public int ActiveCount => Filters.Select(f => f.FieldId).Distinct().Count();

        public bool HasAny => Filters.Count > 0;

        // ── Set by the page, read by the partial ─────────────────────

        /// <summary>The toolbar form's id; every panel input submits with it.</summary>
        public string FormId { get; set; } = "listFilterForm";

        /// <summary>The list page, for the "Clear these" link.</summary>
        public string PageRoute { get; set; } = "./Index";

        /// <summary>The page's own filters WITHOUT the custom ones — what "Clear these" keeps.</summary>
        public Dictionary<string, string> ClearRoute { get; set; } = new();

        /// <summary>True when the field list could not be loaded; the partial says so.</summary>
        public bool LoadFailed { get; set; }

        /// <summary>"contact" — for the panel's one sentence.</summary>
        public string Noun { get; set; } = "record";

        /// <summary>Tenant number culture and date pattern, for the applied-filter chips.</summary>
        public CultureInfo Culture { get; set; } = CultureInfo.InvariantCulture;
        public string DateFormat { get; set; } = "dd/MM/yyyy";

        public static string KeyFor(Guid fieldId, string suffix = "") => Prefix + fieldId.ToString("N") + suffix;

        public string RawFor(string key) => Raw.TryGetValue(key, out var v) ? v : string.Empty;

        /// <summary>
        /// Read the filters from a query string (on GET) or a posted form
        /// (the delete dialog carries the list state). Never throws.
        /// </summary>
        public static CustomFieldListFilterState Parse(
            IEnumerable<KeyValuePair<string, StringValues>> source,
            IEnumerable<CustomFieldDefinitionDto> fields)
        {
            var state = new CustomFieldListFilterState
            {
                Fields = fields.Where(f => f.IsActive).OrderBy(f => f.SortOrder).ToList()
            };

            // First value per key, case-insensitive, and only our own keys.
            var input = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, values) in source)
            {
                if (!key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var first = values.Count > 0 ? values[0]?.Trim() : null;
                if (!string.IsNullOrEmpty(first) && !input.ContainsKey(key)) input[key] = first;
            }

            foreach (var f in state.Fields)
            {
                switch (f.FieldType)
                {
                    case CustomFieldTypes.Text:
                    {
                        var key = KeyFor(f.Id);
                        if (!input.TryGetValue(key, out var v)) break;
                        state.Raw[key] = v;

                        if (v.Length > 200)
                        {
                            state.Warnings.Add($"{f.Label}: the search text is too long.");
                            break;
                        }
                        state.Add(f.Id, CustomFieldFilterOps.Contains, v, key, v);
                        break;
                    }

                    case CustomFieldTypes.Dropdown:
                    {
                        var key = KeyFor(f.Id);
                        if (!input.TryGetValue(key, out var v)) break;
                        state.Raw[key] = v;

                        if (!f.Options.Any(o => o.Key == v))
                        {
                            state.Warnings.Add($"{f.Label}: that choice no longer exists, so it was not applied.");
                            break;
                        }
                        state.Add(f.Id, CustomFieldFilterOps.Exactly, v, key, v);
                        break;
                    }

                    case CustomFieldTypes.Checkbox:
                    {
                        var key = KeyFor(f.Id);
                        if (!input.TryGetValue(key, out var v)) break;
                        state.Raw[key] = v;

                        if (string.Equals(v, "yes", StringComparison.OrdinalIgnoreCase))
                            state.Add(f.Id, CustomFieldFilterOps.Is, "true", key, "yes");
                        else if (string.Equals(v, "no", StringComparison.OrdinalIgnoreCase))
                            state.Add(f.Id, CustomFieldFilterOps.Is, "false", key, "no");
                        break;
                    }

                    case CustomFieldTypes.Number:
                        state.ParseRange<decimal>(f, input, "_min", "_max", "a number",
                            raw => decimal.TryParse(raw,
                                       NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                                       CultureInfo.InvariantCulture, out var n)
                                   ? n : (decimal?)null,
                            n => n.ToString("0.####", CultureInfo.InvariantCulture));
                        break;

                    case CustomFieldTypes.Date:
                        state.ParseRange<DateOnly>(f, input, "_from", "_to", "a date",
                            raw => DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                       DateTimeStyles.None, out var d)
                                   ? d : (DateOnly?)null,
                            d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                        break;
                }
            }

            return state;
        }

        private void Add(Guid fieldId, string op, string value, string routeKey, string routeValue)
        {
            Filters.Add(new CustomFieldFilter { FieldId = fieldId, Op = op, Value = value });
            Route[routeKey] = routeValue;
        }

        /// <summary>A from/to pair. Both ends optional; a reversed pair applies neither.</summary>
        private void ParseRange<T>(
            CustomFieldDefinitionDto f,
            IReadOnlyDictionary<string, string> input,
            string lowSuffix, string highSuffix, string kind,
            Func<string, T?> parse, Func<T, string> wire) where T : struct, IComparable<T>
        {
            var lowKey  = KeyFor(f.Id, lowSuffix);
            var highKey = KeyFor(f.Id, highSuffix);

            T? low = null, high = null;

            if (input.TryGetValue(lowKey, out var lowRaw))
            {
                Raw[lowKey] = lowRaw;
                low = parse(lowRaw);
                if (low is null) Warnings.Add($"{f.Label}: \"{lowRaw}\" is not {kind}, so it was not applied.");
            }

            if (input.TryGetValue(highKey, out var highRaw))
            {
                Raw[highKey] = highRaw;
                high = parse(highRaw);
                if (high is null) Warnings.Add($"{f.Label}: \"{highRaw}\" is not {kind}, so it was not applied.");
            }

            if (low is { } l && high is { } h && l.CompareTo(h) > 0)
            {
                Warnings.Add($"{f.Label}: the first value is after the second, so neither was applied.");
                return;
            }

            if (low is { } lo)  Add(f.Id, CustomFieldFilterOps.AtLeast, wire(lo), lowKey,  wire(lo));
            if (high is { } hi) Add(f.Id, CustomFieldFilterOps.AtMost,  wire(hi), highKey, wire(hi));
        }
    }

    /// <summary>Stored value → what a person reads, in the tenant's own format.</summary>
    public static class CustomFieldFormatter
    {
        /// <summary>
        /// The tenant's number culture. Countries.NumberFormat may still hold
        /// a legacy PATTERN ("#,##0.00") rather than a culture name; that, or
        /// anything unrecognised, falls back to InvariantCulture — the same
        /// rule ICurrentTenantService.FormatCurrency follows.
        /// </summary>
        public static CultureInfo ResolveCulture(string? name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.IndexOf('#') >= 0 || name.IndexOf('0') >= 0)
                return CultureInfo.InvariantCulture;
            try
            {
                return CultureInfo.GetCultureInfo(name.Trim());
            }
            catch (CultureNotFoundException)
            {
                return CultureInfo.InvariantCulture;
            }
        }

        /// <summary>
        /// Never throws. An unreadable stored value shows as the raw text
        /// rather than taking the page down.
        /// </summary>
        public static string Display(
            CustomFieldDefinitionDto field, string? wire, CultureInfo culture, string dateFormat)
        {
            if (field.FieldType == CustomFieldTypes.Checkbox)
                return wire == "true" ? "Yes" : "No";

            if (string.IsNullOrEmpty(wire)) return string.Empty;

            switch (field.FieldType)
            {
                case CustomFieldTypes.Number:
                    return decimal.TryParse(wire, NumberStyles.Number, CultureInfo.InvariantCulture, out var n)
                        ? n.ToString("N" + Math.Clamp(field.DecimalPlaces, 0, CustomFieldLimits.MaxDecimalPlaces), culture)
                        : wire;

                case CustomFieldTypes.Date:
                    // A calendar date: formatted, never timezone-converted.
                    // InvariantCulture so "/" in the tenant's pattern prints as
                    // "/" rather than the server culture's date separator.
                    if (!DateOnly.TryParseExact(wire, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out var d))
                        return wire;
                    try
                    {
                        return d.ToString(string.IsNullOrWhiteSpace(dateFormat) ? "dd/MM/yyyy" : dateFormat,
                                          CultureInfo.InvariantCulture);
                    }
                    catch (FormatException)
                    {
                        return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    }

                case CustomFieldTypes.Dropdown:
                {
                    var option = field.Options.FirstOrDefault(o => o.Key == wire);
                    return option is null ? "(removed choice)" : option.Label;
                }

                default:
                    return wire;
            }
        }
    }
}
