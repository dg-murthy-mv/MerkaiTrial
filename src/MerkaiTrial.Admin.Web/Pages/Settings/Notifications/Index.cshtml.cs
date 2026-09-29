// =====================================================================
// Index.cshtml.cs
// Location: MerkaiTrial.Admin.Web/Pages/Settings/Notifications/Index.cshtml.cs
//
// NEW FILE (038). Settings → Notifications, two tabs in one page.
//
//   My preferences      everyone signed in
//   Workspace defaults  workspace admins only  (040)
//   Email log           workspace admins only
//
// ModuleName IS EMPTY, like the notifications page in 037. Preferences are
// personal, so there is no module to check — being signed in is the
// authorisation, and the API scopes every query to the caller.
//
// The email log tab is shown only to workspace admins, and the API refuses
// it independently. The page hiding the tab is a courtesy, not the
// control: a non-admin who guesses the URL gets a 403 turned into a plain
// message rather than a blank screen.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Notifications;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Notifications;   // 044: PhoneNumbers
using MerkaiTrial.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.Admin.Web.Pages.Settings.Notifications
{
    public class IndexModel : AuthorizedPageModel
    {
        private const int PageSize = 25;

        private readonly INotificationSettingsService _settings;

        public IndexModel(
            IAuthorizationService authorizationService,
            ICurrentUserService currentUserService,
            ILogger<IndexModel> logger,
            INotificationSettingsService settings)
            : base(authorizationService, currentUserService, logger)
        {
            _settings = settings;
        }

        protected override string ModuleName => string.Empty;

        // ── 038b: NO InitializePermissionsAsync() ─────────────────────
        //
        // It sets CanCreate/CanRead/CanUpdate/CanDelete, and EVERY ONE of
        // those calls RequireModuleName — which throws on this page, because
        // ModuleName is deliberately empty:
        //
        //   InvalidOperationException: IndexModel has no ModuleName, so
        //   CanCreateAsync cannot build a policy name.
        //
        // That guard is right and this page is right; I simply called a
        // method that the two together forbid. A page with no module has no
        // module permissions to initialise, so there is nothing to call. The
        // view does not use those four flags, and this page's authorisation
        // is "you are signed in" plus the API scoping every query to the
        // caller.
        //
        // If this page ever needs to check a permission, it uses
        // UserCan(module, action), which takes the module as an argument and
        // does not go near ModuleName.

        // ── Tabs ──────────────────────────────────────────────────────
        [BindProperty(SupportsGet = true)]
        public string Tab { get; set; } = "preferences";

        public bool OnLogTab => string.Equals(Tab, "log", StringComparison.OrdinalIgnoreCase);

        public bool OnDefaultsTab =>
            string.Equals(Tab, "defaults", StringComparison.OrdinalIgnoreCase);

        public bool OnPreferencesTab => !OnLogTab && !OnDefaultsTab;

        /// <summary>Set from the signed-in user, for whether to show the log tab.</summary>
        public bool IsWorkspaceAdmin { get; private set; }

        // ── Preferences ───────────────────────────────────────────────
        public NotificationPreferencesDto? Preferences { get; private set; }

        /// <summary>
        /// Posted back as three parallel arrays rather than a bound list of
        /// objects. An unchecked checkbox posts NOTHING, so a bound
        /// List&lt;T&gt; silently loses its indexes the moment a user
        /// unticks a box in the middle — the classic Razor Pages trap. A
        /// hidden field per row carries the event type, and the two checkbox
        /// arrays are matched against it by value, not by position.
        /// </summary>
        [BindProperty]
        public List<string> EventTypes { get; set; } = new();

        [BindProperty]
        public List<string> InAppOn { get; set; } = new();

        [BindProperty]
        public List<string> EmailOn { get; set; } = new();

        /// <summary>044. The third channel, same by-value matching.</summary>
        [BindProperty]
        public List<string> WhatsAppOn { get; set; } = new();

        /// <summary>
        /// 044. The mobile number, exactly as typed. Normalised on the
        /// server — the browser is not trusted with a value that decides
        /// whose phone a message reaches.
        /// </summary>
        [BindProperty]
        public string? MobileNumber { get; set; }

        /// <summary>044. "Yes, message me on WhatsApp."</summary>
        [BindProperty]
        public bool WhatsAppOptIn { get; set; }

        // ── Email log ─────────────────────────────────────────────────
        // ── Workspace defaults (040) ──────────────────────────────────
        public TenantNotificationDefaultsDto? Defaults { get; private set; }

        /// <summary>
        /// Posted as parallel arrays, matched BY VALUE — the same reason as
        /// the preferences tab. An unchecked checkbox posts nothing, so a
        /// bound list of objects loses its indexes the moment someone
        /// unticks a box in the middle.
        /// </summary>
        [BindProperty] public List<string> DefaultEventTypes { get; set; } = new();
        [BindProperty] public List<string> DefaultInAppOn { get; set; } = new();
        [BindProperty] public List<string> DefaultEmailOn { get; set; } = new();
        [BindProperty] public List<string> DefaultLockedOn { get; set; } = new();
        [BindProperty] public List<string> DefaultWhatsAppOn { get; set; } = new();   // 044

        public IEnumerable<IGrouping<string, TenantNotificationDefaultDto>> DefaultGroups
            => (Defaults?.Items ?? new List<TenantNotificationDefaultDto>())
               .GroupBy(i => i.Group);

        // ── Email log ─────────────────────────────────────────────────
        public OutboundMessagePageDto? Log { get; private set; }

        [BindProperty(SupportsGet = true)]
        public string? Status { get; set; }

        [BindProperty(SupportsGet = true)]
        public int Skip { get; set; }

        public bool HasPrevious => Skip > 0;
        public int PreviousSkip => Math.Max(0, Skip - PageSize);
        public int NextSkip => Skip + PageSize;

        public string? LoadError { get; private set; }

        /// <summary>The statuses the log's filter offers.</summary>
        public static readonly string[] StatusFilters =
            { "Pending", "Sending", "Sent", "Failed", "Dead", "Cancelled" };

        // =============================================================
        // GET
        // =============================================================

        public async Task<IActionResult> OnGetAsync()
        {

            var me = await CurrentUserService.GetCurrentUserAsync();
            IsWorkspaceAdmin = me.IsTenantAdmin;

            // A non-admin landing on an admin tab is sent back rather than
            // shown an error: they did not do anything wrong, the tab just
            // is not theirs. The API refuses it independently.
            if ((OnLogTab || OnDefaultsTab) && !IsWorkspaceAdmin)
                return RedirectToPage(new { tab = "preferences" });

            if (OnLogTab)           await LoadLogAsync();
            else if (OnDefaultsTab) await LoadDefaultsAsync();
            else                    await LoadPreferencesAsync();

            return Page();
        }

        // =============================================================
        // POST
        // =============================================================

        public async Task<IActionResult> OnPostSavePreferencesAsync()
        {

            var items = EventTypes
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Distinct()
                .Select(eventType => new SaveNotificationPreferenceDto(
                    eventType,
                    // Matched by VALUE — see the note on the arrays above.
                    InAppOn.Contains(eventType, StringComparer.Ordinal),
                    EmailOn.Contains(eventType, StringComparer.Ordinal),
                    WhatsAppOn.Contains(eventType, StringComparer.Ordinal)))   // 044
                .ToList();

            if (items.Count == 0)
            {
                TempData["Error"] = "Nothing was submitted — please try again.";
                return RedirectToPage(new { tab = "preferences" });
            }

            try
            {
                // 044: the grid and the WhatsApp contact details go together,
                // because on screen they are one form with one Save button.
                await _settings.SavePreferencesAsync(new SaveNotificationPreferencesDto(
                    items, MobileNumber, WhatsAppOptIn));

                TempData["Success"] = "Your notification preferences have been saved.";
            }
            catch (InvalidOperationException ex)
            {
                TempData["Error"] = ex.Message;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Could not save notification preferences");
                TempData["Error"] = "Your preferences could not be saved just now.";
            }

            return RedirectToPage(new { tab = "preferences" });
        }

        public async Task<IActionResult> OnPostSaveDefaultsAsync()
        {
            var me = await CurrentUserService.GetCurrentUserAsync();
            if (!me.IsTenantAdmin)
                return RedirectToPage(new { tab = "preferences" });

            var items = DefaultEventTypes
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Distinct()
                .Select(eventType => new SaveTenantNotificationDefaultDto(
                    eventType,
                    DefaultInAppOn.Contains(eventType, StringComparer.Ordinal),
                    DefaultEmailOn.Contains(eventType, StringComparer.Ordinal),
                    DefaultLockedOn.Contains(eventType, StringComparer.Ordinal),
                    DefaultWhatsAppOn.Contains(eventType, StringComparer.Ordinal)))   // 044
                .ToList();

            if (items.Count == 0)
            {
                TempData["Error"] = "Nothing was submitted — please try again.";
                return RedirectToPage(new { tab = "defaults" });
            }

            try
            {
                await _settings.SaveDefaultsAsync(new SaveTenantNotificationDefaultsDto(items));

                var locked = items.Count(i => i.IsLocked);
                TempData["Success"] = locked > 0
                    ? $"Workspace defaults saved. {locked} event{(locked == 1 ? " is" : "s are")} locked — people cannot turn those off."
                    : "Workspace defaults saved.";
            }
            catch (InvalidOperationException ex)
            {
                TempData["Error"] = ex.Message;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Could not save the workspace notification defaults");
                TempData["Error"] = "The workspace defaults could not be saved just now.";
            }

            return RedirectToPage(new { tab = "defaults" });
        }

        public async Task<IActionResult> OnPostRetryAsync(Guid id)
        {

            try
            {
                await _settings.RetryAsync(id);
                TempData["Success"] = "Queued for another attempt.";
            }
            catch (InvalidOperationException ex)
            {
                TempData["Error"] = ex.Message;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Could not retry message {Id}", id);
                TempData["Error"] = "That message could not be retried just now.";
            }

            return RedirectToPage(new { tab = "log", status = Status, skip = Skip });
        }

        public async Task<IActionResult> OnPostCancelAsync(Guid id)
        {

            try
            {
                await _settings.CancelAsync(id);
                TempData["Success"] = "That message will not be sent.";
            }
            catch (InvalidOperationException ex)
            {
                TempData["Error"] = ex.Message;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Could not cancel message {Id}", id);
                TempData["Error"] = "That message could not be cancelled just now.";
            }

            return RedirectToPage(new { tab = "log", status = Status, skip = Skip });
        }

        // =============================================================
        // helpers
        // =============================================================

        private async Task LoadPreferencesAsync()
        {
            try
            {
                Preferences = await _settings.GetPreferencesAsync();

                // 044. Pre-fill the contact boxes from what is stored, so
                // the form shows the person's actual state rather than an
                // empty box beside a ticked opt-in.
                MobileNumber  = Preferences?.MyMobileE164;
                WhatsAppOptIn = Preferences?.WhatsAppOptedIn ?? false;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Could not load notification preferences");
                LoadError = "Your preferences could not be loaded just now. Please try again.";
            }
        }

        private async Task LoadDefaultsAsync()
        {
            try
            {
                Defaults = await _settings.GetDefaultsAsync();
            }
            catch (InvalidOperationException ex)
            {
                LoadError = ex.Message;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Could not load the workspace notification defaults");
                LoadError = "The workspace defaults could not be loaded just now. Please try again.";
            }
        }

        private async Task LoadLogAsync()
        {
            try
            {
                Log = await _settings.GetOutboundAsync(Status, Skip, PageSize);
            }
            catch (InvalidOperationException ex)
            {
                // The API's own refusal, worded for a person.
                LoadError = ex.Message;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Could not load the email log");
                LoadError = "The email log could not be loaded just now. Please try again.";
            }
        }

        /// <summary>Group headings, in the order the defaults declare.</summary>
        public IEnumerable<IGrouping<string, NotificationPreferenceDto>> PreferenceGroups
            => (Preferences?.Items ?? new List<NotificationPreferenceDto>())
               .GroupBy(i => i.Group);

        /// <summary>"4 minutes ago", shared with 037's notifications page.</summary>
        /// <summary>
        /// 044. The stored number, spaced for reading: "+91 98765 43210".
        /// Display only — the box itself holds the exact stored value, so
        /// saving an untouched form cannot reformat it into something else.
        /// </summary>
        public string PrettyMobile(string? e164) => PhoneNumbers.Pretty(e164);

        /// <summary>
        /// 044. Why the WhatsApp column is greyed out, in one sentence, or
        /// null when it is usable. Three different reasons, and each has a
        /// different fix — "it isn't working" would leave the person
        /// guessing which.
        /// </summary>
        public string? WhatsAppBlockedReason()
        {
            if (Preferences is null) return null;

            if (!Preferences.WhatsAppEnabled)
                return "WhatsApp isn't set up on this environment yet, so these can't be switched on.";

            if (string.IsNullOrWhiteSpace(Preferences.MyMobileE164))
                return "Add your mobile number below and we'll be able to message you.";

            if (!Preferences.WhatsAppOptedIn)
                return "Tick the box below to agree to WhatsApp messages, then choose your events.";

            return null;
        }

        public string Since(DateTime utc)
            => MerkaiTrial.Admin.Web.Pages.Notifications.IndexModel.Ago(utc);
    }
}
