// =====================================================================
// MetaController.cs
// Location: MerkaiTrial.WebApi/Controllers/MetaController.cs
//
// Follows exact same pattern as LeadsController — dispatches to handlers.
// No direct FlowDbContext. Handlers are auto-registered via Scrutor scan.
// =====================================================================

using MerkaiTrial.Application.Commands.Meta;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.WebApi.Controllers;

[ApiController]
[Route("api/meta")]
public class MetaController : ControllerBase
{
    private readonly GetCountriesMetaHandler _countriesHandler;
    private readonly GetVerticalsHandler     _verticalsHandler;
    private readonly ILogger<MetaController> _logger;

    public MetaController(
        GetCountriesMetaHandler countriesHandler,
        GetVerticalsHandler     verticalsHandler,
        ILogger<MetaController> logger)
    {
        _countriesHandler = countriesHandler;
        _verticalsHandler = verticalsHandler;
        _logger           = logger;
    }

    /// <summary>
    /// GET /api/meta/countries
    /// All active countries with currency info — loaded by _Layout once,
    /// used by country-currency.js on every page. No hardcoded maps needed.
    /// </summary>
    [HttpGet("countries")]
    public async Task<IActionResult> GetCountries(CancellationToken ct)
    {
        try
        {
            var result = await _countriesHandler.Handle(new GetCountriesMetaQuery(), ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get countries meta");
            return StatusCode(500, new { error = "Failed to retrieve countries" });
        }
    }

    /// <summary>
    /// GET /api/meta/verticals
    /// All active company verticals — used in Lead/Deal/Company create/edit dropdowns.
    /// </summary>
    [HttpGet("verticals")]
    public async Task<IActionResult> GetVerticals(CancellationToken ct)
    {
        try
        {
            var result = await _verticalsHandler.Handle(new GetVerticalsQuery(), ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get verticals");
            return StatusCode(500, new { error = "Failed to retrieve verticals" });
        }
    }
}
