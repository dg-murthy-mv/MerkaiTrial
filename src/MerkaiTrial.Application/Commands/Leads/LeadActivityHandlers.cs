// =====================================================================
// LEAD ACTIVITIES HANDLERS
// Location: MerkaiTrial.Application/Commands/Leads/LeadActivityHandlers.cs
// =====================================================================

using MerkaiTrial.Application.DTOs;
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
        public CreateLeadActivityHandler(FlowDbContext context, ILeadScoringService scoring, ILogger<CreateLeadActivityHandler> logger)
        {
            _context = context;
            _scoring = scoring;
            _logger = logger;
        }

        public async Task<LeadActivityDto> Handle(CreateLeadActivityDto dto, CancellationToken cancellationToken = default)
        {
            try
            {
                // Verify lead exists
                var leadExists = await _context.Leads
                    .AnyAsync(l => l.Id == dto.LeadId && l.TenantId == dto.TenantId && !l.IsDeleted, cancellationToken);

                if (!leadExists)
                    throw new KeyNotFoundException($"Lead {dto.LeadId} not found");

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
        private readonly ILogger<GetLeadActivitiesHandler> _logger;

        public GetLeadActivitiesHandler(FlowDbContext context, ILogger<GetLeadActivitiesHandler> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<List<LeadActivityDto>> Handle(Guid tenantId, Guid leadId, CancellationToken cancellationToken = default)
        {
            try
            {
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