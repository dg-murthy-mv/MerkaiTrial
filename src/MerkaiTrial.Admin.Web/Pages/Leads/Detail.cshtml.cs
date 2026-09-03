// =====================================================================
// LEAD DETAIL - Backend
// Location: MerkaiTrial.Admin.Web/Pages/Leads/Detail.cshtml.cs
//
// MIGRATION (this pass):
//   1. Base class AppPageModel -> AuthorizedPageModel
//   2. OnGetAsync now enforces Leads.Read via ValidatePermissionAsync
//      before loading the lead (previously: no permission check at all —
//      anyone could open any lead by URL regardless of role)
//   3. CRITICAL FIX: OnPostDeleteAsync had NO permission check whatsoever
//      before this pass — only the business-rule "not converted" guard.
//      Any authenticated user (including viewer) could POST to this
//      handler directly and delete a non-converted lead. Now gated with
//      ValidatePermissionAsync(Actions.Delete), same as Index's delete
//      handler always was.
//   4. RENAMED to avoid collision with AuthorizedPageModel's own
//      permission properties of the same name:
//        CanEdit   -> IsEditableState   (business rule: not converted)
//        CanDelete -> IsDeletableState  (business rule: not converted)
//      These are NOT permission checks — they reflect record state.
//      The view's actual buttons should now check BOTH, e.g.:
//        @if (Model.CanDelete && Model.IsDeletableState) { ...delete button... }
//      where Model.CanDelete (inherited) is the permission check and
//      Model.IsDeletableState is the "not converted" business lock.
//
//   NOT YET ADDED IN THIS PASS (flagging for a decision, not guessing):
//   OnPostChangeStatusAsync, OnPostAddNoteAsync, OnPostAddActivityAsync,
//   OnPostAddReminderAsync, OnPostCompleteReminderAsync,
//   OnPostConvertToDealAsync, OnPostUploadAttachmentAsync,
//   OnPostDeleteAttachmentAsync still have NO permission checks at all.
//   These need an explicit call on what permission each maps to
//   (e.g. does adding a note require Leads.Update, or is that too
//   restrictive for a role that can only read/create? Does converting to
//   a deal need Deals.Create on top of Leads.Update?) before I add gates,
//   rather than assume and risk locking out a workflow you rely on.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Activities;
using MerkaiTrial.Admin.Web.Services.Leads;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.Activities;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
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
        private readonly IActivityService _activityService;  // unified activities
        private readonly ICurrentUserService _currentUserService;
        private readonly ICurrentTenantService _tenantService;
        private readonly ILogger<DetailModel> _logger;

        protected override string ModuleName => Modules.Leads;

        public DetailModel(
         ILeadService leadService,
         IActivityService activityService,
         IAuthorizationService authorizationService,
         ICurrentUserService currentUserService,
         ICurrentTenantService tenantService,
         ILogger<DetailModel> logger)
            : base(authorizationService, currentUserService, logger)
        {
            _leadService = leadService;
            _activityService = activityService;
            _currentUserService = currentUserService;
            _tenantService = tenantService;
            _logger = logger;
        }

        // ── Lead data ─────────────────────────────────────────────────
        public LeadDetailDto Lead { get; set; } = null!;

        // ── Tenant context (exposed to view — no hardcoding in cshtml) ─
        public string TenantCurrency { get; private set; } = "INR";
        public string TenantCurrencySymbol { get; private set; } = "₹";
        public string TenantDateFormat { get; private set; } = "dd/MM/yyyy";
        public string TenantTimezone { get; private set; } = "Asia/Kolkata";
        public List<AttachmentDto> Attachments { get; set; } = new();
        // ── Status ────────────────────────────────────────────────────
        [BindProperty]
        public LeadStatus? NewStatus { get; set; }
        public List<SelectListItem> StatusOptions { get; set; } = new();

        // ── Notes (kept via ILeadService — backward compat) ───────────
        public List<LeadNoteDto> Notes { get; set; } = new();
        [BindProperty]
        public NoteInputModel NoteInput { get; set; } = new();

        // ── Unified Activities (replaces LeadActivityDto list) ─────────
        public List<ActivityDto> Activities { get; set; } = new();
        [BindProperty]
        public ActivityInputModel ActivityInput { get; set; } = new();

        // ── Reminders (kept via ILeadService — backward compat) ────────
        public List<LeadReminderDto> Reminders { get; set; } = new();
        [BindProperty]
        public ReminderInputModel ReminderInput { get; set; } = new();

        // ── Timeline (unified) ────────────────────────────────────────
        public List<TimelineItemDto> Timeline { get; set; } = new();

        [TempData] public string? SuccessMessage { get; set; }
        [TempData] public string? ErrorMessage { get; set; }

        // ── Level 1: UI lock properties (business-rule state, NOT permission) ──
        // Converted leads are read-only — part of the financial audit trail.
        // RENAMED from CanEdit/CanDelete to avoid colliding with
        // AuthorizedPageModel's permission-based CanUpdate/CanDelete properties.
        // View buttons should check BOTH permission and state, e.g.:
        //   @if (Model.CanDelete && Model.IsDeletableState) { ... }
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

            public DateTime? ActivityDate { get; set; }

            // Task support — IsTask=true means future task with DueDate
            public bool IsTask { get; set; }
            public DateTime? DueDate { get; set; }
        }

        public class ReminderInputModel
        {
            [Required(ErrorMessage = "Title is required")]
            [StringLength(200)]
            public string Title { get; set; } = string.Empty;

            [StringLength(1000)]
            public string? Description { get; set; }

            [Required(ErrorMessage = "Reminder date is required")]
            public DateTime ReminderDate { get; set; }
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
            // ✅ Check READ permission before loading anything
            var permissionCheck = await ValidatePermissionAsync(Actions.Read);
            if (permissionCheck != null) return permissionCheck;

            // Initialize permissions for UI buttons (Edit/Delete gating)
            await InitializePermissionsAsync();

            try
            {
                LoadTenantContext();

                var tenantId = _currentUserService.GetCurrentTenantId();
                Lead = await _leadService.GetByIdAsync(tenantId, id);

                LoadStatusOptions();

                // ── PERF: these five loads are independent of one another.
                // They used to be awaited one at a time, which serialised five
                // WebApi round-trips: the server log showed each call starting
                // at the exact millisecond the previous one finished
                // (~8.5s of the page's 11.8s). Each method assigns only its own
                // property and swallows its own exception, so running them
                // concurrently is safe. Pipeline/Detail already uses this
                // pattern. ApiService is stateless and the tenant/user headers
                // are attached per-request by TenantContextForwardingHandler,
                // so concurrent calls do not race.
                var notesTask       = LoadNotesAsync(tenantId, id);
                var activitiesTask  = LoadActivitiesAsync(tenantId, id);
                var remindersTask   = LoadRemindersAsync(tenantId, id);
                var timelineTask    = LoadTimelineAsync(tenantId, id);
                var attachmentsTask = LoadAttachmentsAsync(tenantId, id);

                await Task.WhenAll(
                    notesTask, activitiesTask, remindersTask, timelineTask, attachmentsTask);

                if (ReminderInput.ReminderDate == default)
                    ReminderInput.ReminderDate = DateTime.Now.AddMinutes(30);

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

        // =====================================================================
        // POST HANDLERS
        // =====================================================================

        public async Task<IActionResult> OnPostChangeStatusAsync(Guid id, LeadStatus status)
        {
            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();
                await _leadService.UpdateStatusAsync(tenantId, id, status);
                TempData["SuccessMessage"] = $"Lead status changed to {status} successfully!";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error changing status for lead {Id}", id);
                TempData["ErrorMessage"] = "Failed to change status. Please try again.";
            }
            return RedirectToPage(new { id });
        }

        public async Task<IActionResult> OnPostAddNoteAsync(Guid id)
        {
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
                TempData["ErrorMessage"] = "Failed to add note. Please try again.";
            }
            return RedirectToPage(new { id });
        }

        public async Task<IActionResult> OnPostDeleteNoteAsync(Guid id, Guid noteId)
        {
            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();
                await _leadService.DeleteNoteAsync(tenantId, id, noteId);
                TempData["SuccessMessage"] = "Note deleted successfully!";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting note {NoteId}", noteId);
                TempData["ErrorMessage"] = "Failed to delete note. Please try again.";
            }
            return RedirectToPage(new { id });
        }

        public async Task<IActionResult> OnPostAddActivityAsync(Guid id)
        {
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

                await _activityService.CreateAsync(new CreateActivityDto(
                    TenantId: tenantId,
                    EntityType: ActivityEntityType.Lead,
                    EntityId: id,
                    ActivityType: ActivityInput.ActivityType,
                    Subject: ActivityInput.Subject,
                    Description: ActivityInput.Description,
                    Duration: ActivityInput.Duration,
                    ActivityDate: ActivityInput.ActivityDate,
                    IsTask: ActivityInput.IsTask,
                    DueDate: ActivityInput.DueDate,
                    AssignedToUserId: currentUserId.ToString(),
                    CreatedBy: currentUserId.ToString()
                ));

                TempData["SuccessMessage"] = ActivityInput.IsTask
                    ? "Task created successfully!"
                    : "Activity logged successfully!";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding activity to lead {Id}", id);
                TempData["ErrorMessage"] = "Failed to log activity. Please try again.";
            }
            return RedirectToPage(new { id });
        }

        public async Task<IActionResult> OnPostAddReminderAsync(Guid id)
        {
            try
            {
                ModelState.Clear();
                if (!TryValidateModel(ReminderInput, nameof(ReminderInput)))
                {
                    LogModelStateErrors();
                    await ReloadPageDataAsync(id);
                    return Page();
                }

                var tenantId = _currentUserService.GetCurrentTenantId();
                var currentUserId = _currentUserService.GetCurrentUserId();

                await _leadService.CreateReminderAsync(new CreateLeadReminderDto(
                    TenantId: tenantId,
                    LeadId: id,
                    Title: ReminderInput.Title,
                    Description: ReminderInput.Description,
                    ReminderDate: ReminderInput.ReminderDate,
                    CreatedBy: currentUserId.ToString()
                ));

                TempData["SuccessMessage"] = "Reminder created successfully!";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding reminder to lead {Id}", id);
                TempData["ErrorMessage"] = "Failed to create reminder. Please try again.";
            }
            return RedirectToPage(new { id });
        }

        public async Task<IActionResult> OnPostCompleteReminderAsync(Guid id, Guid reminderId)
        {
            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();
                await _leadService.CompleteReminderAsync(tenantId, reminderId);
                TempData["SuccessMessage"] = "Reminder marked as complete!";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error completing reminder {ReminderId}", reminderId);
                TempData["ErrorMessage"] = "Failed to complete reminder. Please try again.";
            }
            return RedirectToPage(new { id });
        }

        public async Task<IActionResult> OnPostConvertToDealAsync(Guid id)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    await ReloadPageDataAsync(id);
                    return Page();
                }

                var tenantId = _currentUserService.GetCurrentTenantId();
                var currentUser = await _currentUserService.GetCurrentUserAsync();

                if (!string.Equals((Lead?.Status ?? "").Trim(), "Qualified", StringComparison.OrdinalIgnoreCase))
                {
                    TempData["ErrorMessage"] = "Only qualified leads can be converted to deals.";
                    return RedirectToPage(new { id });
                }

                var result = await _leadService.ConvertToDealAsync(new ConvertLeadToDealDto
                {
                    TenantId = tenantId,
                    LeadId = id,
                    DealTitle = ConvertToDealInput.DealTitle,
                    Description = ConvertToDealInput.Description,
                    Stage = ConvertToDealInput.Stage,
                    ExpectedValue = ConvertToDealInput.ExpectedValue,
                    Currency = ConvertToDealInput.Currency,
                    ExpectedCloseDateUtc = ConvertToDealInput.ExpectedCloseDate.ToUniversalTime(),
                    OwnerUserId = Lead?.OwnerUserId,
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
                TempData["ErrorMessage"] = "Failed to convert lead to deal. Please try again.";
                return RedirectToPage(new { id });
            }
        }

        public async Task<IActionResult> OnPostDeleteAsync(Guid id)
        {
            // ✅ CRITICAL FIX: this handler previously had NO permission check
            // at all — only the "not converted" business-rule guard below.
            // Any authenticated user could delete a non-converted lead via
            // direct POST regardless of role. Now gated the same way
            // Index's delete handler always was.
            var permissionCheck = await ValidatePermissionAsync(Actions.Delete);
            if (permissionCheck != null) return permissionCheck;

            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();

                // Level 2: backend guard — reject even if UI is bypassed
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
                TempData["ErrorMessage"] = "Failed to delete lead. Please try again.";
                return RedirectToPage("./Detail", new { id });
            }
        }

        public async Task<IActionResult> OnPostUploadAttachmentAsync(Guid id)
        {
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
            catch (InvalidOperationException ex) // file type/size
            {
                ErrorMessage = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to upload attachment for lead {LeadId}", id);
                ErrorMessage = "Upload failed. Please try again.";

            }
            return RedirectToPage(new { id });
        }
        public async Task<IActionResult> OnPostDeleteAttachmentAsync(Guid id, Guid attachmentId)
        {
            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();
                await _leadService.DeleteAttachmentAsync(tenantId, attachmentId);
                SuccessMessage = "Attachment deleted.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete attachment {AttachmentId}", attachmentId);
                ErrorMessage = "Delete failed.";

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

        private void LoadStatusOptions()
        {
            StatusOptions = Enum.GetValues<LeadStatus>()
                .Select(s => new SelectListItem
                {
                    Value = ((int)s).ToString(),
                    Text = s.ToString(),
                    Selected = s.ToString() == Lead.Status
                })
                .ToList();
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
                Activities = await _activityService.GetForEntityAsync(
                    new GetActivitiesQuery(tenantId, ActivityEntityType.Lead, leadId));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load activities for lead {Id}", leadId);
                Activities = new();
            }
        }

        private async Task LoadRemindersAsync(Guid tenantId, Guid leadId)
        {
            try { Reminders = await _leadService.GetRemindersAsync(tenantId, leadId); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to load reminders for lead {Id}", leadId); Reminders = new(); }
        }

        private async Task LoadTimelineAsync(Guid tenantId, Guid leadId)
        {
            try { Timeline = await _leadService.GetTimelineAsync(tenantId, leadId); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to load timeline for lead {Id}", leadId); Timeline = new(); }
        }

        // Extracted from OnGetAsync so it can join the Task.WhenAll batch above.
        // Behaviour is unchanged: failure logs and leaves an empty list rather
        // than failing the whole page.
        private async Task LoadAttachmentsAsync(Guid tenantId, Guid leadId)
        {
            try { Attachments = await _leadService.GetAttachmentsAsync(tenantId, leadId); }
            catch (Exception ex) { _logger.LogError(ex, "Failed to load attachments for lead {LeadId}", leadId); Attachments = new(); }
        }

        private async Task ReloadPageDataAsync(Guid id)
        {
            LoadTenantContext();
            var tenantId = _currentUserService.GetCurrentTenantId();
            Lead = await _leadService.GetByIdAsync(tenantId, id);
            LoadStatusOptions();
            await LoadNotesAsync(tenantId, id);
            await LoadActivitiesAsync(tenantId, id);
            await LoadRemindersAsync(tenantId, id);
            await LoadTimelineAsync(tenantId, id);
        }

        private void InitializeConvertToDealInput()
        {
            ConvertToDealInput = new ConvertToDealInputModel
            {
                DealTitle = $"Deal - {Lead.FullName}",
                Stage = "Qualification",
                ExpectedValue = Lead.ExpectedValue,
                Currency = !string.IsNullOrEmpty(Lead.Currency)
                                      ? Lead.Currency
                                      : TenantCurrency,
                ExpectedCloseDate = DateTime.Today.AddDays(30)
            };
        }

        private void LogModelStateErrors()
        {
            foreach (var kv in ModelState.Where(k => k.Value?.Errors.Count > 0))
                foreach (var err in kv.Value!.Errors)
                    _logger.LogWarning("ModelState error. Key={Key} Error={Error}", kv.Key, err.ErrorMessage);
        }

        // =====================================================================
        // VIEW HELPER METHODS
        // =====================================================================

        public string FormatDate(DateTime utcDateTime)
            => _tenantService.FormatDate(utcDateTime);

        public string FormatDateTime(DateTime utcDateTime)
            => _tenantService.FormatDateTime(utcDateTime);

        public string GetRelativeTime(DateTime utcDateTime)
        {
            var localNow = _tenantService.UtcToLocal(DateTime.UtcNow);
            var localTime = _tenantService.UtcToLocal(utcDateTime);
            var timeSpan = localNow - localTime;

            if (timeSpan.TotalMinutes < 1) return "just now";
            if (timeSpan.TotalMinutes < 60) return $"{(int)timeSpan.TotalMinutes}m ago";
            if (timeSpan.TotalHours < 24) return $"{(int)timeSpan.TotalHours}h ago";
            if (timeSpan.TotalDays < 7) return $"{(int)timeSpan.TotalDays}d ago";
            if (timeSpan.TotalDays < 30) return $"{(int)(timeSpan.TotalDays / 7)}w ago";
            return FormatDate(utcDateTime);
        }

        public string FormatCurrency(decimal amount)
            => _tenantService.FormatCurrency(amount);

        public string GetStatusBadgeClass(string status) => status switch
        {
            "New" => "bg-primary",
            "Contacted" => "bg-info",
            "Qualified" => "bg-success",
            "Unqualified" => "bg-secondary",
            "Converted" => "bg-warning text-dark",
            _ => "bg-dark"
        };

        public string GetScoreColor(int score)
        {
            if (score >= 75) return "bg-success";
            if (score >= 50) return "bg-warning";
            if (score >= 25) return "bg-info";
            return "bg-secondary";
        }

        public string GetActivityIcon(string activityType) => activityType switch
        {
            "Call" => "bi-telephone",
            "Email" => "bi-envelope",
            "Meeting" => "bi-calendar-event",
            "SMS" => "bi-chat",
            "WhatsApp" => "bi-whatsapp",
            "Task" => "bi-check-square",
            "Note" => "bi-sticky",
            _ => "bi-activity"
        };
    }
}
