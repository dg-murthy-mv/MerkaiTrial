// =====================================================================
// PipelineRulesController.cs
// Location: MerkaiTrial.WebApi/Controllers/PipelineRulesController.cs
//
// COMPLETE FILE — replaces the 020 version.
//
//   GET  api/pipeline-rules           the process: stages, the matrix,
//                                     the invoice rule, and any dead ends
//   PUT  api/pipeline-rules           save it all in one call
//
// 021: the /suggest endpoint is gone. Process templates are computed over
// the tenant's own stages and applied in the browser as a preview, so
// there is nothing to POST until the person presses Save.
//
// PERMISSIONS (024)
//
//   READING stays deals.read. The pipeline board and the deal page both
//   need the matrix to know which moves to offer and which will ask for
//   a note, so they can put the box up before posting rather than
//   posting, failing, and asking afterwards.
//
//   WRITING moves from deals.update to settings.update. The reasoning
//   behind admin-only was right — the process decides who may reopen a
//   closed deal, so a rep who could edit it could switch the restriction
//   off and reopen the deal anyway — but it was ENFORCED by a hardcoded
//   IsTenantAdmin check sitting inside the action, underneath a
//   deals.update policy that every rep passes. That made the right
//   outcome depend on a runtime if-statement instead of the authorization
//   pipeline, and made delegation impossible: a tenant admin had no way
//   to let their sales manager configure the process.
//
//   settings.update does both jobs. Tenant admins pass it through
//   PermissionHandler's bypass, so nothing changes for them, and granting
//   Sales Configuration to a manager now actually works.
// =====================================================================

using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.PipelineStages;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.WebApi.Controllers
{
    [ApiController]
    [Route("api/pipeline-rules")]
    public class PipelineRulesController : ControllerBase
    {
        private readonly GetPipelineRulesHandler _get;
        private readonly SavePipelineRulesHandler _save;
        private readonly ICurrentUserService _currentUserService;
        private readonly ILogger<PipelineRulesController> _logger;

        public PipelineRulesController(
            GetPipelineRulesHandler get,
            SavePipelineRulesHandler save,
            ICurrentUserService currentUserService,
            ILogger<PipelineRulesController> logger)
        {
            _get = get;
            _save = save;
            _currentUserService = currentUserService;
            _logger = logger;
        }

        /// <summary>
        /// GET /api/pipeline-rules
        ///
        /// Readable by anyone who can read deals, not just admins: the deal
        /// page and the kanban both use it to decide which moves to offer
        /// and which will ask for a note, so they can put the box up before
        /// posting rather than posting, failing and asking afterwards.
        /// </summary>
        [HttpGet]
        [Authorize(Policy = Policies.DealsRead)]
        [ProducesResponseType(typeof(PipelineRulesDto), 200)]
        [ProducesResponseType(500)]
        public async Task<IActionResult> Get(CancellationToken ct = default)
        {
            var tenantId = _currentUserService.GetCurrentTenantId();
            try
            {
                return Ok(await _get.Handle(tenantId, ct));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting pipeline rules for tenant {TenantId}", tenantId);
                return StatusCode(500, new { error = "Failed to retrieve the pipeline process" });
            }
        }

        /// <summary>
        /// PUT /api/pipeline-rules — the whole screen at once.
        ///
        /// One call rather than one per cell. A half-saved process is worse
        /// than none: a tenant who switched three moves off and one on
        /// would, on a partial failure, have no idea which half took.
        /// </summary>
        [HttpPut]
        [Authorize(Policy = Policies.SettingsUpdate)]
        [ProducesResponseType(204)]
        [ProducesResponseType(400)]
        [ProducesResponseType(403)]
        [ProducesResponseType(500)]
        public async Task<IActionResult> Save(
            [FromBody] SaveAllPipelineRulesDto dto,
            CancellationToken ct = default)
        {
            var tenantId = _currentUserService.GetCurrentTenantId();
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                // 024: the IsTenantAdmin check that used to sit here is
                // gone. [Authorize(Policy = Policies.SettingsUpdate)] on
                // this action does the same job through the authorization
                // pipeline — admins still bypass it — and, unlike the
                // hardcoded check, it can be granted to someone else.
                var me = await _currentUserService.GetCurrentUserAsync();

                await _save.Handle(tenantId, dto, me.FullName, ct);

                _logger.LogInformation(
                    "Pipeline process updated for tenant {TenantId} by {User}", tenantId, me.FullName);

                return NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving pipeline process for tenant {TenantId}", tenantId);
                return StatusCode(500, new { error = "Failed to save the pipeline process" });
            }
        }

    }
}
