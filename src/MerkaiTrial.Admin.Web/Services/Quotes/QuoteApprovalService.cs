// =====================================================================
// QuoteApprovalService.cs
// Location: MerkaiTrial.Admin.Web/Services/Quotes/QuoteApprovalService.cs
//
// NEW FILE (017). Thin wrapper over api/quote-approvals — same pattern as
// RecordVisibilityService. Tenant id is never sent; the API uses the
// caller's. A refusal from the API ("This quote is already waiting for
// approval") arrives as InvalidOperationException with that message.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Core;
using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Admin.Web.Services.Quotes;

public interface IQuoteApprovalService
{
    Task<QuoteApprovalSettingsDto> GetSettingsAsync();
    Task SaveSettingsAsync(SaveQuoteApprovalSettingsDto dto);

    Task<QuoteApprovalStateDto> GetStateAsync(Guid quoteId);
    Task SubmitAsync(Guid quoteId, string? comment);
    Task ApproveAsync(Guid quoteId, string? comment);
    Task RequestChangesAsync(Guid quoteId, string comment);
    Task RecallAsync(Guid quoteId);

    /// <summary>Requests waiting for the current user's decision.</summary>
    Task<List<PendingQuoteApprovalDto>> GetPendingAsync();
}

public class QuoteApprovalService : IQuoteApprovalService
{
    private const string Base = "api/quote-approvals";
    private readonly IApiService _api;

    public QuoteApprovalService(IApiService api) => _api = api;

    public async Task<QuoteApprovalSettingsDto> GetSettingsAsync()
        => await _api.GetAsync<QuoteApprovalSettingsDto>($"{Base}/settings");

    public async Task SaveSettingsAsync(SaveQuoteApprovalSettingsDto dto)
        => await _api.PutVoidAsync($"{Base}/settings", dto);

    public async Task<QuoteApprovalStateDto> GetStateAsync(Guid quoteId)
        => await _api.GetAsync<QuoteApprovalStateDto>($"{Base}/quotes/{quoteId}");

    public async Task SubmitAsync(Guid quoteId, string? comment)
        => await _api.PostVoidAsync($"{Base}/quotes/{quoteId}/submit", new QuoteApprovalActionDto(comment));

    public async Task ApproveAsync(Guid quoteId, string? comment)
        => await _api.PostVoidAsync($"{Base}/quotes/{quoteId}/approve", new QuoteApprovalActionDto(comment));

    public async Task RequestChangesAsync(Guid quoteId, string comment)
        => await _api.PostVoidAsync($"{Base}/quotes/{quoteId}/request-changes", new QuoteApprovalActionDto(comment));

    public async Task RecallAsync(Guid quoteId)
        => await _api.PostVoidAsync($"{Base}/quotes/{quoteId}/recall", new QuoteApprovalActionDto(null));

    public async Task<List<PendingQuoteApprovalDto>> GetPendingAsync()
        => await _api.GetAsync<List<PendingQuoteApprovalDto>>($"{Base}/pending") ?? new List<PendingQuoteApprovalDto>();
}
