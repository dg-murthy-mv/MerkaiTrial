// =====================================================================
// FILE: MerkaiTrial.Application/Common/RecordVersion.cs
//
// NEW (102). Optimistic concurrency for the four records people edit in a
// form: leads, deals, contacts and companies.
//
// HOW IT WORKS
//   • Each of those tables has a rowversion column (Sql/102_RowVersion.sql)
//     that SQL Server changes by itself on every write.
//   • The detail DTO carries it as Version (Of), the edit page keeps it in
//     a hidden field, and the update DTO sends it back.
//   • The update handler calls Expect BEFORE changing anything:
//       – an older Version than the row has now → RecordChangedException
//         straight away ("Someone else changed this lead…"), nothing touched;
//       – otherwise the Version becomes the row's expected original value,
//         so a save that slips in between this read and the save is caught
//         too, by the database itself (FlowDbContext turns that into the
//         same exception).
//   • No Version sent (an older API client) → the save still can't overwrite
//     a change made between this request's read and its save, but a form
//     loaded long ago isn't checked. Every page in Admin.Web sends it.
// =====================================================================

using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Application.Common;

public static class RecordVersion
{
    /// <summary>The row's version as text for a DTO and a hidden form field; null when there is none.</summary>
    public static string? Of(byte[]? rowVersion) =>
        rowVersion is { Length: > 0 } ? Convert.ToBase64String(rowVersion) : null;

    /// <summary>
    /// The edit was made from <paramref name="version"/>. Refuses it now if
    /// the record has changed since; otherwise makes the save itself check.
    /// <paramref name="kind"/> names the record for the message ("lead").
    /// </summary>
    public static void Expect<TEntity>(FlowDbContext db, TEntity entity, Func<TEntity, byte[]?> current,
        string? version, string kind) where TEntity : class
    {
        if (string.IsNullOrWhiteSpace(version)) return;

        byte[] expected;
        try
        {
            expected = Convert.FromBase64String(version.Trim());
        }
        catch (FormatException)
        {
            throw new InvalidOperationException(
                $"This {kind}'s form is out of date or damaged. Reload the {kind} and make your changes again.");
        }

        var now = current(entity);
        if (now is null || !now.AsSpan().SequenceEqual(expected))
            throw new RecordChangedException(RecordChangedException.MessageFor(kind));

        // The UPDATE will say "AND RowVersion = <expected>" — the database
        // refuses it if anyone saves in the meantime.
        // Every versioned entity names the property RowVersion.
        db.Entry(entity).Property("RowVersion").OriginalValue = expected;
    }
}
