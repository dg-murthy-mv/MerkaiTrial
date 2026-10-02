// =====================================================================
// CompanyProfileHandlers.cs
// Location: MerkaiTrial.Application/Commands/Tenants/CompanyProfileHandlers.cs
//
// NEW FILE (064). The workspace's own company details — who the quote is
// FROM.
//
// THE TENANT ID IS NEVER A PARAMETER FROM THE CLIENT. Both handlers take
// it, but the controller passes the value off the caller's token and the
// route carries no id at all. There is no shape of request that lets one
// workspace read or write another's profile, which is the whole reason
// these are separate from GetTenantDetailHandler / UpdateTenantHandler —
// those are the super admin's view of any workspace and are guarded as
// such.
//
// WHAT IS DELIBERATELY NOT EDITABLE HERE
//   Name, Plan, IsActive, CountryId, DefaultCurrency, Timezone.
//
//   Not an oversight. Renaming a workspace, changing its plan or moving
//   it to another country are platform operations with consequences
//   elsewhere — the country decides tax rates, currency and date format,
//   and GetDefaultTaxRateAsync only ever looks for rates matching it.
//   A tenant admin editing their letterhead must not be able to change
//   what their quotes are priced in by accident.
//
// Both implement ICommandHandler, so the Scrutor scan in
// AddApiHandlers() registers them. Nothing to add by hand.
// =====================================================================

using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Security;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Domain.Enums;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Commands.Tenants
{
    // ─────────────────────────────────────────────────────────────────
    // READ
    // ─────────────────────────────────────────────────────────────────
    public class GetCompanyProfileHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;

        public GetCompanyProfileHandler(FlowDbContext db) => _db = db;

        public async Task<CompanyProfileDto> HandleAsync(Guid tenantId, CancellationToken ct = default)
        {
            // Tenants carries no TenantId column — it IS the tenant — so
            // there is no query filter on it and this is an ordinary
            // lookup by id. The id came from the token; see the header.
            var row = await _db.Tenants.AsNoTracking()
                .Where(t => t.Id == tenantId && !t.IsDeleted)
                .Select(t => new
                {
                    t.Id,
                    t.Name,
                    t.LegalName,
                    t.AddressLine1,
                    t.AddressLine2,
                    t.City,
                    t.State,
                    t.PostalCode,
                    t.Phone,
                    t.Website,
                    t.TaxNumber,
                    t.TaxNumberLabel,
                    t.ReplyToEmail,
                    CountryCode     = t.Country != null ? t.Country.Code     : null,
                    CountryName     = t.Country != null ? t.Country.Name     : null,
                    CountryTaxLabel = t.Country != null ? t.Country.TaxLabel : null
                })
                .FirstOrDefaultAsync(ct);

            if (row is null)
                throw new KeyNotFoundException($"Workspace {tenantId} not found");

            return new CompanyProfileDto(
                TenantId:        row.Id,
                Name:            row.Name,
                LegalName:       row.LegalName,
                AddressLine1:    row.AddressLine1,
                AddressLine2:    row.AddressLine2,
                City:            row.City,
                State:           row.State,
                PostalCode:      row.PostalCode,
                Phone:           row.Phone,
                Website:         row.Website,
                TaxNumber:       row.TaxNumber,
                TaxNumberLabel:  row.TaxNumberLabel,
                ReplyToEmail:    row.ReplyToEmail,
                CountryCode:     row.CountryCode,
                CountryName:     row.CountryName,
                CountryTaxLabel: row.CountryTaxLabel);
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // WRITE
    // ─────────────────────────────────────────────────────────────────
    public class UpdateCompanyProfileHandler : ICommandHandler
    {
        private readonly FlowDbContext _db;
        private readonly IAuditService _audit;
        private readonly ILogger<UpdateCompanyProfileHandler> _logger;

        public UpdateCompanyProfileHandler(
            FlowDbContext db,
            IAuditService audit,
            ILogger<UpdateCompanyProfileHandler> logger)
        {
            _db = db;
            _audit = audit;
            _logger = logger;
        }

        public async Task HandleAsync(
            Guid tenantId,
            UpdateCompanyProfileCommand cmd,
            CancellationToken ct = default)
        {
            var tenant = await _db.Tenants
                .FirstOrDefaultAsync(t => t.Id == tenantId && !t.IsDeleted, ct);

            if (tenant is null)
                throw new KeyNotFoundException($"Workspace {tenantId} not found");

            // Trimmed to null rather than stored as "" — an empty string
            // and "not filled in" must not be two different states, or
            // every reader needs to test for both and one of them will
            // forget. The quote page tests IsNullOrWhiteSpace; this is
            // the other half of that bargain.
            tenant.LegalName      = Clean(cmd.LegalName,      200);
            tenant.AddressLine1   = Clean(cmd.AddressLine1,   200);
            tenant.AddressLine2   = Clean(cmd.AddressLine2,   200);
            tenant.City           = Clean(cmd.City,           100);
            tenant.State          = Clean(cmd.State,          100);
            tenant.PostalCode     = Clean(cmd.PostalCode,      20);
            tenant.Phone          = Clean(cmd.Phone,           50);
            tenant.Website        = Clean(cmd.Website,        255);
            tenant.TaxNumber      = Clean(cmd.TaxNumber,       50);
            tenant.TaxNumberLabel = Clean(cmd.TaxNumberLabel,  50);
            tenant.ReplyToEmail   = Clean(cmd.ReplyToEmail,   320);

            // A tax number with no label prints as a bare string on a tax
            // document. Fall back to the country's word for it rather
            // than leaving the reader to guess.
            if (!string.IsNullOrWhiteSpace(tenant.TaxNumber)
                && string.IsNullOrWhiteSpace(tenant.TaxNumberLabel))
            {
                var countryLabel = await _db.Countries.AsNoTracking()
                    .Where(c => c.Id == tenant.CountryId)
                    .Select(c => c.TaxLabel)
                    .FirstOrDefaultAsync(ct);

                tenant.TaxNumberLabel = Clean(countryLabel, 50) ?? "Tax No.";
            }

            tenant.UpdatedAtUtc = DateTime.UtcNow;
            tenant.UpdatedBy    = Clean(cmd.UpdatedBy, 255);

            await _db.SaveChangesAsync(ct);

            // ── 066: RESTORED, with the real constants ────────────────
            //
            // 064 shipped this with AuditAction.Updated and failed to
            // compile. AuditAction is a static class of STRING CONSTANTS,
            // not an enum, and it has no generic Updated — its values are
            // <Entity><Verb>: DealUpdated, LeadUpdated, InvoiceUpdated.
            // AuditEntityType.Tenant was real; the action was not.
            // 066 adds TenantProfileUpdated beside the other tenant
            // lifecycle actions.
            //
            // THE DATA SAYS WHETHER, NOT WHAT. hasTaxNumber rather than
            // the number. AuditLog.Data's own comment says never to put
            // raw personal or identifying data there, because an audit log
            // is widely readable by design — and "the tax number changed
            // on 2 October, by Somchai" is the whole question anyone will
            // ever ask of this row.
            await _audit.WriteAsync(
                AuditAction.TenantProfileUpdated, AuditEntityType.Tenant,
                tenant.Id, tenantId,
                new
                {
                    hasAddress   = !string.IsNullOrWhiteSpace(tenant.AddressLine1),
                    taxLabel     = tenant.TaxNumberLabel,
                    hasTaxNumber = !string.IsNullOrWhiteSpace(tenant.TaxNumber)
                },
                CancellationToken.None);

            _logger.LogInformation(
                "Company profile updated for workspace {TenantId} by {User}",
                tenantId, cmd.UpdatedBy ?? "unknown");
        }

        /// <summary>
        /// Trim, collapse empty to NULL, and cut to the column width
        /// rather than letting SaveChanges throw. A letterhead is never
        /// important enough to fail on a string one character too long,
        /// and the form validates the same lengths first — this is the
        /// backstop for anything that did not come through the form.
        /// </summary>
        private static string? Clean(string? value, int max)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var trimmed = value.Trim();
            return trimmed.Length <= max ? trimmed : trimmed[..max];
        }
    }
}
