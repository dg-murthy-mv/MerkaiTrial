// =====================================================================
// WhatsAppTemplateHandlers.cs
// Location: MerkaiTrial.Application/Commands/Notifications/WhatsAppTemplateHandlers.cs
//
// NEW FILE (045). Read and write the eight platform WhatsApp templates,
// and queue a test message.
//
// SUPER ADMIN ONLY. These are MadeeVision's templates on MadeeVision's
// number, not a tenant's. The Razor Pages convention
// AuthorizeFolder("/Admin", "SuperAdmin") is what enforces that, and the
// page lives under /Admin for exactly that reason. These handlers are
// called in-process by Admin.Web, like the Plans ones, so there is no
// controller and no API surface to secure separately — which is also why
// they must never be registered in the WebApi.
//
// The DTOs live here rather than in DTOs/: they are read by one screen
// and nothing else, and PipelineRuleHandlers.cs set that precedent.
// =====================================================================

using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Notifications;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MerkaiTrial.Application.Commands.Notifications;

// ── DTOs ──────────────────────────────────────────────────────────────

public record WhatsAppTemplateRow(
    Guid Id,
    NotificationEventType EventType,
    string EventLabel,
    string Group,
    string TemplateName,
    string LanguageCode,
    string Category,
    string? BodyPreview,
    int VariableCount,
    string? VariableHints,
    bool IsActive,
    DateTime UpdatedAtUtc,
    string? UpdatedBy)
{
    /// <summary>
    /// True when the template cannot send as configured, whatever its
    /// IsActive says. The screen shows this rather than letting someone
    /// switch on a template that will be refused by Meta on first use.
    /// </summary>
    public string? Problem =>
        string.IsNullOrWhiteSpace(TemplateName) ? "No template name yet."
        : VariableCount != WhatsAppVariables.Count
            ? $"Expects {VariableCount} variable(s); Merkai sends {WhatsAppVariables.Count}."
        : null;
}

public record WhatsAppTemplatesDto(
    List<WhatsAppTemplateRow> Items,
    bool WhatsAppConfigured,
    string? FromDisplayNumber,
    string? BusinessAccountId,
    int ActiveCount);

public record SaveWhatsAppTemplateDto(
    Guid Id,
    string TemplateName,
    string LanguageCode,
    string Category,
    string? BodyPreview,
    int VariableCount,
    bool IsActive);

public record SaveWhatsAppTemplatesDto(List<SaveWhatsAppTemplateDto> Items);

// ── READ ──────────────────────────────────────────────────────────────

public class GetWhatsAppTemplatesHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly WhatsAppOptions _options;

    public GetWhatsAppTemplatesHandler(FlowDbContext db, IOptions<WhatsAppOptions> options)
    {
        _db = db;
        _options = options.Value;
    }

    public async Task<WhatsAppTemplatesDto> Handle(CancellationToken ct = default)
    {
        var rows = await _db.WhatsAppTemplates.AsNoTracking().ToListAsync(ct);

        // Built from the enum's display order rather than from the table,
        // so an event with no row yet still appears — the same reasoning as
        // the notification settings page. A missing row shows as an empty
        // name the admin can fill in, not as a gap.
        var items = NotificationDefaults.AllInDisplayOrder
            .Select(eventType =>
            {
                var row = rows.FirstOrDefault(r => r.EventType == eventType);

                return new WhatsAppTemplateRow(
                    Id: row?.Id ?? Guid.Empty,
                    EventType: eventType,
                    EventLabel: NotificationDefaults.Label(eventType),
                    Group: NotificationDefaults.Group(eventType),
                    TemplateName: row?.TemplateName ?? string.Empty,
                    LanguageCode: row?.LanguageCode ?? "en",
                    Category: row?.Category ?? "utility",
                    BodyPreview: row?.BodyPreview,
                    VariableCount: row?.VariableCount ?? WhatsAppVariables.Count,
                    VariableHints: row?.VariableHints,
                    IsActive: row?.IsActive ?? false,
                    UpdatedAtUtc: row?.UpdatedAtUtc ?? DateTime.UtcNow,
                    UpdatedBy: row?.UpdatedBy);
            })
            .ToList();

        return new WhatsAppTemplatesDto(
            items,
            _options.IsConfigured,
            _options.FromDisplayNumber,
            _options.BusinessAccountId,
            items.Count(i => i.IsActive));
    }
}

// ── WRITE ─────────────────────────────────────────────────────────────

public class SaveWhatsAppTemplatesHandler : ICommandHandler
{
    private const int MaxName = 512;

    private readonly FlowDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<SaveWhatsAppTemplatesHandler> _logger;

    public SaveWhatsAppTemplatesHandler(
        FlowDbContext db,
        ICurrentUserService currentUser,
        ILogger<SaveWhatsAppTemplatesHandler> logger)
    {
        _db = db;
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task Handle(SaveWhatsAppTemplatesDto dto, CancellationToken ct = default)
    {
        if (dto?.Items is null || dto.Items.Count == 0) return;

        var me = await _currentUser.GetCurrentUserAsync();
        var now = DateTime.UtcNow;

        var rows = await _db.WhatsAppTemplates.ToListAsync(ct);

        foreach (var item in dto.Items)
        {
            var row = rows.FirstOrDefault(r => r.Id == item.Id);
            if (row is null) continue;

            var name = (item.TemplateName ?? string.Empty).Trim().ToLowerInvariant();

            // Meta's own rule: lower case, digits and underscores. Enforced
            // here because the failure otherwise arrives as a rejected send
            // to a real person, hours later, with a numeric code.
            if (name.Length > 0 && !name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'))
                throw new InvalidOperationException(
                    $"\"{item.TemplateName}\" is not a valid template name. " +
                    "Meta allows lower-case letters, digits and underscores only.");

            if (name.Length > MaxName)
                throw new InvalidOperationException("That template name is too long.");

            // Switching one ON is the moment to be strict: an active
            // template with the wrong variable count fails on every send.
            if (item.IsActive)
            {
                if (name.Length == 0)
                    throw new InvalidOperationException(
                        $"\"{NotificationDefaults.Label(row.EventType)}\" has no template name, so it can't be switched on.");

                if (item.VariableCount != WhatsAppVariables.Count)
                    throw new InvalidOperationException(
                        $"\"{NotificationDefaults.Label(row.EventType)}\" expects {item.VariableCount} variable(s), " +
                        $"but Merkai sends {WhatsAppVariables.Count}. Fix the count, or re-approve the template with " +
                        $"{WhatsAppVariables.Count} placeholders.");
            }

            row.TemplateName  = name;
            row.LanguageCode  = string.IsNullOrWhiteSpace(item.LanguageCode) ? "en" : item.LanguageCode.Trim();
            row.Category      = string.IsNullOrWhiteSpace(item.Category) ? "utility" : item.Category.Trim().ToLowerInvariant();
            row.BodyPreview   = string.IsNullOrWhiteSpace(item.BodyPreview) ? null : item.BodyPreview.Trim();
            row.VariableCount = Math.Clamp(item.VariableCount, 0, 10);
            row.IsActive      = item.IsActive;
            row.UpdatedAtUtc  = now;
            row.UpdatedBy     = me.FullName;
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "WhatsApp templates saved by {User}: {Active} active",
            me.FullName, rows.Count(r => r.IsActive));
    }
}

// ── TEST SEND ─────────────────────────────────────────────────────────

public class SendWhatsAppTestHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<SendWhatsAppTestHandler> _logger;

    public SendWhatsAppTestHandler(
        FlowDbContext db,
        ICurrentUserService currentUser,
        ILogger<SendWhatsAppTestHandler> logger)
    {
        _db = db;
        _currentUser = currentUser;
        _logger = logger;
    }

    /// <summary>
    /// QUEUES a test message rather than sending it inline.
    ///
    /// That is the point. A direct call would prove that Meta accepts the
    /// template and nothing else; going through the queue exercises what
    /// a real notification exercises — the row, the worker's claim, the
    /// sender, the retry classification, and the send log. If this arrives,
    /// a real notification will arrive.
    ///
    /// The cost is a few seconds' wait for the worker's next tick, which
    /// is why the page says so rather than implying it is instant.
    /// </summary>
    public async Task<string> Handle(Guid templateId, CancellationToken ct = default)
    {
        var me = await _currentUser.GetCurrentUserAsync();

        var template = await _db.WhatsAppTemplates
            .FirstOrDefaultAsync(t => t.Id == templateId, ct)
            ?? throw new KeyNotFoundException("That template no longer exists.");

        if (string.IsNullOrWhiteSpace(template.TemplateName))
            throw new InvalidOperationException("Give the template a name first.");

        // The tester's own number, from their user row. Deliberately not a
        // free-text box: a typo there messages a stranger, and Meta counts
        // that against this number's quality rating.
        var user = await _db.Users
            .AsNoTracking()
            .Where(u => u.Id == me.UserId)
            .Select(u => new { u.MobileE164, u.WhatsAppOptInAtUtc, u.TenantId })
            .FirstOrDefaultAsync(ct);

        if (user is null || string.IsNullOrWhiteSpace(user.MobileE164))
            throw new InvalidOperationException(
                "Add your own mobile number under Settings → Notifications first — " +
                "the test goes to you, not to a number typed here.");

        if (user.WhatsAppOptInAtUtc is null)
            throw new InvalidOperationException(
                "Tick the WhatsApp opt-in under Settings → Notifications first. " +
                "The rule applies to you as much as to anyone else.");

        var variables = WhatsAppVariables.For(
            $"Test of {template.TemplateName}",
            $"Sent from Merkai at {DateTime.UtcNow:HH:mm} UTC. If this arrived, the channel works.");

        if (template.VariableCount != variables.Count)
            throw new InvalidOperationException(
                $"This template expects {template.VariableCount} variable(s) and Merkai sends " +
                $"{variables.Count}. Meta would refuse it, so the test is not queued.");

        _db.OutboundMessages.Add(new OutboundMessage
        {
            Id              = Guid.NewGuid(),
            TenantId        = user.TenantId,
            Channel         = OutboundChannel.WhatsApp,
            Status          = OutboundStatus.Pending,
            EventType       = template.EventType,
            RecipientUserId = me.UserId,

            ToAddress       = user.MobileE164!,
            ToName          = me.FullName,

            Subject         = template.TemplateName,
            BodyText        = WhatsAppPayload.PackVariables(variables),
            BodyHtml        = WhatsAppPayload.Render(template.BodyPreview, variables),

            EntityType      = "WhatsAppTemplate",
            EntityId        = template.Id,

            AttemptCount     = 0,
            NextAttemptAtUtc = null,
            CreatedAtUtc     = DateTime.UtcNow
        });

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "WhatsApp test queued for {User} using {Template}", me.FullName, template.TemplateName);

        return template.TemplateName;
    }
}
