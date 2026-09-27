// =====================================================================
// OwnerAssignment.cs
// Location: MerkaiTrial.Application/Services/OwnerAssignment.cs
//
// NEW FILE (041). Nothing to register — it is a static class, so no
// change to Scrutor, ApiServiceRegistration or Program.cs.
//
// WHY THIS EXISTS
//
// Leads and deals both carry an OwnerUserId, and until now each one
// interpreted an incoming value its own way:
//
//   UpdateLeadHandler:  lead.OwnerUserId = dto.OwnerUserId;
//                       → whatever arrives wins. A caller that does not
//                         send the field sends null, and the lead is
//                         SILENTLY UNASSIGNED. No validation either, so
//                         a user id from another tenant was accepted and
//                         the lead effectively disappeared from view.
//
//   UpdateDealHandler:  if (!string.IsNullOrEmpty(dto.OwnerUserId)) { ... }
//                       → the opposite bug. A deal can never be
//                         unassigned, and an owner who fails validation
//                         is dropped in silence: the page says "saved"
//                         and the owner did not change.
//
// One record type answering the same question two different ways is how
// support tickets are made. From 041 there is ONE answer, here, and both
// modules ask it.
//
// THE THREE CASES
//
//   null            the field was not sent  → leave the owner alone
//   "" / "   "      sent, deliberately empty → unassign
//   a user id       sent with a value        → assign, after checking the
//                                             user is active in THIS
//                                             tenant
//
// Those three are only distinguishable because UpdateLeadDto and
// UpdateDealDto declare OwnerUserId as string? rather than string.
// Keep it nullable.
//
// A NOTE ON RAZOR FORMS. MVC model binding turns an empty posted value
// into null for a string property by default, which would collapse case
// 2 into case 1 and make "unassign" unreachable from the UI. The two
// edit page models in this round carry
//
//     [DisplayFormat(ConvertEmptyStringToNull = false)]
//
// on Input.OwnerUserId for exactly that reason. JSON bodies arriving at
// the API are unaffected — System.Text.Json keeps "" as "".
//
// WHY AN UNCHANGED OWNER IS NOT RE-VALIDATED
//
// Both edit pages post the CURRENT owner back on every save. If the
// owner has since been deactivated, validating that value would make
// every later edit fail — you could not even fix the deal's title
// without first reassigning it. So an incoming value equal to the value
// already on the record is accepted untouched. Validation applies to a
// CHANGE of owner, which is the only time a person is choosing.
// =====================================================================

using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Application.Services;

public enum OwnerChange
{
    /// <summary>The field was absent, or already held this value.</summary>
    Unchanged = 0,

    /// <summary>A different, validated user now owns the record.</summary>
    Assigned = 1,

    /// <summary>The record was deliberately left with no owner.</summary>
    Unassigned = 2
}

/// <summary>
/// What should happen to a record's OwnerUserId, and the value to store.
/// OwnerUserId is the value AFTER the decision: null when unassigning,
/// the old value when unchanged.
/// </summary>
public readonly record struct OwnerDecision(OwnerChange Change, string? OwnerUserId)
{
    public bool Changed => Change != OwnerChange.Unchanged;
}

public static class OwnerAssignment
{
    /// <summary>
    /// Decides what an incoming owner value means on an UPDATE.
    /// Throws InvalidOperationException with a message meant for a
    /// person when a new owner is not a usable user in this tenant —
    /// the API turns that into 400 { "error": "..." } and the page shows
    /// it above the form.
    /// </summary>
    /// <param name="db">Tenant-filtered context. Users are exempt from
    /// the global filter, so TenantId is compared explicitly below.</param>
    /// <param name="tenantId">The tenant that owns the record.</param>
    /// <param name="incoming">dto.OwnerUserId, exactly as it arrived.</param>
    /// <param name="currentOwnerUserId">The value on the record now.</param>
    public static async Task<OwnerDecision> ForUpdateAsync(
        FlowDbContext db,
        Guid tenantId,
        string? incoming,
        string? currentOwnerUserId,
        CancellationToken ct = default)
    {
        // Case 1 — the field was not sent at all.
        if (incoming is null)
            return new OwnerDecision(OwnerChange.Unchanged, currentOwnerUserId);

        var wanted = incoming.Trim();

        // Case 2 — sent empty: unassign. If there is no owner already,
        // that is not a change and must not raise a notification or an
        // audit row.
        if (wanted.Length == 0)
            return string.IsNullOrWhiteSpace(currentOwnerUserId)
                ? new OwnerDecision(OwnerChange.Unchanged, currentOwnerUserId)
                : new OwnerDecision(OwnerChange.Unassigned, null);

        // Same owner posted back — accept without a lookup. See the note
        // at the top about deactivated owners.
        if (string.Equals(wanted, currentOwnerUserId?.Trim(), StringComparison.OrdinalIgnoreCase))
            return new OwnerDecision(OwnerChange.Unchanged, currentOwnerUserId);

        // Case 3 — a real change. This is where we are strict.
        await EnsureUsableAsync(db, tenantId, wanted, ct);

        return new OwnerDecision(OwnerChange.Assigned, wanted);
    }

    /// <summary>
    /// The owner to store on a NEW record. No owner chosen → the creator
    /// keeps it, so a rep whose record scope is "Own" does not add
    /// something and immediately lose sight of it. An owner that IS
    /// chosen is validated the same way as on update.
    /// </summary>
    public static async Task<string> ForCreateAsync(
        FlowDbContext db,
        Guid tenantId,
        string? incoming,
        Guid creatorUserId,
        CancellationToken ct = default)
    {
        var wanted = incoming?.Trim();

        // Covers null AND "" — the old deal code used `?? currentUser`,
        // which let an empty string through and created a deal owned by
        // the empty string: not visible to its creator, not visible to
        // anyone, and not reported as unassigned either.
        if (string.IsNullOrEmpty(wanted))
            return creatorUserId.ToString();

        if (string.Equals(wanted, creatorUserId.ToString(), StringComparison.OrdinalIgnoreCase))
            return wanted;

        await EnsureUsableAsync(db, tenantId, wanted, ct);

        return wanted;
    }

    /// <summary>
    /// Is this a user who can be given work in this tenant? Deliberately
    /// one query, and deliberately explicit about TenantId: User is in
    /// FlowDbContext.TenantFilterExemptions, so the global filter does
    /// NOT protect this lookup. Without the comparison below, a user id
    /// from another workspace would be accepted.
    /// </summary>
    private static async Task EnsureUsableAsync(
        FlowDbContext db,
        Guid tenantId,
        string ownerUserId,
        CancellationToken ct)
    {
        if (!Guid.TryParse(ownerUserId, out var ownerGuid))
            throw new InvalidOperationException(
                "That owner is not a valid user. Pick someone from the list.");

        var state = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == ownerGuid && u.TenantId == tenantId)
            .Select(u => new { u.IsActive, u.IsDeleted, u.FirstName, u.LastName })
            .FirstOrDefaultAsync(ct);

        // Not in this tenant at all — same answer as "does not exist".
        // Saying "that user belongs to another workspace" would confirm
        // the id exists somewhere, which is not this caller's business.
        if (state is null || state.IsDeleted)
            throw new InvalidOperationException(
                "That user is no longer in this workspace. Pick someone from the list.");

        if (!state.IsActive)
        {
            var name = $"{state.FirstName} {state.LastName}".Trim();

            throw new InvalidOperationException(
                string.IsNullOrEmpty(name)
                    ? "That user account is deactivated, so it cannot be given new work."
                    : $"{name}'s account is deactivated, so it cannot be given new work.");
        }
    }
}
