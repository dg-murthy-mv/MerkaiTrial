// =====================================================================
// LeadImportService.cs
// Location: MerkaiTrial.Admin.Web/Services/Leads/LeadImportService.cs
//
// NEW FILE. Same pattern as ActivityService — thin wrapper over ApiService.
//
// 092 — GetFieldsAsync: every field a column can be mapped onto (built-in,
// then the workspace's custom Lead fields), for a mapping screen redrawn
// after a failed check.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Core;
using MerkaiTrial.Application.Commands.Leads.Import;

namespace MerkaiTrial.Admin.Web.Services.Leads;

public interface ILeadImportService
{
    Task<ImportUploadResult> UploadAsync(IFormFile file, CancellationToken ct = default);
    Task<ImportPreview> PreviewAsync(ImportRequest request, CancellationToken ct = default);
    Task<ImportResult> CommitAsync(ImportRequest request, CancellationToken ct = default);
    Task<List<ImportField>> GetFieldsAsync(CancellationToken ct = default);     // 092
}

public class LeadImportService : ILeadImportService
{
    private readonly IApiService _api;

    public LeadImportService(IApiService api) => _api = api;

    public async Task<ImportUploadResult> UploadAsync(IFormFile file, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        using var stream = file.OpenReadStream();
        var fileContent = new StreamContent(stream);
        content.Add(fileContent, "file", file.FileName);

        return await _api.PostMultipartAsync<ImportUploadResult>("api/leads/import/upload", content);
    }

    public Task<ImportPreview> PreviewAsync(ImportRequest request, CancellationToken ct = default)
        => _api.PostAsync<ImportPreview>("api/leads/import/preview", request);

    public Task<ImportResult> CommitAsync(ImportRequest request, CancellationToken ct = default)
        => _api.PostAsync<ImportResult>("api/leads/import/commit", request);

    public Task<List<ImportField>> GetFieldsAsync(CancellationToken ct = default)
        => _api.GetAsync<List<ImportField>>("api/leads/import/fields");
}
