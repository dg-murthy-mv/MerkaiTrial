// =====================================================================
// LEAD REMINDERS HANDLERS
// Location: MerkaiTrial.Application/Commands/Leads/LeadReminderHandlers.cs
//
// COMPLETE FILE — replaces the existing one.
// LEGACY: reminders are tasks in the Activities table now. These handlers
// stay only because an endpoint still reaches them — scoped all the same.
//
// RECORD VISIBILITY (015): every handler here checks the current user can
// see the lead first (RecordScopeGuards). Writes on a lead outside scope
// → KeyNotFound → 404. Lists for a lead outside scope → empty, the same
// answer as for a lead that doesn't exist.
// =====================================================================

using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Security;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Commands.Leads
{
    // ==================== CREATE LEAD REMINDER ====================
    public class CreateLeadReminderHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly IRecordScopeService _scope;
        private readonly ILogger<CreateLeadReminderHandler> _logger;

        public CreateLeadReminderHandler(FlowDbContext context, IRecordScopeService scope, ILogger<CreateLeadReminderHandler> logger)
        {
            _context = context;
            _scope = scope;
            _logger = logger;
        }

        public async Task<LeadReminderDto> Handle(CreateLeadReminderDto dto, CancellationToken cancellationToken = default)
        {
            try
            {
                await _scope.EnsureLeadVisibleAsync(_context, dto.TenantId, dto.LeadId, cancellationToken);

                var reminder = new LeadReminder
                {
                    Id = Guid.NewGuid(),
                    LeadId = dto.LeadId,
                    TenantId = dto.TenantId,
                    Title = dto.Title,
                    Description = dto.Description,
                    ReminderDate = dto.ReminderDate,
                    IsCompleted = false,
                    CreatedAtUtc = DateTime.UtcNow,
                    CreatedBy = dto.CreatedBy
                };

                _context.Set<LeadReminder>().Add(reminder);
                await _context.SaveChangesAsync(cancellationToken);

                _logger.LogInformation("Created reminder {ReminderId} for lead {LeadId}", reminder.Id, dto.LeadId);

                return new LeadReminderDto(
                    Id: reminder.Id,
                    LeadId: reminder.LeadId,
                    Title: reminder.Title,
                    Description: reminder.Description,
                    ReminderDate: reminder.ReminderDate,
                    IsCompleted: reminder.IsCompleted,
                    CompletedAtUtc: reminder.CompletedAtUtc,
                    CreatedAtUtc: reminder.CreatedAtUtc,
                    CreatedBy: reminder.CreatedBy
                );
            }
            catch (KeyNotFoundException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating reminder for lead {LeadId}", dto.LeadId);
                throw;
            }
        }
    }

    // ==================== GET LEAD REMINDERS ====================
    public class GetLeadRemindersHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly IRecordScopeService _scope;
        private readonly ILogger<GetLeadRemindersHandler> _logger;

        public GetLeadRemindersHandler(FlowDbContext context, IRecordScopeService scope, ILogger<GetLeadRemindersHandler> logger)
        {
            _context = context;
            _scope = scope;
            _logger = logger;
        }

        public async Task<List<LeadReminderDto>> Handle(Guid tenantId, Guid leadId, CancellationToken cancellationToken = default)
        {
            try
            {
                if (!await _scope.CanSeeLeadAsync(_context, tenantId, leadId, cancellationToken))
                    return new List<LeadReminderDto>();

                var reminders = await _context.Set<LeadReminder>()
                    .AsNoTracking()
                    .Where(r => r.LeadId == leadId && r.TenantId == tenantId && !r.IsDeleted)
                    .OrderBy(r => r.ReminderDate)
                    .Select(r => new LeadReminderDto(
                        r.Id,
                        r.LeadId,
                        r.Title,
                        r.Description,
                        r.ReminderDate,
                        r.IsCompleted,
                        r.CompletedAtUtc,
                        r.CreatedAtUtc,
                        r.CreatedBy
                    ))
                    .ToListAsync(cancellationToken);

                return reminders;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting reminders for lead {LeadId}", leadId);
                throw;
            }
        }
    }

    // ==================== COMPLETE REMINDER ====================
    public class CompleteReminderHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly IRecordScopeService _scope;
        private readonly ILogger<CompleteReminderHandler> _logger;

        public CompleteReminderHandler(FlowDbContext context, IRecordScopeService scope, ILogger<CompleteReminderHandler> logger)
        {
            _context = context;
            _scope = scope;
            _logger = logger;
        }

        public async Task Handle(CompleteReminderDto dto, CancellationToken cancellationToken = default)
        {
            try
            {
                var reminder = await _context.Set<LeadReminder>()
                    .FirstOrDefaultAsync(r => r.Id == dto.ReminderId && r.TenantId == dto.TenantId && !r.IsDeleted, cancellationToken);

                if (reminder == null ||
                    !await _scope.CanSeeLeadAsync(_context, dto.TenantId, reminder.LeadId, cancellationToken))
                    throw new KeyNotFoundException($"Reminder {dto.ReminderId} not found");

                reminder.IsCompleted = true;
                reminder.CompletedAtUtc = DateTime.UtcNow;
                reminder.UpdatedAtUtc = DateTime.UtcNow;

                await _context.SaveChangesAsync(cancellationToken);

                _logger.LogInformation("Completed reminder {ReminderId}", dto.ReminderId);
            }
            catch (KeyNotFoundException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error completing reminder {ReminderId}", dto.ReminderId);
                throw;
            }
        }
    }
}