// =====================================================================
// IFileStorageService.cs
// Location: MerkaiTrial.Application/Services/Storage/IFileStorageService.cs
// Abstraction over file storage. Swap LocalFileStorageService for
// AzureBlobStorageService in Program.cs — zero other code changes.
// =====================================================================

using Microsoft.AspNetCore.Http;

namespace MerkaiTrial.Application.Services.Storage;

public interface IFileStorageService
{
    /// <summary>
    /// Save a file and return the stored URL/path.
    /// tenantId used to namespace storage per tenant.
    /// </summary>
    Task<FileUploadResult> SaveAsync(
        Guid tenantId,
        string entityType,
        IFormFile file,
        CancellationToken ct = default);

    /// <summary>Delete a file by its stored URL/path.</summary>
    Task DeleteAsync(string fileUrl, CancellationToken ct = default);

    /// <summary>Get a publicly accessible URL for download.</summary>
    string GetDownloadUrl(string fileUrl);
}

public class FileUploadResult
{
    public string FileUrl  { get; set; } = string.Empty;
    public long   FileSize { get; set; }
    public string MimeType { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
}



