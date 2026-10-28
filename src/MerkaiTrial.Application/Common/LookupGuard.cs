// =====================================================================
// LookupGuard.cs
// Location: MerkaiTrial.Application/Common/LookupGuard.cs
//
// NEW FILE (104). "Is this source / channel / industry one this workspace
// can use?" — asked before a lead or deal is saved with it.
//
// WHAT WAS WRONG
//   A lead's SourceId, ChannelId and VerticalId, and a deal's SourceId and
//   VerticalId, were stored exactly as the request sent them. The lists on
//   the page only offer this workspace's own entries (and the system's
//   shared ones), but the API took any id — including an entry another
//   workspace made, whose NAME then showed on this workspace's record. It is
//   the same over-posting gap round 100 closed for contacts, companies and
//   owners.
//
// THE RULE
//   An id is fine when it is empty, or names a live entry that is either the
//   system's shared one (TenantId NULL) or this workspace's own. Anything
//   else is refused with a sentence (InvalidOperationException → 400) before
//   anything is saved.
// =====================================================================

using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Application.Common;

public static class LookupGuard
{
    /// <summary>Refuses a source, channel or industry that isn't the system's or this workspace's own.</summary>
    public static async Task CheckAsync(FlowDbContext db, Guid tenantId,
        Guid? sourceId, Guid? channelId, Guid? verticalId, CancellationToken ct = default)
    {
        if (sourceId is Guid s && s != Guid.Empty &&
            !await db.LeadSources.AsNoTracking().IgnoreQueryFilters()
                .AnyAsync(x => x.Id == s && !x.IsDeleted && (x.TenantId == null || x.TenantId == tenantId), ct))
            throw new InvalidOperationException("The source chosen isn't one of this workspace's. Pick one from the list.");

        if (channelId is Guid c && c != Guid.Empty &&
            !await db.LeadChannels.AsNoTracking().IgnoreQueryFilters()
                .AnyAsync(x => x.Id == c && !x.IsDeleted && (x.TenantId == null || x.TenantId == tenantId), ct))
            throw new InvalidOperationException("The channel chosen isn't one of this workspace's. Pick one from the list.");

        if (verticalId is Guid v && v != Guid.Empty &&
            !await db.CompanyVerticals.AsNoTracking().IgnoreQueryFilters()
                .AnyAsync(x => x.Id == v && !x.IsDeleted && (x.TenantId == null || x.TenantId == tenantId), ct))
            throw new InvalidOperationException("The industry chosen isn't one of this workspace's. Pick one from the list.");
    }
}
