// =====================================================================
// PlanQuota.cs
// Location: MerkaiTrial.Application/Common/PlanQuota.cs
//
// NEW FILE (103). "Is there room on the plan for one more?" — asked the
// same way, and safely, by every place that adds a lead, deal, contact,
// company or person.
//
// WHAT WAS WRONG BEFORE
//   1. Each create handler counted on its own and nothing held the count
//      still. With one place left, two saves at the same instant both
//      counted N-1, both passed, and the workspace ended up over its plan.
//   2. Converting a lead creates a deal, and often a contact and a
//      company, and checked none of the three limits. A workspace at its
//      deal limit could keep adding deals by converting leads.
//   3. A limit of -1 (the Plans panel shows it as unlimited) refused
//      everything: "you have 3 and your plan allows -1".
//
// WHAT IT DOES
//   Holds a lock on "this workspace's <leads> count" until the caller's
//   transaction ends, reads the LIVE limit (changed by the super admin a
//   second ago? that is the limit), counts, and refuses with
//   PlanLimitExceededException when there is no room. A second request
//   waits for the first to commit and then counts what the first saved.
//
//     await using var quota = await PlanQuota.HoldAsync(_db, tenantId, PlanQuota.Leads, ct: ct);
//     ... add the lead, SaveChangesAsync ...
//     await quota.CommitAsync(ct);
//
//   • Starts a transaction unless the caller already has one; then
//     CommitAsync commits it. Joined to the caller's: CommitAsync does
//     nothing and the caller commits.
//   • Disposing without CommitAsync rolls back.
//   • Several kinds in one transaction (lead conversion): take them in the
//     order deals → companies → contacts, everywhere, so two requests can
//     never each hold one and wait for the other.
//
// WHERE THE LIMITS LIVE (unchanged)
//   leads, deals, users   — TenantSettings (copied from the plan; the
//                           super admin can change them per workspace)
//   contacts, companies   — the workspace's Plan row
//   No row → no limit to apply (MadeeVision itself has no settings row).
//   A negative limit → unlimited.
// =====================================================================

using MerkaiTrial.Application.Exceptions;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace MerkaiTrial.Application.Common;

public sealed class PlanQuota : IAsyncDisposable
{
    public const string Leads     = "leads";
    public const string Deals     = "deals";
    public const string Contacts  = "contacts";
    public const string Companies = "companies";
    public const string Users     = "users";

    private readonly IDbContextTransaction? _owned;
    private bool _committed;

    private PlanQuota(IDbContextTransaction? owned) => _owned = owned;

    /// <summary>
    /// Holds this workspace's count of <paramref name="kind"/> still and
    /// checks there is room for <paramref name="adding"/> more. Throws
    /// <see cref="PlanLimitExceededException"/> when there isn't (having
    /// rolled back a transaction it started).
    /// </summary>
    public static async Task<PlanQuota> HoldAsync(
        FlowDbContext db, Guid tenantId, string kind, int adding = 1, CancellationToken ct = default)
    {
        var owned = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;

        try
        {
            await RowLock.HoldNameAsync(db, RowLock.NameKey("quota", tenantId, kind), ct);

            var limit = await LimitAsync(db, tenantId, kind, ct);
            if (limit is int max && max >= 0)
            {
                var current = await CountAsync(db, tenantId, kind, ct);
                if (current + Math.Max(adding, 0) > max)
                    throw new PlanLimitExceededException(kind, current, max);
            }
        }
        catch
        {
            if (owned is not null)
            {
                try { await owned.RollbackAsync(CancellationToken.None); } catch { /* the refusal matters more */ }
                await owned.DisposeAsync();
            }
            throw;
        }

        return new PlanQuota(owned);
    }

    /// <summary>The workspace's live limit for this kind; null when there is none to apply.</summary>
    public static async Task<int?> LimitAsync(FlowDbContext db, Guid tenantId, string kind, CancellationToken ct = default)
    {
        switch (kind)
        {
            case Leads:
            case Deals:
            case Users:
                var s = await db.Set<TenantSettings>().AsNoTracking().IgnoreQueryFilters()
                    .Where(x => x.TenantId == tenantId)
                    .Select(x => new { x.MaxLeads, x.MaxDeals, x.MaxUsers })
                    .FirstOrDefaultAsync(ct);
                if (s is null) return null;
                return kind == Leads ? s.MaxLeads : kind == Deals ? s.MaxDeals : s.MaxUsers;

            case Contacts:
            case Companies:
                var planName = await db.Tenants.AsNoTracking().IgnoreQueryFilters()
                    .Where(t => t.Id == tenantId && !t.IsDeleted)
                    .Select(t => t.Plan)
                    .FirstOrDefaultAsync(ct);
                if (string.IsNullOrEmpty(planName)) return null;
                var p = await db.Plans.AsNoTracking()
                    .Where(x => x.Name == planName && x.IsActive)
                    .Select(x => new { x.MaxContacts, x.MaxCompanies })
                    .FirstOrDefaultAsync(ct);
                if (p is null) return null;
                return kind == Contacts ? p.MaxContacts : p.MaxCompanies;

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "PlanQuota knows leads, deals, contacts, companies and users.");
        }
    }

    /// <summary>Live (not deleted) records of this kind in the workspace — what the limit is measured against.</summary>
    public static Task<int> CountAsync(FlowDbContext db, Guid tenantId, string kind, CancellationToken ct = default)
        => kind switch
        {
            Leads     => db.Leads.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantId && !x.IsDeleted, ct),
            Deals     => db.Deals.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantId && !x.IsDeleted, ct),
            Contacts  => db.Contacts.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantId && !x.IsDeleted, ct),
            Companies => db.Companies.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantId && !x.IsDeleted, ct),
            // Everyone in the workspace, switched on or off: a person who
            // is switched off still holds their place (and can be
            // switched back on without asking the plan).
            Users     => db.Users.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantId && !x.IsDeleted, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "PlanQuota knows leads, deals, contacts, companies and users."),
        };

    /// <summary>Commits the transaction this hold started; nothing if it joined one.</summary>
    public async Task CommitAsync(CancellationToken ct = default)
    {
        if (_owned is null || _committed) return;
        await _owned.CommitAsync(ct);
        _committed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_owned is null) return;
        if (!_committed)
        {
            try { await _owned.RollbackAsync(CancellationToken.None); }
            catch { /* already gone */ }
        }
        await _owned.DisposeAsync();
    }
}
