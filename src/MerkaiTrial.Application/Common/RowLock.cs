// =====================================================================
// RowLock.cs
// Location: MerkaiTrial.Application/Common/RowLock.cs
//
// NEW FILE (100). "One at a time" for a single record.
//
// THE PROBLEM IT SOLVES
//   Read-check-write is not safe when two requests arrive together:
//
//     request 1: lead not converted yet ✓ ... creates deal A
//     request 2: lead not converted yet ✓ ... creates deal B
//
//   Both checks ran before either save, so both passed. The same shape
//   double-counted payments (both read "nothing paid yet") and let an
//   approved quote be edited and sent in the same instant.
//
// WHAT IT DOES
//   Starts a transaction (unless the caller already has one) and takes an
//   UPDATE lock on the one row:
//
//     SELECT TOP (1) 1 FROM dbo.[Leads] WITH (UPDLOCK, ROWLOCK) WHERE Id = @p0
//
//   A second request asking for the same lock WAITS until the first
//   commits or rolls back, and then reads what the first one saved. So
//   it sees "already converted" / "already paid" and refuses properly.
//   Readers are not blocked (an update lock still allows ordinary reads),
//   and other records are not touched.
//
// HOW TO USE IT
//
//     await using var held = await RowLock.HoldAsync(_db, RowLock.Leads, leadId, ct);
//     var lead = await _db.Leads.FirstOrDefaultAsync(...);   // AFTER the lock
//     ... checks, changes, SaveChangesAsync ...
//     await held.CommitAsync(ct);
//
//   • Take the lock BEFORE reading the record, or the read is stale.
//   • Disposing without CommitAsync rolls back — an exception anywhere
//     in between leaves nothing half-saved.
//   • If the caller already has a transaction, the lock joins it and
//     CommitAsync does nothing: the owner of the transaction commits.
//
// The table name only ever comes from the constants below, never from a
// caller's text, so it cannot be used to inject SQL. The id is a
// parameter.
// =====================================================================

using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace MerkaiTrial.Application.Common;

public sealed class RowLock : IAsyncDisposable
{
    public const string Leads    = "Leads";
    public const string Deals    = "Deals";
    public const string Quotes   = "Quotes";
    public const string Invoices = "Invoices";

    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        Leads, Deals, Quotes, Invoices
    };

    private readonly IDbContextTransaction? _owned;
    private bool _committed;

    private RowLock(IDbContextTransaction? owned) => _owned = owned;

    /// <summary>True when this lock started the transaction (and so commits it).</summary>
    public bool OwnsTransaction => _owned is not null;

    /// <summary>
    /// Locks one row until the transaction ends. Waits if someone else
    /// holds it. A row that does not exist locks nothing and is not an
    /// error — the caller's own "not found" check still runs.
    /// </summary>
    public static async Task<RowLock> HoldAsync(FlowDbContext db, string table, Guid id, CancellationToken ct = default)
    {
        if (!Allowed.Contains(table))
            throw new ArgumentOutOfRangeException(nameof(table), table, "RowLock only knows Leads, Deals, Quotes and Invoices.");

        var owned = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;

        try
        {
            // {0} becomes the parameter @p0. The table name is one of the
            // constants above.
            await db.Database.ExecuteSqlRawAsync(
                "SELECT TOP (1) 1 FROM dbo.[" + table + "] WITH (UPDLOCK, ROWLOCK) WHERE Id = {0}",
                new object[] { id }, ct);
        }
        catch
        {
            if (owned is not null)
            {
                try { await owned.RollbackAsync(CancellationToken.None); } catch { /* the original error matters more */ }
                await owned.DisposeAsync();
            }
            throw;
        }

        return new RowLock(owned);
    }

    /// <summary>
    /// 100. "One at a time" for something that is not a row yet — "the
    /// company called Acme in this workspace", "the contact with this
    /// email". Two different leads for the same customer, converted at the
    /// same moment, both looked for that company, both found none, and
    /// both made one. Each now holds this lock on the name first, so the
    /// second waits, then finds the company the first one made.
    ///
    /// Must run inside a transaction (it throws otherwise); the lock is
    /// released when that transaction ends. Several names: take them in
    /// the same order everywhere (sorted), so two requests can never each
    /// hold one and wait for the other.
    ///
    /// SQL Server's sp_getapplock. The name is hashed, so any text is
    /// safe and the 255-character limit never matters; it is passed as a
    /// parameter, never pasted into the SQL.
    /// </summary>
    public static async Task HoldNameAsync(FlowDbContext db, string name, CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("RowLock.HoldNameAsync must run inside a transaction.");

        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)));

        await db.Database.ExecuteSqlRawAsync(
            "DECLARE @r int; " +
            "EXEC @r = sp_getapplock @Resource = {0}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 30000; " +
            "IF @r < 0 THROW 51000, 'Another request is saving the same record. Please try again.', 1;",
            new object[] { "merkai:" + hash }, ct);
    }

    /// <summary>A name for <see cref="HoldNameAsync"/>: trimmed and lower-cased, so "Acme " and "acme" are one lock.</summary>
    public static string NameKey(string kind, Guid tenantId, string value) =>
        $"{kind}:{tenantId:N}:{value.Trim().ToLowerInvariant()}";

    /// <summary>Commits the transaction this lock started; nothing if it joined one.</summary>
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
            catch { /* already gone — the connection dropped, say */ }
        }
        await _owned.DisposeAsync();
    }
}
