// =====================================================================
// DEAL DETAIL PAGE MODEL
// Location: MerkaiTrial.Admin.Web/Pages/Pipeline/Detail.cshtml.cs
//
// COMPLETE FILE — replaces the existing one.
//
// NEW IN 020 (Blueprint transitions)
//   • The transition bar. Instead of "go to the Edit page and pick a
//     stage from a dropdown", the deal shows the moves its own process
//     offers — "Send Quote", "Mark as Closed Won" — as buttons, with the
//     blocked ones DISABLED and carrying the reason. A greyed-out button
//     reading "this deal has no quote yet" teaches the process; a missing
//     one just looks broken.
//   • Admin override. Buttons-only navigation means a badly pruned
//     process could strand a deal, so a workspace admin gets a "Change
//     stage" escape hatch. It skips the PROCESS, never the money rule:
//     an invoiced deal still cannot be reopened by anyone.
//   • BUG: Stages loaded with activeOnly: true. A deal sitting in a
//     RETIRED stage found no match, so CurrentStage was null, IsClosedDeal
//     was false, and a closed deal rendered as open, editable and
//     deletable. Now loads all stages.
//
// NEW IN THE PREVIOUS PASS (task follow-through — matching the Lead page)
//   • Assign a task to a colleague.
//   • Edit / reschedule a task; edit a logged activity. Inline, via
//     ?editId=<guid>.
//   • Delete an entry — needs Deals.Delete, not Deals.Update.
//   • "How did it go?" added AFTER completing, so completing is one click.
//   • Editing someone else's entry needs tenant admin.
//
// CLOSED DEALS: notes, activities and tasks can still be added, edited and
// completed after a deal closes — post-sale follow-up is real work, and a
// Won deal still has a handover. Only the DEAL itself is locked.
//
// A deal's entries include rows carried over from the lead it came from
// (IsFromLead). Those are editable under the same rules as any other.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Activities;
using MerkaiTrial.Admin.Web.Services.Deals;
using MerkaiTrial.Admin.Web.Services.Pipeline;
using MerkaiTrial.Admin.Web.Services.Quotes;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.Activities;
using MerkaiTrial.Application.Commands.PipelineStages;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using MerkaiTrial.Domain.Entities;
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
        private readonly IPipelineStageService _stageService;
        private readonly IPipelineRuleService _ruleService;          // 020
        private readonly IDealStageService _dealStageService;        // 020
        protected override string ModuleName => Modules.Deals;

        public DetailModel(
            IDealService dealService,
            IQuoteService quoteService,
            IActivityService activityService,
            ICurrentUserService currentUserService,
            ICurrentTenantService currentTenantService,
            IAuthorizationService authorizationService,
            ILogger<DetailModel> logger,
            IPipelineStageService stageService,
            IPipelineRuleService ruleService,                        // 020
            IDealStageService dealStageService)                      // 020
            : base(authorizationService, currentUserService, logger)
        {
            _dealService          = dealService;
            _quoteService         = quoteService;
            _activityService      = activityService;
            _currentUserService   = currentUserService;
            _currentTenantService = currentTenantService;
            _logger               = logger;
            _stageService         = stageService;
            _ruleService          = ruleService;
            _dealStageService     = dealStageService;
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

        public List<PipelineStageDto> Stages { get; set; } = new();

        /// <summary>
        /// 020 — the moves this deal can make from where it is, each
        /// already marked allowed or not. Null only when the call failed;
        /// the page then falls back to showing no transition bar rather
        /// than breaking.
        /// </summary>
        public DealTransitionsDto? Transitions { get; set; }
        public List<ActivityDto> OpenTasks =>
            Tasks.Where(t => !t.IsCompleted).OrderBy(t => t.DueDate).ToList();

        public List<ActivityDto> CompletedTasks =>
            Tasks.Where(t => t.IsCompleted).OrderByDescending(t => t.CompletedAtUtc).ToList();

        /// <summary>True when the row came from the lead this deal was converted from.</summary>
        public bool IsFromLead(ActivityDto a) => a.EntityType == ActivityEntityType.Lead;

        // ── Who is looking ────────────────────────────────────────────
        public Guid CurrentUserId { get; private set; }
        public bool IsTenantAdmin { get; private set; }

        /// <summary>Colleagues a task can be assigned to.</summary>
        public List<AssigneeDto> Assignees { get; private set; } = new();

        [BindProperty]
        public NoteInputModel NoteInput { get; set; } = new();

        [BindProperty]
        public ActivityInputModel ActivityInput { get; set; } = new();

        [BindProperty]
        public TaskInputModel TaskInput { get; set; } = new();

        [BindProperty]
        public EditEntryModel EditInput { get; set; } = new();

        [BindProperty]
        public string? OutcomeText { get; set; }

        /// <summary>Which entry is being edited inline. Null = none.</summary>
        [BindProperty(SupportsGet = true)]
        public Guid? EditId { get; set; }

        /// <summary>Which completed entry is having an outcome added.</summary>
        [BindProperty(SupportsGet = true)]
        public Guid? OutcomeId { get; set; }

        [TempData]
        public string? SuccessMessage { get; set; }

        [TempData]
        public string? ErrorMessage { get; set; }

        // ── Level 1: UI lock ──────────────────────────────────────────
        // Closed deals are part of the financial audit trail. Notes,
        // activities and tasks can still be added (post-deal records).
        private PipelineStageDto? CurrentStage =>
            Stages.FirstOrDefault(s => s.Key == Deal?.Stage);
        public string StageName(string? key)
        {
            if (string.IsNullOrEmpty(key)) return "—";
            return Stages.FirstOrDefault(s => s.Key == key)?.Name ?? key;
        }

        public bool IsClosedDeal => CurrentStage?.Category is StageCategory.Won or StageCategory.Lost;

        /// <summary>
        /// A stage's category by key. The header badge asks this instead of
        /// matching the literal strings "ClosedWon" / "Won", which a tenant
        /// who renamed their winning stage never matched.
        /// </summary>
        public StageCategory StageCategoryOf(string? key) =>
            Stages.FirstOrDefault(s => s.Key == key)?.Category ?? StageCategory.Open;

        // Quote eligibility was "Proposal or Negotiation" by name. With
        // tenant stages that cannot hold, so: any open stage past the
        // first. A client quoting earlier or later than us is not wrong.
        public bool CanCreateQuote => !IsClosedDeal && !HasExistingQuote;
        public bool IsEditableState => !IsClosedDeal;
        public bool IsDeletableState => !IsClosedDeal;

        public bool HasExistingQuote { get; private set; }

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

            /// <summary>Tenant-local; null = now.</summary>
            public DateTime? ActivityDate { get; set; }
        }

        /// <summary>Scheduling work.</summary>
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

            /// <summary>Empty = assign to myself.</summary>
            public string? AssignedToUserId { get; set; }
        }

        /// <summary>One form, used for editing either a task or a logged activity.</summary>
        public class EditEntryModel
        {
            public Guid ActivityId { get; set; }
            public bool IsTask { get; set; }

            [Required(ErrorMessage = "Type is required")]
            public string ActivityType { get; set; } = string.Empty;

            [Required(ErrorMessage = "Subject is required")]
            [StringLength(200)]
            public string Subject { get; set; } = string.Empty;

            [StringLength(1000)]
            public string? Description { get; set; }

            [Range(1, 480)]
            public int? Duration { get; set; }

            /// <summary>Tasks: when it's due. Local time.</summary>
            public DateTime? DueDate { get; set; }

            /// <summary>Logs: when it happened. Local time.</summary>
            public DateTime? ActivityDate { get; set; }

            public string? AssignedToUserId { get; set; }

            [StringLength(2000)]
            public string? Outcome { get; set; }
        }

        // ==================== GET HANDLER ====================

        public async Task<IActionResult> OnGetAsync(Guid id)
        {
            var check = await ValidatePermissionAsync(Actions.Read);
            if (check != null) return check;

            await InitializePermissionsAsync();

            try
            {
                var me = await _currentUserService.GetCurrentUserAsync();
                CurrentUserId = me.UserId;
                IsTenantAdmin = me.IsTenantAdmin;

                var tenantId = me.TenantId;

                Deal = await _dealService.GetDetailAsync(tenantId, id);

                var notesTask        = _dealService.GetNotesAsync(tenantId, id);
                var stageHistoryTask = _dealService.GetStageHistoryAsync(tenantId, id);
                // activeOnly: FALSE. A deal can sit in a stage the tenant
                // has since retired; with only active stages loaded,
                // CurrentStage came back null, IsClosedDeal read false, and
                // a closed deal was rendered as open and deletable.
                var stagesTask       = _stageService.GetAsync(activeOnly: false);
                var attachmentsTask  = _dealService.GetAttachmentsAsync(tenantId, id);
                var activitiesTask   = LoadActivitiesAsync(tenantId, id);
                var assigneesTask    = LoadAssigneesAsync();
                var transitionsTask  = LoadTransitionsAsync(id);

                await Task.WhenAll(notesTask, stageHistoryTask, stagesTask, attachmentsTask,
                                   activitiesTask, assigneesTask, transitionsTask);

                Notes        = await notesTask;
                StageHistory = await stageHistoryTask;
                Stages       = await stagesTask;
                Attachments  = await attachmentsTask;

                // Default due date: tomorrow morning, tenant-local.
                if (TaskInput.DueDate == default)
                    TaskInput.DueDate = _currentTenantService.UtcToLocal(DateTime.UtcNow)
                                                             .Date.AddDays(1).AddHours(9);

                PrefillEditForm();

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

        // ==================== MOVE THE DEAL (020) ====================

        /// <summary>
        /// The transition buttons post here. One handler for every move,
        /// because the rules live in the guard and the page should not
        /// have opinions about which button means what.
        ///
        /// The note is whatever the transition asked for — a lost reason,
        /// a reopen reason, "which site did you survey" — so this handler
        /// never needs to know which.
        /// </summary>
        public async Task<IActionResult> OnPostMoveStageAsync(
            Guid id, string stage, string? note, bool adminOverride = false)
        {
            var check = await ValidatePermissionAsync(Actions.Update);
            if (check != null) return check;

            if (string.IsNullOrWhiteSpace(stage))
            {
                ErrorMessage = "No stage was chosen.";
                return RedirectToPage(new { id });
            }

            try
            {
                await _dealStageService.MoveAsync(id, stage, note, adminOverride);

                SuccessMessage = adminOverride
                    ? "Stage changed. This move was recorded as an admin override."
                    : "Deal moved.";

                return RedirectToPage(new { id });
            }
            // IApiService turns the API's 400/403 { "error": ... } into
            // this, carrying the guard's own wording. That sentence names
            // the missing piece — "it needs an accepted quote" — and is the
            // whole point of the round, so it is shown as it stands.
            catch (InvalidOperationException ex)
            {
                _logger.LogInformation(
                    "Move refused for deal {DealId} -> {Stage}: {Message}", id, stage, ex.Message);

                ErrorMessage = ex.Message;
                return RedirectToPage(new { id });
            }
            catch (KeyNotFoundException)
            {
                ErrorMessage = "That deal could not be found.";
                return RedirectToPage("/Pipeline/Index");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to move deal {DealId} to {Stage}", id, stage);
                ErrorMessage = Explain(ex, "Failed to move the deal.");
                return RedirectToPage(new { id });
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

        // ==================== LOG AN ACTIVITY ====================

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

                SuccessMessage = "Activity logged.";
                return RedirectToPage(new { id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add activity to deal {DealId}", id);
                ErrorMessage = Explain(ex, "Failed to log activity.");
                return RedirectToPage(new { id });
            }
        }

        // ==================== PLAN A TASK ====================

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

                var assignee = string.IsNullOrWhiteSpace(TaskInput.AssignedToUserId)
                    ? currentUserId.ToString()
                    : TaskInput.AssignedToUserId;

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
                    AssignedToUserId: assignee,
                    CreatedBy: currentUserId.ToString()
                ));

                SuccessMessage = assignee == currentUserId.ToString()
                    ? "Task created."
                    : "Task created and assigned.";
                return RedirectToPage(new { id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add task to deal {DealId}", id);
                ErrorMessage = Explain(ex, "Failed to create task.");
                return RedirectToPage(new { id });
            }
        }

        // ==================== COMPLETE — one click ====================

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

                SuccessMessage = "Task done.";
                return RedirectToPage(new { id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to complete task {ActivityId}", activityId);
                ErrorMessage = Explain(ex, "Failed to complete task.");
                return RedirectToPage(new { id });
            }
        }

        // ==================== OUTCOME — added afterwards ====================

        public async Task<IActionResult> OnPostSetOutcomeAsync(Guid id, Guid activityId)
        {
            var check = await ValidatePermissionAsync(Actions.Update);
            if (check != null) return check;

            try
            {
                var tenantId      = _currentUserService.GetCurrentTenantId();
                var currentUserId = _currentUserService.GetCurrentUserId();

                await _activityService.SetOutcomeAsync(new SetActivityOutcomeDto(
                    TenantId: tenantId,
                    ActivityId: activityId,
                    Outcome: OutcomeText,
                    UpdatedBy: currentUserId.ToString()
                ));

                SuccessMessage = "Outcome saved.";
                return RedirectToPage(new { id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set outcome on {ActivityId}", activityId);
                ErrorMessage = Explain(ex, "Failed to save the outcome.");
                return RedirectToPage(new { id });
            }
        }

        // ==================== EDIT / RESCHEDULE ====================

        public async Task<IActionResult> OnPostUpdateEntryAsync(Guid id)
        {
            var check = await ValidatePermissionAsync(Actions.Update);
            if (check != null) return check;

            ModelState.Clear();
            if (!TryValidateModel(EditInput, nameof(EditInput)))
            {
                LogModelStateErrors();
                EditId = EditInput.ActivityId;   // keep the form open
                return await OnGetAsync(id);
            }

            try
            {
                var tenantId      = _currentUserService.GetCurrentTenantId();
                var currentUserId = _currentUserService.GetCurrentUserId();

                DateTime? dueUtc = EditInput.IsTask && EditInput.DueDate.HasValue
                    ? _currentTenantService.LocalToUtc(EditInput.DueDate.Value)
                    : null;

                var whenUtc = EditInput.ActivityDate.HasValue
                    ? _currentTenantService.LocalToUtc(EditInput.ActivityDate.Value)
                    : DateTime.UtcNow;

                await _activityService.UpdateAsync(new UpdateActivityDto(
                    TenantId: tenantId,
                    ActivityId: EditInput.ActivityId,
                    Subject: EditInput.Subject,
                    Description: EditInput.Description,
                    Duration: EditInput.Duration,
                    ActivityDate: whenUtc,
                    DueDate: dueUtc,
                    AssignedToUserId: EditInput.AssignedToUserId,
                    Outcome: EditInput.Outcome,
                    UpdatedBy: currentUserId.ToString(),
                    ActivityType: EditInput.ActivityType
                ));

                SuccessMessage = EditInput.IsTask ? "Task updated." : "Activity updated.";
                return RedirectToPage(new { id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update entry {ActivityId}", EditInput.ActivityId);
                ErrorMessage = Explain(ex, "Failed to save those changes.");
                return RedirectToPage(new { id });
            }
        }

        // ==================== DELETE ENTRY — needs Deals.Delete ====================

        public async Task<IActionResult> OnPostDeleteEntryAsync(Guid id, Guid activityId)
        {
            var check = await ValidatePermissionAsync(Actions.Delete);
            if (check != null) return check;

            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();
                await _activityService.DeleteAsync(tenantId, activityId);
                SuccessMessage = "Entry deleted.";
                return RedirectToPage(new { id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete entry {ActivityId}", activityId);
                ErrorMessage = Explain(ex, "Failed to delete that entry.");
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
                var stages = await _stageService.GetAsync(activeOnly: false);
                var stage = stages.FirstOrDefault(s => s.Key == deal?.Stage);
                var isTerminal = stage?.Category is StageCategory.Won or StageCategory.Lost;

                // Block delete when an accepted or invoiced quote exists.
                // Checked for ANY open stage: the old code only looked when the
                // stage was "Negotiation", so a deal moved back to Proposal
                // after its quote was accepted could still be deleted.
                if (!isTerminal)
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

                if (isTerminal)
                {
                    ErrorMessage = $"Closed deals cannot be deleted — they are part of the revenue audit trail. Stage: {StageName(deal?.Stage)}";
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

        // ==================== ATTACHMENTS ====================

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

        /// <summary>
        /// 020 — what this deal can do from where it is. A failure here
        /// costs the transition bar, not the page: the deal is still
        /// readable, and the kanban is still a way to move it.
        /// </summary>
        private async Task LoadTransitionsAsync(Guid dealId)
        {
            try
            {
                Transitions = await _ruleService.GetForDealAsync(dealId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load transitions for deal {DealId}", dealId);
                Transitions = null;
            }
        }

        private async Task LoadAssigneesAsync()
        {
            try { Assignees = await _activityService.GetAssigneesAsync(); }
            catch (Exception ex)
            {
                // A missing list just means no dropdown — the task still gets
                // created, assigned to the person creating it.
                _logger.LogWarning(ex, "Failed to load assignees");
                Assignees = new();
            }
        }

        /// <summary>
        /// When ?editId= names an entry, copy its current values into the
        /// edit form so the inline form opens populated.
        /// </summary>
        private void PrefillEditForm()
        {
            if (EditId is null) return;

            var entry = AllActivities.FirstOrDefault(a => a.Id == EditId.Value);
            if (entry is null) { EditId = null; return; }

            if (!CanEditEntry(entry)) { EditId = null; return; }

            EditInput = new EditEntryModel
            {
                ActivityId       = entry.Id,
                IsTask           = entry.IsTask,
                ActivityType     = entry.ActivityType,
                Subject          = entry.Subject,
                Description      = entry.Description,
                Duration         = entry.Duration,
                DueDate          = entry.DueDate.HasValue ? _currentTenantService.UtcToLocal(entry.DueDate.Value) : null,
                ActivityDate     = _currentTenantService.UtcToLocal(entry.ActivityDate),
                AssignedToUserId = entry.AssignedToUserId,
                Outcome          = entry.Outcome
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

        private static string Explain(Exception ex, string fallback)
            => string.IsNullOrWhiteSpace(ex.Message) || ex is NullReferenceException
                ? $"{fallback} Please try again."
                : ex.Message;

        // ==================== VIEW HELPERS ====================

        /// <summary>
        /// You can always edit your own entry. Editing someone else's is a
        /// tenant-admin action. Note this does NOT check IsEditableState:
        /// a closed deal still gets post-sale follow-up, and a Won deal has
        /// a handover. Only the deal record itself is locked.
        ///
        /// Entries created before the activity unification store a NAME in
        /// CreatedBy rather than an id, so they match nobody and are
        /// admin-only. That is the safe side to fail on.
        /// </summary>
        public bool CanEditEntry(ActivityDto a)
        {
            if (!CanUpdate) return false;
            if (IsTenantAdmin) return true;
            return string.Equals(a.CreatedBy, CurrentUserId.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Deleting history needs the Delete permission, not Update.</summary>
        public bool CanDeleteEntry(ActivityDto a)
        {
            if (!CanDelete) return false;
            if (IsTenantAdmin) return true;
            return string.Equals(a.CreatedBy, CurrentUserId.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        // ── 020: the transition bar ───────────────────────────────────

        /// <summary>Moves the process offers from here, blocked ones included.</summary>
        public List<AvailableTransitionDto> AvailableMoves =>
            Transitions?.Transitions ?? new List<AvailableTransitionDto>();

        /// <summary>The viewer may step outside the process.</summary>
        public bool CanOverrideStage => Transitions?.CanOverride == true && CanUpdate;

        /// <summary>
        /// Every stage the override picker can offer — all of them except
        /// the one the deal is already in, retired ones included, because
        /// putting a deal back where it belongs is exactly what the escape
        /// hatch is for.
        /// </summary>
        public List<StageLiteDto> OverrideTargets =>
            (Transitions?.AllStages ?? new List<StageLiteDto>())
                .Where(s => s.Key != Deal?.Stage)
                .ToList();

        /// <summary>
        /// The process offers nothing this person can do. Worth saying out
        /// loud rather than showing an empty strip.
        /// </summary>
        public bool IsStuck => AvailableMoves.Count == 0 || AvailableMoves.All(m => !m.IsAllowed);

        public string MoveButtonClass(AvailableTransitionDto m) => m.ToCategory switch
        {
            StageCategory.Won  => "btn-success",
            StageCategory.Lost => "btn-outline-danger",
            _                  => "btn-outline-primary"
        };

        public string MoveButtonIcon(AvailableTransitionDto m) => m.ToCategory switch
        {
            StageCategory.Won  => "bi-trophy",
            StageCategory.Lost => "bi-x-circle",
            _                  => "bi-arrow-right-circle"
        };

        public bool IsEditing(ActivityDto a) => EditId.HasValue && EditId.Value == a.Id;
        public bool IsAddingOutcome(ActivityDto a) => OutcomeId.HasValue && OutcomeId.Value == a.Id;

        // FormatDate / FormatDateTime / FormatCurrency come from
        // AuthorizedPageModel — single source of truth.

        public string GetRelativeTime(DateTime utcDateTime)
        {
            var localNow  = _currentTenantService.UtcToLocal(DateTime.UtcNow);
            var localThen = _currentTenantService.UtcToLocal(utcDateTime);
            var span      = localNow - localThen;

            // Tasks are shown by DUE date, which is usually ahead.
            if (span.TotalSeconds < -60)
            {
                var ahead = -span;
                if (ahead.TotalHours < 24) return $"in {(int)ahead.TotalHours}h";
                if (ahead.TotalDays < 30)  return $"in {(int)ahead.TotalDays}d";
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

        public string GetStageBadgeClass(string? stageKey)
        {
            var stage = Stages.FirstOrDefault(s => s.Key == stageKey);

            return stage?.Category switch
            {
                StageCategory.Won => "bg-success",
                StageCategory.Lost => "bg-danger",
                _ => "bg-secondary"
            };
        }


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
