// =====================================================================
// FILE: MerkaiTrial.Application/Commands/Contacts/ContactCommandHandlers.cs
//
// CHANGES:
//   CreateContactHandler — added MaxContacts quota check
//   Source: Plans table via Tenants.Plan (string key)
//   Contact.TenantId = Guid → direct comparison, no .ToString() needed
//   catch (PlanLimitExceededException) re-throws without logging as error
//   catch (KeyNotFoundException) re-throws in Update/Delete — same pattern
//
// 078b — AUDIT ON CREATE AND UPDATE
//
//   Only delete was audited (ContactDeleted). Create now writes
//   ContactCreated { name, companyId } and Update writes
//   ContactUpdated { name, changed: [...] } — the NAMES of the fields that
//   changed (AuditChanges.Track), plus "customFields" if any custom value
//   changed. Never the values: an email, a phone number or an address is
//   personal data, and an audit log is widely readable (AuditLog.Data).
//
//   The audit row is written AFTER the save succeeds. If writing it fails,
//   that is logged as an error but the request still succeeds: the
//   contact IS saved, and answering "failed" would make the person try
//   again and create a duplicate.
//
// 075 — CUSTOM FIELDS
//   Create and Update carry an optional CustomFields map and hand it to
//   CustomFieldValueWriter BEFORE SaveChangesAsync, so the contact and
//   its field values commit together or not at all. A null map (every
//   caller written before 075) skips the writer completely.
//
//   CustomFieldValidationException is an InvalidOperationException with
//   a sentence for the person in it. Both handlers re-throw it without
//   logging an error — a required field left empty is not a fault.
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

        /// <summary>075. Null = say nothing about custom fields.</summary>
        public Dictionary<Guid, string?>? CustomFields { get; set; }
    }

    public class CreateContactHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<CreateContactHandler> _logger;
        private readonly IAuditService _audit;   // 078b

        public CreateContactHandler(
            FlowDbContext context, ILogger<CreateContactHandler> logger, IAuditService audit)
        {
            _context = context;
            _logger = logger;
            _audit = audit;
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

                // 075 — staged in the same unit of work as the contact. A
                // validation failure throws here, before anything is saved.
                await CustomFieldValueWriter.ApplyAsync(
                    _context, request.TenantId, CustomFieldEntityTypes.Contact, contact.Id,
                    request.CustomFields, request.CreatedBy, isNew: true, cancellationToken);

                await _context.SaveChangesAsync(cancellationToken);

                // 078b — after the save; a failure here must not undo it.
                try
                {
                    await _audit.WriteAsync(
                        AuditAction.ContactCreated, AuditEntityType.Contact, contact.Id, request.TenantId,
                        new { name = contact.DisplayName, companyId = contact.CompanyId },
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Contact {Id} was created but its audit row could not be written", contact.Id);
                }

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
            catch (CustomFieldValidationException)
            {
                throw; // 075 — a sentence for the person, not a system error
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

        /// <summary>075. Null = say nothing about custom fields.</summary>
        public Dictionary<Guid, string?>? CustomFields { get; set; }
    }

    public class UpdateContactHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<UpdateContactHandler> _logger;
        private readonly IAuditService _audit;   // 078b

        public UpdateContactHandler(
            FlowDbContext context, ILogger<UpdateContactHandler> logger, IAuditService audit)
        {
            _context = context;
            _logger = logger;
            _audit = audit;
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

                // 078b — what changed, by NAME, for the audit row.
                var changed = new List<string>();
                AuditChanges.Track(changed, "companyId",  contact.CompanyId,  request.CompanyId);
                AuditChanges.Track(changed, "firstName",  contact.FirstName,  request.FirstName);
                AuditChanges.Track(changed, "lastName",   contact.LastName,   request.LastName);
                AuditChanges.Track(changed, "jobTitle",   contact.JobTitle,   request.JobTitle);
                AuditChanges.Track(changed, "email",      contact.Email,      request.Email);
                AuditChanges.Track(changed, "phone",      contact.Phone,      request.Phone);
                AuditChanges.Track(changed, "mobile",     contact.Mobile,     request.Mobile);
                AuditChanges.Track(changed, "address",    contact.Address,    request.Address);
                AuditChanges.Track(changed, "city",       contact.City,       request.City);
                AuditChanges.Track(changed, "country",    contact.Country,    request.Country);
                AuditChanges.Track(changed, "postalCode", contact.PostalCode, request.PostalCode);
                AuditChanges.Track(changed, "notes",      contact.Notes,      request.Notes);
                AuditChanges.Track(changed, "isPrimary",  contact.IsPrimary,  request.IsPrimary);

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

                // 075 — same unit of work as the contact's own fields.
                await CustomFieldValueWriter.ApplyAsync(
                    _context, request.TenantId, CustomFieldEntityTypes.Contact, contact.Id,
                    request.CustomFields, request.UpdatedBy, isNew: false, cancellationToken);

                if (AuditChanges.CustomFieldValuesPending(_context))
                    changed.Add(AuditChanges.CustomFields);

                await _context.SaveChangesAsync(cancellationToken);

                // 078b — after the save; a failure here must not undo it.
                try
                {
                    await _audit.WriteAsync(
                        AuditAction.ContactUpdated, AuditEntityType.Contact, contact.Id, request.TenantId,
                        new { name = contact.DisplayName, changed },
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Contact {Id} was updated but its audit row could not be written", contact.Id);
                }

                _logger.LogInformation("Updated contact {Name}", contact.DisplayName);
            }
            catch (KeyNotFoundException) { throw; } // ✅ Expected — not a system error
            catch (CustomFieldValidationException) { throw; } // 075 — expected, not a system error
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
        private readonly IAuditService _audit;

        public DeleteContactHandler(FlowDbContext context, ILogger<DeleteContactHandler> logger, IAuditService audit)
        {
            _context = context;
            _logger = logger;
            _audit = audit;
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
                await _audit.WriteAsync(
                    AuditAction.ContactDeleted, AuditEntityType.Contact, contact.Id, request.TenantId,
                    new { name = contact.DisplayName, email = contact.Email, companyId = contact.CompanyId },
                    cancellationToken);
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
