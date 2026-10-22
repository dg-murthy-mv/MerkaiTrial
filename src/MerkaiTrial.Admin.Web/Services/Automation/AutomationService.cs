// =====================================================================
// AutomationService.cs
// Location: MerkaiTrial.Admin.Web/Services/Automation/AutomationService.cs
//
// NEW FILE (098 — workflow automation). Settings → Automation talks to the
// API through this, like every other settings page. A 400 from the API
// arrives as an InvalidOperationException carrying the API's sentence.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Core;
using MerkaiTrial.Application.Commands.Automation;

namespace MerkaiTrial.Admin.Web.Services.Automation;

public interface IAutomationService
{
    Task<AutomationRulesPageDto> GetRulesAsync();
    Task<AutomationRuleDto> GetRuleAsync(Guid id);
    Task<AutomationOptionsDto> GetOptionsAsync();
    Task<List<AutomationRunDto>> GetRunsAsync(Guid? ruleId = null);

    /// <summary>Adds (id null) or changes a rule. Returns its id.</summary>
    Task<Guid> SaveAsync(Guid? id, AutomationRuleDto rule);
    Task SetActiveAsync(Guid id, bool isActive);
    Task DeleteAsync(Guid id);

    /// <summary>099. Check this workspace's time-based rules now.</summary>
    Task<AutomationTimeCheckResult> CheckTimeRulesNowAsync();
}

public class AutomationService : IAutomationService
{
    private readonly IApiService _api;

    public AutomationService(IApiService api) => _api = api;

    public Task<AutomationRulesPageDto> GetRulesAsync()
        => _api.GetAsync<AutomationRulesPageDto>("api/automation/rules");

    public Task<AutomationRuleDto> GetRuleAsync(Guid id)
        => _api.GetAsync<AutomationRuleDto>($"api/automation/rules/{id}");

    public Task<AutomationOptionsDto> GetOptionsAsync()
        => _api.GetAsync<AutomationOptionsDto>("api/automation/options");

    public Task<List<AutomationRunDto>> GetRunsAsync(Guid? ruleId = null)
        => _api.GetAsync<List<AutomationRunDto>>(
            ruleId is { } id ? $"api/automation/runs?ruleId={id}" : "api/automation/runs");

    public Task<Guid> SaveAsync(Guid? id, AutomationRuleDto rule)
        => id is { } existing
            ? _api.PutAsync<Guid>($"api/automation/rules/{existing}", rule)
            : _api.PostAsync<Guid>("api/automation/rules", rule);

    public Task SetActiveAsync(Guid id, bool isActive)
        => _api.PostVoidAsync($"api/automation/rules/{id}/active", new SetAutomationRuleActiveDto(isActive));

    public Task DeleteAsync(Guid id)
        => _api.DeleteAsync($"api/automation/rules/{id}");

    public Task<AutomationTimeCheckResult> CheckTimeRulesNowAsync()
        => _api.PostAsync<AutomationTimeCheckResult>("api/automation/time-check", new { });
}
