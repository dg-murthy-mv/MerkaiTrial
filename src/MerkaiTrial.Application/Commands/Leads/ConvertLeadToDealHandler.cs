using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Domain.Enums;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Application.Commands.Leads
{
    public class ConvertLeadToDealHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUserService;
        private readonly ICurrentTenantService _tenantService;
        private readonly ILogger<ConvertLeadToDealHandler> _logger;

        public ConvertLeadToDealHandler(
            FlowDbContext db,
            ICurrentUserService currentUserService,
            ICurrentTenantService tenantService,
            ILogger<ConvertLeadToDealHandler> logger)
        {
            _db = db;
            _currentUserService = currentUserService;
            _tenantService = tenantService;
            _logger = logger;
        }

        public async Task<ConvertLeadToDealResultDto> Handle(ConvertLeadToDealDto dto)
        {
            try
            {
                _logger.LogInformation("Starting lead conversion: LeadId={LeadId}", dto.LeadId);

                // ── STEP 1: VALIDATE LEAD ─────────────────────────────────────

                var lead = await _db.Leads
                    .Include(l => l.Contact)
                    .FirstOrDefaultAsync(l =>
                        l.Id == dto.LeadId &&
                        l.TenantId == dto.TenantId &&
                        !l.IsDeleted);

                if (lead == null)
                    throw new KeyNotFoundException($"Lead {dto.LeadId} not found");

                if (lead.Status != LeadStatus.Qualified)
                    throw new InvalidOperationException(
                        $"Lead must be in 'Qualified' status to convert. Current status: {lead.Status}");

                if (lead.IsConverted)
                    throw new InvalidOperationException(
                        $"Lead has already been converted to deal {lead.DealId}");

                var currentUser = await _currentUserService.GetCurrentUserAsync();
                var now = DateTime.UtcNow;

                // ── STEP 2: RESOLVE OR CREATE COMPANY ─────────────────────────

                Guid? companyId = null;
                bool companyWasCreated = false;

                if (!string.IsNullOrWhiteSpace(lead.CompanyName))
                {
                    companyId = await _db.Companies
                        .Where(c => c.TenantId == lead.TenantId &&
                                    c.Name == lead.CompanyName &&
                                    !c.IsDeleted)
                        .Select(c => (Guid?)c.Id)
                        .FirstOrDefaultAsync();

                    if (!companyId.HasValue)
                    {
                        // ✅ FIX: resolve vertical name from lead.VerticalId
                        // Previously hardcoded "Generic" — now looks up the actual vertical name.
                        // Falls back to "Generic" only if lead has no vertical assigned.
                        string verticalName = "Generic";
                        if (lead.VerticalId.HasValue)
                        {
                            verticalName = await _db.CompanyVerticals
                                .Where(v => v.Id == lead.VerticalId.Value && !v.IsDeleted)
                                .Select(v => v.Name)
                                .FirstOrDefaultAsync() ?? "Generic";
                        }

                        var company = new Company
                        {
                            Id = Guid.NewGuid(),
                            TenantId = lead.TenantId,
                            Name = lead.CompanyName,
                            Vertical = verticalName,
                            Country = _tenantService.GetCountryCode(),
                            CreatedAtUtc = now,
                            CreatedBy = currentUser.FullName,
                            IsDeleted = false
                        };
                        _db.Companies.Add(company);
                        companyId = company.Id;
                        companyWasCreated = true;

                        _logger.LogInformation(
                            "Created company '{Name}' ({CompanyId}) from lead {LeadId} with vertical '{Vertical}'",
                            lead.CompanyName, company.Id, lead.Id, verticalName);
                    }
                }

                // ── STEP 3: ENSURE CONTACT EXISTS (with deduplication) ────────
                // ✅ BUG 1 FIX: before creating a new contact, check if one with
                //    the same email already exists for this tenant. If it does,
                //    reuse it — stops Priya Sharma appearing 3× after 3 conversions.

                Guid contactId;
                bool contactWasCreated = false;

                if (lead.ContactId.HasValue)
                {
                    // Lead already linked to a contact — use it
                    contactId = lead.ContactId.Value;

                    // Patch company onto existing contact if missing
                    if (companyId.HasValue)
                    {
                        var existingContact = await _db.Contacts
                            .FirstOrDefaultAsync(c => c.Id == contactId && !c.IsDeleted);

                        if (existingContact != null && !existingContact.CompanyId.HasValue)
                        {
                            existingContact.CompanyId = companyId;
                            existingContact.UpdatedAtUtc = now;
                            existingContact.UpdatedBy = currentUser.FullName;
                        }
                    }

                    _logger.LogInformation("Using existing linked contact: {ContactId}", contactId);
                }
                else if (!string.IsNullOrWhiteSpace(lead.Email))
                {
                    // ✅ DEDUPLICATION: look for existing contact with same email in this tenant
                    var deduped = await _db.Contacts
                        .FirstOrDefaultAsync(c =>
                            c.TenantId == lead.TenantId &&
                            c.Email == lead.Email &&
                            !c.IsDeleted);

                    if (deduped != null)
                    {
                        // Reuse existing — patch company if needed
                        contactId = deduped.Id;

                        if (companyId.HasValue && !deduped.CompanyId.HasValue)
                        {
                            deduped.CompanyId = companyId;
                            deduped.UpdatedAtUtc = now;
                            deduped.UpdatedBy = currentUser.FullName;
                        }

                        lead.ContactId = contactId;
                        lead.ConvertedToContactId = contactId;

                        _logger.LogInformation(
                            "Deduplication: reusing existing contact {ContactId} for email {Email}",
                            contactId, lead.Email);
                    }
                    else
                    {
                        // No duplicate found — create new contact
                        var newContact = await CreateContactFromLead(lead, companyId, currentUser.FullName, now);
                        _db.Contacts.Add(newContact);
                        await _db.SaveChangesAsync();

                        contactId = newContact.Id;
                        contactWasCreated = true;
                        lead.ContactId = contactId;
                        lead.ConvertedToContactId = contactId;

                        _logger.LogInformation("Created new contact: {ContactId}", contactId);
                    }
                }
                else
                {
                    // No email and no existing contact — create (no dedup possible)
                    var newContact = await CreateContactFromLead(lead, companyId, currentUser.FullName, now);
                    _db.Contacts.Add(newContact);
                    await _db.SaveChangesAsync();

                    contactId = newContact.Id;
                    contactWasCreated = true;
                    lead.ContactId = contactId;
                    lead.ConvertedToContactId = contactId;

                    _logger.LogInformation("Created new contact (no email, no dedup): {ContactId}", contactId);
                }

                // ── STEP 4: CREATE DEAL ───────────────────────────────────────

                if (!Enum.TryParse<DealStage>(dto.Stage, out var dealStage))
                    dealStage = DealStage.Qualification;

                int probability = dealStage switch
                {
                    DealStage.Discovery => 20,
                    DealStage.Qualification => 50,
                    DealStage.Proposal => 40,
                    DealStage.Negotiation => 60,
                    DealStage.ClosedWon => 100,
                    DealStage.ClosedLost => 0,
                    _ => 50
                };

                var currency = !string.IsNullOrEmpty(dto.Currency)
                    ? dto.Currency
                    : lead.Currency ?? string.Empty;

                var deal = new Deal
                {
                    Id = Guid.NewGuid(),
                    TenantId = dto.TenantId,
                    ContactId = contactId,
                    LeadId = dto.LeadId,
                    Title = dto.DealTitle,
                    Description = dto.Description ?? lead.CustomFieldsJson,
                    Stage = dealStage.ToString(),
                    ExpectedValue = dto.ExpectedValue,
                    Currency = currency,
                    VerticalId = lead.VerticalId,
                    ExpectedCloseDateUtc = dto.ExpectedCloseDateUtc,
                    OwnerUserId = dto.OwnerUserId ?? lead.OwnerUserId ?? currentUser.UserId.ToString(),
                    Probability = probability,
                    CreatedAtUtc = now,
                    CreatedBy = dto.ConvertedBy ?? currentUser.FullName,
                    UpdatedAtUtc = now,
                    UpdatedBy = dto.ConvertedBy ?? currentUser.FullName,
                    IsDeleted = false
                };

                _db.Deals.Add(deal);

                // ── STEP 5: UPDATE LEAD ───────────────────────────────────────

                lead.Status = LeadStatus.Converted;
                lead.IsConverted = true;
                lead.DealId = deal.Id;
                lead.ConvertedAtUtc = now;
                lead.ConvertedBy = dto.ConvertedBy ?? currentUser.FullName;
                lead.UpdatedAtUtc = now;
                lead.UpdatedBy = dto.ConvertedBy ?? currentUser.FullName;

                if (companyId.HasValue)
                    lead.ConvertedToCompanyId = companyId;

                // ── STEP 6: SAVE ──────────────────────────────────────────────

                await _db.SaveChangesAsync();

                _logger.LogInformation(
                    "Lead conversion complete: LeadId={LeadId} → DealId={DealId} ContactId={ContactId} CompanyId={CompanyId}",
                    lead.Id, deal.Id, contactId, companyId);

                // ── STEP 7: RETURN ────────────────────────────────────────────

                var message = (contactWasCreated, companyWasCreated) switch
                {
                    (true, true) => "Lead converted. New contact and company created.",
                    (true, false) => "Lead converted. New contact created.",
                    (false, true) => "Lead converted. New company created and linked to contact.",
                    _ => "Lead converted successfully."
                };

                return new ConvertLeadToDealResultDto
                {
                    DealId = deal.Id,
                    LeadId = lead.Id,
                    ContactId = contactId,
                    ContactWasCreated = contactWasCreated,
                    CompanyWasCreated = companyWasCreated,
                    DealTitle = deal.Title,
                    Stage = deal.Stage,
                    ConvertedAtUtc = now,
                    Message = message
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to convert lead {LeadId} to deal", dto.LeadId);
                throw;
            }
        }

        private async Task<Contact> CreateContactFromLead(
            Lead lead,
            Guid? companyId,
            string createdBy,
            DateTime createdAt)
        {
            var nameParts = (lead.FullName ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var firstName = nameParts.Length > 0 ? nameParts[0] : "Unknown";
            var lastName = nameParts.Length > 1 ? string.Join(" ", nameParts.Skip(1)) : "";

            string? countryCode = null;
            if (lead.CountryId.HasValue)
            {
                countryCode = await _db.Countries
                    .Where(c => c.Id == lead.CountryId.Value)
                    .Select(c => c.Code)
                    .FirstOrDefaultAsync();
            }

            return new Contact
            {
                Id = Guid.NewGuid(),
                TenantId = lead.TenantId,
                CompanyId = companyId,
                FirstName = firstName,
                LastName = lastName,
                Email = lead.Email,
                Phone = lead.Phone,
                Address = lead.Address,
                Country = countryCode,
                IsPrimary = true,
                CreatedAtUtc = createdAt,
                CreatedBy = createdBy,
                UpdatedAtUtc = createdAt,
                UpdatedBy = createdBy,
                IsDeleted = false
            };
        }
    }
}
