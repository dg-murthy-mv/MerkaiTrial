// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Services/Ai/AiAssistantApi.cs
//
// NEW (122). Admin.Web's side of the AI assistant: calls to the WebApi
// (api/ai/*), which holds the provider key and every rule. Admin.Web never
// talks to the AI provider itself.
//
// AiPanelAvailability: whether the workspace's admin has turned the
// assistant on — the layout asks it to decide whether to show the
// "Ask Merkai" button. One small read, only when the plan includes it.
//
// 123: SetRecordsAsync — the second switch (look at records or not).
// 124: HelpAsync — the record helpers; AiPanelAvailability.RecordsOnAsync
// decides whether a lead's or deal's page shows them.
// 125: ConfirmAsync / CancelAsync (prepared changes) and SaveNoteAsync.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Core;
using MerkaiTrial.Application.Services.Ai;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Admin.Web.Services.Ai
{
    public interface IAiAssistantApi
    {
        Task<AiStatusDto> GetStatusAsync();
        Task<AiAnswerDto> AskAsync(AiAskDto ask);
        Task<AiStatusDto> SetOnAsync(bool on);
        /// <summary>123. Let the assistant look at records (only what the asking person can see).</summary>
        Task<AiStatusDto> SetRecordsAsync(bool records);
        /// <summary>124. A record helper on a lead's or deal's page.</summary>
        Task<AiAnswerDto> HelpAsync(AiHelpDto help);
        /// <summary>125. Do / drop a change the assistant prepared.</summary>
        Task<AiActionResultDto> ConfirmAsync(Guid proposalId);
        Task<AiActionResultDto> CancelAsync(Guid proposalId);
        /// <summary>125. The helpers' "Save as note".</summary>
        Task<AiActionResultDto> SaveNoteAsync(string type, Guid id, string text);
    }

    public class AiAssistantApi : IAiAssistantApi
    {
        private readonly IApiService _api;
        public AiAssistantApi(IApiService api) => _api = api;

        public Task<AiStatusDto> GetStatusAsync() => _api.GetAsync<AiStatusDto>("api/ai/status");
        public Task<AiAnswerDto> AskAsync(AiAskDto ask) => _api.PostAsync<AiAnswerDto>("api/ai/ask", ask);
        public Task<AiStatusDto> SetOnAsync(bool on) => _api.PutAsync<AiStatusDto>("api/ai/settings", new { on });
        public Task<AiStatusDto> SetRecordsAsync(bool records) => _api.PutAsync<AiStatusDto>("api/ai/settings", new { records });
        public Task<AiAnswerDto> HelpAsync(AiHelpDto help) => _api.PostAsync<AiAnswerDto>("api/ai/help", help);
        public Task<AiActionResultDto> ConfirmAsync(Guid proposalId) => _api.PostAsync<AiActionResultDto>($"api/ai/proposals/{proposalId}/confirm", new { });
        public Task<AiActionResultDto> CancelAsync(Guid proposalId) => _api.PostAsync<AiActionResultDto>($"api/ai/proposals/{proposalId}/cancel", new { });
        public Task<AiActionResultDto> SaveNoteAsync(string type, Guid id, string text) => _api.PostAsync<AiActionResultDto>("api/ai/save-note", new { type, id, text });
    }

    public class AiPanelAvailability
    {
        private readonly FlowDbContext _db;
        private bool? _on;
        public AiPanelAvailability(FlowDbContext db) => _db = db;

        private bool? _records;

        /// <summary>The workspace's admin has turned the assistant on (the tenant filter scopes the read).</summary>
        public async Task<bool> IsOnAsync()
        {
            if (_on is { } known) return known;
            try { _on = await _db.AiSettings.AsNoTracking().AnyAsync(s => s.IsOn); }
            catch { _on = false; }   // never break a page over the button
            return _on.Value;
        }

        /// <summary>124. On, AND allowed to look at records — the record helpers need both.</summary>
        public async Task<bool> RecordsOnAsync()
        {
            if (_records is { } known) return known;
            try { _records = await _db.AiSettings.AsNoTracking().AnyAsync(s => s.IsOn && s.CanReadRecords); }
            catch { _records = false; }
            return _records.Value;
        }
    }
}
