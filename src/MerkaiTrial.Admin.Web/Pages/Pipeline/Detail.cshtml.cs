// =====================================================================
// DEAL DETAIL PAGE MODEL
// Location: MerkaiTrial.Admin.Web/Pages/Pipeline/Detail.cshtml.cs
//
// COMPLETE FILE — replaces the existing one.
//
// CHANGES IN THIS PASS
//
// 1. ACTIVITIES AND REMINDERS NOW USE IActivityService (the unified
//    Activities table) instead of IDealService's DealActivities /
//    DealReminders. Migration 010 already copied the existing rows, so
//    nothing disappears. This is the last legacy writer — after this, the
//    old tables are read by nothing.
//
// 2. LEAD HISTORY APPEARS ON THE DEAL. GetActivitiesHandler follows
//    Deal.LeadId, so everything logged while it was a lead now shows in
//    the Activities tab, marked "from lead". Nothing is copied; it is read
//    through the link. This is the context a rep loses today at exactly
//    the moment it matters.
//
// 3. TIMEZONE. ReminderDate used .ToUniversalTime(), which converts using
//    the SERVER's timezone — right on your laptop for an Indian tenant,
//    wrong for Thai tenants, and a silent no-op on Azure (UTC servers).
//    Now _currentTenantService.LocalToUtc. Activity dates can be backdated
//    for the first time (the old handler always stamped "now", so logging
//    yesterday's call recorded it as today).
//
// 4. GetRelativeTime did UtcNow - dateTime with no conversion, so "2h ago"
//    was wrong by the tenant's offset. Now converts both sides.
//
// 5. STAGE NAMES. IsClosedDeal checked four spellings ("ClosedWon",
//    "Won", "ClosedLost", "Lost") because the vocabulary is inconsistent.
//    That defensive check is preserved but moved into one place,
//    StageRules, so normalisation later changes one file, not ten call
//    sites. NOT a fix — the underlying inconsistency still needs the
//    migration we discussed.
//
// 6. CreatedBy was the user's FULL NAME here and the user ID on the Lead
//    page, so the same person counted as two people in reports. The API
//    now sets it from the signed-in user, so both pages agree.
//
// 7. Error messages show the API's reason instead of "Please try again".
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Activities;
using MerkaiTrial.Admin.Web.Services.Deals;
using MerkaiTrial.Admin.Web.Services.Quotes;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.Activities;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.ComponentModel.DataAnnotations;

namespace MerkaiTrial.Admin.Web.Pages.Pipeline
{
    public class DetailModel : AuthorizedPageModel
    {
        private readonly IDealService _dealService;
        private readonly IQuoteService _quoteService;
        private readonly IActivityService _activityService;
        private readonly ICurrentUserService _currentUserService;
        private readonly ICurrentTenantService _currentTenantService;
        private readonly ILogger<DetailModel> _logger;

        protected override string ModuleName => Modules.Deals;

        public DetailModel(
            IDealService dealService,
            IQuoteService quoteService,
            IActivityService activityService,
            ICurrentUserService currentUserService,
            ICurrentTenantService currentTenantService,
            IAuthorizationService authorizationService,
            ILogger<DetailModel> logger)
            : base(authorizationService, currentUserService, logger)
        {
            _dealService          = dealService;
            _quoteService         = quoteService;
            _activityService      = activityService;
            _currentUserService   = currentUserService;
            _currentTenantService = currentTenantService;
            _logger               = logger;
        }

        // ==================== PAGE PROPERTIES ====================

        public DealDetailDto Deal { get; set; } = null!;
        public List<DealNoteDto> Notes { get; set; } = new();
        public List<DealStageHistoryDto> StageHistory { get; set; } = new();
        public List<AttachmentDto> Attachments { get; set; } = new();

        /// <summary>Deal activity AND the source lead's history, in one call.</summary>
        public List<ActivityDto> AllActivities { get; set; } = new();

        public List<ActivityDto> Activities => AllActivities.Where(a => !a.IsTask).ToList();
        public List<ActivityDto> Tasks      => AllActivities.Where(a =>  a.IsTask).ToList();

        public List<ActivityDto> OpenTasks =>
            Tasks.Where(t => !t.IsCompleted).OrderBy(t => t.DueDate).ToList();

        public List<ActivityDto> CompletedTasks =>
            Tasks.Where(t => t.IsCompleted).OrderByDescending(t => t.CompletedAtUtc).ToList();

        /// <summary>True when the row came from the lead this deal was converted from.</summary>
        public bool IsFromLead(ActivityDto a) => a.EntityType == ActivityEntityType.Lead;

        [BindProperty]
        public NoteInputModel NoteInput { get; set; } = new();

        [BindProperty]
        public ActivityInputModel ActivityInput { get; set; } = new();

        [BindProperty]
        public TaskInputModel TaskInput { get; set; } = new();

        [TempData]
        public string? SuccessMessage { get; set; }

        [TempData]
        public string? ErrorMessage { get; set; }

        // ── Level 1: UI lock ──────────────────────────────────────────
        // Closed deals are part of the financial audit trail. Notes,
        // activities and tasks can still be added (post-deal records).
        public bool IsClosedDeal      => StageRules.IsClosed(Deal?.Stage);
        public bool IsEditableState   => !IsClosedDeal;
        public bool IsDeletableState  => !IsClosedDeal;

        // "Create Quote" only when no quote exists and the deal is in a
        // quote-eligible stage.
        public bool CanCreateQuote  => !IsClosedDeal &&
                                       StageRules.IsQuoteEligible(Deal?.Stage) &&
                                       !HasExistingQuote;
        public bool HasExistingQuote { get; private set; }

        /// <summary>
        /// TEMPORARY single place for stage-name checks. Four spellings are
        /// live across the codebase ("Won"/"ClosedWon"/"Lost"/"ClosedLost"),
        /// so every check has to accept all of them. When the stage
        /// vocabulary is normalised, this class is the only thing to change
        /// on this page.
        /// </summary>
        public static class StageRules
        {
            public static bool IsWon(string? stage)
                => stage is "ClosedWon" or "Won";

            public static bool IsLost(string? stage)
                => stage is "ClosedLost" or "Lost";

            public static bool IsClosed(string? stage)
                => IsWon(stage) || IsLost(stage);

            public static bool IsQuoteEligible(string? stage)
                => stage is "Proposal" or "Negotiation";
        }

        // ==================== INPUT MODELS ====================

        public class NoteInputModel
        {
            [Required(ErrorMessage = "Note text is required")]
            [StringLength(2000, ErrorMessage = "Note cannot exceed 2000 characters")]
            public string Note { get; set; } = string.Empty;
        }

        /// <summary>Logging something that already happened.</summary>
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

            /// <summary>Tenant-local; null = now. New — the old page couldn't backdate.</summary>
            public DateTime? ActivityDate { get; set; }
        }

        /// <summary>Scheduling work. Replaces ReminderInputModel.</summary>
        public class TaskInputModel
        {
            [Required(ErrorMessage = "Task type is required")]
            public string ActivityType { get; set; } = MerkaiTrial.Application.Commands.Activities.ActivityType.Task;

            [Required(ErrorMessage = "Title is required")]
            [StringLength(200)]
            public string Subject { get; set; } = string.Empty;

            [StringLength(1000)]
            public string? Description { get; set; }

            [Required(ErrorMessage = "Due date is required")]
            public DateTime DueDate { get; set; }
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
                var stageHistoryTask = _dealService.GetStageHistoryAsync(tenantId, id);
                var attachmentsTask  = _dealService.GetAttachmentsAsync(tenantId, id);
                var activitiesTask   = LoadActivitiesAsync(tenantId, id);

                await Task.WhenAll(notesTask, stageHistoryTask, attachmentsTask, activitiesTask);

                Notes        = await notesTask;
                StageHistory = await stageHistoryTask;
                Attachments  = await attachmentsTask;

                // Default due date: tomorrow morning, tenant-local.
                if (TaskInput.DueDate == default)
                    TaskInput.DueDate = _currentTenantService.UtcToLocal(DateTime.UtcNow)
                                                             .Date.AddDays(1).AddHours(9);

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

            ModelState.Clear();
            if (!TryValidateModel(NoteInput, nameof(NoteInput)))
            {
                LogModelStateErrors();
                return await OnGetAsync(id);
            }

            try
            {
                var tenantId    = _currentUserService.GetCurrentTenantId();
                var currentUser = await _currentUserService.GetCurrentUserAsync();

                await _dealService.AddNoteAsync(new CreateDealNoteDto
                {
                    TenantId  = tenantId,
                    DealId    = id,
                    Note      = NoteInput.Note,
                    // Was FullName. The user ID matches what the Lead page and
                    // the Activities API store, so reports group correctly.
                    CreatedBy = currentUser.UserId.ToString()
                });

                SuccessMessage = "Note added successfully!";
                return RedirectToPage(new { id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add note to deal {DealId}", id);
                ErrorMessage = Explain(ex, "Failed to add note.");
                return RedirectToPage(new { id });
            }
        }

        // ==================== ADD ACTIVITY (log) ====================

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
                var tenantId      = _currentUserService.GetCurrentTenantId();
                var currentUserId = _currentUserService.GetCurrentUserId();

                DateTime? activityDateUtc = ActivityInput.ActivityDate.HasValue
                    ? _currentTenantService.LocalToUtc(ActivityInput.ActivityDate.Value)
                    : null;

                await _activityService.CreateAsync(new CreateActivityDto(
                    TenantId: tenantId,
                    EntityType: ActivityEntityType.Deal,
                    EntityId: id,
                    ActivityType: ActivityInput.ActivityType,
                    // Subject is optional here; the API falls back to the type name.
                    Subject: ActivityInput.Subject ?? string.Empty,
                    Description: ActivityInput.Description,
                    Duration: ActivityInput.Duration,
                    ActivityDate: activityDateUtc,
                    IsTask: false,
                    DueDate: null,
                    AssignedToUserId: currentUserId.ToString(),
                    CreatedBy: currentUserId.ToString()
                ));

                SuccessMessage = "Activity logged successfully!";
                return RedirectToPage(new { id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add activity to deal {DealId}", id);
                ErrorMessage = Explain(ex, "Failed to log activity.");
                return RedirectToPage(new { id });
            }
        }

        // ==================== ADD TASK (was reminder) ====================

        public async Task<IActionResult> OnPostAddTaskAsync(Guid id)
        {
            var check = await ValidatePermissionAsync(Actions.Update);
            if (check != null) return check;

            ModelState.Clear();
            if (!TryValidateModel(TaskInput, nameof(TaskInput)))
            {
                LogModelStateErrors();
                return await OnGetAsync(id);
            }

            try
            {
                var tenantId      = _currentUserService.GetCurrentTenantId();
                var currentUserId = _currentUserService.GetCurrentUserId();

                await _activityService.CreateAsync(new CreateActivityDto(
                    TenantId: tenantId,
                    EntityType: ActivityEntityType.Deal,
                    EntityId: id,
                    ActivityType: TaskInput.ActivityType,
                    Subject: TaskInput.Subject,
                    Description: TaskInput.Description,
                    Duration: null,
                    ActivityDate: null,
                    IsTask: true,
                    DueDate: _currentTenantService.LocalToUtc(TaskInput.DueDate),
                    AssignedToUserId: currentUserId.ToString(),
                    CreatedBy: currentUserId.ToString()
                ));

                SuccessMessage = "Task created successfully!";
                return RedirectToPage(new { id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add task to deal {DealId}", id);
                ErrorMessage = Explain(ex, "Failed to create task.");
                return RedirectToPage(new { id });
            }
        }

        // ==================== COMPLETE TASK ====================

        public async Task<IActionResult> OnPostCompleteTaskAsync(Guid id, Guid activityId)
        {
            var check = await ValidatePermissionAsync(Actions.Update);
            if (check != null) return check;

            try
            {
                var tenantId      = _currentUserService.GetCurrentTenantId();
                var currentUserId = _currentUserService.GetCurrentUserId();

                await _activityService.CompleteAsync(new CompleteActivityDto(
                    TenantId: tenantId,
                    ActivityId: activityId,
                    Outcome: null,
                    CompletedBy: currentUserId.ToString()
                ));

                SuccessMessage = "Task marked as complete!";
                return RedirectToPage(new { id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to complete task {ActivityId}", activityId);
                ErrorMessage = Explain(ex, "Failed to complete task.");
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

                // Level 2: backend guard — reject even if the UI is bypassed
                var deal = await _dealService.GetDetailAsync(tenantId, id);

                // Block delete when an accepted or invoiced quote exists.
                // Checked for ANY open stage: the old code only looked when the
                // stage was "Negotiation", so a deal moved back to Proposal
                // after its quote was accepted could still be deleted.
                if (!StageRules.IsClosed(deal?.Stage))
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

                if (StageRules.IsClosed(deal?.Stage))
                {
                    ErrorMessage = $"Closed deals cannot be deleted — they are part of the revenue audit trail. Stage: {deal!.Stage}";
                    return RedirectToPage(new { id });
                }

                await _dealService.DeleteAsync(tenantId, id);
                SuccessMessage = "Deal deleted successfully!";
                return RedirectToPage("/Pipeline/Index");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete deal {DealId}", id);
                ErrorMessage = Explain(ex, "Failed to delete deal.");
                return RedirectToPage(new { id });
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

                await _dealService.UploadAttachmentAsync(tenantId, id, file, currentUser.UserId.ToString());

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
                ErrorMessage = Explain(ex, "Failed to upload file.");
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
                ErrorMessage = Explain(ex, "Failed to delete attachment.");
                return RedirectToPage(new { id });
            }
        }

        // ==================== PRIVATE HELPERS ====================

        private async Task LoadActivitiesAsync(Guid tenantId, Guid dealId)
        {
            try
            {
                AllActivities = await _activityService.GetForEntityAsync(
                    new GetActivitiesQuery(tenantId, ActivityEntityType.Deal, dealId));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load activities for deal {DealId}", dealId);
                AllActivities = new();
            }
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

        private static string Explain(Exception ex, string fallback)
            => string.IsNullOrWhiteSpace(ex.Message) || ex is NullReferenceException
                ? $"{fallback} Please try again."
                : ex.Message;

        // ==================== VIEW HELPERS ====================

        // FormatDate / FormatDateTime / FormatCurrency come from
        // AuthorizedPageModel — single source of truth.

        public string GetRelativeTime(DateTime utcDateTime)
        {
            // Was UtcNow - dateTime with no conversion: off by the tenant's
            // offset, so a call logged 1 hour ago read "6h ago" in Bangkok.
            var localNow  = _currentTenantService.UtcToLocal(DateTime.UtcNow);
            var localThen = _currentTenantService.UtcToLocal(utcDateTime);
            var span      = localNow - localThen;

            // Timeline shows tasks by DUE date, which is usually in the future.
            if (span.TotalSeconds < -60)
            {
                var ahead = -span;
                if (ahead.TotalHours < 24) return $"in {(int)ahead.TotalHours}h";
                if (ahead.TotalDays < 30) return $"in {(int)ahead.TotalDays}d";
                return FormatDate(utcDateTime);
            }

            if (span.TotalMinutes < 1)  return "just now";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
            if (span.TotalHours < 24)   return $"{(int)span.TotalHours}h ago";
            if (span.TotalDays < 30)    return $"{(int)span.TotalDays}d ago";
            if (span.TotalDays < 365)   return $"{(int)(span.TotalDays / 30)} months ago";
            return $"{(int)(span.TotalDays / 365)} years ago";
        }

        /// <summary>"in 2 days" / "3 days overdue", in the tenant's timezone.</summary>
        public string GetDueDescription(DateTime? dueUtc)
        {
            if (dueUtc is null) return "";

            var localNow = _currentTenantService.UtcToLocal(DateTime.UtcNow);
            var localDue = _currentTenantService.UtcToLocal(dueUtc.Value);
            var span     = localDue - localNow;

            if (span.TotalSeconds < 0)
            {
                var overdue = -span;
                if (overdue.TotalHours < 1)  return $"{(int)overdue.TotalMinutes}m overdue";
                if (overdue.TotalHours < 24) return $"{(int)overdue.TotalHours}h overdue";
                return $"{(int)overdue.TotalDays}d overdue";
            }

            if (span.TotalMinutes < 60) return $"in {(int)span.TotalMinutes}m";
            if (span.TotalHours < 24)   return $"in {(int)span.TotalHours}h";
            return $"in {(int)span.TotalDays}d";
        }

        public bool IsOverdue(ActivityDto task)
            => task.IsTask && !task.IsCompleted && task.DueDate.HasValue
               && task.DueDate.Value < DateTime.UtcNow;

        public string GetActivityIcon(string activityType) => ActivityType.Icon(activityType);

        public string GetStageBadgeClass(string stage) => stage switch
        {
            "Discovery"     => "bg-secondary",
            "Qualification" => "bg-info",
            "Proposal"      => "bg-primary",
            "Negotiation"   => "bg-warning text-dark",
            "ClosedWon"     => "bg-success",
            "Won"           => "bg-success",
            "ClosedLost"    => "bg-danger",
            "Lost"          => "bg-danger",
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
    }
}
