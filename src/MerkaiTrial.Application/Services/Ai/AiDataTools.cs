// =====================================================================
// FILE: MerkaiTrial.Application/Services/Ai/AiDataTools.cs
//
// NEW (123). What the assistant may LOOK AT — six read-only tools the AI
// provider can ask for while answering a question:
//
//   find_records      search leads, deals, contacts, companies, quotes or
//                     invoices by name / number
//   get_record        one record's details, its recent activity and open tasks
//   my_tasks          the person's own open tasks (overdue, today, this week)
//   pipeline_summary  open deals by stage, won and lost this month, closing soon
//   lead_summary      leads by status, new this week / month, the hottest few
//   unpaid_invoices   invoices with money still owed, overdue first
//
// THE RULES — the same ones the rest of Merkai follows, applied here again
// rather than trusted to the AI:
//   • Read only. Nothing here can change anything.
//   • The workspace and the person come from the SIGN-IN (AiCaller, built
//     by the API controller), never from the AI's arguments.
//   • A type needs the person's own read permission (leads.read, deals.read,
//     contacts.read, companies.read, quotes.read, invoices.read) — checked
//     with the same policies the API's pages use. Without it the tool says
//     so, and the AI is told to tell the person.
//   • Leads and deals follow the person's RECORD SCOPE (Own / Team / All,
//     RecordScopeService); quotes and invoices follow the deal they are on.
//     A record outside it is "not found" — the same answer as a record that
//     doesn't exist, exactly as the API does.
//   • Sent to the AI: names, statuses, amounts, dates, owners' names,
//     activity subjects. NOT sent: email addresses, phone numbers, street
//     addresses, tax ids, notes' bodies — the assistant doesn't need them
//     to answer, so they never leave Merkai.
//   • Results are small (a few rows each, capped in size) and every one
//     carries the record's page ("link"), which the chat shows as a link.
// =====================================================================

using System.Globalization;
using System.Text.Json;
using MerkaiTrial.Application.Security;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Domain.Enums;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Services.Ai;

/// <summary>Who is asking — from the sign-in, never from the request.</summary>
public sealed class AiCaller
{
    public Guid TenantId { get; init; }
    public Guid UserId { get; init; }
    public string? FirstName { get; init; }

    /// <summary>Modules (lower case: leads, deals, contacts, companies, quotes, invoices) this person may read.</summary>
    public IReadOnlySet<string> CanRead { get; init; } = new HashSet<string>();

    public bool Can(string module) => CanRead.Contains(module);

    /// <summary>125. Modules (leads, deals) this person may UPDATE — for the prepare-tools and Confirm.</summary>
    public IReadOnlySet<string> CanChange { get; init; } = new HashSet<string>();

    public bool CanEdit(string module) => CanChange.Contains(module);
}

public sealed class AiDataTools
{
    public const int MaxResultChars = 8000;
    private const int ListRows = 8;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly string[] Types = { "lead", "deal", "contact", "company", "quote", "invoice" };

    private static readonly Dictionary<string, string> ModuleOf = new()
    {
        ["lead"] = "leads", ["deal"] = "deals", ["contact"] = "contacts",
        ["company"] = "companies", ["quote"] = "quotes", ["invoice"] = "invoices",
    };

    private readonly FlowDbContext _db;
    private readonly IRecordScopeService _scope;
    private readonly ILogger<AiDataTools> _logger;

    // Per question (the service is scoped): the workspace's clock, stage and status names, owners.
    private TimeZoneInfo? _zone;
    private Dictionary<string, (string Name, StageCategory Category)>? _stages;
    private Dictionary<string, string>? _statuses;
    private Guid _clockTenant;   // the workspace whose clock "today" is read in (set by RunAsync)

    public AiDataTools(FlowDbContext db, IRecordScopeService scope, ILogger<AiDataTools> logger)
    {
        _db = db; _scope = scope; _logger = logger;
    }

    // ── what the AI is offered ───────────────────────────────────────

    private static object TypeParam(string description) => new
    {
        type = "string",
        @enum = Types,
        description,
    };

    public static IReadOnlyList<AiToolDef> Definitions { get; } = new List<AiToolDef>
    {
        new("find_records",
            "Search this workspace's records that the person can see, by name, company or number. Returns up to 8 matches with their id.",
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["type"] = TypeParam("What kind of record to search."),
                    ["text"] = new { type = "string", description = "Part of the name, company name, deal title, or quote / invoice number. At least 2 characters." },
                },
                required = new[] { "type", "text" },
            }),
        new("get_record",
            "One record's details, with its recent activity and open tasks. Use an id from find_records or another tool.",
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["type"] = TypeParam("What kind of record it is."),
                    ["id"] = new { type = "string", description = "The record's id." },
                },
                required = new[] { "type", "id" },
            }),
        new("my_tasks",
            "The person's own open tasks (follow-ups and reminders assigned to them).",
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["when"] = new { type = "string", @enum = new[] { "overdue", "today", "week", "all" }, description = "overdue; today (and overdue); week = the next 7 days (and overdue); all open tasks." },
                },
            }),
        new("pipeline_summary",
            "The deals the person can see: open deals by stage with their value, deals won and lost this month, and open deals due to close in the next 30 days.",
            new { type = "object", properties = new Dictionary<string, object>() }),
        new("lead_summary",
            "The leads the person can see: how many in each status, how many are new this week and this month, and the highest-scoring open leads.",
            new { type = "object", properties = new Dictionary<string, object>() }),
        new("unpaid_invoices",
            "Invoices the person can see that still have money owed, overdue ones first, with the total owed.",
            new { type = "object", properties = new Dictionary<string, object>() }),
    };

    // ── running one call ─────────────────────────────────────────────

    /// <summary>Runs one tool call. Never throws: a problem becomes { error } for the AI to pass on.</summary>
    public async Task<string> RunAsync(AiToolCall call, AiCaller who, CancellationToken ct)
    {
        _clockTenant = who.TenantId;
        object result;
        try
        {
            JsonElement args;
            try { args = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments).RootElement; }
            catch (JsonException) { return Serialize(Error("The tool's arguments were not valid JSON.")); }

            result = call.Name switch
            {
                "find_records" => await FindAsync(Str(args, "type"), Str(args, "text"), who, ct),
                "get_record" => await GetAsync(Str(args, "type"), Str(args, "id"), who, ct),
                "my_tasks" => await MyTasksAsync(Str(args, "when") ?? "week", who, ct),
                "pipeline_summary" => await PipelineAsync(who, ct),
                "lead_summary" => await LeadSummaryAsync(who, ct),
                "unpaid_invoices" => await UnpaidAsync(who, ct),
                _ => Error($"There is no tool called {call.Name}."),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI tool {Tool} failed for tenant {TenantId}", call.Name, who.TenantId);
            result = Error("That information could not be loaded just now.");
        }

        var json = Serialize(result);
        return json.Length <= MaxResultChars ? json : Serialize(Error("That is too much to send at once. Ask about something narrower."));
    }

    private static string Serialize(object o) => JsonSerializer.Serialize(o, Json);

    private static object Error(string message) => new { error = message };

    private static object NoAccess(string what) =>
        Error($"This person's role doesn't allow them to see {what} in Merkai, so the assistant can't look them up. Tell them to ask their workspace admin if they need access.");

    private static object NotFound(string type) =>
        Error($"No {type} with that id that this person can see.");

    private static string? Str(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()?.Trim()
            : null;

    // ── find_records ─────────────────────────────────────────────────

    private async Task<object> FindAsync(string? type, string? text, AiCaller who, CancellationToken ct)
    {
        type = type?.ToLowerInvariant();
        if (type is null || !ModuleOf.TryGetValue(type, out var module))
            return Error("type must be one of: " + string.Join(", ", Types) + ".");
        if (!who.Can(module)) return NoAccess(module);
        if (string.IsNullOrWhiteSpace(text) || text.Length < 2) return Error("Give at least 2 characters to search for.");
        if (text.Length > 100) text = text[..100];
        var t = who.TenantId;

        switch (type)
        {
            case "lead":
            {
                var access = await _scope.GetAsync(RecordModules.Leads, ct);
                var rows = await _db.Leads.AsNoTracking()
                    .Where(l => l.TenantId == t && !l.IsDeleted)
                    .VisibleTo(access)
                    .Where(l => l.FullName.Contains(text) || (l.CompanyName != null && l.CompanyName.Contains(text)))
                    .OrderByDescending(l => l.UpdatedAtUtc ?? l.CreatedAtUtc)
                    .Take(ListRows)
                    .Select(l => new { l.Id, l.FullName, l.CompanyName, l.Status, l.IsConverted, l.EstimatedValue, l.Currency, l.Score, l.OwnerUserId })
                    .ToListAsync(ct);
                var owners = await OwnersAsync(rows.Select(r => r.OwnerUserId), ct);
                var statuses = await StatusesAsync(ct);
                return new
                {
                    type,
                    found = rows.Count,
                    records = rows.Select(r => new
                    {
                        id = r.Id, name = r.FullName, company = r.CompanyName,
                        status = r.IsConverted ? "Converted" : statuses.GetValueOrDefault(r.Status, r.Status),
                        value = Money(r.EstimatedValue, r.Currency), score = r.Score,
                        owner = Owner(owners, r.OwnerUserId), link = Link("lead", r.Id),
                    }),
                };
            }
            case "deal":
            {
                var access = await _scope.GetAsync(RecordModules.Deals, ct);
                var rows = await _db.Deals.AsNoTracking()
                    .Where(d => d.TenantId == t && !d.IsDeleted)
                    .VisibleTo(access)
                    .Where(d => d.Title.Contains(text) || (d.Company != null && d.Company.Name.Contains(text)))
                    .OrderByDescending(d => d.UpdatedAtUtc)
                    .Take(ListRows)
                    .Select(d => new { d.Id, d.Title, Company = d.Company != null ? d.Company.Name : null, d.Stage, d.ExpectedValue, d.Currency, d.ExpectedCloseDateUtc, d.OwnerUserId })
                    .ToListAsync(ct);
                var owners = await OwnersAsync(rows.Select(r => r.OwnerUserId), ct);
                var stages = await StagesAsync(ct);
                var list = new List<object>();
                foreach (var r in rows)
                    list.Add(new
                    {
                        id = r.Id, title = r.Title, company = r.Company, stage = StageName(stages, r.Stage),
                        value = Money(r.ExpectedValue, r.Currency), expectedClose = await DayAsync(r.ExpectedCloseDateUtc, ct),
                        owner = Owner(owners, r.OwnerUserId), link = Link("deal", r.Id),
                    });
                return new { type, found = rows.Count, records = list };
            }
            case "contact":
            {
                var rows = await _db.Contacts.AsNoTracking()
                    .Where(c => c.TenantId == t && !c.IsDeleted)
                    .Where(c => (c.FirstName + " " + (c.LastName ?? "")).Contains(text) ||
                                (c.Company != null && c.Company.Name.Contains(text)))
                    .OrderBy(c => c.FirstName).ThenBy(c => c.LastName)
                    .Take(ListRows)
                    .Select(c => new { c.Id, c.FirstName, c.LastName, c.JobTitle, Company = c.Company != null ? c.Company.Name : null })
                    .ToListAsync(ct);
                return new
                {
                    type,
                    found = rows.Count,
                    records = rows.Select(r => new
                    {
                        id = r.Id, name = $"{r.FirstName} {r.LastName}".Trim(), jobTitle = r.JobTitle, company = r.Company,
                        link = Link("contact", r.Id),
                    }),
                };
            }
            case "company":
            {
                var rows = await _db.Companies.AsNoTracking()
                    .Where(c => c.TenantId == t && !c.IsDeleted && c.Name.Contains(text))
                    .OrderBy(c => c.Name)
                    .Take(ListRows)
                    .Select(c => new { c.Id, c.Name, c.Country, c.Vertical })
                    .ToListAsync(ct);
                return new
                {
                    type,
                    found = rows.Count,
                    records = rows.Select(r => new { id = r.Id, name = r.Name, country = r.Country, industry = r.Vertical, link = Link("company", r.Id) }),
                };
            }
            case "quote":
            {
                var access = await _scope.GetAsync(RecordModules.Deals, ct);
                var rows = await _db.Quotes.AsNoTracking()
                    .Where(q => q.TenantId == t && !q.IsDeleted)
                    .WithVisibleDeal(_db, access)
                    .Where(q => q.Number.Contains(text) || q.Deal.Title.Contains(text))
                    .OrderByDescending(q => q.IssueDateUtc)
                    .Take(ListRows)
                    .Select(q => new { q.Id, q.Number, q.Status, q.GrandTotal, q.Currency, q.ExpiresAtUtc, Deal = q.Deal.Title })
                    .ToListAsync(ct);
                var list = new List<object>();
                foreach (var r in rows)
                    list.Add(new { id = r.Id, number = r.Number, status = Words(r.Status.ToString()), total = Money(r.GrandTotal, r.Currency),
                                   expires = await DayAsync(r.ExpiresAtUtc, ct), deal = r.Deal, link = Link("quote", r.Id) });
                return new { type, found = rows.Count, records = list };
            }
            default: // invoice
            {
                var access = await _scope.GetAsync(RecordModules.Deals, ct);
                var rows = await _db.Invoices.AsNoTracking()
                    .Where(i => i.TenantId == t && !i.IsDeleted)
                    .WithVisibleDeal(_db, access)
                    .Where(i => i.Number.Contains(text) ||
                                (i.Deal != null && i.Deal.Title.Contains(text)) ||
                                (i.Quote != null && i.Quote.Deal.Title.Contains(text)))
                    .OrderByDescending(i => i.IssueDateUtc)
                    .Take(ListRows)
                    .Select(i => new { i.Id, i.Number, i.Status, i.Total, i.Balance, i.Currency, i.DueDateUtc,
                                       Deal = i.Deal != null ? i.Deal.Title : i.Quote != null ? i.Quote.Deal.Title : null })
                    .ToListAsync(ct);
                var list = new List<object>();
                foreach (var r in rows)
                    list.Add(new { id = r.Id, number = r.Number, status = Words(r.Status.ToString()), total = Money(r.Total, r.Currency),
                                   owed = Money(r.Balance, r.Currency), due = await DayAsync(r.DueDateUtc, ct), deal = r.Deal, link = Link("invoice", r.Id) });
                return new { type, found = rows.Count, records = list };
            }
        }
    }

    // ── get_record ───────────────────────────────────────────────────

    private async Task<object> GetAsync(string? type, string? idText, AiCaller who, CancellationToken ct)
    {
        type = type?.ToLowerInvariant();
        if (type is null || !ModuleOf.TryGetValue(type, out var module))
            return Error("type must be one of: " + string.Join(", ", Types) + ".");
        if (!who.Can(module)) return NoAccess(module);
        if (!Guid.TryParse(idText, out var id)) return Error("id must be a record id from another tool.");
        var t = who.TenantId;

        switch (type)
        {
            case "lead":
            {
                var access = await _scope.GetAsync(RecordModules.Leads, ct);
                var l = await _db.Leads.AsNoTracking()
                    .Where(x => x.Id == id && x.TenantId == t && !x.IsDeleted)
                    .VisibleTo(access)
                    .Select(x => new { x.Id, x.FullName, x.CompanyName, x.Status, x.IsConverted, x.DealId, x.Score, x.EstimatedValue, x.Currency,
                                       x.Source, x.OwnerUserId, x.CreatedAtUtc, x.UpdatedAtUtc })
                    .FirstOrDefaultAsync(ct);
                if (l is null) return NotFound(type);
                var owners = await OwnersAsync(new[] { l.OwnerUserId }, ct);
                var statuses = await StatusesAsync(ct);
                return new
                {
                    type, id = l.Id, name = l.FullName, company = l.CompanyName,
                    status = l.IsConverted ? "Converted" : statuses.GetValueOrDefault(l.Status, l.Status),
                    convertedToDeal = l.IsConverted && l.DealId is { } dId && who.Can("deals") ? Link("deal", dId) : null,
                    score = l.Score, value = Money(l.EstimatedValue, l.Currency), source = l.Source,
                    owner = Owner(owners, l.OwnerUserId),
                    created = await DayAsync(l.CreatedAtUtc, ct), lastChanged = await DayAsync(l.UpdatedAtUtc, ct),
                    recentActivity = await ActivityAsync("Lead", l.Id, t, ct),
                    openTasks = await OpenTasksOnAsync("Lead", l.Id, t, ct),
                    link = Link("lead", l.Id),
                };
            }
            case "deal":
            {
                var access = await _scope.GetAsync(RecordModules.Deals, ct);
                var d = await _db.Deals.AsNoTracking()
                    .Where(x => x.Id == id && x.TenantId == t && !x.IsDeleted)
                    .VisibleTo(access)
                    .Select(x => new
                    {
                        x.Id, x.Title, x.Description, x.Stage, x.Probability, x.ExpectedValue, x.ActualValue, x.Currency,
                        x.ExpectedCloseDateUtc, x.ActualCloseDateUtc, x.LostReason, x.OwnerUserId, x.CreatedAtUtc, x.UpdatedAtUtc,
                        Company = x.Company != null ? x.Company.Name : null,
                        Contact = x.Contact != null ? (x.Contact.FirstName + " " + (x.Contact.LastName ?? "")) : null,
                    })
                    .FirstOrDefaultAsync(ct);
                if (d is null) return NotFound(type);
                var owners = await OwnersAsync(new[] { d.OwnerUserId }, ct);
                var stages = await StagesAsync(ct);
                var quotes = who.Can("quotes")
                    ? await _db.Quotes.AsNoTracking().Where(q => q.DealId == d.Id && q.TenantId == t && !q.IsDeleted)
                        .OrderByDescending(q => q.IssueDateUtc).Take(5)
                        .Select(q => new { q.Id, q.Number, q.Status, q.GrandTotal, q.Currency }).ToListAsync(ct)
                    : null;
                return new
                {
                    type, id = d.Id, title = d.Title,
                    description = Clip(d.Description, 300),
                    company = d.Company, contact = d.Contact?.Trim(),
                    stage = StageName(stages, d.Stage), probability = d.Probability,
                    value = Money(d.ExpectedValue, d.Currency), actualValue = Money(d.ActualValue, d.Currency),
                    expectedClose = await DayAsync(d.ExpectedCloseDateUtc, ct), closed = await DayAsync(d.ActualCloseDateUtc, ct),
                    lostReason = d.LostReason, owner = Owner(owners, d.OwnerUserId),
                    created = await DayAsync(d.CreatedAtUtc, ct), lastChanged = await DayAsync(d.UpdatedAtUtc, ct),
                    quotes = quotes?.Select(q => new { number = q.Number, status = Words(q.Status.ToString()), total = Money(q.GrandTotal, q.Currency), link = Link("quote", q.Id) }),
                    recentActivity = await ActivityAsync("Deal", d.Id, t, ct),
                    openTasks = await OpenTasksOnAsync("Deal", d.Id, t, ct),
                    link = Link("deal", d.Id),
                };
            }
            case "contact":
            {
                var c = await _db.Contacts.AsNoTracking()
                    .Where(x => x.Id == id && x.TenantId == t && !x.IsDeleted)
                    .Select(x => new { x.Id, x.FirstName, x.LastName, x.JobTitle, x.City, x.Country, x.CompanyId, Company = x.Company != null ? x.Company.Name : null })
                    .FirstOrDefaultAsync(ct);
                if (c is null) return NotFound(type);
                object? deals = null;
                if (who.Can("deals"))
                {
                    var access = await _scope.GetAsync(RecordModules.Deals, ct);
                    var stages = await StagesAsync(ct);
                    var rows = await _db.Deals.AsNoTracking().Where(x => x.ContactId == c.Id && x.TenantId == t && !x.IsDeleted)
                        .VisibleTo(access).OrderByDescending(x => x.UpdatedAtUtc).Take(5)
                        .Select(x => new { x.Id, x.Title, x.Stage, x.ExpectedValue, x.Currency }).ToListAsync(ct);
                    deals = rows.Select(x => new { title = x.Title, stage = StageName(stages, x.Stage), value = Money(x.ExpectedValue, x.Currency), link = Link("deal", x.Id) });
                }
                return new
                {
                    type, id = c.Id, name = $"{c.FirstName} {c.LastName}".Trim(), jobTitle = c.JobTitle, company = c.Company,
                    companyLink = c.CompanyId is { } co && who.Can("companies") ? Link("company", co) : null,
                    city = c.City, country = c.Country, deals,
                    recentActivity = await ActivityAsync("Contact", c.Id, t, ct),
                    link = Link("contact", c.Id),
                };
            }
            case "company":
            {
                var c = await _db.Companies.AsNoTracking()
                    .Where(x => x.Id == id && x.TenantId == t && !x.IsDeleted)
                    .Select(x => new { x.Id, x.Name, x.Country, x.Vertical, x.CreatedAtUtc })
                    .FirstOrDefaultAsync(ct);
                if (c is null) return NotFound(type);
                var contacts = who.Can("contacts")
                    ? await _db.Contacts.AsNoTracking().Where(x => x.CompanyId == c.Id && x.TenantId == t && !x.IsDeleted)
                        .OrderBy(x => x.FirstName).Take(8)
                        .Select(x => new { x.Id, x.FirstName, x.LastName, x.JobTitle }).ToListAsync(ct)
                    : null;
                object? deals = null;
                if (who.Can("deals"))
                {
                    var access = await _scope.GetAsync(RecordModules.Deals, ct);
                    var stages = await StagesAsync(ct);
                    var rows = await _db.Deals.AsNoTracking().Where(x => x.CompanyId == c.Id && x.TenantId == t && !x.IsDeleted)
                        .VisibleTo(access).OrderByDescending(x => x.UpdatedAtUtc).Take(8)
                        .Select(x => new { x.Id, x.Title, x.Stage, x.ExpectedValue, x.Currency }).ToListAsync(ct);
                    deals = rows.Select(x => new { title = x.Title, stage = StageName(stages, x.Stage), value = Money(x.ExpectedValue, x.Currency), link = Link("deal", x.Id) });
                }
                return new
                {
                    type, id = c.Id, name = c.Name, country = c.Country, industry = c.Vertical,
                    contacts = contacts?.Select(x => new { name = $"{x.FirstName} {x.LastName}".Trim(), jobTitle = x.JobTitle, link = Link("contact", x.Id) }),
                    deals,
                    recentActivity = await ActivityAsync("Company", c.Id, t, ct),
                    link = Link("company", c.Id),
                };
            }
            case "quote":
            {
                var access = await _scope.GetAsync(RecordModules.Deals, ct);
                var q = await _db.Quotes.AsNoTracking()
                    .Where(x => x.Id == id && x.TenantId == t && !x.IsDeleted)
                    .WithVisibleDeal(_db, access)
                    .Select(x => new { x.Id, x.Number, x.Status, x.Subtotal, x.DiscountTotal, x.TaxTotal, x.GrandTotal, x.Currency,
                                       x.IssueDateUtc, x.ExpiresAtUtc, x.DealId, Deal = x.Deal.Title,
                                       Lines = x.Items.Where(i => !i.IsDeleted).Count() })
                    .FirstOrDefaultAsync(ct);
                if (q is null) return NotFound(type);
                return new
                {
                    type, id = q.Id, number = q.Number, status = Words(q.Status.ToString()),
                    subtotal = Money(q.Subtotal, q.Currency), discount = Money(q.DiscountTotal, q.Currency),
                    tax = Money(q.TaxTotal, q.Currency), total = Money(q.GrandTotal, q.Currency), lines = q.Lines,
                    issued = await DayAsync(q.IssueDateUtc, ct), expires = await DayAsync(q.ExpiresAtUtc, ct),
                    deal = q.Deal, dealLink = who.Can("deals") ? Link("deal", q.DealId) : null,
                    link = Link("quote", q.Id),
                };
            }
            default: // invoice
            {
                var access = await _scope.GetAsync(RecordModules.Deals, ct);
                var i = await _db.Invoices.AsNoTracking()
                    .Where(x => x.Id == id && x.TenantId == t && !x.IsDeleted)
                    .WithVisibleDeal(_db, access)
                    .Select(x => new { x.Id, x.Number, x.Status, x.Total, x.Balance, x.Currency, x.IssueDateUtc, x.DueDateUtc,
                                       x.MilestoneName,
                                       DealId = x.DealId ?? (x.Quote != null ? (Guid?)x.Quote.DealId : null),
                                       Deal = x.Deal != null ? x.Deal.Title : x.Quote != null ? x.Quote.Deal.Title : null,
                                       Quote = x.Quote != null ? x.Quote.Number : null })
                    .FirstOrDefaultAsync(ct);
                if (i is null) return NotFound(type);
                var today = await TodayAsync(ct);
                var dueDay = await DayOnlyAsync(i.DueDateUtc, ct);
                return new
                {
                    type, id = i.Id, number = i.Number, status = Words(i.Status.ToString()),
                    total = Money(i.Total, i.Currency), owed = Money(i.Balance, i.Currency),
                    issued = await DayAsync(i.IssueDateUtc, ct), due = await DayAsync(i.DueDateUtc, ct),
                    daysOverdue = i.Balance > 0 && dueDay is { } dd && dd < today ? today.DayNumber - dd.DayNumber : (int?)null,
                    milestone = i.MilestoneName, quote = i.Quote, deal = i.Deal,
                    dealLink = i.DealId is { } did && who.Can("deals") ? Link("deal", did) : null,
                    link = Link("invoice", i.Id),
                };
            }
        }
    }

    // ── my_tasks ─────────────────────────────────────────────────────

    private async Task<object> MyTasksAsync(string when, AiCaller who, CancellationToken ct)
    {
        if (!who.Can("leads") && !who.Can("deals")) return NoAccess("tasks");
        when = when.ToLowerInvariant();
        if (when is not ("overdue" or "today" or "week" or "all")) when = "week";

        var zone = await ZoneAsync(ct);
        var today = await TodayAsync(ct);
        var startOfToday = TimeZoneInfo.ConvertTimeToUtc(today.ToDateTime(TimeOnly.MinValue), zone);
        DateTime? until = when switch
        {
            "overdue" => startOfToday,
            "today" => startOfToday.AddDays(1),
            "week" => startOfToday.AddDays(8),
            _ => null,
        };

        var me = who.UserId.ToString();
        var t = who.TenantId;
        var q = _db.Activities.AsNoTracking()
            .Where(a => a.TenantId == t && a.IsTask && !a.IsCompleted && !a.IsDeleted && a.AssignedToUserId == me);
        if (until is { } u) q = q.Where(a => a.DueDate < u);

        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(a => a.DueDate).ThenBy(a => a.Id).Take(15)
            .Select(a => new { a.Subject, a.DueDate, a.EntityType, a.EntityId, a.ActivityType })
            .ToListAsync(ct);

        // What each task is on — only records this person can see are named.
        var leadIds = rows.Where(r => r.EntityType == "Lead").Select(r => r.EntityId).Distinct().ToList();
        var dealIds = rows.Where(r => r.EntityType == "Deal").Select(r => r.EntityId).Distinct().ToList();
        var leadNames = new Dictionary<Guid, string>();
        var dealNames = new Dictionary<Guid, string>();
        if (leadIds.Count > 0 && who.Can("leads"))
        {
            var access = await _scope.GetAsync(RecordModules.Leads, ct);
            leadNames = await _db.Leads.AsNoTracking().Where(l => leadIds.Contains(l.Id) && l.TenantId == t && !l.IsDeleted)
                .VisibleTo(access).ToDictionaryAsync(l => l.Id, l => l.FullName, ct);
        }
        if (dealIds.Count > 0 && who.Can("deals"))
        {
            var access = await _scope.GetAsync(RecordModules.Deals, ct);
            dealNames = await _db.Deals.AsNoTracking().Where(d => dealIds.Contains(d.Id) && d.TenantId == t && !d.IsDeleted)
                .VisibleTo(access).ToDictionaryAsync(d => d.Id, d => d.Title, ct);
        }

        var list = new List<object>();
        foreach (var r in rows)
        {
            var dueDay = await DayOnlyAsync(r.DueDate, ct);
            string? on = null, link = null;
            if (r.EntityType == "Lead" && leadNames.TryGetValue(r.EntityId, out var ln)) { on = "Lead: " + ln; link = Link("lead", r.EntityId); }
            else if (r.EntityType == "Deal" && dealNames.TryGetValue(r.EntityId, out var dn)) { on = "Deal: " + dn; link = Link("deal", r.EntityId); }
            list.Add(new
            {
                task = r.Subject, kind = r.ActivityType, due = await DayAsync(r.DueDate, ct),
                overdue = dueDay is { } d && d < today, on, link,
            });
        }

        return new
        {
            today = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            showing = when, totalOpen = total, tasks = list,
            note = total > list.Count ? $"Showing the first {list.Count}; the Tasks page (/Tasks) has them all." : null,
        };
    }

    // ── pipeline_summary ─────────────────────────────────────────────

    private async Task<object> PipelineAsync(AiCaller who, CancellationToken ct)
    {
        if (!who.Can("deals")) return NoAccess("deals");
        var access = await _scope.GetAsync(RecordModules.Deals, ct);
        var stages = await StagesAsync(ct);
        var t = who.TenantId;
        var zone = await ZoneAsync(ct);
        var today = await TodayAsync(ct);
        var monthStart = TimeZoneInfo.ConvertTimeToUtc(new DateTime(today.Year, today.Month, 1), zone);

        var deals = _db.Deals.AsNoTracking().Where(d => d.TenantId == t && !d.IsDeleted).VisibleTo(access);

        var byStage = await deals
            .GroupBy(d => new { d.Stage, d.Currency })
            .Select(g => new { g.Key.Stage, g.Key.Currency, Count = g.Count(), Total = g.Sum(x => x.ExpectedValue) })
            .ToListAsync(ct);

        bool IsOpen(string key) => !stages.TryGetValue(key, out var s) || s.Category == StageCategory.Open;
        var wonKeys = stages.Where(s => s.Value.Category == StageCategory.Won).Select(s => s.Key).ToList();
        var lostKeys = stages.Where(s => s.Value.Category == StageCategory.Lost).Select(s => s.Key).ToList();

        var open = byStage.Where(s => IsOpen(s.Stage))
            .GroupBy(s => s.Stage)
            .OrderBy(g => stages.TryGetValue(g.Key, out _) ? 0 : 1)
            .Select(g => new
            {
                stage = StageName(stages, g.Key),
                deals = g.Sum(x => x.Count),
                value = string.Join(" + ", g.Select(x => Money(x.Total, x.Currency))),
            }).ToList();

        var wonThisMonth = await deals.Where(d => wonKeys.Contains(d.Stage) && d.ActualCloseDateUtc >= monthStart)
            .GroupBy(d => d.Currency).Select(g => new { Currency = g.Key, Count = g.Count(), Total = g.Sum(x => x.ActualValue ?? x.ExpectedValue) })
            .ToListAsync(ct);
        var lostThisMonth = await deals.CountAsync(d => lostKeys.Contains(d.Stage) && d.ActualCloseDateUtc >= monthStart, ct);

        var soon = TimeZoneInfo.ConvertTimeToUtc(today.AddDays(31).ToDateTime(TimeOnly.MinValue), zone);
        var startToday = TimeZoneInfo.ConvertTimeToUtc(today.ToDateTime(TimeOnly.MinValue), zone);
        var openKeys = stages.Where(s => s.Value.Category == StageCategory.Open).Select(s => s.Key).ToList();
        var closingRows = await deals.Where(d => openKeys.Contains(d.Stage) && d.ExpectedCloseDateUtc < soon)
            .OrderBy(d => d.ExpectedCloseDateUtc).Take(8)
            .Select(d => new { d.Id, d.Title, d.Stage, d.ExpectedValue, d.Currency, d.ExpectedCloseDateUtc })
            .ToListAsync(ct);
        var pastClose = await deals.CountAsync(d => openKeys.Contains(d.Stage) && d.ExpectedCloseDateUtc < startToday, ct);

        var closing = new List<object>();
        foreach (var d in closingRows)
            closing.Add(new
            {
                title = d.Title, stage = StageName(stages, d.Stage), value = Money(d.ExpectedValue, d.Currency),
                expectedClose = await DayAsync(d.ExpectedCloseDateUtc, ct), pastDue = d.ExpectedCloseDateUtc < startToday,
                link = Link("deal", d.Id),
            });

        return new
        {
            today = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            seeing = ScopeWords(access.Scope, "deals"),
            openByStage = open,
            openDeals = open.Sum(o => o.deals),
            wonThisMonth = new { deals = wonThisMonth.Sum(w => w.Count), value = string.Join(" + ", wonThisMonth.Select(w => Money(w.Total, w.Currency))) },
            lostThisMonth,
            openDealsPastTheirCloseDate = pastClose,
            closingInTheNext30DaysOrOverdue = closing,
        };
    }

    // ── lead_summary ─────────────────────────────────────────────────

    private async Task<object> LeadSummaryAsync(AiCaller who, CancellationToken ct)
    {
        if (!who.Can("leads")) return NoAccess("leads");
        var access = await _scope.GetAsync(RecordModules.Leads, ct);
        var statuses = await StatusesAsync(ct);
        var t = who.TenantId;
        var zone = await ZoneAsync(ct);
        var today = await TodayAsync(ct);
        var weekAgo = TimeZoneInfo.ConvertTimeToUtc(today.AddDays(-6).ToDateTime(TimeOnly.MinValue), zone);
        var monthStart = TimeZoneInfo.ConvertTimeToUtc(new DateTime(today.Year, today.Month, 1), zone);

        var leads = _db.Leads.AsNoTracking().Where(l => l.TenantId == t && !l.IsDeleted).VisibleTo(access);

        var byStatus = await leads.GroupBy(l => new { l.Status, l.IsConverted })
            .Select(g => new { g.Key.Status, g.Key.IsConverted, Count = g.Count() }).ToListAsync(ct);
        var newWeek = await leads.CountAsync(l => l.CreatedAtUtc >= weekAgo, ct);
        var newMonth = await leads.CountAsync(l => l.CreatedAtUtc >= monthStart, ct);
        var hot = await leads.Where(l => !l.IsConverted).OrderByDescending(l => l.Score).ThenByDescending(l => l.CreatedAtUtc).Take(5)
            .Select(l => new { l.Id, l.FullName, l.CompanyName, l.Status, l.Score, l.EstimatedValue, l.Currency }).ToListAsync(ct);

        return new
        {
            today = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            seeing = ScopeWords(access.Scope, "leads"),
            total = byStatus.Sum(s => s.Count),
            byStatus = byStatus
                .GroupBy(s => s.IsConverted ? "Converted" : statuses.GetValueOrDefault(s.Status, s.Status))
                .Select(g => new { status = g.Key, leads = g.Sum(x => x.Count) })
                .OrderByDescending(x => x.leads),
            newInTheLast7Days = newWeek,
            newThisMonth = newMonth,
            highestScoringOpen = hot.Select(l => new
            {
                name = l.FullName, company = l.CompanyName, status = statuses.GetValueOrDefault(l.Status, l.Status),
                score = l.Score, value = Money(l.EstimatedValue, l.Currency), link = Link("lead", l.Id),
            }),
        };
    }

    // ── unpaid_invoices ──────────────────────────────────────────────

    private async Task<object> UnpaidAsync(AiCaller who, CancellationToken ct)
    {
        if (!who.Can("invoices")) return NoAccess("invoices");
        var access = await _scope.GetAsync(RecordModules.Deals, ct);
        var t = who.TenantId;
        var today = await TodayAsync(ct);
        var zone = await ZoneAsync(ct);
        var startToday = TimeZoneInfo.ConvertTimeToUtc(today.ToDateTime(TimeOnly.MinValue), zone);

        var unpaid = new[] { InvoiceStatus.Sent, InvoiceStatus.Viewed, InvoiceStatus.PartiallyPaid, InvoiceStatus.Overdue, InvoiceStatus.Unpaid };
        var q = _db.Invoices.AsNoTracking()
            .Where(i => i.TenantId == t && !i.IsDeleted && i.Balance > 0 && unpaid.Contains(i.Status))
            .WithVisibleDeal(_db, access);

        var totals = await q.GroupBy(i => i.Currency)
            .Select(g => new { Currency = g.Key, Count = g.Count(), Owed = g.Sum(x => x.Balance),
                               Overdue = g.Count(x => x.DueDateUtc < startToday) })
            .ToListAsync(ct);
        var rows = await q.OrderBy(i => i.DueDateUtc ?? DateTime.MaxValue).Take(10)
            .Select(i => new { i.Id, i.Number, i.Status, i.Balance, i.Total, i.Currency, i.DueDateUtc,
                               Deal = i.Deal != null ? i.Deal.Title : i.Quote != null ? i.Quote.Deal.Title : null })
            .ToListAsync(ct);

        var list = new List<object>();
        foreach (var r in rows)
        {
            var dueDay = await DayOnlyAsync(r.DueDateUtc, ct);
            list.Add(new
            {
                number = r.Number, deal = r.Deal, status = Words(r.Status.ToString()),
                owed = Money(r.Balance, r.Currency), total = Money(r.Total, r.Currency),
                due = await DayAsync(r.DueDateUtc, ct),
                daysOverdue = dueDay is { } d && d < today ? today.DayNumber - d.DayNumber : (int?)null,
                link = Link("invoice", r.Id),
            });
        }

        return new
        {
            today = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            unpaidInvoices = totals.Sum(x => x.Count),
            overdueInvoices = totals.Sum(x => x.Overdue),
            totalOwed = string.Join(" + ", totals.Select(x => Money(x.Owed, x.Currency))),
            oldestFirst = list,
        };
    }

    // ── shared pieces ────────────────────────────────────────────────

    private async Task<object> ActivityAsync(string entityType, Guid id, Guid t, CancellationToken ct)
    {
        var rows = await _db.Activities.AsNoTracking()
            .Where(a => a.TenantId == t && a.EntityType == entityType && a.EntityId == id && !a.IsDeleted && (!a.IsTask || a.IsCompleted))
            .OrderByDescending(a => a.ActivityDate).Take(5)
            .Select(a => new { a.ActivityDate, a.ActivityType, a.Subject, a.Outcome })
            .ToListAsync(ct);
        var list = new List<object>();
        foreach (var r in rows)
            list.Add(new { date = await DayAsync(r.ActivityDate, ct), kind = r.ActivityType, subject = Clip(r.Subject, 160), outcome = Clip(r.Outcome, 160) });
        return list;
    }

    private async Task<object> OpenTasksOnAsync(string entityType, Guid id, Guid t, CancellationToken ct)
    {
        var rows = await _db.Activities.AsNoTracking()
            .Where(a => a.TenantId == t && a.EntityType == entityType && a.EntityId == id && a.IsTask && !a.IsCompleted && !a.IsDeleted)
            .OrderBy(a => a.DueDate).Take(5)
            .Select(a => new { a.Subject, a.DueDate, a.AssignedToUserId })
            .ToListAsync(ct);
        var owners = await OwnersAsync(rows.Select(r => r.AssignedToUserId), ct);
        var today = await TodayAsync(ct);
        var list = new List<object>();
        foreach (var r in rows)
        {
            var d = await DayOnlyAsync(r.DueDate, ct);
            list.Add(new { task = Clip(r.Subject, 160), due = await DayAsync(r.DueDate, ct), overdue = d is { } dd && dd < today, assignedTo = Owner(owners, r.AssignedToUserId) });
        }
        return list;
    }

    private async Task<Dictionary<string, string>> OwnersAsync(IEnumerable<string?> ids, CancellationToken ct)
    {
        var guids = ids.Select(i => Guid.TryParse(i, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).Distinct().ToList();
        if (guids.Count == 0) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Users has no tenant filter: names only, of ids already on this workspace's records.
        var rows = await _db.Users.AsNoTracking().Where(u => guids.Contains(u.Id))
            .Select(u => new { u.Id, u.FirstName, u.LastName }).ToListAsync(ct);
        return rows.ToDictionary(u => u.Id.ToString(), u => $"{u.FirstName} {u.LastName}".Trim(), StringComparer.OrdinalIgnoreCase);
    }

    private static string? Owner(Dictionary<string, string> owners, string? id) =>
        string.IsNullOrWhiteSpace(id) ? "nobody (unassigned)" : owners.TryGetValue(id, out var n) ? n : null;

    private async Task<Dictionary<string, (string Name, StageCategory Category)>> StagesAsync(CancellationToken ct)
    {
        if (_stages is not null) return _stages;
        var rows = await _db.PipelineStages.AsNoTracking().OrderBy(s => s.SortOrder)
            .Select(s => new { s.Key, s.Name, s.Category }).ToListAsync(ct);
        _stages = new Dictionary<string, (string, StageCategory)>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows) _stages.TryAdd(r.Key, (r.Name, r.Category));
        return _stages;
    }

    private static string StageName(Dictionary<string, (string Name, StageCategory Category)> stages, string key) =>
        stages.TryGetValue(key, out var s) ? s.Name : key;

    private async Task<Dictionary<string, string>> StatusesAsync(CancellationToken ct)
    {
        if (_statuses is not null) return _statuses;
        var rows = await _db.LeadStatusDefinitions.AsNoTracking().Select(s => new { s.Key, s.Name }).ToListAsync(ct);
        _statuses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows) _statuses.TryAdd(r.Key, r.Name);
        return _statuses;
    }

    private async Task<TimeZoneInfo> ZoneAsync(CancellationToken ct)
    {
        if (_zone is not null) return _zone;
        var id = await _db.Tenants.AsNoTracking().IgnoreQueryFilters()
            .Where(t => t.Id == _clockTenant).Select(t => t.Timezone).FirstOrDefaultAsync(ct);
        try { _zone = string.IsNullOrWhiteSpace(id) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception) { _zone = TimeZoneInfo.Utc; }
        return _zone;
    }

    private async Task<DateOnly> TodayAsync(CancellationToken ct) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, await ZoneAsync(ct)));

    private async Task<DateOnly?> DayOnlyAsync(DateTime? utc, CancellationToken ct)
    {
        if (utc is null) return null;
        var u = DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(u, await ZoneAsync(ct)));
    }

    private async Task<string?> DayAsync(DateTime? utc, CancellationToken ct) =>
        (await DayOnlyAsync(utc, ct))?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? Money(decimal? amount, string? currency) =>
        amount is null ? null : $"{amount.Value.ToString("#,0.##", CultureInfo.InvariantCulture)} {currency}".Trim();

    private static string? Clip(string? s, int max) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Length <= max ? s.Trim() : s[..max].Trim() + "…";

    /// <summary>"PartiallyPaid" → "Partially paid".</summary>
    private static string Words(string pascal)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < pascal.Length; i++)
        {
            var c = pascal[i];
            if (i > 0 && char.IsUpper(c)) sb.Append(' ').Append(char.ToLowerInvariant(c));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static string ScopeWords(RecordScope scope, string what) => scope switch
    {
        RecordScope.All => $"all {what} in the workspace",
        RecordScope.Team => $"{what} of this person's team, and unassigned ones",
        _ => $"only {what} assigned to this person",
    };

    /// <summary>The record's page in Merkai. The chat turns exactly these shapes into links, nothing else.</summary>
    public static string Link(string type, Guid id) => type switch
    {
        "lead" => $"/Leads/Detail/{id}",
        "deal" => $"/Pipeline/Detail/{id}",
        "company" => $"/Companies/Detail/{id}",
        "contact" => $"/Contacts/Detail?id={id}",
        "quote" => $"/Quotes/Detail?id={id}",
        _ => $"/Invoices/Detail?id={id}",
    };
}
