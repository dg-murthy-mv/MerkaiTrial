// =====================================================================
// QuoteApprovalService.cs
// Location: MerkaiTrial.Admin.Web/Services/Quotes/QuoteApprovalService.cs
//
// COMPLETE FILE — replaces the 017 version.
//
// Thin wrapper over api/quote-approvals — same pattern as
// RecordVisibilityService. Tenant id is never sent; the API uses the
// caller's. A refusal from the API ("You already have a rule called …")
// arrives as InvalidOperationException with that message.
//
// 027 adds the rule endpoints. Every 017 method keeps its exact
// signature, so nothing that already calls this service needs touching.
//
// ONLY Get / Post / Put HELPERS ARE USED. IApiService's proven surface is
// GetAsync<T>, PostVoidAsync and PutVoidAsync, so this round doesn't add
// a fourth: deleting a rule goes through a POST alias on the API, and the
// approve/request-changes calls stay void — the queue page already knows
// which step it was showing, so it can word its own confirmation without
// a return value.
//
// NOTE: the typed HTTP clients in Admin.Web ARE hand-registered (unlike
// handlers, which Scrutor scans). IQuoteApprovalService is already in
// Startup/AdminWebServiceRegistration.cs from 017 — nothing to add.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Core;
using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Admin.Web.Services.Quotes;

public interface IQuoteApprovalService
{
    // ── 017: the master switch ────────────────────────────────────────
    Task<QuoteApprovalSettingsDto> GetSettingsAsync();
    Task SaveSettingsAsync(SaveQuoteApprovalSettingsDto dto);

    // ── 027: rules ────────────────────────────────────────────────────

    /// <summary>
    /// The whole rules screen. <paramref name="currencySymbol"/> is passed
    /// in because the API has no view of the tenant's formatting.
    /// </summary>
    Task<ApprovalRulesPageDto> GetRulesAsync(string currencySymbol);

    Task SaveRuleAsync(SaveApprovalRuleDto dto);
    Task ReorderRulesAsync(List<Guid> ruleIdsInOrder);
    Task DeleteRuleAsync(Guid ruleId);
    Task SetEnabledAsync(bool isEnabled);

    /// <summary>Approval requests mid-chain on this rule — ask before deleting it.</summary>
    Task<int> InFlightCountAsync(Guid ruleId);

    // ── One quote ─────────────────────────────────────────────────────
    Task<QuoteApprovalStateDto> GetStateAsync(Guid quoteId);
    Task SubmitAsync(Guid quoteId, string? comment);
    Task ApproveAsync(Guid quoteId, string? comment);
    Task RequestChangesAsync(Guid quoteId, string comment);
    Task RecallAsync(Guid quoteId);

    // ── The queue ─────────────────────────────────────────────────────

    /// <summary>Requests waiting for the current user's decision.</summary>
    Task<List<PendingQuoteApprovalDto>> GetPendingAsync();

    /// <summary>The same, with which step it is and who that step names.</summary>
    Task<List<PendingApprovalChainDto>> GetPendingChainAsync();
}

public class QuoteApprovalService : IQuoteApprovalService
{
    private const string Base = "api/quote-approvals";
    private readonly IApiService _api;

    public QuoteApprovalService(IApiService api) => _api = api;

    // ── master switch ─────────────────────────────────────────────────

    public async Task<QuoteApprovalSettingsDto> GetSettingsAsync()
        => await _api.GetAsync<QuoteApprovalSettingsDto>($"{Base}/settings");

    public async Task SaveSettingsAsync(SaveQuoteApprovalSettingsDto dto)
        => await _api.PutVoidAsync($"{Base}/settings", dto);

    public async Task SetEnabledAsync(bool isEnabled)
        => await _api.PutVoidAsync($"{Base}/enabled", new SetApprovalsEnabledDto(isEnabled));

    // ── rules ─────────────────────────────────────────────────────────

    public async Task<ApprovalRulesPageDto> GetRulesAsync(string currencySymbol)
        => await _api.GetAsync<ApprovalRulesPageDto>(
               $"{Base}/rules?currency={Uri.EscapeDataString(currencySymbol ?? string.Empty)}");

    public async Task SaveRuleAsync(SaveApprovalRuleDto dto)
        => await _api.PutVoidAsync($"{Base}/rules", dto);

    public async Task ReorderRulesAsync(List<Guid> ruleIdsInOrder)
        => await _api.PostVoidAsync($"{Base}/rules/reorder",
               new ReorderApprovalRulesDto(ruleIdsInOrder ?? new List<Guid>()));

    public async Task DeleteRuleAsync(Guid ruleId)
        => await _api.PostVoidAsync($"{Base}/rules/{ruleId}/delete", new { });

    public async Task<int> InFlightCountAsync(Guid ruleId)
        => (await _api.GetAsync<InFlightCountDto>($"{Base}/rules/{ruleId}/in-flight"))?.Count ?? 0;

    // ── one quote ─────────────────────────────────────────────────────

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

    // ── the queue ─────────────────────────────────────────────────────

    public async Task<List<PendingQuoteApprovalDto>> GetPendingAsync()
        => await _api.GetAsync<List<PendingQuoteApprovalDto>>($"{Base}/pending") ?? new List<PendingQuoteApprovalDto>();

    public async Task<List<PendingApprovalChainDto>> GetPendingChainAsync()
        => await _api.GetAsync<List<PendingApprovalChainDto>>($"{Base}/pending-chain") ?? new List<PendingApprovalChainDto>();
}
