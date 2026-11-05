// =====================================================================
// FILE: Pages/Admin/AuditLog/Index.cshtml.cs
//
// NEW (119). Admin → Audit Log: everything MadeeVision's own people did —
// console changes (done / refused / failed, with what was sent), support
// in customers' workspaces, View As, and their sign-ins. Filters: how far
// back, who, which workspace, what kind, and a search. Read only: audit
// rows are add-only (106), nobody can edit or remove them here or anywhere.
//
// The rows come from PlatformAuditLogHandler; this page only describes
// them in words.
// =====================================================================

using System.Text.Json;
using MerkaiTrial.Admin.Web.Pages.Shared;
using MerkaiTrial.Application.Commands.Platform;
using MerkaiTrial.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MerkaiTrial.Admin.Web.Pages.Admin.AuditLog
{
    public class IndexModel : PageModel
    {
        private readonly PlatformAuditLogHandler _log;
        private readonly ILogger<IndexModel> _logger;

        public IndexModel(PlatformAuditLogHandler log, ILogger<IndexModel> logger)
        {
            _log = log; _logger = logger;
        }

        [BindProperty(SupportsGet = true)] public int Days { get; set; } = 30;
        [BindProperty(SupportsGet = true)] public Guid? Who { get; set; }
        [BindProperty(SupportsGet = true)] public Guid? Workspace { get; set; }
        [BindProperty(SupportsGet = true)] public string? Kind { get; set; }
        [BindProperty(SupportsGet = true)] public string? Search { get; set; }
        [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
        [BindProperty(SupportsGet = true)] public int PageSize { get; set; } = 25;

        public PlatformAuditResult Data { get; private set; } = new();
        public string? LoadError { get; private set; }

        public static readonly (string Value, string Label)[] Kinds =
        {
            ("all", "Everything"),
            ("changes", "Console changes"),
            ("problems", "Refused or failed changes"),
            ("support", "Support for people"),
            ("viewas", "View As"),
            ("signin", "Sign-ins"),
        };

        public async Task OnGetAsync()
        {
            if (!PlatformAuditLogHandler.DayChoices.Contains(Days)) Days = 30;
            if (!PagerVm.Sizes.Contains(PageSize)) PageSize = 25;
            if (PageNumber < 1) PageNumber = 1;
            if (Search?.Length > 100) Search = Search[..100];

            try
            {
                Data = await _log.HandleAsync(new PlatformAuditQuery
                {
                    Days = Days, Who = Who, Workspace = Workspace, Kind = Kind,
                    Search = Search, Page = PageNumber, PageSize = PageSize,
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Loading the platform audit log failed");
                LoadError = "The audit log could not be loaded. Please try again, or narrow the time range.";
            }
        }

        public PagerVm Pager => new()
        {
            Page = PageNumber,
            PageSize = PageSize,
            TotalCount = Data.TotalCount,
            TotalPages = Data.TotalCount == 0 ? 1 : (int)Math.Ceiling(Data.TotalCount / (double)PageSize),
            PageField = nameof(PageNumber),
            Route = FilterRoute(),
        };

        private Dictionary<string, string> FilterRoute()
        {
            var r = new Dictionary<string, string> { [nameof(Days)] = Days.ToString() };
            if (Who is { } w) r[nameof(Who)] = w.ToString();
            if (Workspace is { } ws) r[nameof(Workspace)] = ws.ToString();
            if (!string.IsNullOrWhiteSpace(Kind)) r[nameof(Kind)] = Kind!;
            if (!string.IsNullOrWhiteSpace(Search)) r[nameof(Search)] = Search!;
            return r;
        }

        public bool Filtered => Who is not null || Workspace is not null || !string.IsNullOrWhiteSpace(Search)
                                || (!string.IsNullOrWhiteSpace(Kind) && Kind != "all");

        // ── describing a row ──────────────────────────────────────────

        public static Dictionary<string, string> Fields(string? json)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(json)) return d;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return d;
                foreach (var p in doc.RootElement.EnumerateObject())
                    d[p.Name] = p.Value.ValueKind switch
                    {
                        JsonValueKind.String => p.Value.GetString() ?? "",
                        JsonValueKind.Null => "",
                        _ => p.Value.ToString(),
                    };
            }
            catch (JsonException) { }
            return d;
        }

        /// <summary>The values a console form sent, as "name: value" lines.</summary>
        public static List<string> Sent(string? json)
        {
            var lines = new List<string>();
            if (string.IsNullOrWhiteSpace(json)) return lines;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("sent", out var sent) && sent.ValueKind == JsonValueKind.Object)
                    foreach (var p in sent.EnumerateObject())
                        lines.Add($"{(p.Name.StartsWith("Input.") ? p.Name[6..] : p.Name)}: {p.Value}");
            }
            catch (JsonException) { }
            return lines;
        }

        private static readonly Dictionary<string, string> AreaLabels = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Workspace"] = "Workspace", ["Plan"] = "Plan", ["Country"] = "Country", ["TaxRate"] = "Tax rate",
            ["Vertical"] = "Vertical", ["StartingRole"] = "Starting role", ["WhatsAppTemplate"] = "WhatsApp templates",
        };

        private static readonly Dictionary<string, string> HandlerLabels = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Save"] = "saved", ["Delete"] = "delete", ["DeleteRate"] = "delete a tax rate",
            ["ToggleStatus"] = "switch on / off", ["ChangePlan"] = "change plan", ["Deactivate"] = "deactivate",
            ["AutomationLimit"] = "automation limit", ["Test"] = "send a test message",
        };

        /// <summary>One line: what happened.</summary>
        public static string Describe(PlatformAuditRow row)
        {
            var d = Fields(row.Data);
            string S(string k) => d.TryGetValue(k, out var v) ? v : "";

            switch (row.Action)
            {
                case AuditAction.PlatformChange:
                {
                    var area = AreaLabels.TryGetValue(S("area"), out var a) ? a : S("area");
                    var handler = HandlerLabels.TryGetValue(S("handler"), out var h) ? h : S("handler");
                    var page = S("page");
                    var verb = handler == "saved"
                        ? (page.EndsWith("/Create", StringComparison.OrdinalIgnoreCase) ? "added"
                           : page.EndsWith("/Settings", StringComparison.OrdinalIgnoreCase) ? "limits saved" : "saved")
                        : handler;
                    var msg = S("message");
                    return $"{area} — {verb}" + (msg.Length > 0 ? $": {msg}" : "");
                }
                case AuditAction.ViewAsStart: return "Started viewing as " + (S("viewing") is { Length: > 0 } e ? e : "someone");
                case AuditAction.ViewAsEnd: return "Stopped viewing as" + (S("viewing") is { Length: > 0 } v ? $" {v}" : "");
                case AuditAction.SignIn: return "Signed in";
                case AuditAction.SignInFailed: return "Sign-in failed";
                case AuditAction.AccountUnlocked: return $"Unlocked {S("person")}";
                case AuditAction.InviteIssued: return $"Invite {S("summary")}".Trim();
                case AuditAction.ResetIssued: return $"Password reset {S("summary")}".Trim();
                case AuditAction.PermissionChanged:
                case AuditAction.StaffAdded:
                case AuditAction.StaffSwitchedOff:
                case AuditAction.StaffSwitchedOn:
                    return S("summary") is { Length: > 0 } s ? s : Humanise(row.Action);
                default:
                    return Humanise(row.Action) + (S("name") is { Length: > 0 } n ? $" — {n}" : "")
                           + (row.EntityType.Length > 0 ? $" ({row.EntityType})" : "");
            }
        }

        /// <summary>"done", "refused", "failed" for console changes; null for everything else.</summary>
        public static string? OutcomeOf(PlatformAuditRow row) =>
            row.Action == AuditAction.PlatformChange && Fields(row.Data).TryGetValue("outcome", out var o) ? o : null;

        private static string Humanise(string action)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var ch in action)
            {
                if (char.IsUpper(ch) && sb.Length > 0) sb.Append(' ').Append(char.ToLowerInvariant(ch));
                else sb.Append(ch);
            }
            return sb.ToString();
        }
    }
}
