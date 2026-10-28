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
//
// 105 — WHAT IS INSIDE, NOT WHAT IT SAYS (VUL-13)
//   The type a browser or mail program declares is chosen by whoever sends
//   the file, and until now it was all that was checked: a web page sent
//   as "image/png" was stored and served back as one. Now every save —
//   uploads (SaveAsync) and email attachments (SaveBytesAsync) — goes
//   through FileContentCheck:
//     • the name must end in an allowed type (or, with no ending at all,
//       the declared type picks one and the ending is added);
//     • the bytes must really be that type (PDF, PNG, a .docx zip, text…);
//     • Word and Excel files with macros are refused.
//   The stored type is the proven one; the declared type is ignored.
//   Refusals are sentences for the person, as in 091.
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

    // 105: the allowed types now live in FileContentCheck.ByExtension,
    // with the signature each one's bytes must have.

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

        if (file.Length == 0)
            throw new InvalidOperationException($"'{SafeName(file.FileName)}' is empty.");

        // 105 — the type comes from the name and is proved by the contents.
        var expected = FileContentCheck.Expected(file.FileName, file.ContentType)
            ?? throw new InvalidOperationException(NotAllowedMessage(file.FileName));

        byte[] content;
        using (var buffer = new MemoryStream((int)file.Length))
        {
            await file.CopyToAsync(buffer, ct);
            content = buffer.ToArray();
        }

        if (FileContentCheck.WhyNot(content, expected) is { } why)
        {
            _logger.LogWarning("Upload refused: {FileName} declared {Declared}, {Why}",
                SafeName(file.FileName), file.ContentType, why);
            throw new InvalidOperationException($"'{SafeName(file.FileName)}' can't be attached — {why}.");
        }

        var name = NameWithEnding(file.FileName, expected);
        var (fullPath, fileUrl) = BuildPath(tenantId, entityType, name);

        await File.WriteAllBytesAsync(fullPath, content, ct);

        _logger.LogInformation("File saved: {FileName} → {FileUrl}", name, fileUrl);

        return new FileUploadResult
        {
            FileUrl = fileUrl,
            FileSize = content.LongLength,
            MimeType = expected.MimeType,
            FileName = name,
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

        // 105 — the contents must be what the name says.
        var expected = FileContentCheck.Expected(fileName, contentType)!;
        if (FileContentCheck.WhyNot(content, expected) is { } why)
            throw new InvalidOperationException($"'{SafeName(fileName)}' was not saved: {why}.");

        var name = NameWithEnding(fileName, expected);
        var (fullPath, fileUrl) = BuildPath(tenantId, entityType, name);

        await File.WriteAllBytesAsync(fullPath, content, ct);

        _logger.LogInformation("File saved: {FileName} ({Bytes} bytes) → {FileUrl}",
            name, content.LongLength, fileUrl);

        return new FileUploadResult
        {
            FileUrl = fileUrl,
            FileSize = content.LongLength,
            MimeType = expected.MimeType,
            FileName = name,
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
        // 105: by its name (or, with no ending, its declared type). The
        // contents are checked when the file is saved.
        if (FileContentCheck.Expected(fileName, contentType) is null) return "that type of file is not allowed";
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

    /// <summary>105. "'page.html' can't be attached — that type of file isn't allowed. …"</summary>
    private static string NotAllowedMessage(string? fileName)
        => $"'{SafeName(fileName)}' can't be attached — that type of file isn't allowed. {FileContentCheck.AllowedList}";

    /// <summary>
    /// 105. The name shown and downloaded: cleaned, and given its type's
    /// ending when it had none ("invoice" sent as a PDF → "invoice.pdf").
    /// </summary>
    private static string NameWithEnding(string? fileName, AllowedFileType type)
    {
        var name = SafeName(fileName);
        if (!string.IsNullOrEmpty(Path.GetExtension(name))) return name;
        var room = 255 - type.Extension.Length;
        return (name.Length <= room ? name : name[..room]) + type.Extension;
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
