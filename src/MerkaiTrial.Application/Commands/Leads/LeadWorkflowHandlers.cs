// =====================================================================
// LEAD WORKFLOW HANDLERS
// Location: MerkaiTrial.Application/Commands/Leads/LeadWorkflowHandlers.cs
//
// COMPLETE FILE — replaces the existing one.
//
// WHAT CHANGED
//   GetLeadTimelineHandler read LeadActivities and LeadReminders, so every
//   activity logged since March was missing from the Lead timeline. It now
//   delegates to the unified GetTimelineHandler, which reads Activities
//   (logs + tasks) and notes. Same signature, so LeadsExtendedController
//   and LeadService need no change. Missing lead → empty list, as before.
//
//   RECORD VISIBILITY (015)
//     AssignLeadHandler      — the lead must be visible to the caller.
//     GetLeadTimelineHandler — empty timeline for a lead outside scope.
//
//   041a — AssignLeadHandler, THE THIRD WAY TO SET AN OWNER
//
//   This handler is what PATCH /api/leads/{id}/assign calls, and it had
//   all three of the problems 041 fixed elsewhere plus one of its own:
//
//     1. lead.OwnerUserId = dto.OwnerUserId;
//        The same unconditional write UpdateLeadHandler had. A null owner
//        silently unassigned the lead, and no value was ever checked, so
//        a user id from another tenant was accepted and stored.
//
//     2. ★ NO NOTIFICATION. 039 taught the create and update paths to
//        tell a new owner, and missed this one. So assigning a lead from
//        the list — which is the quickest way to do it, and therefore the
//        way it usually gets done — told nobody. The person got the lead
//        and no in-app badge, no email. Everything we built in 039/040
//        was simply bypassed on this route.
//
//     3. NO AUDIT ROW. UpdateLeadHandler writes LeadOwnerChanged. This
//        path wrote nothing, so "who took this lead off me" had an answer
//        only if the change happened to be made on the edit page.
//
//     4. UpdatedBy was not set — only UpdatedAtUtc — so the row said it
//        changed but not who changed it.
//
//   All four are fixed here. The owner decision comes from the shared
//   OwnerAssignment, so this route now agrees with the edit page and with
//   deals: null leaves the owner alone, "" unassigns, an id is validated
//   against this tenant's active users.
// =====================================================================

using MerkaiTrial.Application.Commands.Activities;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Security;
using MerkaiTrial.Application.Services;                  // 041a: OwnerAssignment
using MerkaiTrial.Application.Services.Notifications;    // 041a
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Commands.Leads
{
    // ==================== ASSIGN LEAD ====================
    public class AssignLeadHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly IRecordScopeService _scope;
        private readonly ICurrentUserService _currentUserService;   // 041a
        private readonly INotificationDispatcher _notify;           // 041a
        private readonly IAuditService _audit;                      // 041a
        private readonly ILogger<AssignLeadHandler> _logger;

        public AssignLeadHandler(
            FlowDbContext context,
            IRecordScopeService scope,
            ICurrentUserService currentUserService,                 // 041a
            INotificationDispatcher notify,                         // 041a
            IAuditService audit,                                    // 041a
            ILogger<AssignLeadHandler> logger)
        {
            _context = context;
            _scope = scope;
            _currentUserService = currentUserService;
            _notify = notify;
            _audit = audit;
            _logger = logger;
        }

        public async Task Handle(AssignLeadDto dto, CancellationToken cancellationToken = default)
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

                var currentUserId = _currentUserService.GetCurrentUserId();
                var oldOwner = lead.OwnerUserId;

                // 041a. One rule for every route that sets an owner:
                //   null → leave alone, "" → unassign, an id → validate.
                // Throws a sentence meant for a person if the chosen user
                // is not active in this tenant, before the lead is touched.
                var decision = await OwnerAssignment.ForUpdateAsync(
                    _context, dto.TenantId, dto.OwnerUserId, lead.OwnerUserId, cancellationToken);

                // Nothing asked for, nothing to do. Worth returning early:
                // it keeps a repeated PATCH from writing an UpdatedAtUtc
                // that makes it look as though something changed.
                if (!decision.Changed)
                {
                    _logger.LogInformation(
                        "Assign lead {LeadId}: no change, owner already {OwnerUserId}",
                        dto.LeadId, oldOwner ?? "(nobody)");
                    return;
                }

                lead.OwnerUserId = decision.OwnerUserId;
                lead.UpdatedAtUtc = DateTime.UtcNow;
                lead.UpdatedBy = currentUserId.ToString();          // 041a

                // 041a. BEFORE the save, so the notification and the
                // assignment commit together. The dispatcher never saves —
                // that is its defining rule.
                if (decision.Change == OwnerChange.Assigned)
                    await NotifyNewOwnerAsync(lead, currentUserId, cancellationToken);

                await _context.SaveChangesAsync(cancellationToken);

                // 041a. Ownership is its own event, exactly as on the edit
                // page. Without this, a lead reassigned from the list left
                // no trace at all.
                await _audit.WriteAsync(
                    AuditAction.LeadOwnerChanged, AuditEntityType.Lead, lead.Id, dto.TenantId,
                    new
                    {
                        from = oldOwner,
                        to = lead.OwnerUserId,
                        unassigned = decision.Change == OwnerChange.Unassigned,
                        via = "list"
                    },
                    cancellationToken);

                _logger.LogInformation(
                    "Assigned lead {LeadId} to {OwnerUserId}",
                    dto.LeadId, lead.OwnerUserId ?? "(nobody)");
            }
            catch (KeyNotFoundException)
            {
                throw;
            }
            catch (InvalidOperationException)
            {
                // 041a. A refused owner is the caller's answer to give, not
                // an error to bury. Rethrown untouched so the controller can
                // turn it into 400 { "error": "..." } with the wording the
                // person needs to read. Logging it as an error here would
                // fill the log with other people's typing.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error assigning lead {LeadId}", dto.LeadId);
                throw;
            }
        }

        /// <summary>
        /// 041a. Adds rows; does not save. Never throws — a notification
        /// that cannot be prepared must not stop the assignment.
        /// </summary>
        private async Task NotifyNewOwnerAsync(Lead lead, Guid currentUserId, CancellationToken ct)
        {
            try
            {
                // Assigning something to yourself is not news.
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

    // ==================== GET LEAD TIMELINE ====================
    public class GetLeadTimelineHandler : ICommandHandler
    {
        private readonly GetTimelineHandler _timeline;
        private readonly FlowDbContext _context;
        private readonly IRecordScopeService _scope;

        public GetLeadTimelineHandler(GetTimelineHandler timeline, FlowDbContext context, IRecordScopeService scope)
        {
            _timeline = timeline;
            _context = context;
            _scope = scope;
        }

        public async Task<List<TimelineItemDto>> Handle(
            Guid tenantId, Guid leadId, CancellationToken cancellationToken = default)
        {
            // Outside scope → empty, the same answer as a lead that doesn't exist.
            if (!await _scope.CanSeeLeadAsync(_context, tenantId, leadId, cancellationToken))
                return new List<TimelineItemDto>();

            return await _timeline.Handle(
                new GetTimelineQuery(tenantId, ActivityEntityType.Lead, leadId),
                cancellationToken);
        }
    }
}
