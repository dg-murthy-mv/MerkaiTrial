// =====================================================================
// PipelineRuleService.cs
// Location: MerkaiTrial.Admin.Web/Services/Pipeline/PipelineRuleService.cs
//
// COMPLETE FILE — replaces the 019 version.
//
// Already registered in AdminWebServiceRegistration.AddAdminWebCoreServices:
//     services.AddScoped<IPipelineRuleService, PipelineRuleService>();
//
// CACHING: same answer as PipelineStageService. The process changes
// perhaps twice in a tenant's lifetime but is read on every pipeline
// board and every deal page. A short cache would help; a longer one
// would mean an admin switches a move off and still sees the button.
// Left uncached — measure first.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Core;
using MerkaiTrial.Application.Commands.PipelineStages;

namespace MerkaiTrial.Admin.Web.Services.Pipeline;

public interface IPipelineRuleService
{
    /// <summary>
    /// The tenant's sales process: stages, the from-to matrix, the invoice
    /// rule, and any stage with no way out.
    /// </summary>
    Task<PipelineRulesDto> GetAsync(CancellationToken ct = default);

    /// <summary>The whole settings screen, saved in one call.</summary>
    Task SaveAsync(SaveAllPipelineRulesDto dto, CancellationToken ct = default);

    /// <summary>Switch on the moves a normal pipeline wants, and the rest off.</summary>
    Task ApplySuggestedAsync(CancellationToken ct = default);

    /// <summary>What one deal can do right now, and why not for the rest.</summary>
    Task<DealTransitionsDto> GetForDealAsync(Guid dealId, CancellationToken ct = default);
}

public class PipelineRuleService : IPipelineRuleService
{
    private readonly IApiService _api;

    public PipelineRuleService(IApiService api) => _api = api;

    public async Task<PipelineRulesDto> GetAsync(CancellationToken ct = default)
        => await _api.GetAsync<PipelineRulesDto>("api/pipeline-rules");

    public async Task SaveAsync(SaveAllPipelineRulesDto dto, CancellationToken ct = default)
        => await _api.PutVoidAsync("api/pipeline-rules", dto);

    public async Task ApplySuggestedAsync(CancellationToken ct = default)
        => await _api.PostVoidAsync("api/pipeline-rules/suggest", new { });

    public async Task<DealTransitionsDto> GetForDealAsync(Guid dealId, CancellationToken ct = default)
        => await _api.GetAsync<DealTransitionsDto>($"api/deals/{dealId}/transitions");
}
