// =====================================================================
// DealAttachmentHandlers.cs
// Location: MerkaiTrial.Application/Commands/Deals/DealAttachmentHandlers.cs
//
// Uses polymorphic Attachments table (EntityType="Deal", EntityId=dealId).
// Storage is abstracted behind IFileStorageService — swap Local→Azure
// in Program.cs only.
// =====================================================================

using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Storage;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Commands.Deals
{
    // ── GET ATTACHMENTS ───────────────────────────────────────────────

    public class GetDealAttachmentsHandler : ICommandHandler
    {
        private readonly FlowDbContext          _db;
        private readonly IFileStorageService    _storage;

        public GetDealAttachmentsHandler(FlowDbContext db, IFileStorageService storage)
        {
            _db      = db;
            _storage = storage;
        }

        public async Task<List<AttachmentDto>> HandleAsync(string tenantId, Guid dealId)
        {
            var rows = await _db.Attachments
                .Where(a =>
                    a.TenantId.ToString() == tenantId &&
                    a.EntityType == AttachmentEntityType.Deal &&
                    a.EntityId   == dealId &&
                    !a.IsDeleted)
                .OrderByDescending(a => a.CreatedAtUtc)
                .ToListAsync();

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
    }

    // ── UPLOAD ATTACHMENT ─────────────────────────────────────────────

    public class UploadDealAttachmentHandler : ICommandHandler
    {
        private readonly FlowDbContext          _db;
        private readonly IFileStorageService    _storage;
        private readonly ICurrentUserService    _currentUserService;
        private readonly ILogger<UploadDealAttachmentHandler> _logger;

        public UploadDealAttachmentHandler(
            FlowDbContext       db,
            IFileStorageService storage,
            ICurrentUserService currentUserService,
            ILogger<UploadDealAttachmentHandler> logger)
        {
            _db                 = db;
            _storage            = storage;
            _currentUserService = currentUserService;
            _logger             = logger;
        }

        /// <param name="tenantId">String tenant ID e.g. "BB100001"</param>
        /// <param name="dealId">Deal the attachment belongs to</param>
        /// <param name="file">The uploaded file from the multipart form</param>
        public async Task<AttachmentDto> HandleAsync(
            string    tenantId,
            Guid      dealId,
            IFormFile file,
            CancellationToken ct = default)
        {
            // Verify deal exists and belongs to tenant
            var deal = await _db.Deals
                .FirstOrDefaultAsync(d =>
                    d.Id == dealId &&
                    d.TenantId.ToString() == tenantId &&
                    !d.IsDeleted, ct);

            if (deal is null)
                throw new KeyNotFoundException($"Deal {dealId} not found");

            var currentUser = await _currentUserService.GetCurrentUserAsync();
            var tenantGuid  = Guid.Parse(tenantId);

            // Save file via storage service (validates size + MIME)
            var result = await _storage.SaveAsync(
                tenantGuid, AttachmentEntityType.Deal, file, ct);

            var attachment = new Attachment
            {
                Id           = Guid.NewGuid(),
                TenantId     = tenantGuid,
                EntityType   = AttachmentEntityType.Deal,
                EntityId     = dealId,
                FileName     = result.FileName,
                FileUrl      = result.FileUrl,
                FileSize     = result.FileSize,
                MimeType     = result.MimeType,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy    = currentUser.FullName,
                IsDeleted    = false
            };

            _db.Attachments.Add(attachment);
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Attachment uploaded: {FileName} ({Size} bytes) for Deal {DealId}",
                result.FileName, result.FileSize, dealId);

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

    // ── DELETE ATTACHMENT ─────────────────────────────────────────────

    public class DeleteDealAttachmentHandler : ICommandHandler
    {
        private readonly FlowDbContext          _db;
        private readonly IFileStorageService    _storage;
        private readonly ICurrentUserService    _currentUserService;
        private readonly ILogger<DeleteDealAttachmentHandler> _logger;

        public DeleteDealAttachmentHandler(
            FlowDbContext       db,
            IFileStorageService storage,
            ICurrentUserService currentUserService,
            ILogger<DeleteDealAttachmentHandler> logger)
        {
            _db                 = db;
            _storage            = storage;
            _currentUserService = currentUserService;
            _logger             = logger;
        }

        public async Task HandleAsync(string tenantId, Guid attachmentId, CancellationToken ct = default)
        {
            var attachment = await _db.Attachments
                .FirstOrDefaultAsync(a =>
                    a.Id == attachmentId &&
                    a.TenantId.ToString() == tenantId &&
                    a.EntityType == AttachmentEntityType.Deal &&
                    !a.IsDeleted, ct);

            if (attachment is null)
                throw new KeyNotFoundException($"Attachment {attachmentId} not found");

            var currentUser = await _currentUserService.GetCurrentUserAsync();

            // Soft-delete the DB record first
            attachment.IsDeleted    = true;
            attachment.DeletedAtUtc = DateTime.UtcNow;

            await _db.SaveChangesAsync(ct);

            // Then delete the physical file (non-fatal if file already gone)
            try
            {
                await _storage.DeleteAsync(attachment.FileUrl, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "File deletion failed for attachment {AttachmentId} at {FileUrl} — record already soft-deleted",
                    attachmentId, attachment.FileUrl);
            }

            _logger.LogInformation(
                "Attachment deleted: {FileName} ({AttachmentId}) by {User}",
                attachment.FileName, attachmentId, currentUser.FullName);
        }
    }
}
