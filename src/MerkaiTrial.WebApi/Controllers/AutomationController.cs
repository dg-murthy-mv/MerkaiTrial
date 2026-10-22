// =====================================================================
// AutomationController.cs
// Location: MerkaiTrial.WebApi/Controllers/AutomationController.cs
//
// NEW FILE (098 — workflow automation).
//
//   GET    api/automation/rules              the rules page: list, plan limit
//   GET    api/automation/rules/{id}         one rule, for the editor
//   POST   api/automation/rules              add a rule
//   PUT    api/automation/rules/{id}         change a rule
//   POST   api/automation/rules/{id}/active  switch on / off
//   DELETE api/automation/rules/{id}         remove (soft)
//   GET    api/automation/runs?ruleId=       the rule log, newest 100
//   GET    api/automation/options            every list the editor needs
//   POST   api/automation/time-check         099: check time-based rules now
//
// PERMISSIONS — the "Automation" module in the role editor. Tenant admins
// pass every policy (PermissionHandler's bypass); anyone else needs the
// tick. Reps without it get 403 here and never see the page.
//
// 400 carries { error } with a sentence for a person — every problem with
// a rule at once, or the plan's limit.
// =====================================================================

using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.Automation;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.WebApi.Controllers
{
    [ApiController]
    [Route("api/automation")]
    public class AutomationController : ControllerBase
    {
        private readonly GetAutomationRulesHandler _list;
        private readonly GetAutomationRuleHandler _get;
        private readonly SaveAutomationRuleHandler _save;
        private readonly SetAutomationRuleActiveHandler _setActive;
        private readonly DeleteAutomationRuleHandler _delete;
        private readonly GetAutomationRunsHandler _runs;
        private readonly GetAutomationOptionsHandler _options;
        private readonly AutomationTimeRunner _timeRunner;          // 099
        private readonly ICurrentUserService _currentUserService;
        private readonly ILogger<AutomationController> _logger;

        public AutomationController(
            GetAutomationRulesHandler list,
            GetAutomationRuleHandler get,
            SaveAutomationRuleHandler save,
            SetAutomationRuleActiveHandler setActive,
            DeleteAutomationRuleHandler delete,
            GetAutomationRunsHandler runs,
            GetAutomationOptionsHandler options,
            AutomationTimeRunner timeRunner,
            ICurrentUserService currentUserService,
            ILogger<AutomationController> logger)
        {
            _timeRunner = timeRunner;
            _list = list;
            _get = get;
            _save = save;
            _setActive = setActive;
            _delete = delete;
            _runs = runs;
            _options = options;
            _currentUserService = currentUserService;
            _logger = logger;
        }

        [HttpGet("rules")]
        [Authorize(Policy = Policies.AutomationRead)]
        [ProducesResponseType(typeof(AutomationRulesPageDto), 200)]
        public async Task<IActionResult> List(CancellationToken ct = default)
        {
            var tenantId = _currentUserService.GetCurrentTenantId();
            try
            {
                return Ok(await _list.Handle(tenantId, ct));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error listing automation rules for tenant {TenantId}", tenantId);
                return StatusCode(500, new { error = "Failed to load the automation rules" });
            }
        }

        [HttpGet("rules/{id:guid}")]
        [Authorize(Policy = Policies.AutomationRead)]
        [ProducesResponseType(typeof(AutomationRuleDto), 200)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct = default)
        {
            var tenantId = _currentUserService.GetCurrentTenantId();
            try
            {
                var rule = await _get.Handle(tenantId, id, ct);
                return rule is null ? NotFound(new { error = "That rule no longer exists." }) : Ok(rule);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading automation rule {RuleId}", id);
                return StatusCode(500, new { error = "Failed to load the rule" });
            }
        }

        [HttpPost("rules")]
        [Authorize(Policy = Policies.AutomationCreate)]
        [ProducesResponseType(typeof(Guid), 200)]
        [ProducesResponseType(400)]
        public Task<IActionResult> Create([FromBody] AutomationRuleDto dto, CancellationToken ct = default)
            => SaveAsync(null, dto, ct);

        [HttpPut("rules/{id:guid}")]
        [Authorize(Policy = Policies.AutomationUpdate)]
        [ProducesResponseType(typeof(Guid), 200)]
        [ProducesResponseType(400)]
        [ProducesResponseType(404)]
        public Task<IActionResult> Update(Guid id, [FromBody] AutomationRuleDto dto, CancellationToken ct = default)
            => SaveAsync(id, dto, ct);

        private async Task<IActionResult> SaveAsync(Guid? id, AutomationRuleDto dto, CancellationToken ct)
        {
            var tenantId = _currentUserService.GetCurrentTenantId();
            try
            {
                if (dto is null) return BadRequest(new { error = "Nothing was sent." });
                var me = await _currentUserService.GetCurrentUserAsync();
                var savedId = await _save.Handle(tenantId, id, dto, me.FullName, ct);
                return Ok(savedId);
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving automation rule {RuleId} for tenant {TenantId}", id, tenantId);
                return StatusCode(500, new { error = "Failed to save the rule" });
            }
        }

        [HttpPost("rules/{id:guid}/active")]
        [Authorize(Policy = Policies.AutomationUpdate)]
        [ProducesResponseType(204)]
        [ProducesResponseType(400)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> SetActive(Guid id, [FromBody] SetAutomationRuleActiveDto dto, CancellationToken ct = default)
        {
            var tenantId = _currentUserService.GetCurrentTenantId();
            try
            {
                var me = await _currentUserService.GetCurrentUserAsync();
                await _setActive.Handle(tenantId, id, dto.IsActive, me.FullName, ct);
                return NoContent();
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error switching automation rule {RuleId}", id);
                return StatusCode(500, new { error = "Failed to switch the rule" });
            }
        }

        [HttpDelete("rules/{id:guid}")]
        [Authorize(Policy = Policies.AutomationDelete)]
        [ProducesResponseType(204)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct = default)
        {
            var tenantId = _currentUserService.GetCurrentTenantId();
            try
            {
                var me = await _currentUserService.GetCurrentUserAsync();
                await _delete.Handle(tenantId, id, me.FullName, ct);
                return NoContent();
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting automation rule {RuleId}", id);
                return StatusCode(500, new { error = "Failed to delete the rule" });
            }
        }

        [HttpGet("runs")]
        [Authorize(Policy = Policies.AutomationRead)]
        [ProducesResponseType(typeof(List<AutomationRunDto>), 200)]
        public async Task<IActionResult> Runs([FromQuery] Guid? ruleId, CancellationToken ct = default)
        {
            var tenantId = _currentUserService.GetCurrentTenantId();
            try
            {
                return Ok(await _runs.Handle(tenantId, ruleId, ct));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading the automation log for tenant {TenantId}", tenantId);
                return StatusCode(500, new { error = "Failed to load the rule log" });
            }
        }

        [HttpGet("options")]
        [Authorize(Policy = Policies.AutomationRead)]
        [ProducesResponseType(typeof(AutomationOptionsDto), 200)]
        public async Task<IActionResult> Options(CancellationToken ct = default)
        {
            var tenantId = _currentUserService.GetCurrentTenantId();
            try
            {
                return Ok(await _options.Handle(tenantId, ct));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading automation options for tenant {TenantId}", tenantId);
                return StatusCode(500, new { error = "Failed to load the rule editor" });
            }
        }

        /// <summary>
        /// POST /api/automation/time-check — 099. This workspace's
        /// time-based rules, checked now rather than at the worker's next
        /// pass. Same code as the worker (AutomationTimeRunner), so what it
        /// does is what the worker would have done.
        /// </summary>
        [HttpPost("time-check")]
        [Authorize(Policy = Policies.AutomationUpdate)]
        [ProducesResponseType(typeof(AutomationTimeCheckResult), 200)]
        public async Task<IActionResult> TimeCheck(CancellationToken ct = default)
        {
            var tenantId = _currentUserService.GetCurrentTenantId();
            try
            {
                return Ok(await _timeRunner.RunTenantAsync(tenantId, ct));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking time-based rules for tenant {TenantId}", tenantId);
                return StatusCode(500, new { error = "The time-based rules couldn't be checked just now" });
            }
        }
    }
}
