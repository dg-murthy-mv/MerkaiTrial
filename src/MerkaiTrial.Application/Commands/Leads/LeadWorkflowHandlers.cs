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
//   AssignLeadHandler is unchanged.
// =====================================================================

using MerkaiTrial.Application.Commands.Activities;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Commands.Leads
{
    // ==================== ASSIGN LEAD ====================
    public class AssignLeadHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<AssignLeadHandler> _logger;

        public AssignLeadHandler(FlowDbContext context, ILogger<AssignLeadHandler> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task Handle(AssignLeadDto dto, CancellationToken cancellationToken = default)
        {
            try
            {
                var lead = await _context.Leads
                    .FirstOrDefaultAsync(l => l.Id == dto.LeadId && l.TenantId == dto.TenantId && !l.IsDeleted, cancellationToken);

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

        public GetLeadTimelineHandler(GetTimelineHandler timeline) => _timeline = timeline;

        public Task<List<TimelineItemDto>> Handle(
            Guid tenantId, Guid leadId, CancellationToken cancellationToken = default)
            => _timeline.Handle(
                new GetTimelineQuery(tenantId, ActivityEntityType.Lead, leadId),
                cancellationToken);
    }
}
