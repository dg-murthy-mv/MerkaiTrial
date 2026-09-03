// =====================================================================
// FILE: MerkaiTrial.Application/Commands/Contacts/ContactCommandHandlers.cs
//
// CHANGES:
//   CreateContactHandler — added MaxContacts quota check
//   Source: Plans table via Tenants.Plan (string key)
//   Contact.TenantId = Guid → direct comparison, no .ToString() needed
//   catch (PlanLimitExceededException) re-throws without logging as error
//   catch (KeyNotFoundException) re-throws in Update/Delete — same pattern
// =====================================================================

using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Exceptions;          // ✅ ADDED
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Commands.Contacts
{
    // ==================== CREATE CONTACT ====================

    public class CreateContactCommand : ICommandHandler
    {
        public Guid TenantId { get; set; }
        public Guid? CompanyId { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string? LastName { get; set; }
        public string? JobTitle { get; set; }
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public string? Mobile { get; set; }
        public string? Address { get; set; }
        public string? City { get; set; }
        public string? Country { get; set; }
        public string? PostalCode { get; set; }
        public string? Notes { get; set; }
        public bool IsPrimary { get; set; }
        public string CreatedBy { get; set; } = string.Empty;
    }

    public class CreateContactHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<CreateContactHandler> _logger;

        public CreateContactHandler(FlowDbContext context, ILogger<CreateContactHandler> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<ContactDto> Handle(
            CreateContactCommand request,
            CancellationToken cancellationToken)
        {
            try
            {
                // ── ✅ Plan quota check — MaxContacts from Plans table ────────
                // Contact.TenantId = Guid, request.TenantId = Guid → direct comparison
                // MaxContacts is NOT in TenantSettings — must read from Plans via Tenants.Plan
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
                        var currentCount = await _context.Contacts
                            .CountAsync(
                                c => c.TenantId == request.TenantId && !c.IsDeleted,
                                cancellationToken);

                        if (currentCount >= plan.MaxContacts)
                            throw new PlanLimitExceededException(
                                "contacts", currentCount, plan.MaxContacts);
                    }
                }
                // ── end quota check ───────────────────────────────────────────

                var contact = new Contact
                {
                    Id = Guid.NewGuid(),
                    TenantId = request.TenantId,
                    CompanyId = request.CompanyId,
                    FirstName = request.FirstName,
                    LastName = request.LastName,
                    JobTitle = request.JobTitle,
                    Email = request.Email,
                    Phone = request.Phone,
                    Mobile = request.Mobile,
                    Address = request.Address,
                    City = request.City,
                    Country = request.Country,
                    PostalCode = request.PostalCode,
                    Notes = request.Notes,
                    IsPrimary = request.IsPrimary,
                    CreatedAtUtc = DateTime.UtcNow,
                    CreatedBy = request.CreatedBy,
                    IsDeleted = false
                };

                _context.Contacts.Add(contact);
                await _context.SaveChangesAsync(cancellationToken);

                _logger.LogInformation("Created contact {Name} for tenant {TenantId}",
                    contact.DisplayName, request.TenantId);

                // Load company name for DTO
                string? companyName = null;
                if (contact.CompanyId.HasValue)
                {
                    companyName = await _context.Companies
                        .Where(c => c.Id == contact.CompanyId.Value)
                        .Select(c => c.Name)
                        .FirstOrDefaultAsync(cancellationToken);
                }

                return new ContactDto
                {
                    Id = contact.Id,
                    TenantId = contact.TenantId,
                    CompanyId = contact.CompanyId,
                    FirstName = contact.FirstName,
                    LastName = contact.LastName,
                    JobTitle = contact.JobTitle,
                    Email = contact.Email,
                    Phone = contact.Phone,
                    Mobile = contact.Mobile,
                    Address = contact.Address,
                    City = contact.City,
                    Country = contact.Country,
                    PostalCode = contact.PostalCode,
                    Notes = contact.Notes,
                    IsPrimary = contact.IsPrimary,
                    CompanyName = companyName,
                    DealCount = 0,
                    CreatedAtUtc = contact.CreatedAtUtc,
                    CreatedBy = contact.CreatedBy
                };
            }
            catch (PlanLimitExceededException)
            {
                throw; // ✅ Re-throw without logging as error — expected business logic
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating contact");
                throw;
            }
        }
    }

    // ==================== UPDATE CONTACT ====================

    public class UpdateContactCommand : ICommandHandler
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public Guid? CompanyId { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string? LastName { get; set; }
        public string? JobTitle { get; set; }
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public string? Mobile { get; set; }
        public string? Address { get; set; }
        public string? City { get; set; }
        public string? Country { get; set; }
        public string? PostalCode { get; set; }
        public string? Notes { get; set; }
        public bool IsPrimary { get; set; }
        public string UpdatedBy { get; set; } = string.Empty;
    }

    public class UpdateContactHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<UpdateContactHandler> _logger;

        public UpdateContactHandler(FlowDbContext context, ILogger<UpdateContactHandler> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task Handle(UpdateContactCommand request, CancellationToken cancellationToken)
        {
            try
            {
                var contact = await _context.Contacts
                    .FirstOrDefaultAsync(
                        c => c.Id == request.Id && c.TenantId == request.TenantId && !c.IsDeleted,
                        cancellationToken);

                if (contact == null)
                    throw new KeyNotFoundException($"Contact {request.Id} not found");

                contact.CompanyId = request.CompanyId;
                contact.FirstName = request.FirstName;
                contact.LastName = request.LastName;
                contact.JobTitle = request.JobTitle;
                contact.Email = request.Email;
                contact.Phone = request.Phone;
                contact.Mobile = request.Mobile;
                contact.Address = request.Address;
                contact.City = request.City;
                contact.Country = request.Country;
                contact.PostalCode = request.PostalCode;
                contact.Notes = request.Notes;
                contact.IsPrimary = request.IsPrimary;
                contact.UpdatedAtUtc = DateTime.UtcNow;
                contact.UpdatedBy = request.UpdatedBy;

                await _context.SaveChangesAsync(cancellationToken);

                _logger.LogInformation("Updated contact {Name}", contact.DisplayName);
            }
            catch (KeyNotFoundException) { throw; } // ✅ Expected — not a system error
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating contact {Id}", request.Id);
                throw;
            }
        }
    }

    // ==================== DELETE CONTACT ====================

    public class DeleteContactCommand : ICommandHandler
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public string DeletedBy { get; set; } = string.Empty;
    }

    public class DeleteContactHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<DeleteContactHandler> _logger;

        public DeleteContactHandler(FlowDbContext context, ILogger<DeleteContactHandler> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task Handle(DeleteContactCommand request, CancellationToken cancellationToken)
        {
            try
            {
                var contact = await _context.Contacts
                    .FirstOrDefaultAsync(
                        c => c.Id == request.Id && c.TenantId == request.TenantId && !c.IsDeleted,
                        cancellationToken);

                if (contact == null)
                    throw new KeyNotFoundException($"Contact {request.Id} not found");

                contact.IsDeleted = true;
                contact.UpdatedAtUtc = DateTime.UtcNow;
                contact.UpdatedBy = request.DeletedBy;

                await _context.SaveChangesAsync(cancellationToken);

                _logger.LogInformation("Deleted contact {Name}", contact.DisplayName);
            }
            catch (KeyNotFoundException) { throw; } // ✅ Expected — not a system error
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting contact {Id}", request.Id);
                throw;
            }
        }
    }
}
