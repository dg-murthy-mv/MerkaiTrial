// =====================================================================
// Settings → Automation → a rule — the When / If / Then editor
// Location: MerkaiTrial.Admin.Web/Pages/Settings/Automation/Edit.cshtml.cs
//
// NEW FILE (098 — workflow automation).
//
//   GET  /Settings/Automation/Edit                 a new, empty rule
//   GET  /Settings/Automation/Edit?template=won    a new rule from a starter
//                                                  (099: "quiet" — a time-based one)
//   GET  /Settings/Automation/Edit/{id}            an existing rule
//   POST                                           save (RuleJson)
//
// THE RULE TRAVELS AS ONE JSON FIELD
//   The editor is built in the browser from the workspace's own lists
//   (statuses, stages, people, custom fields), and a rule's lines are
//   lists of differently-shaped things. One hidden field holding the
//   whole rule — the same AutomationRuleDto the API takes — is far easier
//   to keep right than a nested form binding. The SERVER validates it all
//   (AutomationRuleCheck); the editor's limits are a convenience.
//
//   After a refused save the page re-renders from the posted JSON, so
//   nothing typed is lost, with every problem listed at the top.
// =====================================================================

using System.Text.Json;
using MerkaiTrial.Admin.Web.Services.Automation;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.Automation;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.Admin.Web.Pages.Settings.Automation
{
    public class EditModel : AuthorizedPageModel
    {
        private readonly IAutomationService _automation;
        private readonly ILogger<EditModel> _logger;

        protected override string ModuleName => Modules.Automation;

        public EditModel(
            IAutomationService automation,
            ICurrentUserService currentUserService,
            IAuthorizationService authorizationService,
            ILogger<EditModel> logger)
            : base(authorizationService, currentUserService, logger)
        {
            _automation = automation;
            _logger = logger;
        }

        [BindProperty(SupportsGet = true)] public Guid? Id { get; set; }
        [BindProperty(SupportsGet = true)] public string? Template { get; set; }

        /// <summary>The whole rule as JSON — read and written by the editor's script.</summary>
        [BindProperty] public string RuleJson { get; set; } = "{}";

        public string OptionsJson { get; private set; } = "{}";
        public AutomationOptionsDto? Options { get; private set; }
        public bool IsNew => Id is null;
        public string? SaveError { get; private set; }
        public int RunCount { get; private set; }

        [TempData] public string? SuccessMessage { get; set; }
        [TempData] public string? ErrorMessage { get; set; }

        private static readonly JsonSerializerOptions Json = AutomationJson.Options;

        public async Task<IActionResult> OnGetAsync()
        {
            var check = await ValidatePermissionAsync(IsNew ? Actions.Create : Actions.Update);
            if (check != null) return check;
            await InitializePermissionsAsync();

            if (!await LoadOptionsAsync()) return RedirectToPage("./Index");

            AutomationRuleDto rule;
            if (Id is { } id)
            {
                try
                {
                    rule = await _automation.GetRuleAsync(id);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Automation rule {RuleId} could not be opened", id);
                    ErrorMessage = "That rule no longer exists.";
                    return RedirectToPage("./Index");
                }
                RunCount = rule.RunCount;
            }
            else
            {
                rule = Starter(Template, Options!);
            }

            RuleJson = JsonSerializer.Serialize(rule, Json);
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            var check = await ValidatePermissionAsync(IsNew ? Actions.Create : Actions.Update);
            if (check != null) return check;
            await InitializePermissionsAsync();

            AutomationRuleDto? rule = null;
            try { rule = JsonSerializer.Deserialize<AutomationRuleDto>(RuleJson ?? "{}", Json); }
            catch (JsonException) { }

            if (rule is null)
            {
                SaveError = "The rule couldn't be read. Please reload the page and try again.";
                await LoadOptionsAsync();
                return Page();
            }

            try
            {
                var savedId = await _automation.SaveAsync(Id, rule);
                SuccessMessage = rule.IsActive
                    ? $"Rule \"{rule.Name.Trim()}\" saved and switched on."
                    : $"Rule \"{rule.Name.Trim()}\" saved. It's switched off — switch it on from the list when you're ready.";
                _logger.LogInformation("Automation rule {RuleId} saved from the editor", savedId);
                return RedirectToPage("./Index");
            }
            catch (InvalidOperationException ex)
            {
                // The API's sentence — every problem at once.
                SaveError = ex.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save automation rule {RuleId}", Id);
                SaveError = "The rule couldn't be saved. Please try again.";
            }

            await LoadOptionsAsync();
            return Page();
        }

        private async Task<bool> LoadOptionsAsync()
        {
            try
            {
                Options = await _automation.GetOptionsAsync();
                OptionsJson = JsonSerializer.Serialize(Options, Json);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load the automation editor's lists");
                ErrorMessage = "The rule editor couldn't be loaded. Please try again.";
                return false;
            }
        }

        /// <summary>
        /// A new rule, optionally from one of the starters on the list page.
        /// Starters only pre-fill: people, values and stages that differ in
        /// every workspace are left for the admin to choose.
        /// </summary>
        public static AutomationRuleDto Starter(string? template, AutomationOptionsDto options)
        {
            var activityTask = options.TaskTypes.FirstOrDefault() ?? "Task";

            return template switch
            {
                "welcome" => new AutomationRuleDto
                {
                    Name = "Call every new lead",
                    EntityType = AutomationEntity.Lead,
                    Trigger = AutomationTrigger.RecordCreated,
                    Actions =
                    {
                        new AutomationAction
                        {
                            Kind = AutomationActionKind.CreateTask, Subject = "Call {name}", ActivityType = "Call",
                            DueInDays = 0, AssignTo = AutomationPerson.Owner,
                            Description = "Added by the rule \"{rule}\"."
                        }
                    }
                },
                "share" => new AutomationRuleDto
                {
                    Name = "Share new leads in turn",
                    EntityType = AutomationEntity.Lead,
                    Trigger = AutomationTrigger.RecordCreated,
                    Conditions = { new AutomationCondition { Field = AutomationFields.Owner, Op = AutomationOp.IsEmpty } },
                    Actions =
                    {
                        new AutomationAction
                        {
                            Kind = AutomationActionKind.UpdateField, Field = AutomationFields.Owner,
                            Mode = AutomationUpdateMode.RoundRobin,
                            UserIds = options.Users.Select(u => u.Value).Take(AutomationRuleCheck.MaxRoundRobinPeople).ToList()
                        }
                    }
                },
                "bigdeal" => new AutomationRuleDto
                {
                    Name = "Big deal alert",
                    EntityType = AutomationEntity.Deal,
                    Trigger = AutomationTrigger.FieldChanged,
                    TriggerField = AutomationFields.Stage,
                    Conditions = { new AutomationCondition { Field = AutomationFields.Value, Op = AutomationOp.AtLeast, Value = "500000" } },
                    Actions =
                    {
                        new AutomationAction
                        {
                            Kind = AutomationActionKind.Notify, AssignTo = AutomationPerson.User,
                            Message = "{name} ({value}) moved to {stage}"
                        }
                    }
                },
                "won" => new AutomationRuleDto
                {
                    Name = "Handover when won",
                    EntityType = AutomationEntity.Deal,
                    Trigger = AutomationTrigger.FieldChanged,
                    TriggerField = AutomationFields.Stage,
                    Actions =
                    {
                        new AutomationAction
                        {
                            Kind = AutomationActionKind.CreateTask, Subject = "Hand over {name}", ActivityType = activityTask,
                            DueInDays = 1, AssignTo = AutomationPerson.Owner,
                            Description = "{name} reached {stage}. Brief the delivery team."
                        }
                    }
                },
                "quiet" => new AutomationRuleDto
                {
                    Name = "Nudge when a lead goes quiet",
                    EntityType = AutomationEntity.Lead,
                    Trigger = AutomationTrigger.TimeBased,
                    TimeKind = AutomationTimeKinds.NoActivity,
                    TimeDays = 7,
                    Actions =
                    {
                        new AutomationAction
                        {
                            Kind = AutomationActionKind.CreateTask, Subject = "Follow up with {name}", ActivityType = "Call",
                            DueInDays = 0, AssignTo = AutomationPerson.Owner,
                            Description = "Nothing has happened on this lead for a week."
                        }
                    }
                },
                _ => new AutomationRuleDto
                {
                    EntityType = AutomationEntity.Lead,
                    Trigger = AutomationTrigger.RecordCreated,
                    Actions = { new AutomationAction { Kind = AutomationActionKind.CreateTask, AssignTo = AutomationPerson.Owner, ActivityType = activityTask } }
                }
            };
        }
    }
}
