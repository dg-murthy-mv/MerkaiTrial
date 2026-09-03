// =====================================================================
// LeadAttachmentHandlers.cs
// Location: MerkaiTrial.Application/Commands/Leads/LeadAttachmentHandlers.cs
// DTOs live in MerkaiTrial.Application.DTOs.AttachmentDtos — not here.
// =====================================================================

using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services.Storage;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Commands.Leads;

// ── UPLOAD ────────────────────────────────────────────────────────────

public class UploadLeadAttachmentHandler : ICommandHandler
{
    private readonly FlowDbContext                       _db;
    private readonly IFileStorageService                 _storage;
    private readonly ILogger<UploadLeadAttachmentHandler> _logger;

    public UploadLeadAttachmentHandler(
        FlowDbContext db,
        IFileStorageService storage,
        ILogger<UploadLeadAttachmentHandler> logger)
    {
        _db      = db;
        _storage = storage;
        _logger  = logger;
    }

    public async Task<AttachmentDto> Handle(
        UploadLeadAttachmentDto dto, CancellationToken ct = default)
    {
        var leadExists = await _db.Leads
            .AnyAsync(l => l.Id == dto.LeadId && l.TenantId == dto.TenantId && !l.IsDeleted, ct);

        if (!leadExists)
            throw new KeyNotFoundException($"Lead {dto.LeadId} not found");

        var result = await _storage.SaveAsync(
            dto.TenantId, AttachmentEntityType.Lead, dto.File, ct);

        var attachment = new Attachment
        {
            Id           = Guid.NewGuid(),
            TenantId     = dto.TenantId,
            EntityType   = AttachmentEntityType.Lead,
            EntityId     = dto.LeadId,
            FileName     = result.FileName,
            FileUrl      = result.FileUrl,
            FileSize     = result.FileSize,
            MimeType     = result.MimeType,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy    = dto.UploadedBy,
            IsDeleted    = false
        };

        _db.Attachments.Add(attachment);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Uploaded {FileName} for lead {LeadId}", result.FileName, dto.LeadId);

        return new AttachmentDto(
             attachment.Id,
             attachment.FileName,
             _storage.GetDownloadUrl(attachment.FileUrl),
             attachment.FileSize,
             attachment.MimeType,
             attachment.CreatedAtUtc,
             attachment.CreatedBy,
             attachment.TenantId,
             attachment.EntityType,
             attachment.EntityId
         );

    }
}

// ── GET LIST ──────────────────────────────────────────────────────────

public class GetLeadAttachmentsHandler : ICommandHandler
{
    private readonly FlowDbContext                      _db;
    private readonly ILogger<GetLeadAttachmentsHandler> _logger;
    private readonly IFileStorageService _storage;
    public GetLeadAttachmentsHandler(
        FlowDbContext db,
         IFileStorageService storage,
        ILogger<GetLeadAttachmentsHandler> logger)
    {
        _db     = db;
        _storage = storage;
        _logger = logger;
    }

    public async Task<List<AttachmentDto>> Handle(
        Guid tenantId, Guid leadId, CancellationToken ct = default)
    {
        try
        {
            var rows = await _db.Attachments
                .AsNoTracking()
                .Where(a =>
                    a.TenantId == tenantId &&
                    a.EntityType == AttachmentEntityType.Lead &&
                    a.EntityId == leadId &&
                    !a.IsDeleted)
                .OrderByDescending(a => a.CreatedAtUtc)
                .ToListAsync(ct);

            // ✅ Object initializer — matches class-based AttachmentDto
            return rows.Select(a => new AttachmentDto(
                a.Id,
                a.FileName,
                _storage.GetDownloadUrl(a.FileUrl),
                a.FileSize,
                a.MimeType,
                a.CreatedAtUtc,
                a.CreatedBy,
                a.TenantId,
                a.EntityType,
                a.EntityId
            )).ToList();

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting attachments for lead {LeadId}", leadId);
            throw;
        }
    }
}

// ── DELETE ────────────────────────────────────────────────────────────

public class DeleteLeadAttachmentHandler : ICommandHandler
{
    private readonly FlowDbContext                        _db;
    private readonly IFileStorageService                  _storage;
    private readonly ILogger<DeleteLeadAttachmentHandler> _logger;

    public DeleteLeadAttachmentHandler(
        FlowDbContext db,
        IFileStorageService storage,
        ILogger<DeleteLeadAttachmentHandler> logger)
    {
        _db      = db;
        _storage = storage;
        _logger  = logger;
    }

    public async Task Handle(Guid tenantId, Guid attachmentId, CancellationToken ct = default)
    {
        var attachment = await _db.Attachments
            .FirstOrDefaultAsync(a =>
                a.Id       == attachmentId &&
                a.TenantId == tenantId     &&
                !a.IsDeleted, ct);

        if (attachment == null)
            throw new KeyNotFoundException($"Attachment {attachmentId} not found");

        var leadId = attachment.EntityId;

        // Soft-delete first — safe even if physical file delete fails
        attachment.IsDeleted    = true;
        attachment.DeletedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        try
        {
            await _storage.DeleteAsync(attachment.FileUrl, ct);
        }
        catch (Exception ex)
        {
            // Non-fatal — record already soft-deleted
            _logger.LogWarning(ex,
                "Physical file delete failed for {FileUrl}", attachment.FileUrl);
        }

        _logger.LogInformation("Deleted attachment {AttachmentId} from lead {LeadId}",
            attachmentId, leadId);
    }
}
