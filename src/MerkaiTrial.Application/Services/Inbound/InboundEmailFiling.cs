// =====================================================================
// InboundEmailFiling.cs
// Location: MerkaiTrial.Application/Services/Inbound/InboundEmailFiling.cs
//
// NEW FILE (083). Putting an email ON a record — shared by the two ways
// that happens, so they cannot disagree:
//
//   • automatically, by InboundEmailWorker (a reply token, a thread, or
//     the customer's address)
//   • by hand, from the Inbox or the email page ("Link to…")
//
// WHAT "ON A RECORD" MEANS
//   An email carries up to four links: LeadId, DealId, ContactId,
//   CompanyId. Filing it on one record fills in the others that follow
//   from it, because that is where people will look:
//
//     a quote   → its deal → that deal's contact and company
//     a deal    → its contact and company
//     a lead    → the lead, plus the contact / deal it was converted into
//     a contact → the contact and its company
//
//   Every page shows emails by those links: the deal page shows the
//   deal's emails AND its contact's, so a customer who writes in fresh
//   (no token) still lands on the deal they are in.
//
// ATTACHMENTS ON THE RECORD
//   A lead and a deal each have an Attachments tab. An email's files get
//   a COPY there — a separate stored file, not a second pointer to the
//   same one — so a rep deleting "quote-signed.pdf" from the deal's tab
//   cannot break the email, and deleting the email cannot empty the tab.
//   Contacts and companies have no Attachments tab; their emails keep
//   their files on the email page.
//
// TENANT: every query carries an explicit TenantId. Callers pass a
// FlowDbContext whose filter is either the signed-in user's workspace
// (pages) or the email's workspace (the worker, through its own
// tenant-scoped context) — the explicit predicate agrees with both.
// =====================================================================

using MerkaiTrial.Application.Services.Storage;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Services.Inbound;

/// <summary>The kinds of record an email can be filed on.</summary>
public static class InboundLinkTypes
{
    public const string Lead    = "Lead";
    public const string Deal    = "Deal";
    public const string Contact = "Contact";
    public const string Quote   = "Quote";

    /// <summary>085. Token-only, like Quote: a reply to an invoice email.</summary>
    public const string Invoice = "Invoice";

    /// <summary>Accepted from a person (the Link form). Quote and Invoice are token-only.</summary>
    public static readonly IReadOnlySet<string> Linkable =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Lead, Deal, Contact };

    public static string? Normalise(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "lead"    => Lead,
        "deal"    => Deal,
        "contact" => Contact,
        "quote"   => Quote,
        "invoice" => Invoice,
        _         => null
    };
}

public static class InboundEmailFiling
{
    /// <summary>
    /// Fills the email's links from one record, replacing whatever was
    /// there. False when the record does not exist in this workspace (or
    /// is deleted) — the email is left untouched.
    /// </summary>
    public static async Task<bool> ApplyTargetAsync(
        FlowDbContext db, InboundEmail email, string entityType, Guid entityId, CancellationToken ct)
    {
        var tenantId = email.TenantId;

        switch (InboundLinkTypes.Normalise(entityType))
        {
            case InboundLinkTypes.Quote:
            {
                var dealId = await db.Quotes.AsNoTracking()
                    .Where(q => q.Id == entityId && q.TenantId == tenantId && !q.IsDeleted)
                    .Select(q => (Guid?)q.DealId)
                    .FirstOrDefaultAsync(ct);

                if (dealId is null) return false;
                if (!await ApplyTargetAsync(db, email, InboundLinkTypes.Deal, dealId.Value, ct)) return false;

                email.QuoteId = entityId;
                return true;
            }

            case InboundLinkTypes.Invoice:
            {
                // 085. A reply to an invoice email is filed on the invoice's
                // deal — directly (manual invoice) or through its quote.
                // There is no InvoiceId on the email: the deal's Emails tab
                // is where the conversation lives, as for quotes.
                var target = await db.Invoices.AsNoTracking()
                    .Where(i => i.Id == entityId && i.TenantId == tenantId && !i.IsDeleted)
                    .Select(i => new
                    {
                        i.DealId,
                        i.QuoteId,
                        QuoteDealId = i.Quote != null ? (Guid?)i.Quote.DealId : null
                    })
                    .FirstOrDefaultAsync(ct);

                if (target is null) return false;

                var dealId = target.DealId ?? target.QuoteDealId;
                if (dealId is null) return false;

                if (!await ApplyTargetAsync(db, email, InboundLinkTypes.Deal, dealId.Value, ct)) return false;
                if (target.QuoteId.HasValue) email.QuoteId = target.QuoteId;
                return true;
            }

            case InboundLinkTypes.Deal:
            {
                var deal = await db.Deals.AsNoTracking()
                    .Where(d => d.Id == entityId && d.TenantId == tenantId && !d.IsDeleted)
                    .Select(d => new { d.Id, d.ContactId, d.CompanyId })
                    .FirstOrDefaultAsync(ct);

                if (deal is null) return false;

                Set(email, leadId: null, dealId: deal.Id,
                    contactId: deal.ContactId == Guid.Empty ? null : deal.ContactId,
                    companyId: deal.CompanyId);
                return true;
            }

            case InboundLinkTypes.Lead:
            {
                var lead = await db.Leads.AsNoTracking()
                    .Where(l => l.Id == entityId && l.TenantId == tenantId && !l.IsDeleted)
                    .Select(l => new
                    {
                        l.Id, l.ContactId, l.ConvertedToContactId, l.DealId, l.CompanyId, l.ConvertedToCompanyId
                    })
                    .FirstOrDefaultAsync(ct);

                if (lead is null) return false;

                Set(email, leadId: lead.Id, dealId: lead.DealId,
                    contactId: lead.ContactId ?? lead.ConvertedToContactId,
                    companyId: lead.CompanyId ?? lead.ConvertedToCompanyId);
                return true;
            }

            case InboundLinkTypes.Contact:
            {
                var contact = await db.Contacts.AsNoTracking()
                    .Where(c => c.Id == entityId && c.TenantId == tenantId && !c.IsDeleted)
                    .Select(c => new { c.Id, c.CompanyId })
                    .FirstOrDefaultAsync(ct);

                if (contact is null) return false;

                Set(email, leadId: null, dealId: null, contactId: contact.Id, companyId: contact.CompanyId);
                return true;
            }

            default:
                return false;
        }
    }

    /// <summary>Clears every link. The email goes back to the Inbox.</summary>
    public static void ClearLinks(InboundEmail email)
    {
        Set(email, null, null, null, null);
        email.QuoteId = null;
        email.MatchMethod = InboundMatchMethod.None;
        email.MatchNote = null;
        if (email.Status != InboundEmailStatus.Dismissed) email.Status = InboundEmailStatus.Unmatched;
    }

    private static void Set(InboundEmail email, Guid? leadId, Guid? dealId, Guid? contactId, Guid? companyId)
    {
        email.LeadId    = leadId;
        email.DealId    = dealId;
        email.ContactId = contactId;
        email.CompanyId = companyId;
        email.QuoteId   = null;

        if (email.Status != InboundEmailStatus.Dismissed)
            email.Status = email.HasLinks ? InboundEmailStatus.Matched : InboundEmailStatus.Unmatched;
    }

    /// <summary>
    /// Who should hear about this email: the deal's owner, else the
    /// lead's, else the owners of the contact's open-or-recent deals (at
    /// most five). The person who forwarded it is removed by the
    /// dispatcher (ActorUserId), never told about their own action.
    /// </summary>
    public static async Task<List<Guid>> OwnersAsync(FlowDbContext db, InboundEmail email, CancellationToken ct)
    {
        var tenantId = email.TenantId;
        var raw = new List<string?>();

        if (email.DealId.HasValue)
        {
            raw.Add(await db.Deals.AsNoTracking()
                .Where(d => d.Id == email.DealId && d.TenantId == tenantId && !d.IsDeleted)
                .Select(d => d.OwnerUserId).FirstOrDefaultAsync(ct));
        }
        else if (email.LeadId.HasValue)
        {
            raw.Add(await db.Leads.AsNoTracking()
                .Where(l => l.Id == email.LeadId && l.TenantId == tenantId && !l.IsDeleted)
                .Select(l => l.OwnerUserId).FirstOrDefaultAsync(ct));
        }
        else if (email.ContactId.HasValue)
        {
            raw.AddRange(await db.Deals.AsNoTracking()
                .Where(d => d.ContactId == email.ContactId && d.TenantId == tenantId && !d.IsDeleted)
                .OrderByDescending(d => d.UpdatedAtUtc)
                .Select(d => d.OwnerUserId)
                .Take(5)
                .ToListAsync(ct));
        }

        return raw.Select(r => Guid.TryParse(r, out var g) ? g : Guid.Empty)
                  .Where(g => g != Guid.Empty)
                  .Distinct()
                  .ToList();
    }

    /// <summary>
    /// Copies the email's files onto the lead and/or deal it is filed on.
    /// Skips a file already copied to that record (same name, same
    /// "from email" marker), so re-linking never doubles them up.
    /// Returns how many copies were added. ADDS rows; does not save.
    /// A file that cannot be read is logged and skipped — an attachment is
    /// never worth failing the filing of the email itself.
    /// </summary>
    public static async Task<int> CopyAttachmentsToRecordsAsync(
        FlowDbContext db, IFileStorageService storage, InboundEmail email,
        IReadOnlyCollection<InboundEmailAttachment> files, ILogger logger, CancellationToken ct)
    {
        if (files.Count == 0) return 0;

        var targets = new List<(string Type, Guid Id)>();
        if (email.LeadId.HasValue) targets.Add((AttachmentEntityType.Lead, email.LeadId.Value));
        if (email.DealId.HasValue) targets.Add((AttachmentEntityType.Deal, email.DealId.Value));
        if (targets.Count == 0) return 0;

        var marker = CopyMarker(email);
        var added = 0;

        foreach (var (type, id) in targets)
        {
            var already = await db.Attachments.AsNoTracking()
                .Where(a => a.TenantId == email.TenantId && a.EntityType == type && a.EntityId == id
                         && !a.IsDeleted && a.CreatedBy == marker)
                .Select(a => a.FileName)
                .ToListAsync(ct);

            foreach (var file in files)
            {
                if (already.Contains(file.FileName, StringComparer.OrdinalIgnoreCase)) continue;

                // 105 — VUL-14: only a file in this workspace's own folder.
                if (!MerkaiTrial.Application.Commands.Attachments.GetAttachmentDownloadHandler
                        .IsInWorkspaceFolder(file.FileUrl, email.TenantId))
                {
                    logger.LogWarning("Email attachment {AttachmentId} points outside its workspace's folder — not copied", file.Id);
                    continue;
                }

                try
                {
                    await using var stream = await storage.OpenReadAsync(file.FileUrl, ct);
                    if (stream is null)
                    {
                        logger.LogWarning("Email attachment {AttachmentId} is missing from storage — not copied", file.Id);
                        continue;
                    }

                    using var buffer = new MemoryStream();
                    await stream.CopyToAsync(buffer, ct);

                    var saved = await storage.SaveBytesAsync(
                        email.TenantId, type, file.FileName, file.MimeType, buffer.ToArray(), ct);

                    db.Attachments.Add(new Attachment
                    {
                        Id           = Guid.NewGuid(),
                        TenantId     = email.TenantId,
                        EntityType   = type,
                        EntityId     = id,
                        FileName     = saved.FileName,
                        FileUrl      = saved.FileUrl,
                        FileSize     = saved.FileSize,
                        MimeType     = saved.MimeType,
                        CreatedAtUtc = DateTime.UtcNow,
                        CreatedBy    = marker,
                        IsDeleted    = false
                    });

                    already.Add(file.FileName);
                    added++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Could not copy email attachment {AttachmentId} to {Type} {Id}",
                        file.Id, type, id);
                }
            }
        }

        return added;
    }

    /// <summary>
    /// What a copied attachment shows as "uploaded by" on the record's
    /// tab: "Email from priya@acme.com". Also how a second copy is spotted.
    /// Attachment.CreatedBy normally holds a user id (36 characters), so
    /// this is kept to 100 — comfortably inside any width that column has.
    /// </summary>
    public static string CopyMarker(InboundEmail email)
    {
        var who = email.CounterpartAddress ?? email.FromAddress;
        var text = $"Email from {who}";
        return text.Length <= 100 ? text : text[..100];
    }
}
