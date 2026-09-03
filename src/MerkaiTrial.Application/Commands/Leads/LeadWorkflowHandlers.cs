// =====================================================================
// LEAD WORKFLOW HANDLERS
// Location: MerkaiTrial.Application/Commands/Leads/LeadWorkflowHandlers.cs
// =====================================================================

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
        private readonly FlowDbContext _context;
        private readonly ILogger<GetLeadTimelineHandler> _logger;

        public GetLeadTimelineHandler(FlowDbContext context, ILogger<GetLeadTimelineHandler> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<List<TimelineItemDto>> Handle(Guid tenantId, Guid leadId, CancellationToken cancellationToken = default)
        {
            try
            {
                var timeline = new List<TimelineItemDto>();

                // ✅ FIXED: Use AnyAsync first to check if lead exists
                var leadExists = await _context.Leads
                    .AsNoTracking()
                    .AnyAsync(l => l.Id == leadId && l.TenantId == tenantId && !l.IsDeleted, cancellationToken);

                if (!leadExists)
                {
                    _logger.LogWarning("Lead {LeadId} not found for tenant {TenantId}", leadId, tenantId);
                    return timeline; // ✅ Return empty timeline instead of throwing exception
                }

                // Get lead for creation event
                var lead = await _context.Leads
                    .AsNoTracking()
                    .FirstOrDefaultAsync(l => l.Id == leadId && l.TenantId == tenantId && !l.IsDeleted, cancellationToken);

                if (lead != null)
                {
                    timeline.Add(new TimelineItemDto(
                        Id: lead.Id,
                        Type: "Created",
                        Title: "Lead Created",
                        Description: $"Lead '{lead.FullName}' was created",
                        Date: lead.CreatedAtUtc,
                        CreatedBy: lead.CreatedBy,
                        Icon: "bi-plus-circle",
                        BadgeClass: "bg-primary"
                    ));
                }

                // Get notes
                var notes = await _context.Set<Domain.Entities.LeadNote>()
                    .AsNoTracking()
                    .Where(n => n.LeadId == leadId && n.TenantId == tenantId && !n.IsDeleted)
                    .ToListAsync(cancellationToken);

                foreach (var note in notes)
                {
                    timeline.Add(new TimelineItemDto(
                        Id: note.Id,
                        Type: "Note",
                        Title: "Note Added",
                        Description: note.Note?.Length > 100 ? note.Note.Substring(0, 100) + "..." : note.Note,
                        Date: note.CreatedAtUtc,
                        CreatedBy: note.CreatedBy,
                        Icon: "bi-chat-left-text",
                        BadgeClass: "bg-info"
                    ));
                }

                // Get activities
                var activities = await _context.Set<Domain.Entities.LeadActivity>()
                    .AsNoTracking()
                    .Where(a => a.LeadId == leadId && a.TenantId == tenantId && !a.IsDeleted)
                    .ToListAsync(cancellationToken);

                foreach (var activity in activities)
                {
                    timeline.Add(new TimelineItemDto(
                        Id: activity.Id,
                        Type: "Activity",
                        Title: $"{activity.ActivityType}: {activity.Subject}",
                        Description: activity.Description,
                        Date: activity.ActivityDate,
                        CreatedBy: activity.CreatedBy,
                        Icon: activity.ActivityType switch
                        {
                            "Call" => "bi-telephone",
                            "Email" => "bi-envelope",
                            "Meeting" => "bi-calendar-event",
                            "SMS" => "bi-chat",
                            "WhatsApp" => "bi-whatsapp",
                            "Task" => "bi-check-square",
                            "Note" => "bi-sticky",
                            _ => "bi-activity"
                        },
                        BadgeClass: "bg-success"
                    ));
                }

                // Get reminders
                var reminders = await _context.Set<Domain.Entities.LeadReminder>()
                    .AsNoTracking()
                    .Where(r => r.LeadId == leadId && r.TenantId == tenantId && !r.IsDeleted)
                    .ToListAsync(cancellationToken);

                foreach (var reminder in reminders)
                {
                    timeline.Add(new TimelineItemDto(
                        Id: reminder.Id,
                        Type: "Reminder",
                        Title: reminder.Title,
                        Description: reminder.Description,
                        Date: reminder.ReminderDate,
                        CreatedBy: reminder.CreatedBy,
                        Icon: "bi-bell",
                        BadgeClass: reminder.IsCompleted ? "bg-secondary" : "bg-warning"
                    ));
                }

                // Sort by date descending (newest first)
                return timeline.OrderByDescending(t => t.Date).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting timeline for lead {LeadId}", leadId);
                throw;
            }
        }
    }
}