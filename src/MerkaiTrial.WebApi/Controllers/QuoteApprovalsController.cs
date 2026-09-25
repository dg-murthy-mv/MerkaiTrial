// =====================================================================
// QuoteApprovalsController.cs
// Location: MerkaiTrial.WebApi/Controllers/QuoteApprovalsController.cs
//
// COMPLETE FILE — replaces the 017 version.
//
//   GET    api/quote-approvals/settings                    Quotes.Read
//   PUT    api/quote-approvals/settings                    Settings.Update  (legacy: master switch only)
//   PUT    api/quote-approvals/enabled                     Settings.Update
//
//   GET    api/quote-approvals/rules                       Settings.Read
//   PUT    api/quote-approvals/rules                       Settings.Update
//   POST   api/quote-approvals/rules/reorder               Settings.Update
//   DELETE api/quote-approvals/rules/{id}                  Settings.Update
//   GET    api/quote-approvals/rules/{id}/in-flight        Settings.Read
//
//   GET    api/quote-approvals/pending                     Quotes.Read  (waiting for ME)
//   GET    api/quote-approvals/pending-chain               Quotes.Read  (same, with step context)
//   GET    api/quote-approvals/quotes/{id}                 Quotes.Read  + can read the quote
//   POST   api/quote-approvals/quotes/{id}/submit          Quotes.Update + can change the quote
//   POST   api/quote-approvals/quotes/{id}/approve         Quotes.Read  + is an approver for the current step
//   POST   api/quote-approvals/quotes/{id}/request-changes  Quotes.Read + is an approver for the current step
//   POST   api/quote-approvals/quotes/{id}/recall          Quotes.Update + requester or admin
//
// AUTHORIZATION CHANGE (027)
//   The 017 version guarded PUT settings with a hardcoded
//   `if (!me.IsTenantAdmin) return 403` inside the action. That is the
//   same anti-pattern round 024 removed from PipelineRulesController: it
//   means a Sales Manager you WANT to own quote policy can't be given
//   that power without making them a full workspace admin. Every rule
//   endpoint now uses the `settings.*` policies added in 024 instead.
//   PermissionHandler still grants workspace admins a blanket bypass, so
//   an admin is unaffected.
//
//   Policy names are built from the module and action constants rather
//   than spelled out, so they can't drift from ModuleCatalog. Both are
//   `const string`, so the concatenation is a compile-time constant and
//   is legal in an attribute. PermissionPolicyProvider builds any
//   "module.action" policy at runtime, so there is nothing to register.
//
//   Approve / request changes still need only Quotes.Read, on purpose: a
//   sales manager may have read-only rights on quotes and still be the
//   person who signs off the discount. Whether THEY may decide THIS step
//   is settled in the handler.
//
// Tenant id always comes from the signed-in user, never the request.
// Refusals → 400 { error }, "not yours to decide" → 403 { error }; the
// Admin.Web ApiService turns both into InvalidOperationException with the
// message, so the page can show it as is.
// =====================================================================

using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.Quotes;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.WebApi.Controllers;

[ApiController]
[Authorize]
[Route("api/quote-approvals")]
public class QuoteApprovalsController : ControllerBase
{
    // Compile-time constants, so these are legal in an [Authorize]
    // attribute and can never drift from ModuleCatalog's keys.
    private const string SettingsRead = Modules.Settings + "." + Actions.Read;
    private const string SettingsUpdate = Modules.Settings + "." + Actions.Update;
    private const string QuotesRead = Modules.Quotes + "." + Actions.Read;
    private const string QuotesUpdate = Modules.Quotes + "." + Actions.Update;

    private readonly GetQuoteApprovalSettingsHandler _getSettings;
    private readonly SaveQuoteApprovalSettingsHandler _saveSettings;
    private readonly GetApprovalRulesHandler _getRules;
    private readonly SaveApprovalRuleHandler _saveRule;
    private readonly ReorderApprovalRulesHandler _reorderRules;
    private readonly DeleteApprovalRuleHandler _deleteRule;
    private readonly GetQuoteApprovalStateHandler _getState;
    private readonly SubmitQuoteForApprovalHandler _submit;
    private readonly DecideQuoteApprovalHandler _decide;
    private readonly RecallQuoteApprovalHandler _recall;
    private readonly GetPendingQuoteApprovalsHandler _pending;
    private readonly GetPendingApprovalChainsHandler _pendingChain;
    private readonly QuoteApprovalEngine _access;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<QuoteApprovalsController> _logger;

    public QuoteApprovalsController(
        GetQuoteApprovalSettingsHandler getSettings,
        SaveQuoteApprovalSettingsHandler saveSettings,
        GetApprovalRulesHandler getRules,
        SaveApprovalRuleHandler saveRule,
        ReorderApprovalRulesHandler reorderRules,
        DeleteApprovalRuleHandler deleteRule,
        GetQuoteApprovalStateHandler getState,
        SubmitQuoteForApprovalHandler submit,
        DecideQuoteApprovalHandler decide,
        RecallQuoteApprovalHandler recall,
        GetPendingQuoteApprovalsHandler pending,
        GetPendingApprovalChainsHandler pendingChain,
        QuoteApprovalEngine access,
        ICurrentUserService currentUser,
        ILogger<QuoteApprovalsController> logger)
    {
        _getSettings = getSettings;
        _saveSettings = saveSettings;
        _getRules = getRules;
        _saveRule = saveRule;
        _reorderRules = reorderRules;
        _deleteRule = deleteRule;
        _getState = getState;
        _submit = submit;
        _decide = decide;
        _recall = recall;
        _pending = pending;
        _pendingChain = pendingChain;
        _access = access;
        _currentUser = currentUser;
        _logger = logger;
    }

    // ── Settings: the master switch ───────────────────────────────────

    [HttpGet("settings")]
    [Authorize(Policy = QuotesRead)]
    public Task<IActionResult> GetSettings(CancellationToken ct)
        => Run(async () => Ok(await _getSettings.Handle(TenantId(), ct)), "reading quote approval settings");

    /// <summary>
    /// LEGACY (017). Only the master switch is applied now — the limits it
    /// carries are decided by rules. Kept so an older client keeps working.
    /// </summary>
    [HttpPut("settings")]
    [Authorize(Policy = SettingsUpdate)]
    public Task<IActionResult> SaveSettings([FromBody] SaveQuoteApprovalSettingsDto dto, CancellationToken ct)
        => Run(async () =>
        {
            var me = await _currentUser.GetCurrentUserAsync();
            await _saveSettings.Handle(me.TenantId, dto.IsEnabled, me.FullName, ct);
            return Ok();
        }, "saving quote approval settings");

    [HttpPut("enabled")]
    [Authorize(Policy = SettingsUpdate)]
    public Task<IActionResult> SetEnabled([FromBody] SetApprovalsEnabledDto dto, CancellationToken ct)
        => Run(async () =>
        {
            var me = await _currentUser.GetCurrentUserAsync();
            await _saveSettings.Handle(me.TenantId, dto.IsEnabled, me.FullName, ct);
            return Ok();
        }, "switching quote approvals on or off");

    // ── Rules ─────────────────────────────────────────────────────────

    /// <summary>
    /// The whole rules screen in one call. <paramref name="currency"/> is
    /// the tenant's symbol, passed in by Admin.Web so the descriptions
    /// read in the right currency — the API has no view of the tenant's
    /// formatting.
    /// </summary>
    [HttpGet("rules")]
    [Authorize(Policy = SettingsRead)]
    public Task<IActionResult> GetRules([FromQuery] string? currency, CancellationToken ct)
        => Run(async () => Ok(await _getRules.Handle(TenantId(), currency ?? string.Empty, ct)),
               "reading approval rules");

    [HttpPut("rules")]
    [Authorize(Policy = SettingsUpdate)]
    public Task<IActionResult> SaveRule([FromBody] SaveApprovalRuleDto dto, CancellationToken ct)
        => Run(async () =>
        {
            var me = await _currentUser.GetCurrentUserAsync();
            var id = await _saveRule.Handle(me.TenantId, dto, me.FullName, ct);
            return Ok(new { id });
        }, "saving an approval rule");

    [HttpPost("rules/reorder")]
    [Authorize(Policy = SettingsUpdate)]
    public Task<IActionResult> ReorderRules([FromBody] ReorderApprovalRulesDto dto, CancellationToken ct)
        => Run(async () =>
        {
            var me = await _currentUser.GetCurrentUserAsync();
            await _reorderRules.Handle(me.TenantId, dto.RuleIdsInOrder ?? new List<Guid>(), me.FullName, ct);
            return Ok();
        }, "reordering approval rules");

    // Both verbs on one action. DELETE is the correct one; the POST alias
    // exists because Admin.Web's IApiService has proven Get/Post/Put
    // helpers and this round does not need to add a fourth.
    [HttpDelete("rules/{id:guid}")]
    [HttpPost("rules/{id:guid}/delete")]
    [Authorize(Policy = SettingsUpdate)]
    public Task<IActionResult> DeleteRule(Guid id, CancellationToken ct)
        => Run(async () =>
        {
            var me = await _currentUser.GetCurrentUserAsync();
            await _deleteRule.Handle(me.TenantId, id, me.FullName, ct);
            return Ok();
        }, "deleting an approval rule");

    /// <summary>
    /// How many approval requests are mid-chain on this rule, so the page
    /// can warn before the delete rather than after.
    /// </summary>
    [HttpGet("rules/{id:guid}/in-flight")]
    [Authorize(Policy = SettingsRead)]
    public Task<IActionResult> InFlight(Guid id, CancellationToken ct)
        => Run(async () => Ok(new InFlightCountDto(await _deleteRule.InFlightCountAsync(TenantId(), id, ct))),
               "counting requests in flight on a rule");

    // ── Waiting for me ────────────────────────────────────────────────

    [HttpGet("pending")]
    [Authorize(Policy = QuotesRead)]
    public Task<IActionResult> GetPending(CancellationToken ct)
        => Run(async () => Ok(await _pending.Handle(TenantId(), ct)), "reading pending quote approvals");

    /// <summary>The same queue, with which step it is and who it names.</summary>
    [HttpGet("pending-chain")]
    [Authorize(Policy = QuotesRead)]
    public Task<IActionResult> GetPendingChain(CancellationToken ct)
        => Run(async () => Ok(await _pendingChain.Handle(TenantId(), ct)), "reading pending approval chains");

    // ── One quote ─────────────────────────────────────────────────────

    [HttpGet("quotes/{id:guid}")]
    [Authorize(Policy = QuotesRead)]
    public Task<IActionResult> GetState(Guid id, CancellationToken ct)
        => Run(async () =>
        {
            if (!await _access.CanReadQuoteAsync(TenantId(), id, ct))
                return NotFound(new { error = $"Quote {id} not found" });

            return Ok(await _getState.Handle(TenantId(), id, ct));
        }, "reading quote approval state");

    [HttpPost("quotes/{id:guid}/submit")]
    [Authorize(Policy = QuotesUpdate)]
    public Task<IActionResult> Submit(Guid id, [FromBody] QuoteApprovalActionDto? dto, CancellationToken ct)
        => Run(async () =>
        {
            await _submit.Handle(TenantId(), id, dto?.Comment, ct);
            return Ok();
        }, "submitting quote for approval");

    [HttpPost("quotes/{id:guid}/approve")]
    [Authorize(Policy = QuotesRead)]
    public Task<IActionResult> Approve(Guid id, [FromBody] QuoteApprovalActionDto? dto, CancellationToken ct)
        => Run(async () =>
        {
            if (!await _access.CanReadQuoteAsync(TenantId(), id, ct))
                return NotFound(new { error = $"Quote {id} not found" });

            // The outcome says whether the chain moved on or finished, so
            // the page can stop telling the rep to send a quote that is
            // still waiting on step 2.
            var outcome = await _decide.Handle(TenantId(), id, approve: true, dto?.Comment, ct);
            return Ok(outcome);
        }, "approving quote");

    [HttpPost("quotes/{id:guid}/request-changes")]
    [Authorize(Policy = QuotesRead)]
    public Task<IActionResult> RequestChanges(Guid id, [FromBody] QuoteApprovalActionDto? dto, CancellationToken ct)
        => Run(async () =>
        {
            if (!await _access.CanReadQuoteAsync(TenantId(), id, ct))
                return NotFound(new { error = $"Quote {id} not found" });

            var outcome = await _decide.Handle(TenantId(), id, approve: false, dto?.Comment, ct);
            return Ok(outcome);
        }, "requesting changes on quote");

    [HttpPost("quotes/{id:guid}/recall")]
    [Authorize(Policy = QuotesUpdate)]
    public Task<IActionResult> Recall(Guid id, CancellationToken ct)
        => Run(async () =>
        {
            if (!await _access.CanReadQuoteAsync(TenantId(), id, ct))
                return NotFound(new { error = $"Quote {id} not found" });

            await _recall.Handle(TenantId(), id, ct);
            return Ok();
        }, "recalling quote approval");

    // =================================================================

    private Guid TenantId() => _currentUser.GetCurrentTenantId();

    private async Task<IActionResult> Run(Func<Task<IActionResult>> body, string what)
    {
        try
        {
            return await body();
        }
        catch (KeyNotFoundException ex)        { return NotFound(new { error = ex.Message }); }
        catch (InvalidOperationException ex)   { return BadRequest(new { error = ex.Message }); }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new { error = ex.Message }); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error {What}", what);
            return StatusCode(500, new { error = "Something went wrong. Please try again." });
        }
    }
}
