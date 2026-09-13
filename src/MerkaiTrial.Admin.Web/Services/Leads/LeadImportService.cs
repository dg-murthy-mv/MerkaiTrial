// =====================================================================
// LeadImportService.cs
// Location: MerkaiTrial.Admin.Web/Services/Leads/LeadImportService.cs
//
// NEW FILE. Same pattern as ActivityService — thin wrapper over ApiService.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Core;
using MerkaiTrial.Application.Commands.Leads.Import;

namespace MerkaiTrial.Admin.Web.Services.Leads;

public interface ILeadImportService
{
    Task<ImportUploadResult> UploadAsync(IFormFile file, CancellationToken ct = default);
    Task<ImportPreview> PreviewAsync(ImportRequest request, CancellationToken ct = default);
    Task<ImportResult> CommitAsync(ImportRequest request, CancellationToken ct = default);
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
}
