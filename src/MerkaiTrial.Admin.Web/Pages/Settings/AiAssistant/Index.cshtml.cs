// =====================================================================
// FILE: Pages/Settings/AiAssistant/Index.cshtml.cs
//
// NEW (122). Settings → AI Assistant. The workspace admin (or anyone
// holding settings.update) turns the assistant on or off for everyone in
// the workspace, and sees how many questions are left this month.
//
// It starts OFF: turning it on is the admin agreeing that people's
// questions may be sent to the AI provider MadeeVision uses. The page
// says what is sent. Turning it on or off is recorded in the Activity Log.
//
// 123: a second switch — "Let the assistant look at records". Also starts
// OFF. When on, the assistant may look up leads, deals, contacts,
// companies, quotes, invoices and tasks — always and only what the person
// asking is allowed to see — and what it finds is sent to the AI provider
// with the question. Recorded in the Activity Log too.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Ai;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Ai;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.Admin.Web.Pages.Settings.AiAssistant
{
    [RequiresPlanFeature(PlanFeatureKeys.AiAssistant)]
    public class IndexModel : AuthorizedPageModel
    {
        private readonly IAiAssistantApi _ai;

        public IndexModel(IAuthorizationService authorizationService, ICurrentUserService currentUserService,
            ILogger<IndexModel> logger, IAiAssistantApi ai)
            : base(authorizationService, currentUserService, logger)
        {
            _ai = ai;
        }

        protected override string ModuleName => Modules.Settings;

        public AiStatusDto? Status { get; private set; }
        public string? LoadError { get; private set; }

        public async Task<IActionResult> OnGetAsync()
        {
            var denied = await ValidatePermissionAsync(Actions.Read);
            if (denied is not null) return denied;
            await InitializePermissionsAsync();

            try { Status = await _ai.GetStatusAsync(); }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Loading the AI assistant settings failed");
                LoadError = "The AI assistant's settings could not be loaded just now. Please try again.";
            }
            return Page();
        }

        public async Task<IActionResult> OnPostSetAsync(bool on)
        {
            var denied = await ValidatePermissionAsync(Actions.Update);
            if (denied is not null) return denied;

            try
            {
                await _ai.SetOnAsync(on);
                TempData["SuccessMessage"] = on
                    ? "The AI assistant is on. Everyone in the workspace now sees \"Ask Merkai\" at the bottom right."
                    : "The AI assistant is off. Nothing more is sent to the AI provider.";
            }
            catch (InvalidOperationException ex) { TempData["ErrorMessage"] = ex.Message; }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Turning the AI assistant {State} failed", on ? "on" : "off");
                TempData["ErrorMessage"] = "That could not be saved just now. Please try again.";
            }
            return RedirectToPage();
        }

        /// <summary>123. Let the assistant look at records, or stop it.</summary>
        public async Task<IActionResult> OnPostRecordsAsync(bool records)
        {
            var denied = await ValidatePermissionAsync(Actions.Update);
            if (denied is not null) return denied;

            try
            {
                await _ai.SetRecordsAsync(records);
                TempData["SuccessMessage"] = records
                    ? "The assistant can now look at records — only the ones each person is allowed to see."
                    : "The assistant no longer looks at records.";
            }
            catch (InvalidOperationException ex) { TempData["ErrorMessage"] = ex.Message; }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Changing the AI assistant's record access failed");
                TempData["ErrorMessage"] = "That could not be saved just now. Please try again.";
            }
            return RedirectToPage();
        }
    }
}
