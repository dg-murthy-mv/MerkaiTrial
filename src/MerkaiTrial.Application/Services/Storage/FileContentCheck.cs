// =====================================================================
// FileContentCheck.cs
// Location: MerkaiTrial.Application/Services/Storage/FileContentCheck.cs
//
// NEW FILE (105 — VUL-13). What a file REALLY is, from its bytes.
//
// Until 105 an upload was judged by the type the browser said it was
// (Content-Type), which the person sending it chooses. A web page sent as
// "image/png", or a program sent as "application/pdf", was stored and
// served back under that label. Now:
//
//   1. The file's name must end in one of the allowed types (.pdf, .docx,
//      .png, …). A name with no ending takes it from the declared type,
//      and the ending is added to the name.
//   2. The first bytes must be that type's own signature — %PDF- for a
//      PDF, the PNG header for a PNG, a zip holding word/document.xml for
//      a .docx, and so on. Text and CSV must be text (no binary bytes).
//   3. Word and Excel files with macros (vbaProject.bin) are refused.
//
// The declared Content-Type is not believed at all: the stored type is
// the one that goes with the name, now proved by the contents.
// =====================================================================

using System.IO.Compression;
using System.Text;

namespace MerkaiTrial.Application.Services.Storage;

public enum FileKind { Unknown, Pdf, Png, Jpeg, Gif, Webp, OfficeLegacy, Docx, Xlsx, Text }

public sealed record AllowedFileType(string Extension, string MimeType, FileKind Kind, string Label);

public static class FileContentCheck
{
    /// <summary>Every type that may be attached, by the ending of its name.</summary>
    public static readonly IReadOnlyDictionary<string, AllowedFileType> ByExtension =
        new[]
        {
            new AllowedFileType(".pdf",  "application/pdf", FileKind.Pdf, "PDF"),
            new AllowedFileType(".doc",  "application/msword", FileKind.OfficeLegacy, "Word document"),
            new AllowedFileType(".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", FileKind.Docx, "Word document"),
            new AllowedFileType(".xls",  "application/vnd.ms-excel", FileKind.OfficeLegacy, "Excel workbook"),
            new AllowedFileType(".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", FileKind.Xlsx, "Excel workbook"),
            new AllowedFileType(".jpg",  "image/jpeg", FileKind.Jpeg, "JPEG image"),
            new AllowedFileType(".jpeg", "image/jpeg", FileKind.Jpeg, "JPEG image"),
            new AllowedFileType(".png",  "image/png",  FileKind.Png,  "PNG image"),
            new AllowedFileType(".gif",  "image/gif",  FileKind.Gif,  "GIF image"),
            new AllowedFileType(".webp", "image/webp", FileKind.Webp, "WebP image"),
            new AllowedFileType(".txt",  "text/plain", FileKind.Text, "text file"),
            new AllowedFileType(".csv",  "text/csv",   FileKind.Text, "CSV file"),
        }.ToDictionary(t => t.Extension, StringComparer.OrdinalIgnoreCase);

    public const string AllowedList =
        "Use a PDF, Word or Excel file, an image (JPG, PNG, GIF, WebP), or a text or CSV file.";

    /// <summary>
    /// The type a file is meant to be: from its name's ending, or — when the
    /// name has no ending at all — from the declared type. Null when it is
    /// not an allowed type either way.
    /// </summary>
    public static AllowedFileType? Expected(string? fileName, string? declaredType)
    {
        var ext = Path.GetExtension(fileName ?? string.Empty);
        if (!string.IsNullOrEmpty(ext))
            return ByExtension.TryGetValue(ext, out var byName) ? byName : null;

        var declared = (declaredType ?? string.Empty).Split(';')[0].Trim();
        return ByExtension.Values.FirstOrDefault(t => t.MimeType.Equals(declared, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>What the bytes are.</summary>
    public static FileKind Detect(byte[] content)
    {
        if (content.Length == 0) return FileKind.Unknown;

        if (StartsWith(content, "%PDF-"u8)) return FileKind.Pdf;
        if (StartsWith(content, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return FileKind.Png;
        if (StartsWith(content, new byte[] { 0xFF, 0xD8, 0xFF })) return FileKind.Jpeg;
        if (StartsWith(content, "GIF87a"u8) || StartsWith(content, "GIF89a"u8)) return FileKind.Gif;
        if (content.Length >= 12 && StartsWith(content, "RIFF"u8)
            && content.AsSpan(8, 4).SequenceEqual("WEBP"u8)) return FileKind.Webp;
        if (StartsWith(content, new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 })) return FileKind.OfficeLegacy;
        if (StartsWith(content, new byte[] { 0x50, 0x4B, 0x03, 0x04 })) return OfficeZip(content);

        return LooksLikeText(content) ? FileKind.Text : FileKind.Unknown;
    }

    /// <summary>True when a Word or Excel zip carries a macro project.</summary>
    public static bool HasMacros(byte[] content)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
            return zip.Entries.Any(e => e.FullName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase));
        }
        catch (InvalidDataException) { return false; }
    }

    /// <summary>
    /// Null when the file may be kept as <paramref name="expected"/>;
    /// otherwise the reason, in words for the person who sent it.
    /// </summary>
    public static string? WhyNot(byte[] content, AllowedFileType expected)
    {
        var actual = Detect(content);
        if (actual != expected.Kind)
            return $"it isn't really a {expected.Label} — what's inside doesn't match its name, so it may be a different kind of file renamed";

        if (actual is FileKind.Docx or FileKind.Xlsx && HasMacros(content))
            return $"it contains macros. Save it as a {expected.Label} without macros and attach that";

        return null;
    }

    // ── helpers ───────────────────────────────────────────────────────

    private static bool StartsWith(byte[] content, ReadOnlySpan<byte> signature)
        => content.Length >= signature.Length && content.AsSpan(0, signature.Length).SequenceEqual(signature);

    private static FileKind OfficeZip(byte[] content)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
            var names = new HashSet<string>(zip.Entries.Select(e => e.FullName), StringComparer.OrdinalIgnoreCase);
            if (!names.Contains("[Content_Types].xml")) return FileKind.Unknown;
            if (names.Contains("word/document.xml")) return FileKind.Docx;
            if (names.Contains("xl/workbook.xml")) return FileKind.Xlsx;
            return FileKind.Unknown;
        }
        catch (InvalidDataException)
        {
            return FileKind.Unknown;
        }
    }

    /// <summary>
    /// Text: no NUL and no control characters other than tab, line feed,
    /// form feed, carriage return and escape. Any byte from 0x80 up is
    /// allowed, so UTF-8 and the Windows code pages a spreadsheet saves
    /// CSV in are both text.
    /// </summary>
    private static bool LooksLikeText(byte[] content)
    {
        foreach (var b in content)
        {
            if (b >= 0x20 && b != 0x7F) continue;
            if (b is 0x09 or 0x0A or 0x0C or 0x0D or 0x1B) continue;
            return false;
        }
        return true;
    }
}
