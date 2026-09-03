// =====================================================================
// LEAD COMMANDS - Write Operations
// Location: MerkaiTrial.Application/Commands/Leads/LeadCommandHandlers.cs
//
// FIXES APPLIED:
//   1. CreateLeadHandler    — Currency falls back to ICurrentTenantService
//   2. ConvertLeadHandler   — Country no longer hardcoded to "IN"
//   3. UpdateLeadHandler    — OwnerUserId string→Guid fix (index-safe)
// =====================================================================

using DocumentFormat.OpenXml.Spreadsheet;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Exceptions;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Domain.Enums;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Commands.Leads
{
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

        public CreateLeadHandler(
            FlowDbContext         context,
            ICurrentUserService   currentUserService,
            ICurrentTenantService tenantService,
            ILeadScoringService scoring,
            ILogger<CreateLeadHandler> logger)
        {
            _context            = context;
            _currentUserService = currentUserService;
            _tenantService      = tenantService;
            _scoring = scoring;
            _logger             = logger;
        }

        public async Task<LeadDto> Handle(CreateLeadDto dto, CancellationToken cancellationToken = default)
        {
            try
            {
                var currentUserId = _currentUserService.GetCurrentUserId();

                // FIX 1: Currency from tenant if not provided in DTO
                var currency = !string.IsNullOrWhiteSpace(dto.Currency)
                    ? dto.Currency.Trim().ToUpperInvariant()
                    : _tenantService.GetCurrencyCode();

                // ── ✅ Plan quota check ───────────────────────────────────────────
                // ── Plan quota check ──────────────────────────────────────────────
                var planSettings = await _context.TenantSettings
                    .AsNoTracking()
                    .FirstOrDefaultAsync(ts => ts.TenantId == dto.TenantId, cancellationToken);

                if (planSettings != null)
                {
                    var currentLeadCount = await _context.Leads
                        .CountAsync(l => l.TenantId == dto.TenantId && !l.IsDeleted, cancellationToken);

                    if (currentLeadCount >= planSettings.MaxLeads)
                        throw new PlanLimitExceededException("leads", currentLeadCount, planSettings.MaxLeads);
                }
                // ── end quota check ───────────────────────────────────────────────

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
                    OwnerUserId = dto.OwnerUserId,
                    Status      = LeadStatus.New,
                    Score       = 0,
                    IsConverted = false,
                    CreatedAtUtc = DateTime.UtcNow,
                    CreatedBy   = currentUserId.ToString()
                };

                _context.Leads.Add(lead);
                await _context.SaveChangesAsync(cancellationToken);

                // ✅ Recalculate score immediately after creation
                await _scoring.RecalculateAsync(lead.Id, lead.TenantId, cancellationToken);

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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating lead");
                throw;
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
        private readonly FlowDbContext         _context;
        private readonly ICurrentUserService   _currentUserService;
        private readonly ICurrentTenantService _tenantService;
        private readonly ILogger<UpdateLeadHandler> _logger;
        private readonly ILeadScoringService _scoring;
        public UpdateLeadHandler(
            FlowDbContext         context,
            ICurrentUserService   currentUserService,
            ICurrentTenantService tenantService,
            ILeadScoringService scoring,
            ILogger<UpdateLeadHandler> logger)
        {
            _context            = context;
            _currentUserService = currentUserService;
            _tenantService      = tenantService;
            _scoring = scoring;
            _logger             = logger;
        }

        public async Task<LeadDetailDto> Handle(UpdateLeadDto dto, CancellationToken cancellationToken = default)
        {
            try
            {
                var lead = await _context.Leads
                    .FirstOrDefaultAsync(l => l.Id == dto.LeadId && l.TenantId == dto.TenantId && !l.IsDeleted, cancellationToken);

                if (lead == null)
                    throw new KeyNotFoundException($"Lead {dto.LeadId} not found");

                var currentUserId = _currentUserService.GetCurrentUserId();

                // FIX 1: Currency from tenant if not provided
                var currency = !string.IsNullOrWhiteSpace(dto.Currency)
                    ? dto.Currency.Trim().ToUpperInvariant()
                    : _tenantService.GetCurrencyCode();

                lead.FullName      = dto.FullName;
                lead.Email         = dto.Email;
                lead.Phone         = dto.Phone;
                lead.CompanyName   = dto.CompanyName;
                lead.Address       = dto.Address;
                lead.CountryId     = dto.CountryId;
                lead.Currency      = currency;
                lead.ChannelId     = dto.ChannelId;
                lead.SourceId      = dto.SourceId;
                lead.VerticalId    = dto.VerticalId;
                lead.Score         = dto.Score;
                lead.EstimatedValue = dto.EstimatedValue;
                lead.OwnerUserId   = dto.OwnerUserId;
                lead.UpdatedAtUtc  = DateTime.UtcNow;
                lead.UpdatedBy     = currentUserId.ToString();

                await _context.SaveChangesAsync(cancellationToken);

                // ✅ Recalculate score after any profile change
                await _scoring.RecalculateAsync(lead.Id, lead.TenantId, cancellationToken);

                _logger.LogInformation("Updated lead {LeadId} by user {UserId}", dto.LeadId, currentUserId);

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

                string? ownerName     = null;
                string? ownerJobTitle = null;

                // FIX 2: Guid comparison — not string cast
                if (Guid.TryParse(lead.OwnerUserId, out var ownerGuid))
                {
                    var owner = await _context.Users
                        .Where(u => u.Id == ownerGuid)
                        .Select(u => new { u.FullName, u.JobTitle })
                        .FirstOrDefaultAsync(cancellationToken);

                    ownerName     = owner?.FullName;
                    ownerJobTitle = owner?.JobTitle;
                }

                return new LeadDetailDto(
                    Id:           lead.Id,
                    TenantId:     lead.TenantId,
                    ContactId:    lead.ContactId ?? Guid.Empty,
                    FullName:     lead.FullName,
                    Email:        lead.Email ?? "",
                    Phone:        lead.Phone ?? "",
                    CompanyName:  lead.CompanyName,
                    Address:      lead.Address,
                    CountryId:    lead.CountryId,
                    CountryName:  countryName,
                    Currency:     currency,
                    ChannelId:    lead.ChannelId,
                    SourceId:     lead.SourceId,
                    Channel:      channelName ?? lead.Channel.ToString(),
                    Source:       sourceName  ?? lead.Source,
                    VerticalId:   lead.VerticalId,
                    VerticalName: verticalName,
                    Status:       lead.Status.ToString(),
                    Score:        lead.Score,
                    OwnerUserId:  lead.OwnerUserId,
                    OwnerName:    ownerName,
                    OwnerJobTitle: ownerJobTitle,
                    CreatedAtUtc: lead.CreatedAtUtc,
                    UpdatedAtUtc: lead.UpdatedAtUtc,
                    HasDeal:      lead.DealId.HasValue,
                    IsConverted: lead.Status == LeadStatus.Converted,
                    DealId:       lead.DealId,
                    DealStage:    "",
                    ExpectedValue: lead.EstimatedValue ?? 0
                );
            }
            catch (KeyNotFoundException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating lead {LeadId}", dto.LeadId);
                throw;
            }
        }
    }

    // ==================== UPDATE LEAD STATUS ====================
    public class UpdateLeadStatusHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<UpdateLeadStatusHandler> _logger;
        private readonly ILeadScoringService _scoring;

        public UpdateLeadStatusHandler(FlowDbContext context, ILeadScoringService scoring, ILogger<UpdateLeadStatusHandler> logger)
        {
            _context = context;
            _scoring = scoring;
            _logger  = logger;
        }

        public async Task Handle(UpdateLeadStatusDto dto, CancellationToken cancellationToken = default)
        {
            try
            {
                var lead = await _context.Leads
                    .FirstOrDefaultAsync(l => l.Id == dto.LeadId && l.TenantId == dto.TenantId && !l.IsDeleted, cancellationToken);

                if (lead == null)
                    throw new KeyNotFoundException($"Lead {dto.LeadId} not found");

                var oldStatus = lead.Status;
                lead.Status       = dto.Status;
                lead.UpdatedAtUtc = DateTime.UtcNow;

                await _context.SaveChangesAsync(cancellationToken);
                // ✅ Status progression affects score
                await _scoring.RecalculateAsync(lead.Id, lead.TenantId, cancellationToken);

                _logger.LogInformation("Updated lead {LeadId} status {Old} → {New}", dto.LeadId, oldStatus, dto.Status);
            }
            catch (KeyNotFoundException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating lead status {LeadId}", dto.LeadId);
                throw;
            }
        }

        public async Task Handle(Guid tenantId, Guid leadId, LeadStatus status, CancellationToken ct = default)
            => await Handle(new UpdateLeadStatusDto(tenantId, leadId, status), ct);
    }

    // ==================== DELETE LEAD ====================
    public record DeleteLeadCommand(Guid TenantId, Guid LeadId);

    public class DeleteLeadHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<DeleteLeadHandler> _logger;

        public DeleteLeadHandler(FlowDbContext context, ILogger<DeleteLeadHandler> logger)
        {
            _context = context;
            _logger  = logger;
        }

        public async Task Handle(DeleteLeadCommand command, CancellationToken cancellationToken = default)
        {
            try
            {
                var lead = await _context.Leads
                    .FirstOrDefaultAsync(l => l.Id == command.LeadId && l.TenantId == command.TenantId && !l.IsDeleted, cancellationToken);

                if (lead == null)
                    throw new KeyNotFoundException($"Lead {command.LeadId} not found");

                lead.IsDeleted    = true;
                lead.UpdatedAtUtc = DateTime.UtcNow;

                await _context.SaveChangesAsync(cancellationToken);

                _logger.LogInformation("Deleted lead {LeadId}", command.LeadId);
            }
            catch (KeyNotFoundException) { throw; }
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

        public ConvertLeadHandler(
            FlowDbContext         context,
            ICurrentTenantService tenantService,
            ILogger<ConvertLeadHandler> logger)
        {
            _context       = context;
            _tenantService = tenantService;
            _logger        = logger;
        }

        public async Task<ConversionResult> Handle(ConvertLeadCommand cmd, CancellationToken cancellationToken = default)
        {
            using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

            try
            {
                var lead = await _context.Leads
                    .FirstOrDefaultAsync(l => l.Id == cmd.LeadId && l.TenantId == cmd.TenantId && !l.IsDeleted, cancellationToken);

                if (lead == null)
                    throw new KeyNotFoundException($"Lead {cmd.LeadId} not found");

                if (lead.IsConverted)
                    throw new InvalidOperationException("Lead already converted");

                // Create Company if requested
                Guid? companyId = cmd.ExistingCompanyId;
                if (cmd.CreateCompany && !string.IsNullOrEmpty(cmd.CompanyName))
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

                // Mark lead converted
                lead.IsConverted          = true;
                lead.ConvertedToContactId = contact.Id;
                lead.ConvertedToCompanyId = companyId;
                lead.ConvertedAtUtc       = DateTime.UtcNow;
                lead.ConvertedBy          = cmd.ConvertedBy;
                lead.Status               = LeadStatus.Converted;

                await _context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

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
