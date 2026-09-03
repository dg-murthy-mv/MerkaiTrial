// =====================================================================
// AttachmentDtos.cs
// Location: MerkaiTrial.Application/DTOs/AttachmentDtos.cs
// =====================================================================

using Microsoft.AspNetCore.Http;

namespace MerkaiTrial.Application.DTOs;

// ── Read DTO — returned to Web layer ─────────────────────────────────

public record AttachmentDto(
    Guid     Id,
    string   FileName,
    string   FileUrl,
    long     FileSize,
    string?  MimeType,
    DateTime CreatedAtUtc,
    string?  CreatedBy,
    Guid     TenantId,
    string   EntityType,
    Guid     EntityId

)
{
    /// <summary>Human-readable size e.g. "1.2 MB"</summary>
    public string FileSizeDisplay => FileSize switch
    {
        < 1024            => $"{FileSize} B",
        < 1024 * 1024     => $"{FileSize / 1024.0:F1} KB",
        _                 => $"{FileSize / (1024.0 * 1024):F1} MB"
    };

    /// <summary>Bootstrap icon class based on MIME type</summary>
    public string FileIcon => MimeType switch
    {
        "application/pdf"                                                                  => "bi-file-earmark-pdf text-danger",
        "application/msword"
            or "application/vnd.openxmlformats-officedocument.wordprocessingml.document"  => "bi-file-earmark-word text-primary",
        "application/vnd.ms-excel"
            or "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"        => "bi-file-earmark-excel text-success",
        string m when m.StartsWith("image/")                                              => "bi-file-earmark-image text-info",
        "text/plain" or "text/csv"                                                        => "bi-file-earmark-text text-secondary",
        _                                                                                  => "bi-file-earmark text-muted"
    };
}

// ── Write DTOs — passed from Web layer into handlers ─────────────────

public record UploadLeadAttachmentDto(
    Guid      TenantId,
    Guid      LeadId,
    IFormFile File,
    string?   UploadedBy
);
