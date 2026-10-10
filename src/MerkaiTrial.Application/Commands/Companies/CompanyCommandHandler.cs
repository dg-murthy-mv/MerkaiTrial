// =====================================================================
// FILE: MerkaiTrial.Application/Commands/Companies/CompanyCommandHandlers.cs
//
// CHANGES:
//   CreateCompanyHandler — added MaxCompanies quota check
//   Source: Plans table via Tenants.Plan (string key)
//   Company.TenantId = Guid → direct comparison, no .ToString() needed
//
// 078b — AUDIT ON CREATE AND UPDATE
//
//   Only delete was audited (CompanyDeleted). Create now writes
//   CompanyCreated { name, vertical, country } and Update writes
//   CompanyUpdated { name, changed: [...] } — the NAMES of the fields that
//   changed (AuditChanges.Track), "customFields" if any custom value
//   changed, and the old/new name when the name itself changed. Never the
//   tax ID's value: an audit log is widely readable (see AuditLog.Data).
//
//   The audit row is written AFTER the save succeeds. If writing it fails,
//   that is logged as an error but the request still succeeds: the
//   company IS saved, and answering "failed" would make the person try
//   again and create a duplicate.
//
// 078 — CUSTOM FIELDS ON COMPANIES
//
//   Create and Update carry an optional CustomFields map and hand it to
//   CustomFieldValueWriter.ApplyAsync BEFORE SaveChangesAsync, so the
//   company and its field values commit together or not at all. A null
//   map (every caller written before 078) skips the writer completely.
//
//   CustomFieldValidationException is an InvalidOperationException with
//   a sentence for the person in it ("Renewal date must be a valid
//   date."). Both handlers re-throw it without logging an error — a
//   required field left empty is not a system fault.
//
//   Create returns the stored values in CompanyDto.CustomFieldValues, so
//   a caller that shows the new company does not need a second read.
//
//   Removed: `using DocumentFormat.OpenXml.Presentation;` — an IDE
//   auto-import nothing used. It dragged an Open XML type namespace into
//   scope; one of its names colliding with ours would have been a
//   baffling compile error.
// =====================================================================

using MerkaiTrial.Application.Commands.CustomFields;
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Exceptions;          // ✅ ADDED
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MerkaiTrial.Application.Security;

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

        /// <summary>078. Null = say nothing about custom fields.</summary>
        public Dictionary<Guid, string?>? CustomFields { get; set; }
    }

    public class CreateCompanyHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<CreateCompanyHandler> _logger;
        private readonly IAuditService _audit;   // 078b

        public CreateCompanyHandler(
            FlowDbContext context, ILogger<CreateCompanyHandler> logger, IAuditService audit)
        {
            _context = context;
            _logger = logger;
            _audit = audit;
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

                // 078 — staged in the same unit of work as the company. A
                // validation failure throws here, before anything is saved.
                await CustomFieldValueWriter.ApplyAsync(
                    _context, request.TenantId, CustomFieldEntityTypes.Company, company.Id,
                    request.CustomFields, request.CreatedBy, isNew: true, cancellationToken);

                await _context.SaveChangesAsync(cancellationToken);

                // 078b — after the save; a failure here must not undo it.
                try
                {
                    await _audit.WriteAsync(
                        AuditAction.CompanyCreated, AuditEntityType.Company, company.Id, request.TenantId,
                        new { name = company.Name, vertical = company.Vertical, country = company.Country },
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Company {Id} was created but its audit row could not be written", company.Id);
                }

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
                    CreatedBy = company.CreatedBy,
                    CustomFieldValues = await CustomFieldValueWriter.ReadAsync(
                        _context, request.TenantId, CustomFieldEntityTypes.Company, company.Id, cancellationToken)
                };
            }
            catch (PlanLimitExceededException)
            {
                throw; // ✅ Let controller catch it and return 422 — don't log as error
            }
            catch (CustomFieldValidationException)
            {
                throw; // 078 — a sentence for the person, not a system error
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

        /// <summary>078. Null = say nothing about custom fields.</summary>
        public Dictionary<Guid, string?>? CustomFields { get; set; }
    }

    public class UpdateCompanyHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<UpdateCompanyHandler> _logger;
        private readonly IAuditService _audit;   // 078b

        public UpdateCompanyHandler(
            FlowDbContext context, ILogger<UpdateCompanyHandler> logger, IAuditService audit)
        {
            _context = context;
            _logger = logger;
            _audit = audit;
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

                // 078b — what changed, by NAME, for the audit row.
                var changed = new List<string>();
                var previousName = company.Name;
                AuditChanges.Track(changed, "name",     company.Name,     request.Name);
                AuditChanges.Track(changed, "country",  company.Country,  request.Country);
                AuditChanges.Track(changed, "taxId",    company.TaxId,    request.TaxId);
                AuditChanges.Track(changed, "vertical", company.Vertical, request.Vertical);

                company.Name = request.Name;
                company.Country = request.Country;
                company.TaxId = request.TaxId;
                company.Vertical = request.Vertical;
                company.UpdatedAtUtc = DateTime.UtcNow;
                company.UpdatedBy = request.UpdatedBy;

                // 078 — same unit of work as the company's own fields.
                await CustomFieldValueWriter.ApplyAsync(
                    _context, request.TenantId, CustomFieldEntityTypes.Company, company.Id,
                    request.CustomFields, request.UpdatedBy, isNew: false, cancellationToken);

                if (AuditChanges.CustomFieldValuesPending(_context))
                    changed.Add(AuditChanges.CustomFields);

                await _context.SaveChangesAsync(cancellationToken);

                // 078b — after the save; a failure here must not undo it.
                try
                {
                    await _audit.WriteAsync(
                        AuditAction.CompanyUpdated, AuditEntityType.Company, company.Id, request.TenantId,
                        changed.Contains("name")
                            ? new { name = company.Name, changed, previousName }
                            : (object)new { name = company.Name, changed },
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Company {Id} was updated but its audit row could not be written", company.Id);
                }

                _logger.LogInformation("Updated company {Name}", company.Name);
            }
            catch (KeyNotFoundException) { throw; }
            catch (CustomFieldValidationException) { throw; } // 078 — expected, not a system error
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
        private readonly IAuditService _audit;
        public DeleteCompanyHandler(FlowDbContext context, ILogger<DeleteCompanyHandler> logger, IAuditService audit)
        {
            _context = context;
            _logger = logger;
            _audit = audit;
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
                await _audit.WriteAsync(
                   AuditAction.CompanyDeleted, AuditEntityType.Company, company.Id, request.TenantId,
                   new { name = company.Name, country = company.Country, taxId = company.TaxId },
                   cancellationToken);
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
