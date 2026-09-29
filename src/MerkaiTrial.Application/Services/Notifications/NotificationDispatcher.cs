// =====================================================================
// NotificationDispatcher.cs
// Location: MerkaiTrial.Application/Services/Notifications/NotificationDispatcher.cs
//
// NEW FILE (037). Contains INotificationDispatcher and its implementation.
//
// THE ONE RULE: THIS NEVER CALLS SaveChanges
//
//   Every method ADDS rows to the caller's DbContext and returns. The
//   caller's own SaveChangesAsync commits them, in the caller's
//   transaction, alongside the change that caused them.
//
//   That is the entire reason in-app notifications are written inline
//   rather than queued. A deal cannot move without its notification, and a
//   notification cannot survive a rolled-back move. Calling SaveChanges
//   here would break both halves of that and would also commit whatever
//   else the caller happened to have tracked but not yet saved — which, in
//   a handler mid-way through its work, is a genuinely dangerous thing to
//   do.
//
//   If you ever need to notify OUTSIDE a transaction, add rows and save
//   deliberately at the call site. Do not add a SaveChanges here.
//
// WHY ONE ROW PER RECIPIENT
//   Notifying three people writes three rows, so each of them reads and
//   dismisses independently. A shared row with a join table would be
//   smaller and would make "is this read" a per-person query instead of a
//   column — the wrong trade for the most frequently asked question in the
//   app.
//
// EMAIL (038)
//   The same methods now ALSO write OutboundMessage rows, in the same
//   transaction, for recipients whose preferences ask for email. As
//   promised in 037 the interface did not change — only the body grew,
//   which is why callers take the dispatcher instead of inserting
//   Notification rows themselves.
//
//   Preference resolution, per recipient per event type (040):
//
//       the workspace's TenantNotificationDefault, IF it is LOCKED
//       otherwise the user's own UserNotificationPreference row
//       otherwise the workspace's TenantNotificationDefault
//       otherwise NotificationDefaults (in-app on, email only for the two
//       approval events)
//
//   That order lives in NotificationResolution.For, beside the entity, and
//   the settings page calls the SAME method to decide what to render. If
//   the two ever disagree the page is lying to the user about what will
//   happen, so there is exactly one implementation.
//
//   An email row is skipped silently when the user has no address. That is
//   not an error worth failing a deal over.
//
//   SMS is still not here. The queue and the worker are channel-agnostic;
//   only a sender is missing.
// =====================================================================

using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MerkaiTrial.Application.Services.Notifications;

/// <summary>
/// What happened, in the words the recipient will read. Built by the
/// handler that knows the context; the dispatcher only decides who gets it.
/// </summary>
public sealed record NotificationRequest(
    Guid TenantId,
    NotificationEventType EventType,

    /// <summary>
    /// The line shown in the bell, already written out: "Quote QUO-0042 was
    /// accepted". Never recomputed later, so it stays a record of what
    /// happened rather than a view of current state.
    /// </summary>
    string Title,

    string? Body = null,
    string? EntityType = null,
    Guid? EntityId = null,

    /// <summary>
    /// Who did it. Excluded from the recipients automatically — being told
    /// about your own click is what makes people mute notifications. Null
    /// for a customer acting on a public quote link.
    /// </summary>
    Guid? ActorUserId = null,

    /// <summary>Their name, or something like "the customer".</summary>
    string? ActorName = null);

public interface INotificationDispatcher
{
    /// <summary>
    /// Adds one notification per recipient. DOES NOT SAVE — the caller's
    /// SaveChangesAsync commits these in its own transaction.
    /// Returns how many rows were added, for logging.
    /// </summary>
    Task<int> AddForUsersAsync(
        NotificationRequest request,
        IEnumerable<Guid> recipientUserIds,
        CancellationToken ct = default);

    /// <summary>
    /// The same, for a record's owner. Takes the raw string because
    /// Deal.OwnerUserId and Lead.OwnerUserId are strings; a null, blank or
    /// unparseable value notifies nobody and is not an error — an unassigned
    /// record has no owner to tell.
    /// </summary>
    Task<int> AddForOwnerAsync(
        NotificationRequest request,
        string? ownerUserId,
        CancellationToken ct = default);
}

public sealed class NotificationDispatcher : INotificationDispatcher
{
    private readonly FlowDbContext _db;
    private readonly ILogger<NotificationDispatcher> _logger;
    private readonly EmailOptions _email;          // 038

    public NotificationDispatcher(
        FlowDbContext db,
        ILogger<NotificationDispatcher> logger,
        IOptions<EmailOptions> email)
    {
        _db = db;
        _logger = logger;
        _email = email.Value;
    }

    public async Task<int> AddForUsersAsync(
        NotificationRequest request,
        IEnumerable<Guid> recipientUserIds,
        CancellationToken ct = default)
    {
        if (request.TenantId == Guid.Empty)
        {
            // Fail loudly rather than writing rows nobody can ever see: the
            // global query filter would hide a TenantId of Guid.Empty for
            // ever, which is the same bug 036 had to repair in
            // DealStageHistory.
            _logger.LogError(
                "Refusing to write a notification with an empty TenantId ({Event}: {Title})",
                request.EventType, request.Title);
            return 0;
        }

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            _logger.LogError(
                "Refusing to write a notification with no title ({Event})", request.EventType);
            return 0;
        }

        // Distinct, and never the person who caused it.
        var candidates = recipientUserIds
            .Where(id => id != Guid.Empty)
            .Where(id => request.ActorUserId is null || id != request.ActorUserId.Value)
            .Distinct()
            .ToList();

        if (candidates.Count == 0) return 0;

        // Users is deliberately NOT tenant-filtered in FlowDbContext (login
        // and cookie validation read it with no tenant context), so the
        // TenantId predicate here is not belt-and-braces — it is the ONLY
        // thing stopping a stray id from another workspace being notified.
        var valid = await _db.Users.AsNoTracking()
            .Where(u => candidates.Contains(u.Id)
                     && u.TenantId == request.TenantId
                     && u.IsActive
                     && !u.IsDeleted)
            .Select(u => new
            {
                u.Id,
                u.Email,
                FullName = (u.FirstName + " " + u.LastName).Trim(),

                // 045. Both halves of "can we WhatsApp this person".
                u.MobileE164,
                HasOptedIn = u.WhatsAppOptInAtUtc != null
            })
            .ToListAsync(ct);

        if (valid.Count == 0)
        {
            _logger.LogInformation(
                "No active recipients for {Event} in tenant {TenantId} — nothing written",
                request.EventType, request.TenantId);
            return 0;
        }

        // 038: the workspace's name goes in the subject, and replies go to
        // the tenant rather than to Merkai. Tenants is not tenant-filtered
        // (it IS the tenant), so this is an ordinary lookup by id.
        var tenant = await _db.Tenants.AsNoTracking()
            .Where(t => t.Id == request.TenantId)
            .Select(t => new { t.Name, t.ReplyToEmail, t.FromEmail })
            .FirstOrDefaultAsync(ct);

        var tenantName = tenant?.Name ?? string.Empty;
        var replyTo = !string.IsNullOrWhiteSpace(tenant?.ReplyToEmail)
            ? tenant!.ReplyToEmail
            : tenant?.FromEmail;

        var now = DateTime.UtcNow;

        // 038: preferences for exactly these people and this event. Absent
        // rows are the normal case.
        var prefs = await _db.UserNotificationPreferences.AsNoTracking()
            .Where(p => p.TenantId == request.TenantId
                     && p.EventType == request.EventType
                     && valid.Select(v => v.Id).Contains(p.UserId))
            .ToDictionaryAsync(p => p.UserId, ct);

        // 040: the workspace's own default for this event, and whether it
        // is locked. Absent is also normal — it means nobody has visited
        // the workspace defaults tab, so the built-in defaults apply.
        var tenantDefault = await _db.TenantNotificationDefaults.AsNoTracking()
            .FirstOrDefaultAsync(d => d.TenantId == request.TenantId
                                   && d.EventType == request.EventType, ct);

        // Rendered ONCE for all recipients — the body does not vary by
        // person, and rendering it per recipient would be wasted work on
        // an approval that notifies five managers.
        RenderedEmail? rendered = null;

        // 045. The approved WhatsApp template for this event, if there is
        // one and it is switched on. Loaded once, outside the loop, and
        // deliberately NOT treated as an error when missing: a workspace
        // that has not had its templates approved yet still gets the bell
        // and the email, and the log records why WhatsApp was skipped.
        //
        // WhatsAppTemplates has no TenantId — these are our templates on
        // our number — so this is an ordinary unfiltered read.
        var template = await _db.WhatsAppTemplates.AsNoTracking()
            .FirstOrDefaultAsync(t => t.EventType == request.EventType
                                   && t.IsActive, ct);

        var written = 0;

        foreach (var user in valid)
        {
            prefs.TryGetValue(user.Id, out var pref);

            // 040: one shared resolution. A locked workspace default wins
            // over the user's own row — which is the whole point of the
            // lock, and is enforced here rather than only in the UI.
            var resolved = NotificationResolution.For(
                request.EventType, tenantDefault, pref);

            var wantsInApp = resolved.InApp;
            var wantsEmail = resolved.Email;
            var wantsWhatsApp = resolved.WhatsApp;        // 045

            if (wantsInApp)
            {
                _db.Notifications.Add(new Notification
                {
                    Id              = Guid.NewGuid(),
                    TenantId        = request.TenantId,
                    RecipientUserId = user.Id,
                    EventType       = request.EventType,
                    Title           = Truncate(request.Title, 200)!,
                    Body            = Truncate(request.Body, 500),
                    EntityType      = Truncate(request.EntityType, 50),
                    EntityId        = request.EntityId,
                    ActorUserId     = request.ActorUserId,
                    ActorName       = Truncate(request.ActorName, 200),
                    CreatedAtUtc    = now,
                    ReadAtUtc       = null,
                    DismissedAtUtc  = null
                });

                written++;
            }

            // ── 045: WhatsApp ────────────────────────────────────────
            // BEFORE the email block's `continue`, deliberately. Somebody
            // who wants WhatsApp and not email is an ordinary case, and
            // putting this after that line would silently never message
            // them — the kind of bug that looks like the channel is broken.
            if (wantsWhatsApp)
                QueueWhatsApp(request, user.Id, user.MobileE164, user.HasOptedIn, template, now);

            if (!wantsEmail) continue;

            if (string.IsNullOrWhiteSpace(user.Email))
            {
                // Not an error. A user with no address simply cannot be
                // emailed, and that must not fail the deal that triggered it.
                _logger.LogInformation(
                    "User {UserId} wants email for {Event} but has no address", user.Id, request.EventType);
                continue;
            }

            rendered ??= NotificationEmailTemplate.Render(
                request, tenantName, _email.AppBaseUrl);

            _db.OutboundMessages.Add(new OutboundMessage
            {
                Id              = Guid.NewGuid(),   // also the idempotency key
                TenantId        = request.TenantId,
                Channel         = OutboundChannel.Email,
                Status          = OutboundStatus.Pending,
                EventType       = request.EventType,
                RecipientUserId = user.Id,

                // Resolved NOW, not at send time: if the address changes
                // between the deal closing and the worker running, this
                // message should still go where it was addressed.
                ToAddress       = Truncate(user.Email, 320)!,
                ToName          = Truncate(user.FullName, 200),
                ReplyToAddress  = Truncate(replyTo, 320),

                Subject         = Truncate(rendered.Subject, 300)!,
                BodyHtml        = rendered.Html,
                BodyText        = rendered.Text,

                EntityType      = Truncate(request.EntityType, 50),
                EntityId        = request.EntityId,

                AttemptCount     = 0,
                NextAttemptAtUtc = null,            // null = due now
                CreatedAtUtc     = now
            });
        }

        // NO SaveChangesAsync. See the header.
        return written;
    }

    /// <summary>
    /// 045. Queues one WhatsApp message, or explains in the log why it
    /// could not.
    ///
    /// Adds a row; does NOT save — the same rule as everything else here.
    ///
    /// FOUR REASONS TO SKIP, each logged as itself rather than as a
    /// failure, because none of them is one:
    ///
    ///   no template          this event has no approved template yet
    ///   no number            the person never filled it in
    ///   no opt-in            they have not agreed, and Meta requires it
    ///   variable mismatch    the approved template expects a different
    ///                        number of slots than we fill, which would be
    ///                        rejected by Meta with a numeric code
    ///
    /// A skip is not written to the queue at all. A queued row that can
    /// never succeed burns sixteen retries over six hours and then sits in
    /// the log as a red line implying something broke.
    /// </summary>
    private void QueueWhatsApp(
        NotificationRequest request,
        Guid userId,
        string? mobileE164,
        bool hasOptedIn,
        WhatsAppTemplate? template,
        DateTime now)
    {
        if (template is null || !template.IsUsable)
        {
            _logger.LogInformation(
                "WhatsApp skipped for {Event}: no approved template", request.EventType);
            return;
        }

        if (string.IsNullOrWhiteSpace(mobileE164))
        {
            _logger.LogInformation(
                "WhatsApp skipped for user {UserId}: no mobile number on file", userId);
            return;
        }

        if (!hasOptedIn)
        {
            // Meta's rule, not ours, and the one with a regulator behind
            // it. A preference ticked without an opt-in is not consent.
            _logger.LogInformation(
                "WhatsApp skipped for user {UserId}: has not opted in", userId);
            return;
        }

        var variables = WhatsAppVariables.For(request.Title, request.Body);

        if (template.VariableCount != variables.Count)
        {
            // Meta would answer 132000, whose text mentions neither the
            // template nor the count. Catching it here names both.
            _logger.LogWarning(
                "WhatsApp skipped for {Event}: template {Template} expects {Expected} variable(s), " +
                "we supply {Actual}. Correct VariableCount on the template screen, or re-approve the template.",
                request.EventType, template.TemplateName, template.VariableCount, variables.Count);
            return;
        }

        _db.OutboundMessages.Add(new OutboundMessage
        {
            Id              = Guid.NewGuid(),
            TenantId        = request.TenantId,
            Channel         = OutboundChannel.WhatsApp,
            Status          = OutboundStatus.Pending,
            EventType       = request.EventType,
            RecipientUserId = userId,

            // Resolved NOW, like the email address above: this message goes
            // to the number the person had when the event happened.
            ToAddress       = Truncate(mobileE164, 20)!,
            ToName          = null,

            // The convention from OutboundChannel.WhatsApp, applied through
            // WhatsAppPayload so the worker reads it back the same way.
            Subject         = Truncate(template.TemplateName, 300)!,
            BodyText        = WhatsAppPayload.PackVariables(variables),
            BodyHtml        = WhatsAppPayload.Render(template.BodyPreview, variables),

            EntityType      = Truncate(request.EntityType, 50),
            EntityId        = request.EntityId,

            AttemptCount     = 0,
            NextAttemptAtUtc = null,
            CreatedAtUtc     = now
        });
    }

    public Task<int> AddForOwnerAsync(
        NotificationRequest request,
        string? ownerUserId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ownerUserId))
            return Task.FromResult(0);      // unassigned record — nobody to tell

        if (!Guid.TryParse(ownerUserId, out var id))
        {
            _logger.LogWarning(
                "Owner id '{OwnerUserId}' is not a Guid — no notification written for {Event}",
                ownerUserId, request.EventType);
            return Task.FromResult(0);
        }

        return AddForUsersAsync(request, new[] { id }, ct);
    }

    /// <summary>
    /// Trimmed to the column width rather than allowed to throw at
    /// SaveChanges. A notification is never important enough to fail the
    /// business operation it describes, and a title cut at 200 characters is
    /// still a useful notification.
    /// </summary>
    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
