// =====================================================================
// LeadStatusHandlers.cs
// Location: MerkaiTrial.Application/Commands/LeadStatuses/
//
// COMPLETE FILE — replaces the existing one.
//
// CHANGES (025 — lead status care)
//
//   ✅ MOVE LEADS OUT OF A STATUS. The headline, and the same gap 023
//      closed for pipeline stages. Both the retire path and the delete
//      path refused a status holding leads with "move them first" — and
//      nothing in the product moved them except one lead at a time.
//
//   ✅ TotalLeadCount, SEPARATE FROM LeadCount. This one matters.
//      LeadCount is deliberately scoped by record visibility, because it
//      drives the Leads page tab counts and must agree with the list the
//      user is about to see. But the retire and delete rules count EVERY
//      lead in the tenant. Reusing the visible count for those would tell
//      a sales manager with Own scope that a status is empty, offer them
//      Delete, and then refuse — the exact class of bug 023 set out to
//      remove. The settings page reads TotalLeadCount; the Leads page
//      keeps reading LeadCount.
//
//   ✅ THE DTO CARRIES THE REASONS. CanDelete / DeleteBlockedReason and
//      CanRetire / RetireBlockedReason, computed from exactly the checks
//      the write handlers enforce, so the UI can never promise something
//      the server will refuse.
//
//   ✅ THE SYSTEM STATUS CANNOT DRIFT INTO THE MIDDLE OF THE ORDER.
//      Reorder used to apply whatever order it was handed. The settings
//      page hid the consequence because it renders Converted in its own
//      section, but the Leads page tabs read SortOrder directly.
//
//   ✅ Colour and description on create and update.
//
// THE RULES, ENFORCED HERE RATHER THAN IN THE UI
//   • Key is generated once and never changes — renaming must not move
//     the leads sitting in a status.
//   • A tenant must always have at least one Qualified status, or no lead
//     could ever be converted to a deal.
//   • Converted is IsSystem: renameable, but not deletable, not
//     deactivatable, and not offered in any dropdown.
//   • A status holding leads cannot be deleted — only emptied and then
//     retired.
//   • The system status always sorts last.
// =====================================================================

using MerkaiTrial.Application.Security;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text;

namespace MerkaiTrial.Application.Commands.LeadStatuses;

// ── DTOs ──────────────────────────────────────────────────────────────

/// <param name="LeadCount">
/// Leads in this status THAT THE CURRENT USER CAN SEE. Drives the Leads
/// page tab counts, so it must agree with the list they are about to see.
/// NOT the number the retire and delete rules use.
/// </param>
/// <param name="TotalLeadCount">
/// Every live lead in this status, whoever owns it. What the retire and
/// delete rules actually count, and what the settings page displays. Only
/// populated when the caller asks for the admin detail.
/// </param>
/// <param name="Color">Always a real "#RRGGBB" — resolved server-side, never null.</param>
/// <param name="BlockedOnlyByLeads">
/// True when the only thing standing between this status and a retirement
/// is the leads sitting in it — the one case the page can offer to fix.
/// </param>
public record LeadStatusDto(
    Guid Id,
    string Key,
    string Name,
    int SortOrder,
    int Score,
    LeadStatusCategory Category,
    bool IsActive,
    bool IsDefault,
    bool IsSystem,
    int LeadCount,

    // ── 025 ──────────────────────────────────────────────────────────
    // Optional with defaults so that any existing positional
    // construction of this record elsewhere in the solution keeps
    // compiling.
    string Color = "#6366F1",
    string? Description = null,
    int TotalLeadCount = 0,
    bool CanDelete = false,
    string? DeleteBlockedReason = null,
    bool CanRetire = false,
    string? RetireBlockedReason = null,
    bool BlockedOnlyByLeads = false);

public record CreateLeadStatusDto(
    Guid TenantId,
    string Name,
    int Score,
    LeadStatusCategory Category,
    string? CreatedBy = null,
    string? Color = null,
    string? Description = null);

public record UpdateLeadStatusDefDto(
    Guid TenantId,
    Guid StatusId,
    string Name,
    int Score,
    bool IsActive,
    string? UpdatedBy = null,
    string? Color = null,
    string? Description = null);

/// <param name="OrderedIds">
/// Status ids in the order they should appear. Ids left out keep their
/// relative order, after the ones listed. The system status is forced
/// last whatever this says.
/// </param>
public record ReorderLeadStatusesDto(
    Guid TenantId,
    List<Guid> OrderedIds,
    string? UpdatedBy = null);

/// <summary>Empty a status so it can be retired.</summary>
public record MoveStatusLeadsDto(
    Guid TenantId,
    Guid FromStatusId,
    Guid ToStatusId,
    string? MovedBy = null,
    bool ThenRetire = false);

/// <param name="Problems">
/// One line per lead whose bookkeeping failed AFTER the move committed —
/// a timeline entry that could not be written, a score that could not be
/// recalculated. Never a reason to report the move itself as failed.
/// Defaulted so an older caller still compiles.
/// </param>
public record MoveStatusLeadsResult(
    int Moved,
    string FromName,
    string ToName,
    bool Retired,
    List<string>? Problems = null);

// ── RESOLVER — the shared piece every lead handler uses ───────────────

/// <summary>
/// A tenant's statuses, loaded once and asked many questions. Mirrors
/// TenantStages for deals.
/// </summary>
public sealed class TenantLeadStatuses
{
    private readonly List<LeadStatusDefinition> _all;

    public TenantLeadStatuses(List<LeadStatusDefinition> all) => _all = all;

    public IReadOnlyList<LeadStatusDefinition> All => _all;

    /// <summary>
    /// What a person may choose. Excludes system statuses — Converted is
    /// set by conversion and by nothing else.
    /// </summary>
    public IReadOnlyList<LeadStatusDefinition> Selectable =>
        _all.Where(s => s.IsActive && !s.IsSystem).OrderBy(s => s.SortOrder).ToList();

    public LeadStatusDefinition? Find(string? key) =>
        key is null ? null : _all.FirstOrDefault(s => s.Key == key);

    public bool IsValid(string? key) => Find(key) is not null;

    /// <summary>
    /// Ready to become a deal. Conversion checks THIS, not a status named
    /// "Qualified" — a tenant calling it "Ready for Site Visit" must still
    /// be able to convert.
    /// </summary>
    public bool IsQualified(string? key) =>
        Find(key)?.Category == LeadStatusCategory.Qualified;

    public bool IsConverted(string? key) =>
        Find(key)?.Category == LeadStatusCategory.Converted;

    public bool IsDisqualified(string? key) =>
        Find(key)?.Category == LeadStatusCategory.Disqualified;

    public int ScoreOf(string? key) => Find(key)?.Score ?? 0;

    /// <summary>Display name, falling back to the key for a status that
    /// was deleted after the history was written.</summary>
    public string NameOf(string? key) =>
        string.IsNullOrEmpty(key) ? "—" : (Find(key)?.Name ?? key);

    /// <summary>Where a new lead starts.</summary>
    public LeadStatusDefinition? Default =>
        _all.FirstOrDefault(s => s.IsDefault && s.IsActive && !s.IsSystem)
        ?? Selectable.FirstOrDefault();

    /// <summary>The single system status conversion writes.</summary>
    public LeadStatusDefinition? ConvertedStatus =>
        _all.FirstOrDefault(s => s.Category == LeadStatusCategory.Converted);

    public string SelectableKeysText => string.Join(", ", Selectable.Select(s => s.Key));
}

public interface ILeadStatusResolver
{
    Task<TenantLeadStatuses> GetAsync(Guid tenantId, CancellationToken ct = default);
}

public class LeadStatusResolver : ILeadStatusResolver
{
    private readonly FlowDbContext _db;

    public LeadStatusResolver(FlowDbContext db) => _db = db;

    public async Task<TenantLeadStatuses> GetAsync(Guid tenantId, CancellationToken ct = default)
    {
        // Retired statuses included on purpose: an existing lead may sit
        // in one, and it must still resolve to a name and a category.
        var all = await _db.LeadStatusDefinitions.AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .OrderBy(s => s.SortOrder)
            .ToListAsync(ct);

        return new TenantLeadStatuses(all);
    }
}

// ── SHARED ────────────────────────────────────────────────────────────

internal static class StatusKeys
{
    /// <summary>
    /// "Site Visit" -> "SiteVisit". ASCII letters and digits only, so a
    /// Thai-named status still produces a usable key.
    /// </summary>
    public static string FromName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name)
            if (char.IsLetterOrDigit(c) && c < 128) sb.Append(c);

        var key = sb.ToString();
        return key.Length == 0 ? "Status" + DateTime.UtcNow.Ticks.ToString()[^6..] : key;
    }

    public static async Task<string> UniqueAsync(
        FlowDbContext db, Guid tenantId, string name, CancellationToken ct)
    {
        var baseKey = FromName(name);
        var key = baseKey;
        var n = 2;

        while (await db.LeadStatusDefinitions.AnyAsync(
                   s => s.TenantId == tenantId && s.Key == key, ct))
            key = baseKey + n++;

        return key;
    }
}

internal static class StatusOrdering
{
    /// <summary>
    /// Assigns SortOrder 1..n across the whole tenant, working statuses
    /// first and the system status last.
    ///
    /// EVERY path that changes order goes through here. The invariant is
    /// not "the page sends a sensible order"; it is "no order the page
    /// can send produces a bad result". The old code trusted the caller,
    /// and the settings page hid the consequence because it renders
    /// Converted in its own section — but the Leads page tabs read
    /// SortOrder directly, so a Converted tab could end up in the middle
    /// of the working statuses.
    /// </summary>
    /// <param name="preferred">
    /// Optional ranking hint. Statuses named here sort in that order
    /// within their own group; statuses not named keep their existing
    /// relative order, after the named ones.
    /// </param>
    public static void Renumber(List<LeadStatusDefinition> statuses, List<Guid>? preferred, string? by)
    {
        var rank = new Dictionary<Guid, int>();
        if (preferred is not null)
            for (var i = 0; i < preferred.Count; i++)
                rank[preferred[i]] = i;

        var ordered = statuses
            .OrderBy(s => s.IsSystem ? 1 : 0)
            .ThenBy(s => rank.TryGetValue(s.Id, out var r) ? r : int.MaxValue)
            .ThenBy(s => s.SortOrder)
            .ThenBy(s => s.Id)
            .ToList();

        var order = 1;
        foreach (var s in ordered)
        {
            var next = order++;
            if (s.SortOrder == next) continue;   // don't dirty rows that didn't move

            s.SortOrder = next;
            s.UpdatedAtUtc = DateTime.UtcNow;
            s.UpdatedBy = by;
        }
    }
}

internal static class StatusRules
{
    /// <summary>
    /// The reasons an ACTIVE status cannot be retired, in the order a
    /// person can act on them. Null means it can.
    ///
    /// CALLERS MUST CHECK IsActive THEMSELVES — null has to mean one
    /// thing, so "already retired" belongs to the caller.
    ///
    /// Shared by the read handler (to disable the menu item and explain)
    /// and the write handler (to refuse), including the "…and here is
    /// what fixes it" suffix, so the two can never disagree.
    /// </summary>
    /// <param name="hasMoveTarget">
    /// Whether another ACTIVE, non-system status of the same category
    /// exists to move the leads into. Without one there is nowhere to
    /// send them, and telling someone to use a tool that cannot work is
    /// worse than telling them nothing.
    /// </param>
    public static string? WhyNotRetire(
        LeadStatusDefinition status, int leadCount, int otherActiveQualified, bool hasMoveTarget)
    {
        if (status.IsSystem)
            return "This status is set automatically when a lead converts, so it can't be retired. You can rename it.";

        if (status.IsDefault)
            return "New leads start here. Make another status the starting point first.";

        if (status.Category == LeadStatusCategory.Qualified && otherActiveQualified == 0)
            return "This is your only qualified status. Without it, no lead could be converted to a deal.";

        if (leadCount > 0)
            return hasMoveTarget
                ? $"{leadCount} {Leads(leadCount)} still here. " +
                  "Use \"Move leads somewhere else\" on this menu to send them on first."
                : $"{leadCount} {Leads(leadCount)} still here, and there is no other active " +
                  "status of the same kind to move them to. Add one first.";

        return null;
    }

    public static string? WhyNotDelete(
        LeadStatusDefinition status, int leadCount, int otherQualified)
    {
        // Same order as DeleteLeadStatusHandler checks them, so the
        // message on the disabled menu item is the message you would have
        // got from pressing it.
        if (status.IsSystem)
            return "This status is set automatically when a lead converts and can't be removed.";

        if (status.IsDefault)
            return "New leads start here. Make another status the starting point first.";

        if (leadCount > 0)
            return $"{leadCount} {Leads(leadCount)} in this status.";

        if (status.Category == LeadStatusCategory.Qualified && otherQualified == 0)
            return "This is your only qualified status. Without it, no lead could be converted to a deal.";

        return null;
    }

    /// <summary>
    /// True when moving the leads out is all that stands between this
    /// status and a retirement — the one case the page can offer to fix.
    /// </summary>
    public static bool BlockedOnlyByLeads(
        LeadStatusDefinition status, int leadCount, int otherActiveQualified, bool hasMoveTarget)
        => status.IsActive
           && !status.IsSystem
           && leadCount > 0
           && !status.IsDefault
           && hasMoveTarget                // ← without this the menu offers a move
           && !(status.Category == LeadStatusCategory.Qualified && otherActiveQualified == 0);

    public static string Leads(int n) => n == 1 ? "lead is" : "leads are";
}

// ── READ ──────────────────────────────────────────────────────────────

public class GetLeadStatusesHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly IRecordScopeService _scope;

    public GetLeadStatusesHandler(FlowDbContext db, IRecordScopeService scope)
    {
        _db = db;
        _scope = scope;
    }

    /// <param name="withAdminDetail">
    /// TotalLeadCount and the delete/retire reasons. Only the settings
    /// page needs them, and working them out costs an extra round trip.
    ///
    /// THIS ENDPOINT IS HOT — every lead page and list reads it to render
    /// a dropdown or a badge. Making all of them pay for numbers only one
    /// settings page displays is how a helpful addition turns into a
    /// performance regression nobody connects back to it.
    /// </param>
    public async Task<List<LeadStatusDto>> Handle(
        Guid tenantId,
        bool selectableOnly = false,
        bool withAdminDetail = false,
        CancellationToken ct = default)
    {
        // EVERY status is loaded, always, and selectableOnly filters only
        // what comes BACK. The rules below count across all of them —
        // "is this your only qualified status" has to include the retired
        // ones, because a retired status can be brought back and a deleted
        // one cannot — and the colour ordinal is a position among all open
        // statuses. Filtering first made the same status report a
        // different colour and a different CanDelete depending on which
        // caller asked, which is precisely the read/write disagreement
        // this round set out to remove.
        var all = await _db.LeadStatusDefinitions.AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .OrderBy(s => s.SortOrder)
            .ToListAsync(ct);

        if (all.Count == 0) return new List<LeadStatusDto>();

        var statuses = selectableOnly
            ? all.Where(s => s.IsActive && !s.IsSystem).ToList()
            : all;

        if (statuses.Count == 0) return new List<LeadStatusDto>();

        // Counts respect record visibility — they are the Leads page's tab
        // counts, and must agree with the list the user will see.
        var access = await _scope.GetAsync(RecordModules.Leads, ct);

        var visibleRows = await _db.Leads.AsNoTracking()
            .Where(l => l.TenantId == tenantId && !l.IsDeleted)
            .VisibleTo(access)
            .GroupBy(l => l.Status)
            .Select(g => new { Status = g.Key, N = g.Count() })
            .ToListAsync(ct);

        // Built in memory with OrdinalIgnoreCase: SQL Server's default
        // collation is case-insensitive, so Lead.Status can hold a casing
        // that matches in the database and misses in a default-comparer
        // dictionary. It also survives a null Status, which
        // ToDictionaryAsync would throw on.
        var visible = Counts(visibleRows.Select(x => (x.Status, x.N)));

        // The UNSCOPED count — every live lead, whoever owns it. This is
        // what the retire and delete rules check, so it is what the
        // settings page must display and reason from. See the note at the
        // top of this file.
        var total = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (withAdminDetail)
        {
            var totalRows = await _db.Leads.AsNoTracking()
                .Where(l => l.TenantId == tenantId && !l.IsDeleted)
                .GroupBy(l => l.Status)
                .Select(g => new { Status = g.Key, N = g.Count() })
                .ToListAsync(ct);

            total = Counts(totalRows.Select(x => (x.Status, x.N)));
        }

        // Colour fallback needs to know a status's position among the OPEN
        // ones, so the palette walks the pipeline rather than jumping about.
        var openOrdinals = all
            .Where(s => s.Category == LeadStatusCategory.Open)
            .OrderBy(s => s.SortOrder)
            .Select((s, i) => new { s.Id, i })
            .ToDictionary(x => x.Id, x => x.i);

        var result = new List<LeadStatusDto>(statuses.Count);

        foreach (var s in statuses)
        {
            var visibleCount = visible.GetValueOrDefault(s.Key);
            var totalCount = withAdminDetail ? total.GetValueOrDefault(s.Key) : 0;

            var otherActiveQualified = all.Count(
                x => x.Category == LeadStatusCategory.Qualified
                  && x.IsActive && x.Id != s.Id);

            // Delete counts every qualified status, active or not — the
            // delete handler does the same, because a retired qualified
            // status can be brought back whereas a deleted one cannot.
            var otherQualified = all.Count(
                x => x.Category == LeadStatusCategory.Qualified && x.Id != s.Id);

            // Somewhere to actually move the leads: another status of the
            // same kind, active, and not the system one. Without this the
            // menu offered "Move 3 leads somewhere else…" to a tenant with
            // one disqualified status, and the dialog opened with every
            // option disabled.
            var hasMoveTarget = all.Any(
                x => x.Id != s.Id && x.IsActive && !x.IsSystem && x.Category == s.Category);

            // Without the admin detail the true counts are unknown, so
            // "can delete" is unanswerable. It comes back false with no
            // reason — deny by default. A caller that needs the answer
            // asks for it; a caller that does not must never be told "yes"
            // by an absence of evidence.
            var deleteWhyNot = withAdminDetail
                ? StatusRules.WhyNotDelete(s, totalCount, otherQualified)
                : "unknown";

            var retireWhyNot = withAdminDetail && s.IsActive
                ? StatusRules.WhyNotRetire(s, totalCount, otherActiveQualified, hasMoveTarget)
                : "unknown";

            var blockedOnlyByLeads = withAdminDetail &&
                StatusRules.BlockedOnlyByLeads(s, totalCount, otherActiveQualified, hasMoveTarget);

            result.Add(new LeadStatusDto(
                Id: s.Id,
                Key: s.Key,
                Name: s.Name,
                SortOrder: s.SortOrder,
                Score: s.Score,
                Category: s.Category,
                IsActive: s.IsActive,
                IsDefault: s.IsDefault,
                IsSystem: s.IsSystem,
                LeadCount: visibleCount,
                Color: StatusColors.Resolve(s.Color, s.Category, openOrdinals.GetValueOrDefault(s.Id)),
                Description: s.Description,
                TotalLeadCount: totalCount,
                CanDelete: withAdminDetail && deleteWhyNot is null,
                DeleteBlockedReason: withAdminDetail ? deleteWhyNot : null,
                CanRetire: withAdminDetail && s.IsActive && retireWhyNot is null,
                RetireBlockedReason: withAdminDetail && s.IsActive ? retireWhyNot : null,
                BlockedOnlyByLeads: blockedOnlyByLeads));
        }

        return result;
    }

    private static Dictionary<string, int> Counts(IEnumerable<(string? Status, int N)> rows)
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (status, n) in rows)
        {
            if (string.IsNullOrEmpty(status)) continue;
            d[status] = d.GetValueOrDefault(status) + n;
        }
        return d;
    }
}

// ── CREATE ────────────────────────────────────────────────────────────

public class CreateLeadStatusHandler : ICommandHandler
{
    private const int MaxStatuses = 15;

    private readonly FlowDbContext _db;
    private readonly ILogger<CreateLeadStatusHandler> _logger;

    public CreateLeadStatusHandler(FlowDbContext db, ILogger<CreateLeadStatusHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<LeadStatusDto> Handle(CreateLeadStatusDto dto, CancellationToken ct = default)
    {
        var name = dto.Name?.Trim() ?? "";
        if (name.Length == 0)
            throw new InvalidOperationException("Give the status a name.");
        if (name.Length > 100)
            throw new InvalidOperationException("Status names can be at most 100 characters.");

        // Converted is set by the system. A second one would mean two
        // different statuses both claiming a lead became a deal.
        if (dto.Category == LeadStatusCategory.Converted)
            throw new InvalidOperationException(
                "Converted is set automatically when a lead becomes a deal — you can rename it, but not add another.");

        var description = dto.Description?.Trim();
        if (description is { Length: > 500 })
            throw new InvalidOperationException("A status description can be at most 500 characters.");

        var existing = await _db.LeadStatusDefinitions
            .Where(s => s.TenantId == dto.TenantId)
            .ToListAsync(ct);

        if (existing.Count >= MaxStatuses)
            throw new InvalidOperationException(
                $"A lead pipeline can have at most {MaxStatuses} statuses, including retired ones. " +
                "Delete one you no longer use, or rename an existing one instead of adding another.");

        if (existing.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"You already have a status called \"{name}\".");

        var status = new LeadStatusDefinition
        {
            Id = Guid.NewGuid(),
            TenantId = dto.TenantId,
            Key = await StatusKeys.UniqueAsync(_db, dto.TenantId, name, ct),
            Name = name,

            // Provisional — the renumber below lands it at the end of the
            // WORKING group rather than after the system status. Before
            // this, a new status appeared after Converted in the Leads
            // page tabs, which is the small bug that made the reorder
            // arrows feel broken.
            SortOrder = int.MaxValue - 1,

            Score = Math.Clamp(dto.Score, 0, 15),
            Category = dto.Category,
            IsActive = true,
            IsDefault = false,
            IsSystem = false,
            Color = StatusColors.Clean(dto.Color),
            Description = string.IsNullOrWhiteSpace(description) ? null : description,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = dto.CreatedBy
        };

        _db.LeadStatusDefinitions.Add(status);

        StatusOrdering.Renumber(existing.Append(status).ToList(), null, dto.CreatedBy);

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Created lead status {Name} ({Key}) for tenant {TenantId}",
            status.Name, status.Key, dto.TenantId);

        return new LeadStatusDto(
            status.Id, status.Key, status.Name, status.SortOrder, status.Score,
            status.Category, status.IsActive, status.IsDefault, status.IsSystem, 0,
            Color: StatusColors.Resolve(status.Color, status.Category,
                existing.Count(s => s.Category == LeadStatusCategory.Open)),
            Description: status.Description);
    }
}

// ── UPDATE ────────────────────────────────────────────────────────────

public class UpdateLeadStatusDefHandler : ICommandHandler
{
    private readonly FlowDbContext _db;

    public UpdateLeadStatusDefHandler(FlowDbContext db) => _db = db;

    public async Task Handle(UpdateLeadStatusDefDto dto, CancellationToken ct = default)
    {
        var status = await _db.LeadStatusDefinitions
            .FirstOrDefaultAsync(s => s.Id == dto.StatusId && s.TenantId == dto.TenantId, ct)
            ?? throw new KeyNotFoundException("Status not found");

        var name = dto.Name?.Trim() ?? "";
        if (name.Length == 0)
            throw new InvalidOperationException("Give the status a name.");
        if (name.Length > 100)
            throw new InvalidOperationException("Status names can be at most 100 characters.");

        // Compared in memory with OrdinalIgnoreCase, exactly as Create
        // does. Translated to SQL this followed the column collation, so
        // on a case-sensitive database you could not CREATE "working"
        // beside "Working" but you could RENAME into it — two rules for
        // one question. At most fifteen rows, so the round trip is free.
        var otherNames = await _db.LeadStatusDefinitions.AsNoTracking()
            .Where(s => s.TenantId == dto.TenantId && s.Id != dto.StatusId)
            .Select(s => s.Name)
            .ToListAsync(ct);

        if (otherNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"You already have a status called \"{name}\".");

        var description = dto.Description?.Trim();
        if (description is { Length: > 500 })
            throw new InvalidOperationException("A status description can be at most 500 characters.");

        if (status.IsActive && !dto.IsActive)
        {
            // Every reason lives in one place now, shared with the read
            // path — including the pointer at the tool that fixes the
            // common one. Previously "move them first" was advice with
            // nothing behind it.
            var leadsHere = await _db.Leads.CountAsync(
                l => l.TenantId == dto.TenantId && l.Status == status.Key && !l.IsDeleted, ct);

            var otherActiveQualified = await _db.LeadStatusDefinitions.CountAsync(
                s => s.TenantId == dto.TenantId
                  && s.Category == LeadStatusCategory.Qualified
                  && s.IsActive && s.Id != dto.StatusId, ct);

            var hasMoveTarget = await _db.LeadStatusDefinitions.AnyAsync(
                s => s.TenantId == dto.TenantId
                  && s.Id != dto.StatusId
                  && s.IsActive && !s.IsSystem
                  && s.Category == status.Category, ct);

            var why = StatusRules.WhyNotRetire(status, leadsHere, otherActiveQualified, hasMoveTarget);
            if (why is not null) throw new InvalidOperationException(why);
        }

        // Key deliberately NOT recalculated — leads store it.
        status.Name = name;
        status.Score = Math.Clamp(dto.Score, 0, 15);
        status.IsActive = dto.IsActive;
        status.Color = StatusColors.Clean(dto.Color) ?? status.Color;
        status.Description = string.IsNullOrWhiteSpace(description) ? null : description;
        status.UpdatedAtUtc = DateTime.UtcNow;
        status.UpdatedBy = dto.UpdatedBy;

        await _db.SaveChangesAsync(ct);
    }
}

// ── REORDER ───────────────────────────────────────────────────────────

public class ReorderLeadStatusesHandler : ICommandHandler
{
    private readonly FlowDbContext _db;

    public ReorderLeadStatusesHandler(FlowDbContext db) => _db = db;

    public async Task Handle(ReorderLeadStatusesDto dto, CancellationToken ct = default)
    {
        var all = await _db.LeadStatusDefinitions
            .Where(s => s.TenantId == dto.TenantId)
            .ToListAsync(ct);

        if (all.Count == 0) return;

        // 025: the system status is forced last, always. The caller's
        // order is a hint applied WITHIN the working group. See the note
        // on StatusOrdering.Renumber for what the old trust-the-caller
        // version silently did to the Leads page tabs.
        StatusOrdering.Renumber(all, dto.OrderedIds, dto.UpdatedBy);

        await _db.SaveChangesAsync(ct);
    }
}

// ── SET DEFAULT ───────────────────────────────────────────────────────

public class SetDefaultLeadStatusHandler : ICommandHandler
{
    private readonly FlowDbContext _db;

    public SetDefaultLeadStatusHandler(FlowDbContext db) => _db = db;

    public async Task Handle(Guid tenantId, Guid statusId, string? updatedBy, CancellationToken ct = default)
    {
        var all = await _db.LeadStatusDefinitions.Where(s => s.TenantId == tenantId).ToListAsync(ct);

        var target = all.FirstOrDefault(s => s.Id == statusId)
            ?? throw new KeyNotFoundException("Status not found");

        if (!target.IsActive)
            throw new InvalidOperationException("A retired status can't be where new leads start.");

        if (target.IsSystem)
            throw new InvalidOperationException(
                "This status is set automatically when a lead converts — a new lead can't start there.");

        if (target.Category != LeadStatusCategory.Open)
            throw new InvalidOperationException(
                "New leads must start in an open status, not a qualified or disqualified one.");

        foreach (var s in all)
        {
            if (s.IsDefault == (s.Id == statusId)) continue;

            s.IsDefault = s.Id == statusId;
            s.UpdatedAtUtc = DateTime.UtcNow;
            s.UpdatedBy = updatedBy;
        }

        await _db.SaveChangesAsync(ct);
    }
}

// ── MOVE LEADS (025) ──────────────────────────────────────────────────

/// <summary>
/// Empties a status by sending every lead in it somewhere else, and
/// optionally retires the status afterwards.
///
/// WHY THIS EXISTS
///   Both the retire path and the delete path refused a status holding
///   leads with the words "move them first". Nothing in the product moved
///   them. The only route was opening each lead and changing its status
///   by hand.
///
/// WHY THERE IS NO STATUS HISTORY TABLE
///   There is no LeadStatusHistory, by an earlier and correct decision —
///   lead status changes are recorded in AuditLogs, and GetTimelineHandler
///   reads them from there to build the lead's timeline. So this writes
///   the SAME audit row UpdateLeadStatusHandler writes, in the same shape:
///
///       new { from = <old NAME>, to = <new NAME> }
///
///   Names, not keys — the timeline reads those two properties straight
///   out of the JSON and renders them. Writing keys would give every
///   moved lead a timeline entry reading "Status: — → —". The extra
///   `bulk` flag is ignored by the timeline and makes these findable in
///   one query later.
///
/// WHY SCORES ARE RECALCULATED ONE AT A TIME
///   LeadScoringService.Calculate adds the status points to profile and
///   engagement points and then caps the total at 100. A lead already at
///   the ceiling cannot have its old status contribution subtracted back
///   out, so a delta is not safe. RecalculateAsync is the single source
///   of truth and is called per lead — which is why the cap below is
///   lower than the pipeline one.
///
/// WHY THE CATEGORY MUST MATCH
///   Open → Qualified in bulk would mark a batch of leads ready to
///   convert without anyone vetting them, and Qualified → Disqualified
///   would write off a batch nobody looked at. Both are decisions about
///   one lead. Merging two open statuses, or two disqualified ones, is
///   just tidying and is allowed.
/// </summary>
public class MoveStatusLeadsHandler : ICommandHandler
{
    /// <summary>
    /// Lower than the pipeline's 2,000 on purpose. Each lead costs an
    /// audit write AND a full score recalculation — three queries and a
    /// save apiece — so this is a couple of hundred round trips, not one
    /// statement. A tenant past this wants a filtered bulk edit on the
    /// leads list, not a settings page holding a web request open.
    /// </summary>
    private const int MaxLeadsInOneMove = 200;

    private readonly FlowDbContext _db;
    private readonly ILeadScoringService _scoring;
    private readonly IAuditService _audit;
    private readonly ILogger<MoveStatusLeadsHandler> _logger;

    public MoveStatusLeadsHandler(
        FlowDbContext db,
        ILeadScoringService scoring,
        IAuditService audit,
        ILogger<MoveStatusLeadsHandler> logger)
    {
        _db = db;
        _scoring = scoring;
        _audit = audit;
        _logger = logger;
    }

    public async Task<MoveStatusLeadsResult> Handle(
        MoveStatusLeadsDto dto, CancellationToken ct = default)
    {
        if (dto.FromStatusId == dto.ToStatusId)
            throw new InvalidOperationException("Pick a different status to move the leads into.");

        var all = await _db.LeadStatusDefinitions
            .Where(s => s.TenantId == dto.TenantId)
            .ToListAsync(ct);

        var from = all.FirstOrDefault(s => s.Id == dto.FromStatusId)
            ?? throw new KeyNotFoundException("Status not found");

        var to = all.FirstOrDefault(s => s.Id == dto.ToStatusId)
            ?? throw new KeyNotFoundException("The status you picked no longer exists.");

        if (from.IsSystem || to.IsSystem)
            throw new InvalidOperationException(
                "Converted is set automatically when a lead becomes a deal, so leads can't be moved into or out of it here.");

        if (!to.IsActive)
            throw new InvalidOperationException(
                $"\"{to.Name}\" is retired, so leads can't be moved into it. Bring it back first, or pick another status.");

        if (from.Category != to.Category)
            throw new InvalidOperationException(
                $"\"{from.Name}\" and \"{to.Name}\" aren't the same kind of status. " +
                "Leads can only be moved in bulk between statuses of the same type — qualifying or " +
                "disqualifying a lead is a decision about that lead, so it has to happen on the lead itself.");

        // The cap is checked with a COUNT, before anything is materialised.
        // Loading 80,000 tracked entities and then refusing is a way to run
        // a settings page out of memory to print a message.
        var leadCount = await _db.Leads.CountAsync(
            l => l.TenantId == dto.TenantId && l.Status == from.Key && !l.IsDeleted, ct);

        if (leadCount > MaxLeadsInOneMove)
            throw new InvalidOperationException(
                $"\"{from.Name}\" holds {leadCount:N0} leads, which is more than this page will move at once " +
                $"(the limit is {MaxLeadsInOneMove:N0}). Move them from the leads list in batches instead.");

        // ── EVERY REFUSAL HAPPENS BEFORE ANYTHING CHANGES ─────────────
        // The retire check used to sit after the leads had been staged, so
        // a refusal there threw away a move that was perfectly legal — and
        // the dialog ships with "retire it once it's empty" TICKED, which
        // made that the common path rather than an edge case. Someone
        // asking for two things got zero.
        var willRetire = dto.ThenRetire && from.IsActive;

        if (willRetire)
        {
            var otherActiveQualified = all.Count(
                s => s.Category == LeadStatusCategory.Qualified && s.IsActive && s.Id != from.Id);

            // leadCount 0 and hasMoveTarget true: by the time the retire
            // applies, the leads are somewhere else in the same
            // SaveChanges, and `to` is the target we already validated.
            var why = StatusRules.WhyNotRetire(from, 0, otherActiveQualified, hasMoveTarget: true);
            if (why is not null) throw new InvalidOperationException(why);
        }

        var leads = await _db.Leads
            .Where(l => l.TenantId == dto.TenantId && l.Status == from.Key && !l.IsDeleted)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;

        foreach (var lead in leads)
        {
            lead.Status = to.Key;
            lead.UpdatedAtUtc = now;
            lead.UpdatedBy = dto.MovedBy;
        }

        if (willRetire)
        {
            from.IsActive = false;
            from.UpdatedAtUtc = now;
            from.UpdatedBy = dto.MovedBy;
        }

        // One SaveChanges, so one transaction: either every lead moves and
        // the status retires, or nothing happens. A half-emptied status
        // that also got retired would strand whatever was left.
        await _db.SaveChangesAsync(ct);

        // ── After the move is committed ───────────────────────────────
        // THE MOVE IS NOW A FACT. Nothing below may throw, for the same
        // reason the transition actions in 022 never throw: an exception
        // here would surface as "Couldn't move those leads" on a page
        // whose leads HAVE moved and whose status IS retired — and the
        // retry would be a no-op, because the status now holds nothing.
        // The person would be told it failed, see that it hadn't, try
        // again, and be told it moved nothing.
        //
        // So each lead's bookkeeping runs in its own try/catch and
        // failures come back as messages the page can show alongside the
        // success.
        var problems = new List<string>();

        foreach (var lead in leads)
        {
            try
            {
                await _audit.WriteAsync(
                    AuditAction.LeadStatusChanged, AuditEntityType.Lead, lead.Id, dto.TenantId,
                    new
                    {
                        // NAMES, not keys. GetTimelineHandler parses
                        // exactly these two properties out of the JSON to
                        // render "Status: Working → Qualified" on the
                        // lead's timeline. Keys here would produce
                        // "Status: — → —" on every moved lead.
                        from = from.Name,
                        to = to.Name,

                        // Ignored by the timeline; makes every lead moved
                        // by a reorganisation findable in one query.
                        bulk = true
                    }, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Lead {LeadId} moved from {From} to {To}, but its timeline entry could not be written",
                    lead.Id, from.Key, to.Key);

                problems.Add($"\"{lead.FullName}\" moved, but the change could not be added to its timeline.");
            }

            try
            {
                // Status points feed the lead score, so a status worth a
                // different number of points changes it. Per lead because
                // the score is capped at 100 and a delta cannot be
                // reversed out of a lead already at the ceiling.
                //
                // RecalculateAsync swallows its own exceptions by design,
                // so this catch is belt and braces.
                await _scoring.RecalculateAsync(lead.Id, dto.TenantId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Lead {LeadId} moved, but its score could not be recalculated", lead.Id);

                problems.Add($"\"{lead.FullName}\" moved, but its lead score is out of date. " +
                             "It corrects itself the next time the lead is touched.");
            }
        }

        _logger.LogInformation(
            "Moved {Count} lead(s) from {From} to {To} for tenant {TenantId} (retired: {Retired}, problems: {Problems})",
            leads.Count, from.Key, to.Key, dto.TenantId, willRetire, problems.Count);

        return new MoveStatusLeadsResult(leads.Count, from.Name, to.Name, willRetire, problems);
    }
}

// ── DELETE ────────────────────────────────────────────────────────────

public class DeleteLeadStatusHandler : ICommandHandler
{
    private readonly FlowDbContext _db;

    public DeleteLeadStatusHandler(FlowDbContext db) => _db = db;

    public async Task Handle(Guid tenantId, Guid statusId, CancellationToken ct = default)
    {
        var status = await _db.LeadStatusDefinitions
            .FirstOrDefaultAsync(s => s.Id == statusId && s.TenantId == tenantId, ct)
            ?? throw new KeyNotFoundException("Status not found");

        var leadsHere = await _db.Leads.CountAsync(
            l => l.TenantId == tenantId && l.Status == status.Key && !l.IsDeleted, ct);

        var otherQualified = await _db.LeadStatusDefinitions.CountAsync(
            s => s.TenantId == tenantId
              && s.Category == LeadStatusCategory.Qualified
              && s.Id != statusId, ct);

        var why = StatusRules.WhyNotDelete(status, leadsHere, otherQualified);
        if (why is not null) throw new InvalidOperationException(why);

        // UNLIKE A PIPELINE STAGE, a lead status CAN be deleted after its
        // leads have been moved out. Deal history stores the stage KEY, so
        // deleting a stage leaves those rows pointing at a name that no
        // longer resolves — hence the "deals have passed through this
        // stage before" refusal there. Lead status history lives in
        // AuditLogs and stores the NAME, already baked into the JSON, so
        // nothing on a lead's timeline breaks when the status goes.
        _db.LeadStatusDefinitions.Remove(status);
        await _db.SaveChangesAsync(ct);
    }
}
