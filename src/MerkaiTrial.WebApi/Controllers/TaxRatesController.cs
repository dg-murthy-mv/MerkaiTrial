// =====================================================================
// TaxRatesController.cs
// COMPLETE FILE — 056.
//
// THIS CONTROLLER HAD NO [Authorize] AT ALL. Not on the class, not on a
// single action, while ProductsController next door carries [Authorize]
// plus a Policies.Products* policy on every one of its. There is a
// FallbackPolicy, so an anonymous caller was stopped — but any signed-in
// user of any workspace could list, create, edit and delete tax rates,
// and Create read the target TenantId STRAIGHT FROM THE REQUEST BODY.
// A rep could have written a tax rate into somebody else's workspace by
// typing their id into the JSON.
//
// TWO AUDIENCES, TWO RULES
//
//   SYSTEM rates (TenantId NULL) are platform configuration — the
//   standard VAT or GST for a country, shared by every workspace that
//   sells there. Only a super admin may write one.
//
//   TENANT rates belong to the workspace. settings.* is the permission,
//   the same module that already gates pipeline stages and the sales
//   process; PermissionHandler bypasses it for a tenant admin, so
//   workspace admins can configure tax the day this ships and nobody
//   else can until it is granted in the role editor.
//
// WHY settings.* AND NOT A NEW taxrates MODULE: a new module means
// ModuleCatalog, seeding and the role editor as well. settings.* already
// means "workspace configuration" and already exists everywhere. If tax
// ever needs delegating separately from the pipeline, that is the round
// to add the module.
//
// THE TENANT IS TAKEN FROM THE TOKEN, NEVER FROM THE REQUEST. That is
// the whole of the cross-tenant fix; the rest is enforcement in
// TaxRateOwnership, which the commands call.
//
// PermissionHandler bypasses on IsTenantAdmin only, NOT IsSuperAdmin, so
// a "settings.*" policy would NOT let a super admin through — they have
// no tenant and no permission claims. The system-rate paths therefore
// check the IsSuperAdmin claim directly, the same way
// WhatsAppStatusController does.
// =====================================================================

using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MerkaiTrial.Application.Commands.TaxRates;

namespace MerkaiTrial.WebApi.Controllers
{
    [ApiController]
    [Route("api/tax-rates")]
    [Authorize]
    public class TaxRatesController : ControllerBase
    {
        private readonly GetTaxRatesHandler _getTaxRates;
        private readonly GetTaxRateByIdHandler _getById;
        private readonly CreateTaxRateHandler _createTaxRate;
        private readonly UpdateTaxRateHandler _updateTaxRate;
        private readonly DeleteTaxRateHandler _deleteTaxRate;
        private readonly GetDefaultTaxRateForCountryHandler _getDefaultForCountry;
        private readonly GetTaxRateStatsHandler _getStats;
        private readonly ICurrentTenantService _tenant;          // 056
        private readonly ILogger<TaxRatesController> _logger;

        public TaxRatesController(
            GetTaxRatesHandler getTaxRates,
            GetTaxRateStatsHandler getStats,
            GetTaxRateByIdHandler getById,
            CreateTaxRateHandler createTaxRate,
            UpdateTaxRateHandler updateTaxRate,
            DeleteTaxRateHandler deleteTaxRate,
            GetDefaultTaxRateForCountryHandler getDefaultForCountry,
            ICurrentTenantService tenant,                        // 056
            ILogger<TaxRatesController> logger)
        {
            _getTaxRates = getTaxRates;
            _getStats = getStats;
            _getById = getById;
            _createTaxRate = createTaxRate;
            _updateTaxRate = updateTaxRate;
            _deleteTaxRate = deleteTaxRate;
            _getDefaultForCountry = getDefaultForCountry;
            _tenant = tenant;
            _logger = logger;
        }

        // ── 056: who is asking, from the TOKEN ───────────────────────
        //
        // Never from a query string or a request body. Everything below
        // goes through these two and the commands enforce the rest.

        private bool IsSuperAdmin =>
            User.HasClaim(c => c.Type == "IsSuperAdmin" &&
                               string.Equals(c.Value, "true", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// The caller's workspace, or NULL for a super admin (who has none).
        /// Returns NULL rather than throwing when the claim is missing —
        /// GetTenantId() throws, and a 500 on a missing claim is a worse
        /// answer than treating the caller as having no workspace.
        /// </summary>
        private Guid? ActingTenantId
        {
            get
            {
                try
                {
                    var id = _tenant.GetTenantId();
                    return id == Guid.Empty ? null : id;
                }
                catch (UnauthorizedAccessException)
                {
                    return null;
                }
            }
        }

        // 056. settings.read. The tenantId QUERY PARAMETER IS GONE: the
        // global query filter resolves "system rates plus mine" for a tenant
        // and "system rates only" for a super admin, so a caller naming a
        // workspace could only ever have been naming somebody else's.
        [HttpGet]
        [Authorize(Policy = Policies.SettingsRead)]
        [ProducesResponseType(typeof(PaginatedResult<TaxRateListItem>), 200)]
        public async Task<IActionResult> GetAll(
            [FromQuery] string? countryCode = null,
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] string? searchTerm = null,
            // 057: a management screen asks for true; a picker leaves it
            // false and gets only rates worth choosing.
            [FromQuery] bool includeInactive = false,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var query = new GetTaxRatesQuery
                {
                    TenantId = ActingTenantId ?? Guid.Empty,
                    CountryCode = countryCode,
                    PageNumber = pageNumber,
                    PageSize = pageSize,
                    SearchTerm = searchTerm,
                    IncludeInactive = includeInactive     // 057
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

        // 056. settings.read, and the tenant comes from the token.
        [HttpGet("country/{countryCode}/default")]
        [Authorize(Policy = Policies.SettingsRead)]
        [ProducesResponseType(typeof(TaxRateDto), 200)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> GetDefaultForCountry(
            string countryCode,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var query = new GetDefaultTaxRateForCountryQuery
                {
                    TenantId = ActingTenantId ?? Guid.Empty,
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
        [Authorize(Policy = Policies.SettingsRead)]
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
        [Authorize(Policy = Policies.SettingsCreate)]
        [ProducesResponseType(typeof(TaxRateDto), 201)]
        [ProducesResponseType(400)]
        [ProducesResponseType(403)]
        public async Task<IActionResult> Create([FromBody] CreateTaxRateDto dto, CancellationToken cancellationToken = default)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                // 056. THE CROSS-TENANT FIX.
                //
                // dto.TenantId used to go straight into the command, so the
                // body decided whose workspace got the rate. Now the body can
                // only ask for ONE thing — a SYSTEM rate, by sending null or
                // Guid.Empty — and that is refused unless the caller is a
                // super admin. Anything else is the caller's own workspace,
                // taken from the token.
                var wantsSystemRate = dto.TenantId is null || dto.TenantId == Guid.Empty;

                if (wantsSystemRate && !IsSuperAdmin)
                    return Forbid();

                var owner = wantsSystemRate ? (Guid?)null : ActingTenantId;

                if (owner is null && !wantsSystemRate)
                    return Forbid();

                var command = new CreateTaxRateCommand
                {
                    TenantId = owner,
                    ActingTenantId = ActingTenantId,
                    IsSuperAdmin = IsSuperAdmin,
                    CountryCode = dto.CountryCode,
                    Name = dto.Name,
                    TaxType = dto.TaxType,
                    Rate = dto.Rate,
                    IsDefault = dto.IsDefault,
                    EffectiveFrom = dto.EffectiveFrom,    // 057
                    EffectiveTo = dto.EffectiveTo,        // 057
                    CreatedBy = dto.CreatedBy
                };

                var taxRate = await _createTaxRate.Handle(command, cancellationToken);
                return CreatedAtAction(nameof(GetById), new { id = taxRate.Id }, taxRate);
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(403, new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                // A business rule — unknown country, duplicate name. The
                // caller can act on this, so it is a 400 with the message
                // rather than a 500 with "An error occurred".
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating tax rate");
                return StatusCode(500, "An error occurred");
            }
        }

        [HttpGet("stats")]
        [Authorize(Policy = Policies.SettingsRead)]
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
        [Authorize(Policy = Policies.SettingsUpdate)]
        [ProducesResponseType(204)]
        [ProducesResponseType(403)]
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
                    // 056: from the token. dto.TenantId is ignored — which
                    // row you may edit is decided by the row's own owner, not
                    // by what the request claims.
                    ActingTenantId = ActingTenantId,
                    IsSuperAdmin = IsSuperAdmin,
                    Name = dto.Name,
                    Rate = dto.Rate,
                    IsDefault = dto.IsDefault,
                    EffectiveFrom = dto.EffectiveFrom,    // 057
                    EffectiveTo = dto.EffectiveTo,        // 057
                    IsActive = dto.IsActive,              // 057
                    UpdatedBy = dto.UpdatedBy
                };

                await _updateTaxRate.Handle(command, cancellationToken);
                return NoContent();
            }
            catch (InvalidOperationException ex)
            {
                // 057: an inverted effective window, or switching off the last
                // active default. Both are the caller's to fix.
                return BadRequest(new { error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(403, new { error = ex.Message });
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
        [Authorize(Policy = Policies.SettingsDelete)]
        [ProducesResponseType(204)]
        [ProducesResponseType(400)]
        [ProducesResponseType(403)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken = default)
        {
            try
            {
                var command = new DeleteTaxRateCommand
                {
                    Id = id,
                    ActingTenantId = ActingTenantId,   // 056
                    IsSuperAdmin = IsSuperAdmin
                };

                await _deleteTaxRate.Handle(command, cancellationToken);
                return NoContent();
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(403, new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                // "This is the last default rate for IN" — the caller can fix
                // that, so tell them rather than returning a bare 500.
                return BadRequest(new { error = ex.Message });
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