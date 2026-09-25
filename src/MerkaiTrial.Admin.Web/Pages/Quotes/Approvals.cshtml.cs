// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Quotes/Approvals.cshtml.cs
//
// COMPLETE FILE — replaces the 017 version.
//
// "Waiting for me" — every approval STEP the signed-in user can decide:
//   • the step names the deal owner's team managers, and they manage that
//     team (the 017 rule)
//   • the step names a role, and they hold it
//   • the step names them personally
//   • the step says "any workspace admin", and they are one
//   • or they are a workspace admin standing in for whoever it names —
//     the escape hatch, flagged as such on the card
//
// WHAT CHANGED (027)
//   The old page said "approved. The rep can send it now." after every
//   approval. On a two-level chain that was simply wrong: the quote was
//   still sitting with the next approver. The page now reads the chain, so
//   a card says "step 1 of 2 — Senior manager sign-off", the confirmation
//   says where it went next, and the decisions already taken are listed.
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

        public List<PendingApprovalChainDto> Pending { get; private set; } = new();
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
                Pending = await _approvals.GetPendingChainAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load pending quote approvals");
                LoadFailed = true;
            }

            return Page();
        }

        /// <summary>
        /// Approve straight from the list.
        ///
        /// Nothing here is authorised on what the page posted: the API
        /// re-reads the request, works out the current step itself, and
        /// refuses if this user isn't one of its approvers.
        ///
        /// The CONFIRMATION is read back from the quote rather than guessed
        /// from the card. A card can be stale — two approvers on a two-step
        /// chain, the first approves, the second's page still says "step 1
        /// of 2" — and guessing from it would tell somebody who had just
        /// finished a chain that the quote still wasn't ready to send.
        /// </summary>
        public async Task<IActionResult> OnPostApproveAsync(Guid quoteId, string? number)
        {
            var check = await ValidatePermissionAsync(Actions.Read);
            if (check != null) return check;

            var what = string.IsNullOrWhiteSpace(number) ? "Quote" : number!;

            try
            {
                await _approvals.ApproveAsync(quoteId, null);
                SuccessMessage = await DescribeOutcomeAsync(quoteId, what);
            }
            catch (InvalidOperationException ex)
            {
                ErrorMessage = ex.Message;
            }
            catch (UnauthorizedAccessException ex)
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

        /// <summary>
        /// What the approval actually did, read back from the quote. Falls
        /// back to a sentence that is true either way if the read fails —
        /// the approval itself has already committed, so this must never
        /// turn into an error message.
        /// </summary>
        private async Task<string> DescribeOutcomeAsync(Guid quoteId, string what)
        {
            try
            {
                var state = await _approvals.GetStateAsync(quoteId);

                if (string.Equals(state.QuoteStatus, "Approved", StringComparison.OrdinalIgnoreCase))
                    return $"{what} is approved. The rep can send it now.";

                if (string.Equals(state.QuoteStatus, "PendingApproval", StringComparison.OrdinalIgnoreCase))
                {
                    var next = state.ApproverNames.Count > 0
                        ? string.Join(", ", state.ApproverNames.Take(3))
                        : "the next approver";

                    return $"{what}: your step is signed. It has moved on to {next} — it isn't ready to send yet.";
                }

                return $"{what}: your approval is recorded.";
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Approved quote {QuoteId} but couldn't read back its state", quoteId);
                return $"{what}: your approval is recorded. Open the quote to see where it stands.";
            }
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

        /// <summary>"Step 2 of 3" — or nothing at all for a single-step chain.</summary>
        public static string? StepLabel(PendingApprovalChainDto p)
            => p.TotalSteps <= 1 ? null : $"Step {p.CurrentStepOrder} of {p.TotalSteps}";

        /// <summary>How many of this request's steps are already signed.</summary>
        public static int Signed(PendingApprovalChainDto p)
            => p.SoFar.Count(d => d.Decision == "Approved");
    }
}
