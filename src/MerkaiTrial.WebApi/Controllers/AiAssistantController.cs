// =====================================================================
// FILE: MerkaiTrial.WebApi/Controllers/AiAssistantController.cs
//
// NEW (122). The AI assistant, for people in a workspace whose plan
// includes it (ai_assistant — Professional, Enterprise, trial).
//
//   GET  /api/ai/status     on or off, set up or not, questions left this month
//   POST /api/ai/ask        { question, history[] } → { answer, remaining }
//   POST /api/ai/help       124: { kind, type, id, note? } → { answer, remaining }
//                           kind: summary | next_step | draft_email | draft_message
//                           type: lead | deal — the record helpers on its page
//   POST /api/ai/proposals/{id}/confirm   125: do a change the assistant
//                           prepared — only the person it was prepared for,
//                           once, within 30 minutes, with their Update
//                           permission checked again → { done, message, link }
//   POST /api/ai/proposals/{id}/cancel    125: drop it; nothing changes
//   POST /api/ai/save-note  125: { type, id, text } — the helpers' "Save as
//                           note" (Leads/Deals Update permission)
//   Someone else's proposal, or another workspace's, answers 404.
//   PUT  /api/ai/settings   { on?, records? } — turn it on or off, and (123)
//                           let it look at records or not (settings.update;
//                           workspace admins always). Leave one out to keep it.
//
// 123: Ask also works out what the person may READ (leads, deals,
// contacts, companies, quotes, invoices) with the SAME policies the rest of
// the API uses, and hands that to the assistant's record tools (AiCaller).
//
// Answers: 400 { error } with a sentence the person can act on (turned off,
// not set up, limit reached, too long); 502 { error } when the provider did
// not answer (nothing counted); 403 "Not in your plan" from the plan filter.
// The workspace and the person ALWAYS come from the sign-in, never from the
// request.
// =====================================================================

using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.Services.Ai;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.WebApi.Controllers;

[ApiController]
[Authorize]
[RequiresPlanFeature(PlanFeatureKeys.AiAssistant)]
[Route("api/ai")]
public class AiAssistantController : ControllerBase
{
    private static readonly (string Module, string Policy)[] Readable =
    {
        ("leads", Policies.LeadsRead), ("deals", Policies.DealsRead), ("contacts", Policies.ContactsRead),
        ("companies", Policies.CompaniesRead), ("quotes", Policies.QuotesRead), ("invoices", Policies.InvoicesRead),
    };

    private static readonly (string Module, string Policy)[] Changeable =
    {
        ("leads", Policies.LeadsUpdate), ("deals", Policies.DealsUpdate),
    };

    private readonly AiAssistantService _ai;
    private readonly AiActions _actions;
    private readonly IAuthorizationService _authz;
    private readonly ILogger<AiAssistantController> _logger;

    public AiAssistantController(AiAssistantService ai, AiActions actions, IAuthorizationService authz, ILogger<AiAssistantController> logger)
    {
        _ai = ai; _actions = actions; _authz = authz; _logger = logger;
    }

    public sealed class SaveNoteDto
    {
        public string? Type { get; set; }
        public Guid Id { get; set; }
        public string? Text { get; set; }
    }

    public sealed class SetOnDto
    {
        public bool? On { get; set; }
        /// <summary>123. Let the assistant look at records.</summary>
        public bool? Records { get; set; }
    }

    private Guid TenantId => Guid.TryParse(User.FindFirst("TenantId")?.Value, out var t) ? t : Guid.Empty;
    private Guid UserId => Guid.TryParse(User.FindFirst("UserId")?.Value, out var u) ? u : Guid.Empty;
    private string Who => User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? User.Identity?.Name ?? "someone";
    private string? FirstName => (User.Identity?.Name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        if (TenantId == Guid.Empty) return Forbid();
        return Ok(await _ai.GetStatusAsync(TenantId, ct));
    }

    [HttpPost("ask")]
    public async Task<IActionResult> Ask([FromBody] AiAskDto dto, CancellationToken ct)
    {
        if (TenantId == Guid.Empty || UserId == Guid.Empty) return Forbid();
        try
        {
            return Ok(await _ai.AskAsync(await CallerAsync(), dto ?? new AiAskDto(), ct));
        }
        catch (AiRefusedException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (AiProviderException)
        {
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = "The assistant couldn't answer just now. Please try again in a minute — this question wasn't counted." });
        }
    }

    /// <summary>124. Summarise / next step / draft email / draft message for one lead or deal.</summary>
    [HttpPost("help")]
    public async Task<IActionResult> Help([FromBody] AiHelpDto dto, CancellationToken ct)
    {
        if (TenantId == Guid.Empty || UserId == Guid.Empty) return Forbid();
        try
        {
            return Ok(await _ai.HelpAsync(await CallerAsync(), dto ?? new AiHelpDto(), ct));
        }
        catch (AiRefusedException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (AiProviderException)
        {
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = "The assistant couldn't answer just now. Please try again in a minute — this wasn't counted." });
        }
    }

    /// <summary>Who is asking, and what they may read — the same policies as the rest of the API.</summary>
    private async Task<AiCaller> CallerAsync()
    {
        var canRead = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (module, policy) in Readable)
            if ((await _authz.AuthorizeAsync(User, policy)).Succeeded) canRead.Add(module);
        var canChange = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (module, policy) in Changeable)
            if ((await _authz.AuthorizeAsync(User, policy)).Succeeded) canChange.Add(module);
        return new AiCaller { TenantId = TenantId, UserId = UserId, FirstName = FirstName, CanRead = canRead, CanChange = canChange };
    }

    // ── 125: prepared changes ────────────────────────────────────────

    [HttpPost("proposals/{id:guid}/confirm")]
    public Task<IActionResult> Confirm(Guid id, CancellationToken ct) =>
        ActAsync(who => _actions.ConfirmAsync(id, who, ct), ct);

    [HttpPost("proposals/{id:guid}/cancel")]
    public Task<IActionResult> Cancel(Guid id, CancellationToken ct) =>
        ActAsync(who => _actions.CancelAsync(id, who, ct), ct, needsRecords: false);

    [HttpPost("save-note")]
    public Task<IActionResult> SaveNote([FromBody] SaveNoteDto dto, CancellationToken ct) =>
        ActAsync(who => _actions.SaveNoteAsync(who, dto?.Type, dto?.Id ?? Guid.Empty, dto?.Text, ct), ct);

    private async Task<IActionResult> ActAsync(Func<AiCaller, Task<AiActionResultDto>> act, CancellationToken ct, bool needsRecords = true)
    {
        if (TenantId == Guid.Empty || UserId == Guid.Empty) return Forbid();
        if (needsRecords)
        {
            // Switched off since it was prepared? Then nothing more happens.
            var status = await _ai.GetStatusAsync(TenantId, ct);
            if (!status.IsOn || !status.RecordsOn)
                return BadRequest(new { error = "The assistant is turned off, or not allowed to look at records, in this workspace." });
        }
        try
        {
            return Ok(await act(await CallerAsync()));
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = "Not found." });
        }
        catch (AiRefusedException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPut("settings")]
    [Authorize(Policy = Policies.SettingsUpdate)]
    public async Task<IActionResult> SetOn([FromBody] SetOnDto dto, CancellationToken ct)
    {
        if (TenantId == Guid.Empty) return Forbid();
        if (dto is null || (dto.On is null && dto.Records is null))
            return BadRequest(new { error = "Say what to change: on, records, or both." });
        var status = await _ai.SetAsync(TenantId, dto.On, dto.Records, Who, ct);
        _logger.LogInformation("AI assistant settings for tenant {TenantId} by {User}: on={On} records={Records}",
            TenantId, Who, status.IsOn, status.RecordsOn);
        return Ok(status);
    }
}
