// =====================================================================
// DEALS COMMAND HANDLERS
// Location: MerkaiTrial.Application/Commands/Deals/DealsCommandHandlers.cs
// Changes:
//   ✅ GetDealDetailHandler — joins CompanyVerticals, populates VerticalId/VerticalName
//   ✅ CreateDealHandler    — sets VerticalId from CreateDealDto
//   ✅ UpdateDealHandler    — sets VerticalId from UpdateDealDto
// =====================================================================

using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Exceptions;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Commands.Deals
{
    // ==================== GET DEALS BY CONTACT ====================

    public class GetDealsByContactRequest
    {
        public string TenantId { get; set; } = string.Empty;
        public Guid ContactId { get; set; }
    }

    /// <summary>Lightweight DTO for the Contact → Deals panel.</summary>
    public record ContactDealItem(
        Guid Id,
        string Title,
        string Stage,
        decimal ExpectedValue,
        string Currency,
        string? CompanyName,
        DateTime? ExpectedCloseDateUtc,
        int Probability
    );

    public class GetDealsByContactHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;

        public GetDealsByContactHandler(FlowDbContext db) => _db = db;

        public async Task<List<ContactDealItem>> HandleAsync(GetDealsByContactRequest request)
        {
            var deals = await (
                from d in _db.Deals
                join comp in _db.Companies on d.CompanyId equals (Guid?)comp.Id into compGroup
                from comp in compGroup.DefaultIfEmpty()
                where d.TenantId.ToString() == request.TenantId
                   && d.ContactId == request.ContactId
                   && !d.IsDeleted
                orderby d.CreatedAtUtc descending
                select new ContactDealItem(
                    d.Id,
                    d.Title,
                    d.Stage,
                    d.ExpectedValue,
                    d.Currency ?? "",
                    comp != null ? comp.Name : null,
                    d.ExpectedCloseDateUtc,
                    d.Probability
                )
            ).ToListAsync();

            return deals;
        }
    }
    public class TransitionDealStageDto
    {
        public string Stage { get; set; } = string.Empty;
        public int Probability { get; set; }
    }
    public class TransitionDealStageHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        public TransitionDealStageHandler(FlowDbContext db) => _db = db;

        public async Task HandleAsync(
            string tenantId, Guid dealId, string toStage, int probability, string changedBy)
        {
            var deal = await _db.Deals
                .FirstOrDefaultAsync(d =>
                    d.Id == dealId &&
                    d.TenantId.ToString() == tenantId &&
                    !d.IsDeleted);

            if (deal == null)
                throw new KeyNotFoundException($"Deal {dealId} not found");

            if (deal.Stage is "Won" or "Lost" or "ClosedWon" or "ClosedLost")
                return;  // already terminal — no-op

            var fromStage = deal.Stage;
            deal.Stage = toStage;
            deal.Probability = probability;
            deal.UpdatedAtUtc = DateTime.UtcNow;
            deal.UpdatedBy = changedBy;

            _db.DealStageHistory.Add(new DealStageHistory
            {
                Id = Guid.NewGuid(),
                DealId = dealId,
                FromStage = fromStage,
                ToStage = toStage,
                ChangedAtUtc = DateTime.UtcNow,
                ChangedBy = changedBy
            });

            await _db.SaveChangesAsync();
        }
    }
    // ==================== GET DEALS LIST ====================

    public class GetDealsHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        public GetDealsHandler(FlowDbContext db) => _db = db;

        public async Task<GetDealsResponse> HandleAsync(GetDealsRequest request)
        {
            // Previously: "d.TenantId.ToString() == request.TenantId" forces
            // SQL Server to convert every row's TenantId to a string before
            // it can compare, which defeats any index on TenantId entirely
            // (turns an index seek into a full scan). Parsing once here and
            // comparing Guid-to-Guid lets the index actually get used.
            var tenantId = Guid.Parse(request.TenantId);

            var query =
                from d    in _db.Deals
                join c    in _db.Contacts  on d.ContactId  equals c.Id
                join comp in _db.Companies on d.CompanyId  equals (Guid?)comp.Id into compGroup
                from comp in compGroup.DefaultIfEmpty()
                where d.TenantId == tenantId && !d.IsDeleted
                select new { Deal = d, Contact = c, Company = comp };

            if (!string.IsNullOrEmpty(request.Stage))
                query = query.Where(x => x.Deal.Stage == request.Stage);

            if (!string.IsNullOrEmpty(request.Search))
            {
                var q = request.Search.ToLower();
                query = query.Where(x =>
                    x.Deal.Title.ToLower().Contains(q) ||
                    (x.Company != null && x.Company.Name.ToLower().Contains(q)));
            }

            if (!string.IsNullOrEmpty(request.OwnerUserId))
                query = query.Where(x => x.Deal.OwnerUserId == request.OwnerUserId);

            var total = await query.CountAsync();

            var rows = await query
                .OrderByDescending(x => x.Deal.CreatedAtUtc)
                .Skip((request.Page - 1) * request.PageSize)
                .Take(request.PageSize)
                .ToListAsync();

            var ownerIds = rows
                .Where(x => !string.IsNullOrEmpty(x.Deal.OwnerUserId))
                .Select(x => x.Deal.OwnerUserId!)
                .Distinct().ToList();

            var ownerIdsLower = ownerIds.Select(id => id.ToLowerInvariant()).ToList();

            var owners = ownerIdsLower.Any()
                ? await _db.Users
                    .Where(u => ownerIdsLower.Contains(u.Id.ToString().ToLower()))
                    .Select(u => new { Id = u.Id.ToString().ToLower(), u.FirstName, u.LastName })
                    .ToListAsync()
                : new();

            // OrdinalIgnoreCase so DD100003-... and dd100003-... both match
            var ownerMap = owners.ToDictionary(u => u.Id, StringComparer.OrdinalIgnoreCase);

            var items = rows.Select(x =>
            {
                ownerMap.TryGetValue(x.Deal.OwnerUserId ?? "", out var owner);
                var ownerName     = owner != null ? $"{owner.FirstName} {owner.LastName}".Trim() : "Unassigned";
                var ownerInitials = owner != null
                    ? $"{owner.FirstName[..1]}{owner.LastName[..1]}".ToUpper()
                    : "UN";

                return new DealListItem(
                    x.Deal.Id,
                    x.Deal.Title,
                    x.Company?.Name,
                    x.Deal.Stage,
                    x.Deal.ExpectedValue,
                    x.Deal.Currency ?? "",
                    x.Deal.ExpectedCloseDateUtc,
                    x.Deal.ActualCloseDateUtc,
                    x.Deal.OwnerUserId,
                    ownerName,
                    ownerInitials,
                    x.Deal.Probability
                );
            }).ToList();

            return new GetDealsResponse(items, total, request.Page, request.PageSize);
        }
    }

    // ==================== GET DEAL DETAIL ====================

    public class GetDealDetailHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        public GetDealDetailHandler(FlowDbContext db) => _db = db;

        public async Task<DealDetailDto> HandleAsync(string tenantId, Guid dealId)
        {
            // ✅ Join CompanyVerticals to resolve VerticalName
            var row = await (
                from deal in _db.Deals
                join c    in _db.Contacts      on deal.ContactId  equals c.Id
                join comp in _db.Companies     on deal.CompanyId  equals (Guid?)comp.Id  into compGroup
                from comp in compGroup.DefaultIfEmpty()
                join vert in _db.CompanyVerticals on deal.VerticalId equals (Guid?)vert.Id into vertGroup
                from vert in vertGroup.DefaultIfEmpty()
                where deal.Id == dealId && deal.TenantId.ToString() == tenantId && !deal.IsDeleted
                select new { Deal = deal, Contact = c, Company = comp, Vertical = vert }
            ).FirstOrDefaultAsync();

            if (row is null)
                throw new KeyNotFoundException($"Deal {dealId} not found");

            // Owner — resolve separately
            string? ownerName = null, ownerInitials = null;
            if (!string.IsNullOrEmpty(row.Deal.OwnerUserId))
            {
                var owner = await _db.Users
                    .Where(u => u.Id.ToString() == row.Deal.OwnerUserId)
                    .Select(u => new { u.FirstName, u.LastName })
                    .FirstOrDefaultAsync();

                if (owner != null)
                {
                    ownerName     = $"{owner.FirstName} {owner.LastName}".Trim();
                    ownerInitials = $"{owner.FirstName[..1]}{owner.LastName[..1]}".ToUpper();
                }
            }

            string? sourceLabel = null;
            if (row.Deal.SourceId.HasValue)
            {
                sourceLabel = await _db.LeadSources
                    .Where(s => s.Id == row.Deal.SourceId.Value)
                    .Select(s => s.Name)
                    .FirstOrDefaultAsync();
            }

            var d = row.Deal;

            return new DealDetailDto(
                d.Id,
                d.TenantId.ToString(),
                d.Title,
                d.Description,
                d.Stage,
                d.Probability,
                d.ExpectedValue,
                d.Currency ?? "",
                d.ExpectedCloseDateUtc,
                d.ActualCloseDateUtc,
                row.Company?.Name,
                d.CompanyId,
                d.ContactId,
                $"{row.Contact.FirstName} {row.Contact.LastName}".Trim(),
                d.OwnerUserId,
                ownerName,
                ownerInitials,
                d.LeadId,
                sourceLabel,
                d.Tags,
                d.CreatedAtUtc,
                d.CreatedBy,
                d.UpdatedAtUtc,
                d.UpdatedBy,
                // ✅ Vertical
                d.VerticalId,
                row.Vertical?.Name
            );
        }
    }

    // ==================== GET DEAL BY ID (summary) ====================

    public class GetDealByIdHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        public GetDealByIdHandler(FlowDbContext db) => _db = db;

        public async Task<DealDto> HandleAsync(string tenantId, Guid dealId)
        {
            var row = await (
                from deal in _db.Deals
                join c    in _db.Contacts  on deal.ContactId equals c.Id
                join comp in _db.Companies on deal.CompanyId equals (Guid?)comp.Id into compGroup
                from comp in compGroup.DefaultIfEmpty()
                where deal.Id == dealId && deal.TenantId.ToString() == tenantId && !deal.IsDeleted
                select new { Deal = deal, Contact = c, Company = comp }
            ).FirstOrDefaultAsync();

            if (row is null)
                throw new KeyNotFoundException($"Deal {dealId} not found");

            var d = row.Deal;

            return new DealDto(
                d.Id, d.TenantId.ToString(), d.Title, d.Description,
                d.Stage, d.Probability, d.ExpectedValue, d.Currency ?? "",
                d.ExpectedCloseDateUtc, d.ActualCloseDateUtc, d.ActualValue,
                d.CompanyId, row.Company?.Name,
                d.ContactId, $"{row.Contact.FirstName} {row.Contact.LastName}".Trim(),
                d.OwnerUserId, null, d.LeadId, null, d.Tags,
                d.CreatedAtUtc, d.CreatedBy, d.UpdatedAtUtc, d.UpdatedBy
            );
        }
    }

    // ==================== CREATE DEAL ====================

    public class CreateDealHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUserService;
        private readonly ILogger<CreateDealHandler> _logger;

        public CreateDealHandler(
            FlowDbContext db,
            ICurrentUserService currentUserService,
            ILogger<CreateDealHandler> logger)
        {
            _db = db;
            _currentUserService = currentUserService;
            _logger = logger;
        }

        public async Task<DealDto> HandleAsync(CreateDealDto dto)
        {
            var currentUser = await _currentUserService.GetCurrentUserAsync();

            // ── ✅ Plan quota check ───────────────────────────────────────────
            // dto.TenantId is string → parse to Guid for TenantSettings query
            // Deal.TenantId is Guid  → direct comparison, no .ToString() needed
            var tenantGuid = Guid.Parse(dto.TenantId);

            var planSettings = await _db.TenantSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(ts => ts.TenantId == tenantGuid);

            if (planSettings != null)
            {
                var currentDealCount = await _db.Deals
                    .CountAsync(d => d.TenantId == tenantGuid && !d.IsDeleted);

                if (currentDealCount >= planSettings.MaxDeals)
                    throw new PlanLimitExceededException(
                        "deals", currentDealCount, planSettings.MaxDeals);
            }
            // ── end quota check ───────────────────────────────────────────────


            // ✅ Validate stage — falls back to Discovery (not "New")
            var stage = DealStages.IsValid(dto.Stage) ? dto.Stage : DealStages.Discovery;
            var probability = dto.Probability ?? DealStages.DefaultProbability(stage);

            var deal = new Deal
            {
                Id = Guid.NewGuid(),
                TenantId = Guid.Parse(dto.TenantId),
                ContactId = dto.ContactId,
                LeadId = dto.LeadId,
                CompanyId = dto.CompanyId,
                Title = dto.Title,
                Description = dto.Description,
                Stage = stage,
                ExpectedValue = dto.ExpectedValue,
                // ✅ Multi-tenant: currency stored per-deal, set from tenant on create
                Currency = dto.Currency,
                VerticalId = dto.VerticalId,
                // ✅ FIXED: Source stored as string name — no int→Guid conversion
                SourceId = dto.SourceId,
                Source = dto.Source,
                // SourceId removed — was causing new Guid(int.ToString()) crash
                ExpectedCloseDateUtc = dto.ExpectedCloseDateUtc,
                OwnerUserId = dto.OwnerUserId ?? currentUser.UserId.ToString(),
                Probability = probability,
                Tags = dto.Tags,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = dto.CreatedBy ?? currentUser.FullName,
                UpdatedAtUtc = DateTime.UtcNow,
                UpdatedBy = currentUser.FullName,
                IsDeleted = false
            };

            _db.Deals.Add(deal);

            _db.DealStageHistory.Add(new DealStageHistory
            {
                Id = Guid.NewGuid(),
                TenantId = Guid.Parse(dto.TenantId),
                DealId = deal.Id,
                FromStage = null,
                ToStage = stage,
                ChangedAtUtc = DateTime.UtcNow,
                ChangedBy = deal.CreatedBy
            });

            await _db.SaveChangesAsync();

            _logger.LogInformation(
                "Deal '{Title}' created at stage {Stage} for tenant {TenantId}",
                deal.Title, stage, dto.TenantId);

            // Re-query to get Contact + Company names for the DTO
            var row = await (
                from d in _db.Deals
                join c in _db.Contacts on d.ContactId equals c.Id
                join comp in _db.Companies on d.CompanyId equals (Guid?)comp.Id into compGroup
                from comp in compGroup.DefaultIfEmpty()
                where d.Id == deal.Id
                select new { Deal = d, Contact = c, Company = comp }
            ).FirstAsync();

            return new DealDto(
                row.Deal.Id,
                row.Deal.TenantId.ToString(),
                row.Deal.Title,
                row.Deal.Description,
                row.Deal.Stage,
                row.Deal.Probability,
                row.Deal.ExpectedValue,
                row.Deal.Currency ?? "",
                row.Deal.ExpectedCloseDateUtc,
                row.Deal.ActualCloseDateUtc,
                row.Deal.ActualValue,
                row.Deal.CompanyId,
                row.Company?.Name,
                row.Deal.ContactId,
                $"{row.Contact.FirstName} {row.Contact.LastName}".Trim(),
                row.Deal.OwnerUserId,
                null,   // OwnerName — resolved separately in GetDealDetailHandler
                row.Deal.LeadId,
                row.Deal.SourceId,
                row.Deal.Tags,
                row.Deal.CreatedAtUtc,
                row.Deal.CreatedBy,
                row.Deal.UpdatedAtUtc,
                row.Deal.UpdatedBy
            );
        }
    }


    // ==================== UPDATE DEAL ====================

    public class UpdateDealHandler : ICommandHandler
    {
        private readonly FlowDbContext       _db;
        private readonly ICurrentUserService _currentUserService;

        public UpdateDealHandler(FlowDbContext db, ICurrentUserService currentUserService)
        {
            _db                 = db;
            _currentUserService = currentUserService;
        }

        public async Task HandleAsync(string tenantId, Guid dealId, UpdateDealDto dto)
        {
            var deal = await _db.Deals
                .FirstOrDefaultAsync(d => d.Id == dealId && d.TenantId.ToString() == tenantId && !d.IsDeleted);

            if (deal is null)
                throw new KeyNotFoundException($"Deal {dealId} not found");

            var currentUser = await _currentUserService.GetCurrentUserAsync();

            deal.Title                = dto.Title;
            deal.Description          = dto.Description;
            deal.ExpectedValue        = dto.ExpectedValue;
            deal.Currency             = dto.Currency;
            deal.ExpectedCloseDateUtc = dto.ExpectedCloseDateUtc;
            deal.Probability          = dto.Probability;
            deal.Tags                 = dto.Tags;

            // ✅ Vertical — update if provided
            if (dto.VerticalId.HasValue)
                deal.VerticalId = dto.VerticalId;

            // Source FK
            if (dto.SourceId.HasValue)
            {
                var sourceExists = await _db.LeadSources
                    .AnyAsync(s => s.Id == new Guid(dto.SourceId.Value.ToString()) && s.IsActive && !s.IsDeleted);
                if (sourceExists)
                    deal.SourceId = new Guid(dto.SourceId.Value.ToString());
            }

            // Stage change + history
            if (DealStages.IsValid(dto.Stage) && deal.Stage != dto.Stage)
            {
                var previousStage = deal.Stage;

                _db.DealStageHistory.Add(new DealStageHistory
                {
                    Id           = Guid.NewGuid(),
                    TenantId     = Guid.Parse(tenantId),
                    DealId       = dealId,
                    FromStage    = previousStage,
                    ToStage      = dto.Stage,
                    ChangedAtUtc = DateTime.UtcNow,
                    ChangedBy    = currentUser.FullName,
                    Note         = dto.Stage == DealStages.ClosedLost ? dto.LostReason : null
                });

                deal.Stage = dto.Stage;

                if (dto.Stage is DealStages.ClosedWon or DealStages.ClosedLost)
                {
                    deal.ActualCloseDateUtc ??= dto.ActualCloseDateUtc ?? DateTime.UtcNow;
                    deal.ActualValue        ??= dto.ActualValue        ?? deal.ExpectedValue;
                }

                if (dto.Stage == DealStages.ClosedLost && !string.IsNullOrEmpty(dto.LostReason))
                    deal.LostReason = dto.LostReason;
            }

            // Owner
            if (!string.IsNullOrEmpty(dto.OwnerUserId))
            {
                var userExists = await _db.Users
                    .AnyAsync(u => u.Id.ToString() == dto.OwnerUserId
                                && u.TenantId.ToString() == tenantId
                                && u.IsActive);
                if (userExists)
                    deal.OwnerUserId = dto.OwnerUserId;
            }

            deal.UpdatedAtUtc = DateTime.UtcNow;
            deal.UpdatedBy    = currentUser.FullName;

            await _db.SaveChangesAsync();
        }
    }

    // ==================== UPDATE DEAL STAGE ====================

    public class UpdateDealStageHandler : ICommandHandler
    {
        private readonly FlowDbContext       _db;
        private readonly ICurrentUserService _currentUserService;

        public UpdateDealStageHandler(FlowDbContext db, ICurrentUserService currentUserService)
        {
            _db                 = db;
            _currentUserService = currentUserService;
        }

        public async Task HandleAsync(string tenantId, Guid dealId, string newStage)
        {
            if (!DealStages.IsValid(newStage))
                throw new ArgumentException(
                    $"Invalid stage '{newStage}'. Valid: {string.Join(", ", DealStages.All)}");

            // ✅ Parse once — lets EF use the TenantId index properly
            if (!Guid.TryParse(tenantId, out var tenantGuid))
                throw new ArgumentException($"Invalid tenantId: {tenantId}");

            var deal = await _db.Deals
                   .FirstOrDefaultAsync(d => d.Id == dealId
                                          && d.TenantId == tenantGuid   // ← direct Guid compare
                                          && !d.IsDeleted);


            if (deal is null)
                throw new KeyNotFoundException($"Deal {dealId} not found");

            if (deal.Stage == newStage) return;

            var currentUser   = await _currentUserService.GetCurrentUserAsync();
            var previousStage = deal.Stage;

            _db.DealStageHistory.Add(new DealStageHistory
            {
                Id           = Guid.NewGuid(),
                TenantId     = Guid.Parse(tenantId),
                DealId       = dealId,
                FromStage    = previousStage,
                ToStage      = newStage,
                ChangedAtUtc = DateTime.UtcNow,
                ChangedBy    = currentUser.FullName
            });

            deal.Stage       = newStage;
            deal.Probability = DealStages.DefaultProbability(newStage);

            if (newStage is DealStages.ClosedWon or DealStages.ClosedLost)
            {
                deal.ActualCloseDateUtc ??= DateTime.UtcNow;
                deal.ActualValue        ??= deal.ExpectedValue;
            }

            deal.UpdatedAtUtc = DateTime.UtcNow;
            deal.UpdatedBy    = currentUser.FullName;

            await _db.SaveChangesAsync();
        }
    }

    // ==================== DELETE DEAL ====================

    public class DeleteDealHandler : ICommandHandler
    {
        private readonly FlowDbContext       _db;
        private readonly ICurrentUserService _currentUserService;

        public DeleteDealHandler(FlowDbContext db, ICurrentUserService currentUserService)
        {
            _db                 = db;
            _currentUserService = currentUserService;
        }

        public async Task HandleAsync(string tenantId, Guid dealId)
        {
            var deal = await _db.Deals
                .FirstOrDefaultAsync(d => d.Id == dealId && d.TenantId.ToString() == tenantId && !d.IsDeleted);

            if (deal is null)
                throw new KeyNotFoundException($"Deal {dealId} not found");

            var currentUser = await _currentUserService.GetCurrentUserAsync();

            deal.IsDeleted    = true;
            deal.DeletedAtUtc = DateTime.UtcNow;
            deal.DeletedBy    = currentUser.FullName;

            await _db.SaveChangesAsync();
        }
    }

    // ==================== NOTES ====================

    public class GetDealNotesHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        public GetDealNotesHandler(FlowDbContext db) => _db = db;

        public async Task<List<DealNoteDto>> HandleAsync(string tenantId, Guid dealId)
        {
            return await _db.DealNotes
                .Where(n => n.DealId == dealId && n.TenantId.ToString() == tenantId && !n.IsDeleted)
                .OrderByDescending(n => n.CreatedAtUtc)
                .Select(n => new DealNoteDto(n.Id, n.DealId, n.Note, n.CreatedAtUtc, n.CreatedBy))
                .ToListAsync();
        }
    }

    public class CreateDealNoteHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        public CreateDealNoteHandler(FlowDbContext db) => _db = db;

        public async Task HandleAsync(CreateDealNoteDto dto)
        {
            _db.DealNotes.Add(new DealNote
            {
                Id           = Guid.NewGuid(),
                TenantId     = dto.TenantId,
                DealId       = dto.DealId,
                Note         = dto.Note,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy    = dto.CreatedBy,
                IsDeleted    = false
            });
            await _db.SaveChangesAsync();
        }
    }

    // ==================== ACTIVITIES ====================

    public class GetDealActivitiesHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        public GetDealActivitiesHandler(FlowDbContext db) => _db = db;

        public async Task<List<DealActivityDto>> HandleAsync(string tenantId, Guid dealId)
        {
            return await _db.DealActivities
                .Where(a => a.DealId == dealId && a.TenantId.ToString() == tenantId && !a.IsDeleted)
                .OrderByDescending(a => a.ActivityDate)
                .Select(a => new DealActivityDto(
                    a.Id, a.DealId, a.ActivityType, a.Subject,
                    a.Description, a.Duration, a.ActivityDate,
                    a.CreatedAtUtc, a.CreatedBy))
                .ToListAsync();
        }
    }

    public class CreateDealActivityHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        public CreateDealActivityHandler(FlowDbContext db) => _db = db;

        public async Task HandleAsync(CreateDealActivityDto dto)
        {
            _db.DealActivities.Add(new DealActivity
            {
                Id           = Guid.NewGuid(),
                TenantId     = dto.TenantId,
                DealId       = dto.DealId,
                ActivityType = dto.ActivityType,
                Subject      = dto.Subject,
                Description  = dto.Description,
                Duration     = dto.Duration,
                ActivityDate = DateTime.UtcNow,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy    = dto.CreatedBy,
                IsDeleted    = false
            });
            await _db.SaveChangesAsync();
        }
    }

    // ==================== REMINDERS ====================

    public class GetDealRemindersHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        public GetDealRemindersHandler(FlowDbContext db) => _db = db;

        public async Task<List<DealReminderDto>> HandleAsync(string tenantId, Guid dealId)
        {
            return await _db.DealReminders
                .Where(r => r.DealId == dealId && r.TenantId.ToString() == tenantId && !r.IsDeleted)
                .OrderBy(r => r.ReminderDate)
                .Select(r => new DealReminderDto(
                    r.Id, r.DealId, r.Title, r.Description,
                    r.ReminderDate, r.IsCompleted, r.CompletedAtUtc,
                    r.CreatedAtUtc, r.CreatedBy))
                .ToListAsync();
        }
    }

    public class CreateDealReminderHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        public CreateDealReminderHandler(FlowDbContext db) => _db = db;

        public async Task HandleAsync(CreateDealReminderDto dto)
        {
            _db.DealReminders.Add(new DealReminder
            {
                Id           = Guid.NewGuid(),
                TenantId     = dto.TenantId,
                DealId       = dto.DealId,
                Title        = dto.Title,
                Description  = dto.Description,
                ReminderDate = dto.ReminderDate,
                IsCompleted  = false,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy    = dto.CreatedBy,
                IsDeleted    = false
            });
            await _db.SaveChangesAsync();
        }
    }

    public class CompleteDealReminderHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        public CompleteDealReminderHandler(FlowDbContext db) => _db = db;

        public async Task HandleAsync(string tenantId, Guid reminderId)
        {
            var reminder = await _db.DealReminders
                .FirstOrDefaultAsync(r => r.Id == reminderId && r.TenantId.ToString() == tenantId && !r.IsDeleted);

            if (reminder is null)
                throw new KeyNotFoundException($"Reminder {reminderId} not found");

            reminder.IsCompleted    = true;
            reminder.CompletedAtUtc = DateTime.UtcNow;

            await _db.SaveChangesAsync();
        }
    }

    // ==================== STAGE HISTORY ====================

    public class GetDealStageHistoryHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        public GetDealStageHistoryHandler(FlowDbContext db) => _db = db;

        public async Task<List<DealStageHistoryDto>> HandleAsync(GetDealStageHistoryRequest request)
        {
            return await _db.DealStageHistory
                .Where(h => h.TenantId.ToString() == request.TenantId && h.DealId == request.DealId)
                .OrderByDescending(h => h.ChangedAtUtc)
                .Select(h => new DealStageHistoryDto(
                    h.Id, h.DealId, h.FromStage ?? null, h.ToStage, h.ChangedAtUtc, h.ChangedBy))
                .ToListAsync();
        }
    }

    // ==================== SOURCES ====================

    public class GetDealSourcesHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        public GetDealSourcesHandler(FlowDbContext db) => _db = db;

        public async Task<List<DealSourceDto>> HandleAsync(GetDealSourcesRequest request)
        {
            var sources = await _db.LeadSources
                .Where(s => (s.TenantId.ToString() == request.TenantId || s.TenantId == null) && s.IsActive && !s.IsDeleted)
                .OrderBy(s => s.DisplayOrder).ThenBy(s => s.Name)
                .Select(s => new DealSourceDto(s.Id.ToString(), s.Name))
                .ToListAsync();

            if (!sources.Any())
            {
                sources = new List<DealSourceDto>
                {
                    new("Website",        "Website"),
                    new("Referral",       "Referral"),
                    new("Cold Call",      "Cold Call"),
                    new("Email Campaign", "Email Campaign"),
                    new("Social Media",   "Social Media"),
                    new("Event",          "Event / Trade Show"),
                    new("Partner",        "Partner"),
                    new("Inbound",        "Inbound"),
                    new("Other",          "Other"),
                };
            }

            return sources;
        }
    }
}
