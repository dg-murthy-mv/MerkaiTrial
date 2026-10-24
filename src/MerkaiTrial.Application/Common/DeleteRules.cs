// =====================================================================
// FILE: MerkaiTrial.Application/Common/DeleteRules.cs
//
// NEW (102). When a deal, company or contact may be deleted — the same
// rule quotes, invoices, products and converted leads already follow:
//
//     A record is NOT deleted while other live records depend on it.
//     The refusal says what is in the way and what to do instead.
//
//   DEAL     refused when it is closed (Won or Lost — part of the sales
//            record), or still has an invoice (any, draft included) or a
//            quote. Invoices and accepted quotes stay on record, so the
//            answer for those is "mark the deal Lost".
//   COMPANY  refused while it has live contacts or deals.
//   CONTACT  refused while deals point at it.
//
// WHY
//   Only the deal had rules, and only in two Admin.Web pages — the API
//   deleted any deal, closed or invoiced. A company or contact had none
//   anywhere. Deleting them left deals pointing at a record nobody could
//   open, and a deal deleted with its invoices made those invoices
//   invisible to everyone without "all records" visibility (an invoice
//   follows its deal).
//
// Every handler that deletes one of these asks here first, so the API,
// the pages and any future caller give the same answer in the same words.
// Deleted records keep their history: every delete is written to the
// audit log by its handler, as before.
// =====================================================================

using MerkaiTrial.Application.Commands.PipelineStages;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Domain.Enums;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Application.Common;

public static class DeleteRules
{
    /// <summary>Why this deal can't be deleted, or null when it can.</summary>
    public static async Task<string?> WhyDealCannotBeDeletedAsync(
        FlowDbContext db, Guid tenantId, Deal deal, TenantStages stages, CancellationToken ct = default)
    {
        if (stages.IsTerminal(deal.Stage))
            return $"Closed deals can't be deleted — they are part of the sales record (stage: {stages.NameOf(deal.Stage)}).";

        var invoices = await db.Invoices.AsNoTracking()
            .Where(i => i.TenantId == tenantId && !i.IsDeleted &&
                        (i.DealId == deal.Id || (i.QuoteId != null && i.Quote != null && i.Quote.DealId == deal.Id)))
            .Select(i => i.Number)
            .ToListAsync(ct);

        if (invoices.Count > 0)
            return $"This deal has {Count(invoices.Count, "invoice")} ({Names(invoices)}). " +
                   "Issued invoices stay on record, so the deal can't be deleted — mark it Lost instead. " +
                   "A draft invoice can be deleted first.";

        var quotes = await db.Quotes.AsNoTracking()
            .Where(q => q.TenantId == tenantId && !q.IsDeleted && q.DealId == deal.Id)
            .Select(q => new { q.Number, q.Status })
            .ToListAsync(ct);

        var accepted = quotes.Where(q => q.Status == QuoteStatus.Accepted).Select(q => q.Number).ToList();
        if (accepted.Count > 0)
            return $"This deal has an accepted quote ({Names(accepted)}). The customer's acceptance stays on record, " +
                   "so the deal can't be deleted — mark it Lost instead.";

        if (quotes.Count > 0)
            return $"This deal has {Count(quotes.Count, "quote")} ({Names(quotes.Select(q => q.Number))}). " +
                   "Delete the quotes first, or mark the deal Lost.";

        return null;
    }

    /// <summary>Why this company can't be deleted, or null when it can.</summary>
    public static async Task<string?> WhyCompanyCannotBeDeletedAsync(
        FlowDbContext db, Guid tenantId, Guid companyId, CancellationToken ct = default)
    {
        var contacts = await db.Contacts.AsNoTracking()
            .Where(c => c.TenantId == tenantId && !c.IsDeleted && c.CompanyId == companyId)
            .Select(c => (c.FirstName + " " + (c.LastName ?? "")).Trim())
            .ToListAsync(ct);

        var deals = await db.Deals.AsNoTracking()
            .Where(d => d.TenantId == tenantId && !d.IsDeleted && d.CompanyId == companyId)
            .Select(d => d.Title)
            .ToListAsync(ct);

        if (contacts.Count == 0 && deals.Count == 0) return null;

        var parts = new List<string>();
        if (contacts.Count > 0) parts.Add($"{Count(contacts.Count, "contact")} ({Names(contacts)})");
        if (deals.Count > 0) parts.Add($"{Count(deals.Count, "deal")} ({Names(deals)})");

        return $"This company has {string.Join(" and ", parts)}. " +
               "Move them to another company or delete them first — a company with live contacts or deals can't be deleted.";
    }

    /// <summary>Why this contact can't be deleted, or null when it can.</summary>
    public static async Task<string?> WhyContactCannotBeDeletedAsync(
        FlowDbContext db, Guid tenantId, Guid contactId, CancellationToken ct = default)
    {
        var deals = await db.Deals.AsNoTracking()
            .Where(d => d.TenantId == tenantId && !d.IsDeleted && d.ContactId == contactId)
            .Select(d => d.Title)
            .ToListAsync(ct);

        if (deals.Count == 0) return null;

        return $"This contact is on {Count(deals.Count, "deal")} ({Names(deals)}). " +
               "Change the deals' contact or delete the deals first — a contact with live deals can't be deleted.";
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    /// <summary>At most three names, then "and N more".</summary>
    private static string Names(IEnumerable<string?> names)
    {
        var list = names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).ToList();
        var shown = string.Join(", ", list.Take(3));
        return list.Count > 3 ? $"{shown} and {list.Count - 3} more" : shown;
    }
}
