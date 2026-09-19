// =====================================================================
// LEAD DETAIL - Backend
// Location: MerkaiTrial.Admin.Web/Pages/Leads/Detail.cshtml.cs
//
// COMPLETE FILE — replaces the existing one.
//
// NEW IN THIS PASS (task follow-through)
//   • Assign a task to a colleague — Assignees list + dropdown.
//   • Edit / reschedule a task, edit a logged activity. Inline, driven by
//     ?editId=<guid> rather than a modal per row: no JS, and the edit
//     survives a validation failure.
//   • Delete an entry. Needs Leads.Delete, not Leads.Update — erasing the
//     record of a call is a different act from adding to it.
//   • "How did it go?" — an outcome added AFTER completing, so completing
//     stays one click. A dialog on every completion would add friction to
//     the one action we most want repeated.
//   • Editing someone else's entry needs tenant admin. Correcting your own
//     typo is not the same as rewriting another rep's record.
//
// The API enforces all of this again; the flags here only decide which
// buttons render.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Activities;
using MerkaiTrial.Admin.Web.Services.Leads;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.Activities;
using MerkaiTrial.Application.Commands.LeadStatuses;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using System.ComponentModel.DataAnnotations;

namespace MerkaiTrial.Admin.Web.Pages.Leads
{
    public class DetailModel : AuthorizedPageModel
    {
        private readonly ILeadService _leadService;
        private readonly IActivityService _activityService;
        private readonly ICurrentUserService _currentUserService;
        private readonly ICurrentTenantService _tenantService;
        private readonly IAuthorizationService _authorizationService;
        private readonly ILogger<DetailModel> _logger;
        private readonly ILeadStatusService _statusService;

        protected override string ModuleName => Modules.Leads;

        public DetailModel(
         ILeadService leadService,
         IActivityService activityService,
         IAuthorizationService authorizationService,
         ICurrentUserService currentUserService,
         ICurrentTenantService tenantService,
         ILogger<DetailModel> logger,
         ILeadStatusService statuses)
            : base(authorizationService, currentUserService, logger)
        {
            _leadService = leadService;
            _activityService = activityService;
            _currentUserService = currentUserService;
            _statusService = statuses;
            _tenantService = tenantService;
            _authorizationService = authorizationService;
            _logger = logger;
        }

        // ── Lead data ─────────────────────────────────────────────────
        public LeadDetailDto Lead { get; set; } = null!;

        // ── Tenant context ────────────────────────────────────────────
        public string TenantCurrency { get; private set; } = "INR";
        public string TenantCurrencySymbol { get; private set; } = "₹";
        public string TenantDateFormat { get; private set; } = "dd/MM/yyyy";
        public string TenantTimezone { get; private set; } = "Asia/Kolkata";
        public List<AttachmentDto> Attachments { get; set; } = new();

        // ── Who is looking ────────────────────────────────────────────
        public Guid CurrentUserId { get; private set; }
        public bool IsTenantAdmin { get; private set; }

        /// <summary>Colleagues a task can be assigned to.</summary>
        public List<AssigneeDto> Assignees { get; private set; } = new();

        // ── Status ────────────────────────────────────────────────────
        [BindProperty]
        public string? NewStatus { get; set; }
        public List<SelectListItem> StatusOptions { get; set; } = new();
        public List<LeadStatusDto> Statuses { get; private set; } = new();

        // ── Notes ─────────────────────────────────────────────────────
        public List<LeadNoteDto> Notes { get; set; } = new();
        [BindProperty]
        public NoteInputModel NoteInput { get; set; } = new();

        // ── Unified Activities ────────────────────────────────────────
        public List<ActivityDto> AllActivities { get; set; } = new();

        /// <summary>Logged history — the Activities tab.</summary>
        public List<ActivityDto> Activities => AllActivities.Where(a => !a.IsTask).ToList();

        /// <summary>Scheduled work — the Tasks tab.</summary>
        public List<ActivityDto> Tasks => AllActivities.Where(a => a.IsTask).ToList();

        public List<ActivityDto> OpenTasks =>
            Tasks.Where(t => !t.IsCompleted).OrderBy(t => t.DueDate).ToList();

        public List<ActivityDto> CompletedTasks =>
            Tasks.Where(t => t.IsCompleted).OrderByDescending(t => t.CompletedAtUtc).ToList();

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

        
       

        // ── Timeline ──────────────────────────────────────────────────
        public List<TimelineItemDto> Timeline { get; set; } = new();

        [TempData] public string? SuccessMessage { get; set; }
        [TempData] public string? ErrorMessage { get; set; }

        // ── Business-rule state (NOT permission) ──────────────────────
        public bool IsEditableState => Lead != null && !Lead.IsConverted;
        public bool IsDeletableState => Lead != null && !Lead.IsConverted;

        // =====================================================================
        // INPUT MODELS
        // =====================================================================

        public class NoteInputModel
        {
            [Required(ErrorMessage = "Note text is required")]
            [StringLength(2000)]
            public string Note { get; set; } = string.Empty;
        }

        /// <summary>Logging something that already happened.</summary>
        public class ActivityInputModel
        {
            [Required(ErrorMessage = "Activity type is required")]
            public string ActivityType { get; set; } = string.Empty;

            [Required(ErrorMessage = "Subject is required")]
            [StringLength(200)]
            public string Subject { get; set; } = string.Empty;

            [StringLength(1000)]
            public string? Description { get; set; }

            [Range(1, 480)]
            public int? Duration { get; set; }

            /// <summary>Tenant-local; null = now.</summary>
            public DateTime? ActivityDate { get; set; }
        }

        /// <summary>Scheduling something to do.</summary>
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

        [BindProperty]
        public ConvertToDealInputModel ConvertToDealInput { get; set; } = new();

        public class ConvertToDealInputModel
        {
            [Required(ErrorMessage = "Deal title is required")]
            [StringLength(200)]
            public string DealTitle { get; set; } = string.Empty;

            public string? Description { get; set; }

            [Required(ErrorMessage = "Stage is required")]
            public string Stage { get; set; } = "Qualification";

            [Required(ErrorMessage = "Expected value is required")]
            [Range(0.01, double.MaxValue)]
            public decimal ExpectedValue { get; set; }

            public string Currency { get; set; } = string.Empty;

            [Required(ErrorMessage = "Expected close date is required")]
            [DataType(DataType.Date)]
            public DateTime ExpectedCloseDate { get; set; } = DateTime.Today.AddDays(30);
        }

        // =====================================================================
        // GET
        // =====================================================================

        public async Task<IActionResult> OnGetAsync(Guid id)
        {
            var permissionCheck = await ValidatePermissionAsync(Actions.Read);
            if (permissionCheck != null) return permissionCheck;

            await InitializePermissionsAsync();

            try
            {
                LoadTenantContext();

                var me = await _currentUserService.GetCurrentUserAsync();
                CurrentUserId = me.UserId;
                IsTenantAdmin = me.IsTenantAdmin;

                var tenantId = me.TenantId;
                Lead = await _leadService.GetByIdAsync(tenantId, id);

                await LoadStatusOptionsAsync();

                var notesTask       = LoadNotesAsync(tenantId, id);
                var activitiesTask  = LoadActivitiesAsync(tenantId, id);
                var timelineTask    = LoadTimelineAsync(tenantId, id);
                var attachmentsTask = LoadAttachmentsAsync(tenantId, id);
                var assigneesTask   = LoadAssigneesAsync();

                await Task.WhenAll(notesTask, activitiesTask, timelineTask, attachmentsTask, assigneesTask);

                if (TaskInput.DueDate == default)
                    TaskInput.DueDate = _tenantService.UtcToLocal(DateTime.UtcNow)
                                                      .Date.AddDays(1).AddHours(9);

                PrefillEditForm();
                InitializeConvertToDealInput();
                return Page();
            }
            catch (KeyNotFoundException)
            {
                TempData["ErrorMessage"] = "Lead not found.";
                return RedirectToPage("./Index");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading lead {Id}", id);
                TempData["ErrorMessage"] = "Failed to load lead. Please try again.";
                return RedirectToPage("./Index");
            }
        }
        private async Task LoadStatusOptionsAsync()
        {
            try
            {
                // selectableOnly: FALSE. The badge helper needs the system
                // and retired statuses too; the dropdown filters below.
                Statuses = await _statusService.GetAsync(selectableOnly: false);
            }
            catch (Exception ex)
            {
                // A missing list means no dropdown, not a broken page.
                _logger.LogWarning(ex, "Failed to load lead statuses");
                Statuses = new();
            }

            StatusOptions = Statuses
                .Where(s => s.IsActive && !s.IsSystem)
                .OrderBy(s => s.SortOrder)
                .Select(s => new SelectListItem
                {
                    Value = s.Key,
                    Text = s.Name,
                    Selected = s.Key == Lead.Status
                })
                .ToList();
        }
        // =====================================================================
        // NOTES / STATUS / ATTACHMENTS  (unchanged behaviour)
        // =====================================================================

        public async Task<IActionResult> OnPostChangeStatusAsync(Guid id, string status)
        {
            var permissionCheck = await ValidatePermissionAsync(Actions.Update);
            if (permissionCheck != null) return permissionCheck;

            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();
                await _leadService.UpdateStatusAsync(tenantId, id, status);

                // The client's word for it, not our key — "Site Visit
                // Booked", not "SiteVisitBooked". Nothing is loaded on a
                // POST, so fetch the list to resolve the name.
                var all = await _statusService.GetAsync(selectableOnly: false);
                var name = all.FirstOrDefault(s => s.Key == status)?.Name ?? status;

                TempData["SuccessMessage"] = $"Lead status changed to {name}.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error changing status for lead {Id}", id);
                TempData["ErrorMessage"] = Explain(ex, "Failed to change status.");
            }
            return RedirectToPage(new { id });
        }

        public async Task<IActionResult> OnPostAddNoteAsync(Guid id)
        {
            var permissionCheck = await ValidatePermissionAsync(Actions.Update);
            if (permissionCheck != null) return permissionCheck;

            try
            {
                ModelState.Clear();
                if (!TryValidateModel(NoteInput, nameof(NoteInput)))
                {
                    LogModelStateErrors();
                    await ReloadPageDataAsync(id);
                    return Page();
                }

                var tenantId = _currentUserService.GetCurrentTenantId();
                var currentUserId = _currentUserService.GetCurrentUserId();

                await _leadService.CreateNoteAsync(new CreateLeadNoteDto(
                    TenantId: tenantId,
                    LeadId: id,
                    Note: NoteInput.Note,
                    CreatedBy: currentUserId.ToString()
                ));

                TempData["SuccessMessage"] = "Note added successfully!";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding note to lead {Id}", id);
                TempData["ErrorMessage"] = Explain(ex, "Failed to add note.");
            }
            return RedirectToPage(new { id });
        }

        public async Task<IActionResult> OnPostDeleteNoteAsync(Guid id, Guid noteId)
        {
            var permissionCheck = await ValidatePermissionAsync(Actions.Update);
            if (permissionCheck != null) return permissionCheck;

            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();
                await _leadService.DeleteNoteAsync(tenantId, id, noteId);
                TempData["SuccessMessage"] = "Note deleted successfully!";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting note {NoteId}", noteId);
                TempData["ErrorMessage"] = Explain(ex, "Failed to delete note.");
            }
            return RedirectToPage(new { id });
        }

        // =====================================================================
        // LOG AN ACTIVITY  ("Log what happened")
        // =====================================================================

        public async Task<IActionResult> OnPostAddActivityAsync(Guid id)
        {
            var permissionCheck = await ValidatePermissionAsync(Actions.Update);
            if (permissionCheck != null) return permissionCheck;

            try
            {
                ModelState.Clear();
                if (!TryValidateModel(ActivityInput, nameof(ActivityInput)))
                {
                    LogModelStateErrors();
                    await ReloadPageDataAsync(id);
                    return Page();
                }

                var tenantId = _currentUserService.GetCurrentTenantId();
                var currentUserId = _currentUserService.GetCurrentUserId();

                DateTime? activityDateUtc = ActivityInput.ActivityDate.HasValue
                    ? _tenantService.LocalToUtc(ActivityInput.ActivityDate.Value)
                    : null;

                await _activityService.CreateAsync(new CreateActivityDto(
                    TenantId: tenantId,
                    EntityType: ActivityEntityType.Lead,
                    EntityId: id,
                    ActivityType: ActivityInput.ActivityType,
                    Subject: ActivityInput.Subject,
                    Description: ActivityInput.Description,
                    Duration: ActivityInput.Duration,
                    ActivityDate: activityDateUtc,
                    IsTask: false,
                    DueDate: null,
                    AssignedToUserId: currentUserId.ToString(),
                    CreatedBy: currentUserId.ToString()
                ));

                TempData["SuccessMessage"] = "Activity logged.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding activity to lead {Id}", id);
                TempData["ErrorMessage"] = Explain(ex, "Failed to log activity.");
            }
            return RedirectToPage(new { id });
        }

        // =====================================================================
        // PLAN A TASK  ("Plan what's next")
        // =====================================================================

        public async Task<IActionResult> OnPostAddTaskAsync(Guid id)
        {
            var permissionCheck = await ValidatePermissionAsync(Actions.Update);
            if (permissionCheck != null) return permissionCheck;

            try
            {
                ModelState.Clear();
                if (!TryValidateModel(TaskInput, nameof(TaskInput)))
                {
                    LogModelStateErrors();
                    await ReloadPageDataAsync(id);
                    return Page();
                }

                var tenantId = _currentUserService.GetCurrentTenantId();
                var currentUserId = _currentUserService.GetCurrentUserId();

                var assignee = string.IsNullOrWhiteSpace(TaskInput.AssignedToUserId)
                    ? currentUserId.ToString()
                    : TaskInput.AssignedToUserId;

                await _activityService.CreateAsync(new CreateActivityDto(
                    TenantId: tenantId,
                    EntityType: ActivityEntityType.Lead,
                    EntityId: id,
                    ActivityType: TaskInput.ActivityType,
                    Subject: TaskInput.Subject,
                    Description: TaskInput.Description,
                    Duration: null,
                    ActivityDate: null,
                    IsTask: true,
                    DueDate: _tenantService.LocalToUtc(TaskInput.DueDate),
                    AssignedToUserId: assignee,
                    CreatedBy: currentUserId.ToString()
                ));

                TempData["SuccessMessage"] =
                    assignee == currentUserId.ToString()
                        ? "Task created."
                        : "Task created and assigned.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding task to lead {Id}", id);
                TempData["ErrorMessage"] = Explain(ex, "Failed to create task.");
            }
            return RedirectToPage(new { id });
        }

        // =====================================================================
        // COMPLETE — one click, no dialog
        // =====================================================================

        public async Task<IActionResult> OnPostCompleteTaskAsync(Guid id, Guid activityId)
        {
            var permissionCheck = await ValidatePermissionAsync(Actions.Update);
            if (permissionCheck != null) return permissionCheck;

            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();
                var currentUserId = _currentUserService.GetCurrentUserId();

                await _activityService.CompleteAsync(new CompleteActivityDto(
                    TenantId: tenantId,
                    ActivityId: activityId,
                    Outcome: null,
                    CompletedBy: currentUserId.ToString()
                ));

                TempData["SuccessMessage"] = "Task done.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error completing task {ActivityId}", activityId);
                TempData["ErrorMessage"] = Explain(ex, "Failed to complete task.");
            }
            return RedirectToPage(new { id });
        }

        // =====================================================================
        // OUTCOME — "how did it go?", added afterwards
        // =====================================================================

        public async Task<IActionResult> OnPostSetOutcomeAsync(Guid id, Guid activityId)
        {
            var permissionCheck = await ValidatePermissionAsync(Actions.Update);
            if (permissionCheck != null) return permissionCheck;

            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();
                var currentUserId = _currentUserService.GetCurrentUserId();

                await _activityService.SetOutcomeAsync(new SetActivityOutcomeDto(
                    TenantId: tenantId,
                    ActivityId: activityId,
                    Outcome: OutcomeText,
                    UpdatedBy: currentUserId.ToString()
                ));

                TempData["SuccessMessage"] = "Outcome saved.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting outcome on {ActivityId}", activityId);
                TempData["ErrorMessage"] = Explain(ex, "Failed to save the outcome.");
            }
            return RedirectToPage(new { id });
        }

        // =====================================================================
        // EDIT / RESCHEDULE
        // =====================================================================

        public async Task<IActionResult> OnPostUpdateEntryAsync(Guid id)
        {
            var permissionCheck = await ValidatePermissionAsync(Actions.Update);
            if (permissionCheck != null) return permissionCheck;

            try
            {
                ModelState.Clear();
                if (!TryValidateModel(EditInput, nameof(EditInput)))
                {
                    LogModelStateErrors();
                    EditId = EditInput.ActivityId;   // keep the form open
                    await ReloadPageDataAsync(id);
                    return Page();
                }

                var tenantId = _currentUserService.GetCurrentTenantId();
                var currentUserId = _currentUserService.GetCurrentUserId();

                // Tasks carry a due date; logs carry the date it happened.
                DateTime? dueUtc = EditInput.IsTask && EditInput.DueDate.HasValue
                    ? _tenantService.LocalToUtc(EditInput.DueDate.Value)
                    : null;

                var whenUtc = EditInput.ActivityDate.HasValue
                    ? _tenantService.LocalToUtc(EditInput.ActivityDate.Value)
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

                TempData["SuccessMessage"] = EditInput.IsTask ? "Task updated." : "Activity updated.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating entry {ActivityId}", EditInput.ActivityId);
                TempData["ErrorMessage"] = Explain(ex, "Failed to save those changes.");
            }
            return RedirectToPage(new { id });
        }

        // =====================================================================
        // DELETE — needs Leads.Delete, not Update
        // =====================================================================

        public async Task<IActionResult> OnPostDeleteEntryAsync(Guid id, Guid activityId)
        {
            var permissionCheck = await ValidatePermissionAsync(Actions.Delete);
            if (permissionCheck != null) return permissionCheck;

            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();
                await _activityService.DeleteAsync(tenantId, activityId);
                TempData["SuccessMessage"] = "Entry deleted.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting entry {ActivityId}", activityId);
                TempData["ErrorMessage"] = Explain(ex, "Failed to delete that entry.");
            }
            return RedirectToPage(new { id });
        }

        // =====================================================================
        // CONVERT / DELETE LEAD / ATTACHMENTS
        // =====================================================================

        public async Task<IActionResult> OnPostConvertToDealAsync(Guid id)
        {
            var permissionCheck = await ValidatePermissionAsync(Actions.Update);
            if (permissionCheck != null) return permissionCheck;

            var canCreateDeal = await _authorizationService.AuthorizeAsync(User, "Deals.Create");
            if (!canCreateDeal.Succeeded)
            {
                TempData["ErrorMessage"] = "You don't have permission to create deals.";
                return RedirectToPage(new { id });
            }

            try
            {
                ModelState.Clear();
                if (!TryValidateModel(ConvertToDealInput, nameof(ConvertToDealInput)))
                {
                    LogModelStateErrors();
                    await ReloadPageDataAsync(id);
                    return Page();
                }

                var tenantId = _currentUserService.GetCurrentTenantId();
                var currentUser = await _currentUserService.GetCurrentUserAsync();

                // Lead is only populated by OnGetAsync, so on a POST it is
                // null — load it before checking status.
                var lead = await _leadService.GetByIdAsync(tenantId, id);

               

                var result = await _leadService.ConvertToDealAsync(new ConvertLeadToDealDto
                {
                    TenantId = tenantId,
                    LeadId = id,
                    DealTitle = ConvertToDealInput.DealTitle,
                    Description = ConvertToDealInput.Description,
                    Stage = ConvertToDealInput.Stage,
                    ExpectedValue = ConvertToDealInput.ExpectedValue,
                    Currency = ConvertToDealInput.Currency,
                    ExpectedCloseDateUtc = _tenantService.LocalToUtc(ConvertToDealInput.ExpectedCloseDate),
                    OwnerUserId = lead?.OwnerUserId,
                    ConvertedBy = currentUser.FullName
                });

                TempData["SuccessMessage"] = result.Message;
                return RedirectToPage("/Pipeline/Index");
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Conversion validation failed for lead {LeadId}", id);
                TempData["ErrorMessage"] = ex.Message;
                return RedirectToPage(new { id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to convert lead {LeadId} to deal", id);
                TempData["ErrorMessage"] = Explain(ex, "Failed to convert lead to deal.");
                return RedirectToPage(new { id });
            }
        }

        public async Task<IActionResult> OnPostDeleteAsync(Guid id)
        {
            var permissionCheck = await ValidatePermissionAsync(Actions.Delete);
            if (permissionCheck != null) return permissionCheck;

            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();

                var lead = await _leadService.GetByIdAsync(tenantId, id);
                if (lead?.IsConverted == true)
                {
                    TempData["ErrorMessage"] = "Converted leads cannot be deleted — they are part of the deal audit trail.";
                    return RedirectToPage("./Detail", new { id });
                }

                await _leadService.DeleteAsync(tenantId, id);
                TempData["SuccessMessage"] = "Lead deleted successfully!";
                return RedirectToPage("./Index");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting lead {Id}", id);
                TempData["ErrorMessage"] = Explain(ex, "Failed to delete lead.");
                return RedirectToPage("./Detail", new { id });
            }
        }

        public async Task<IActionResult> OnPostUploadAttachmentAsync(Guid id)
        {
            var permissionCheck = await ValidatePermissionAsync(Actions.Update);
            if (permissionCheck != null) return permissionCheck;

            var file = Request.Form.Files.FirstOrDefault();
            if (file == null || file.Length == 0)
            {
                ErrorMessage = "Please select a file to upload.";
                return RedirectToPage(new { id });
            }

            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();
                var uploadedBy = _currentUserService.GetCurrentUserId().ToString();

                await _leadService.UploadAttachmentAsync(tenantId, id, file, uploadedBy);

                SuccessMessage = $"'{file.FileName}' uploaded successfully.";
            }
            catch (InvalidOperationException ex)
            {
                ErrorMessage = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to upload attachment for lead {LeadId}", id);
                ErrorMessage = Explain(ex, "Upload failed.");
            }
            return RedirectToPage(new { id });
        }

        public async Task<IActionResult> OnPostDeleteAttachmentAsync(Guid id, Guid attachmentId)
        {
            var permissionCheck = await ValidatePermissionAsync(Actions.Update);
            if (permissionCheck != null) return permissionCheck;

            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();
                await _leadService.DeleteAttachmentAsync(tenantId, attachmentId);
                SuccessMessage = "Attachment deleted.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete attachment {AttachmentId}", attachmentId);
                ErrorMessage = Explain(ex, "Delete failed.");
            }

            return RedirectToPage(new { id });
        }

        // =====================================================================
        // PRIVATE HELPERS
        // =====================================================================

        private void LoadTenantContext()
        {
            TenantCurrency = _tenantService.GetCurrencyCode();
            TenantCurrencySymbol = _tenantService.GetCurrencySymbol();
            TenantDateFormat = _tenantService.GetDateFormat();
            TenantTimezone = _tenantService.GetTimezone();
        }

        

        private async Task LoadNotesAsync(Guid tenantId, Guid leadId)
        {
            try { Notes = await _leadService.GetNotesAsync(tenantId, leadId); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to load notes for lead {Id}", leadId); Notes = new(); }
        }

        private async Task LoadActivitiesAsync(Guid tenantId, Guid leadId)
        {
            try
            {
                AllActivities = await _activityService.GetForEntityAsync(
                    new GetActivitiesQuery(tenantId, ActivityEntityType.Lead, leadId));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load activities for lead {Id}", leadId);
                AllActivities = new();
            }
        }

        private async Task LoadAssigneesAsync()
        {
            try { Assignees = await _activityService.GetAssigneesAsync(); }
            catch (Exception ex)
            {
                // A missing list just means no dropdown — the task still
                // gets created, assigned to the person creating it.
                _logger.LogWarning(ex, "Failed to load assignees");
                Assignees = new();
            }
        }

        private async Task LoadTimelineAsync(Guid tenantId, Guid leadId)
        {
            try { Timeline = await _leadService.GetTimelineAsync(tenantId, leadId); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to load timeline for lead {Id}", leadId); Timeline = new(); }
        }

        private async Task LoadAttachmentsAsync(Guid tenantId, Guid leadId)
        {
            try { Attachments = await _leadService.GetAttachmentsAsync(tenantId, leadId); }
            catch (Exception ex) { _logger.LogError(ex, "Failed to load attachments for lead {LeadId}", leadId); Attachments = new(); }
        }

        private async Task ReloadPageDataAsync(Guid id)
        {
            LoadTenantContext();

            var me = await _currentUserService.GetCurrentUserAsync();
            CurrentUserId = me.UserId;
            IsTenantAdmin = me.IsTenantAdmin;

            var tenantId = me.TenantId;
            Lead = await _leadService.GetByIdAsync(tenantId, id);

            await LoadStatusOptionsAsync();

            await Task.WhenAll(
                LoadNotesAsync(tenantId, id),
                LoadActivitiesAsync(tenantId, id),
                LoadTimelineAsync(tenantId, id),
                LoadAttachmentsAsync(tenantId, id),
                LoadAssigneesAsync());
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
                DueDate          = entry.DueDate.HasValue ? _tenantService.UtcToLocal(entry.DueDate.Value) : null,
                ActivityDate     = _tenantService.UtcToLocal(entry.ActivityDate),
                AssignedToUserId = entry.AssignedToUserId,
                Outcome          = entry.Outcome
            };
        }

        private void InitializeConvertToDealInput()
        {
            ConvertToDealInput = new ConvertToDealInputModel
            {
                DealTitle = $"Deal - {Lead.FullName}",
                Stage = "Qualification",
                ExpectedValue = Lead.ExpectedValue,
                Currency = !string.IsNullOrEmpty(Lead.Currency) ? Lead.Currency : TenantCurrency,
                ExpectedCloseDate = DateTime.Today.AddDays(30)
            };
        }

        private void LogModelStateErrors()
        {
            foreach (var kv in ModelState.Where(k => k.Value?.Errors.Count > 0))
                foreach (var err in kv.Value!.Errors)
                    _logger.LogWarning("ModelState error. Key={Key} Error={Error}", kv.Key, err.ErrorMessage);
        }

        private static string Explain(Exception ex, string fallback)
            => string.IsNullOrWhiteSpace(ex.Message) || ex is NullReferenceException
                ? $"{fallback} Please try again."
                : ex.Message;

        // =====================================================================
        // VIEW HELPERS
        // =====================================================================

        /// <summary>
        /// You can always edit your own entry. Editing someone else's is a
        /// tenant-admin action — correcting your own typo isn't the same as
        /// rewriting another rep's record of what they did.
        /// Older rows store a NAME in CreatedBy rather than an id; those are
        /// admin-only, which is the safe side to fail on.
        /// </summary>
        public bool CanEditEntry(ActivityDto a)
        {
            if (!CanUpdate || !IsEditableState) return false;
            if (IsTenantAdmin) return true;
            return string.Equals(a.CreatedBy, CurrentUserId.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Deleting history needs the Delete permission, not Update.</summary>
        public bool CanDeleteEntry(ActivityDto a)
        {
            if (!CanDelete || !IsEditableState) return false;
            if (IsTenantAdmin) return true;
            return string.Equals(a.CreatedBy, CurrentUserId.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        public bool IsEditing(ActivityDto a) => EditId.HasValue && EditId.Value == a.Id;
        public bool IsAddingOutcome(ActivityDto a) => OutcomeId.HasValue && OutcomeId.Value == a.Id;

        public string FormatDate(DateTime utcDateTime)
            => _tenantService.FormatDate(utcDateTime);

        public string FormatDateTime(DateTime utcDateTime)
            => _tenantService.FormatDateTime(utcDateTime);

        public string GetRelativeTime(DateTime utcDateTime)
        {
            var localNow = _tenantService.UtcToLocal(DateTime.UtcNow);
            var localTime = _tenantService.UtcToLocal(utcDateTime);
            var timeSpan = localNow - localTime;

            // The timeline shows tasks by DUE date, which is usually ahead.
            if (timeSpan.TotalSeconds < -60)
            {
                var ahead = -timeSpan;
                if (ahead.TotalHours < 24) return $"in {(int)ahead.TotalHours}h";
                if (ahead.TotalDays < 30)  return $"in {(int)ahead.TotalDays}d";
                return FormatDate(utcDateTime);
            }

            if (timeSpan.TotalMinutes < 1) return "just now";
            if (timeSpan.TotalMinutes < 60) return $"{(int)timeSpan.TotalMinutes}m ago";
            if (timeSpan.TotalHours < 24) return $"{(int)timeSpan.TotalHours}h ago";
            if (timeSpan.TotalDays < 7) return $"{(int)timeSpan.TotalDays}d ago";
            if (timeSpan.TotalDays < 30) return $"{(int)(timeSpan.TotalDays / 7)}w ago";
            return FormatDate(utcDateTime);
        }

        public string GetDueDescription(DateTime? dueUtc)
        {
            if (dueUtc is null) return "";

            var localNow = _tenantService.UtcToLocal(DateTime.UtcNow);
            var localDue = _tenantService.UtcToLocal(dueUtc.Value);
            var span = localDue - localNow;

            if (span.TotalSeconds < 0)
            {
                var overdue = -span;
                if (overdue.TotalHours < 1) return $"{(int)overdue.TotalMinutes}m overdue";
                if (overdue.TotalHours < 24) return $"{(int)overdue.TotalHours}h overdue";
                return $"{(int)overdue.TotalDays}d overdue";
            }

            if (span.TotalMinutes < 60) return $"in {(int)span.TotalMinutes}m";
            if (span.TotalHours < 24) return $"in {(int)span.TotalHours}h";
            return $"in {(int)span.TotalDays}d";
        }

        public bool IsOverdue(ActivityDto task)
            => task.IsTask && !task.IsCompleted && task.DueDate.HasValue
               && task.DueDate.Value < DateTime.UtcNow;

        public string FormatCurrency(decimal amount)
            => _tenantService.FormatCurrency(amount);

        public string GetStatusBadgeClass(string statusKey)
        {
            var s = Statuses.FirstOrDefault(x => x.Key == statusKey);

            return s?.Category switch
            {
                LeadStatusCategory.Qualified => "bg-success",
                LeadStatusCategory.Disqualified => "bg-secondary",
                LeadStatusCategory.Converted => "bg-warning text-dark",
                _ => "bg-primary"
            };
        }


        public string GetScoreColor(int score)
        {
            if (score >= 75) return "bg-success";
            if (score >= 50) return "bg-warning";
            if (score >= 25) return "bg-info";
            return "bg-secondary";
        }

        public string GetActivityIcon(string activityType) => ActivityType.Icon(activityType);
    }
}
