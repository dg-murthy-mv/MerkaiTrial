// =====================================================================
// LeadAttachmentHandlers.cs
// Location: MerkaiTrial.Application/Commands/Leads/LeadAttachmentHandlers.cs
// DTOs live in MerkaiTrial.Application.DTOs.AttachmentDtos — not here.
//
// COMPLETE FILE — replaces the existing one.
//
// RECORD VISIBILITY (015): every handler here checks the current user can
// see the lead first (RecordScopeGuards). Writes on a lead outside scope
// → KeyNotFound → 404. Lists for a lead outside scope → empty, the same
// answer as for a lead that doesn't exist.
// =====================================================================

using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Security;
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
    private readonly IRecordScopeService                 _scope;

    public UploadLeadAttachmentHandler(
        FlowDbContext db,
        IFileStorageService storage,
        IRecordScopeService scope,
        ILogger<UploadLeadAttachmentHandler> logger)
    {
        _db      = db;
        _scope   = scope;
        _storage = storage;
        _logger  = logger;
    }

    public async Task<AttachmentDto> Handle(
        UploadLeadAttachmentDto dto, CancellationToken ct = default)
    {
        // Checked BEFORE the file is written to storage — no orphan files
        // from a refused upload.
        await _scope.EnsureLeadVisibleAsync(_db, dto.TenantId, dto.LeadId, ct);

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
    private readonly IRecordScopeService _scope;
    public GetLeadAttachmentsHandler(
        FlowDbContext db,
        IFileStorageService storage,
        IRecordScopeService scope,
        ILogger<GetLeadAttachmentsHandler> logger)
    {
        _db     = db;
        _scope  = scope;
        _storage = storage;
        _logger = logger;
    }

    public async Task<List<AttachmentDto>> Handle(
        Guid tenantId, Guid leadId, CancellationToken ct = default)
    {
        try
        {
            if (!await _scope.CanSeeLeadAsync(_db, tenantId, leadId, ct))
                return new List<AttachmentDto>();

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
    private readonly IRecordScopeService                  _scope;

    public DeleteLeadAttachmentHandler(
        FlowDbContext db,
        IFileStorageService storage,
        IRecordScopeService scope,
        ILogger<DeleteLeadAttachmentHandler> logger)
    {
        _db      = db;
        _scope   = scope;
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

        // This handler serves the Lead page. An attachment on a lead the
        // user can't see — or on something that isn't a lead — isn't theirs
        // to delete from here.
        if (attachment.EntityType != AttachmentEntityType.Lead ||
            !await _scope.CanSeeLeadAsync(_db, tenantId, attachment.EntityId, ct))
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
