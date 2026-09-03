// =====================================================================
// Attachment.cs
// Location: MerkaiTrial.Domain/Entities/Attachment.cs
// EntityType + EntityId pattern — one table covers Lead, Deal,
// Quote, Invoice without FK explosion. Swap storage provider
// by replacing IFileStorageService implementation only.
// =====================================================================

namespace MerkaiTrial.Domain.Entities;

public class Attachment
{
    public Guid   Id         { get; set; } = Guid.NewGuid();
    public Guid   TenantId   { get; set; }

    // ── Entity reference (polymorphic) ────────────────────────────────
    /// <summary>Lead | Deal | Quote | Invoice</summary>
    public string EntityType { get; set; } = string.Empty;
    public Guid   EntityId   { get; set; }

    // ── File metadata ─────────────────────────────────────────────────
    /// <summary>Original filename shown to user e.g. Contract_2026.pdf</summary>
    public string  FileName     { get; set; } = string.Empty;

    /// <summary>
    /// Stored path or URL. Local: /uploads/{tenantId}/{year}/{guid}.pdf
    /// Azure Blob: https://blob.core.windows.net/...
    /// Abstracted behind IFileStorageService — never build URLs manually.
    /// </summary>
    public string  FileUrl      { get; set; } = string.Empty;

    /// <summary>File size in bytes</summary>
    public long    FileSize     { get; set; }

    /// <summary>MIME type e.g. application/pdf, image/png</summary>
    public string? MimeType     { get; set; }

    // ── Audit ─────────────────────────────────────────────────────────
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string?  CreatedBy    { get; set; }
    public bool     IsDeleted    { get; set; } = false;
    public DateTime? DeletedAtUtc { get; set; }
}

// ── Allowed entity types (use as constants, not enum — easier to extend)
public static class AttachmentEntityType
{
    public const string Lead    = "Lead";
    public const string Deal    = "Deal";
    public const string Quote   = "Quote";
    public const string Invoice = "Invoice";
}
