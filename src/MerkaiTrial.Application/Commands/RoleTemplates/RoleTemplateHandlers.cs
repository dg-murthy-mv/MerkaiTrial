// =====================================================================
// FILE: MerkaiTrial.Application/Commands/RoleTemplates/RoleTemplateHandlers.cs
//
// NEW (117). STARTING ROLES — the RoleTemplates every NEW workspace copies
// when it is created (TenantProvisioningService.BuildSeedRolesAsync).
//
// Until now nothing in the product could change them: Round 113 had to
// edit them with SQL. The super admin's old "Roles" page edited something
// else entirely — the SYSTEM roles (TenantId NULL, e.g. tenant_admin) that
// every workspace SHARES, so one save there changed every customer at once.
//
// What these handlers allow:
//   • list the starting roles, each with how many workspaces have a role
//     of that name today (they keep their own copy — editing a starting
//     role never changes them)
//   • the system roles, READ ONLY (shown so the page explains the whole
//     picture; nothing here edits them)
//   • change a starting role's display name, description, order,
//     permissions, and whether new workspaces get it (IsActive)
//   • add a new starting role
// No delete: switching one off (IsActive = false) stops new workspaces
// getting it, and a row that was ever used stays explainable.
//
// PERMISSIONS are checked against ModuleCatalog: only modules and actions
// the product enforces are accepted (a typo would otherwise be a grant
// that silently does nothing). Keys are saved in the PascalCase the
// existing role data uses ("Leads"), whatever case the caller sent.
//
// Super-admin only — the controller (RoleTemplatesController) checks.
// =====================================================================

using System.Text.Json;
using System.Text.RegularExpressions;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Application.Commands.RoleTemplates;

// ── DTOs ──────────────────────────────────────────────────────────────

public sealed class RoleTemplateDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    /// <summary>JSON: {"Leads":["read","create"], ...}</summary>
    public string? Permissions { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
    /// <summary>Workspaces that have a role of this name today (their own copy).</summary>
    public int WorkspacesWithIt { get; set; }
}

public sealed class SystemRoleDto
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public sealed class RoleTemplatesOverviewDto
{
    public List<RoleTemplateDto> Templates { get; set; } = new();
    public List<SystemRoleDto> SystemRoles { get; set; } = new();
}

public sealed class SaveRoleTemplateDto
{
    /// <summary>Create only: the key, e.g. "support_agent". Ignored on update.</summary>
    public string? Name { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Permissions { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

// ── Shared rules ──────────────────────────────────────────────────────

public static class RoleTemplateRules
{
    private static readonly Regex NamePattern = new("^[a-z][a-z0-9_]{1,49}$", RegexOptions.Compiled);

    /// <summary>Lower-case letters, digits and _, starting with a letter, 2–50 long.</summary>
    public static string? NameProblem(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Give the role a key, like support_agent.";
        if (!NamePattern.IsMatch(name.Trim()))
            return "The key may use lower-case letters, digits and _ only, starting with a letter (2–50 characters), like support_agent.";
        return null;
    }

    /// <summary>
    /// Checks the permission JSON against ModuleCatalog and returns it in the
    /// stored shape (PascalCase module keys, lower-case actions, no
    /// duplicates, modules with nothing ticked left out).
    /// alreadyStored (when changing a role): anything the role ALREADY held
    /// that the product's list doesn't know (older data) is kept as it was
    /// rather than refused, so an old starting role can still be edited.
    /// Only something NEW and unknown is refused.
    /// </summary>
    public static (string? Json, string? Problem) Normalise(string? json, string? alreadyStored = null)
    {
        if (string.IsNullOrWhiteSpace(json)) return ("{}", null);

        var old = ReadLoose(alreadyStored);
        bool HeldBefore(string? key, string? action) =>
            key is not null && old.TryGetValue(key.Trim(), out var acts) &&
            (action is null || acts.Contains(action.Trim(), StringComparer.OrdinalIgnoreCase));

        Dictionary<string, List<string>?>? raw;
        try { raw = JsonSerializer.Deserialize<Dictionary<string, List<string>?>>(json); }
        catch (JsonException) { return (null, "The permissions could not be read."); }

        var result = new Dictionary<string, List<string>>();
        foreach (var (key, actions) in raw ?? new())
        {
            var module = ModuleCatalog.All.FirstOrDefault(m => string.Equals(m.Key, key?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (module is null)
            {
                if (!HeldBefore(key, null)) return (null, $"'{key}' is not an area the product has.");
                var keptActions = (actions ?? new()).Where(a => HeldBefore(key, a)).Select(a => a!.Trim()).Distinct().ToList();
                if (keptActions.Count > 0) result[key!.Trim()] = keptActions;
                continue;
            }

            var list = new List<string>();
            foreach (var a in actions ?? new())
            {
                var action = module.Actions.FirstOrDefault(x => string.Equals(x, a?.Trim(), StringComparison.OrdinalIgnoreCase));
                if (action is null)
                {
                    if (!HeldBefore(key, a)) return (null, $"'{a}' is not something that can be done in {module.DisplayName}.");
                    action = a!.Trim();
                }
                if (!list.Contains(action, StringComparer.OrdinalIgnoreCase)) list.Add(action);
            }
            if (list.Count == 0) continue;

            var pascal = PascalKey(module.Key);
            if (result.TryGetValue(pascal, out var existing))
                existing.AddRange(list.Where(x => !existing.Contains(x)));
            else
                result[pascal] = list;
        }

        return (JsonSerializer.Serialize(result), null);
    }

    /// <summary>The stored JSON as module → actions, ignoring case; empty if unreadable.</summary>
    private static Dictionary<string, List<string>> ReadLoose(string? json)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return map;
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, List<string>?>>(json) ?? new();
            foreach (var (k, v) in raw)
                if (!string.IsNullOrWhiteSpace(k)) map[k.Trim()] = (v ?? new()).Where(x => x is not null).Select(x => x.Trim()).ToList();
        }
        catch (JsonException) { }
        return map;
    }

    /// <summary>"leads" → "Leads", "financereports" → "FinanceReports" (the role data's spelling).</summary>
    public static string PascalKey(string key) => key.ToLowerInvariant() switch
    {
        "financereports" => "FinanceReports",
        _ => char.ToUpperInvariant(key[0]) + key[1..],
    };

    public static string? TextProblem(SaveRoleTemplateDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.DisplayName)) return "Give the role a name people will see, like Support Agent.";
        if (dto.DisplayName.Trim().Length > 200) return "The name can be up to 200 characters.";
        if ((dto.Description?.Trim().Length ?? 0) > 500) return "The description can be up to 500 characters.";
        if (dto.SortOrder is < 0 or > 1000) return "The order must be between 0 and 1000.";
        return null;
    }
}

// ── List ──────────────────────────────────────────────────────────────

public sealed class GetRoleTemplatesHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    public GetRoleTemplatesHandler(FlowDbContext db) => _db = db;

    public async Task<RoleTemplatesOverviewDto> HandleAsync(CancellationToken ct = default)
    {
        var templates = await _db.Set<RoleTemplate>().AsNoTracking()
            .OrderBy(t => t.SortOrder).ThenBy(t => t.DisplayName)
            .ToListAsync(ct);

        var names = templates.Select(t => t.Name).ToList();
        var counts = await _db.Roles.AsNoTracking().IgnoreQueryFilters()
            .Where(r => r.TenantId != null && !r.IsDeleted && names.Contains(r.Name))
            .GroupBy(r => r.Name)
            .Select(g => new { g.Key, Count = g.Select(r => r.TenantId).Distinct().Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, StringComparer.OrdinalIgnoreCase, ct);

        var system = await _db.Roles.AsNoTracking().IgnoreQueryFilters()
            .Where(r => r.TenantId == null && !r.IsDeleted)
            .OrderBy(r => r.Name)
            .Select(r => new SystemRoleDto { Name = r.Name, DisplayName = r.DisplayName, Description = r.Description })
            .ToListAsync(ct);

        return new RoleTemplatesOverviewDto
        {
            Templates = templates.Select(t => Map(t, counts.TryGetValue(t.Name, out var c) ? c : 0)).ToList(),
            SystemRoles = system,
        };
    }

    internal static RoleTemplateDto Map(RoleTemplate t, int workspaces) => new()
    {
        Id = t.Id, Name = t.Name, DisplayName = t.DisplayName, Description = t.Description,
        Permissions = t.Permissions, SortOrder = t.SortOrder, IsActive = t.IsActive,
        CreatedAtUtc = t.CreatedAtUtc, UpdatedAtUtc = t.UpdatedAtUtc, UpdatedBy = t.UpdatedBy,
        WorkspacesWithIt = workspaces,
    };
}

// ── One ───────────────────────────────────────────────────────────────

public sealed class GetRoleTemplateHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    public GetRoleTemplateHandler(FlowDbContext db) => _db = db;

    /// <exception cref="KeyNotFoundException">No such starting role.</exception>
    public async Task<RoleTemplateDto> HandleAsync(Guid id, CancellationToken ct = default)
    {
        var t = await _db.Set<RoleTemplate>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new KeyNotFoundException("That starting role does not exist.");
        var count = await _db.Roles.AsNoTracking().IgnoreQueryFilters()
            .Where(r => r.TenantId != null && !r.IsDeleted && r.Name == t.Name)
            .Select(r => r.TenantId).Distinct().CountAsync(ct);
        return GetRoleTemplatesHandler.Map(t, count);
    }
}

// ── Add ───────────────────────────────────────────────────────────────

public sealed class CreateRoleTemplateHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    public CreateRoleTemplateHandler(FlowDbContext db) => _db = db;

    /// <exception cref="InvalidOperationException">A sentence for the person.</exception>
    public async Task<RoleTemplateDto> HandleAsync(SaveRoleTemplateDto dto, string by, CancellationToken ct = default)
    {
        var name = dto.Name?.Trim().ToLowerInvariant() ?? "";
        var problem = RoleTemplateRules.NameProblem(name) ?? RoleTemplateRules.TextProblem(dto);
        if (problem is not null) throw new InvalidOperationException(problem);

        var (json, permProblem) = RoleTemplateRules.Normalise(dto.Permissions);
        if (permProblem is not null) throw new InvalidOperationException(permProblem);

        if (await _db.Set<RoleTemplate>().AnyAsync(t => t.Name == name, ct))
            throw new InvalidOperationException($"There is already a starting role with the key '{name}'.");
        if (await _db.Roles.IgnoreQueryFilters().AnyAsync(r => r.TenantId == null && r.Name == name, ct))
            throw new InvalidOperationException($"'{name}' is a system role's key. Choose another.");

        var t = new RoleTemplate
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayName = dto.DisplayName.Trim(),
            Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim(),
            Permissions = json,
            SortOrder = dto.SortOrder,
            IsActive = dto.IsActive,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = Short(by),
        };
        _db.Set<RoleTemplate>().Add(t);
        await _db.SaveChangesAsync(ct);
        return GetRoleTemplatesHandler.Map(t, 0);
    }

    internal static string Short(string by) => by.Length <= 100 ? by : by[..100];
}

// ── Change ────────────────────────────────────────────────────────────

public sealed class UpdateRoleTemplateHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    public UpdateRoleTemplateHandler(FlowDbContext db) => _db = db;

    /// <exception cref="KeyNotFoundException">No such starting role.</exception>
    /// <exception cref="InvalidOperationException">A sentence for the person.</exception>
    public async Task HandleAsync(Guid id, SaveRoleTemplateDto dto, string by, CancellationToken ct = default)
    {
        var problem = RoleTemplateRules.TextProblem(dto);
        if (problem is not null) throw new InvalidOperationException(problem);

        var t = await _db.Set<RoleTemplate>().FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new KeyNotFoundException("That starting role does not exist.");

        var (json, permProblem) = RoleTemplateRules.Normalise(dto.Permissions, t.Permissions);
        if (permProblem is not null) throw new InvalidOperationException(permProblem);

        // Never leave new workspaces with no starting role but their admin.
        if (!dto.IsActive && t.IsActive &&
            !await _db.Set<RoleTemplate>().AnyAsync(x => x.Id != id && x.IsActive, ct))
            throw new InvalidOperationException("This is the last starting role new workspaces get. Keep at least one switched on.");

        t.DisplayName = dto.DisplayName.Trim();
        t.Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim();
        t.Permissions = json;
        t.SortOrder = dto.SortOrder;
        t.IsActive = dto.IsActive;
        t.UpdatedAtUtc = DateTime.UtcNow;
        t.UpdatedBy = CreateRoleTemplateHandler.Short(by);
        await _db.SaveChangesAsync(ct);
    }
}
