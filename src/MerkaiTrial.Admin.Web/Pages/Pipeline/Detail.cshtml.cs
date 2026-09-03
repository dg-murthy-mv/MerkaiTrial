// =====================================================================
// DEAL DETAIL PAGE MODEL - FIXED
// Location: MerkaiTrial.Admin.Web/Pages/Pipeline/Detail.cshtml.cs
// Fixes:
//   - StageHistory property added and loaded in OnGetAsync
//   - Relaxed model validation retained from previous version
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Deals;
using MerkaiTrial.Admin.Web.Services.Quotes;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;

namespace MerkaiTrial.Admin.Web.Pages.Pipeline
{
    public class DetailModel : AuthorizedPageModel
    {
        private readonly IDealService _dealService;
        private readonly IQuoteService _quoteService;
        private readonly ICurrentUserService _currentUserService;
        private readonly ICurrentTenantService _currentTenantService;
        private readonly ILogger<DetailModel> _logger;

        protected override string ModuleName => Modules.Deals;

        public DetailModel(
            IDealService dealService,
            IQuoteService quoteService,
            ICurrentUserService currentUserService,
            ICurrentTenantService currentTenantService,
            IAuthorizationService authorizationService,
            ILogger<DetailModel> logger)
            : base(authorizationService, currentUserService, logger)
        {
            _dealService          = dealService;
            _quoteService         = quoteService;
            _currentUserService   = currentUserService;
            _currentTenantService = currentTenantService;
            _logger               = logger;
        }

        // ==================== PAGE PROPERTIES ====================

        public DealDetailDto Deal { get; set; } = null!;
        public List<DealNoteDto> Notes { get; set; } = new();
        public List<DealActivityDto> Activities { get; set; } = new();
        public List<DealReminderDto> Reminders { get; set; } = new();
        public List<DealStageHistoryDto> StageHistory { get; set; } = new();
        public List<AttachmentDto> Attachments { get; set; } = new();  // ✅

        [BindProperty]
        public NoteInputModel NoteInput { get; set; } = new();

        [BindProperty]
        public ActivityInputModel ActivityInput { get; set; } = new();

        [BindProperty]
        public ReminderInputModel ReminderInput { get; set; } = new();

        [TempData]
        public string? SuccessMessage { get; set; }

        [TempData]
        public string? ErrorMessage { get; set; }

        // ── Level 1: UI lock ──────────────────────────────────────────
        // ClosedWon/ClosedLost deals are part of the financial audit trail.
        // Notes, activities and reminders can still be added (for post-deal records).
        public bool IsClosedDeal      => Deal?.Stage is "ClosedWon" or "Won" or "ClosedLost" or "Lost";
        public bool IsEditableState   => !IsClosedDeal;
        public bool IsDeletableState  => !IsClosedDeal;

        // ✅ FIX: "Create Quote" should only show when no quote exists yet
        // and the deal is in a quote-eligible stage (Proposal or Negotiation)
        public bool CanCreateQuote  => !IsClosedDeal &&
                                       Deal?.Stage is "Proposal" or "Negotiation" &&
                                       !HasExistingQuote;
        public bool HasExistingQuote { get; private set; }

        // ==================== INPUT MODELS ====================

        public class NoteInputModel
        {
            [Required(ErrorMessage = "Note text is required")]
            [StringLength(2000, ErrorMessage = "Note cannot exceed 2000 characters")]
            public string Note { get; set; } = string.Empty;
        }

        public class ActivityInputModel
        {
            [Required(ErrorMessage = "Activity type is required")]
            public string ActivityType { get; set; } = string.Empty;

            [StringLength(200)]
            public string? Subject { get; set; }

            [StringLength(1000)]
            public string? Description { get; set; }

            [Range(1, 480, ErrorMessage = "Duration must be between 1 and 480 minutes")]
            public int? Duration { get; set; }
        }

        public class ReminderInputModel
        {
            [Required(ErrorMessage = "Title is required")]
            [StringLength(200)]
            public string Title { get; set; } = string.Empty;

            [StringLength(1000)]
            public string? Description { get; set; }

            [Required(ErrorMessage = "Reminder date is required")]
            public DateTime ReminderDate { get; set; } = DateTime.Now.AddDays(1);
        }

        // ==================== GET HANDLER ====================

        public async Task<IActionResult> OnGetAsync(Guid id)
        {
            var check = await ValidatePermissionAsync(Actions.Read);
            if (check != null) return check;

            await InitializePermissionsAsync();

            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();

                Deal = await _dealService.GetDetailAsync(tenantId, id);

                var notesTask        = _dealService.GetNotesAsync(tenantId, id);
                var activitiesTask   = _dealService.GetActivitiesAsync(tenantId, id);
                var remindersTask    = _dealService.GetRemindersAsync(tenantId, id);
                var stageHistoryTask = _dealService.GetStageHistoryAsync(tenantId, id);
                var attachmentsTask  = _dealService.GetAttachmentsAsync(tenantId, id);  // ✅

                await Task.WhenAll(notesTask, activitiesTask, remindersTask, stageHistoryTask, attachmentsTask);

                Notes        = await notesTask;
                Activities   = await activitiesTask;
                Reminders    = await remindersTask;
                StageHistory = await stageHistoryTask;
                Attachments  = await attachmentsTask;

                // ✅ FIX: check if a quote already exists for this deal
                try
                {
                    var quotes = await _quoteService.GetAllAsync(tenantId);
                    HasExistingQuote = quotes?.Any(q => q.DealId == id) == true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not load quotes for deal {DealId}", id);
                    HasExistingQuote = false;
                }

                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load deal detail {DealId}", id);
                ErrorMessage = "Failed to load deal details. Please try again.";
                return RedirectToPage("/Pipeline/Index");
            }
        }

        // ==================== ADD NOTE ====================

        public async Task<IActionResult> OnPostAddNoteAsync(Guid id)
        {
            var check = await ValidatePermissionAsync(Actions.Update);
            if (check != null) return check;

            _logger.LogInformation("AddNote POST: NoteInput.Note = '{Value}'", NoteInput?.Note ?? "<null>");

            ModelState.Clear();
            if (!TryValidateModel(NoteInput, nameof(NoteInput)))
                return await OnGetAsync(id);

            try
            {
                var tenantId    = _currentUserService.GetCurrentTenantId();
                var currentUser = await _currentUserService.GetCurrentUserAsync();

                await _dealService.AddNoteAsync(new CreateDealNoteDto
                {
                    TenantId  = tenantId,
                    DealId    = id,
                    Note      = NoteInput.Note,
                    CreatedBy = currentUser.FullName
                });

                SuccessMessage = "Note added successfully!";
                return RedirectToPage(new { id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add note to deal {DealId}", id);
                ErrorMessage = "Failed to add note. Please try again.";
                return await OnGetAsync(id);
            }
        }

        // ==================== ADD ACTIVITY ====================

        public async Task<IActionResult> OnPostAddActivityAsync(Guid id)
        {
            var check = await ValidatePermissionAsync(Actions.Update);
            if (check != null) return check;

            ModelState.Clear();
            if (!TryValidateModel(ActivityInput, nameof(ActivityInput)))
            {
                LogModelStateErrors();
                return await OnGetAsync(id);
            }

            try
            {
                var tenantId    = _currentUserService.GetCurrentTenantId();
                var currentUser = await _currentUserService.GetCurrentUserAsync();

                await _dealService.AddActivityAsync(new CreateDealActivityDto
                {
                    TenantId     = tenantId,
                    DealId       = id,
                    ActivityType = ActivityInput.ActivityType,
                    Subject      = ActivityInput.Subject,
                    Description  = ActivityInput.Description,
                    Duration     = ActivityInput.Duration,
                    CreatedBy    = currentUser.FullName
                });

                SuccessMessage = "Activity logged successfully!";
                return RedirectToPage(new { id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add activity to deal {DealId}", id);
                ErrorMessage = "Failed to log activity. Please try again.";
                return await OnGetAsync(id);
            }
        }

        // ==================== ADD REMINDER ====================

        public async Task<IActionResult> OnPostAddReminderAsync(Guid id)
        {
            var check = await ValidatePermissionAsync(Actions.Update);
            if (check != null) return check;

            ModelState.Clear();
            if (!TryValidateModel(ReminderInput, nameof(ReminderInput)))
            {
                LogModelStateErrors();
                return await OnGetAsync(id);
            }

            try
            {
                var tenantId    = _currentUserService.GetCurrentTenantId();
                var currentUser = await _currentUserService.GetCurrentUserAsync();

                await _dealService.AddReminderAsync(new CreateDealReminderDto
                {
                    TenantId     = tenantId,
                    DealId       = id,
                    Title        = ReminderInput.Title,
                    Description  = ReminderInput.Description,
                    ReminderDate = ReminderInput.ReminderDate.ToUniversalTime(),
                    CreatedBy    = currentUser.FullName
                });

                SuccessMessage = "Reminder created successfully!";
                return RedirectToPage(new { id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add reminder to deal {DealId}", id);
                ErrorMessage = "Failed to create reminder. Please try again.";
                return await OnGetAsync(id);
            }
        }

        // ==================== COMPLETE REMINDER ====================

        public async Task<IActionResult> OnPostCompleteReminderAsync(Guid id, Guid reminderId)
        {
            try
            {
                var check = await ValidatePermissionAsync(Actions.Update);
                if (check != null) return check;

                var tenantId = _currentUserService.GetCurrentTenantId();
                // ✅ FIXED — pass id as dealId (it's right there in the method signature)
                await _dealService.CompleteReminderAsync(tenantId, id, reminderId);

                SuccessMessage = "Reminder marked as complete!";
                return RedirectToPage(new { id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to complete reminder {ReminderId}", reminderId);
                ErrorMessage = "Failed to complete reminder. Please try again.";
                return RedirectToPage(new { id });
            }
        }

        // ==================== DELETE DEAL ====================

        public async Task<IActionResult> OnPostDeleteAsync(Guid id)
        {
            try
            {
                var check = await ValidatePermissionAsync(Actions.Delete);
                if (check != null) return check;

                var tenantId = _currentUserService.GetCurrentTenantId();

                // ✅ Level 2: backend guard — reject even if UI is bypassed
                var deal = await _dealService.GetDetailAsync(tenantId, id);

                // Guard: block delete if an accepted/invoiced quote exists
                if (deal.Stage == "Negotiation")
                {
                    var quotes = await _quoteService.GetAllAsync(tenantId);
                    var hasAcceptedQuote = quotes?.Any(q =>
                        q.DealId == id &&
                        (q.Status == "Accepted" || q.Status == "Invoiced")) == true;

                    if (hasAcceptedQuote)
                    {
                        ErrorMessage = "This deal cannot be deleted — it has an accepted quote. Manage it via the Quote or close the deal as Lost.";
                        return RedirectToPage(new { id });
                    }
                }
                if (deal?.Stage is "ClosedWon" or "Won" or "ClosedLost" or "Lost")
                {
                    ErrorMessage = $"Closed deals cannot be deleted — they are part of the revenue audit trail. Stage: {deal.Stage}";
                    return RedirectToPage(new { id });
                }

                await _dealService.DeleteAsync(tenantId, id);
                SuccessMessage = "Deal deleted successfully!";
                return RedirectToPage("/Pipeline/Index");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete deal {DealId}", id);
                ErrorMessage = "Failed to delete deal. Please try again.";
                return RedirectToPage(new { id });
            }
        }

        // ==================== HELPER METHODS ====================

        // ✅ FormatDate / FormatDateTime / FormatCurrency now come from
        //    AuthorizedPageModel — removed from here to avoid CS0108 (hiding
        //    the inherited member). Same behaviour, single source of truth.

        public string GetRelativeTime(DateTime dateTime)
        {
            var span = DateTime.UtcNow - dateTime;
            if (span.TotalMinutes < 1)  return "just now";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
            if (span.TotalHours < 24)   return $"{(int)span.TotalHours}h ago";
            if (span.TotalDays < 30)    return $"{(int)span.TotalDays}d ago";
            if (span.TotalDays < 365)   return $"{(int)(span.TotalDays / 30)} months ago";
            return $"{(int)(span.TotalDays / 365)} years ago";
        }

        public string GetStageBadgeClass(string stage) => stage switch
        {
            "Discovery"     => "bg-secondary",
            "Qualification" => "bg-info",
            "Proposal"      => "bg-primary",
            "Negotiation"   => "bg-warning text-dark",
            "ClosedWon"     => "bg-success",
            "ClosedLost"    => "bg-danger",
            _               => "bg-secondary"
        };

        public string GetCurrencySymbol(string? currencyCode)
        {
            var code = currencyCode ?? _currentTenantService.GetCurrencyCode();
            return code switch
            {
                "INR" => "₹",
                "USD" => "$",
                "EUR" => "€",
                "GBP" => "£",
                "THB" => "฿",
                "PHP" => "₱",
                "AED" => "د.إ",
                _ => code
            };
        }
        private void LogModelStateErrors()
        {
            foreach (var kv in ModelState)
            {
                if (kv.Value?.Errors.Count > 0)
                    foreach (var err in kv.Value.Errors)
                        _logger.LogWarning("ModelState error. Key={Key} Error={Error}", kv.Key, err.ErrorMessage);
            }
        }

        // ==================== UPLOAD ATTACHMENT ====================

        public async Task<IActionResult> OnPostUploadAttachmentAsync(Guid id, IFormFile file)
        {
            try
            {
                var check = await ValidatePermissionAsync(Actions.Update);
                if (check != null) return check;

                if (file == null || file.Length == 0)
                {
                    ErrorMessage = "Please select a file to upload.";
                    return RedirectToPage(new { id });
                }

                var tenantId    = _currentUserService.GetCurrentTenantId();
                var currentUser = await _currentUserService.GetCurrentUserAsync();

                await _dealService.UploadAttachmentAsync(tenantId, id, file, currentUser.FullName);

                SuccessMessage = $"'{file.FileName}' uploaded successfully.";
                return RedirectToPage(new { id });
            }
            catch (InvalidOperationException ex)
            {
                // File size / MIME type rejected by storage service
                ErrorMessage = ex.Message;
                return RedirectToPage(new { id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to upload attachment for deal {DealId}", id);
                ErrorMessage = "Failed to upload file. Please try again.";
                return RedirectToPage(new { id });
            }
        }

        // ==================== DELETE ATTACHMENT ====================

        public async Task<IActionResult> OnPostDeleteAttachmentAsync(Guid id, Guid attachmentId)
        {
            try
            {
                var check = await ValidatePermissionAsync(Actions.Update);
                if (check != null) return check;

                var tenantId = _currentUserService.GetCurrentTenantId();
                await _dealService.DeleteAttachmentAsync(tenantId, attachmentId);

                SuccessMessage = "Attachment deleted.";
                return RedirectToPage(new { id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete attachment {AttachmentId}", attachmentId);
                ErrorMessage = "Failed to delete attachment. Please try again.";
                return RedirectToPage(new { id });
            }
        }
    }
}
