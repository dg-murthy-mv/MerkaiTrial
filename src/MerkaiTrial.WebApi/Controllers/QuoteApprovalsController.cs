// =====================================================================
// QuoteApprovalsController.cs
// Location: MerkaiTrial.WebApi/Controllers/QuoteApprovalsController.cs
//
// NEW FILE (017).
//
//   GET  api/quote-approvals/settings                   Quotes.Read
//   PUT  api/quote-approvals/settings                   workspace admin only
//   GET  api/quote-approvals/pending                    Quotes.Read  (waiting for ME)
//   GET  api/quote-approvals/quotes/{id}                Quotes.Read  + can read the quote
//   POST api/quote-approvals/quotes/{id}/submit         Quotes.Update + can change the quote
//   POST api/quote-approvals/quotes/{id}/approve        Quotes.Read  + is an approver
//   POST api/quote-approvals/quotes/{id}/request-changes Quotes.Read + is an approver
//   POST api/quote-approvals/quotes/{id}/recall         Quotes.Update + requester or admin
//
// Approve / request changes need only Quotes.Read on purpose: a sales
// manager may have read-only rights on quotes and still be the person who
// signs off the discount. Whether THEY may approve THIS quote is decided
// in the handler (manager of the deal owner's team, or admin; never the
// requester).
//
// Tenant id always comes from the signed-in user, never the request.
// Refusals → 400 { error }, "not yours to decide" → 403 { error }; the
// Admin.Web ApiService turns both into InvalidOperationException with the
// message, so the page can show it as is.
// =====================================================================

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
    private readonly GetQuoteApprovalSettingsHandler _getSettings;
    private readonly SaveQuoteApprovalSettingsHandler _saveSettings;
    private readonly GetQuoteApprovalStateHandler _getState;
    private readonly SubmitQuoteForApprovalHandler _submit;
    private readonly DecideQuoteApprovalHandler _decide;
    private readonly RecallQuoteApprovalHandler _recall;
    private readonly GetPendingQuoteApprovalsHandler _pending;
    private readonly QuoteApprovalEngine _access;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<QuoteApprovalsController> _logger;

    public QuoteApprovalsController(
        GetQuoteApprovalSettingsHandler getSettings,
        SaveQuoteApprovalSettingsHandler saveSettings,
        GetQuoteApprovalStateHandler getState,
        SubmitQuoteForApprovalHandler submit,
        DecideQuoteApprovalHandler decide,
        RecallQuoteApprovalHandler recall,
        GetPendingQuoteApprovalsHandler pending,
        QuoteApprovalEngine access,
        ICurrentUserService currentUser,
        ILogger<QuoteApprovalsController> logger)
    {
        _getSettings = getSettings;
        _saveSettings = saveSettings;
        _getState = getState;
        _submit = submit;
        _decide = decide;
        _recall = recall;
        _pending = pending;
        _access = access;
        _currentUser = currentUser;
        _logger = logger;
    }

    // ── Settings ──────────────────────────────────────────────────────

    [HttpGet("settings")]
    [Authorize(Policy = "Quotes.Read")]
    public Task<IActionResult> GetSettings(CancellationToken ct)
        => Run(async () => Ok(await _getSettings.Handle(TenantId(), ct)), "reading quote approval settings");

    [HttpPut("settings")]
    public Task<IActionResult> SaveSettings([FromBody] SaveQuoteApprovalSettingsDto dto, CancellationToken ct)
        => Run(async () =>
        {
            var me = await _currentUser.GetCurrentUserAsync();
            if (!me.IsTenantAdmin)
                return StatusCode(403, new { error = "Only workspace admins can change the approval rules." });

            await _saveSettings.Handle(me.TenantId, dto, me.FullName, ct);
            return Ok();
        }, "saving quote approval settings");

    // ── Waiting for me ────────────────────────────────────────────────

    [HttpGet("pending")]
    [Authorize(Policy = "Quotes.Read")]
    public Task<IActionResult> GetPending(CancellationToken ct)
        => Run(async () => Ok(await _pending.Handle(TenantId(), ct)), "reading pending quote approvals");

    // ── One quote ─────────────────────────────────────────────────────

    [HttpGet("quotes/{id:guid}")]
    [Authorize(Policy = "Quotes.Read")]
    public Task<IActionResult> GetState(Guid id, CancellationToken ct)
        => Run(async () =>
        {
            if (!await _access.CanReadQuoteAsync(TenantId(), id, ct))
                return NotFound(new { error = $"Quote {id} not found" });

            return Ok(await _getState.Handle(TenantId(), id, ct));
        }, "reading quote approval state");

    [HttpPost("quotes/{id:guid}/submit")]
    [Authorize(Policy = "Quotes.Update")]
    public Task<IActionResult> Submit(Guid id, [FromBody] QuoteApprovalActionDto? dto, CancellationToken ct)
        => Run(async () =>
        {
            await _submit.Handle(TenantId(), id, dto?.Comment, ct);
            return Ok();
        }, "submitting quote for approval");

    [HttpPost("quotes/{id:guid}/approve")]
    [Authorize(Policy = "Quotes.Read")]
    public Task<IActionResult> Approve(Guid id, [FromBody] QuoteApprovalActionDto? dto, CancellationToken ct)
        => Run(async () =>
        {
            if (!await _access.CanReadQuoteAsync(TenantId(), id, ct))
                return NotFound(new { error = $"Quote {id} not found" });

            await _decide.Handle(TenantId(), id, approve: true, dto?.Comment, ct);
            return Ok();
        }, "approving quote");

    [HttpPost("quotes/{id:guid}/request-changes")]
    [Authorize(Policy = "Quotes.Read")]
    public Task<IActionResult> RequestChanges(Guid id, [FromBody] QuoteApprovalActionDto? dto, CancellationToken ct)
        => Run(async () =>
        {
            if (!await _access.CanReadQuoteAsync(TenantId(), id, ct))
                return NotFound(new { error = $"Quote {id} not found" });

            await _decide.Handle(TenantId(), id, approve: false, dto?.Comment, ct);
            return Ok();
        }, "requesting changes on quote");

    [HttpPost("quotes/{id:guid}/recall")]
    [Authorize(Policy = "Quotes.Update")]
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
