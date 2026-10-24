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
// 100 — THE COMPANY MUST BE THIS WORKSPACE'S
//   Create and Update stored whatever CompanyId they were given. A contact
//   could be saved pointing at another workspace's company (nothing of
//   theirs was shown — every read is filtered by workspace — but the link
//   crossed the boundary). ContactInput.CheckCompanyAsync refuses it with
//   a 400 and a sentence, before anything is saved.
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
//
// 095 — WHAT IS TYPED IS CHECKED HERE TOO
//   Create and Update check the contact's own fields with the shared
//   InputRules before anything is staged: a first name, the lengths of
//   the columns, an email that looks like one, and the phone and mobile
//   numbers — against the contact's own country when they have no +. The
//   Admin.Web form checks the same rules first, so a person sees these
//   sentences under each box; this is the guard for anything that does
//   not come through that form.
//
//   Every problem is listed at once, as an InputValidationException (a
//   CustomFieldValidationException, so the existing catch below re-throws
//   it without an error log and the controller answers 400 with the
//   sentences).
// =====================================================================

using MerkaiTrial.Application.Commands.CustomFields;
using MerkaiTrial.Application.Common;                // 102 — DeleteRules
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Exceptions;          // ✅ ADDED
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MerkaiTrial.Application.Security;
using MerkaiTrial.Application.Validation;            // 095


namespace MerkaiTrial.Application.Commands.Contacts
{
    // ==================== 095: THE CHECKS ====================

    internal static class ContactInput
    {
        /// <summary>Every problem with a contact's own fields, at once. Throws InputValidationException.</summary>
        public static void Check(string? firstName, string? lastName, string? jobTitle, string? email,
            string? phone, string? mobile, string? address, string? city, string? country, string? postalCode)
        {
            var errors = new List<string>();
            errors.AddIf(InputRules.Text(firstName, "a first name", 100, required: true));
            errors.AddIf(InputRules.Text(lastName, "the last name", 100));
            errors.AddIf(InputRules.Text(jobTitle, "the job title", 100));
            errors.AddIf(InputRules.For("Email", InputRules.Email(email)));
            errors.AddIf(InputRules.For("Phone", InputRules.Phone(phone, country)));
            errors.AddIf(InputRules.For("Mobile", InputRules.Phone(mobile, country)));
            errors.AddIf(InputRules.Text(address, "the address", 500));
            errors.AddIf(InputRules.Text(city, "the city", 100));
            errors.AddIf(InputRules.Text(country, "the country", 100));
            errors.AddIf(InputRules.Text(postalCode, "the postal code", 20));
            InputRules.ThrowIfAny(errors);
        }

        /// <summary>
        /// 100. A company named on a contact must be one of this
        /// workspace's own. Before, any company id was stored as given, so
        /// a contact could point into another workspace's companies.
        /// Throws InputValidationException — a 400 with the sentence, on
        /// the path every contact controller action already has.
        /// </summary>
        public static async Task CheckCompanyAsync(FlowDbContext db, Guid tenantId, Guid? companyId, CancellationToken ct)
        {
            if (companyId is not Guid id || id == Guid.Empty) return;
            var ours = await db.Companies.AsNoTracking()
                .AnyAsync(c => c.Id == id && c.TenantId == tenantId && !c.IsDeleted, ct);
            if (!ours)
                throw new InputValidationException(new[] { "The company chosen isn't in this workspace. Pick one from the list." });
        }
    }

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
                // 095 — before the quota, so a mistyped form is told about
                // the mistake rather than counted against the plan.
                ContactInput.Check(request.FirstName, request.LastName, request.JobTitle, request.Email,
                    request.Phone, request.Mobile, request.Address, request.City, request.Country, request.PostalCode);
                await ContactInput.CheckCompanyAsync(_context, request.TenantId, request.CompanyId, cancellationToken);   // 100

                // ── Plan quota (103: PlanQuota) ───────────────────────────────
                // MaxContacts from the workspace's plan, read live, held until
                // the contact is saved so two saves can't take one place.
                await using var quota = await PlanQuota.HoldAsync(
                    _context, request.TenantId, PlanQuota.Contacts, ct: cancellationToken);

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
                await quota.CommitAsync(cancellationToken);   // 103

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

        /// <summary>102. The version the edit was made from; stale → RecordChangedException.</summary>
        public string? Version { get; set; }
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

                // 102 — made from the version on screen? Refused before
                // anything changes if someone else saved since.
                RecordVersion.Expect(_context, contact, versioned => versioned.RowVersion, request.Version, "contact");

                // 095 — before anything is assigned.
                ContactInput.Check(request.FirstName, request.LastName, request.JobTitle, request.Email,
                    request.Phone, request.Mobile, request.Address, request.City, request.Country, request.PostalCode);
                await ContactInput.CheckCompanyAsync(_context, request.TenantId, request.CompanyId, cancellationToken);   // 100

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
            catch (InvalidOperationException) { throw; }       // 102 — RecordChangedException and other refusals
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

                // 102 — not while deals point at it.
                var why = await DeleteRules.WhyContactCannotBeDeletedAsync(
                    _context, request.TenantId, contact.Id, cancellationToken);
                if (why is not null) throw new InvalidOperationException(why);

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
            catch (InvalidOperationException) { throw; }   // 102 — a refusal, not a failure
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting contact {Id}", request.Id);
                throw;
            }
        }
    }
}
