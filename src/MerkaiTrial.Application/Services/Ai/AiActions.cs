// =====================================================================
// FILE: MerkaiTrial.Application/Services/Ai/AiActions.cs
//
// NEW (125). What the assistant may PREPARE — and nothing more. Three
// tools the AI provider can call:
//
//   propose_task   a follow-up task for the person, on a lead or deal
//   propose_log    log something that happened (a call, meeting, email,
//                  WhatsApp / LINE / SMS) on a lead or deal
//   propose_note   a note on a lead or deal
//
// THE RULES
//   • A proposal DOES NOTHING. It is checked, written to AiProposals and
//     shown under the answer with Confirm / Cancel. Only the person who
//     asked can confirm it, once, within 30 minutes.
//   • Confirm runs the SAME handlers the pages use (CreateActivityHandler,
//     CreateLeadNoteHandler, CreateDealNoteHandler), which check again that
//     the record exists and this person may see it — and it checks the
//     person's Update permission again at that moment.
//   • The sentence the person confirms is written by Merkai from the
//     checked details ("Create a task on lead Jose Reyes: …"), not by the
//     AI.
//   • Tasks are only ever for the person themselves; no assigning others.
//   • At most 3 proposals per question.
//   • Confirmed changes are in the Activity Log (AiActionConfirmed).
//   • Offered only when the admin has allowed records AND the person can
//     update leads or deals.
//
// Also: SaveNoteAsync — the record helpers' "Save as note" button (124's
// card). The person clicked it on the text they can see, so it is done at
// once, with the same permission check and the same audit entry.
// =====================================================================

using System.Globalization;
using System.Text.Json;
using MerkaiTrial.Application.Commands.Activities;
using MerkaiTrial.Application.Commands.Deals;
using MerkaiTrial.Application.Commands.Leads;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Security;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Services.Ai;

/// <summary>A prepared change, as the chat shows it.</summary>
public sealed class AiProposalDto
{
    public Guid Id { get; set; }
    /// <summary>task | log | note</summary>
    public string Kind { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    /// <summary>The record's page.</summary>
    public string Link { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
}

/// <summary>What Confirm / Cancel / Save as note did.</summary>
public sealed class AiActionResultDto
{
    public bool Done { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? Link { get; set; }
}

public sealed class AiActions
{
    public const int MaxPerQuestion = 3;
    public const int MaxSubject = 200;
    public const int MaxNote = 2000;
    public const int MaxOutcome = 500;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private static readonly string[] TaskKinds = { "Task", "Call", "Email", "Meeting", "WhatsApp", "LINE", "SMS" };
    private static readonly string[] LogKinds = { "Call", "Email", "Meeting", "WhatsApp", "LINE", "SMS" };

    private readonly FlowDbContext _db;
    private readonly IRecordScopeService _scope;
    private readonly CreateActivityHandler _createActivity;
    private readonly CreateLeadNoteHandler _leadNote;
    private readonly CreateDealNoteHandler _dealNote;
    private readonly IAuditService _audit;
    private readonly ILogger<AiActions> _logger;
    private int _madeThisRequest;

    public AiActions(FlowDbContext db, IRecordScopeService scope, CreateActivityHandler createActivity,
        CreateLeadNoteHandler leadNote, CreateDealNoteHandler dealNote, IAuditService audit, ILogger<AiActions> logger)
    {
        _db = db; _scope = scope; _createActivity = createActivity; _leadNote = leadNote; _dealNote = dealNote;
        _audit = audit; _logger = logger;
    }

    // ── what the AI is offered ───────────────────────────────────────

    private static Dictionary<string, object> Props(params (string Name, object Schema)[] extra)
    {
        var d = new Dictionary<string, object>
        {
            ["type"] = new { type = "string", @enum = new[] { "lead", "deal" }, description = "The kind of record." },
            ["id"] = new { type = "string", description = "The record's id, from find_records or get_record." },
        };
        foreach (var (n, s) in extra) d[n] = s;
        return d;
    }

    public static IReadOnlyList<AiToolDef> Definitions { get; } = new List<AiToolDef>
    {
        new("propose_task",
            "PREPARE a follow-up task for the person on a lead or deal. Nothing is created until they click Confirm.",
            new
            {
                type = "object",
                properties = Props(
                    ("subject", new { type = "string", description = "What to do, e.g. \"Call Jose about the demo\". Up to 200 characters." }),
                    ("due", new { type = "string", description = "Due date, yyyy-MM-dd, today or later." }),
                    ("kind", new { type = "string", @enum = TaskKinds, description = "Task, Call, Email, Meeting, WhatsApp, LINE or SMS. Default Task." })),
                required = new[] { "type", "id", "subject", "due" },
            }),
        new("propose_log",
            "PREPARE a record of something that already happened (a call, meeting, email or message) on a lead or deal. Nothing is saved until they click Confirm.",
            new
            {
                type = "object",
                properties = Props(
                    ("kind", new { type = "string", @enum = LogKinds, description = "What happened." }),
                    ("subject", new { type = "string", description = "A short title, e.g. \"Discussed pricing\". Up to 200 characters." }),
                    ("outcome", new { type = "string", description = "Optional: what came of it. Up to 500 characters." }),
                    ("date", new { type = "string", description = "Optional: when, yyyy-MM-dd, not in the future. Default today." })),
                required = new[] { "type", "id", "kind", "subject" },
            }),
        new("propose_note",
            "PREPARE a note on a lead or deal. Nothing is saved until they click Confirm.",
            new
            {
                type = "object",
                properties = Props(
                    ("text", new { type = "string", description = "The note. Up to 2000 characters." })),
                required = new[] { "type", "id", "text" },
            }),
    };

    /// <summary>The prepare-tools this person may be offered: only if they can update leads or deals.</summary>
    public static IReadOnlyList<AiToolDef> For(AiCaller who) =>
        who.CanEdit("leads") || who.CanEdit("deals") ? Definitions : Array.Empty<AiToolDef>();

    public static bool IsActionTool(string name) => name.StartsWith("propose_", StringComparison.Ordinal);

    // ── prepare ──────────────────────────────────────────────────────

    private sealed class Payload
    {
        public string? Subject { get; set; }
        public string? ActivityType { get; set; }
        public DateTime? DueUtc { get; set; }
        public DateTime? DateUtc { get; set; }
        public string? Outcome { get; set; }
        public string? Text { get; set; }
    }

    /// <summary>Checks and stores a proposal. Never throws: problems become { error } for the AI.</summary>
    public async Task<(string Json, AiProposalDto? Proposal)> ProposeAsync(AiToolCall call, AiCaller who, CancellationToken ct)
    {
        try
        {
            JsonElement a;
            try { a = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments).RootElement; }
            catch (JsonException) { return (Err("The arguments were not valid JSON."), null); }

            if (_madeThisRequest >= MaxPerQuestion)
                return (Err($"At most {MaxPerQuestion} changes can be prepared in one answer."), null);

            var type = Str(a, "type")?.ToLowerInvariant();
            if (type is not ("lead" or "deal")) return (Err("type must be lead or deal."), null);
            var module = type == "lead" ? "leads" : "deals";
            if (!who.CanEdit(module))
                return (Err($"This person's role doesn't allow them to change {module}, so nothing can be prepared. Tell them to ask their workspace admin."), null);
            if (!Guid.TryParse(Str(a, "id"), out var id)) return (Err("id must be a record id from find_records or get_record."), null);

            var entityType = type == "lead" ? ActivityEntityType.Lead : ActivityEntityType.Deal;
            if (!await ActivityGuards.EntityVisibleAsync(_db, _scope, who.TenantId, entityType, id, ct))
                return (Err($"No {type} with that id that this person can see."), null);
            var name = await NameAsync(type, id, who.TenantId, ct) ?? type;

            var zone = await ZoneAsync(who.TenantId, ct);
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone));
            var kind = call.Name switch { "propose_task" => "task", "propose_log" => "log", _ => "note" };
            var payload = new Payload();
            string summary;

            switch (kind)
            {
                case "task":
                {
                    var subject = Clean(Str(a, "subject"));
                    if (subject is null) return (Err("A task needs a subject."), null);
                    if (subject.Length > MaxSubject) return (Err($"The subject can be up to {MaxSubject} characters."), null);
                    if (!DateOnly.TryParseExact(Str(a, "due"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var due))
                        return (Err("due must be a date like 2026-11-14."), null);
                    if (due < today) return (Err("The due date is in the past."), null);
                    if (due > today.AddDays(366)) return (Err("The due date is more than a year away."), null);
                    var activityType = Pick(Str(a, "kind"), TaskKinds) ?? "Task";
                    payload.Subject = subject;
                    payload.ActivityType = activityType;
                    payload.DueUtc = TimeZoneInfo.ConvertTimeToUtc(due.ToDateTime(new TimeOnly(9, 0)), zone);
                    summary = $"Create a {(activityType == "Task" ? "task" : activityType + " task")} for you on {type} \"{name}\": \"{subject}\" — due {due.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture)}.";
                    break;
                }
                case "log":
                {
                    var activityType = Pick(Str(a, "kind"), LogKinds);
                    if (activityType is null) return (Err("kind must be one of: " + string.Join(", ", LogKinds) + "."), null);
                    var subject = Clean(Str(a, "subject"));
                    if (subject is null) return (Err("A logged activity needs a subject."), null);
                    if (subject.Length > MaxSubject) return (Err($"The subject can be up to {MaxSubject} characters."), null);
                    var outcome = Clean(Str(a, "outcome"));
                    if (outcome is { Length: > MaxOutcome }) return (Err($"The outcome can be up to {MaxOutcome} characters."), null);
                    var day = today;
                    if (Str(a, "date") is { Length: > 0 } ds &&
                        !DateOnly.TryParseExact(ds, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day))
                        return (Err("date must be a date like 2026-11-07."), null);
                    if (day > today) return (Err("Something that happened can't be in the future — prepare a task instead."), null);
                    if (day < today.AddDays(-366)) return (Err("That date is more than a year ago."), null);
                    payload.Subject = subject;
                    payload.ActivityType = activityType;
                    payload.Outcome = outcome;
                    payload.DateUtc = day == today ? null : TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(new TimeOnly(12, 0)), zone);
                    summary = $"Log a {activityType} on {type} \"{name}\" ({(day == today ? "today" : day.ToString("d MMM yyyy", CultureInfo.InvariantCulture))}): \"{subject}\""
                              + (outcome is null ? "." : $" — outcome: \"{Cut(outcome, 120)}\".");
                    break;
                }
                default:
                {
                    var text = Clean(Str(a, "text"));
                    if (text is null) return (Err("A note needs text."), null);
                    if (text.Length > MaxNote) return (Err($"A note can be up to {MaxNote} characters."), null);
                    payload.Text = text;
                    summary = $"Add a note to {type} \"{name}\": \"{Cut(text, 160)}\".";
                    break;
                }
            }

            if (summary.Length > 500) summary = summary[..497] + "…";
            var p = new AiProposal
            {
                TenantId = who.TenantId,
                UserId = who.UserId,
                Kind = kind,
                RecordType = entityType,
                RecordId = id,
                PayloadJson = JsonSerializer.Serialize(payload),
                Summary = summary,
                CreatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.Add(Lifetime),
            };
            _db.AiProposals.Add(p);
            await _db.SaveChangesAsync(ct);
            _madeThisRequest++;

            var dto = new AiProposalDto
            {
                Id = p.Id, Kind = kind, Summary = summary, ExpiresAtUtc = p.ExpiresAtUtc,
                Link = AiDataTools.Link(type, id),
            };
            return (JsonSerializer.Serialize(new
            {
                prepared = true,
                summary,
                status = "NOT done yet. The person sees this with a Confirm button under your answer. Tell them to check it and click Confirm.",
            }), dto);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Preparing an AI action ({Tool}) failed for tenant {TenantId}", call.Name, who.TenantId);
            return (Err("That change could not be prepared just now."), null);
        }
    }

    // ── confirm / cancel ─────────────────────────────────────────────

    /// <summary>Does the prepared change. KeyNotFoundException = not this person's (or not there); AiRefusedException = can't now.</summary>
    public async Task<AiActionResultDto> ConfirmAsync(Guid proposalId, AiCaller who, CancellationToken ct)
    {
        var p = await MineAsync(proposalId, who, ct);
        if (p.Status != AiProposalStatus.Pending)
            throw new AiRefusedException(p.Status == AiProposalStatus.Done ? "This was already done." :
                                         p.Status == AiProposalStatus.Cancelled ? "This was cancelled." :
                                         "This couldn't be done. Ask the assistant again.");
        if (p.ExpiresAtUtc < DateTime.UtcNow)
            throw new AiRefusedException("This suggestion has expired. Ask the assistant again.");

        var module = p.RecordType == ActivityEntityType.Lead ? "leads" : "deals";
        if (!who.CanEdit(module))
            throw new AiRefusedException($"Your role doesn't allow you to change {module}.");

        // Claim it first: two clicks (or two tabs) can't both do it.
        var now = DateTime.UtcNow;
        var claimed = await _db.AiProposals
            .Where(x => x.Id == p.Id && x.TenantId == who.TenantId && x.UserId == who.UserId && x.Status == AiProposalStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, AiProposalStatus.Done).SetProperty(x => x.DecidedAtUtc, now), ct);
        if (claimed != 1) throw new AiRefusedException("This was already done.");

        var payload = JsonSerializer.Deserialize<Payload>(p.PayloadJson) ?? new Payload();
        var me = who.UserId.ToString();
        Guid? resultId = null;
        try
        {
            switch (p.Kind)
            {
                case "task":
                    resultId = (await _createActivity.Handle(new CreateActivityDto(
                        who.TenantId, p.RecordType, p.RecordId, payload.ActivityType ?? "Task", payload.Subject ?? "Follow up",
                        null, null, null, IsTask: true, DueDate: payload.DueUtc, AssignedToUserId: me, CreatedBy: me), ct)).Id;
                    break;
                case "log":
                    resultId = (await _createActivity.Handle(new CreateActivityDto(
                        who.TenantId, p.RecordType, p.RecordId, payload.ActivityType ?? "Call", payload.Subject ?? "Activity",
                        payload.Outcome, null, payload.DateUtc, IsTask: false, CreatedBy: me), ct)).Id;
                    break;
                default:
                    resultId = await NoteAsync(p.RecordType, p.RecordId, payload.Text ?? "", who, ct);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Claimed but not done: mark it failed, so it can't look done.
            await _db.AiProposals.Where(x => x.Id == p.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, AiProposalStatus.Failed), CancellationToken.None);
            if (ex is not (KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException))
                _logger.LogError(ex, "Confirming AI proposal {ProposalId} failed", p.Id);
            throw new AiRefusedException(ex switch
            {
                KeyNotFoundException or UnauthorizedAccessException => "The record can't be found any more, or you can't see it.",
                InvalidOperationException => ex.Message,
                _ => "That couldn't be done just now. Ask the assistant again.",
            });
        }

        if (resultId is { } rid)
            await _db.AiProposals.Where(x => x.Id == p.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ResultId, rid), ct);

        await AuditAsync(p.Kind, p.RecordType, p.RecordId, p.Summary, "assistant", who, ct);
        var type = p.RecordType == ActivityEntityType.Lead ? "lead" : "deal";
        return new AiActionResultDto
        {
            Done = true,
            Message = p.Kind switch { "task" => "Task created.", "log" => "Activity logged.", _ => "Note added." },
            Link = AiDataTools.Link(type, p.RecordId),
        };
    }

    public async Task<AiActionResultDto> CancelAsync(Guid proposalId, AiCaller who, CancellationToken ct)
    {
        var p = await MineAsync(proposalId, who, ct);
        var n = await _db.AiProposals
            .Where(x => x.Id == p.Id && x.UserId == who.UserId && x.Status == AiProposalStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, AiProposalStatus.Cancelled).SetProperty(x => x.DecidedAtUtc, DateTime.UtcNow), ct);
        return new AiActionResultDto { Done = n == 1, Message = n == 1 ? "Cancelled — nothing was changed." : "This was already decided." };
    }

    private async Task<AiProposal> MineAsync(Guid proposalId, AiCaller who, CancellationToken ct)
    {
        // Someone else's proposal, or another workspace's, is "not found" — the same answer as one that doesn't exist.
        var p = await _db.AiProposals.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == proposalId && x.TenantId == who.TenantId && x.UserId == who.UserId, ct);
        return p ?? throw new KeyNotFoundException("Not found.");
    }

    // ── save as note (record helpers) ────────────────────────────────

    public async Task<AiActionResultDto> SaveNoteAsync(AiCaller who, string? type, Guid id, string? text, CancellationToken ct)
    {
        type = (type ?? "").Trim().ToLowerInvariant();
        if (type is not ("lead" or "deal")) throw new AiRefusedException("Notes can be saved on leads and deals.");
        var module = type == "lead" ? "leads" : "deals";
        if (!who.CanEdit(module)) throw new AiRefusedException($"Your role doesn't allow you to change {module}.");
        var clean = Clean(text);
        if (clean is null) throw new AiRefusedException("There is nothing to save.");
        if (clean.Length > 4000) throw new AiRefusedException("That is too long for a note (4,000 characters).");

        var entityType = type == "lead" ? ActivityEntityType.Lead : ActivityEntityType.Deal;
        try
        {
            await NoteAsync(entityType, id, clean, who, ct);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or UnauthorizedAccessException)
        {
            throw new AiRefusedException($"This {type} can't be found, or you can't see it.");
        }
        var name = await NameAsync(type, id, who.TenantId, ct) ?? type;
        await AuditAsync("note", entityType, id, $"Saved an AI draft as a note on {type} \"{name}\": \"{Cut(clean, 160)}\".", "helper", who, ct);
        return new AiActionResultDto { Done = true, Message = "Saved as a note.", Link = AiDataTools.Link(type, id) };
    }

    // ── plumbing ─────────────────────────────────────────────────────

    private async Task<Guid?> NoteAsync(string recordType, Guid id, string text, AiCaller who, CancellationToken ct)
    {
        if (recordType == ActivityEntityType.Lead)
            return (await _leadNote.Handle(new CreateLeadNoteDto(who.TenantId, id, text, who.UserId.ToString()), ct)).Id;

        await _dealNote.HandleAsync(new CreateDealNoteDto { TenantId = who.TenantId, DealId = id, Note = text, CreatedBy = who.UserId.ToString() });
        return null;
    }

    private async Task AuditAsync(string kind, string recordType, Guid recordId, string summary, string source, AiCaller who, CancellationToken ct)
    {
        try
        {
            await _audit.WriteAsync(AuditAction.AiActionConfirmed, recordType, recordId, who.TenantId,
                new { kind, recordType, recordId, summary, source }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recording an AI action for tenant {TenantId} failed", who.TenantId);
        }
    }

    private async Task<string?> NameAsync(string type, Guid id, Guid tenantId, CancellationToken ct) =>
        type == "lead"
            ? await _db.Leads.AsNoTracking().Where(l => l.Id == id && l.TenantId == tenantId).Select(l => l.FullName).FirstOrDefaultAsync(ct)
            : await _db.Deals.AsNoTracking().Where(d => d.Id == id && d.TenantId == tenantId).Select(d => d.Title).FirstOrDefaultAsync(ct);

    private async Task<TimeZoneInfo> ZoneAsync(Guid tenantId, CancellationToken ct)
    {
        var id = await _db.Tenants.AsNoTracking().IgnoreQueryFilters().Where(t => t.Id == tenantId).Select(t => t.Timezone).FirstOrDefaultAsync(ct);
        try { return string.IsNullOrWhiteSpace(id) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception) { return TimeZoneInfo.Utc; }
    }

    private static string Err(string message) => JsonSerializer.Serialize(new { error = message });

    private static string? Str(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        // No control characters (other than line breaks) in anything that is saved.
        var chars = s.Trim().Where(c => !char.IsControl(c) || c == '\n' || c == '\r' || c == '\t').ToArray();
        var t = new string(chars).Trim();
        return t.Length == 0 ? null : t;
    }

    private static string? Pick(string? value, string[] allowed) =>
        value is null ? null : allowed.FirstOrDefault(x => string.Equals(x, value.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";
}
