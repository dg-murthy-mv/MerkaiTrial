// =====================================================================
// PipelineRulesController.cs
// Location: MerkaiTrial.WebApi/Controllers/PipelineRulesController.cs
//
// NEW FILE (019).
//
//   GET  api/pipeline-rules    the rules and every stage's requirements
//   PUT  api/pipeline-rules    save both in one call
//
// Deliberately NOT bolted onto PipelineStagesController. That one is
// about what a stage IS — name, order, probability, category — and is
// used by every screen that shows a pipeline. This is settings, read by
// one page and the board, and keeping it separate means the existing
// stages controller and its DTOs are untouched by this round.
//
// Both endpoints are admin-only. The rules decide who may reopen a
// closed deal, so a rep who could edit them could simply switch the
// restriction off and reopen it anyway.
// =====================================================================

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
        /// Readable by anyone who can read deals, not just admins: the
        /// pipeline board uses it to work out which drags are going to ask
        /// for a reason, so it can put the box up before posting instead of
        /// posting, failing and asking afterwards.
        /// </summary>
        [HttpGet]
        [Authorize(Policy = "Deals.Read")]
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
                return StatusCode(500, new { error = "Failed to retrieve pipeline rules" });
            }
        }

        /// <summary>
        /// PUT /api/pipeline-rules — the whole screen at once.
        ///
        /// One call rather than one per switch. Half-saved rules are worse
        /// than none: a tenant who turned the reopen restriction on and the
        /// invoice block off would, on a partial failure, have no idea
        /// which half took effect.
        /// </summary>
        [HttpPut]
        [Authorize(Policy = "Deals.Update")]
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

                var me = await _currentUserService.GetCurrentUserAsync();

                if (!me.IsTenantAdmin)
                    return StatusCode(403, new
                    {
                        error = "Only a workspace admin can change the pipeline rules."
                    });

                await _save.Handle(tenantId, dto, me.FullName, ct);

                _logger.LogInformation(
                    "Pipeline rules updated for tenant {TenantId} by {User}", tenantId, me.FullName);

                return NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving pipeline rules for tenant {TenantId}", tenantId);
                return StatusCode(500, new { error = "Failed to save pipeline rules" });
            }
        }
    }
}
