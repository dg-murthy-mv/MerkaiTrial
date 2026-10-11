// =====================================================================
// IFileStorageService.cs
// Location: MerkaiTrial.Application/Services/Storage/IFileStorageService.cs
// Abstraction over file storage. Swap LocalFileStorageService for
// AzureBlobStorageService in Program.cs — zero other code changes.
//
// 083 — THREE MEMBERS ADDED, for email attachments
//   SaveBytesAsync   a file that arrived as bytes (an email attachment),
//                    not as a browser upload. Same rules as SaveAsync:
//                    same size limit, same allowed types, same folders.
//   OpenReadAsync    read a stored file back — used to put a copy of an
//                    email's attachment on the lead or deal it was filed
//                    against, so deleting one never breaks the other.
//   WhyNotAllowed    the size/type check on its own, so a file can be
//                    refused BEFORE it is downloaded from Resend.
//
//   When the Azure Blob implementation is written, it implements these
//   three as well — the compiler will say so.
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

    /// <summary>
    /// 083. Save a file that arrived as bytes. Throws
    /// InvalidOperationException with a readable reason when the size or
    /// type is not allowed — call WhyNotAllowed first to skip quietly.
    /// </summary>
    Task<FileUploadResult> SaveBytesAsync(
        Guid tenantId,
        string entityType,
        string fileName,
        string? contentType,
        byte[] content,
        CancellationToken ct = default);

    /// <summary>
    /// 083. Open a stored file for reading, or null when it is gone.
    /// The caller disposes the stream.
    /// </summary>
    Task<Stream?> OpenReadAsync(string fileUrl, CancellationToken ct = default);

    /// <summary>
    /// 083. Null when a file of this type and size would be accepted;
    /// otherwise a short reason ("type not allowed", "larger than 10 MB").
    /// The file name is used to recognise a PDF sent as
    /// application/octet-stream, which mail programs do a lot.
    /// </summary>
    string? WhyNotAllowed(string? contentType, long sizeBytes, string? fileName = null);

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
