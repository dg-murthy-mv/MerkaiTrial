// =====================================================================
// DealStageService.cs
// Location: MerkaiTrial.Admin.Web/Services/Deals/DealStageService.cs
//
// NEW FILE (019).
//
// WHY A NEW SERVICE RATHER THAN A CHANGE TO DealService
//   IDealService.UpdateStageAsync(tenantId, dealId, stage) is called from
//   several pages and posts only the stage. A move can now also carry a
//   lost reason or a reopen reason, and widening that method would mean
//   editing every caller for the benefit of one.
//
//   This adds the richer call beside it. DealService keeps working
//   untouched — the API takes the two reasons as optional, so a body of
//   just { stage } still binds exactly as before.
//
// REGISTER BY HAND in Startup/AdminWebServiceRegistration.cs:
//
//     services.AddScoped<IDealStageService, DealStageService>();
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Core;

namespace MerkaiTrial.Admin.Web.Services.Deals;

/// <summary>What the browser sends up when it moves a card.</summary>
public record MoveDealStageRequest(
    string Stage,
    string? LostReason = null,
    string? ReopenReason = null);

public interface IDealStageService
{
    /// <summary>
    /// Move a deal to another stage, carrying whichever reason the move
    /// needs. Throws InvalidOperationException with the server's own
    /// wording when a rule refuses — IApiService turns the 400/403 body
    /// into that, and the page shows the message as it stands.
    /// </summary>
    Task MoveAsync(
        Guid dealId,
        string stage,
        string? lostReason = null,
        string? reopenReason = null,
        CancellationToken ct = default);
}

public class DealStageService : IDealStageService
{
    private readonly IApiService _api;

    public DealStageService(IApiService api) => _api = api;

    public async Task MoveAsync(
        Guid dealId,
        string stage,
        string? lostReason = null,
        string? reopenReason = null,
        CancellationToken ct = default)
        => await _api.PutVoidAsync(
            $"api/deals/{dealId}/stage",
            new MoveDealStageRequest(stage, lostReason, reopenReason));
}
