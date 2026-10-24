// =====================================================================
// WorkspaceAccess.cs
// Location: MerkaiTrial.Application/Common/WorkspaceAccess.cs
//
// NEW FILE (103). "Can this workspace write right now?" for code that runs
// with nobody signed in — the background workers.
//
// For a signed-in person the answer comes from
// ICurrentTenantService.IsAccountActive(), checked on every write by
// TrialActiveActionFilter (API) and AccountStatePageFilter (pages). The
// workers never pass through either filter, so before 103 an expired or
// suspended workspace's time-based rules kept running: creating tasks and
// sending emails and WhatsApp messages for a workspace whose people could
// no longer add a lead.
//
// The rule is the same as IsAccountActive():
//   live (not deleted), switched on, not suspended, and not a trial whose
//   clock has run out.
// =====================================================================

using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Application.Common;

public static class WorkspaceAccess
{
    /// <summary>The ids of every workspace that may write at <paramref name="nowUtc"/>, as a query (not run yet).</summary>
    public static IQueryable<Guid> WritableTenantIds(FlowDbContext db, DateTime nowUtc)
        => db.Tenants.AsNoTracking().IgnoreQueryFilters()
            .Where(t => !t.IsDeleted
                     && t.IsActive
                     && !t.IsSuspended
                     && !(t.Plan != null
                          && t.Plan.ToLower() == "trial"
                          && t.TrialExpiresAt != null
                          && t.TrialExpiresAt <= nowUtc))
            .Select(t => t.Id);

    /// <summary>True when the workspace may write now.</summary>
    public static Task<bool> CanWriteAsync(FlowDbContext db, Guid tenantId, CancellationToken ct = default)
        => WritableTenantIds(db, DateTime.UtcNow).AnyAsync(id => id == tenantId, ct);
}
