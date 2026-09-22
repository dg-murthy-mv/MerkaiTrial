// =====================================================================
// DealStageService.cs
// Location: MerkaiTrial.Admin.Web/Services/Deals/DealStageService.cs
//
// COMPLETE FILE — replaces the 019 version.
//
// Already registered in AdminWebServiceRegistration.AddAdminWebCoreServices:
//     services.AddScoped<IDealStageService, DealStageService>();
//
// CHANGES (020)
//   ✅ One Note replaces the two reason fields. A transition carries its
//      own prompt now, so the caller does not need to know whether it is
//      being asked about a loss or a reopen — it just passes on whatever
//      the person typed.
//   ✅ adminOverride, for the escape hatch out of a stage whose configured
//      moves are all blocked.
//
// WHY THIS IS SEPARATE FROM DealService
//   IDealService.UpdateStageAsync(tenantId, dealId, stage) posts only the
//   stage and is called from several pages. Widening it would mean editing
//   every caller for the benefit of one. The API takes everything after
//   the stage as optional, so DealService keeps working untouched.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Core;

namespace MerkaiTrial.Admin.Web.Services.Deals;

/// <summary>What the browser sends up when it moves a deal.</summary>
public record MoveDealStageRequest(
    string Stage,
    string? Note = null,
    bool AdminOverride = false);

public interface IDealStageService
{
    /// <summary>
    /// Move a deal to another stage, carrying whatever note the transition
    /// asked for. Throws InvalidOperationException with the server's own
    /// wording when a rule refuses — IApiService turns the 400/403 body
    /// into that, and the page shows the message as it stands.
    /// </summary>
    /// <param name="adminOverride">
    /// A workspace admin deliberately stepping outside the configured
    /// process. Refused for anyone else, and recorded on the deal's stage
    /// history either way.
    /// </param>
    Task MoveAsync(
        Guid dealId,
        string stage,
        string? note = null,
        bool adminOverride = false,
        CancellationToken ct = default);
}

public class DealStageService : IDealStageService
{
    private readonly IApiService _api;

    public DealStageService(IApiService api) => _api = api;

    public async Task MoveAsync(
        Guid dealId,
        string stage,
        string? note = null,
        bool adminOverride = false,
        CancellationToken ct = default)
        => await _api.PutVoidAsync(
            $"api/deals/{dealId}/stage",
            new MoveDealStageRequest(stage, note, adminOverride));
}
