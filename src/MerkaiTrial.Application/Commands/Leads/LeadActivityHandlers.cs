// =====================================================================
// LEAD ACTIVITIES HANDLERS
// Location: MerkaiTrial.Application/Commands/Leads/LeadActivityHandlers.cs
//
// COMPLETE FILE — replaces the existing one.
// LEGACY: writes to LeadActivities. The Lead page uses the unified
// Activities table (ActivityHandlers). Kept and scoped because an
// endpoint still reaches it.
//
// RECORD VISIBILITY (015): every handler here checks the current user can
// see the lead first (RecordScopeGuards). Writes on a lead outside scope
// → KeyNotFound → 404. Lists for a lead outside scope → empty, the same
// answer as for a lead that doesn't exist.
// =====================================================================

using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Security;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Commands.Leads
{
    // ==================== CREATE LEAD ACTIVITY ====================
    public class CreateLeadActivityHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<CreateLeadActivityHandler> _logger;
        private readonly ILeadScoringService _scoring;
        private readonly IRecordScopeService _scope;
        public CreateLeadActivityHandler(FlowDbContext context, ILeadScoringService scoring,
            IRecordScopeService scope, ILogger<CreateLeadActivityHandler> logger)
        {
            _context = context;
            _scope = scope;
            _scoring = scoring;
            _logger = logger;
        }

        public async Task<LeadActivityDto> Handle(CreateLeadActivityDto dto, CancellationToken cancellationToken = default)
        {
            try
            {
                await _scope.EnsureLeadVisibleAsync(_context, dto.TenantId, dto.LeadId, cancellationToken);

                var activity = new LeadActivity
                {
                    Id = Guid.NewGuid(),
                    LeadId = dto.LeadId,
                    TenantId = dto.TenantId,
                    ActivityType = dto.ActivityType,
                    Subject = dto.Subject,
                    Description = dto.Description,
                    Duration = dto.Duration,
                    ActivityDate = dto.ActivityDate ?? DateTime.UtcNow,
                    CreatedAtUtc = DateTime.UtcNow,
                    CreatedBy = dto.CreatedBy
                };

                _context.Set<LeadActivity>().Add(activity);
                await _context.SaveChangesAsync(cancellationToken);

                // ✅ Activity logged → biggest engagement score boost
                await _scoring.RecalculateAsync(dto.LeadId, dto.TenantId, cancellationToken);

                _logger.LogInformation("Created activity {ActivityId} for lead {LeadId}", activity.Id, dto.LeadId);

                return new LeadActivityDto(
                    Id: activity.Id,
                    LeadId: activity.LeadId,
                    ActivityType: activity.ActivityType,
                    Subject: activity.Subject,
                    Description: activity.Description,
                    Duration: activity.Duration,
                    ActivityDate: activity.ActivityDate,
                    CreatedAtUtc: activity.CreatedAtUtc,
                    CreatedBy: activity.CreatedBy
                );
            }
            catch (KeyNotFoundException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating activity for lead {LeadId}", dto.LeadId);
                throw;
            }
        }
    }

    // ==================== GET LEAD ACTIVITIES ====================
    public class GetLeadActivitiesHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly IRecordScopeService _scope;
        private readonly ILogger<GetLeadActivitiesHandler> _logger;

        public GetLeadActivitiesHandler(FlowDbContext context, IRecordScopeService scope, ILogger<GetLeadActivitiesHandler> logger)
        {
            _context = context;
            _scope = scope;
            _logger = logger;
        }

        public async Task<List<LeadActivityDto>> Handle(Guid tenantId, Guid leadId, CancellationToken cancellationToken = default)
        {
            try
            {
                if (!await _scope.CanSeeLeadAsync(_context, tenantId, leadId, cancellationToken))
                    return new List<LeadActivityDto>();

                var activities = await _context.Set<LeadActivity>()
                    .AsNoTracking()
                    .Where(a => a.LeadId == leadId && a.TenantId == tenantId && !a.IsDeleted)
                    .OrderByDescending(a => a.ActivityDate)
                    .Select(a => new LeadActivityDto(
                        a.Id,
                        a.LeadId,
                        a.ActivityType,
                        a.Subject,
                        a.Description,
                        a.Duration,
                        a.ActivityDate,
                        a.CreatedAtUtc,
                        a.CreatedBy
                    ))
                    .ToListAsync(cancellationToken);

                return activities;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting activities for lead {LeadId}", leadId);
                throw;
            }
        }
    }
}