// =====================================================================
// QuoteAttachmentHandlers.cs
// Location: MerkaiTrial.Application/Commands/Quotes/QuoteAttachmentHandlers.cs
// Uses polymorphic Attachments table (EntityType="Quote", EntityId=quoteId)
// =====================================================================

using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Storage;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Commands.Quotes
{
    // ── GET ATTACHMENTS ───────────────────────────────────────────────

    public class GetQuoteAttachmentsHandler : ICommandHandler
    {
        private readonly FlowDbContext       _db;
        private readonly IFileStorageService _storage;

        public GetQuoteAttachmentsHandler(FlowDbContext db, IFileStorageService storage)
        {
            _db      = db;
            _storage = storage;
        }

        public async Task<List<AttachmentDto>> HandleAsync(Guid tenantId, Guid quoteId)
        {
            var rows = await _db.Attachments
                .Where(a =>
                    a.TenantId   == tenantId &&
                    a.EntityType == AttachmentEntityType.Quote &&
                    a.EntityId   == quoteId &&
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

    public class UploadQuoteAttachmentHandler : ICommandHandler
    {
        private readonly FlowDbContext       _db;
        private readonly IFileStorageService _storage;
        private readonly ICurrentUserService _currentUserService;
        private readonly ILogger<UploadQuoteAttachmentHandler> _logger;

        public UploadQuoteAttachmentHandler(
            FlowDbContext       db,
            IFileStorageService storage,
            ICurrentUserService currentUserService,
            ILogger<UploadQuoteAttachmentHandler> logger)
        {
            _db                 = db;
            _storage            = storage;
            _currentUserService = currentUserService;
            _logger             = logger;
        }

        public async Task<AttachmentDto> HandleAsync(
            Guid      tenantId,
            Guid      quoteId,
            IFormFile file,
            CancellationToken ct = default)
        {
            // Verify quote exists and belongs to tenant
            var quoteExists = await _db.Quotes
                .AnyAsync(q => q.Id == quoteId && q.TenantId == tenantId && !q.IsDeleted, ct);

            if (!quoteExists)
                throw new KeyNotFoundException($"Quote {quoteId} not found");

            var currentUser = await _currentUserService.GetCurrentUserAsync();

            // Save file via storage service (validates size + MIME)
            var result = await _storage.SaveAsync(tenantId, AttachmentEntityType.Quote, file, ct);

            var attachment = new Attachment
            {
                Id           = Guid.NewGuid(),
                TenantId     = tenantId,
                EntityType   = AttachmentEntityType.Quote,
                EntityId     = quoteId,
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
                "Attachment uploaded: {FileName} ({Size} bytes) for Quote {QuoteId}",
                result.FileName, result.FileSize, quoteId);

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

    public class DeleteQuoteAttachmentHandler : ICommandHandler
    {
        private readonly FlowDbContext       _db;
        private readonly IFileStorageService _storage;
        private readonly ILogger<DeleteQuoteAttachmentHandler> _logger;

        public DeleteQuoteAttachmentHandler(
            FlowDbContext       db,
            IFileStorageService storage,
            ILogger<DeleteQuoteAttachmentHandler> logger)
        {
            _db      = db;
            _storage = storage;
            _logger  = logger;
        }

        public async Task HandleAsync(Guid tenantId, Guid attachmentId, CancellationToken ct = default)
        {
            var attachment = await _db.Attachments
                .FirstOrDefaultAsync(a =>
                    a.Id         == attachmentId &&
                    a.TenantId   == tenantId &&
                    a.EntityType == AttachmentEntityType.Quote &&
                    !a.IsDeleted, ct);

            if (attachment is null)
                throw new KeyNotFoundException($"Attachment {attachmentId} not found");

            // Soft-delete DB record first — safe even if file delete fails
            attachment.IsDeleted    = true;
            attachment.DeletedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            try
            {
                await _storage.DeleteAsync(attachment.FileUrl, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Physical file delete failed for {FileUrl} — record already soft-deleted",
                    attachment.FileUrl);
            }

            _logger.LogInformation(
                "Quote attachment deleted: {FileName} ({AttachmentId})",
                attachment.FileName, attachmentId);
        }
    }
}
