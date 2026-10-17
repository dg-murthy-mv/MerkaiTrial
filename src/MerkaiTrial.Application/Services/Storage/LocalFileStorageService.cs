// =====================================================================
// LocalFileStorageService.cs
// Location: MerkaiTrial.Application/Services/Storage/LocalFileStorageService.cs
//
// COMPLETE FILE — replaces the existing one.
//
// 083 — email attachments
//   • SaveBytesAsync: the same checks and the same folder layout as
//     SaveAsync, for a file that arrived as bytes. SaveAsync now goes
//     through the same private writer, so the two cannot drift.
//   • OpenReadAsync: reads a stored file back, refusing any path that
//     resolves outside the uploads folder. A FileUrl is our own data, but
//     "../../appsettings.json" must not become readable because a row was
//     ever written badly.
//   • WhyNotAllowed: the size/type check on its own. A mail program often
//     labels a PDF "application/octet-stream"; the extension is then used
//     to recognise it, and only for the types already allowed here.
//   • The extension written to disk is cleaned: letters and digits only,
//     at most 10 — the name comes from a stranger's email.
//
// 091 — the two refusals in SaveAsync are written for the person who
//   pressed Upload. They reach the page word for word, and used to read
//   "File type 'text/html' is not allowed" and "File size 11534336
//   exceeds maximum 10485760 bytes". Now:
//     'page.html' can't be attached — that type of file isn't allowed. …
//     'huge.pdf' is 11.0 MB. Files can be up to 10 MB.
//   The checks themselves are unchanged.
// =====================================================================

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

    /// <summary>
    /// 083. Extension → the allowed type it stands for. Used ONLY when the
    /// sender labelled the file application/octet-stream (or nothing), so
    /// it can never widen the list above.
    /// </summary>
    private static readonly Dictionary<string, string> TypeByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"]  = "application/pdf",
        [".doc"]  = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"]  = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".jpg"]  = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"]  = "image/png",
        [".gif"]  = "image/gif",
        [".webp"] = "image/webp",
        [".txt"]  = "text/plain",
        [".csv"]  = "text/csv",
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
        // Validate size — 091: in words a person can act on.
        if (file.Length > MaxFileSizeBytes)
            throw new InvalidOperationException(TooLargeMessage(file.FileName, file.Length));

        // Validate MIME type — 091: likewise.
        if (!AllowedMimeTypes.Contains(file.ContentType))
            throw new InvalidOperationException(
                $"'{SafeName(file.FileName)}' can't be attached — that type of file isn't allowed. " +
                "Use a PDF, Word or Excel file, an image (JPG, PNG, GIF, WebP), or a text or CSV file.");

        var (fullPath, fileUrl) = BuildPath(tenantId, entityType, file.FileName);

        await using (var stream = new FileStream(fullPath, FileMode.Create))
        {
            await file.CopyToAsync(stream, ct);
        }

        _logger.LogInformation("File saved: {FileName} → {FileUrl}", file.FileName, fileUrl);

        return new FileUploadResult
        {
            FileUrl = fileUrl,
            FileSize = file.Length,
            MimeType = file.ContentType,
            FileName = Path.GetFileName(file.FileName),
        };
    }

    public async Task<FileUploadResult> SaveBytesAsync(
        Guid tenantId,
        string entityType,
        string fileName,
        string? contentType,
        byte[] content,
        CancellationToken ct = default)
    {
        content ??= Array.Empty<byte>();

        var refusal = WhyNotAllowed(contentType, content.LongLength, fileName);
        if (refusal is not null)
            throw new InvalidOperationException($"'{SafeName(fileName)}' was not saved: {refusal}.");

        var type = Resolve(contentType, fileName)!;
        var (fullPath, fileUrl) = BuildPath(tenantId, entityType, fileName);

        await File.WriteAllBytesAsync(fullPath, content, ct);

        _logger.LogInformation("File saved: {FileName} ({Bytes} bytes) → {FileUrl}",
            SafeName(fileName), content.LongLength, fileUrl);

        return new FileUploadResult
        {
            FileUrl = fileUrl,
            FileSize = content.LongLength,
            MimeType = type,
            FileName = SafeName(fileName),
        };
    }

    public Task<Stream?> OpenReadAsync(string fileUrl, CancellationToken ct = default)
    {
        var fullPath = ResolveInsideBase(fileUrl);
        if (fullPath is null || !File.Exists(fullPath))
            return Task.FromResult<Stream?>(null);

        Stream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Task.FromResult<Stream?>(stream);
    }

    /// <summary>091. "'huge.pdf' is 11.0 MB. Files can be up to 10 MB."</summary>
    private static string TooLargeMessage(string? fileName, long sizeBytes)
        => $"'{SafeName(fileName)}' is {(sizeBytes / (1024d * 1024d)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} MB. " +
           $"Files can be up to {MaxFileSizeBytes / (1024 * 1024)} MB.";

    public string? WhyNotAllowed(string? contentType, long sizeBytes, string? fileName = null)
    {
        if (sizeBytes <= 0) return "the file is empty";
        if (sizeBytes > MaxFileSizeBytes) return $"larger than {MaxFileSizeBytes / (1024 * 1024)} MB";
        if (Resolve(contentType, fileName) is null) return "that type of file is not allowed";
        return null;
    }

    public Task DeleteAsync(string fileUrl, CancellationToken ct = default)
    {
        // 083: resolved through the same guard as OpenReadAsync, so a bad
        // FileUrl can never delete anything outside the uploads folder.
        var fullPath = ResolveInsideBase(fileUrl);

        if (fullPath is not null && File.Exists(fullPath))
        {
            File.Delete(fullPath);
            _logger.LogInformation("File deleted: {Path}", fullPath);
        }

        return Task.CompletedTask;
    }

    public string GetDownloadUrl(string fileUrl) => fileUrl; // Local: URL is the path

    // ── helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// The allowed type this file counts as, or null. A declared type wins
    /// when it is on the list; octet-stream / blank falls back to the
    /// extension; anything else is refused.
    /// </summary>
    private static string? Resolve(string? contentType, string? fileName)
    {
        var declared = (contentType ?? string.Empty).Split(';')[0].Trim();

        if (AllowedMimeTypes.Contains(declared)) return declared.ToLowerInvariant();

        var generic = declared.Length == 0
                   || declared.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase);

        if (generic && !string.IsNullOrWhiteSpace(fileName)
            && TypeByExtension.TryGetValue(Path.GetExtension(fileName), out var byExt))
            return byExt;

        return null;
    }

    /// <summary>uploads/{tenantId}/{entityType}/{year}/{guid}{ext} — unchanged layout.</summary>
    private (string FullPath, string FileUrl) BuildPath(Guid tenantId, string entityType, string? fileName)
    {
        var ext = CleanExtension(fileName);
        var guid = Guid.NewGuid().ToString("N");
        var year = DateTime.UtcNow.Year.ToString();
        var relPath = Path.Combine(
            tenantId.ToString("N"),
            CleanSegment(entityType),
            year,
            $"{guid}{ext}");

        var fullPath = Path.Combine(_baseDirectory, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        // Stored as forward-slash URL path (works on both Windows & Linux)
        return (fullPath, $"{_baseUrl}/{relPath.Replace('\\', '/')}");
    }

    private string? ResolveInsideBase(string? fileUrl)
    {
        if (string.IsNullOrWhiteSpace(fileUrl)) return null;

        var relativePath = fileUrl.StartsWith(_baseUrl, StringComparison.OrdinalIgnoreCase)
            ? fileUrl[_baseUrl.Length..]
            : fileUrl;

        relativePath = relativePath.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar);

        var baseFull = Path.GetFullPath(_baseDirectory);
        var full = Path.GetFullPath(Path.Combine(baseFull, relativePath));

        var basePrefix = baseFull.EndsWith(Path.DirectorySeparatorChar)
            ? baseFull
            : baseFull + Path.DirectorySeparatorChar;

        if (!full.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Refused a file path outside the uploads folder");
            return null;
        }

        return full;
    }

    private static string CleanExtension(string? fileName)
    {
        var ext = Path.GetExtension(fileName ?? string.Empty);
        if (string.IsNullOrEmpty(ext)) return string.Empty;

        var letters = new string(ext.Skip(1).Where(char.IsLetterOrDigit).Take(10).ToArray());
        return letters.Length == 0 ? string.Empty : "." + letters.ToLowerInvariant();
    }

    private static string CleanSegment(string? value)
    {
        var cleaned = new string((value ?? "file").Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
        return cleaned.Length == 0 ? "file" : cleaned.ToLowerInvariant();
    }

    /// <summary>The name shown to people: no folders, no control characters, at most 255.</summary>
    private static string SafeName(string? fileName)
    {
        var name = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/'));
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length == 0) name = "attachment";
        return name.Length <= 255 ? name : name[^255..];
    }
}
