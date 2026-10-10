// =====================================================================
// FILE: MerkaiTrial.Application/Commands/AuditChanges.cs
//
// NEW FILE (078b). Small helpers for the "…Updated" audit rows, shared by
// the Contact and Company handlers (and the Lead handlers next).
//
// THE RULE (from AuditLog.cs): a field-level edit is ONE audit row with
// the changed fields in Data, not one row per field. These helpers build
// that list of NAMES.
//
// NAMES, NOT VALUES. An audit log is widely readable by design, and a
// contact's email or phone, or a company's tax number, is exactly the
// "raw personal data" AuditLog.Data must not copy. The row says WHAT
// changed, WHO changed it and WHEN; the record itself holds the value.
//
// Custom field VALUES are likewise not written (075: definitions are
// audited, values are not) — but WHETHER any changed in this save is,
// as the single name "customFields", read from EF's change tracker just
// before SaveChanges, so it is exactly what is about to be written.
// =====================================================================

using MerkaiTrial.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Application.Commands
{
    public static class AuditChanges
    {
        /// <summary>The name used in the changed list for any custom field value change.</summary>
        public const string CustomFields = "customFields";

        /// <summary>
        /// Adds <paramref name="field"/> to <paramref name="changed"/> when the
        /// two values differ. Strings compare after trimming, with null and
        /// empty treated alike — re-saving a form that turns null into ""
        /// is not a change anybody made.
        /// </summary>
        public static void Track<T>(List<string> changed, string field, T before, T after)
        {
            if (before is string || after is string)
            {
                var b = (before as string)?.Trim() ?? string.Empty;
                var a = (after  as string)?.Trim() ?? string.Empty;
                if (!string.Equals(b, a, StringComparison.Ordinal)) changed.Add(field);
                return;
            }

            if (!EqualityComparer<T>.Default.Equals(before, after)) changed.Add(field);
        }

        /// <summary>
        /// True when this save will add, change or remove any custom field
        /// value. Call AFTER CustomFieldValueWriter.ApplyAsync and BEFORE
        /// SaveChangesAsync.
        /// </summary>
        public static bool CustomFieldValuesPending(DbContext db)
            => db.ChangeTracker.Entries<CustomFieldValue>()
                 .Any(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
    }
}
