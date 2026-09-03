// =====================================================================
// FILE: MerkaiTrial.Application/Commands/Companies/CompanyCommandHandlers.cs
//
// CHANGES:
//   CreateCompanyHandler — added MaxCompanies quota check
//   Source: Plans table via Tenants.Plan (string key)
//   Company.TenantId = Guid → direct comparison, no .ToString() needed
// =====================================================================

using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Exceptions;          // ✅ ADDED
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Commands.Companies
{
    // ==================== CREATE COMPANY ====================

    public class CreateCompanyCommand : ICommandHandler
    {
        public Guid TenantId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Country { get; set; } = "TH";
        public string? TaxId { get; set; }
        public string Vertical { get; set; } = "Generic";
        public string CreatedBy { get; set; } = string.Empty;
    }

    public class CreateCompanyHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<CreateCompanyHandler> _logger;

        public CreateCompanyHandler(FlowDbContext context, ILogger<CreateCompanyHandler> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<CompanyDto> Handle(
            CreateCompanyCommand request,
            CancellationToken cancellationToken)
        {
            try
            {
                // ── ✅ Plan quota check — MaxCompanies from Plans table ───────
                // Company.TenantId = Guid, request.TenantId = Guid → direct comparison
                // MaxCompanies is NOT in TenantSettings — must read from Plans via Tenants.Plan
                var tenantPlan = await _context.Tenants
                    .AsNoTracking()
                    .Where(t => t.Id == request.TenantId && !t.IsDeleted)
                    .Select(t => t.Plan)
                    .FirstOrDefaultAsync(cancellationToken);

                if (!string.IsNullOrEmpty(tenantPlan))
                {
                    var plan = await _context.Plans
                        .AsNoTracking()
                        .FirstOrDefaultAsync(
                            p => p.Name == tenantPlan && p.IsActive,
                            cancellationToken);

                    if (plan != null)
                    {
                        var currentCount = await _context.Companies
                            .CountAsync(
                                c => c.TenantId == request.TenantId && !c.IsDeleted,
                                cancellationToken);

                        if (currentCount >= plan.MaxCompanies)
                            throw new PlanLimitExceededException(
                                "companies", currentCount, plan.MaxCompanies);
                    }
                }
                // ── end quota check ────────────────────────────────────────────

                var company = new Company
                {
                    Id = Guid.NewGuid(),
                    TenantId = request.TenantId,
                    Name = request.Name,
                    Country = request.Country,
                    TaxId = request.TaxId,
                    Vertical = request.Vertical,
                    CreatedAtUtc = DateTime.UtcNow,
                    CreatedBy = request.CreatedBy,
                    IsDeleted = false
                };

                _context.Companies.Add(company);
                await _context.SaveChangesAsync(cancellationToken);

                _logger.LogInformation("Created company {Name} for tenant {TenantId}",
                    company.Name, request.TenantId);

                return new CompanyDto
                {
                    Id = company.Id,
                    TenantId = company.TenantId,
                    Name = company.Name,
                    Country = company.Country,
                    TaxId = company.TaxId,
                    Vertical = company.Vertical,
                    ContactCount = 0,
                    DealCount = 0,
                    PrimaryContactCount = 0,
                    CreatedAtUtc = company.CreatedAtUtc,
                    CreatedBy = company.CreatedBy
                };
            }
            catch (PlanLimitExceededException)
            {
                throw; // ✅ Let controller catch it and return 422 — don't log as error
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating company");
                throw;
            }
        }
    }

    // ==================== UPDATE COMPANY ====================

    public class UpdateCompanyCommand : ICommandHandler
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Country { get; set; } = "TH";
        public string? TaxId { get; set; }
        public string Vertical { get; set; } = "Generic";
        public string UpdatedBy { get; set; } = string.Empty;
    }

    public class UpdateCompanyHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<UpdateCompanyHandler> _logger;

        public UpdateCompanyHandler(FlowDbContext context, ILogger<UpdateCompanyHandler> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task Handle(UpdateCompanyCommand request, CancellationToken cancellationToken)
        {
            try
            {
                var company = await _context.Companies
                    .FirstOrDefaultAsync(
                        c => c.Id == request.Id && c.TenantId == request.TenantId && !c.IsDeleted,
                        cancellationToken);

                if (company == null)
                    throw new KeyNotFoundException($"Company {request.Id} not found");

                company.Name = request.Name;
                company.Country = request.Country;
                company.TaxId = request.TaxId;
                company.Vertical = request.Vertical;
                company.UpdatedAtUtc = DateTime.UtcNow;
                company.UpdatedBy = request.UpdatedBy;

                await _context.SaveChangesAsync(cancellationToken);

                _logger.LogInformation("Updated company {Name}", company.Name);
            }
            catch (KeyNotFoundException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating company {Id}", request.Id);
                throw;
            }
        }
    }

    // ==================== DELETE COMPANY ====================

    public class DeleteCompanyCommand : ICommandHandler
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public string DeletedBy { get; set; } = string.Empty;
    }

    public class DeleteCompanyHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<DeleteCompanyHandler> _logger;

        public DeleteCompanyHandler(FlowDbContext context, ILogger<DeleteCompanyHandler> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task Handle(DeleteCompanyCommand request, CancellationToken cancellationToken)
        {
            try
            {
                var company = await _context.Companies
                    .FirstOrDefaultAsync(
                        c => c.Id == request.Id && c.TenantId == request.TenantId && !c.IsDeleted,
                        cancellationToken);

                if (company == null)
                    throw new KeyNotFoundException($"Company {request.Id} not found");

                company.IsDeleted = true;
                company.UpdatedAtUtc = DateTime.UtcNow;
                company.UpdatedBy = request.DeletedBy;

                await _context.SaveChangesAsync(cancellationToken);

                _logger.LogInformation("Deleted company {Name}", company.Name);
            }
            catch (KeyNotFoundException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting company {Id}", request.Id);
                throw;
            }
        }
    }
}
