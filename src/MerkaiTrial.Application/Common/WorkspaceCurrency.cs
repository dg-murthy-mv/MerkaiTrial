// =====================================================================
// WorkspaceCurrency.cs
// Location: MerkaiTrial.Application/Common/WorkspaceCurrency.cs
//
// NEW (111). One currency per workspace — the design every report,
// total and PDF relies on. A Philippines workspace works in PHP, full
// stop.
//
// Before 111 the screens filled in the workspace's currency, but the
// server stored whatever it was sent: the lead form had a currency
// drop-down (it followed the lead's country, so a Thai lead in a
// Philippines workspace was saved in THB), and the API took any currency
// for leads, deals, quotes and invoices. Reports then added baht to
// pesos.
//
// Now every create and update of a lead, deal, quote or invoice stores
// the workspace's currency, whatever the form or the API call said.
// (A payment always takes its invoice's currency.)
//
// A workspace with no DefaultCurrency set (very old data) keeps what it
// was sent — there is nothing better to put there.
// =====================================================================

using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Application.Common;

public static class WorkspaceCurrency
{
    /// <summary>
    /// The workspace's currency code (upper case), or <paramref name="fallback"/>
    /// when the workspace has none set.
    /// </summary>
    public static async Task<string> ForAsync(FlowDbContext db, Guid tenantId, string? fallback,
        CancellationToken ct = default)
    {
        var code = await db.Tenants.AsNoTracking().IgnoreQueryFilters()
            .Where(t => t.Id == tenantId)
            .Select(t => t.DefaultCurrency)
            .FirstOrDefaultAsync(ct);

        if (!string.IsNullOrWhiteSpace(code)) return code.Trim().ToUpperInvariant();
        return (fallback ?? string.Empty).Trim().ToUpperInvariant();
    }
}
