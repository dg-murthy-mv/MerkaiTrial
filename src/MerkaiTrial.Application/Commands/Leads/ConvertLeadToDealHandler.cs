// =====================================================================
// ConvertLeadToDealHandler.cs
// Location: MerkaiTrial.Application/Commands/Leads/ConvertLeadToDealHandler.cs
//
// COMPLETE FILE — replaces the existing one.
//
// RECORD VISIBILITY (015): the lead is loaded through .VisibleTo(access).
// A rep who can't see a lead can't convert it either — a pasted id gets
// "not found", the same as another tenant's lead.
//
// 079 — CUSTOM FIELDS CARRY ACROSS
//
//   1. Mapped lead values are copied onto the contact and the new deal
//      (LeadFieldMapper — the mapping is set per Lead field in Settings →
//      Custom Fields → Leads). Staged before the final SaveChangesAsync,
//      inside the transaction, so they commit with the deal or not at all.
//      A REUSED contact (same email, or already linked) only has its EMPTY
//      fields filled; what is already on it is never overwritten.
//
//   2. ★ THE DEAL'S DESCRIPTION NO LONGER FALLS BACK TO
//      lead.CustomFieldsJson. When nobody typed a description, the old
//      line `Description = dto.Description ?? lead.CustomFieldsJson`
//      dropped the lead's raw JSON blob into the deal — text a customer
//      could see on anything printed from the deal. Custom fields travel
//      through the mapping now; an empty description stays empty.
//
//   3. The LeadConverted audit row records how many values were copied,
//      and a contact CREATED here gets its own ContactCreated row (078b),
//      as the deal already got DealCreated.
//
// 088b — THE DEAL NOW CARRIES ITS COMPANY
//   The company was found or created and linked to the CONTACT, but the
//   deal itself was saved with CompanyId = null. Every deal query joins
//   Companies on Deal.CompanyId, so a converted deal showed no company in
//   its header, its overview, the deals list, or a search by company
//   name — found by the E2E test "Converting a lead creates its contact
//   and company once". The deal now gets the lead's company, or, when
//   the lead named none, the company its contact already belongs to.
//   Sql/088b_BackfillConvertedDealCompanies.sql repairs deals converted
//   before this fix.
//
// 100 — ONE CONVERSION, EVEN WHEN TWO ARRIVE TOGETHER
//   Two requests converting the same lead at the same moment both read
//   "not converted yet" before either saved, and each made its own deal
//   (and its own contact and company when those were new). The lead's
//   row is now locked first (RowLock), inside the existing transaction:
//   the second request waits, then reads IsConverted = true and is told
//   "Lead has already been converted". Other leads are not held up.
//
// 100 (second part) — TWO LEADS, ONE CUSTOMER
//   Two different leads with the same company name or the same email,
//   converted at the same moment, each checked "is there a company /
//   contact already?" before either saved, and each created one. The
//   conversion now also holds a named lock on the company name and on the
//   email (RowLock.HoldNameAsync) before looking, so the second waits and
//   then reuses what the first created.
// =====================================================================

using MerkaiTrial.Application.Commands.CustomFields;     // 079
using MerkaiTrial.Application.Common;                    // 100 — RowLock
using MerkaiTrial.Application.Commands.LeadStatuses;
using MerkaiTrial.Application.Commands.Automation;     // 098
using MerkaiTrial.Application.Commands.PipelineStages;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Security;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Domain.Enums;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Commands.Leads
{
    public class ConvertLeadToDealHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly ICurrentUserService _currentUserService;
        private readonly ICurrentTenantService _tenantService;
        private readonly ILogger<ConvertLeadToDealHandler> _logger;
        private readonly IAuditService _audit;
        private readonly IStageResolver _stages;
        private readonly ILeadStatusResolver _statuses;
        private readonly IRecordScopeService _scope;
        private readonly AutomationEngine _automation;               // 098
        public ConvertLeadToDealHandler(
            FlowDbContext db,
            ICurrentUserService currentUserService,
            ICurrentTenantService tenantService,
            ILogger<ConvertLeadToDealHandler> logger,
            IAuditService audit,
            IStageResolver stages, ILeadStatusResolver statuses,
            IRecordScopeService scope,
            AutomationEngine automation)                             // 098
        {
            _automation = automation;
            _scope = scope;
            _db = db;
            _currentUserService = currentUserService;
            _tenantService = tenantService;
            _logger = logger;
            _statuses = statuses;
            _stages = stages;
            _audit = audit;
        }

        public async Task<ConvertLeadToDealResultDto> Handle(ConvertLeadToDealDto dto)
        {
            using var transaction = await _db.Database.BeginTransactionAsync();
            try
            {
                _logger.LogInformation("Starting lead conversion: LeadId={LeadId}", dto.LeadId);

                // 100 — before the lead is read. Joins the transaction
                // above; it commits (or rolls back) with it.
                await using var held = await RowLock.HoldAsync(_db, RowLock.Leads, dto.LeadId);

                // ── STEP 1: VALIDATE LEAD ─────────────────────────────────────

                // Record visibility: outside your scope = not found.
                var access = await _scope.GetAsync(RecordModules.Leads);

                var lead = await _db.Leads
                    .Include(l => l.Contact)
                    .Where(l =>
                        l.Id == dto.LeadId &&
                        l.TenantId == dto.TenantId &&
                        !l.IsDeleted)
                    .VisibleTo(access)
                    .FirstOrDefaultAsync();
                var statuses = await _statuses.GetAsync(dto.TenantId);

                if (lead == null)
                    throw new KeyNotFoundException($"Lead {dto.LeadId} not found");

                // 100 — "already converted" is checked FIRST. A converted
                // lead's status is Converted, which is not a Qualified one,
                // so the second of two simultaneous clicks used to be told
                // "Lead must be qualified to convert. Current status:
                // Converted" — true, but not the reason.
                if (lead.IsConverted)
                    throw new InvalidOperationException(
                        $"Lead has already been converted to deal {lead.DealId}");

                if (!statuses.IsQualified(lead.Status))
                    throw new InvalidOperationException(
                        $"Lead must be qualified to convert. Current status: {statuses.NameOf(lead.Status)}");

                var currentUser = await _currentUserService.GetCurrentUserAsync();
                var now = DateTime.UtcNow;

                // 100 — the company and contact this conversion may CREATE,
                // held by name until it commits. Two DIFFERENT leads for the
                // same customer, converted together, used to both find "no
                // such company / contact yet" and both create one. Sorted,
                // so two conversions always take them in the same order.
                var names = new List<string>();
                if (!string.IsNullOrWhiteSpace(lead.CompanyName))
                    names.Add(RowLock.NameKey("company", lead.TenantId, lead.CompanyName));
                if (!lead.ContactId.HasValue && !string.IsNullOrWhiteSpace(lead.Email))
                    names.Add(RowLock.NameKey("contact", lead.TenantId, lead.Email));
                foreach (var name in names.OrderBy(n => n, StringComparer.Ordinal))
                    await RowLock.HoldNameAsync(_db, name);

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

                // 088b — the deal's own company. The lead's, when it named
                // one (the lead is what this deal is about); otherwise
                // whatever company the reused contact is already in.
                var dealCompanyId = companyId;
                if (!dealCompanyId.HasValue)
                {
                    dealCompanyId = await _db.Contacts
                        .Where(c => c.Id == contactId && !c.IsDeleted)
                        .Select(c => c.CompanyId)
                        .FirstOrDefaultAsync();
                }

                var stages = await _stages.GetAsync(dto.TenantId);
                var chosen = stages.ResolveOrDefault(dto.Stage)
                    ?? throw new InvalidOperationException(
                        "This workspace has no pipeline stages set up.");

                var dealStage = chosen.Key;
                int probability = chosen.Probability;


                var currency = !string.IsNullOrEmpty(dto.Currency)
                    ? dto.Currency
                    : lead.Currency ?? string.Empty;

                var deal = new Deal
                {
                    Id = Guid.NewGuid(),
                    TenantId = dto.TenantId,
                    ContactId = contactId,
                    CompanyId = dealCompanyId,                       // 088b
                    LeadId = dto.LeadId,
                    Title = dto.DealTitle,
                    Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim(),   // 079 — note 2
                    Stage = dealStage,
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
                var oldStatus = lead.Status;
                lead.Status = statuses.ConvertedStatus?.Key
                ?? throw new InvalidOperationException(
                    "No converted status is configured for this workspace.");
                lead.IsConverted = true;
                lead.DealId = deal.Id;
                lead.ConvertedAtUtc = now;
                lead.ConvertedBy = dto.ConvertedBy ?? currentUser.FullName;
                lead.UpdatedAtUtc = now;
                lead.UpdatedBy = dto.ConvertedBy ?? currentUser.FullName;

                if (companyId.HasValue)
                    lead.ConvertedToCompanyId = companyId;

                // ── STEP 5b (079): CARRY MAPPED CUSTOM FIELDS ACROSS ──────
                // Onto the contact (filling blanks only if it already
                // existed) and onto the new deal. Staged; saved below.
                var copied = await LeadFieldMapper.CopyAsync(
                    _db, dto.TenantId, lead.Id, contactId, contactWasCreated, deal.Id,
                    dto.ConvertedBy ?? currentUser.FullName);

                // ── STEP 6: SAVE ──────────────────────────────────────────────

                await _db.SaveChangesAsync();
                await transaction.CommitAsync();

                // ✅ AUDIT — two events, because two things happened.
                // The deal is a new record in its own right; without this,
                // deals born from conversion would have no creation entry
                // while manually created ones do.
                await _audit.WriteAsync(
                    AuditAction.LeadConverted, AuditEntityType.Lead, lead.Id, dto.TenantId,
                    new
                    {
                        name = lead.FullName,
                        fromStatus = oldStatus.ToString(),
                        dealId = deal.Id,
                        contactId,
                        companyId,
                        contactWasCreated,
                        companyWasCreated,
                        customFieldsToContact = copied.ToContact,     // 079
                        customFieldsToDeal    = copied.ToDeal
                    });

                await _audit.WriteAsync(
                    AuditAction.DealCreated, AuditEntityType.Deal, deal.Id, dto.TenantId,
                    new
                    {
                        title = deal.Title,
                        stage = deal.Stage,
                        value = deal.ExpectedValue,
                        currency = deal.Currency,
                        companyId = deal.CompanyId,                  // 088b
                        fromLeadId = lead.Id
                    });

                // 079 — a contact created here is a new record too (078b).
                if (contactWasCreated)
                {
                    try
                    {
                        await _audit.WriteAsync(
                            AuditAction.ContactCreated, AuditEntityType.Contact, contactId, dto.TenantId,
                            new { name = lead.FullName, companyId, fromLeadId = lead.Id });
                    }
                    catch (Exception auditEx)
                    {
                        _logger.LogError(auditEx, "Contact {Id} was created by conversion but its audit row could not be written", contactId);
                    }
                }

                // 098 — two things happened, so two kinds of rule may
                // fire: the lead's status became Converted, and a deal was
                // added. The engine never throws, and the transaction is
                // already committed.
                await _automation.FieldsChangedAsync(
                    dto.TenantId, AutomationEntity.Lead, lead.Id,
                    new[] { new AutomationFieldChange(AutomationFields.Status, oldStatus.ToString(), lead.Status) },
                    currentUser.UserId.ToString());
                await _automation.RecordCreatedAsync(
                    dto.TenantId, AutomationEntity.Deal, deal.Id, currentUser.UserId.ToString());

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

                // 079 — say so when details came across; it is the first
                // thing a person checks on the new deal.
                if (copied.Total > 0)
                    message += $" {copied.Total} additional detail{(copied.Total == 1 ? "" : "s")} carried over.";

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
                await transaction.RollbackAsync();
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
