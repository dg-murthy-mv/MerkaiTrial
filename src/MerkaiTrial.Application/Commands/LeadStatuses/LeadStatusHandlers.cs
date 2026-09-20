// =====================================================================
// LeadStatusHandlers.cs
// Location: MerkaiTrial.Application/Commands/LeadStatuses/
//
// NEW FILE. Read and write side of the tenant's lead statuses, plus the
// resolver every lead handler will use.
//
// THE RULES, ENFORCED HERE RATHER THAN IN THE UI
//   • Key is generated once and never changes — renaming must not move
//     the leads sitting in a status.
//   • A tenant must always have at least one Qualified status, or no lead
//     could ever be converted to a deal.
//   • Converted is IsSystem: renameable, but not deletable, not
//     deactivatable, and not offered in any dropdown.
//   • A status holding leads cannot be deleted — only retired.
//
// RECORD VISIBILITY (014): LeadCount in the list is what the CURRENT USER
// can see (it drives the Leads page tab counts). A rep with Own scope sees
// counts of their own leads. Delete/retire checks below still count every
// lead in the tenant — they are about data integrity, not display.
// =====================================================================

using MerkaiTrial.Application.Security;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text;

namespace MerkaiTrial.Application.Commands.LeadStatuses;

// ── DTOs ──────────────────────────────────────────────────────────────

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
    int LeadCount);

public record CreateLeadStatusDto(
    Guid TenantId,
    string Name,
    int Score,
    LeadStatusCategory Category,
    string? CreatedBy = null);

public record UpdateLeadStatusDefDto(
    Guid TenantId,
    Guid StatusId,
    string Name,
    int Score,
    bool IsActive,
    string? UpdatedBy = null);

public record ReorderLeadStatusesDto(
    Guid TenantId,
    List<Guid> OrderedIds,
    string? UpdatedBy = null);

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

    public async Task<List<LeadStatusDto>> Handle(
        Guid tenantId, bool selectableOnly = false, CancellationToken ct = default)
    {
        var q = _db.LeadStatusDefinitions.AsNoTracking().Where(s => s.TenantId == tenantId);

        if (selectableOnly)
            q = q.Where(s => s.IsActive && !s.IsSystem);

        var statuses = await q.OrderBy(s => s.SortOrder).ToListAsync(ct);

        // Counts respect record visibility — they are the Leads page's tab
        // counts, and must agree with the list the user will see.
        var access = await _scope.GetAsync(RecordModules.Leads, ct);

        var counts = await _db.Leads.AsNoTracking()
            .Where(l => l.TenantId == tenantId && !l.IsDeleted)
            .VisibleTo(access)
            .GroupBy(l => l.Status)
            .Select(g => new { Status = g.Key, N = g.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.N, ct);

        return statuses.Select(s => new LeadStatusDto(
            s.Id, s.Key, s.Name, s.SortOrder, s.Score,
            s.Category, s.IsActive, s.IsDefault, s.IsSystem,
            counts.GetValueOrDefault(s.Key))).ToList();
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

        var existing = await _db.LeadStatusDefinitions
            .Where(s => s.TenantId == dto.TenantId)
            .ToListAsync(ct);

        if (existing.Count >= MaxStatuses)
            throw new InvalidOperationException(
                $"A lead pipeline can have at most {MaxStatuses} statuses. Retire one you no longer use.");

        if (existing.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"You already have a status called \"{name}\".");

        var status = new LeadStatusDefinition
        {
            Id = Guid.NewGuid(),
            TenantId = dto.TenantId,
            Key = await StatusKeys.UniqueAsync(_db, dto.TenantId, name, ct),
            Name = name,
            SortOrder = existing.Count == 0 ? 1 : existing.Max(s => s.SortOrder) + 1,
            Score = Math.Clamp(dto.Score, 0, 15),
            Category = dto.Category,
            IsActive = true,
            IsDefault = false,
            IsSystem = false,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = dto.CreatedBy
        };

        _db.LeadStatusDefinitions.Add(status);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Created lead status {Name} ({Key}) for tenant {TenantId}",
            status.Name, status.Key, dto.TenantId);

        return new LeadStatusDto(status.Id, status.Key, status.Name, status.SortOrder,
            status.Score, status.Category, status.IsActive, status.IsDefault, status.IsSystem, 0);
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

        var clash = await _db.LeadStatusDefinitions.AnyAsync(
            s => s.TenantId == dto.TenantId && s.Id != dto.StatusId && s.Name == name, ct);
        if (clash)
            throw new InvalidOperationException($"You already have a status called \"{name}\".");

        // The system status can be RENAMED but never retired — conversion
        // has to have somewhere to put the lead.
        if (status.IsSystem && !dto.IsActive)
            throw new InvalidOperationException(
                "This status is set automatically when a lead converts, so it can't be retired. You can rename it.");

        if (status.IsActive && !dto.IsActive)
        {
            if (status.IsDefault)
                throw new InvalidOperationException(
                    "This is where new leads start. Make another status the starting point first.");

            // Without a Qualified status, no lead could ever convert.
            if (status.Category == LeadStatusCategory.Qualified)
            {
                var otherQualified = await _db.LeadStatusDefinitions.CountAsync(
                    s => s.TenantId == dto.TenantId
                      && s.Category == LeadStatusCategory.Qualified
                      && s.IsActive && s.Id != dto.StatusId, ct);

                if (otherQualified == 0)
                    throw new InvalidOperationException(
                        "This is your only qualified status. Without it, no lead could be converted to a deal.");
            }

            var leadsHere = await _db.Leads.CountAsync(
                l => l.TenantId == dto.TenantId && l.Status == status.Key && !l.IsDeleted, ct);

            if (leadsHere > 0)
                throw new InvalidOperationException(
                    $"{leadsHere} lead(s) are still in this status. Move them first, then retire it.");
        }

        // Key deliberately NOT recalculated — leads store it.
        status.Name = name;
        status.Score = Math.Clamp(dto.Score, 0, 15);
        status.IsActive = dto.IsActive;
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

        var order = 1;
        foreach (var id in dto.OrderedIds)
        {
            var s = all.FirstOrDefault(x => x.Id == id);
            if (s is null) continue;

            s.SortOrder = order++;
            s.UpdatedAtUtc = DateTime.UtcNow;
            s.UpdatedBy = dto.UpdatedBy;
        }

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
            s.IsDefault = s.Id == statusId;
            s.UpdatedAtUtc = DateTime.UtcNow;
            s.UpdatedBy = updatedBy;
        }

        await _db.SaveChangesAsync(ct);
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

        if (status.IsSystem)
            throw new InvalidOperationException(
                "This status is set automatically when a lead converts and can't be removed.");

        if (status.IsDefault)
            throw new InvalidOperationException(
                "This is where new leads start. Make another status the starting point first.");

        var leadsHere = await _db.Leads.CountAsync(
            l => l.TenantId == tenantId && l.Status == status.Key && !l.IsDeleted, ct);

        if (leadsHere > 0)
            throw new InvalidOperationException(
                $"{leadsHere} lead(s) are in this status. Move them first, or retire it instead of deleting.");

        if (status.Category == LeadStatusCategory.Qualified)
        {
            var otherQualified = await _db.LeadStatusDefinitions.CountAsync(
                s => s.TenantId == tenantId
                  && s.Category == LeadStatusCategory.Qualified
                  && s.Id != statusId, ct);

            if (otherQualified == 0)
                throw new InvalidOperationException(
                    "This is your only qualified status. Without it, no lead could be converted to a deal.");
        }

        _db.LeadStatusDefinitions.Remove(status);
        await _db.SaveChangesAsync(ct);
    }
}
