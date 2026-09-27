// =====================================================================
// Index.cshtml.cs
// Location: MerkaiTrial.Admin.Web/Pages/Notifications/Index.cshtml.cs
//
// NEW FILE (037).
//
// ModuleName IS EMPTY, ON PURPOSE
//   Notifications are personal, not a module. AuthorizedPageModel supports
//   this — the same thing Dashboard does — and RequireModuleName makes it
//   explicit: calling a module-scoped permission helper on this page throws
//   rather than building a nonsense policy name. So this page never calls
//   ValidatePermissionAsync; being signed in IS the authorisation, and the
//   API scopes every query to the caller's own RecipientUserId.
//
//   Putting a module permission here would hide the bell from exactly the
//   read-only users who most need telling that something is waiting on them.
//
// THE ?open= ROUTE
//   The bell dropdown links here with ?open={id}. That marks the item read
//   and then redirects to whatever it points at. Doing it server-side keeps
//   the layout's JavaScript free of antiforgery handling, and means a
//   middle-click or a copied link behaves the same as a normal click.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Notifications;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.Admin.Web.Pages.Notifications
{
    public class IndexModel : AuthorizedPageModel
    {
        private const int PageSize = 25;

        private readonly INotificationService _notifications;

        public IndexModel(
            IAuthorizationService authorizationService,
            ICurrentUserService currentUserService,
            ILogger<IndexModel> logger,
            INotificationService notifications)
            : base(authorizationService, currentUserService, logger)
        {
            _notifications = notifications;
        }

        // Empty: this page owns no module. See the header.
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

        public List<NotificationDto> Items { get; private set; } = new();
        public int UnreadCount { get; private set; }
        public int TotalCount { get; private set; }
        public bool HasMore { get; private set; }

        [BindProperty(SupportsGet = true)]
        public bool UnreadOnly { get; set; }

        [BindProperty(SupportsGet = true)]
        public int Skip { get; set; }

        public int Take => PageSize;
        public bool HasPrevious => Skip > 0;
        public int PreviousSkip => Math.Max(0, Skip - PageSize);
        public int NextSkip => Skip + PageSize;

        /// <summary>Set when the list could not be loaded at all.</summary>
        public string? LoadError { get; private set; }

        // =============================================================
        // GET
        // =============================================================

        public async Task<IActionResult> OnGetAsync(Guid? open = null)
        {

            // ── the bell's "open this one" path ───────────────────────
            if (open.HasValue && open.Value != Guid.Empty)
            {
                // Mark read first, so the count drops even if the redirect
                // target turns out to be gone.
                try
                {
                    await _notifications.MarkReadAsync(open.Value);
                }
                catch (InvalidOperationException ex)
                {
                    // A stale click. Not worth showing the user anything.
                    Logger.LogInformation(ex,
                        "Could not mark notification {Id} read — probably stale", open.Value);
                }

                await LoadAsync();

                var target = Items.FirstOrDefault(i => i.Id == open.Value)?.Url;
                if (!string.IsNullOrEmpty(target))
                    return Redirect(target);

                // It points at nothing, or it is not on this page. Falling
                // through shows the list, which is a reasonable landing spot.
                return Page();
            }

            await LoadAsync();
            return Page();
        }

        /// <summary>
        /// The layout's poll. Returns the bell's count and newest few as JSON.
        /// Lives on this page rather than a controller so the whole feature is
        /// one folder, and so it is covered by the same cookie authentication
        /// as every other page.
        /// </summary>
        public async Task<IActionResult> OnGetSummaryAsync()
        {
            try
            {
                var summary = await _notifications.GetSummaryAsync();

                // Projected by hand rather than serialising the DTO, so the
                // computed Icon / IconClass / Url are definitely included —
                // they are get-only properties on a record, which some
                // serialiser settings skip.
                return new JsonResult(new
                {
                    unreadCount = summary.UnreadCount,
                    items = summary.Recent.Select(n => new
                    {
                        id = n.Id,
                        title = n.Title,
                        body = n.Body,
                        icon = n.Icon,
                        iconClass = n.IconClass,
                        isRead = n.IsRead,
                        actorName = n.ActorName,
                        when = FormatDateTime(n.CreatedAtUtc),
                        ago = Ago(n.CreatedAtUtc)
                    })
                });
            }
            catch (Exception ex)
            {
                // The poll must never produce a noisy error in the console on
                // every page of the app. Zero is a safe answer.
                Logger.LogWarning(ex, "Notification summary poll failed");
                return new JsonResult(new { unreadCount = 0, items = Array.Empty<object>() });
            }
        }

        // =============================================================
        // POST
        // =============================================================

        public async Task<IActionResult> OnPostMarkReadAsync(Guid id)
        {

            try
            {
                await _notifications.MarkReadAsync(id);
            }
            catch (InvalidOperationException ex)
            {
                TempData["Error"] = ex.Message;
            }

            return RedirectToSelf();
        }

        public async Task<IActionResult> OnPostMarkAllReadAsync()
        {

            try
            {
                await _notifications.MarkAllReadAsync();
                TempData["Success"] = "All notifications marked as read.";
            }
            catch (InvalidOperationException ex)
            {
                TempData["Error"] = ex.Message;
            }

            return RedirectToSelf();
        }

        public async Task<IActionResult> OnPostDismissAsync(Guid id)
        {

            try
            {
                await _notifications.DismissAsync(id);
            }
            catch (InvalidOperationException ex)
            {
                TempData["Error"] = ex.Message;
            }

            return RedirectToSelf();
        }

        public async Task<IActionResult> OnPostDismissReadAsync()
        {

            try
            {
                await _notifications.DismissReadAsync();
                TempData["Success"] = "Cleared everything you had already read.";
            }
            catch (InvalidOperationException ex)
            {
                TempData["Error"] = ex.Message;
            }

            // Back to the first page: the list just got shorter, so whatever
            // Skip was pointing at is probably past the end now.
            return RedirectToPage(new { unreadOnly = UnreadOnly, skip = 0 });
        }

        // =============================================================
        // helpers
        // =============================================================

        private async Task LoadAsync()
        {
            try
            {
                var page = await _notifications.GetPageAsync(UnreadOnly, Skip, PageSize);

                Items = page.Items ?? new List<NotificationDto>();
                UnreadCount = page.UnreadCount;
                TotalCount = page.TotalCount;
                HasMore = page.HasMore;
            }
            catch (Exception ex)
            {
                // An empty list with a message beats a 500. Nothing on this
                // page is load-bearing for the rest of the app.
                Logger.LogError(ex, "Could not load notifications");
                LoadError = "Your notifications could not be loaded just now. Please try again.";
                Items = new List<NotificationDto>();
            }
        }

        /// <summary>Keeps the tab and the page position across a POST.</summary>
        private IActionResult RedirectToSelf()
            => RedirectToPage(new { unreadOnly = UnreadOnly, skip = Skip });

        /// <summary>
        /// "4 minutes ago". Deliberately coarse — a notification list is
        /// scanned, not read, and an exact timestamp is on the tooltip.
        /// Uses the tenant's own local time via FormatDateTime for that.
        /// </summary>
        /// <summary>Instance wrapper, so the view can write @Model.Since(x).</summary>
        public string Since(DateTime utc) => Ago(utc);

        public static string Ago(DateTime utc)
        {
            var span = DateTime.UtcNow - utc;

            if (span.TotalSeconds < 60) return "just now";
            if (span.TotalMinutes < 60)
                return $"{(int)span.TotalMinutes} minute{Plural((int)span.TotalMinutes)} ago";
            if (span.TotalHours < 24)
                return $"{(int)span.TotalHours} hour{Plural((int)span.TotalHours)} ago";
            if (span.TotalDays < 7)
                return $"{(int)span.TotalDays} day{Plural((int)span.TotalDays)} ago";
            if (span.TotalDays < 35)
                return $"{(int)(span.TotalDays / 7)} week{Plural((int)(span.TotalDays / 7))} ago";

            return $"{(int)(span.TotalDays / 30)} month{Plural((int)(span.TotalDays / 30))} ago";
        }

        private static string Plural(int n) => n == 1 ? "" : "s";
    }
}
