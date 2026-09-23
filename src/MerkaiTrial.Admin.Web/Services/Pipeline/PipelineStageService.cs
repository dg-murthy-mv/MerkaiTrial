// =====================================================================
// PipelineStageService.cs
// Location: MerkaiTrial.Admin.Web/Services/Pipeline/PipelineStageService.cs
//
// COMPLETE FILE — replaces the existing one.
//
// CHANGES (023)
//   ✅ MoveDealsAsync — returns the result rather than void, because the
//      page has something worth saying afterwards ("14 deals moved to
//      Proposal. Negotiation is retired."). IApiService turns a 400 with
//      { "error": "..." } into an InvalidOperationException carrying that
//      message, so every refusal the handler writes reaches the page as
//      a sentence a tenant admin can act on.
//
// ALREADY REGISTERED. Admin.Web services are hand-registered in
// Startup/AdminWebServiceRegistration.cs, and IPipelineStageService is
// already in there. Adding a method to an existing interface needs no
// registration change.
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
    /// <param name="detail">
    /// True only from the stage settings page. Adds the ways-in/ways-out
    /// counts and the reasons a stage cannot be deleted or retired, at
    /// the cost of two extra queries. Every other caller reads this for a
    /// picker and leaves it false.
    /// </param>
    Task<List<PipelineStageDto>> GetAsync(
        bool activeOnly = false, bool detail = false, CancellationToken ct = default);
    Task<PipelineStageDto> CreateAsync(CreatePipelineStageDto dto, CancellationToken ct = default);
    Task UpdateAsync(UpdatePipelineStageDto dto, CancellationToken ct = default);
    Task ReorderAsync(ReorderPipelineStagesDto dto, CancellationToken ct = default);
    Task SetDefaultAsync(Guid stageId, CancellationToken ct = default);

    /// <summary>
    /// Sends every deal in <paramref name="dto"/>.FromStageId to another
    /// stage of the same kind, optionally retiring the source afterwards.
    /// </summary>
    Task<MoveStageDealsResult> MoveDealsAsync(MoveStageDealsDto dto, CancellationToken ct = default);

    Task DeleteAsync(Guid stageId, CancellationToken ct = default);
}

public class PipelineStageService : IPipelineStageService
{
    private readonly IApiService _api;

    public PipelineStageService(IApiService api) => _api = api;

    public async Task<List<PipelineStageDto>> GetAsync(
        bool activeOnly = false, bool detail = false, CancellationToken ct = default)
        => await _api.GetAsync<List<PipelineStageDto>>(
            $"api/pipeline-stages?activeOnly={activeOnly}&detail={detail}");

    public async Task<PipelineStageDto> CreateAsync(CreatePipelineStageDto dto, CancellationToken ct = default)
        => await _api.PostAsync<PipelineStageDto>("api/pipeline-stages", dto);

    public async Task UpdateAsync(UpdatePipelineStageDto dto, CancellationToken ct = default)
        => await _api.PutVoidAsync($"api/pipeline-stages/{dto.StageId}", dto);

    public async Task ReorderAsync(ReorderPipelineStagesDto dto, CancellationToken ct = default)
        => await _api.PostVoidAsync("api/pipeline-stages/reorder", dto);

    public async Task SetDefaultAsync(Guid stageId, CancellationToken ct = default)
        => await _api.PostVoidAsync($"api/pipeline-stages/{stageId}/default", new { });

    public async Task<MoveStageDealsResult> MoveDealsAsync(MoveStageDealsDto dto, CancellationToken ct = default)
        => await _api.PostAsync<MoveStageDealsResult>(
            $"api/pipeline-stages/{dto.FromStageId}/move-deals", dto);

    public async Task DeleteAsync(Guid stageId, CancellationToken ct = default)
        => await _api.DeleteAsync($"api/pipeline-stages/{stageId}");
}
