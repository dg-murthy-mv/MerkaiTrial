// =====================================================================
// FILE: Pages/Assistant/Index.cshtml.cs
//
// NEW (122). The AI assistant as a page (/Assistant) — the same chat as
// the "Ask Merkai" panel, full-screen (handy on a phone). Its handlers
// also serve the panel:
//   GET  ?handler=Status   on/off, set up, questions left
//   POST ?handler=Ask      question + the conversation so far (JSON in a
//                          form field) → { answer, remaining } or { error }
//   POST ?handler=Help     124: kind, type, id, note → the record helpers on a
//                          lead's or deal's page; same answers
//   POST ?handler=Confirm  125: id → do a change the assistant prepared
//   POST ?handler=Cancel   125: id → drop it
//   POST ?handler=SaveNote 125: type, id, text → the helpers' "Save as note"
//                          → { done, message, link } or { error }
// Everything is decided by the WebApi (api/ai); this page only relays it.
// =====================================================================

using System.Text.Json;
using MerkaiTrial.Admin.Web.Services.Ai;
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.Services.Ai;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MerkaiTrial.Admin.Web.Pages.Assistant
{
    [RequiresPlanFeature(PlanFeatureKeys.AiAssistant)]
    public class IndexModel : PageModel
    {
        private readonly IAiAssistantApi _ai;
        private readonly ILogger<IndexModel> _logger;

        public IndexModel(IAiAssistantApi ai, ILogger<IndexModel> logger)
        {
            _ai = ai; _logger = logger;
        }

        public AiStatusDto? Status { get; private set; }
        public string? LoadError { get; private set; }

        public async Task OnGetAsync()
        {
            try { Status = await _ai.GetStatusAsync(); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Loading the AI assistant's status failed");
                LoadError = "The assistant could not be reached just now. Please try again.";
            }
        }

        public async Task<IActionResult> OnGetStatusAsync()
        {
            try { return new JsonResult(await _ai.GetStatusAsync()); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Loading the AI assistant's status failed");
                return new JsonResult(new { error = "The assistant could not be reached just now." }) { StatusCode = 502 };
            }
        }

        public async Task<IActionResult> OnPostHelpAsync(string? kind, string? type, Guid id, string? note)
        {
            try
            {
                var answer = await _ai.HelpAsync(new AiHelpDto { Kind = kind, Type = type, Id = id, Note = note });
                return new JsonResult(answer);
            }
            catch (InvalidOperationException ex)   // 400 / 403 from the API: its sentence
            {
                return new JsonResult(new { error = ex.Message }) { StatusCode = 400 };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The AI record helper did not answer");
                return new JsonResult(new { error = "The assistant couldn't answer just now. Please try again in a minute — this wasn't counted." })
                { StatusCode = 502 };
            }
        }

        public Task<IActionResult> OnPostConfirmAsync(Guid id) => ActAsync(() => _ai.ConfirmAsync(id));
        public Task<IActionResult> OnPostCancelAsync(Guid id) => ActAsync(() => _ai.CancelAsync(id));
        public Task<IActionResult> OnPostSaveNoteAsync(string? type, Guid id, string? text) =>
            ActAsync(() => _ai.SaveNoteAsync(type ?? "", id, text ?? ""));

        private async Task<IActionResult> ActAsync(Func<Task<AiActionResultDto>> act)
        {
            try
            {
                var result = await act();
                if (result is null)   // the API answered 404 (someone else's, expired, gone)
                    return new JsonResult(new { error = "This suggestion can't be found any more. Ask the assistant again." }) { StatusCode = 404 };
                return new JsonResult(result);
            }
            catch (InvalidOperationException ex)   // 400 / 403 from the API: its sentence
            {
                return new JsonResult(new { error = ex.Message }) { StatusCode = 400 };
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound || ex.Message.Contains("NotFound"))
            {
                return new JsonResult(new { error = "This suggestion can't be found any more. Ask the assistant again." }) { StatusCode = 404 };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "An AI action did not go through");
                return new JsonResult(new { error = "That couldn't be done just now. Please try again." }) { StatusCode = 502 };
            }
        }

        public async Task<IActionResult> OnPostAskAsync(string? question, string? history)
        {
            List<AiTurnDto>? turns = null;
            if (!string.IsNullOrWhiteSpace(history) && history.Length < 40_000)
            {
                try { turns = JsonSerializer.Deserialize<List<AiTurnDto>>(history, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
                catch (JsonException) { turns = null; }   // a broken history is dropped, not an error
            }

            try
            {
                var answer = await _ai.AskAsync(new AiAskDto { Question = question, History = turns });
                return new JsonResult(answer);
            }
            catch (InvalidOperationException ex)   // 400 / 403 from the API: its sentence
            {
                return new JsonResult(new { error = ex.Message }) { StatusCode = 400 };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The AI assistant did not answer");
                return new JsonResult(new { error = "The assistant couldn't answer just now. Please try again in a minute — this question wasn't counted." })
                { StatusCode = 502 };
            }
        }
    }
}
