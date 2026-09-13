// =====================================================================
// LeadImportHandlers.cs
// Location: MerkaiTrial.Application/Commands/Leads/Import/LeadImportHandlers.cs
//
// NEW FILE. Replaces the old ImportLeadsHandler entirely.
//
// WHAT THE OLD ONE DID WRONG (all fixed here)
//   • Created Contact rows with NO TenantId — either rejected by the DB or
//     saved against Guid.Empty, where the global query filter makes them
//     invisible to every tenant.
//   • Created Leads with no FullName, Email or Phone (it put the name on
//     the Contact instead), so the leads list showed blank rows.
//   • Re-importing overwrote Status, Score and Owner — a Qualified lead
//     silently reset to New.
//   • Required an email, which rules out the phone-only lists that are
//     normal in these markets.
//   • SaveChanges per row, no transaction: a failure at row 400 left 399
//     committed.
//   • No plan quota check.
//   • No preview — the user found out what happened afterwards.
//
// DELIBERATE: import does NOT create Contact or Company records. A Lead
// holds its own name, email, phone and company name; Contact and Company
// are created at conversion, which is where the dedup logic already lives.
// Importing 500 leads should not silently create 500 contacts.
// =====================================================================

using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Domain.Enums;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Commands.Leads.Import;

// =====================================================================
// SESSION STORE
// =====================================================================

/// <summary>
/// Holds a parsed file between upload, preview and commit.
///
/// NOTE FOR AZURE: this is in-process. With the WebApi scaled to more than
/// one instance, a preview could land on a different instance from the
/// upload and fail with "session expired". Fine for one instance; if you
/// scale out, either turn on ARR affinity or move this to a table.
/// </summary>
public interface IImportSessionStore
{
    Guid Save(Guid tenantId, string fileName, LeadFileParser.Grid grid);
    (string FileName, LeadFileParser.Grid Grid)? Get(Guid sessionId, Guid tenantId);
    void Remove(Guid sessionId);
}

public class ImportSessionStore : IImportSessionStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private readonly IMemoryCache _cache;

    public ImportSessionStore(IMemoryCache cache) => _cache = cache;

    private sealed record Entry(Guid TenantId, string FileName, LeadFileParser.Grid Grid);

    public Guid Save(Guid tenantId, string fileName, LeadFileParser.Grid grid)
    {
        var id = Guid.NewGuid();
        _cache.Set(Key(id), new Entry(tenantId, fileName, grid), Lifetime);
        return id;
    }

    public (string FileName, LeadFileParser.Grid Grid)? Get(Guid sessionId, Guid tenantId)
    {
        if (!_cache.TryGetValue(Key(sessionId), out Entry? e) || e is null) return null;

        // The session belongs to the tenant that uploaded it. Without this,
        // a guessed session id would expose another tenant's file.
        if (e.TenantId != tenantId) return null;

        return (e.FileName, e.Grid);
    }

    public void Remove(Guid sessionId) => _cache.Remove(Key(sessionId));

    private static string Key(Guid id) => $"import:{id}";
}

// =====================================================================
// UPLOAD
// =====================================================================

public class UploadLeadImportHandler : ICommandHandler
{
    private readonly IImportSessionStore _sessions;

    public UploadLeadImportHandler(IImportSessionStore sessions) => _sessions = sessions;

    public ImportUploadResult Handle(Guid tenantId, byte[] bytes, string fileName)
    {
        var grid = LeadFileParser.Parse(bytes, fileName);
        var sessionId = _sessions.Save(tenantId, fileName, grid);

        var suggested = LeadFileParser.SuggestMapping(grid.Headers);

        var warnings = new List<string>(grid.Warnings);
        if (!suggested.ContainsKey(ImportFields.FullName))
            warnings.Add("Couldn't work out which column holds the name — please pick it below.");

        return new ImportUploadResult(
            SessionId: sessionId,
            FileName: fileName,
            TotalRows: grid.Rows.Count,
            Headers: grid.Headers,
            SampleRows: grid.Rows.Take(5).ToList(),
            SuggestedMapping: suggested,
            Warnings: warnings);
    }
}

// =====================================================================
// SHARED VALIDATION — used by both preview and commit, so what the user
// is shown and what actually happens can never diverge.
// =====================================================================

internal sealed class ImportContext
{
    public required string? CountryCode { get; init; }
    public required Dictionary<string, Guid> SourcesByName { get; init; }
    public required Dictionary<string, Guid> ChannelsByName { get; init; }
    public required Dictionary<string, Guid> CountriesByName { get; init; }
    public required Dictionary<string, Guid> VerticalsByName { get; init; }
    public required Dictionary<string, Guid> UsersByEmail { get; init; }
    public required Dictionary<string, Guid> ExistingByEmail { get; init; }
    public required Dictionary<string, Guid> ExistingByPhone { get; init; }
    public required string DefaultCurrency { get; init; }
    public HashSet<string> UnmatchedSources { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> UnmatchedOwners { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> UnmatchedCountries { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One row, validated and resolved, ready to become a Lead.</summary>
internal sealed record ResolvedRow(
    int RowNumber,
    string Verdict,
    string FullName,
    string? Email,
    string? Phone,
    string? CompanyName,
    string? Address,
    Guid? CountryId,
    Guid? SourceId,
    string? SourceText,
    Guid? ChannelId,
    Guid? VerticalId,
    LeadStatus Status,
    int Score,
    decimal? EstimatedValue,
    string Currency,
    string? OwnerUserId,
    Guid? ExistingLeadId,
    List<string> Messages);

internal static class ImportValidator
{
    public static async Task<ImportContext> BuildContextAsync(
         FlowDbContext db, Guid tenantId, string defaultCurrency, string? countryCode, CancellationToken ct)
    {
        

        var sources = await db.LeadSources.AsNoTracking()
            .Where(s => s.TenantId == tenantId && !s.IsDeleted)
            .Select(s => new { s.Id, s.Name }).ToListAsync(ct);

        var channels = await db.LeadChannels.AsNoTracking()
            .Where(c => c.TenantId == tenantId && !c.IsDeleted)
            .Select(c => new { c.Id, c.Name }).ToListAsync(ct);

        var countries = await db.Countries.AsNoTracking()
            .Select(c => new { c.Id, c.Name, c.Code }).ToListAsync(ct);

        var verticals = await db.CompanyVerticals.AsNoTracking()
            .Where(v => !v.IsDeleted)
            .Select(v => new { v.Id, v.Name }).ToListAsync(ct);

        var users = await db.Users.AsNoTracking()
            .Where(u => u.TenantId == tenantId && !u.IsDeleted)
            .Select(u => new { u.Id, u.Email }).ToListAsync(ct);

        // Existing leads, for duplicate detection. Only the fields needed.
        var existing = await db.Leads.AsNoTracking()
            .Where(l => l.TenantId == tenantId && !l.IsDeleted)
            .Select(l => new { l.Id, l.Email, l.Phone }).ToListAsync(ct);

        var byEmail = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var byPhone = new Dictionary<string, Guid>();

        foreach (var l in existing)
        {
            if (!string.IsNullOrWhiteSpace(l.Email))
                byEmail.TryAdd(l.Email.Trim(), l.Id);

            var phone = LeadFileParser.NormalisePhone(l.Phone, countryCode);
            if (phone != null) byPhone.TryAdd(phone, l.Id);
        }

        static Dictionary<string, Guid> Dict(IEnumerable<(string Name, Guid Id)> items)
        {
            var d = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, id) in items)
                if (!string.IsNullOrWhiteSpace(name)) d.TryAdd(name.Trim(), id);
            return d;
        }

        var countryLookup = Dict(countries.Select(c => (c.Name, c.Id)));
        foreach (var c in countries)
            if (!string.IsNullOrWhiteSpace(c.Code)) countryLookup.TryAdd(c.Code.Trim(), c.Id);

        return new ImportContext
        {
            SourcesByName   = Dict(sources.Select(s => (s.Name, s.Id))),
            ChannelsByName  = Dict(channels.Select(c => (c.Name, c.Id))),
            CountriesByName = countryLookup,
            VerticalsByName = Dict(verticals.Select(v => (v.Name, v.Id))),
            UsersByEmail    = Dict(users.Select(u => (u.Email, u.Id))),
            ExistingByEmail = byEmail,
            ExistingByPhone = byPhone,
            CountryCode = countryCode,
            DefaultCurrency = defaultCurrency,

        };
    }

    public static List<ResolvedRow> Resolve(
        LeadFileParser.Grid grid,
        Dictionary<string, int> mapping,
        ImportContext ctx,
        string duplicateAction)
    {
        var results = new List<ResolvedRow>(grid.Rows.Count);

        // Duplicates WITHIN the file matter as much as against the database.
        var seenEmails = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var seenPhones = new Dictionary<string, int>();

        string? Get(IReadOnlyList<string> row, string field)
        {
            if (!mapping.TryGetValue(field, out var col)) return null;
            if (col < 0 || col >= row.Count) return null;
            var v = row[col]?.Trim();
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }

        for (var i = 0; i < grid.Rows.Count; i++)
        {
            var row = grid.Rows[i];
            var rowNumber = i + 2;              // +1 for zero-based, +1 for the header row
            var messages = new List<string>();
            var verdict = RowVerdict.Ok;

            // ── Name (the only required field) ────────────────────────
            var fullName = Get(row, ImportFields.FullName);
            if (string.IsNullOrWhiteSpace(fullName))
            {
                results.Add(Fail(rowNumber, "Name is missing"));
                continue;
            }
            if (fullName.Length > 200) fullName = fullName[..200];

            // ── Email ─────────────────────────────────────────────────
            var email = Get(row, ImportFields.Email);
            if (email != null && !LeadFileParser.LooksLikeEmail(email))
            {
                messages.Add($"\"{email}\" doesn't look like an email — imported without it");
                email = null;
                verdict = RowVerdict.Warning;
            }

            // ── Phone ─────────────────────────────────────────────────
            var phoneRaw = Get(row, ImportFields.Phone);
            var phoneKey = LeadFileParser.NormalisePhone(phoneRaw, ctx.CountryCode);
            if (phoneRaw != null && phoneKey == null)
            {
                messages.Add($"\"{phoneRaw}\" isn't a usable phone number — imported without it");
                phoneRaw = null;
                verdict = RowVerdict.Warning;
            }

            if (email == null && phoneKey == null)
            {
                messages.Add("No email or phone — nobody will be able to contact this lead");
                verdict = RowVerdict.Warning;
            }

            // ── Duplicates ────────────────────────────────────────────
            Guid? existingId = null;

            if (email != null && ctx.ExistingByEmail.TryGetValue(email, out var byEmailId))
                existingId = byEmailId;
            else if (phoneKey != null && ctx.ExistingByPhone.TryGetValue(phoneKey, out var byPhoneId))
                existingId = byPhoneId;

            if (existingId.HasValue)
            {
                verdict = RowVerdict.Duplicate;
                messages.Add(duplicateAction switch
                {
                    DuplicateAction.Update => "Already in your leads — blank fields will be filled in",
                    DuplicateAction.Import => "Already in your leads — will be imported again",
                    _ => "Already in your leads — will be skipped"
                });
            }
            else
            {
                // Same person twice inside the file.
                if (email != null && seenEmails.TryGetValue(email, out var firstRow))
                {
                    verdict = RowVerdict.Duplicate;
                    messages.Add($"Same email as row {firstRow} in this file — will be skipped");
                }
                else if (phoneKey != null && seenPhones.TryGetValue(phoneKey, out var firstPhoneRow))
                {
                    verdict = RowVerdict.Duplicate;
                    messages.Add($"Same phone as row {firstPhoneRow} in this file — will be skipped");
                }
                else
                {
                    if (email != null) seenEmails[email] = rowNumber;
                    if (phoneKey != null) seenPhones[phoneKey] = rowNumber;
                }
            }

            // ── Lookups ───────────────────────────────────────────────
            Guid? sourceId = null;
            var sourceText = Get(row, ImportFields.Source);
            if (sourceText != null)
            {
                if (ctx.SourcesByName.TryGetValue(sourceText, out var sid)) sourceId = sid;
                else ctx.UnmatchedSources.Add(sourceText);
            }

            Guid? channelId = null;
            var channelText = Get(row, ImportFields.Channel);
            if (channelText != null && ctx.ChannelsByName.TryGetValue(channelText, out var chId))
                channelId = chId;

            Guid? countryId = null;
            var countryText = Get(row, ImportFields.Country);
            if (countryText != null)
            {
                if (ctx.CountriesByName.TryGetValue(countryText, out var cid)) countryId = cid;
                else ctx.UnmatchedCountries.Add(countryText);
            }

            Guid? verticalId = null;
            var verticalText = Get(row, ImportFields.Vertical);
            if (verticalText != null && ctx.VerticalsByName.TryGetValue(verticalText, out var vid))
                verticalId = vid;

            string? ownerUserId = null;
            var ownerText = Get(row, ImportFields.Owner);
            if (ownerText != null)
            {
                if (ctx.UsersByEmail.TryGetValue(ownerText, out var uid)) ownerUserId = uid.ToString();
                else
                {
                    ctx.UnmatchedOwners.Add(ownerText);
                    messages.Add($"No user with email \"{ownerText}\" — imported unassigned");
                    if (verdict == RowVerdict.Ok) verdict = RowVerdict.Warning;
                }
            }

            // ── Status / score / value ────────────────────────────────
            var status = LeadStatus.New;
            var statusText = Get(row, ImportFields.Status);
            if (statusText != null)
            {
                if (Enum.TryParse<LeadStatus>(statusText.Replace(" ", ""), true, out var parsed)
                    && parsed != LeadStatus.Converted)   // conversion is an action, never an import value
                {
                    status = parsed;
                }
                else
                {
                    messages.Add($"Status \"{statusText}\" not recognised — imported as New");
                    if (verdict == RowVerdict.Ok) verdict = RowVerdict.Warning;
                }
            }

            var score = LeadFileParser.ParseInt(Get(row, ImportFields.Score)) ?? 0;
            if (score < 0) score = 0;
            if (score > 100) score = 100;

            var value = LeadFileParser.ParseDecimal(Get(row, ImportFields.EstimatedValue));
            if (value is < 0) value = null;

            var currency = Get(row, ImportFields.Currency)?.ToUpperInvariant();
            if (currency != null && currency.Length != 3)
            {
                messages.Add($"Currency \"{currency}\" isn't a 3-letter code — using {ctx.DefaultCurrency}");
                currency = null;
                if (verdict == RowVerdict.Ok) verdict = RowVerdict.Warning;
            }

            results.Add(new ResolvedRow(
                RowNumber: rowNumber,
                Verdict: verdict,
                FullName: fullName,
                Email: email,
                Phone: phoneRaw,
                CompanyName: Truncate(Get(row, ImportFields.CompanyName), 200),
                Address: Truncate(Get(row, ImportFields.Address), 500),
                CountryId: countryId,
                SourceId: sourceId,
                SourceText: sourceText,
                ChannelId: channelId,
                VerticalId: verticalId,
                Status: status,
                Score: score,
                EstimatedValue: value,
                Currency: currency ?? ctx.DefaultCurrency,
                OwnerUserId: ownerUserId,
                ExistingLeadId: existingId,
                Messages: messages));

            ResolvedRow Fail(int n, string reason) => new(
                n, RowVerdict.Error, "", null, null, null, null, null, null, null,
                null, null, LeadStatus.New, 0, null, ctx.DefaultCurrency, null, null,
                new List<string> { reason });
        }

        return results;
    }

    private static string? Truncate(string? s, int max) =>
        s is null ? null : (s.Length > max ? s[..max] : s);

    public static ImportRowPreview ToPreview(ResolvedRow r) => new(
        RowNumber: r.RowNumber,
        Verdict: r.Verdict,
        FullName: r.FullName,
        Email: r.Email,
        Phone: r.Phone,
        CompanyName: r.CompanyName,
        Messages: r.Messages,
        ExistingLeadId: r.ExistingLeadId);
}

// =====================================================================
// PREVIEW
// =====================================================================

public class PreviewLeadImportHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly IImportSessionStore _sessions;

    public PreviewLeadImportHandler(FlowDbContext db, IImportSessionStore sessions)
    {
        _db = db;
        _sessions = sessions;
    }

    public async Task<ImportPreview> Handle(
        ImportRequest request, string defaultCurrency, string? countryCode, CancellationToken ct = default)
    {
        var session = _sessions.Get(request.SessionId, request.TenantId)
            ?? throw new KeyNotFoundException("That upload has expired. Please upload the file again.");

        if (!request.Mapping.ContainsKey(ImportFields.FullName))
            throw new InvalidOperationException("Choose which column holds the lead's name.");

        var ctx = await ImportValidator.BuildContextAsync(
           _db, request.TenantId, defaultCurrency, countryCode, ct);
        var rows = ImportValidator.Resolve(session.Grid, request.Mapping, ctx, request.Duplicates);

        var errors = rows.Count(r => r.Verdict == RowVerdict.Error);
        var dupes  = rows.Where(r => r.Verdict == RowVerdict.Duplicate).ToList();

        // In-file duplicates are always skipped; only database matches follow
        // the chosen action.
        var dbDupes = dupes.Count(r => r.ExistingLeadId.HasValue);
        var fileDupes = dupes.Count - dbDupes;

        var willUpdate = request.Duplicates == DuplicateAction.Update ? dbDupes : 0;
        var willSkip   = fileDupes + (request.Duplicates == DuplicateAction.Skip ? dbDupes : 0);
        var willImport = rows.Count - errors - willUpdate - willSkip;

        // Quota
        var currentCount = await _db.Leads.CountAsync(
            l => l.TenantId == request.TenantId && !l.IsDeleted, ct);

        var maxLeads = await _db.TenantSettings
            .Where(s => s.TenantId == request.TenantId)
            .Select(s => s.MaxLeads)
            .FirstOrDefaultAsync(ct);

        var notices = BuildNotices(ctx, fileDupes, dbDupes);

        return new ImportPreview(
            SessionId: request.SessionId,
            TotalRows: rows.Count,
            WillImport: willImport,
            WillUpdate: willUpdate,
            WillSkip: willSkip,
            ErrorCount: errors,
            CurrentLeadCount: currentCount,
            MaxLeads: maxLeads,
            ExceedsQuota: maxLeads > 0 && currentCount + willImport > maxLeads,
            Rows: rows.Select(ImportValidator.ToPreview).ToList(),
            Notices: notices);
    }

    private static List<string> BuildNotices(ImportContext ctx, int fileDupes, int dbDupes)
    {
        var notices = new List<string>();

        if (ctx.UnmatchedSources.Count > 0)
            notices.Add($"These lead sources aren't set up in your workspace, so those rows will have no source: " +
                        $"{string.Join(", ", ctx.UnmatchedSources.Take(10))}" +
                        (ctx.UnmatchedSources.Count > 10 ? ", …" : "") +
                        ". Add them under Settings first if you want them kept.");

        if (ctx.UnmatchedOwners.Count > 0)
            notices.Add($"No user found for: {string.Join(", ", ctx.UnmatchedOwners.Take(10))}" +
                        (ctx.UnmatchedOwners.Count > 10 ? ", …" : "") +
                        ". Owners must match a user's email address exactly.");

        if (ctx.UnmatchedCountries.Count > 0)
            notices.Add($"Country not recognised: {string.Join(", ", ctx.UnmatchedCountries.Take(10))}.");

        if (fileDupes > 0)
            notices.Add($"{fileDupes} row(s) repeat someone already listed earlier in the same file — only the first is imported.");

        if (dbDupes > 0)
            notices.Add($"{dbDupes} row(s) match a lead you already have.");

        return notices;
    }
}

// =====================================================================
// COMMIT
// =====================================================================

public class CommitLeadImportHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly IImportSessionStore _sessions;
    private readonly ILogger<CommitLeadImportHandler> _logger;

    public CommitLeadImportHandler(
        FlowDbContext db,
        IImportSessionStore sessions,
        ILogger<CommitLeadImportHandler> logger)
    {
        _db = db;
        _sessions = sessions;
        _logger = logger;
    }

    public async Task<ImportResult> Handle(ImportRequest request, string defaultCurrency, string? countryCode, CancellationToken ct = default)
    {
        var session = _sessions.Get(request.SessionId, request.TenantId)
            ?? throw new KeyNotFoundException("That upload has expired. Please upload the file again.");

        if (!request.Mapping.ContainsKey(ImportFields.FullName))
            throw new InvalidOperationException("Choose which column holds the lead's name.");

        var ctx = await ImportValidator.BuildContextAsync(
             _db, request.TenantId, defaultCurrency, countryCode, ct);
        var rows = ImportValidator.Resolve(session.Grid, request.Mapping, ctx, request.Duplicates);

        // ── Quota, checked BEFORE anything is written ─────────────────
        var currentCount = await _db.Leads.CountAsync(
            l => l.TenantId == request.TenantId && !l.IsDeleted, ct);

        var maxLeads = await _db.TenantSettings
            .Where(s => s.TenantId == request.TenantId)
            .Select(s => s.MaxLeads)
            .FirstOrDefaultAsync(ct);

        var toCreate = rows.Where(r => ShouldCreate(r, request.Duplicates)).ToList();

        if (maxLeads > 0 && currentCount + toCreate.Count > maxLeads)
            throw new InvalidOperationException(
                $"This import would put you at {currentCount + toCreate.Count:N0} leads, over your plan limit of {maxLeads:N0}. " +
                $"You can import {Math.Max(0, maxLeads - currentCount):N0} more.");

        var now = DateTime.UtcNow;
        var imported = 0;
        var updated = 0;

        // ── One transaction: 400 good rows and one bad one means nothing
        //    is written, rather than a half-loaded list nobody can trust.
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        try
        {
            foreach (var r in rows)
            {
                if (r.Verdict == RowVerdict.Error) continue;

                if (r.ExistingLeadId.HasValue && request.Duplicates == DuplicateAction.Update)
                {
                    var existing = await _db.Leads
                        .FirstOrDefaultAsync(l => l.Id == r.ExistingLeadId.Value &&
                                                  l.TenantId == request.TenantId && !l.IsDeleted, ct);
                    if (existing is null) continue;

                    // FILL BLANKS ONLY. Never overwrite something a person
                    // typed, and never touch Status, Score or OwnerUserId —
                    // that is the old importer's worst behaviour.
                    existing.Email       ??= r.Email;
                    existing.Phone       ??= r.Phone;
                    existing.CompanyName ??= r.CompanyName;
                    existing.Address     ??= r.Address;
                    existing.CountryId   ??= r.CountryId;
                    existing.SourceId    ??= r.SourceId;
                    existing.ChannelId   ??= r.ChannelId;
                    existing.VerticalId  ??= r.VerticalId;
                    existing.EstimatedValue ??= r.EstimatedValue;

                    existing.UpdatedAtUtc = now;
                    existing.UpdatedBy = request.ImportedBy;
                    updated++;
                    continue;
                }

                if (!ShouldCreate(r, request.Duplicates)) continue;

                _db.Leads.Add(new Lead
                {
                    Id = Guid.NewGuid(),
                    TenantId = request.TenantId,        // the old importer's bug
                    FullName = r.FullName,
                    Email = r.Email,
                    Phone = r.Phone,
                    CompanyName = r.CompanyName,
                    Address = r.Address,
                    CountryId = r.CountryId,
                    SourceId = r.SourceId,
                    Source = r.SourceText ?? "Import",
                    ChannelId = r.ChannelId,
                    VerticalId = r.VerticalId,
                    Status = r.Status,
                    Score = r.Score,
                    EstimatedValue = r.EstimatedValue,
                    Currency = r.Currency,
                    OwnerUserId = r.OwnerUserId,
                    ContactId = null,                   // created at conversion, not here
                    CompanyId = null,
                    IsConverted = false,
                    CreatedAtUtc = now,
                    CreatedBy = request.ImportedBy,
                    IsDeleted = false
                });

                imported++;
            }

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            _logger.LogError(ex, "Lead import failed for tenant {TenantId} — rolled back", request.TenantId);
            throw new InvalidOperationException(
                "The import failed and nothing was saved. Please check the file and try again.");
        }

        _sessions.Remove(request.SessionId);

        var failed = rows.Where(r => r.Verdict == RowVerdict.Error).ToList();
        var skipped = rows.Count - imported - updated - failed.Count;

        _logger.LogInformation(
            "Imported leads for tenant {TenantId}: {Imported} new, {Updated} updated, {Skipped} skipped, {Failed} failed",
            request.TenantId, imported, updated, skipped, failed.Count);

        return new ImportResult(
            TotalRows: rows.Count,
            Imported: imported,
            Updated: updated,
            Skipped: skipped,
            Failed: failed.Count,
            FailedRows: failed.Select(ImportValidator.ToPreview).ToList());
    }

    private static bool ShouldCreate(ResolvedRow r, string duplicateAction)
    {
        if (r.Verdict == RowVerdict.Error) return false;
        if (r.Verdict != RowVerdict.Duplicate) return true;

        // In-file duplicates are always skipped, whatever the setting.
        if (!r.ExistingLeadId.HasValue) return false;

        return duplicateAction == DuplicateAction.Import;
    }
}
