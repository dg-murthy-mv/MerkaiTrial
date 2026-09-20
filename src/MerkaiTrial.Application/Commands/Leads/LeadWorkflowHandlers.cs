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
// =====================================================================

using MerkaiTrial.Application.Commands.Activities;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Security;
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
        private readonly ILogger<AssignLeadHandler> _logger;

        public AssignLeadHandler(FlowDbContext context, IRecordScopeService scope, ILogger<AssignLeadHandler> logger)
        {
            _context = context;
            _scope = scope;
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

                lead.OwnerUserId = dto.OwnerUserId;
                lead.UpdatedAtUtc = DateTime.UtcNow;

                await _context.SaveChangesAsync(cancellationToken);

                _logger.LogInformation("Assigned lead {LeadId} to {OwnerUserId}", dto.LeadId, dto.OwnerUserId);
            }
            catch (KeyNotFoundException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error assigning lead {LeadId}", dto.LeadId);
                throw;
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
