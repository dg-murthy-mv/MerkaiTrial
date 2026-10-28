// =====================================================================
// FILE: MerkaiTrial.Application/Commands/Attachments/AttachmentDownloadHandler.cs
//
// NEW FILE (090b). Reading an attachment back, for the person who is
// allowed to see it.
//
// WHY THIS EXISTS
//   Every attachment link pointed at its STORAGE path —
//   /uploads/<workspace>/deal/<year>/<file>.pdf — on the Admin.Web site.
//   The file is written under the WebApi's folder, and Admin.Web has no
//   such folder, so every link answered 404: nobody could open any deal,
//   lead or quote attachment. Serving that folder as plain static files
//   would have "fixed" it the wrong way — anyone holding a link (a
//   forwarded email, browser history) could then download a customer's
//   contract without signing in.
//
//   So a link is now /Attachments/Download/{id} (AttachmentLinks), and
//   that page asks this handler, through GET api/attachments/{id}/download,
//   on behalf of the signed-in person. The handler answers only when:
//     • the attachment belongs to THIS workspace and is not deleted;
//     • the record it hangs off is visible to this person — record
//       visibility (Own / Team / All) for leads and deals; a quote through
//       its deal; an invoice within the workspace.
//   Anything else is "not found" — the same answer as a file that does
//   not exist, so nothing can be learned by guessing ids.
//
//   The controller additionally checks the module's read permission
//   (Leads.Read, Deals.Read, …) for the record type.
//
// Registered automatically: the Scrutor scan picks up every ICommandHandler.
//
// 105 — VUL-14: THE FILE MUST BE IN THIS WORKSPACE'S OWN FOLDER.
//   Storage already refuses any path outside the uploads folder. Inside it,
//   every workspace has its own folder (/uploads/<workspace id>/…), and a
//   row of workspace A's could still have named a file in workspace B's
//   folder if it was ever written wrongly (a bad import, a hand fix, a bug
//   in some future handler). Now the stored path must start with THIS
//   workspace's folder and contain no "..", or the answer is "not found" —
//   the same as any other refusal — and a warning is logged.
// =====================================================================

using MerkaiTrial.Application.Security;
using MerkaiTrial.Application.Services.Storage;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Commands.Attachments
{
    /// <summary>
    /// 090b. Where an attachment is opened from: an Admin.Web page that checks
    /// the person, never the storage path. Used for AttachmentDto.FileUrl by
    /// the lead, deal and quote attachment handlers.
    /// </summary>
    public static class AttachmentLinks
    {
        public static string Download(Guid attachmentId) => $"/Attachments/Download/{attachmentId}";
    }

    /// <summary>An opened attachment. The caller disposes Content (FileStreamResult does).</summary>
    public sealed record AttachmentFile(Stream Content, string FileName, string ContentType, string EntityType);

    public class GetAttachmentDownloadHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly IFileStorageService _storage;
        private readonly IRecordScopeService _scope;
        private readonly ILogger<GetAttachmentDownloadHandler> _logger;

        public GetAttachmentDownloadHandler(
            FlowDbContext db,
            IFileStorageService storage,
            IRecordScopeService scope,
            ILogger<GetAttachmentDownloadHandler> logger)
        {
            _db      = db;
            _storage = storage;
            _scope   = scope;
            _logger  = logger;
        }

        /// <summary>
        /// The file, opened — or null when it is not this person's to see,
        /// does not exist, was deleted, or is missing from storage.
        /// </summary>
        public async Task<AttachmentFile?> Handle(Guid tenantId, Guid attachmentId, CancellationToken ct = default)
        {
            var a = await _db.Attachments.AsNoTracking()
                .Where(x => x.Id == attachmentId && x.TenantId == tenantId && !x.IsDeleted)
                .Select(x => new { x.EntityType, x.EntityId, x.FileName, x.FileUrl, x.MimeType })
                .FirstOrDefaultAsync(ct);

            if (a is null) return null;

            if (!await CanSeeOwnerAsync(tenantId, a.EntityType, a.EntityId, ct))
                return null;

            // 105 — only from this workspace's own folder.
            if (!IsInWorkspaceFolder(a.FileUrl, tenantId))
            {
                _logger.LogWarning(
                    "Attachment {AttachmentId} points outside its workspace's folder — refused", attachmentId);
                return null;
            }

            var stream = await _storage.OpenReadAsync(a.FileUrl, ct);
            if (stream is null)
            {
                // The row exists but the file does not — worth knowing about,
                // and never worth showing the person a storage path for.
                _logger.LogWarning("Attachment {AttachmentId} has no file in storage", attachmentId);
                return null;
            }

            return new AttachmentFile(
                stream,
                string.IsNullOrWhiteSpace(a.FileName) ? "attachment" : a.FileName,
                string.IsNullOrWhiteSpace(a.MimeType) ? "application/octet-stream" : a.MimeType,
                a.EntityType);
        }

        /// <summary>
        /// 105. "/uploads/{workspace id}/…" with no "..", no "\" and no
        /// drive or host — the only shape the storage service ever writes.
        /// </summary>
        public static bool IsInWorkspaceFolder(string? fileUrl, Guid tenantId)
        {
            if (string.IsNullOrWhiteSpace(fileUrl)) return false;

            var prefix = $"/uploads/{tenantId:N}/";
            if (!fileUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

            var rest = fileUrl[prefix.Length..];
            if (rest.Length == 0 || rest.Contains('\\') || rest.Contains(':') || rest.Contains('%')) return false;

            return rest.Split('/').All(part => part.Length > 0 && part != "." && part != "..");
        }

        private async Task<bool> CanSeeOwnerAsync(Guid tenantId, string entityType, Guid entityId, CancellationToken ct)
        {
            switch (entityType)
            {
                case AttachmentEntityType.Lead:
                    return await _scope.CanSeeLeadAsync(_db, tenantId, entityId, ct);

                case AttachmentEntityType.Deal:
                    return await _scope.CanSeeDealAsync(_db, tenantId, entityId, ct);

                case AttachmentEntityType.Quote:
                {
                    // A quote is seen through its deal, the way the quote
                    // pages are reached. A quote with no deal is the
                    // workspace's.
                    var quote = await _db.Quotes.AsNoTracking()
                        .Where(q => q.Id == entityId && q.TenantId == tenantId && !q.IsDeleted)
                        .Select(q => new { DealId = (Guid?)q.DealId })
                        .FirstOrDefaultAsync(ct);

                    if (quote is null) return false;
                    return quote.DealId is not { } dealId || dealId == Guid.Empty
                        || await _scope.CanSeeDealAsync(_db, tenantId, dealId, ct);
                }

                case AttachmentEntityType.Invoice:
                    return await _db.Invoices.AsNoTracking()
                        .AnyAsync(i => i.Id == entityId && i.TenantId == tenantId && !i.IsDeleted, ct);

                default:
                    return false;
            }
        }
    }
}
