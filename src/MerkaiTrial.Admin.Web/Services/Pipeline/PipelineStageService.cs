// =====================================================================
// PipelineStageService.cs
// Location: MerkaiTrial.Admin.Web/Services/Pipeline/PipelineStageService.cs
//
// NEW FILE. Same thin-wrapper pattern as ActivityService.
//
// CACHING: stages change perhaps twice in a tenant's lifetime but are
// read on every deal page, pipeline board and quote screen. A short
// per-request cache would help; a longer one would mean a client renames
// a stage and does not see it. Left uncached for now — measure first.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Core;
using MerkaiTrial.Application.Commands.PipelineStages;

namespace MerkaiTrial.Admin.Web.Services.Pipeline;

public interface IPipelineStageService
{
    Task<List<PipelineStageDto>> GetAsync(bool activeOnly = false, CancellationToken ct = default);
    Task<PipelineStageDto> CreateAsync(CreatePipelineStageDto dto, CancellationToken ct = default);
    Task UpdateAsync(UpdatePipelineStageDto dto, CancellationToken ct = default);
    Task ReorderAsync(ReorderPipelineStagesDto dto, CancellationToken ct = default);
    Task SetDefaultAsync(Guid stageId, CancellationToken ct = default);
    Task DeleteAsync(Guid stageId, CancellationToken ct = default);
}

public class PipelineStageService : IPipelineStageService
{
    private readonly IApiService _api;

    public PipelineStageService(IApiService api) => _api = api;

    public async Task<List<PipelineStageDto>> GetAsync(bool activeOnly = false, CancellationToken ct = default)
        => await _api.GetAsync<List<PipelineStageDto>>($"api/pipeline-stages?activeOnly={activeOnly}");

    public async Task<PipelineStageDto> CreateAsync(CreatePipelineStageDto dto, CancellationToken ct = default)
        => await _api.PostAsync<PipelineStageDto>("api/pipeline-stages", dto);

    public async Task UpdateAsync(UpdatePipelineStageDto dto, CancellationToken ct = default)
        => await _api.PutVoidAsync($"api/pipeline-stages/{dto.StageId}", dto);

    public async Task ReorderAsync(ReorderPipelineStagesDto dto, CancellationToken ct = default)
        => await _api.PostVoidAsync("api/pipeline-stages/reorder", dto);

    public async Task SetDefaultAsync(Guid stageId, CancellationToken ct = default)
        => await _api.PostVoidAsync($"api/pipeline-stages/{stageId}/default", new { });

    public async Task DeleteAsync(Guid stageId, CancellationToken ct = default)
        => await _api.DeleteAsync($"api/pipeline-stages/{stageId}");
}
