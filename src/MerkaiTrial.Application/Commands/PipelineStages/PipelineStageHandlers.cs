// =====================================================================
// PipelineStageHandlers.cs
// Location: MerkaiTrial.Application/Commands/PipelineStages/
//
// COMPLETE FILE — replaces the existing one.
//
// CHANGES (023 — pipeline stage care)
//
//   ✅ MOVE DEALS OUT OF A STAGE. The headline. Before this round, both
//      the retire path and the delete path refused a stage holding deals
//      with "move them first" — and the product gave you no way to move
//      them except one deal at a time through the transition bar. A
//      pipeline with 50 deals in Negotiation could not be reshaped at
//      all. MoveStageDealsHandler does it in one transaction, writing a
//      DealStageHistory row per deal so no timeline is left lying.
//
//   ✅ THE DTO NOW CARRIES THE REASONS. The page used to decide whether
//      to show a Delete button from DealCount and IsDefault, while the
//      handler ALSO refused on stage history. So a stage that once held
//      deals and is now empty showed a Delete button, asked you to
//      confirm, and then refused. CanDelete / DeleteBlockedReason and
//      CanRetire / RetireBlockedReason are computed here, from exactly
//      the checks the write handlers enforce, so the UI can never
//      promise something the server will not do.
//
//   ✅ WAYS IN / WAYS OUT. A stage with no active way in is invisible to
//      the tenant's users and completely undetectable from the stage
//      list. Now it is a number on the row.
//
//   ✅ REORDER CAN NO LONGER INTERLEAVE WORKING AND CLOSING STAGES.
//      Previously "down" on the last working stage swapped it past a
//      closing stage in SortOrder. The settings page hid this because it
//      groups by category when rendering — but this handler copies
//      SortOrder onto the transitions, so the buttons on the deal page
//      silently reordered and "Mark as Won" started appearing before
//      "Move to Negotiation". Renumber() now sorts by category first,
//      always, whatever order it is handed.
//
//   ✅ Colour and description on create and update.
//
// CHANGES (020 — Blueprint transitions, unchanged)
//   ✅ CREATE wires the new stage into the process.
//   ✅ RETIRE switches off the transitions INTO the stage and leaves the
//      ones OUT of it alone, so deals parked there can still be moved.
//   ✅ DELETE clears the stage's transitions first.
//
// THE RULES, ENFORCED HERE RATHER THAN IN THE UI
//   • Key is generated once from the name and never changes. Renaming a
//     stage must not move the deals sitting in it.
//   • A tenant must always have at least one Won and one Lost stage, or
//     no deal can ever be closed.
//   • Exactly one default stage, or new deals have nowhere to start.
//   • A stage holding deals cannot be deleted — only emptied and then
//     retired. A hard delete would orphan them, and the FK would refuse
//     anyway.
//   • Working stages always sort before closing stages.
// =====================================================================

using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text;

namespace MerkaiTrial.Application.Commands.PipelineStages;

// ── DTOs ──────────────────────────────────────────────────────────────

/// <summary>
/// Everything the settings page needs about one stage, including WHY it
/// cannot do something. The new members are optional parameters with
/// defaults so that any existing positional construction of this record
/// elsewhere in the solution still compiles.
/// </summary>
/// <param name="DealCount">How many live deals sit in this stage.</param>
/// <param name="Color">Always a real "#RRGGBB" — resolved server-side, never null.</param>
/// <param name="Description">What has to be true for a deal to sit here. Optional.</param>
/// <param name="WaysIn">Active transitions ending here. Zero means no user can reach it.</param>
/// <param name="WaysOut">Active transitions leaving here. Zero means a deal that lands here can never move on.</param>
/// <param name="DeleteBlockedReason">Why not, in words a tenant admin can act on. Null when CanDelete is true.</param>
/// <param name="BlockedOnlyByDeals">
/// True when the only thing standing between this stage and a retirement
/// is the deals sitting in it — the one case the page can offer to fix,
/// rather than showing a dead end.
/// </param>
public record PipelineStageDto(
    Guid Id,
    string Key,
    string Name,
    int SortOrder,
    int Probability,
    StageCategory Category,
    bool IsActive,
    bool IsDefault,
    int DealCount,

    // ── 023 ──────────────────────────────────────────────────────────
    // Optional with defaults so that any existing positional
    // construction of this record elsewhere in the solution keeps
    // compiling. Doc comments go on the record declaration above: inside
    // a positional parameter list they are CS1587, which becomes a build
    // break the day someone turns on TreatWarningsAsErrors.
    string Color = "#6366F1",
    string? Description = null,
    int WaysIn = 0,
    int WaysOut = 0,
    bool CanDelete = false,
    string? DeleteBlockedReason = null,
    bool CanRetire = false,
    string? RetireBlockedReason = null,
    bool BlockedOnlyByDeals = false);

public record CreatePipelineStageDto(
    Guid TenantId,
    string Name,
    int Probability,
    StageCategory Category,
    string? CreatedBy = null,
    string? Color = null,
    string? Description = null);

public record UpdatePipelineStageDto(
    Guid TenantId,
    Guid StageId,
    string Name,
    int Probability,
    bool IsActive,
    string? UpdatedBy = null,
    string? Color = null,
    string? Description = null);

/// <param name="OrderedIds">
/// Stage ids in the order they should appear. Ids left out keep their
/// relative order, after the ones listed.
/// </param>
public record ReorderPipelineStagesDto(
    Guid TenantId,
    List<Guid> OrderedIds,
    string? UpdatedBy = null);

/// <summary>
/// Empty a stage so it can be retired. See MoveStageDealsHandler for why
/// there is no "and then delete" option.
/// </summary>
public record MoveStageDealsDto(
    Guid TenantId,
    Guid FromStageId,
    Guid ToStageId,
    string? MovedBy = null,
    bool ThenRetire = false);

public record MoveStageDealsResult(
    int Moved,
    string FromName,
    string ToName,
    bool Retired);

// ── SHARED ────────────────────────────────────────────────────────────

internal static class StageKeys
{
    /// <summary>
    /// "Site Visit" -> "SiteVisit". Letters and digits only, so the key
    /// is safe in a URL, a constraint and a Thai-named stage alike — a
    /// stage called "ตรวจหน้างาน" still needs a usable key.
    /// </summary>
    public static string FromName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name)
            if (char.IsLetterOrDigit(c) && c < 128) sb.Append(c);

        var key = sb.ToString();

        // Non-Latin names produce nothing usable — fall back to something
        // stable rather than an empty key.
        return key.Length == 0 ? "Stage" + DateTime.UtcNow.Ticks.ToString()[^6..] : key;
    }

    public static async Task<string> UniqueAsync(
        FlowDbContext db, Guid tenantId, string name, CancellationToken ct)
    {
        var baseKey = FromName(name);
        var key = baseKey;
        var n = 2;

        while (await db.PipelineStages.AnyAsync(s => s.TenantId == tenantId && s.Key == key, ct))
            key = baseKey + n++;

        return key;
    }
}

internal static class StageOrdering
{
    /// <summary>
    /// Assigns SortOrder 1..n across the whole tenant, working stages
    /// first, then Won, then Lost — which is exactly (int)Category.
    ///
    /// EVERY path that changes order goes through here. The invariant is
    /// not "the page sends a sensible order"; it is "no order the page
    /// can send produces a bad result". The old code trusted the caller
    /// and a single misplaced down-arrow silently reordered the buttons
    /// on every deal page.
    /// </summary>
    /// <param name="preferred">
    /// Optional ranking hint. Stages named here sort in that order within
    /// their own category; stages not named keep their existing relative
    /// order, after the named ones.
    /// </param>
    public static void Renumber(List<PipelineStage> stages, List<Guid>? preferred, string? by)
    {
        var rank = new Dictionary<Guid, int>();
        if (preferred is not null)
            for (var i = 0; i < preferred.Count; i++)
                rank[preferred[i]] = i;

        var ordered = stages
            .OrderBy(s => (int)s.Category)
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

internal static class StageRetirement
{
    /// <summary>
    /// Stop offering a stage as a destination, WITHOUT touching the ways
    /// out of it.
    ///
    /// The asymmetry is deliberate and is the whole reason this is a
    /// shared helper rather than two copies. A deal parked in a retired
    /// stage — or moved there before the retirement — must still have a
    /// way forward. Switching off the transitions out of it is exactly
    /// how buttons-only navigation strands a deal with no route anywhere.
    /// </summary>
    public static async Task SwitchOffWaysInAsync(
        FlowDbContext db, Guid tenantId, string stageKey, string? by, CancellationToken ct)
    {
        var into = await db.ProcessTransitions
            .Where(t => t.TenantId == tenantId && t.ToStageKey == stageKey && t.IsActive)
            .ToListAsync(ct);

        foreach (var t in into)
        {
            t.IsActive = false;
            t.UpdatedAtUtc = DateTime.UtcNow;
            t.UpdatedBy = by;
        }
    }

    public static async Task SwitchOnWaysInAsync(
        FlowDbContext db, Guid tenantId, string stageKey, string? by, CancellationToken ct)
    {
        var into = await db.ProcessTransitions
            .Where(t => t.TenantId == tenantId && t.ToStageKey == stageKey && !t.IsActive)
            .ToListAsync(ct);

        foreach (var t in into)
        {
            t.IsActive = true;
            t.UpdatedAtUtc = DateTime.UtcNow;
            t.UpdatedBy = by;
        }
    }

    /// <summary>
    /// The reasons an ACTIVE stage cannot be retired, in the order a
    /// person can act on them. Null means it can.
    ///
    /// CALLERS MUST CHECK IsActive THEMSELVES. This used to open with
    /// "if (!stage.IsActive) return null" meaning "already retired,
    /// nothing blocks" — which the read path then reported to the API as
    /// CanRetire = true on every retired stage. Null has to mean one
    /// thing, so the not-applicable case belongs to the caller.
    ///
    /// Shared by the read handler (to disable the menu item and explain)
    /// and the write handler (to refuse), so the two cannot disagree —
    /// including the "…and here is what fixes it" suffix, which used to
    /// be added only on the write side.
    /// </summary>
    public static string? WhyNotRetire(
        PipelineStage stage, int dealCount, int otherActiveInCategory)
    {
        if (stage.IsDefault)
            return "New deals start here. Make another stage the starting point first.";

        if (stage.Category != StageCategory.Open && otherActiveInCategory == 0)
            return $"This is your only {Label(stage.Category)} stage. " +
                   $"Without it, no deal could ever be marked as {Label(stage.Category).ToLowerInvariant()}.";

        if (dealCount > 0)
            return $"{dealCount} {Deals(dealCount)} still here. " +
                   "Use \"Move deals somewhere else\" on this menu to send them on first.";

        return null;
    }

    /// <summary>
    /// True when moving the deals out is all that stands between this
    /// stage and a retirement — the one case the page can offer to fix.
    /// Computed here so the menu item and the refusal message agree about
    /// which stages it applies to.
    /// </summary>
    public static bool BlockedOnlyByDeals(
        PipelineStage stage, int dealCount, int otherActiveInCategory)
        => stage.IsActive
           && dealCount > 0
           && !stage.IsDefault
           && !(stage.Category != StageCategory.Open && otherActiveInCategory == 0);

    public static string? WhyNotDelete(
        PipelineStage stage, int dealCount, bool everUsed, int otherActiveInCategory)
    {
        // Same order as DeletePipelineStageHandler checks them, so the
        // message on the disabled menu item is the message you would have
        // got from pressing it.
        if (dealCount > 0)
            return $"{dealCount} {Deals(dealCount)} in this stage.";

        if (everUsed)
            return "Deals have passed through this stage before, so their history still needs it. Retire it instead.";

        if (stage.IsDefault)
            return "New deals start here. Make another stage the starting point first.";

        // The invariant the retire path has always protected and the
        // delete path never did. On a trial workspace the seeded Closed
        // Won and Closed Lost stages have no deals and no history yet, so
        // without this a tenant admin could delete Closed Won on day one
        // and then find no deal could ever be marked as sold.
        if (stage.Category != StageCategory.Open && otherActiveInCategory == 0)
            return $"This is your only {Label(stage.Category)} stage. " +
                   $"Without it, no deal could ever be marked as {Label(stage.Category).ToLowerInvariant()}.";

        return null;
    }

    public static string Label(StageCategory c) => c switch
    {
        StageCategory.Won  => "Won",
        StageCategory.Lost => "Lost",
        _ => "Open"
    };

    public static string Deals(int n) => n == 1 ? "deal is" : "deals are";
}

// ── READ ──────────────────────────────────────────────────────────────

public class GetPipelineStagesHandler : ICommandHandler
{
    private readonly FlowDbContext _db;

    public GetPipelineStagesHandler(FlowDbContext db) => _db = db;

    /// <param name="activeOnly">
    /// True for pickers — a retired stage should not be offered for new
    /// work. False for settings and for resolving the stage of an
    /// existing deal, which may sit in a retired one.
    /// </param>
    /// <param name="withAdminDetail">
    /// The ways-in/ways-out counts and the delete/retire reasons. Only
    /// the settings page needs them, and working them out costs two extra
    /// round trips.
    ///
    /// THIS ENDPOINT IS HOT. Every deal page, every kanban board and
    /// every quote screen reads the stage list to render a picker. Making
    /// all of them pay for numbers only one settings page displays is how
    /// a helpful addition turns into a performance regression nobody
    /// connects back to it. Off by default, on for one caller.
    /// </param>
    public async Task<List<PipelineStageDto>> Handle(
        Guid tenantId,
        bool activeOnly = false,
        bool withAdminDetail = false,
        CancellationToken ct = default)
    {
        var q = _db.PipelineStages.AsNoTracking().Where(s => s.TenantId == tenantId);
        if (activeOnly) q = q.Where(s => s.IsActive);

        var stages = await q.OrderBy(s => s.SortOrder).ToListAsync(ct);
        if (stages.Count == 0) return new List<PipelineStageDto>();

        // One grouped query rather than a count per stage.
        //
        // Materialised first and then keyed OrdinalIgnoreCase on purpose.
        // SQL Server's default collation is case-insensitive, so
        // Deal.Stage can hold "closedwon" against a key of "ClosedWon"
        // and the database's own count — which is what the write handlers
        // use — would find it while a case-sensitive dictionary here said
        // "no deals". That is a read/write split on the exact number the
        // Delete button depends on. Building it in memory also survives a
        // null Stage, which ToDictionaryAsync would throw on.
        var countRows = await _db.Deals.AsNoTracking()
            .Where(d => d.TenantId == tenantId && !d.IsDeleted)
            .GroupBy(d => d.Stage)
            .Select(g => new { Stage = g.Key, N = g.Count() })
            .ToListAsync(ct);

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in countRows)
        {
            if (string.IsNullOrEmpty(row.Stage)) continue;
            counts[row.Stage] = counts.GetValueOrDefault(row.Stage) + row.N;
        }

        var everUsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var waysIn   = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var waysOut  = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (withAdminDetail)
        {
            // Which stages a deal has ever been in or out of. This is the
            // check the page never knew about, which is why it used to
            // offer a Delete button the server then refused. DISTINCT on
            // a pair of stage keys, so the row count is bounded by the
            // number of stages squared, not by the number of moves ever
            // made.
            //
            // THE NULL GUARD IS LOAD-BEARING. DealStageHistory.FromStage
            // and .ToStage are declared non-nullable in the entity, so EF
            // reads them with SqlDataReader.GetString — which throws
            // SqlNullValueException the instant it meets a NULL. The
            // columns ARE nullable in the database and real rows hold
            // NULL (a deal's first history row has no stage it came
            // from), so this is the first query in the application ever
            // to materialise them as strings and the first to fall over.
            // The delete check uses AnyAsync, a server-side predicate,
            // which never reads a value and so never noticed.
            //
            // Written as a CASE in SQL rather than filtered in C# so the
            // NULL never reaches the reader at all. 023a normalises the
            // stored rows; this keeps the page standing whether or not
            // that has been run, and whatever writes a NULL there next.
            var used = await _db.DealStageHistory.AsNoTracking()
                .Where(h => h.TenantId == tenantId)
                .Select(h => new
                {
                    From = h.FromStage == null ? string.Empty : h.FromStage,
                    To   = h.ToStage   == null ? string.Empty : h.ToStage
                })
                .Distinct()
                .ToListAsync(ct);

            foreach (var u in used)
            {
                if (u.From.Length > 0) everUsed.Add(u.From);
                if (u.To.Length > 0)   everUsed.Add(u.To);
            }

            // Ways in and ways out, counting only transitions a user could
            // actually take today. One round trip and grouped in memory —
            // a tenant is capped at 20 stages, so this is at most a few
            // hundred rows and two GROUP BYs are not worth the second
            // query.
            var links = await _db.ProcessTransitions.AsNoTracking()
                .Where(t => t.TenantId == tenantId && t.IsActive)
                .Select(t => new { t.FromStageKey, t.ToStageKey })
                .ToListAsync(ct);

            foreach (var l in links)
            {
                waysOut[l.FromStageKey] = waysOut.GetValueOrDefault(l.FromStageKey) + 1;
                waysIn[l.ToStageKey]    = waysIn.GetValueOrDefault(l.ToStageKey) + 1;
            }
        }

        // Colour fallback needs to know a stage's position among the
        // OPEN stages, so the palette walks the pipeline rather than
        // jumping about.
        var openOrdinals = stages
            .Where(s => s.Category == StageCategory.Open)
            .OrderBy(s => s.SortOrder)
            .Select((s, i) => new { s.Id, i })
            .ToDictionary(x => x.Id, x => x.i);

        var result = new List<PipelineStageDto>(stages.Count);

        foreach (var s in stages)
        {
            var dealCount = counts.GetValueOrDefault(s.Key);

            var otherActiveInCategory = stages.Count(
                x => x.Category == s.Category && x.IsActive && x.Id != s.Id);

            // Without the admin detail the history is unknown, so "can
            // delete" is unanswerable. It comes back false with no reason
            // — deny by default. A caller that needs the answer asks for
            // it; a caller that does not must never be told "yes" by an
            // absence of evidence.
            //
            // A RETIRED stage is not "retirable" either: WhyNotRetire
            // answers for active stages only, and reporting CanRetire on
            // something already retired is how the read path and the
            // write path start disagreeing.
            var deleteWhyNot = withAdminDetail
                ? StageRetirement.WhyNotDelete(s, dealCount, everUsed.Contains(s.Key), otherActiveInCategory)
                : "unknown";

            var retireWhyNot = withAdminDetail && s.IsActive
                ? StageRetirement.WhyNotRetire(s, dealCount, otherActiveInCategory)
                : "unknown";

            // "Deals are the only thing in the way" — the case the page
            // can now offer to fix, rather than showing a dead end.
            var blockedOnlyByDeals = withAdminDetail &&
                StageRetirement.BlockedOnlyByDeals(s, dealCount, otherActiveInCategory);

            result.Add(new PipelineStageDto(
                Id: s.Id,
                Key: s.Key,
                Name: s.Name,
                SortOrder: s.SortOrder,
                Probability: s.Probability,
                Category: s.Category,
                IsActive: s.IsActive,
                IsDefault: s.IsDefault,
                DealCount: dealCount,
                Color: StageColors.Resolve(s.Color, s.Category, openOrdinals.GetValueOrDefault(s.Id)),
                Description: s.Description,
                WaysIn: waysIn.GetValueOrDefault(s.Key),
                WaysOut: waysOut.GetValueOrDefault(s.Key),
                CanDelete: withAdminDetail && deleteWhyNot is null,
                DeleteBlockedReason: withAdminDetail ? deleteWhyNot : null,
                CanRetire: withAdminDetail && s.IsActive && retireWhyNot is null,
                RetireBlockedReason: withAdminDetail && s.IsActive ? retireWhyNot : null,
                BlockedOnlyByDeals: blockedOnlyByDeals));
        }

        return result;
    }
}

// ── CREATE ────────────────────────────────────────────────────────────

public class CreatePipelineStageHandler : ICommandHandler
{
    private const int MaxStages = 20;

    private readonly FlowDbContext _db;
    private readonly ILogger<CreatePipelineStageHandler> _logger;

    public CreatePipelineStageHandler(FlowDbContext db, ILogger<CreatePipelineStageHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<PipelineStageDto> Handle(CreatePipelineStageDto dto, CancellationToken ct = default)
    {
        var name = dto.Name?.Trim() ?? "";
        if (name.Length == 0)
            throw new InvalidOperationException("Give the stage a name.");
        if (name.Length > 100)
            throw new InvalidOperationException("Stage names can be at most 100 characters.");

        var existing = await _db.PipelineStages
            .Where(s => s.TenantId == dto.TenantId)
            .ToListAsync(ct);

        if (existing.Count >= MaxStages)
            throw new InvalidOperationException(
                $"A pipeline can have at most {MaxStages} stages. Retire one you no longer use.");

        if (existing.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"You already have a stage called \"{name}\".");

        var probability = Math.Clamp(dto.Probability, 0, 100);

        var description = dto.Description?.Trim();
        if (description is { Length: > 500 })
            throw new InvalidOperationException("A stage description can be at most 500 characters.");

        var stage = new PipelineStage
        {
            Id = Guid.NewGuid(),
            TenantId = dto.TenantId,
            Key = await StageKeys.UniqueAsync(_db, dto.TenantId, name, ct),
            Name = name,

            // Provisional — the renumber below lands it at the end of its
            // OWN category rather than at the end of the whole list. A new
            // working stage that appeared after "Closed Lost" was the
            // small bug that made the reorder arrows feel broken.
            SortOrder = int.MaxValue - 1,

            Probability = probability,
            Category = dto.Category,
            IsActive = true,
            IsDefault = existing.Count == 0,   // first stage ever is the default
            Color = StageColors.Clean(dto.Color),
            Description = string.IsNullOrWhiteSpace(description) ? null : description,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = dto.CreatedBy
        };

        _db.PipelineStages.Add(stage);

        // ── 020: wire it into the process ─────────────────────────────
        // A stage with no transitions is a stage no deal can enter or
        // leave. The tenant would see an empty column that never fills and
        // deals that refuse to move, with nothing on screen explaining
        // why. Created switched ON: an extra button is a tidiness problem
        // a tenant fixes in a minute, an unreachable stage is a support
        // call.
        var wired = WireIntoProcess(stage, existing, dto.CreatedBy);

        // ── 023 ───────────────────────────────────────────────────────
        StageOrdering.Renumber(existing.Append(stage).ToList(), null, dto.CreatedBy);

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Created pipeline stage {Name} ({Key}) for tenant {TenantId} with {Count} transitions",
            stage.Name, stage.Key, dto.TenantId, wired);

        var activeBefore = existing.Count(s => s.IsActive);

        return new PipelineStageDto(
            stage.Id, stage.Key, stage.Name, stage.SortOrder, stage.Probability,
            stage.Category, stage.IsActive, stage.IsDefault, 0,
            Color: StageColors.Resolve(stage.Color, stage.Category, existing.Count(s => s.Category == StageCategory.Open)),
            Description: stage.Description,

            // WireIntoProcess just linked this stage both ways with every
            // active stage there was. Reporting zero here would have any
            // caller rendering this response show the red "nothing leads
            // into this stage" warning on a stage that is fully wired.
            WaysIn: activeBefore,
            WaysOut: activeBefore);
    }

    /// <summary>
    /// Links the new stage both ways with every active stage the tenant
    /// already has. Returns how many rows were added.
    /// </summary>
    private int WireIntoProcess(PipelineStage created, List<PipelineStage> existing, string? by)
    {
        var count = 0;

        foreach (var other in existing.Where(s => s.IsActive))
        {
            // into the new stage
            _db.ProcessTransitions.Add(NewLink(other, created, by));
            count++;

            // and back out of it
            _db.ProcessTransitions.Add(NewLink(created, other, by));
            count++;
        }

        return count;
    }

    private static ProcessTransition NewLink(PipelineStage from, PipelineStage to, string? by)
    {
        var isReopen = from.IsTerminal && to.Category == StageCategory.Open;

        return new ProcessTransition
        {
            Id = Guid.NewGuid(),
            TenantId = from.TenantId,
            FromStageKey = from.Key,
            ToStageKey = to.Key,
            Label = TransitionLabels.For(from.Category, to.Category, to.Name),
            SortOrder = to.SortOrder,
            IsActive = true,

            // Same shape the migration gives a reopen: managers, with a
            // reason. Everything else is open to whoever can update deals.
            Actor = isReopen ? TransitionActor.TeamManagers : TransitionActor.Anyone,
            RequiresNote = isReopen || to.Category == StageCategory.Lost,
            NotePrompt = isReopen
                ? TransitionLabels.ReopenPrompt
                : to.Category == StageCategory.Lost ? TransitionLabels.LostPrompt : null,

            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = by
        };
    }
}

// ── UPDATE ────────────────────────────────────────────────────────────

public class UpdatePipelineStageHandler : ICommandHandler
{
    private readonly FlowDbContext _db;

    public UpdatePipelineStageHandler(FlowDbContext db) => _db = db;

    public async Task Handle(UpdatePipelineStageDto dto, CancellationToken ct = default)
    {
        var stage = await _db.PipelineStages
            .FirstOrDefaultAsync(s => s.Id == dto.StageId && s.TenantId == dto.TenantId, ct)
            ?? throw new KeyNotFoundException("Stage not found");

        var name = dto.Name?.Trim() ?? "";
        if (name.Length == 0)
            throw new InvalidOperationException("Give the stage a name.");
        if (name.Length > 100)
            throw new InvalidOperationException("Stage names can be at most 100 characters.");

        var clash = await _db.PipelineStages.AnyAsync(
            s => s.TenantId == dto.TenantId && s.Id != dto.StageId && s.Name == name, ct);
        if (clash)
            throw new InvalidOperationException($"You already have a stage called \"{name}\".");

        var description = dto.Description?.Trim();
        if (description is { Length: > 500 })
            throw new InvalidOperationException("A stage description can be at most 500 characters.");

        var wasActive = stage.IsActive;

        // Retiring a stage: check it would not leave the pipeline unable
        // to close a deal, and refuse while deals are sitting in it.
        //
        // 023: the message now points at the tool that fixes it. Before
        // this round "move them first" was advice with nothing behind it —
        // the only way to move a deal was one at a time on the deal page.
        if (wasActive && !dto.IsActive)
        {
            var dealsHere = await _db.Deals.CountAsync(
                d => d.TenantId == dto.TenantId && d.Stage == stage.Key && !d.IsDeleted, ct);

            var otherActive = await _db.PipelineStages.CountAsync(
                s => s.TenantId == dto.TenantId && s.Category == stage.Category
                     && s.IsActive && s.Id != dto.StageId, ct);

            // The "…use Move deals" suffix now lives inside WhyNotRetire,
            // so the disabled menu item and this refusal are the same
            // sentence. Adding it here used to mean a stage that was both
            // the only Lost stage AND holding deals got told to use a menu
            // item the page correctly never showed it.
            var why = StageRetirement.WhyNotRetire(stage, dealsHere, otherActive);
            if (why is not null) throw new InvalidOperationException(why);
        }

        // Key is deliberately NOT recalculated from the new name. Deals
        // store the key; regenerating it would strand every deal in this
        // stage and break the foreign key.
        stage.Name = name;
        stage.Probability = Math.Clamp(dto.Probability, 0, 100);
        stage.IsActive = dto.IsActive;
        stage.Color = StageColors.Clean(dto.Color) ?? stage.Color;
        stage.Description = string.IsNullOrWhiteSpace(description) ? null : description;
        stage.UpdatedAtUtc = DateTime.UtcNow;
        stage.UpdatedBy = dto.UpdatedBy;

        // ── 020 ───────────────────────────────────────────────────────
        if (wasActive && !dto.IsActive)
            await StageRetirement.SwitchOffWaysInAsync(_db, dto.TenantId, stage.Key, dto.UpdatedBy, ct);
        else if (!wasActive && dto.IsActive)
            // Brought back. Switch the ways in back on so it is usable
            // again without a trip to the process settings.
            await StageRetirement.SwitchOnWaysInAsync(_db, dto.TenantId, stage.Key, dto.UpdatedBy, ct);

        await _db.SaveChangesAsync(ct);
    }
}

// ── REORDER ───────────────────────────────────────────────────────────

public class ReorderPipelineStagesHandler : ICommandHandler
{
    private readonly FlowDbContext _db;

    public ReorderPipelineStagesHandler(FlowDbContext db) => _db = db;

    public async Task Handle(ReorderPipelineStagesDto dto, CancellationToken ct = default)
    {
        var stages = await _db.PipelineStages
            .Where(s => s.TenantId == dto.TenantId)
            .ToListAsync(ct);

        if (stages.Count == 0) return;

        // 023: category first, always. The caller's order is a hint
        // applied WITHIN a category, never across one. See the note on
        // StageOrdering.Renumber for what the old trust-the-caller
        // version silently did to the deal page.
        StageOrdering.Renumber(stages, dto.OrderedIds, dto.UpdatedBy);

        // 020: button order on the deal page follows the target stage's
        // order, so reordering the pipeline reorders the buttons too.
        // Without this the board would read left-to-right and the deal
        // page would not.
        var byKey = stages.ToDictionary(s => s.Key);

        var transitions = await _db.ProcessTransitions
            .Where(t => t.TenantId == dto.TenantId)
            .ToListAsync(ct);

        foreach (var t in transitions)
            if (byKey.TryGetValue(t.ToStageKey, out var to) && t.SortOrder != to.SortOrder)
                t.SortOrder = to.SortOrder;

        await _db.SaveChangesAsync(ct);
    }
}

// ── SET DEFAULT ───────────────────────────────────────────────────────

public class SetDefaultPipelineStageHandler : ICommandHandler
{
    private readonly FlowDbContext _db;

    public SetDefaultPipelineStageHandler(FlowDbContext db) => _db = db;

    public async Task Handle(Guid tenantId, Guid stageId, string? updatedBy, CancellationToken ct = default)
    {
        var stages = await _db.PipelineStages.Where(s => s.TenantId == tenantId).ToListAsync(ct);

        var target = stages.FirstOrDefault(s => s.Id == stageId)
            ?? throw new KeyNotFoundException("Stage not found");

        if (!target.IsActive)
            throw new InvalidOperationException("A retired stage can't be where new deals start.");

        if (target.Category != StageCategory.Open)
            throw new InvalidOperationException(
                "New deals must start in an open stage, not a won or lost one.");

        foreach (var s in stages)
        {
            if (s.IsDefault == (s.Id == stageId)) continue;

            s.IsDefault = s.Id == stageId;
            s.UpdatedAtUtc = DateTime.UtcNow;
            s.UpdatedBy = updatedBy;
        }

        await _db.SaveChangesAsync(ct);
    }
}

// ── MOVE DEALS (023) ──────────────────────────────────────────────────

/// <summary>
/// Empties a stage by sending every deal in it somewhere else, and
/// optionally retires the stage afterwards.
///
/// WHY THIS EXISTS
///   Both the retire path and the delete path refused a stage holding
///   deals with the words "move them first". Nothing in the product moved
///   them. The only route was opening each deal and pressing a transition
///   button, which also had to satisfy the transition rules — so a
///   pipeline with fifty deals in one stage could not be reshaped at all.
///
/// WHY THERE IS NO "AND THEN DELETE"
///   Moving a deal writes a DealStageHistory row naming the stage it came
///   from. So by the time the move finishes, the stage has history, and
///   DeletePipelineStageHandler refuses a stage with history — correctly,
///   because deleting it would leave every one of those timeline entries
///   pointing at a name that no longer resolves. Retire is the honest
///   outcome and the only one offered.
///
/// WHY IT DOES NOT GO THROUGH StageTransitionGuard
///   The guard answers "is this person allowed to advance this deal, and
///   does the deal meet the requirements". Neither question applies here.
///   Nobody is advancing anything: an administrator is reshaping the
///   pipeline underneath deals that did not move of their own accord.
///   Running the guard would refuse the move for exactly the deals most
///   in need of it — the half-finished ones missing a quote or a close
///   date. The history note says plainly that this was a reorganisation,
///   so the timeline does not read as sales progress that never happened.
///
/// WHY THE CATEGORY MUST MATCH
///   Open → Won in bulk would mark deals as revenue without a close date
///   or an actual value, and Won → Open would silently remove revenue
///   already reported. Closing a deal is a decision about one deal and
///   belongs on that deal. Merging two Won stages, or two Open ones, is
///   just tidying and is allowed.
/// </summary>
public class MoveStageDealsHandler : ICommandHandler
{
    /// <summary>
    /// Above this, the operation is refused rather than run. Not a
    /// technical limit — a safety rail. Rewriting tens of thousands of
    /// deals and history rows inside one web request is how a settings
    /// page takes the database down, and a tenant at that size wants a
    /// filtered bulk edit from the deal list, not a checkbox in settings.
    /// </summary>
    private const int MaxDealsInOneMove = 2000;

    private readonly FlowDbContext _db;
    private readonly ILogger<MoveStageDealsHandler> _logger;

    public MoveStageDealsHandler(FlowDbContext db, ILogger<MoveStageDealsHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<MoveStageDealsResult> Handle(
        MoveStageDealsDto dto, CancellationToken ct = default)
    {
        if (dto.FromStageId == dto.ToStageId)
            throw new InvalidOperationException("Pick a different stage to move the deals into.");

        var stages = await _db.PipelineStages
            .Where(s => s.TenantId == dto.TenantId)
            .ToListAsync(ct);

        var from = stages.FirstOrDefault(s => s.Id == dto.FromStageId)
            ?? throw new KeyNotFoundException("Stage not found");

        var to = stages.FirstOrDefault(s => s.Id == dto.ToStageId)
            ?? throw new KeyNotFoundException("The stage you picked no longer exists.");

        if (!to.IsActive)
            throw new InvalidOperationException(
                $"\"{to.Name}\" is retired, so deals can't be moved into it. Bring it back first, or pick another stage.");

        if (from.Category != to.Category)
            throw new InvalidOperationException(
                $"\"{from.Name}\" and \"{to.Name}\" aren't the same kind of stage. " +
                "Deals can only be moved in bulk between stages of the same type — " +
                "closing or reopening a deal changes its close date and value, so it has to happen on the deal itself.");

        var deals = await _db.Deals
            .Where(d => d.TenantId == dto.TenantId && d.Stage == from.Key && !d.IsDeleted)
            .ToListAsync(ct);

        if (deals.Count > MaxDealsInOneMove)
            throw new InvalidOperationException(
                $"\"{from.Name}\" holds {deals.Count:N0} deals, which is more than this page will move at once " +
                $"(the limit is {MaxDealsInOneMove:N0}). Move them from the deals list in batches instead.");

        var now = DateTime.UtcNow;

        // Written on every row so the reason is visible on the deal's own
        // timeline months later, not just in a log nobody reads.
        var note = $"Moved in bulk when \"{from.Name}\" was reorganised.";

        foreach (var deal in deals)
        {
            _db.DealStageHistory.Add(new DealStageHistory
            {
                Id = Guid.NewGuid(),
                TenantId = dto.TenantId,
                DealId = deal.Id,
                FromStage = from.Key,
                ToStage = to.Key,
                ChangedAtUtc = now,
                ChangedBy = dto.MovedBy,
                Note = note
            });

            deal.Stage = to.Key;

            // Forecasting reads Deal.Probability, not the stage's. Leaving
            // it behind would have the weighted pipeline reporting against
            // a stage the deal is no longer in, which is worse than losing
            // a manual override the administrator has just overruled
            // anyway by moving the deal.
            deal.Probability = to.Probability;

            deal.UpdatedAtUtc = now;
            deal.UpdatedBy = dto.MovedBy;
        }

        var retired = false;

        if (dto.ThenRetire && from.IsActive)
        {
            var otherActive = stages.Count(
                s => s.Category == from.Category && s.IsActive && s.Id != from.Id);

            // dealCount 0 — they are all in `deals` above and about to be
            // somewhere else in the same transaction.
            var why = StageRetirement.WhyNotRetire(from, 0, otherActive);

            if (why is not null)
                throw new InvalidOperationException(why);

            from.IsActive = false;
            from.UpdatedAtUtc = now;
            from.UpdatedBy = dto.MovedBy;

            await StageRetirement.SwitchOffWaysInAsync(_db, dto.TenantId, from.Key, dto.MovedBy, ct);
            retired = true;
        }

        // One SaveChanges, so one transaction: either every deal moves and
        // the stage retires, or nothing happens. A half-emptied stage that
        // also got retired would strand whatever was left.
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Moved {Count} deal(s) from {From} to {To} for tenant {TenantId} (retired: {Retired})",
            deals.Count, from.Key, to.Key, dto.TenantId, retired);

        return new MoveStageDealsResult(deals.Count, from.Name, to.Name, retired);
    }
}

// ── DELETE ────────────────────────────────────────────────────────────

public class DeletePipelineStageHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly ILogger<DeletePipelineStageHandler> _logger;

    public DeletePipelineStageHandler(FlowDbContext db, ILogger<DeletePipelineStageHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task Handle(Guid tenantId, Guid stageId, CancellationToken ct = default)
    {
        var stage = await _db.PipelineStages
            .FirstOrDefaultAsync(s => s.Id == stageId && s.TenantId == tenantId, ct)
            ?? throw new KeyNotFoundException("Stage not found");

        // Deals hold the key, and the FK would refuse anyway — but a clear
        // message beats a constraint violation surfacing as "please try
        // again".
        var dealsHere = await _db.Deals.CountAsync(
            d => d.TenantId == tenantId && d.Stage == stage.Key && !d.IsDeleted, ct);

        // History keeps the key as text, so old rows still read correctly
        // after the stage is gone — but only if the name still resolves to
        // something. It does not once the stage is deleted.
        var everUsed = await _db.DealStageHistory.AnyAsync(
            h => h.TenantId == tenantId && (h.ToStage == stage.Key || h.FromStage == stage.Key), ct);

        // Needed for the "your only Won stage" check. On a trial workspace
        // the seeded closing stages have no deals and no history yet, so
        // without this a tenant admin could delete Closed Won on day one.
        var otherActiveInCategory = await _db.PipelineStages.CountAsync(
            s => s.TenantId == tenantId && s.Category == stage.Category
                 && s.IsActive && s.Id != stageId, ct);

        var why = StageRetirement.WhyNotDelete(stage, dealsHere, everUsed, otherActiveInCategory);
        if (why is not null) throw new InvalidOperationException(why);

        // ── 020 ───────────────────────────────────────────────────────
        // ProcessTransitions has foreign keys onto PipelineStages in both
        // directions. Without clearing them first, this delete fails with
        // a raw constraint violation.
        //
        // Deleting the rows outright is right: they describe moves to and
        // from a stage that is about to stop existing, and nothing refers
        // back to them. The stage has no deals and no history by the
        // checks above, so nothing is lost.
        //
        // 022 note: TransitionActions cascade from ProcessTransitions, so
        // any follow-ups configured on these steps go with them. That is
        // the cascade doing its job — an action describing what happens
        // on a step that no longer exists has no meaning.
        var links = await _db.ProcessTransitions
            .Where(t => t.TenantId == tenantId
                     && (t.FromStageKey == stage.Key || t.ToStageKey == stage.Key))
            .ToListAsync(ct);

        if (links.Count > 0)
        {
            _db.ProcessTransitions.RemoveRange(links);
            _logger.LogInformation(
                "Removing {Count} process transitions with stage {Key} for tenant {TenantId}",
                links.Count, stage.Key, tenantId);
        }

        _db.PipelineStages.Remove(stage);
        await _db.SaveChangesAsync(ct);

        // The gap this leaves in SortOrder is harmless — Renumber closes
        // it on the next reorder, and nothing reads SortOrder as anything
        // but a sequence.
    }
}
