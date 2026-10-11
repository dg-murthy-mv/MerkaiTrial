// =====================================================================
// Index.cshtml.cs
// Location: MerkaiTrial.Admin.Web/Pages/Inbox/Index.cshtml.cs
//
// NEW FILE (083). The shared inbox: received email that matched no lead,
// deal or contact, waiting for someone to file it.
//
// MODULE: inbox (new). Read to see the list, Update to dismiss / restore
// from here. Filing happens on the email's own page, where the person can
// read it first — filing a customer's email on a deal without reading it
// is how emails end up on the wrong deal.
//
// Nobody holds inbox.* until a workspace admin grants it under Roles &
// Permissions ("Shared Inbox"); admins bypass. The API enforces the same.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.InboundEmails;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.Admin.Web.Pages.Inbox
{
    public class IndexModel : AuthorizedPageModel
    {
        private const int PageSize = 25;

        private readonly IInboundEmailService _emails;

        public IndexModel(
            IAuthorizationService authorizationService,
            ICurrentUserService currentUserService,
            ILogger<IndexModel> logger,
            IInboundEmailService emails)
            : base(authorizationService, currentUserService, logger)
        {
            _emails = emails;
        }

        protected override string ModuleName => Modules.Inbox;

        /// <summary>"unmatched" (default), "dismissed", "problems", "all".</summary>
        [BindProperty(SupportsGet = true)] public string? Status { get; set; }
        [BindProperty(SupportsGet = true)] public string? Search { get; set; }
        [BindProperty(SupportsGet = true)] public int Skip { get; set; }

        public InboundEmailPageDto Data { get; private set; } = new(new(), 0, 0, 0, false);
        public string? LoadError { get; private set; }

        public string CurrentStatus => (Status ?? "unmatched").ToLowerInvariant() switch
        {
            "dismissed" => "dismissed",
            "problems"  => "problems",
            "all"       => "all",
            _           => "unmatched"
        };

        public bool HasPrevious => Skip > 0;
        public int PreviousSkip => Math.Max(0, Skip - PageSize);
        public int NextSkip => Skip + PageSize;

        public async Task<IActionResult> OnGetAsync()
        {
            var denied = await ValidatePermissionAsync(Actions.Read);
            if (denied is not null) return denied;

            await InitializePermissionsAsync();

            try
            {
                Data = await _emails.GetInboxAsync(CurrentStatus, Search, Math.Max(0, Skip), PageSize);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Could not load the inbox");
                LoadError = "The inbox could not be loaded just now. Please try again.";
            }

            return Page();
        }

        /// <summary>
        /// The menu badge, fetched once per page by the layout. JSON, never
        /// an error page: no permission or a failure is simply 0, and the
        /// badge stays hidden.
        /// </summary>
        public async Task<IActionResult> OnGetCountAsync()
        {
            if (!UserCanRead(Modules.Inbox)) return new JsonResult(new { unmatched = 0 });
            return new JsonResult(new { unmatched = await _emails.GetUnmatchedCountAsync() });
        }

        public Task<IActionResult> OnPostDismissAsync(Guid id)
            => Act(() => _emails.DismissAsync(id), "Email dismissed.");

        public Task<IActionResult> OnPostRestoreAsync(Guid id)
            => Act(() => _emails.RestoreAsync(id), "Email restored.");

        private async Task<IActionResult> Act(Func<Task> action, string success)
        {
            var denied = await ValidatePermissionAsync(Actions.Update);
            if (denied is not null) return denied;

            try
            {
                await action();
                TempData["SuccessMessage"] = success;
            }
            catch (InvalidOperationException ex)
            {
                TempData["ErrorMessage"] = ex.Message;
            }
            catch (KeyNotFoundException ex)
            {
                TempData["ErrorMessage"] = ex.Message;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Inbox action failed");
                TempData["ErrorMessage"] = "That could not be done just now. Please try again.";
            }

            return RedirectToPage(new { status = CurrentStatus, search = Search, skip = Skip });
        }
    }
}
