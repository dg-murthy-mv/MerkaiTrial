// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Quotes/Approvals.cshtml.cs
//
// NEW FILE (017). "Waiting for my approval" — every quote approval request
// the signed-in user can decide:
//   • workspace admins: all of them (except their own)
//   • managers: requests on deals owned by people in the teams they manage
//
// Approve works straight from the list. Request changes needs a comment,
// so it opens the quote (where the reason box is) — the approver should
// look at the lines before sending it back anyway.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Quotes;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.Admin.Web.Pages.Quotes
{
    public class ApprovalsModel : AuthorizedPageModel
    {
        private readonly IQuoteApprovalService _approvals;
        private readonly ILogger<ApprovalsModel> _logger;

        protected override string ModuleName => Modules.Quotes;

        public ApprovalsModel(
            IQuoteApprovalService approvals,
            ICurrentUserService currentUserService,
            IAuthorizationService authorizationService,
            ILogger<ApprovalsModel> logger)
            : base(authorizationService, currentUserService, logger)
        {
            _approvals = approvals;
            _logger = logger;
        }

        public List<PendingQuoteApprovalDto> Pending { get; private set; } = new();
        public bool LoadFailed { get; private set; }

        [TempData] public string? ErrorMessage { get; set; }
        [TempData] public string? SuccessMessage { get; set; }

        public async Task<IActionResult> OnGetAsync()
        {
            var check = await ValidatePermissionAsync(Actions.Read);
            if (check != null) return check;

            await InitializePermissionsAsync();

            try
            {
                Pending = await _approvals.GetPendingAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load pending quote approvals");
                LoadFailed = true;
            }

            return Page();
        }

        public async Task<IActionResult> OnPostApproveAsync(Guid quoteId, string? number)
        {
            var check = await ValidatePermissionAsync(Actions.Read);
            if (check != null) return check;

            try
            {
                await _approvals.ApproveAsync(quoteId, null);
                SuccessMessage = $"{number ?? "Quote"} approved. The rep can send it now.";
            }
            catch (InvalidOperationException ex)
            {
                ErrorMessage = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to approve quote {QuoteId}", quoteId);
                ErrorMessage = "Couldn't approve that quote. Please try again.";
            }

            return RedirectToPage();
        }

        /// <summary>"3 hours ago" style — requests are usually hours old, not days.</summary>
        public static string Ago(DateTime utc)
        {
            var span = DateTime.UtcNow - utc;
            if (span.TotalMinutes < 1) return "just now";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
            if (span.TotalHours < 24) return $"{(int)span.TotalHours} h ago";
            var days = (int)span.TotalDays;
            return days == 1 ? "yesterday" : $"{days} days ago";
        }
    }
}
