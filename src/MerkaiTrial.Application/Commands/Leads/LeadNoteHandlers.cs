// =====================================================================
// LEAD NOTES HANDLERS
// Location: MerkaiTrial.Application/Commands/Leads/LeadNoteHandlers.cs
// =====================================================================

using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Commands.Leads
{
    // ==================== CREATE LEAD NOTE ====================
    public class CreateLeadNoteHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILeadScoringService _scoring;
        private readonly ILogger<CreateLeadNoteHandler> _logger;

        public CreateLeadNoteHandler(FlowDbContext context, ILeadScoringService scoring, ILogger<CreateLeadNoteHandler> logger)
        {
            _context = context;
            _scoring = scoring;
            _logger = logger;
        }

        public async Task<LeadNoteDto> Handle(CreateLeadNoteDto dto, CancellationToken cancellationToken = default)
        {
            try
            {
                // Verify lead exists
                var leadExists = await _context.Leads
                    .AnyAsync(l => l.Id == dto.LeadId && l.TenantId == dto.TenantId && !l.IsDeleted, cancellationToken);

                if (!leadExists)
                    throw new KeyNotFoundException($"Lead {dto.LeadId} not found");

                var note = new LeadNote
                {
                    Id = Guid.NewGuid(),
                    LeadId = dto.LeadId,
                    TenantId = dto.TenantId,
                    Note = dto.Note,
                    CreatedAtUtc = DateTime.UtcNow,
                    CreatedBy = dto.CreatedBy
                };

                _context.Set<LeadNote>().Add(note);
                await _context.SaveChangesAsync(cancellationToken);

                // ✅ Note added → engagement score increases
                await _scoring.RecalculateAsync(dto.LeadId, dto.TenantId, cancellationToken);

                _logger.LogInformation("Created note {NoteId} for lead {LeadId}", note.Id, dto.LeadId);

                return new LeadNoteDto(
                    Id: note.Id,
                    LeadId: note.LeadId,
                    Note: note.Note,
                    CreatedAtUtc: note.CreatedAtUtc,
                    CreatedBy: note.CreatedBy
                );
            }
            catch (KeyNotFoundException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating note for lead {LeadId}", dto.LeadId);
                throw;
            }
        }
    }

    // ==================== GET LEAD NOTES ====================
    public class GetLeadNotesHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<GetLeadNotesHandler> _logger;

        public GetLeadNotesHandler(FlowDbContext context, ILogger<GetLeadNotesHandler> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<List<LeadNoteDto>> Handle(Guid tenantId, Guid leadId, CancellationToken cancellationToken = default)
        {
            try
            {
                var notes = await _context.Set<LeadNote>()
                    .AsNoTracking()
                    .Where(n => n.LeadId == leadId && n.TenantId == tenantId && !n.IsDeleted)
                    .OrderByDescending(n => n.CreatedAtUtc)
                    .Select(n => new LeadNoteDto(
                        n.Id,
                        n.LeadId,
                        n.Note,
                        n.CreatedAtUtc,
                        n.CreatedBy
                    ))
                    .ToListAsync(cancellationToken);

                return notes;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting notes for lead {LeadId}", leadId);
                throw;
            }
        }
    }

    // ==================== DELETE LEAD NOTE ====================
    public class DeleteLeadNoteHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<DeleteLeadNoteHandler> _logger;
        private readonly ILeadScoringService _scoring;
        public DeleteLeadNoteHandler(FlowDbContext context, ILeadScoringService scoring, ILogger<DeleteLeadNoteHandler> logger)
        {
            _context = context;
            _scoring = scoring;
            _logger = logger;
        }

        public async Task Handle(Guid tenantId, Guid noteId, CancellationToken cancellationToken = default)
        {
            try
            {
                var note = await _context.Set<LeadNote>()
                    .FirstOrDefaultAsync(n => n.Id == noteId && n.TenantId == tenantId && !n.IsDeleted,
                        cancellationToken);

                if (note == null)
                    throw new KeyNotFoundException($"Note {noteId} not found");

                var leadId = note.LeadId;

                note.IsDeleted = true;
                note.UpdatedAtUtc = DateTime.UtcNow;

                await _context.SaveChangesAsync(cancellationToken);

                // ✅ Note removed → engagement score may decrease
                await _scoring.RecalculateAsync(leadId, tenantId, cancellationToken);

                _logger.LogInformation("Deleted note {NoteId}", noteId);
            }
            catch (KeyNotFoundException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting note {NoteId}", noteId);
                throw;
            }
        }
    }
}