// =====================================================================
// LEAD COMMANDS - Write Operations
// Location: MerkaiTrial.Application/Commands/Leads/LeadCommandHandlers.cs
//
// 100: ONE AT A TIME, AND A REPEAT IS A NO-OP
//   • UpdateLeadStatusHandler locks the lead's row (RowLock) before it
//     reads it. Two identical status changes arriving together (a double
//     click, a retried request) used to both read the OLD status, both
//     save, and both set off "when the status becomes …" rules — two
//     tasks, two notifications. Now the second waits, reads the new
//     status, sees nothing to change, and returns without saving,
//     auditing or running rules.
//   • ConvertLeadHandler locks the lead's row too, for the same reason
//     as ConvertLeadToDealHandler: two conversions at once made two
//     contacts. And ExistingCompanyId is now checked: it must be a
//     company in the caller's own workspace. It was copied onto the new
//     contact as given, so a company id from another workspace could be
//     attached to this workspace's contact.
//
// 099: UpdateLeadHandler also tells the automation engine which CUSTOM
//      fields changed (read before the edit, again after the save), so
//      "when Tier changes to Gold" rules work.
//
// FIXES APPLIED:
//   1. CreateLeadHandler    — Currency falls back to ICurrentTenantService
//   2. ConvertLeadHandler   — Country no longer hardcoded to "IN"
//   3. UpdateLeadHandler    — OwnerUserId string→Guid fix (index-safe)
//   4. RECORD VISIBILITY (014)
//        • Create: a lead with no owner is assigned to whoever created it,
//          so a rep with "Own" scope never loses a lead they just added.
//        • Update / status / delete / convert: the lead is loaded through
//          .VisibleTo(access). Outside your scope = KeyNotFound = 404,
//          the same answer as another tenant's lead.
//   5. DeleteLeadHandler refuses converted leads server-side (the Index
//      page hid the button, but a direct API call still went through).
//
//   6. (039) LeadAssigned notifications. Create and Update both tell the
//      new owner, unless the new owner is the person doing it — being
//      notified that you assigned something to yourself is what makes
//      people mute notifications.
//
//      The rows are added BEFORE the existing SaveChangesAsync, never
//      after, so the notification and the assignment commit together.
//      INotificationDispatcher never saves; that is its defining rule.
//
//   7. ★ (041) THE OWNER COULD BE LOST WITHOUT ANYONE ASKING. Update read:
//
//          lead.OwnerUserId = dto.OwnerUserId;
//
//      so any caller that did not send the field — an integration, a
//      partially-filled DTO, a form where the select was disabled —
//      wiped the owner, and the lead vanished for every rep whose record
//      scope is "Own". There was no validation either: a user id from
//      ANOTHER tenant was accepted and stored.
//
//      Deals had the mirror-image bug (an owner could never be removed).
//      Both now go through OwnerAssignment, which gives one answer for
//      both modules:
//
//          null        field absent      → leave the owner alone
//          ""          sent empty        → unassign
//          a user id   sent with a value → assign, after checking the
//                                          user is active in THIS tenant
//
//      Create validates the same way. Previously a supplied owner was
//      trusted blindly there too.
//
//   8. (079) CUSTOM FIELDS ON LEADS
//      • Create and Update hand CreateLeadDto/UpdateLeadDto.CustomFields
//        to CustomFieldValueWriter.ApplyAsync BEFORE SaveChangesAsync, so
//        the lead and its values commit together or not at all. A null
//        map (the widget, the importer, every older caller) skips it.
//      • A refused value is a CustomFieldValidationException — an
//        InvalidOperationException carrying a sentence for the person.
//        Create and Update now re-throw InvalidOperationException (and
//        Create the plan limit) WITHOUT an ERROR log line: a required
//        field left empty, or a full plan, is not a system fault. The
//        controller already turns both into a readable 400 / 422.
//      • Update's audit row gains "customFields": "changed" when any
//        custom value changed — never the values themselves.
//      • Update returns the stored values in LeadDetailDto.CustomFieldValues.
//      • ConvertLeadHandler copies mapped values onto the contact it
//        creates (LeadFieldMapper), and writes ContactCreated (078b) for
//        that contact — before, a contact born from a conversion had no
//        creation entry at all.
//
//   9. (079) Removed `using DocumentFormat.OpenXml.Presentation;` and
//      `using DocumentFormat.OpenXml.Spreadsheet;` — IDE auto-imports that
//      nothing used. Spreadsheet alone declares types called Company and
//      Column; one ambiguous name away from a baffling compile error.
//
//  10. (095) Create and Update check a name, the column lengths and the
//      estimated value (not negative, not absurd) with the shared
//      InputRules, before anything is staged — every problem at once, as
//      an InputValidationException (an InvalidOperationException, so the
//      existing catch re-throws it without an error log and the controller
//      answers 400).
//
//      THE EMAIL AND PHONE FORMATS ARE NOT CHECKED HERE, ON PURPOSE. This
//      handler also takes the website enquiry form. A
//      visitor who types "98765 43210 after 6pm" is still a customer;
//      refusing the enquiry would lose them, where saving it lets a rep
//      call and fix the number. The Admin.Web Add lead / Edit lead pages
//      check both formats, in the browser and on the server, before they
//      get here — so a lead a PERSON types is checked fully.
// =====================================================================

using MerkaiTrial.Application.Commands.CustomFields;    // 079
using MerkaiTrial.Application.Common;                   // 100 — RowLock
using MerkaiTrial.Application.Configuration;            // 079
using MerkaiTrial.Application.Commands.LeadStatuses;
using MerkaiTrial.Application.Commands.Automation;     // 098
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Exceptions;
using MerkaiTrial.Application.Security;   // IRecordScopeService, VisibleTo
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Notifications;   // 039
using MerkaiTrial.Application.Services.Tenants;
using MerkaiTrial.Application.Validation;            // 095
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Domain.Enums;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Commands.Leads
{
    // ==================== 095: THE CHECKS ====================

    internal static class LeadInput
    {
        /// <summary>
        /// Lengths, a name and the value. NOT the email and phone formats —
        /// see note 10 in the header: the website enquiry form comes through
        /// here too, and an enquiry is not refused for its phone.
        /// </summary>
        public static void Check(string? fullName, string? email, string? phone, string? companyName,
            string? address, decimal? estimatedValue)
        {
            var errors = new List<string>();
            errors.AddIf(InputRules.Text(fullName, "a name", 200, required: true));
            errors.AddIf(InputRules.Text(email, "the email address", InputRules.EmailMax));
            errors.AddIf(InputRules.Text(phone, "the phone number", InputRules.PhoneMax));
            errors.AddIf(InputRules.Text(companyName, "the company name", 200));
            errors.AddIf(InputRules.Text(address, "the address", 500));
            errors.AddIf(InputRules.Money(estimatedValue, "the estimated value"));
            InputRules.ThrowIfAny(errors);
        }
    }

    // ==================== CREATE LEAD ====================
    public record CreateLeadCommand(
        Guid TenantId,
        string FullName,
        string? Email,
        string? Phone,
        string? CompanyName,
        Guid? ChannelId,
        Guid? SourceId,
        decimal? EstimatedValue,
        string? OwnerUserId,
        string? CreatedBy
    );

    public class CreateLeadHandler : ICommandHandler
    {
        private readonly FlowDbContext         _context;
        private readonly ICurrentUserService   _currentUserService;
        private readonly ICurrentTenantService _tenantService;   // ← FIX 1
        private readonly ILogger<CreateLeadHandler> _logger;
        private readonly ILeadScoringService _scoring;
        private readonly IAuditService _audit;
        private readonly ILeadStatusResolver _statuses;
        private readonly INotificationDispatcher _notify;          // 039
        private readonly AutomationEngine _automation;             // 098

        public CreateLeadHandler(
            FlowDbContext         context,
            ICurrentUserService   currentUserService,
            ICurrentTenantService tenantService,
            ILeadScoringService scoring,
            ILogger<CreateLeadHandler> logger,
            IAuditService audit,
            ILeadStatusResolver statuses,
            INotificationDispatcher notify,
            AutomationEngine automation)                           // 098
        {
            _automation = automation;
            _context            = context;
            _currentUserService = currentUserService;
            _tenantService      = tenantService;
            _scoring = scoring;
            _statuses = statuses;
            _audit = audit;
            _notify = notify;
            _logger             = logger;
        }

        public async Task<LeadDto> Handle(CreateLeadDto dto, CancellationToken cancellationToken = default)
        {
            try
            {
                // 095 — before the quota check and before anything is staged.
                LeadInput.Check(dto.FullName, dto.Email, dto.Phone, dto.CompanyName, dto.Address, dto.EstimatedValue);

                // 104 — the source, channel and industry must be the system's
                // or this workspace's own; another workspace's id was stored
                // as sent (LookupGuard).
                await LookupGuard.CheckAsync(_context, dto.TenantId, dto.SourceId, dto.ChannelId, dto.VerticalId, cancellationToken);

                var currentUserId = _currentUserService.GetCurrentUserId();

                // FIX 1: Currency from tenant if not provided in DTO
                var currency = !string.IsNullOrWhiteSpace(dto.Currency)
                    ? dto.Currency.Trim().ToUpperInvariant()
                    : _tenantService.GetCurrencyCode();

                // ── Plan quota (103: PlanQuota) ──────────────────────────────────
                // Held until the lead is saved, so two saves at the same
                // instant can't both take the last place. The limit is read
                // live: a change the super admin made a second ago applies.
                await using var quota = await PlanQuota.HoldAsync(
                    _context, dto.TenantId, PlanQuota.Leads, ct: cancellationToken);
                var statuses = await _statuses.GetAsync(dto.TenantId, cancellationToken);
                var startStatus = statuses.Default
                    ?? throw new InvalidOperationException(
                        "This workspace has no lead statuses set up. Add them under Settings → Lead Statuses.");

                // 041. No owner chosen → the creator owns it. An owner that
                // WAS chosen is checked against this tenant's active users
                // before it is stored; the old code trusted it blindly, so a
                // user id from another workspace was accepted and the lead
                // was then invisible to everyone here.
                var ownerUserId = await OwnerAssignment.ForCreateAsync(
                    _context, dto.TenantId, dto.OwnerUserId, currentUserId, cancellationToken);

                var lead = new Lead
                {
                    Id          = Guid.NewGuid(),
                    TenantId    = dto.TenantId,
                    FullName    = dto.FullName,
                    Email       = dto.Email,
                    Phone       = dto.Phone,
                    CompanyName = dto.CompanyName,
                    ChannelId   = dto.ChannelId,
                    SourceId    = dto.SourceId,
                    Address     = dto.Address,
                    CountryId   = dto.CountryId,
                    Currency    = currency,
                    // Legacy enum fields — kept for backward compat
                    VerticalId = dto.VerticalId,
                    Channel     = Channel.Web,
                    Source      = dto.SourceId.HasValue ? "Dynamic" : "widget",
                    EstimatedValue = dto.EstimatedValue ?? 0,
                    // 041 — decided above, by OwnerAssignment.ForCreateAsync.
                    OwnerUserId = ownerUserId,
                    Status      = startStatus.Key,
                    Score       = 0,
                    IsConverted = false,
                    CreatedAtUtc = DateTime.UtcNow,
                    CreatedBy   = currentUserId.ToString()
                };

                _context.Leads.Add(lead);

                // 079 — staged in the same unit of work as the lead. A
                // refused value throws here, before anything is saved.
                await CustomFieldValueWriter.ApplyAsync(
                    _context, dto.TenantId, CustomFieldEntityTypes.Lead, lead.Id,
                    dto.CustomFields, currentUserId.ToString(), isNew: true, cancellationToken);

                // ── 039: tell the new owner, if it is not the creator ──
                // Added BEFORE the save so the notification and the lead
                // commit together. The dispatcher does not save.
                await NotifyOwnerAsync(lead, currentUserId, cancellationToken);

                await _context.SaveChangesAsync(cancellationToken);
                await quota.CommitAsync(cancellationToken);   // 103 — the place is taken; let the next one count

                // ✅ Recalculate score immediately after creation
                await _scoring.RecalculateAsync(lead.Id, lead.TenantId, cancellationToken);

                await _audit.WriteAsync(
                    AuditAction.LeadCreated, AuditEntityType.Lead, lead.Id, dto.TenantId,
                    new { name = lead.FullName, source = lead.Source, value = lead.EstimatedValue },
                    cancellationToken);

                // 098 — the workspace's automation rules. Last, after
                // everything of this save is written: a rule never undoes
                // the lead, and the engine never throws.
                await _automation.RecordCreatedAsync(
                    dto.TenantId, AutomationEntity.Lead, lead.Id, currentUserId.ToString(), cancellationToken);

                _logger.LogInformation("Created lead {LeadId} for tenant {TenantId}", lead.Id, dto.TenantId);

                return new LeadDto(
                    Id:          lead.Id,
                    TenantId:    lead.TenantId,
                    FullName:    lead.FullName,
                    Email:       lead.Email ?? "",
                    Phone:       lead.Phone ?? "",
                    Source:      lead.Source,
                    CreatedUtc:  lead.CreatedAtUtc,
                    OwnerUserId: lead.OwnerUserId ?? ""
                );
            }
            catch (PlanLimitExceededException)
            {
                throw;   // 079 — the controller returns 422; not a system error
            }
            catch (InvalidOperationException)
            {
                throw;   // 079 — a sentence for the person (custom field, no statuses, owner)
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating lead");
                throw;
            }
        }

        /// <summary>
        /// 039. Adds rows; does not save. Never throws — a notification that
        /// cannot be prepared must not stop a lead being created.
        /// </summary>
        private async Task NotifyOwnerAsync(Lead lead, Guid currentUserId, CancellationToken ct)
        {
            try
            {
                // A lead you created and kept is not news. The dispatcher
                // would drop the actor anyway; returning early saves the
                // lookup entirely for the common case.
                if (string.IsNullOrWhiteSpace(lead.OwnerUserId)
                    || string.Equals(lead.OwnerUserId, currentUserId.ToString(),
                                     StringComparison.OrdinalIgnoreCase))
                    return;

                var me = await _currentUserService.GetCurrentUserAsync();

                await _notify.AddForOwnerAsync(
                    DealNotifications.LeadAssigned(
                        tenantId: lead.TenantId,
                        leadId: lead.Id,
                        leadName: lead.FullName,
                        companyName: lead.CompanyName,
                        estimatedValue: lead.EstimatedValue,
                        currency: lead.Currency,
                        actorUserId: me.UserId,
                        actorName: me.FullName),
                    lead.OwnerUserId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Could not prepare the assignment notification for lead {LeadId}", lead.Id);
            }
        }
    }

    // ==================== UPDATE LEAD ====================
    public record UpdateLeadCommand(
        Guid    TenantId,
        Guid    LeadId,
        string  FullName,
        string? Email,
        string? Phone,
        string? CompanyName,
        Guid?   ChannelId,
        Guid?   SourceId,
        int     Score,
        decimal? EstimatedValue,
        string? OwnerUserId,
        string? UpdatedBy
    );


    public class UpdateLeadHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ICurrentUserService _currentUserService;
        private readonly ICurrentTenantService _tenantService;
        private readonly ILogger<UpdateLeadHandler> _logger;
        private readonly ILeadScoringService _scoring;
        private readonly IAuditService _audit;                       // ✅ AUDIT
        private readonly ILeadStatusResolver _statusResolver;
        private readonly IRecordScopeService _scope;
        private readonly INotificationDispatcher _notify;            // 039
        private readonly AutomationEngine _automation;               // 098

        public UpdateLeadHandler(
            FlowDbContext context,
            ICurrentUserService currentUserService,
            ICurrentTenantService tenantService,
            ILeadScoringService scoring,
            IAuditService audit,                                     // ✅ AUDIT
            ILeadStatusResolver statusResolver,
            IRecordScopeService scope,
            INotificationDispatcher notify,                          // 039
            ILogger<UpdateLeadHandler> logger,
            AutomationEngine automation)                             // 098
        {
            _automation = automation;
            _notify = notify;
            _scope = scope;
            _context = context;
            _currentUserService = currentUserService;
            _tenantService = tenantService;
            _scoring = scoring;
            _audit = audit;                             // ✅ AUDIT
            _statusResolver = statusResolver;
            _logger = logger;
        }

        public async Task<LeadDetailDto> Handle(UpdateLeadDto dto, CancellationToken cancellationToken = default)
        {
            try
            {
                var access = await _scope.GetAsync(RecordModules.Leads, cancellationToken);

                var lead = await _context.Leads
                    .Where(l => l.Id == dto.LeadId && l.TenantId == dto.TenantId && !l.IsDeleted)
                    .VisibleTo(access)
                    .FirstOrDefaultAsync(cancellationToken);

                if (lead == null)
                    throw new KeyNotFoundException($"Lead {dto.LeadId} not found");

                // 102 — made from the version on screen? Refused before
                // anything changes if someone else saved since.
                RecordVersion.Expect(_context, lead, versioned => versioned.RowVersion, dto.Version, "lead");

                // 095 — before anything is assigned.
                LeadInput.Check(dto.FullName, dto.Email, dto.Phone, dto.CompanyName, dto.Address, dto.EstimatedValue);

                // 104 — the source, channel and industry must be the system's
                // or this workspace's own; another workspace's id was stored
                // as sent (LookupGuard).
                await LookupGuard.CheckAsync(_context, dto.TenantId, dto.SourceId, dto.ChannelId, dto.VerticalId, cancellationToken);

                var currentUserId = _currentUserService.GetCurrentUserId();

                // FIX 1: Currency from tenant if not provided
                var currency = !string.IsNullOrWhiteSpace(dto.Currency)
                    ? dto.Currency.Trim().ToUpperInvariant()
                    : _tenantService.GetCurrencyCode();

                // ✅ AUDIT — snapshot BEFORE anything is assigned.
                // Plain locals rather than an anonymous type: the fields below
                // are about to be overwritten, and copying the values now is
                // what makes a before/after diff possible at all.
                var oldFullName = lead.FullName;
                var oldEmail = lead.Email;
                var oldPhone = lead.Phone;
                var oldCompanyName = lead.CompanyName;
                var oldAddress = lead.Address;
                var oldCountryId = lead.CountryId;
                var oldCurrency = lead.Currency;
                var oldChannelId = lead.ChannelId;
                var oldSourceId = lead.SourceId;
                var oldVerticalId = lead.VerticalId;
                var oldValue = lead.EstimatedValue;
                var oldOwner = lead.OwnerUserId;

                // ── 041: what does the incoming owner value MEAN? ─────────
                // Asked BEFORE any field is written, and asked in the one
                // place both leads and deals ask it. Throws a message meant
                // for a person if a new owner is not active in this tenant,
                // and it throws before the lead has been touched, so a
                // rejected owner cannot leave half an edit applied.
                var ownerDecision = await OwnerAssignment.ForUpdateAsync(
                    _context, dto.TenantId, dto.OwnerUserId, lead.OwnerUserId, cancellationToken);

                lead.FullName = dto.FullName;
                lead.Email = dto.Email;
                lead.Phone = dto.Phone;
                lead.CompanyName = dto.CompanyName;
                lead.Address = dto.Address;
                lead.CountryId = dto.CountryId;
                lead.Currency = currency;
                lead.ChannelId = dto.ChannelId;
                lead.SourceId = dto.SourceId;
                lead.VerticalId = dto.VerticalId;
                // 104 — Score is not taken from the request. It is worked out
                // by LeadScoringService (just below, after the save); a caller
                // could post any number and, where scoring is switched off for
                // the plan, it stuck.
                lead.EstimatedValue = dto.EstimatedValue;
                lead.OwnerUserId = ownerDecision.OwnerUserId;        // 041
                lead.UpdatedAtUtc = DateTime.UtcNow;
                lead.UpdatedBy = currentUserId.ToString();

                // 099 — the values BEFORE this edit, for "when a custom
                // field changes" rules. Only when the form sent any.
                var customBefore = dto.CustomFields is null ? null
                    : await CustomFieldValueWriter.ReadAsync(
                        _context, dto.TenantId, CustomFieldEntityTypes.Lead, lead.Id, cancellationToken);

                // 079 — same unit of work as the lead's own fields. Throws
                // a CustomFieldValidationException before anything is saved.
                await CustomFieldValueWriter.ApplyAsync(
                    _context, dto.TenantId, CustomFieldEntityTypes.Lead, lead.Id,
                    dto.CustomFields, currentUserId.ToString(), isNew: false, cancellationToken);

                var customFieldsChanged = _context.ChangeTracker.Entries<CustomFieldValue>()
                    .Any(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);

                // ── 039: the owner changed, so tell whoever now has it ──
                // BEFORE the save, deliberately: the notification and the
                // reassignment are one transaction.
                //
                // 041: driven by the decision rather than by comparing
                // strings. Only an actual assignment is news — unassigning
                // has nobody to tell, and "unchanged" must stay silent even
                // when the caller posted the same id back.
                if (ownerDecision.Change == OwnerChange.Assigned)
                    await NotifyNewOwnerAsync(lead, currentUserId, cancellationToken);

                await _context.SaveChangesAsync(cancellationToken);

                // ✅ Recalculate score after any profile change
                await _scoring.RecalculateAsync(lead.Id, lead.TenantId, cancellationToken);

                // ✅ AUDIT — only the fields that actually changed, as one row.
                // Score is deliberately excluded: LeadScoringService rewrites it
                // on every save, so auditing it would produce a row on every
                // edit whether or not a person changed anything.
                var changes = new Dictionary<string, object?>();

                if (oldFullName != lead.FullName) changes["fullName"] = new { from = oldFullName, to = lead.FullName };
                if (oldEmail != lead.Email) changes["email"] = new { from = oldEmail, to = lead.Email };
                if (oldPhone != lead.Phone) changes["phone"] = new { from = oldPhone, to = lead.Phone };
                if (oldCompanyName != lead.CompanyName) changes["company"] = new { from = oldCompanyName, to = lead.CompanyName };
                if (oldAddress != lead.Address) changes["address"] = new { from = oldAddress, to = lead.Address };
                if (oldCountryId != lead.CountryId) changes["countryId"] = new { from = oldCountryId, to = lead.CountryId };
                if (oldCurrency != lead.Currency) changes["currency"] = new { from = oldCurrency, to = lead.Currency };
                if (oldChannelId != lead.ChannelId) changes["channelId"] = new { from = oldChannelId, to = lead.ChannelId };
                if (oldSourceId != lead.SourceId) changes["sourceId"] = new { from = oldSourceId, to = lead.SourceId };
                if (oldVerticalId != lead.VerticalId) changes["verticalId"] = new { from = oldVerticalId, to = lead.VerticalId };
                if (oldValue != lead.EstimatedValue) changes["value"] = new { from = oldValue, to = lead.EstimatedValue };

                // 079 — that something changed, never the values (AuditLog.Data).
                if (customFieldsChanged) changes["customFields"] = "changed";

                if (changes.Count > 0)
                    await _audit.WriteAsync(
                        AuditAction.LeadUpdated, AuditEntityType.Lead, lead.Id, dto.TenantId,
                        changes, cancellationToken);

                // ✅ AUDIT — ownership is its own event. "Who took this lead off
                // me, and when" is a question people actually ask, and it should
                // not be buried inside a field-diff blob.
                //
                // 041: the decision decides, so an unassignment is recorded as
                // one (the old test `oldOwner != lead.OwnerUserId` was right
                // here, but leaving two sources of truth in one method is how
                // they drift apart later).
                if (ownerDecision.Changed)
                    await _audit.WriteAsync(
                        AuditAction.LeadOwnerChanged, AuditEntityType.Lead, lead.Id, dto.TenantId,
                        new
                        {
                            from = oldOwner,
                            to = lead.OwnerUserId,
                            unassigned = ownerDecision.Change == OwnerChange.Unassigned
                        },
                        cancellationToken);

                // 098 — rules watching the owner or the value. Unchanged
                // pairs are dropped by the engine. A rule may change the
                // lead again (say, its owner); the reply below reads the
                // lead after that, so the page shows what is true now.
                // 099 — and its custom fields, read again now they're saved.
                var customAfter = customBefore is null ? null
                    : await CustomFieldValueWriter.ReadAsync(
                        _context, dto.TenantId, CustomFieldEntityTypes.Lead, lead.Id, cancellationToken);

                await _automation.FieldsChangedAsync(
                    dto.TenantId, AutomationEntity.Lead, lead.Id,
                    new[]
                    {
                        new AutomationFieldChange(AutomationFields.Owner, oldOwner, lead.OwnerUserId),
                        new AutomationFieldChange(AutomationFields.Value,
                            AutomationEngine.Wire(oldValue), AutomationEngine.Wire(lead.EstimatedValue))
                    }.Concat(AutomationEngine.CustomChanges(customBefore, customAfter)),
                    currentUserId.ToString(), cancellationToken);

                _logger.LogInformation("Updated lead {LeadId} by user {UserId}", dto.LeadId, currentUserId);

                // Needed for IsConverted below — the resolver returns the
                // tenant's statuses; the category questions live on THAT.
                var leadStatuses = await _statusResolver.GetAsync(dto.TenantId, cancellationToken);

                // Resolve display names after save
                var channelName = lead.ChannelId.HasValue
                    ? await _context.LeadChannels
                        .Where(c => c.Id == lead.ChannelId)
                        .Select(c => c.Name)
                        .FirstOrDefaultAsync(cancellationToken)
                    : null;

                var sourceName = lead.SourceId.HasValue
                    ? await _context.LeadSources
                        .Where(s => s.Id == lead.SourceId)
                        .Select(s => s.Name)
                        .FirstOrDefaultAsync(cancellationToken)
                    : null;

                var countryName = lead.CountryId.HasValue
                    ? await _context.Countries
                        .Where(c => c.Id == lead.CountryId)
                        .Select(c => c.Name)
                        .FirstOrDefaultAsync(cancellationToken)
                    : null;

                string? verticalName = lead.VerticalId.HasValue
                    ? await _context.CompanyVerticals
                        .Where(v => v.Id == lead.VerticalId.Value)
                        .Select(v => v.Name)
                        .FirstOrDefaultAsync(cancellationToken)
                    : null;

                string? ownerName = null;
                string? ownerJobTitle = null;

                // FIX 2: Guid comparison — not string cast
                if (Guid.TryParse(lead.OwnerUserId, out var ownerGuid))
                {
                    var owner = await _context.Users
                        .Where(u => u.Id == ownerGuid)
                        .Select(u => new { u.FullName, u.JobTitle })
                        .FirstOrDefaultAsync(cancellationToken);

                    ownerName = owner?.FullName;
                    ownerJobTitle = owner?.JobTitle;
                }

                return new LeadDetailDto(
                    Id: lead.Id,
                    TenantId: lead.TenantId,
                    ContactId: lead.ContactId ?? Guid.Empty,
                    FullName: lead.FullName,
                    Email: lead.Email ?? "",
                    Phone: lead.Phone ?? "",
                    CompanyName: lead.CompanyName,
                    Address: lead.Address,
                    CountryId: lead.CountryId,
                    CountryName: countryName,
                    Currency: currency,
                    ChannelId: lead.ChannelId,
                    SourceId: lead.SourceId,
                    Channel: channelName ?? lead.Channel.ToString(),
                    Source: sourceName ?? lead.Source,
                    VerticalId: lead.VerticalId,
                    VerticalName: verticalName,
                    Status: lead.Status.ToString(),
                    Score: lead.Score,
                    OwnerUserId: lead.OwnerUserId,
                    OwnerName: ownerName,
                    OwnerJobTitle: ownerJobTitle,
                    CreatedAtUtc: lead.CreatedAtUtc,
                    UpdatedAtUtc: lead.UpdatedAtUtc,
                    HasDeal: lead.DealId.HasValue,
                    // Category, not a status named "Converted" — a tenant
                    // may rename it, and IsConverted must still be right.
                    IsConverted: leadStatuses.IsConverted(lead.Status),
                    DealId: lead.DealId,
                    DealStage: "",
                    ExpectedValue: lead.EstimatedValue ?? 0
                )
                {
                    // 079 — what is stored now, retired fields included.
                    CustomFieldValues = await CustomFieldValueWriter.ReadAsync(
                        _context, dto.TenantId, CustomFieldEntityTypes.Lead, lead.Id, cancellationToken),
                    // 102 — the version AFTER this save, for the next edit.
                    Version = RecordVersion.Of(lead.RowVersion)
                };
            }
            catch (KeyNotFoundException) { throw; }
            catch (InvalidOperationException) { throw; }   // 079 — a sentence for the person; not an ERROR line
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating lead {LeadId}", dto.LeadId);
                throw;
            }
        }

        /// <summary>
        /// 039. Adds rows; does not save. Never throws — a notification
        /// failing must not stop a lead being saved.
        /// </summary>
        private async Task NotifyNewOwnerAsync(Lead lead, Guid currentUserId, CancellationToken ct)
        {
            try
            {
                // Unassigned, or assigned to yourself: nothing to send.
                if (string.IsNullOrWhiteSpace(lead.OwnerUserId)
                    || string.Equals(lead.OwnerUserId, currentUserId.ToString(),
                                     StringComparison.OrdinalIgnoreCase))
                    return;

                var me = await _currentUserService.GetCurrentUserAsync();

                await _notify.AddForOwnerAsync(
                    DealNotifications.LeadAssigned(
                        tenantId: lead.TenantId,
                        leadId: lead.Id,
                        leadName: lead.FullName,
                        companyName: lead.CompanyName,
                        estimatedValue: lead.EstimatedValue,
                        currency: lead.Currency,
                        actorUserId: me.UserId,
                        actorName: me.FullName),
                    lead.OwnerUserId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Could not prepare the assignment notification for lead {LeadId}", lead.Id);
            }
        }
    }

    // ==================== UPDATE LEAD STATUS ====================
    public class UpdateLeadStatusHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<UpdateLeadStatusHandler> _logger;
        private readonly ILeadScoringService _scoring;
        private readonly IAuditService _audit;
        private readonly ILeadStatusResolver _statuses;
        private readonly IRecordScopeService _scope;
        private readonly AutomationEngine _automation;               // 098

        public UpdateLeadStatusHandler(FlowDbContext context, ILeadScoringService scoring, ILogger<UpdateLeadStatusHandler> logger, 
            IAuditService audit, ILeadStatusResolver statuses, IRecordScopeService scope,
            AutomationEngine automation)                             // 098
        {
            _automation = automation;
            _scope = scope;
            _context = context;
            _scoring = scoring;
            _logger  = logger;
            _audit   = audit;
            _statuses = statuses;
        }

        public async Task Handle(UpdateLeadStatusDto dto, CancellationToken cancellationToken = default)
        {
            try
            {
                var access = await _scope.GetAsync(RecordModules.Leads, cancellationToken);

                // 100 — before the read: a second identical change waits
                // here, then sees the status already set (below).
                await using var held = await RowLock.HoldAsync(_context, RowLock.Leads, dto.LeadId, cancellationToken);

                var lead = await _context.Leads
                    .Where(l => l.Id == dto.LeadId && l.TenantId == dto.TenantId && !l.IsDeleted)
                    .VisibleTo(access)
                    .FirstOrDefaultAsync(cancellationToken);

                if (lead == null)
                    throw new KeyNotFoundException($"Lead {dto.LeadId} not found");
                var statuses = await _statuses.GetAsync(dto.TenantId, cancellationToken);

                // 100 — already in that status (a double click, a retried
                // request, the other half of a race): nothing to save,
                // nothing to audit, no rules to run.
                if (string.Equals(lead.Status, dto.StatusKey, StringComparison.OrdinalIgnoreCase))
                {
                    await held.CommitAsync(cancellationToken);
                    return;
                }

                var target = statuses.Find(dto.StatusKey)
                    ?? throw new InvalidOperationException(
                        $"'{dto.StatusKey}' is not a status in this workspace. Valid: {statuses.SelectableKeysText}");

                // Converted is set by conversion and by nothing else —
                // otherwise a lead could read as converted with no deal
                // behind it, and the conversion funnel would lie.
                if (target.IsSystem)
                    throw new InvalidOperationException(
                        $"\"{target.Name}\" is set automatically when a lead becomes a deal — it can't be chosen by hand.");


                var oldStatus = lead.Status;
                lead.Status = dto.StatusKey;
                lead.UpdatedAtUtc = DateTime.UtcNow;

                await _context.SaveChangesAsync(cancellationToken);
                await held.CommitAsync(cancellationToken);   // 100 — the lock ends here

                // ✅ Status progression affects score
                await _scoring.RecalculateAsync(lead.Id, lead.TenantId, cancellationToken);
                await _audit.WriteAsync(
                    AuditAction.LeadStatusChanged, AuditEntityType.Lead, lead.Id, dto.TenantId,
                    new { from = statuses.NameOf(oldStatus), to = target.Name },
                    cancellationToken);

                // 098 — rules watching the status. The engine finds who
                // made the change from the signed-in user.
                await _automation.FieldsChangedAsync(
                    dto.TenantId, AutomationEntity.Lead, lead.Id,
                    new[] { new AutomationFieldChange(AutomationFields.Status, oldStatus, lead.Status) },
                    null, cancellationToken);


                _logger.LogInformation("Updated lead {LeadId} status {Old} → {New}", dto.LeadId, oldStatus, dto.StatusKey);
            }
            catch (KeyNotFoundException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating lead status {LeadId}", dto.LeadId);
                throw;
            }
        }

        public async Task Handle(Guid tenantId, Guid leadId, string status, CancellationToken ct = default)
            => await Handle(new UpdateLeadStatusDto(tenantId, leadId, status), ct);
    }

    // ==================== DELETE LEAD ====================
    public record DeleteLeadCommand(Guid TenantId, Guid LeadId);

    public class DeleteLeadHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<DeleteLeadHandler> _logger;
        private readonly IAuditService _audit;
        private readonly IRecordScopeService _scope;
        public DeleteLeadHandler(FlowDbContext context, ILogger<DeleteLeadHandler> logger, IAuditService audit,
            IRecordScopeService scope)
        {
            _context = context;
            _logger  = logger;
            _audit   = audit;
            _scope   = scope;
        }

        public async Task Handle(DeleteLeadCommand command, CancellationToken cancellationToken = default)
        {
            try
            {
                var access = await _scope.GetAsync(RecordModules.Leads, cancellationToken);

                var lead = await _context.Leads
                    .Where(l => l.Id == command.LeadId && l.TenantId == command.TenantId && !l.IsDeleted)
                    .VisibleTo(access)
                    .FirstOrDefaultAsync(cancellationToken);

                if (lead == null)
                    throw new KeyNotFoundException($"Lead {command.LeadId} not found");

                // Server-side guard. The Index page hides Delete for converted
                // leads, but a direct API call used to go straight through and
                // orphan the deal's history.
                if (lead.IsConverted)
                    throw new InvalidOperationException(
                        "Converted leads cannot be deleted. Manage this record via the associated deal.");

                lead.IsDeleted    = true;
                lead.UpdatedAtUtc = DateTime.UtcNow;

                await _context.SaveChangesAsync(cancellationToken);
                await _audit.WriteAsync(
                    AuditAction.LeadDeleted, AuditEntityType.Lead, lead.Id, command.TenantId,
                    new { name = lead.FullName, status = lead.Status },
                    cancellationToken);


                _logger.LogInformation("Deleted lead {LeadId}", command.LeadId);
            }
            catch (KeyNotFoundException) { throw; }
            catch (InvalidOperationException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting lead {LeadId}", command.LeadId);
                throw;
            }
        }
    }

    // ==================== CONVERT LEAD ====================
    // NOTE: Two convert handlers exist — ConvertLeadHandler (simple, used in
    // lead list) and ConvertLeadToDealHandler (full pipeline flow).
    // This is ConvertLeadHandler — creates Contact + Company only.
    // ConvertLeadToDealHandler creates Contact + Deal + marks converted.

    public record ConvertLeadCommand(
        Guid    TenantId,
        Guid    LeadId,
        bool    CreateCompany,
        string? CompanyName,
        Guid?   ExistingCompanyId,
        bool    CreateDeal,
        decimal? DealValue,
        string  ConvertedBy
    );

    public class ConvertLeadHandler : ICommandHandler
    {
        private readonly FlowDbContext         _context;
        private readonly ICurrentTenantService _tenantService;   // FIX 3 — replaces hardcoded "IN"
        private readonly ILogger<ConvertLeadHandler> _logger;
        private readonly IAuditService _audit;
        private readonly ILeadStatusResolver _statuses;
        private readonly IRecordScopeService _scope;
        private readonly AutomationEngine _automation;               // 098
        public ConvertLeadHandler(
            FlowDbContext         context,
            ICurrentTenantService tenantService,
            ILogger<ConvertLeadHandler> logger,
            IAuditService audit,
            ILeadStatusResolver statuses,
            IRecordScopeService scope,
            AutomationEngine automation)                             // 098
        {
            _automation   = automation;
            _scope         = scope;
            _context       = context;
            _tenantService = tenantService;
            _logger        = logger;
            _audit         = audit;
            _statuses      = statuses;
        }

        public async Task<ConversionResult> Handle(ConvertLeadCommand cmd, CancellationToken cancellationToken = default)
        {
            using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

            try
            {
                var access = await _scope.GetAsync(RecordModules.Leads, cancellationToken);

                // 100 — before the read; joins the transaction above.
                await using var held = await RowLock.HoldAsync(_context, RowLock.Leads, cmd.LeadId, cancellationToken);

                var lead = await _context.Leads
                    .Where(l => l.Id == cmd.LeadId && l.TenantId == cmd.TenantId && !l.IsDeleted)
                    .VisibleTo(access)
                    .FirstOrDefaultAsync(cancellationToken);

                if (lead == null)
                    throw new KeyNotFoundException($"Lead {cmd.LeadId} not found");

                if (lead.IsConverted)
                    throw new InvalidOperationException("Lead already converted");

                // 100 — an existing company must be one of this
                // workspace's own. The global filter already hides other
                // workspaces' companies from this query, and the explicit
                // TenantId says so out loud.
                if (cmd.ExistingCompanyId is Guid existingCompanyId && existingCompanyId != Guid.Empty)
                {
                    var companyIsOurs = await _context.Companies
                        .AnyAsync(c => c.Id == existingCompanyId && c.TenantId == cmd.TenantId && !c.IsDeleted, cancellationToken);
                    if (!companyIsOurs)
                        throw new InvalidOperationException("The company chosen isn't in this workspace. Pick one from the list, or create a new one.");
                }

                // 103 — the plan's limits, held until this commits. Order
                // matters (PlanQuota): companies before contacts.
                var createsCompany = cmd.CreateCompany && !string.IsNullOrEmpty(cmd.CompanyName);
                if (createsCompany)
                    await using (await PlanQuota.HoldAsync(_context, cmd.TenantId, PlanQuota.Companies, ct: cancellationToken)) { }
                await using (await PlanQuota.HoldAsync(_context, cmd.TenantId, PlanQuota.Contacts, ct: cancellationToken)) { }

                // Create Company if requested
                Guid? companyId = cmd.ExistingCompanyId == Guid.Empty ? null : cmd.ExistingCompanyId;
                if (createsCompany)
                {
                    var company = new Company
                    {
                        Id           = Guid.NewGuid(),
                        TenantId     = cmd.TenantId,
                        Name         = cmd.CompanyName,
                        Vertical     = "Generic",
                        Country      = _tenantService.GetCountryCode(), // FIX 3 — was hardcoded "IN"
                        CreatedAtUtc = DateTime.UtcNow,
                        CreatedBy    = cmd.ConvertedBy
                    };
                    _context.Companies.Add(company);
                    companyId = company.Id;
                }
                var statuses = await _statuses.GetAsync(cmd.TenantId, cancellationToken);
                var converted = statuses.ConvertedStatus
                            ?? throw new InvalidOperationException("No converted status is configured.");
                                        
                // Create Contact
                var names = lead.FullName.Split(' ', 2);
                var contact = new Contact
                {
                    Id           = Guid.NewGuid(),
                    TenantId     = cmd.TenantId,
                    CompanyId    = companyId,
                    FirstName    = names[0],
                    LastName     = names.Length > 1 ? names[1] : null,
                    Email        = lead.Email,
                    Phone        = lead.Phone,
                    CreatedAtUtc = DateTime.UtcNow,
                    CreatedBy    = cmd.ConvertedBy
                };
                _context.Contacts.Add(contact);

                // 079 — mapped lead values onto the new contact (no deal on
                // this path). Staged, saved with everything below.
                var copied = await LeadFieldMapper.CopyAsync(
                    _context, cmd.TenantId, lead.Id, contact.Id, contactIsNew: true, dealId: null,
                    cmd.ConvertedBy, cancellationToken);

                var statusBefore = lead.Status;                      // 098

                // Mark lead converted
                lead.IsConverted          = true;
                lead.ConvertedToContactId = contact.Id;
                lead.ConvertedToCompanyId = companyId;
                lead.ConvertedAtUtc       = DateTime.UtcNow;
                lead.ConvertedBy          = cmd.ConvertedBy;
                lead.Status               = converted.Key;

                await _context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                await _audit.WriteAsync(
                    AuditAction.LeadConverted, AuditEntityType.Lead, lead.Id, cmd.TenantId,
                    new
                    {
                        name = lead.FullName,
                        contactId = contact.Id,
                        companyId,
                        companyCreated = cmd.CreateCompany && companyId != cmd.ExistingCompanyId,
                        customFieldsCopied = copied.ToContact          // 079
                    },
                    cancellationToken);

                // 079 — the contact is a new record in its own right (078b).
                try
                {
                    await _audit.WriteAsync(
                        AuditAction.ContactCreated, AuditEntityType.Contact, contact.Id, cmd.TenantId,
                        new { name = $"{contact.FirstName} {contact.LastName}".Trim(), companyId = contact.CompanyId, fromLeadId = lead.Id },
                        cancellationToken);
                }
                catch (Exception auditEx)
                {
                    _logger.LogError(auditEx, "Contact {Id} was created by conversion but its audit row could not be written", contact.Id);
                }

                
                // 098 — "when a lead's status becomes Converted" rules.
                await _automation.FieldsChangedAsync(
                    cmd.TenantId, AutomationEntity.Lead, lead.Id,
                    new[] { new AutomationFieldChange(AutomationFields.Status, statusBefore, lead.Status) },
                    null, cancellationToken);

                _logger.LogInformation("Converted lead {LeadId} → contact {ContactId}", cmd.LeadId, contact.Id);

                return new ConversionResult
                {
                    ContactId = contact.Id,
                    CompanyId = companyId,
                    Success   = true
                };

            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(cancellationToken);
                _logger.LogError(ex, "Error converting lead {LeadId}", cmd.LeadId);
                throw;
            }
        }
    }

    public class ConversionResult
    {
        public Guid  ContactId { get; set; }
        public Guid? CompanyId { get; set; }
        public bool  Success   { get; set; }
    }
}
