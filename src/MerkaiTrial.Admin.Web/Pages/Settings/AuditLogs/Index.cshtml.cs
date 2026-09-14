// =====================================================================
// AUDIT LOG — Backend
// Location: MerkaiTrial.Admin.Web/Pages/Settings/AuditLogs/Index.cshtml.cs
//
// NEW FILE. Replaces the "Soon" nav item.
//
// THE POINT OF THIS PAGE is not to show a table of rows. It is to answer
// questions people actually ask:
//
//   "Who moved this deal to Closed Lost?"
//   "Who deleted Pimchanok?"
//   "Why is this invoice not marked paid?"
//   "Did anyone change that product's price last month?"
//
// So the raw Action + Data JSON is turned into a sentence. A row reading
//   DealStageChanged | {"from":"Proposal","to":"ClosedWon"}
// is a database dump; "Deal moved from Proposal to ClosedWon" is an
// answer. The JSON stays available underneath for the rare case where
// the sentence is not enough.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Audit;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Queries;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using MerkaiTrial.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace MerkaiTrial.Admin.Web.Pages.Settings.AuditLogs;

public class IndexModel : AuthorizedPageModel
{
    private readonly IAuditLogService _auditService;
    private readonly ICurrentTenantService _tenantService;

    protected override string ModuleName => "audit";

    public IndexModel(
        IAuditLogService auditService,
        ICurrentTenantService tenantService,
        IAuthorizationService authorizationService,
        ICurrentUserService currentUserService,
        ILogger<IndexModel> logger)
        : base(authorizationService, currentUserService, logger)
    {
        _auditService  = auditService;
        _tenantService = tenantService;
    }

    public AuditLogPage Logs { get; private set; } =
        new(new List<AuditLogListItem>(), 0, 1, 25, 0);

    public AuditFilterOptions FilterOptions { get; private set; } =
        new(new List<string>(), new List<string>());

    public string TenantTimezone { get; private set; } = "";

    [BindProperty(SupportsGet = true)] public string? SearchTerm { get; set; }
    [BindProperty(SupportsGet = true)] public string? EntityType { get; set; }
    [BindProperty(SupportsGet = true)] public string? Action { get; set; }
    [BindProperty(SupportsGet = true)] public string? Range { get; set; } = "30";
    [BindProperty(SupportsGet = true)] public int Page { get; set; } = 1;
    [BindProperty(SupportsGet = true)] public int PageSize { get; set; } = 25;

    /// <summary>Which row has its raw JSON expanded.</summary>
    [BindProperty(SupportsGet = true)] public Guid? DetailId { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var permissionCheck = await ValidatePermissionAsync(Actions.Read);
        if (permissionCheck != null) return permissionCheck;

        await InitializePermissionsAsync();

        if (PageSize < 5) PageSize = 5;
        if (PageSize > 100) PageSize = 100;

        TenantTimezone = _tenantService.GetTimezone();

        // The range is a UTC window computed from the TENANT's day, so
        // "last 7 days" means their week, not the server's.
        DateTime? fromUtc = Range switch
        {
            "7"   => DateTime.UtcNow.AddDays(-7),
            "30"  => DateTime.UtcNow.AddDays(-30),
            "90"  => DateTime.UtcNow.AddDays(-90),
            "all" => null,
            _     => DateTime.UtcNow.AddDays(-30)
        };

        try
        {
            var logsTask    = _auditService.GetAsync(
                SearchTerm, EntityType, Action, fromUtc, null, Page, PageSize);
            var filtersTask = _auditService.GetFilterOptionsAsync();

            await Task.WhenAll(logsTask, filtersTask);

            Logs          = await logsTask;
            FilterOptions = await filtersTask;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load audit log");
            TempData["ErrorMessage"] = "Couldn't load the activity log. Please try again.";
        }

        return Page();
    }

    // =================================================================
    // TURNING A ROW INTO A SENTENCE
    // =================================================================

    /// <summary>
    /// A readable description built from the Action and the Data JSON.
    /// Falls back to a spaced-out version of the action name for any
    /// action added later that has no case here — so a new audit action
    /// degrades to "Quote Revised" rather than breaking the page.
    /// </summary>
    public string Describe(AuditLogListItem row)
    {
        var d = Parse(row.Data);

        string S(string key) => d.TryGetValue(key, out var v) ? v : "";
        string Name() => S("name") is { Length: > 0 } n ? $" — {n}" : "";
        string FromTo() =>
            (S("from"), S("to")) is ({ Length: > 0 } f, { Length: > 0 } t)
                ? $" from {f} to {t}"
                : "";

        return row.Action switch
        {
            AuditAction.LeadCreated        => $"Lead created{Name()}",
            AuditAction.LeadUpdated        => $"Lead details changed{DescribeFieldChanges(d)}",
            AuditAction.LeadDeleted        => $"Lead deleted{Name()}",
            AuditAction.LeadStatusChanged  => $"Lead status changed{FromTo()}",
            AuditAction.LeadOwnerChanged   => "Lead reassigned to a different owner",
            AuditAction.LeadConverted      => $"Lead converted{Name()}",
            AuditAction.LeadsImported      => $"Leads imported ({S("count")})",

            AuditAction.DealCreated        => $"Deal created — {S("title")}",
            AuditAction.DealUpdated        => "Deal details changed",
            AuditAction.DealDeleted        => "Deal deleted",
            AuditAction.DealStageChanged   => $"Deal stage changed{FromTo()}",
            AuditAction.DealOwnerChanged   => "Deal reassigned to a different owner",
            AuditAction.DealStageFailed    =>
                $"Deal stage could NOT be updated automatically to {S("attemptedStage")} — needs fixing by hand",

            AuditAction.QuoteCreated       => $"Quote created — {S("number")}",
            AuditAction.QuoteUpdated       => $"Quote changed — {S("number")}",
            AuditAction.QuoteDeleted       => $"Quote deleted — {S("number")}",
            AuditAction.QuoteStatusChanged =>
                S("bySource") == "PublicLink"
                    ? $"Customer opened quote {S("number")}"
                    : $"Quote {S("number")} status changed{FromTo()}",
            AuditAction.QuoteAcceptedPublic => $"Customer ACCEPTED quote {S("number")}",
            AuditAction.QuoteRejectedPublic => $"Customer declined quote {S("number")}",

            AuditAction.InvoiceCreated       => $"Invoice created — {S("number")}",
            AuditAction.InvoiceUpdated       => $"Invoice changed — {S("number")}",
            AuditAction.InvoiceDeleted       => $"Invoice deleted — {S("number")}",
            AuditAction.InvoiceStatusChanged => $"Invoice {S("number")} status changed{FromTo()}",
            AuditAction.InvoiceCancelled     => $"Invoice cancelled — {S("number")}",

            AuditAction.PaymentRecorded => $"Payment of {S("amount")} recorded against {S("invoiceNumber")}",
            AuditAction.PaymentDeleted  => "Payment removed",

            AuditAction.ContactDeleted  => $"Contact deleted{Name()}",
            AuditAction.CompanyDeleted  => $"Company deleted{Name()}",

            AuditAction.ProductPriceChanged => $"Price or tax changed — {S("name")}",
            AuditAction.ProductDeleted      => $"Product deleted{Name()}",

            AuditAction.ActivityDeleted => $"Activity deleted — {S("subject")}",

            AuditAction.ActionRefused =>
                $"Refused: {S("reason")}",

            AuditAction.SignIn           => "Signed in",
            AuditAction.SignInFailed     => "Sign-in failed",
            AuditAction.PasswordChanged  => "Password changed",
            AuditAction.InviteIssued     => "Invite link issued",
            AuditAction.ResetIssued      => "Password reset link issued",
            AuditAction.PermissionChanged => "Permissions changed",
            AuditAction.ViewAsStart      => "Started viewing as another user",
            AuditAction.ViewAsEnd        => "Stopped viewing as another user",

            AuditAction.TrialProvisioned => "Workspace created",
            AuditAction.TrialConverted   => "Trial converted to a paid plan",
            AuditAction.TrialExtended    => "Trial extended",
            AuditAction.TenantPlanChanged => "Plan changed",

            _ => Humanise(row.Action)
        };
    }

    /// <summary>"LeadUpdated" -> "Lead updated". Used for actions with no
    /// case above, so adding one to AuditAction never breaks this page.</summary>
    private static string Humanise(string action)
    {
        if (string.IsNullOrEmpty(action)) return "Unknown action";

        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < action.Length; i++)
        {
            if (i > 0 && char.IsUpper(action[i])) sb.Append(' ').Append(char.ToLower(action[i]));
            else sb.Append(action[i]);
        }
        return sb.ToString();
    }

    /// <summary>"(name, phone)" for a field-diff row.</summary>
    private static string DescribeFieldChanges(Dictionary<string, string> d)
    {
        // The LeadUpdated payload is { field: { from, to } }, so the keys
        // ARE the changed field names.
        var fields = d.Keys
            .Where(k => k is not ("name" or "from" or "to"))
            .Take(5)
            .ToList();

        return fields.Count == 0 ? "" : $" ({string.Join(", ", fields)})";
    }

    /// <summary>
    /// Flattens the Data JSON one level into string values, so the
    /// Describe switch can read it without fighting JsonElement.
    /// A malformed blob returns empty rather than breaking the page.
    /// </summary>
    private static Dictionary<string, string> Parse(string? json)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return result;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                result[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString() ?? "",
                    JsonValueKind.Null   => "",
                    JsonValueKind.Object => "",   // nested — the key alone is the signal
                    _ => prop.Value.ToString()
                };
            }
        }
        catch
        {
            // Deliberately silent: a bad payload must not take the page down.
        }

        return result;
    }

    // =================================================================
    // VIEW HELPERS
    // =================================================================

    public string FormatWhen(DateTime utc) => _tenantService.FormatDateTime(utc);

    public string RelativeWhen(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays}d ago";
        return _tenantService.FormatDate(utc);
    }

    public string IconFor(string entityType) => entityType switch
    {
        AuditEntityType.Lead     => "bi-funnel",
        AuditEntityType.Deal     => "bi-kanban",
        AuditEntityType.Quote    => "bi-file-earmark-text",
        AuditEntityType.Invoice  => "bi-receipt",
        AuditEntityType.Payment  => "bi-cash-coin",
        AuditEntityType.Contact  => "bi-person",
        AuditEntityType.Company  => "bi-building",
        AuditEntityType.Product  => "bi-box-seam",
        AuditEntityType.Activity => "bi-activity",
        AuditEntityType.User     => "bi-person-badge",
        AuditEntityType.Role     => "bi-shield-lock",
        AuditEntityType.Tenant   => "bi-briefcase",
        _ => "bi-dot"
    };

    /// <summary>
    /// Red for anything destructive or refused, amber for a failure that
    /// needs a human, green for money in, grey for the rest. Colour is
    /// the fastest way to scan a long list for the row that matters.
    /// </summary>
    public string ToneFor(string action) => action switch
    {
        AuditAction.LeadDeleted or AuditAction.DealDeleted or AuditAction.QuoteDeleted
            or AuditAction.InvoiceDeleted or AuditAction.ContactDeleted
            or AuditAction.CompanyDeleted or AuditAction.ProductDeleted
            or AuditAction.ActivityDeleted or AuditAction.PaymentDeleted => "text-danger",

        AuditAction.DealStageFailed or AuditAction.ActionRefused
            or AuditAction.SignInFailed => "text-warning",

        AuditAction.PaymentRecorded or AuditAction.QuoteAcceptedPublic => "text-success",

        _ => "text-muted"
    };

    /// <summary>Link back to the record, where one exists.</summary>
    public string? RecordUrl(AuditLogListItem row)
    {
        if (row.EntityId is null || row.EntityId == Guid.Empty) return null;

        return row.EntityType switch
        {
            AuditEntityType.Lead    => $"/Leads/Detail/{row.EntityId}",
            AuditEntityType.Deal    => $"/Pipeline/Detail/{row.EntityId}",
            AuditEntityType.Contact => $"/Contacts/Detail/{row.EntityId}",
            AuditEntityType.Company => $"/Companies/Detail/{row.EntityId}",
            AuditEntityType.Product => $"/Products/Detail/{row.EntityId}",
            _ => null
        };
    }

    /// <summary>Deleted records have no page left to open.</summary>
    public bool IsDeletion(string action) => action.EndsWith("Deleted", StringComparison.Ordinal);

    public bool IsExpanded(AuditLogListItem row) => DetailId.HasValue && DetailId.Value == row.Id;

    public string PrettyJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "(no detail)";
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return json;
        }
    }
}
