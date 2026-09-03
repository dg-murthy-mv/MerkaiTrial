// =====================================================================
// IMetaService.cs + MetaService.cs
// Location: MerkaiTrial.Admin.Web/Services/Meta/
//
// Follows exact same pattern as ILeadService / LeadService.
// Razor pages inject IMetaService — never call IApiService directly.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Core;
using MerkaiTrial.Application.Commands.Meta;

namespace MerkaiTrial.Admin.Web.Services.Meta;

// ── INTERFACE ─────────────────────────────────────────────────────────

public interface IMetaService
{
    Task<List<CountryMetaDto>> GetCountriesAsync();
    Task<List<VerticalDto>>    GetVerticalsAsync();
}

// ── IMPLEMENTATION ────────────────────────────────────────────────────

public class MetaService : IMetaService
{
    private readonly IApiService           _api;
    private readonly ILogger<MetaService>  _logger;

    public MetaService(IApiService api, ILogger<MetaService> logger)
    {
        _api    = api;
        _logger = logger;
    }

    public async Task<List<CountryMetaDto>> GetCountriesAsync()
    {
        try
        {
            return await _api.GetAsync<List<CountryMetaDto>>("api/meta/countries");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load countries");
            return new List<CountryMetaDto>();
        }
    }

    public async Task<List<VerticalDto>> GetVerticalsAsync()
    {
        try
        {
            return await _api.GetAsync<List<VerticalDto>>("api/meta/verticals");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load verticals");
            return new List<VerticalDto>();
        }
    }
}
