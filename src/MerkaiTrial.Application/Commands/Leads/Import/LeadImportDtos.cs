// =====================================================================
// LeadImportDtos.cs
// Location: MerkaiTrial.Application/Commands/Leads/Import/LeadImportDtos.cs
//
// NEW FILE.
//
// Three-step import: UPLOAD -> PREVIEW -> COMMIT.
// The parsed file is held server-side between steps and referenced by
// SessionId, so a 2,000-row sheet is not shipped back and forth as JSON.
//
// 092 — CUSTOM FIELDS, AND A FILE THAT GOES OUT AND COMES BACK
//   • ImportField.IsCustom: the workspace's active Lead custom fields are
//     mapping targets too, keyed "cf_" + the field id (ImportFieldKeys).
//     Underscore, not a colon or dot: the key travels as a form name,
//     Mapping[cf_…], and a dot there means "property path" to the binder.
//   • ImportUploadResult.Fields: every field the page can map, built-in
//     first, then custom — so the page never has to guess which custom
//     fields exist. Optional and trailing.
//   • ImportRequest.DateFormat: the workspace's date format, set by the
//     controller (never trusted from the browser), used to read custom
//     DATE columns. 03/04/2026 is 3 April in Mumbai and 4 March in Manila,
//     so the importer reads dates the way THIS workspace writes them.
//   • ImportFields.ExportHeading: the Export button writes exactly these
//     headings, and the auto-mapper tries them first, so an exported file
//     re-imports with every column already matched.
// =====================================================================

namespace MerkaiTrial.Application.Commands.Leads.Import;

/// <summary>
/// The lead fields a spreadsheet column can be mapped onto. Keys are
/// stable strings — they travel to the browser and back, so renaming one
/// breaks in-flight imports.
/// </summary>
public sealed record ImportField(
    string Key,
    string Label,
    bool IsRequired,
    string? Hint = null,
    /// <summary>092. One of the workspace's Lead custom fields.</summary>
    bool IsCustom = false);

/// <summary>092. Mapping keys for custom fields: "cf_" + the field id, no dashes.</summary>
public static class ImportFieldKeys
{
    public const string CustomPrefix = "cf_";

    public static string ForCustomField(Guid fieldId) => CustomPrefix + fieldId.ToString("N");

    public static bool TryParseCustom(string? key, out Guid fieldId)
    {
        fieldId = Guid.Empty;
        return key is not null
               && key.StartsWith(CustomPrefix, StringComparison.Ordinal)
               && Guid.TryParseExact(key[CustomPrefix.Length..], "N", out fieldId);
    }
}

public static class ImportFields
{
    public const string FullName      = "fullName";
    public const string Email         = "email";
    public const string Phone         = "phone";
    public const string CompanyName   = "companyName";
    public const string Address       = "address";
    public const string Country       = "country";
    public const string Source        = "source";
    public const string Channel       = "channel";
    public const string Status        = "status";
    public const string Score         = "score";
    public const string EstimatedValue= "estimatedValue";
    public const string Currency      = "currency";
    public const string Owner         = "owner";
    public const string Vertical      = "vertical";

    public static readonly IReadOnlyList<ImportField> All = new List<ImportField>
    {
        new(FullName,       "Full name",       true,  "The only required column"),
        new(Email,          "Email",           false),
        new(Phone,          "Phone",           false, "Any format — normalised on import"),
        new(CompanyName,    "Company",         false),
        new(Address,        "Address",         false),
        new(Country,        "Country",         false, "Name or 2-letter code"),
        new(Source,         "Lead source",     false, "Matched to your configured sources"),
        new(Channel,        "Channel",         false, "Matched to your configured channels"),
        new(Status,         "Status",          false, "New / Working / Qualified / Unqualified"),
        new(Score,          "Score",           false, "0-100"),
        new(EstimatedValue, "Estimated value", false, "Currency symbols and separators are stripped"),
        new(Currency,       "Currency",        false, "Defaults to the workspace currency"),
        new(Owner,          "Owner",           false, "Email address of a user in this workspace"),
        new(Vertical,       "Industry",        false, "Matched to your verticals list"),
    };

    /// <summary>
    /// 092. The heading the Export button writes for a built-in field — its
    /// label. Every one of these is also an exact synonym below, so an
    /// exported file maps itself on the way back in.
    /// </summary>
    public static string ExportHeading(string key)
        => All.FirstOrDefault(f => f.Key == key)?.Label ?? key;

    /// <summary>
    /// Header text that maps to each field, lowercased and stripped of
    /// spaces, underscores and punctuation before comparison. Covers the
    /// wording Zoho, HubSpot and hand-kept sheets tend to use.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> Synonyms =
        new Dictionary<string, string[]>
        {
            [FullName]       = new[] { "fullname", "name", "leadname", "contactname", "customername",
                                       "firstname", "person", "clientname", "ชื่อ" },
            [Email]          = new[] { "email", "emailaddress", "mail", "emailid", "primaryemail",
                                       "workemail", "อีเมล" },
            [Phone]          = new[] { "phone", "phonenumber", "mobile", "mobilenumber", "contactnumber",
                                       "contact", "tel", "telephone", "cell", "whatsapp", "เบอร์โทร" },
            [CompanyName]    = new[] { "company", "companyname", "organisation", "organization",
                                       "account", "accountname", "business", "firm" },
            [Address]        = new[] { "address", "streetaddress", "location", "city", "mailingaddress" },
            [Country]        = new[] { "country", "countrycode", "nation" },
            [Source]         = new[] { "source", "leadsource", "origin", "howdidyouhear", "campaign" },
            [Channel]        = new[] { "channel", "leadchannel", "medium", "contactmethod" },
            [Status]         = new[] { "status", "leadstatus", "stage", "leadstage", "state" },
            [Score]          = new[] { "score", "leadscore", "rating", "grade" },
            [EstimatedValue] = new[] { "estimatedvalue", "value", "dealvalue", "amount", "budget",
                                       "expectedvalue", "opportunityamount", "price" },
            [Currency]       = new[] { "currency", "currencycode" },
            [Owner]          = new[] { "owner", "leadowner", "assignedto", "assignee", "salesrep",
                                       "accountowner", "responsible" },
            [Vertical]       = new[] { "vertical", "industry", "sector", "segment", "businesstype" },
        };
}

// ── UPLOAD ────────────────────────────────────────────────────────────

/// <summary>What the browser gets back after the file is parsed.</summary>
public record ImportUploadResult(
    Guid SessionId,
    string FileName,
    int TotalRows,
    IReadOnlyList<string> Headers,
    /// <summary>First few rows, so the user can see what each column holds.</summary>
    IReadOnlyList<IReadOnlyList<string>> SampleRows,
    /// <summary>field key -> column index, auto-detected. May be empty.</summary>
    IReadOnlyDictionary<string, int> SuggestedMapping,
    IReadOnlyList<string> Warnings,
    /// <summary>092. Every mappable field: built-in, then custom. Null from an older API.</summary>
    IReadOnlyList<ImportField>? Fields = null);

// ── PREVIEW / COMMIT REQUEST ──────────────────────────────────────────

public static class DuplicateAction
{
    /// <summary>Leave the existing lead untouched and skip the row.</summary>
    public const string Skip = "skip";

    /// <summary>Fill in blanks on the existing lead. Never overwrites a
    /// non-empty field, and never touches Status, Score or Owner.</summary>
    public const string Update = "update";

    /// <summary>Import anyway, accepting a second record.</summary>
    public const string Import = "import";
}

public record ImportRequest(
    Guid SessionId,
    Guid TenantId,
    /// <summary>field key -> column index. Unmapped fields are omitted.</summary>
    Dictionary<string, int> Mapping,
    string Duplicates = DuplicateAction.Skip,
    string? ImportedBy = null,
    /// <summary>092. The workspace's date format — set by the controller, never by the browser.</summary>
    string? DateFormat = null);

// ── PREVIEW RESULT ────────────────────────────────────────────────────

public static class RowVerdict
{
    public const string Ok        = "ok";
    public const string Warning   = "warning";   // imports, but something is worth seeing
    public const string Duplicate = "duplicate"; // handled per DuplicateAction
    public const string Error     = "error";     // will not import
}

public record ImportRowPreview(
    int RowNumber,               // 1-based, matching the spreadsheet (header = row 1)
    string Verdict,
    string FullName,
    string? Email,
    string? Phone,
    string? CompanyName,
    IReadOnlyList<string> Messages,
    /// <summary>Set when this row matches an existing lead.</summary>
    Guid? ExistingLeadId = null);

public record ImportPreview(
    Guid SessionId,
    int TotalRows,
    int WillImport,
    int WillUpdate,
    int WillSkip,
    int ErrorCount,
    // Quota
    int CurrentLeadCount,
    int MaxLeads,
    bool ExceedsQuota,
    /// <summary>Every row, so the user can scroll the whole file before committing.</summary>
    IReadOnlyList<ImportRowPreview> Rows,
    /// <summary>File-level messages: unmatched sources, unknown owners, etc.</summary>
    IReadOnlyList<string> Notices);

// ── COMMIT RESULT ─────────────────────────────────────────────────────

public record ImportResult(
    int TotalRows,
    int Imported,
    int Updated,
    int Skipped,
    int Failed,
    IReadOnlyList<ImportRowPreview> FailedRows);
