using MerkaiTrial.Application.Services.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Services.Storage;

public class LocalFileStorageService : IFileStorageService
{
    private readonly string _baseDirectory;
    private readonly string _baseUrl;
    private readonly ILogger<LocalFileStorageService> _logger;

    // Max 10MB per file — reasonable for CRM docs
    private const long MaxFileSizeBytes = 10 * 1024 * 1024;

    private static readonly HashSet<string> AllowedMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "application/msword",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.ms-excel",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "image/jpeg", "image/png", "image/gif", "image/webp",
        "text/plain", "text/csv",
    };

    public LocalFileStorageService(
        IHostEnvironment env,
        ILogger<LocalFileStorageService> logger,
        string? baseUrl = null)
    {
        // Files stored at: {ContentRoot}/uploads/{tenantId}/{entityType}/{year}/
        _baseDirectory = Path.Combine(env.ContentRootPath, "uploads");
        _baseUrl = baseUrl ?? "/uploads";
        _logger = logger;
    }

    public async Task<FileUploadResult> SaveAsync(
        Guid tenantId,
        string entityType,
        IFormFile file,
        CancellationToken ct = default)
    {
        // Validate size
        if (file.Length > MaxFileSizeBytes)
            throw new InvalidOperationException(
                $"File size {file.Length} exceeds maximum {MaxFileSizeBytes} bytes");

        // Validate MIME type
        if (!AllowedMimeTypes.Contains(file.ContentType))
            throw new InvalidOperationException(
                $"File type '{file.ContentType}' is not allowed");

        // Build path: uploads/{tenantId}/{entityType}/{year}/{guid}{ext}
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        var guid = Guid.NewGuid().ToString("N");
        var year = DateTime.UtcNow.Year.ToString();
        var relPath = Path.Combine(
            tenantId.ToString("N"),
            entityType.ToLowerInvariant(),
            year,
            $"{guid}{ext}");

        var fullPath = Path.Combine(_baseDirectory, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        await using var stream = new FileStream(fullPath, FileMode.Create);
        await file.CopyToAsync(stream, ct);

        // Stored as forward-slash URL path (works on both Windows & Linux)
        var fileUrl = $"{_baseUrl}/{relPath.Replace('\\', '/')}";

        _logger.LogInformation("File saved: {FileName} → {FileUrl}", file.FileName, fileUrl);

        return new FileUploadResult
        {
            FileUrl = fileUrl,
            FileSize = file.Length,
            MimeType = file.ContentType,
            FileName = Path.GetFileName(file.FileName),
        };
    }

    public Task DeleteAsync(string fileUrl, CancellationToken ct = default)
    {
        // Convert URL back to disk path
        var relativePath = fileUrl.Replace(_baseUrl, string.Empty)
                                  .TrimStart('/', '\\')
                                  .Replace('/', Path.DirectorySeparatorChar);

        var fullPath = Path.Combine(_baseDirectory, relativePath);

        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
            _logger.LogInformation("File deleted: {Path}", fullPath);
        }

        return Task.CompletedTask;
    }

    public string GetDownloadUrl(string fileUrl) => fileUrl; // Local: URL is the path
}
