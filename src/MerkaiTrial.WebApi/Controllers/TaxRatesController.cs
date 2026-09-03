using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Mvc;
using MerkaiTrial.Application.Commands.TaxRates;

namespace MerkaiTrial.WebApi.Controllers
{
    [ApiController]
    [Route("api/tax-rates")]
    public class TaxRatesController : ControllerBase
    {
        private readonly GetTaxRatesHandler _getTaxRates;
        private readonly GetTaxRateByIdHandler _getById;
        private readonly CreateTaxRateHandler _createTaxRate;
        private readonly UpdateTaxRateHandler _updateTaxRate;
        private readonly DeleteTaxRateHandler _deleteTaxRate;
        private readonly GetDefaultTaxRateForCountryHandler _getDefaultForCountry;
        private readonly GetTaxRateStatsHandler _getStats;
        private readonly ILogger<TaxRatesController> _logger;

        public TaxRatesController(
            GetTaxRatesHandler getTaxRates,
            GetTaxRateStatsHandler getStats,
            GetTaxRateByIdHandler getById,
            CreateTaxRateHandler createTaxRate,
            UpdateTaxRateHandler updateTaxRate,
            DeleteTaxRateHandler deleteTaxRate,
            GetDefaultTaxRateForCountryHandler getDefaultForCountry,
            ILogger<TaxRatesController> logger)
        {
            _getTaxRates = getTaxRates;
            _getStats = getStats;
            _getById = getById;
            _createTaxRate = createTaxRate;
            _updateTaxRate = updateTaxRate;
            _deleteTaxRate = deleteTaxRate;
            _getDefaultForCountry = getDefaultForCountry;
            _logger = logger;
        }

        [HttpGet]
        [ProducesResponseType(typeof(PaginatedResult<TaxRateListItem>), 200)]
        public async Task<IActionResult> GetAll(
            [FromQuery] Guid? tenantId = null,
            [FromQuery] string? countryCode = null,
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] string? searchTerm = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var query = new GetTaxRatesQuery
                {
                    TenantId = tenantId ?? Guid.Empty,
                    CountryCode = countryCode,
                    PageNumber = pageNumber,
                    PageSize = pageSize,
                    SearchTerm = searchTerm
                };

                var result = await _getTaxRates.Handle(query, cancellationToken);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting tax rates");
                return StatusCode(500, "An error occurred");
            }
        }

        [HttpGet("country/{countryCode}/default")]
        [ProducesResponseType(typeof(TaxRateDto), 200)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> GetDefaultForCountry(
            string countryCode,
            [FromQuery] Guid? tenantId = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var query = new GetDefaultTaxRateForCountryQuery
                {
                    TenantId = tenantId ?? Guid.Empty,
                    CountryCode = countryCode
                };

                var taxRate = await _getDefaultForCountry.Handle(query, cancellationToken);
                return Ok(taxRate);
            }
            catch (KeyNotFoundException)
            {
                return NotFound($"No default tax rate for country {countryCode}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting default tax rate for {Code}", countryCode);
                return StatusCode(500, "An error occurred");
            }
        }

        [HttpGet("{id}")]
        [ProducesResponseType(typeof(TaxRateDto), 200)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken = default)
        {
            try
            {
                var query = new GetTaxRateByIdQuery { Id = id };
                var taxRate = await _getById.Handle(query, cancellationToken);
                return Ok(taxRate);
            }
            catch (KeyNotFoundException)
            {
                return NotFound($"Tax rate {id} not found");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting tax rate {Id}", id);
                return StatusCode(500, "An error occurred");
            }
        }

        [HttpPost]
        [ProducesResponseType(typeof(TaxRateDto), 201)]
        [ProducesResponseType(400)]
        public async Task<IActionResult> Create([FromBody] CreateTaxRateDto dto, CancellationToken cancellationToken = default)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                var command = new CreateTaxRateCommand
                {
                    TenantId = dto.TenantId,
                    CountryCode = dto.CountryCode,
                    Name = dto.Name,
                    TaxType = dto.TaxType,
                    Rate = dto.Rate,
                    IsDefault = dto.IsDefault,
                    CreatedBy = dto.CreatedBy
                };

                var taxRate = await _createTaxRate.Handle(command, cancellationToken);
                return CreatedAtAction(nameof(GetById), new { id = taxRate.Id }, taxRate);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating tax rate");
                return StatusCode(500, "An error occurred");
            }
        }

        [HttpGet("stats")]
        [ProducesResponseType(typeof(TaxRateStatsDto), 200)]
        public async Task<IActionResult> GetStats(CancellationToken cancellationToken = default)
        {
            try
            {
                var stats = await _getStats.Handle(cancellationToken);
                return Ok(stats);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting tax rate statistics");
                return StatusCode(500, "An error occurred");
            }
        }

        [HttpPut("{id}")]
        [ProducesResponseType(204)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> Update(Guid id, [FromBody] UpdateTaxRateDto dto, CancellationToken cancellationToken = default)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                var command = new UpdateTaxRateCommand
                {
                    Id = id,
                    Name = dto.Name,
                    Rate = dto.Rate,
                    IsDefault = dto.IsDefault,
                    UpdatedBy = dto.UpdatedBy
                };

                await _updateTaxRate.Handle(command, cancellationToken);
                return NoContent();
            }
            catch (KeyNotFoundException)
            {
                return NotFound($"Tax rate {id} not found");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating tax rate {Id}", id);
                return StatusCode(500, "An error occurred");
            }
        }

        [HttpDelete("{id}")]
        [ProducesResponseType(204)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken = default)
        {
            try
            {
                var command = new DeleteTaxRateCommand { Id = id };
                await _deleteTaxRate.Handle(command, cancellationToken);
                return NoContent();
            }
            catch (KeyNotFoundException)
            {
                return NotFound($"Tax rate {id} not found");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting tax rate {Id}", id);
                return StatusCode(500, "An error occurred");
            }
        }
    }
}