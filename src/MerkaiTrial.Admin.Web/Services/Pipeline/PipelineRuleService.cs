// =====================================================================
// PipelineRuleService.cs
// Location: MerkaiTrial.Admin.Web/Services/Pipeline/PipelineRuleService.cs
//
// NEW FILE (019). Same thin-wrapper pattern as PipelineStageService.
//
// REGISTER BY HAND in Startup/AdminWebServiceRegistration.cs — Admin.Web
// services are not part of the Scrutor scan:
//
//     services.AddScoped<IPipelineRuleService, PipelineRuleService>();
//
// CACHING: same answer as PipelineStageService. These change perhaps
// twice in a tenant's lifetime but are read on every pipeline board load.
// A short cache would help; a longer one would mean an admin switches a
// rule off and does not see it. Left uncached — measure first.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Core;
using MerkaiTrial.Application.Commands.PipelineStages;

namespace MerkaiTrial.Admin.Web.Services.Pipeline;

public interface IPipelineRuleService
{
    /// <summary>
    /// The tenant's transition rules, plus what each stage asks of a deal
    /// before letting it in.
    /// </summary>
    Task<PipelineRulesDto> GetAsync(CancellationToken ct = default);

    /// <summary>The whole settings screen, saved in one call.</summary>
    Task SaveAsync(SaveAllPipelineRulesDto dto, CancellationToken ct = default);
}

public class PipelineRuleService : IPipelineRuleService
{
    private readonly IApiService _api;

    public PipelineRuleService(IApiService api) => _api = api;

    public async Task<PipelineRulesDto> GetAsync(CancellationToken ct = default)
        => await _api.GetAsync<PipelineRulesDto>("api/pipeline-rules");

    public async Task SaveAsync(SaveAllPipelineRulesDto dto, CancellationToken ct = default)
        => await _api.PutVoidAsync("api/pipeline-rules", dto);
}
