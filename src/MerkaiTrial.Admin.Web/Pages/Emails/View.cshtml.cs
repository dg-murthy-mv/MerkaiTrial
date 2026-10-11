// =====================================================================
// View.cshtml.cs
// Location: MerkaiTrial.Admin.Web/Pages/Emails/View.cshtml.cs
//
// NEW FILE (083). One received email: who, what, the files, which records
// it is filed on — and, for people who sort the shared inbox, filing it.
//
// ModuleName IS EMPTY, ON PURPOSE (like Notifications). Whether this
// person may read THIS email depends on where it is filed — a rep may
// read the emails on their own deal without any inbox permission — and
// only the API knows that. So the page never calls
// ValidatePermissionAsync; the API answers "not found" for an email the
// person may not see, and this page turns that into a polite redirect.
// For the same reason there is no InitializePermissionsAsync (it would
// throw with no module); the API returns CanLink / CanDismiss / CanDelete
// with the email, and those decide the buttons.
//
// THE EMAIL'S HTML IS UNTRUSTED. It is shown in an <iframe sandbox> via
// srcdoc — no scripts, no forms, its own opaque origin, so it cannot read
// this page, the cookie or anything else — with a Content-Security-Policy
// that blocks every remote resource. Pictures from the internet stay off
// until the reader asks (they are how senders track opens). Links open in
// a new tab.
// =====================================================================

using System.Text;
using MerkaiTrial.Admin.Web.Services.InboundEmails;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.Admin.Web.Pages.Emails
{
    public class ViewModel : AuthorizedPageModel
    {
        private readonly IInboundEmailService _emails;

        public ViewModel(
            IAuthorizationService authorizationService,
            ICurrentUserService currentUserService,
            ILogger<ViewModel> logger,
            IInboundEmailService emails)
            : base(authorizationService, currentUserService, logger)
        {
            _emails = emails;
        }

        // See the header: no module, the API decides.
        protected override string ModuleName => string.Empty;

        public InboundEmailDetailDto Email { get; private set; } = null!;

        /// <summary>"html" or "text".</summary>
        [BindProperty(SupportsGet = true)] public string? Tab { get; set; }

        /// <summary>Remote pictures allowed in the formatted view.</summary>
        [BindProperty(SupportsGet = true)] public bool Images { get; set; }

        /// <summary>The "Link to a record" search box.</summary>
        [BindProperty(SupportsGet = true)] public string? Q { get; set; }

        public List<InboundLinkTargetDto> Targets { get; private set; } = new();
        public bool TargetsLoadFailed { get; private set; }

        /// <summary>Set when the search ran, so "no results" is only said after one.</summary>
        public bool Searched => !string.IsNullOrWhiteSpace(Q);

        public bool HasHtml => !string.IsNullOrWhiteSpace(Email?.BodyHtml);
        public bool ShowHtml => HasHtml && !string.Equals(Tab, "text", StringComparison.OrdinalIgnoreCase);

        /// <summary>"Create a lead from this email" — only from an email nobody has filed.</summary>
        public bool CanCreateLead =>
            Email is not null && Email.CanLink && Email.Status == "Unmatched" && UserCanCreate(Modules.Leads);

        // =============================================================
        // GET
        // =============================================================

        public async Task<IActionResult> OnGetAsync(Guid id)
        {
            try
            {
                Email = await _emails.GetAsync(id);
            }
            catch (KeyNotFoundException)
            {
                TempData["ErrorMessage"] = "That email could not be found, or you do not have access to it.";
                return RedirectToFallback();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to load email {Id}", id);
                TempData["ErrorMessage"] = "That email could not be loaded just now. Please try again.";
                return RedirectToFallback();
            }

            if (Email.CanLink)
            {
                try
                {
                    Targets = await _emails.SearchTargetsAsync(id, Q);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Failed to search link targets for email {Id}", id);
                    TargetsLoadFailed = true;
                }
            }

            return Page();
        }

        // =============================================================
        // POST — the API checks every one of these again
        // =============================================================

        public Task<IActionResult> OnPostLinkAsync(Guid id, string entityType, Guid entityId)
            => Act(id, () => _emails.LinkAsync(id, entityType, entityId),
                   $"Email filed on the {Describe(entityType)}.", "That email could not be filed.");

        public Task<IActionResult> OnPostUnlinkAsync(Guid id)
            => Act(id, () => _emails.UnlinkAsync(id),
                   "Email taken off its records. It is back in the Inbox.", "That email could not be unfiled.");

        public Task<IActionResult> OnPostDismissAsync(Guid id)
            => Act(id, () => _emails.DismissAsync(id),
                   "Email dismissed. It stays under Inbox → Dismissed if you need it again.", "That email could not be dismissed.");

        public Task<IActionResult> OnPostRestoreAsync(Guid id)
            => Act(id, () => _emails.RestoreAsync(id),
                   "Email restored.", "That email could not be restored.");

        public async Task<IActionResult> OnPostDeleteAsync(Guid id)
        {
            try
            {
                await _emails.DeleteAsync(id);
                TempData["SuccessMessage"] = "Email deleted.";
                return RedirectToPage("/Inbox/Index");
            }
            catch (InvalidOperationException ex)
            {
                TempData["ErrorMessage"] = ex.Message;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to delete email {Id}", id);
                TempData["ErrorMessage"] = "That email could not be deleted. Please try again.";
            }

            return RedirectToPage(new { id });
        }

        private async Task<IActionResult> Act(Guid id, Func<Task> action, string success, string failure)
        {
            try
            {
                await action();
                TempData["SuccessMessage"] = success;
            }
            catch (InvalidOperationException ex)
            {
                // The API's own sentence ("That record could not be found…").
                TempData["ErrorMessage"] = ex.Message;
            }
            catch (KeyNotFoundException ex)
            {
                TempData["ErrorMessage"] = ex.Message;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Email action failed for {Id}", id);
                TempData["ErrorMessage"] = failure + " Please try again.";
            }

            return RedirectToPage(new { id });
        }

        /// <summary>Back to the Inbox if they could use it, else the dashboard.</summary>
        private IActionResult RedirectToFallback()
            => UserCanRead(Modules.Inbox) ? RedirectToPage("/Inbox/Index") : RedirectToPage("/Dashboard/Index");

        // =============================================================
        // view helpers
        // =============================================================

        public static string Describe(string? entityType) => entityType?.ToLowerInvariant() switch
        {
            "lead"    => "lead",
            "deal"    => "deal",
            "contact" => "contact",
            _         => "record"
        };

        public static string LinkIcon(string entityType) => entityType switch
        {
            "Deal"    => "bi-kanban",
            "Lead"    => "bi-funnel",
            "Contact" => "bi-person",
            "Company" => "bi-building",
            _         => "bi-link-45deg"
        };

        public static string LinkPage(string entityType) => entityType switch
        {
            "Deal"    => "/Pipeline/Detail",
            "Lead"    => "/Leads/Detail",
            "Contact" => "/Contacts/Detail",
            "Company" => "/Companies/Detail",
            _         => "/Dashboard/Index"
        };

        public string HowFiled => Email.MatchMethod switch
        {
            "ReplyToken" => "Filed automatically — it is a reply to an email sent from Merkai.",
            "Thread"     => "Filed automatically — it answers an email already on this record.",
            "Address"    => "Filed automatically — the customer's address matches this record.",
            "Manual"     => Email.LinkedBy is null ? "Filed by hand." : $"Filed by {Email.LinkedBy}.",
            _            => string.Empty
        };

        public static string FileSize(long bytes) =>
            bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:0.#} MB"
          : bytes >= 1024        ? $"{bytes / 1024d:0} KB"
          :                        $"{bytes} B";

        public static string FileIcon(string? mime) => (mime ?? string.Empty) switch
        {
            var m when m.Contains("pdf")                              => "bi-file-earmark-pdf",
            var m when m.StartsWith("image/")                         => "bi-file-earmark-image",
            var m when m.Contains("sheet") || m.Contains("excel") || m.Contains("csv") => "bi-file-earmark-spreadsheet",
            var m when m.Contains("word")                             => "bi-file-earmark-word",
            _                                                         => "bi-file-earmark"
        };

        /// <summary>
        /// The document the sandboxed frame shows. The CSP blocks every
        /// remote resource (and pictures unless Images is on); data: images
        /// are allowed, which is where Resend puts inline pictures.
        /// </summary>
        public string SrcDoc
        {
            get
            {
                var img = Images ? "data: https: http:" : "data:";
                var sb = new StringBuilder();
                sb.Append("<!doctype html><html><head><meta charset=\"utf-8\">");
                sb.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; ");
                sb.Append("img-src ").Append(img).Append("; style-src 'unsafe-inline'; font-src data:\">");
                sb.Append("<base target=\"_blank\">");
                sb.Append("<style>body{font-family:Inter,system-ui,-apple-system,'Segoe UI',Arial,sans-serif;");
                sb.Append("font-size:14px;line-height:1.5;color:#1e293b;margin:14px;word-wrap:break-word;}");
                sb.Append("img{max-width:100%;height:auto;}table{max-width:100%;}");
                sb.Append("blockquote{border-left:3px solid #e2e8f0;margin:8px 0;padding-left:10px;color:#64748b;}</style>");
                sb.Append("</head><body>");
                sb.Append(Email?.BodyHtml ?? string.Empty);
                sb.Append("</body></html>");
                return sb.ToString();
            }
        }

        /// <summary>True when the HTML refers to remote pictures that are being held back.</summary>
        public bool HasBlockedImages =>
            HasHtml && !Images &&
            (Email.BodyHtml!.Contains("src=\"http", StringComparison.OrdinalIgnoreCase)
             || Email.BodyHtml!.Contains("src='http", StringComparison.OrdinalIgnoreCase)
             || Email.BodyHtml!.Contains("url(http", StringComparison.OrdinalIgnoreCase));
    }
}
