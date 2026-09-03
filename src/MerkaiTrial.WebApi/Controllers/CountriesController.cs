// =====================================================================
// COUNTRIES CONTROLLER - UPDATED with Stats Endpoint
// Location: MerkaiTrial.WebApi/Controllers/CountriesController.cs
// =====================================================================

using MerkaiTrial.Application.DTOs;
using Microsoft.AspNetCore.Mvc;
using MerkaiTrial.Application.Commands.Countries;

namespace MerkaiTrial.WebApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class CountriesController : ControllerBase
    {
        private readonly GetCountriesHandler _getCountries;
        private readonly GetCountryByCodeHandler _getByCode;
        private readonly CreateCountryHandler _createCountry;
        private readonly UpdateCountryHandler _updateCountry;
        private readonly GetCountryStatsHandler _getStats;  // ✅ NEW
        private readonly ILogger<CountriesController> _logger;

        public CountriesController(
            GetCountriesHandler getCountries,
            GetCountryByCodeHandler getByCode,
            CreateCountryHandler createCountry,
            UpdateCountryHandler updateCountry,
            GetCountryStatsHandler getStats,  // ✅ NEW
            ILogger<CountriesController> logger)
        {
            _getCountries = getCountries;
            _getByCode = getByCode;
            _createCountry = createCountry;
            _updateCountry = updateCountry;
            _getStats = getStats;  // ✅ NEW
            _logger = logger;
        }

        // ==================== GET ALL COUNTRIES (PAGINATED) ====================
        [HttpGet]
        [ProducesResponseType(typeof(PaginatedResult<CountryListItem>), 200)]
        public async Task<IActionResult> GetAll(
            [FromQuery] bool activeOnly = true,
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] string? searchTerm = null,
            [FromQuery] string? currencyFilter = null,
            [FromQuery] bool showInactiveOnly = false,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var query = new GetCountriesQuery
                {
                    ActiveOnly = activeOnly,
                    PageNumber = pageNumber,
                    PageSize = pageSize,
                    SearchTerm = searchTerm,
                    CurrencyFilter = currencyFilter,
                    ShowInactiveOnly = showInactiveOnly
                };

                var result = await _getCountries.Handle(query, cancellationToken);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting countries");
                return StatusCode(500, "An error occurred");
            }
        }

        // ==================== GET COUNTRY BY CODE ====================
        [HttpGet("{code}")]
        [ProducesResponseType(typeof(CountryDto), 200)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> GetByCode(string code, CancellationToken cancellationToken = default)
        {
            try
            {
                var query = new GetCountryByCodeQuery { Code = code };
                var country = await _getByCode.Handle(query, cancellationToken);
                return Ok(country);
            }
            catch (KeyNotFoundException)
            {
                return NotFound($"Country {code} not found");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting country {Code}", code);
                return StatusCode(500, "An error occurred");
            }
        }

        // ==================== GET STATISTICS (NEW!) ====================
        [HttpGet("stats")]
        [ProducesResponseType(typeof(CountryStatsDto), 200)]
        public async Task<IActionResult> GetStats(CancellationToken cancellationToken = default)
        {
            try
            {
                var stats = await _getStats.Handle(cancellationToken);
                return Ok(stats);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting country statistics");
                return StatusCode(500, "An error occurred");
            }
        }

        // ==================== CREATE COUNTRY ====================
        [HttpPost]
        [ProducesResponseType(typeof(CountryDto), 201)]
        [ProducesResponseType(400)]
        public async Task<IActionResult> Create([FromBody] CreateCountryDto dto, CancellationToken cancellationToken = default)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                var command = new CreateCountryCommand
                {
                    Code = dto.Code,
                    Name = dto.Name,
                    DialCode = dto.DialCode,
                    CurrencyCode = dto.CurrencyCode,
                    TaxLabel = dto.TaxLabel,
                    DefaultTaxRate = dto.DefaultTaxRate
                };

                var country = await _createCountry.Handle(command, cancellationToken);
                return CreatedAtAction(nameof(GetByCode), new { code = country.Code }, country);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating country");
                return StatusCode(500, "An error occurred");
            }
        }

        // ==================== UPDATE COUNTRY ====================
        [HttpPut("{code}")]
        [ProducesResponseType(204)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> Update(string code, [FromBody] UpdateCountryDto dto, CancellationToken cancellationToken = default)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                var command = new UpdateCountryCommand
                {
                    Code = code,
                    Name = dto.Name,
                    DialCode = dto.DialCode,
                    CurrencyCode = dto.CurrencyCode,
                    TaxLabel = dto.TaxLabel,
                    DefaultTaxRate = dto.DefaultTaxRate,
                    IsActive = dto.IsActive
                };

                await _updateCountry.Handle(command, cancellationToken);
                return NoContent();
            }
            catch (KeyNotFoundException)
            {
                return NotFound($"Country {code} not found");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating country {Code}", code);
                return StatusCode(500, "An error occurred");
            }
        }
    }
}
