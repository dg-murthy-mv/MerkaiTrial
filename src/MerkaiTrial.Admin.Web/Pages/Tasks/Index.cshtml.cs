// =====================================================================
// TASKS PAGE — "My Tasks"
// Location: MerkaiTrial.Admin.Web/Pages/Tasks/Index.cshtml.cs
//
// COMPLETE FILE — replaces the existing one.
//
// NEW: reassign a task to a colleague without opening the record. That
// is the action a manager takes on a Monday morning looking at someone
// else's overdue list, and having to open each lead to do it is what
// makes people give up and use a spreadsheet.
//
// PERMISSIONS — deliberately NOT a new module in ModuleCatalog.
//   Gated on leads.read OR deals.read, so every existing role gets it
//   with no permission-JSON migration. Per-record enforcement still
//   happens in the API.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Activities;
using MerkaiTrial.Application.Commands.Activities;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MerkaiTrial.Admin.Web.Pages.Tasks
{
    [Authorize]
    public class IndexModel : PageModel
    {
        private readonly IActivityService _activityService;
        private readonly ICurrentUserService _currentUserService;
        private readonly ICurrentTenantService _tenantService;
        private readonly IAuthorizationService _auth;
        private readonly ILogger<IndexModel> _logger;

        public IndexModel(
            IActivityService activityService,
            ICurrentUserService currentUserService,
            ICurrentTenantService tenantService,
            IAuthorizationService auth,
            ILogger<IndexModel> logger)
        {
            _activityService    = activityService;
            _currentUserService = currentUserService;
            _tenantService      = tenantService;
            _auth               = auth;
            _logger             = logger;
        }

        // ── Filters ───────────────────────────────────────────────────
        [BindProperty(SupportsGet = true)]
        public string Scope { get; set; } = "mine";     // "mine" | "all"

        [BindProperty(SupportsGet = true)]
        public int Days { get; set; } = 30;

        /// <summary>Which task's reassign dropdown is open.</summary>
        [BindProperty(SupportsGet = true)]
        public Guid? ReassignId { get; set; }

        [BindProperty]
        public string? NewAssigneeId { get; set; }

        // ── State ─────────────────────────────────────────────────────
        public bool IsTenantAdmin { get; private set; }
        public Guid CurrentUserId { get; private set; }
        public string TenantTimezone { get; private set; } = "";
        public List<AssigneeDto> Assignees { get; private set; } = new();

        public List<TaskGroup> Groups { get; private set; } = new();
        public int TotalCount   => Groups.Sum(g => g.Tasks.Count);
        public int OverdueCount => Groups.FirstOrDefault(g => g.Key == "overdue")?.Tasks.Count ?? 0;
        public int TodayCount   => Groups.FirstOrDefault(g => g.Key == "today")?.Tasks.Count ?? 0;

        [TempData] public string? SuccessMessage { get; set; }
        [TempData] public string? ErrorMessage { get; set; }

        public record TaskGroup(string Key, string Title, string BadgeClass, List<ActivityDto> Tasks);

        // =================================================================

        public async Task<IActionResult> OnGetAsync()
        {
            if (!await CanSeeTasksAsync()) return Forbid();

            try
            {
                var me = await _currentUserService.GetCurrentUserAsync();
                IsTenantAdmin  = me.IsTenantAdmin;
                CurrentUserId  = me.UserId;
                TenantTimezone = _tenantService.GetTimezone();

                var assignee = (IsTenantAdmin && Scope == "all")
                    ? null
                    : me.UserId.ToString();

                var tasksTask     = _activityService.GetUpcomingTasksAsync(
                    new GetUpcomingTasksQuery(me.TenantId, assignee, Math.Clamp(Days, 1, 90)));
                var assigneesTask = LoadAssigneesAsync();

                await Task.WhenAll(tasksTask, assigneesTask);

                Groups = BuildGroups(await tasksTask);
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load tasks");
                ErrorMessage = "Couldn't load your tasks. Please try again.";
                Groups = new();
                return Page();
            }
        }

        public async Task<IActionResult> OnPostCompleteAsync(Guid activityId)
        {
            if (!await CanSeeTasksAsync()) return Forbid();

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
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to complete task {ActivityId}", activityId);
                ErrorMessage = Explain(ex, "Couldn't complete that task.");
            }

            return RedirectToPage(new { Scope, Days });
        }

        public async Task<IActionResult> OnPostReassignAsync(Guid activityId)
        {
            if (!await CanSeeTasksAsync()) return Forbid();

            if (string.IsNullOrWhiteSpace(NewAssigneeId))
            {
                ErrorMessage = "Choose who this should go to.";
                return RedirectToPage(new { Scope, Days });
            }

            try
            {
                var tenantId      = _currentUserService.GetCurrentTenantId();
                var currentUserId = _currentUserService.GetCurrentUserId();

                await _activityService.ReassignAsync(new ReassignActivityDto(
                    TenantId: tenantId,
                    ActivityId: activityId,
                    AssignedToUserId: NewAssigneeId,
                    UpdatedBy: currentUserId.ToString()
                ));

                SuccessMessage = "Task reassigned.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reassign task {ActivityId}", activityId);
                ErrorMessage = Explain(ex, "Couldn't reassign that task.");
            }

            return RedirectToPage(new { Scope, Days });
        }

        // =================================================================
        // HELPERS
        // =================================================================

        private async Task<bool> CanSeeTasksAsync()
        {
            var leads = await _auth.AuthorizeAsync(User, "leads.read");
            if (leads.Succeeded) return true;

            var deals = await _auth.AuthorizeAsync(User, "deals.read");
            return deals.Succeeded;
        }

        private async Task LoadAssigneesAsync()
        {
            try { Assignees = await _activityService.GetAssigneesAsync(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load assignees");
                Assignees = new();
            }
        }

        private List<TaskGroup> BuildGroups(List<ActivityDto> tasks)
        {
            // Boundaries are the TENANT's local day, not the server's.
            var localNow    = _tenantService.UtcToLocal(DateTime.UtcNow);
            var todayEnd    = localNow.Date.AddDays(1);
            var tomorrowEnd = localNow.Date.AddDays(2);
            var weekEnd     = localNow.Date.AddDays(7);

            var overdue  = new List<ActivityDto>();
            var today    = new List<ActivityDto>();
            var tomorrow = new List<ActivityDto>();
            var thisWeek = new List<ActivityDto>();
            var later    = new List<ActivityDto>();

            foreach (var t in tasks.OrderBy(t => t.DueDate))
            {
                if (t.DueDate is null) { later.Add(t); continue; }

                var due = _tenantService.UtcToLocal(t.DueDate.Value);

                if (due < localNow)         overdue.Add(t);
                else if (due < todayEnd)    today.Add(t);
                else if (due < tomorrowEnd) tomorrow.Add(t);
                else if (due < weekEnd)     thisWeek.Add(t);
                else                        later.Add(t);
            }

            var groups = new List<TaskGroup>
            {
                new("overdue",  "Overdue",   "bg-danger",            overdue),
                new("today",    "Today",     "bg-warning text-dark", today),
                new("tomorrow", "Tomorrow",  "bg-info",              tomorrow),
                new("week",     "This week", "bg-primary",           thisWeek),
                new("later",    "Later",     "bg-secondary",         later),
            };

            return groups.Where(g => g.Tasks.Count > 0).ToList();
        }

        private static string Explain(Exception ex, string fallback)
            => string.IsNullOrWhiteSpace(ex.Message) || ex is NullReferenceException
                ? fallback
                : ex.Message;

        // ── View helpers ──────────────────────────────────────────────

        public bool IsReassigning(ActivityDto t) => ReassignId.HasValue && ReassignId.Value == t.Id;

        /// <summary>
        /// Giving away your own task is delegation; taking someone else's
        /// off them is a manager's action.
        /// </summary>
        public bool CanReassign(ActivityDto t) =>
            Assignees.Count > 1 &&
            (IsTenantAdmin ||
             string.Equals(t.AssignedToUserId, CurrentUserId.ToString(), StringComparison.OrdinalIgnoreCase));

        public string FormatDueDate(DateTime? dueUtc)
        {
            if (dueUtc is null) return "No due date";

            var localNow = _tenantService.UtcToLocal(DateTime.UtcNow);
            var localDue = _tenantService.UtcToLocal(dueUtc.Value);

            if (localDue.Date >= localNow.Date && localDue.Date < localNow.Date.AddDays(7))
                return localDue.ToString("ddd HH:mm");

            return _tenantService.FormatDateTime(dueUtc.Value);
        }

        public string GetDueDescription(DateTime? dueUtc)
        {
            if (dueUtc is null) return "";

            var localNow = _tenantService.UtcToLocal(DateTime.UtcNow);
            var localDue = _tenantService.UtcToLocal(dueUtc.Value);
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

        public string GetActivityIcon(string activityType) => ActivityType.Icon(activityType);

        public string RecordUrl(ActivityDto task) => task.EntityType switch
        {
            ActivityEntityType.Lead    => $"/Leads/Detail/{task.EntityId}",
            ActivityEntityType.Deal    => $"/Pipeline/Detail/{task.EntityId}",
            ActivityEntityType.Contact => $"/Contacts/Detail/{task.EntityId}",
            ActivityEntityType.Company => $"/Companies/Detail/{task.EntityId}",
            _ => "#"
        };

        public string RecordIcon(ActivityDto task) => task.EntityType switch
        {
            ActivityEntityType.Lead    => "bi-funnel",
            ActivityEntityType.Deal    => "bi-kanban",
            ActivityEntityType.Contact => "bi-person",
            ActivityEntityType.Company => "bi-building",
            _ => "bi-link-45deg"
        };

        public string RecordName(ActivityDto task) =>
            string.IsNullOrWhiteSpace(task.EntityName) ? task.EntityType : task.EntityName!;
    }
}
