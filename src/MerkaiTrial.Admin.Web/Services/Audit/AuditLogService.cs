// =====================================================================
// AuditLogService.cs
// Location: MerkaiTrial.Admin.Web/Services/Audit/AuditLogService.cs
//
// NEW FILE. Same thin-wrapper pattern as ActivityService.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Core;
using MerkaiTrial.Application.Queries;

namespace MerkaiTrial.Admin.Web.Services.Audit;

public record AuditFilterOptions(List<string> EntityTypes, List<string> Actions);

public interface IAuditLogService
{
    Task<AuditLogPage> GetAsync(
        string? search, string? entityType, string? action,
        DateTime? fromUtc, DateTime? toUtc, int page, int pageSize,
        CancellationToken ct = default);

    Task<AuditFilterOptions> GetFilterOptionsAsync(CancellationToken ct = default);
}

public class AuditLogService : IAuditLogService
{
    private readonly IApiService _api;

    public AuditLogService(IApiService api) => _api = api;

    public async Task<AuditLogPage> GetAsync(
        string? search, string? entityType, string? action,
        DateTime? fromUtc, DateTime? toUtc, int page, int pageSize,
        CancellationToken ct = default)
    {
        var url = $"api/audit?page={page}&pageSize={pageSize}";

        if (!string.IsNullOrWhiteSpace(search))     url += $"&search={Uri.EscapeDataString(search)}";
        if (!string.IsNullOrWhiteSpace(entityType)) url += $"&entityType={Uri.EscapeDataString(entityType)}";
        if (!string.IsNullOrWhiteSpace(action))     url += $"&action={Uri.EscapeDataString(action)}";
        if (fromUtc.HasValue) url += $"&fromUtc={fromUtc.Value:O}";
        if (toUtc.HasValue)   url += $"&toUtc={toUtc.Value:O}";

        return await _api.GetAsync<AuditLogPage>(url);
    }

    public async Task<AuditFilterOptions> GetFilterOptionsAsync(CancellationToken ct = default)
        => await _api.GetAsync<AuditFilterOptions>("api/audit/filters");
}
