// =====================================================================
// ProductCatalog.cs
// Location: MerkaiTrial.Application/Configuration/ProductCatalog.cs
//
// NEW FILE (051). The catalogue's vocabulary: what KIND a product is, and
// what UNIT it is sold in.
//
// WHY THIS FILE EXISTS
//
//   The three product types were a string array written out twice —
//   once in Pages/Products/Create.cshtml.cs and again in Edit.cshtml.cs:
//
//       var types = new[] { "Product", "Service", "Subscription" };
//
//   Two copies of the same truth, no constants anywhere, and the value
//   stored on Product.Type never affected anything. A typo in either list
//   would have written a kind nothing else recognises, silently.
//
//   Units of measure did not exist at all, which is the bigger problem —
//   see UnitsOfMeasure below.
//
// Deliberately NOT tenant-configurable yet. Categories already are (via
// ProductCategoriesConfiguration), and a tenant-editable unit list is a
// settings screen, a migration and a seeding story. These two lists cover
// the four markets Merkai sells into; when a customer needs a unit that
// is not here, that is the moment to build the screen.
// =====================================================================

namespace MerkaiTrial.Application.Configuration;

/// <summary>
/// What a catalogue line IS. Stored on Product.Type — the existing
/// column, not a new one, so nothing has to be migrated or backfilled.
/// </summary>
public static class ProductKinds
{
    public const string Product      = "Product";
    public const string Service      = "Service";
    public const string Subscription = "Subscription";

    /// <summary>The three, in the order the dropdown should show them.</summary>
    public static readonly string[] All = { Product, Service, Subscription };

    public static bool IsKnown(string? value)
        => value is not null && All.Contains(value, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Falls back to Product rather than throwing. A row carrying an
    /// unrecognised kind — one typed in before this file existed — should
    /// still open, edit and quote; the dropdown will simply show Product
    /// until someone saves it.
    /// </summary>
    public static string Normalise(string? value)
        => All.FirstOrDefault(k => string.Equals(k, value, StringComparison.OrdinalIgnoreCase))
           ?? Product;

    public static string Icon(string? kind) => Normalise(kind) switch
    {
        Service      => "bi-tools",
        Subscription => "bi-arrow-repeat",
        _            => "bi-box-seam"
    };

    /// <summary>One line telling the person what the kind means for them.</summary>
    public static string Hint(string? kind) => Normalise(kind) switch
    {
        Service      => "Sold by time or effort — priced per hour, day or job.",
        Subscription => "Recurring. Renewals aren't tracked yet, so treat it as a one-off for now.",
        _            => "A physical or countable item."
    };

    /// <summary>
    /// The unit that makes sense as a starting point for a kind. Only a
    /// default — the person can pick anything.
    /// </summary>
    public static string DefaultUnit(string? kind) => Normalise(kind) switch
    {
        Service      => UnitsOfMeasure.Hour,
        Subscription => UnitsOfMeasure.Month,
        _            => UnitsOfMeasure.Unit
    };
}

/// <summary>
/// What a quantity of this product MEANS.
///
/// ── WHY THIS MATTERS MORE THAN IT LOOKS ──────────────────────────────
///
/// Until now a quote line's quantity was a bare number with no unit, and
/// an INTEGER at that. So an interiors firm quoting 12.5 m² of flooring
/// had nowhere to put either the 12.5 or the m²: the real quantity went
/// into the description as prose, and the unit price was fudged to make
/// the line total come out right.
///
/// The line total was correct. Everything else was not — every report
/// about WHAT was sold, every per-unit margin, every "how much flooring
/// did we quote this quarter".
///
/// Merkai's markets make this ordinary rather than exotic: interiors and
/// construction quote in m², consultancies in days, freight by weight,
/// software by seat. A catalogue that only counts whole things serves
/// none of them.
///
/// The Code is what gets STORED and what a line snapshots. Short, stable
/// and lowercase — renaming a label is a display change, renaming a code
/// would orphan every line that used it.
/// </summary>
public static class UnitsOfMeasure
{
    public const string Unit   = "unit";
    public const string Hour   = "hour";
    public const string Day    = "day";
    public const string Month  = "month";
    public const string Year   = "year";
    public const string User   = "user";
    public const string SqM    = "sqm";
    public const string SqFt   = "sqft";
    public const string Metre  = "m";
    public const string Kg     = "kg";
    public const string Litre  = "litre";
    public const string Set    = "set";

    /// <param name="Code">Stored on the product and snapshotted onto the line.</param>
    /// <param name="Label">What a person picks from the dropdown.</param>
    /// <param name="Short">What prints beside a number: "12.5 m²".</param>
    /// <param name="Group">Only groups the dropdown.</param>
    public readonly record struct Uom(string Code, string Label, string Short, string Group);

    public static readonly Uom[] All =
    {
        new(Unit,  "Unit / piece",   "",      "General"),
        new(Set,   "Set",            "sets",  "General"),

        new(Hour,  "Hour",           "hrs",   "Time"),
        new(Day,   "Day",            "days",  "Time"),
        new(Month, "Month",          "mo",    "Time"),
        new(Year,  "Year",           "yr",    "Time"),

        new(User,  "User / seat",    "users", "Licensing"),

        new(SqM,   "Square metre",   "m²",    "Area and length"),
        new(SqFt,  "Square foot",    "ft²",   "Area and length"),
        new(Metre, "Metre",          "m",     "Area and length"),

        new(Kg,    "Kilogram",       "kg",    "Weight and volume"),
        new(Litre, "Litre",          "L",     "Weight and volume")
    };

    public static bool IsKnown(string? code)
        => code is not null && All.Any(u => string.Equals(u.Code, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// An unknown or missing code becomes "unit" rather than throwing, so a
    /// row written before this existed still renders.
    /// </summary>
    public static string Normalise(string? code)
        => All.FirstOrDefault(u => string.Equals(u.Code, code, StringComparison.OrdinalIgnoreCase)).Code
           ?? Unit;

    public static Uom Find(string? code)
    {
        var normalised = Normalise(code);
        return All.First(u => u.Code == normalised);
    }

    public static string LabelOf(string? code) => Find(code).Label;

    /// <summary>
    /// The suffix for a number. EMPTY for a plain unit, on purpose: "3"
    /// reads better than "3 units" on a quote line, and a unit that has
    /// nothing to say should say nothing.
    /// </summary>
    public static string ShortOf(string? code) => Find(code).Short;

    /// <summary>
    /// "12.5 m²", "3", "2 days". The quantity is trimmed of trailing zeros
    /// so a whole number does not print as "3.00" — a decimal column is
    /// there for the businesses that need it, not to make everyone else's
    /// quotes uglier.
    /// </summary>
    public static string Describe(decimal quantity, string? code)
    {
        var number = quantity.ToString("0.####",
            System.Globalization.CultureInfo.InvariantCulture);

        var suffix = ShortOf(code);

        return string.IsNullOrEmpty(suffix) ? number : $"{number} {suffix}";
    }

    public static IEnumerable<IGrouping<string, Uom>> Grouped()
        => All.GroupBy(u => u.Group);
}

/// <summary>
/// The tax classification code an invoice has to carry in some markets.
///
/// India is the reason this exists: a GST invoice needs an HSN code for
/// goods and a SAC code for services, and both live on the product. It is
/// a free-text string rather than a validated list because the code sets
/// are long, they change, and they differ by country — what Merkai can
/// usefully do is carry it, print it, and not lose it.
///
/// Nullable everywhere. A Thai or Philippine tenant will never fill it in,
/// and nothing should nag them to.
/// </summary>
public static class TaxCodes
{
    /// <summary>
    /// What to call the field for this tenant.
    ///
    /// Keyed on the CURRENCY code, not the country. ICurrentTenantService
    /// exposes GetCurrencyCode() and no country, and across Merkai's four
    /// markets the two map one to one — INR means India. If a tenant's
    /// country ever becomes available on that service, this is the one
    /// place to switch it over.
    /// </summary>
    public static string LabelFor(string? currencyCode) =>
        (currencyCode ?? string.Empty).ToUpperInvariant() switch
        {
            "INR" => "HSN / SAC code",
            _     => "Tax classification code"
        };

    /// <summary>One line of help under the field.</summary>
    public static string HintFor(string? currencyCode) =>
        (currencyCode ?? string.Empty).ToUpperInvariant() switch
        {
            "INR" => "HSN for goods, SAC for services. Printed on the GST invoice.",
            _     => "Optional. Printed on the invoice where your tax authority asks for it."
        };
}
