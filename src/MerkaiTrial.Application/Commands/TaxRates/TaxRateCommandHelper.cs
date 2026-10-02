using MerkaiTrial.Application.Common;      // 057: TaxRateStatus
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

// =====================================================================
// TaxRateCommandHelper.cs
// COMPLETE FILE — 056.
//
// WHO MAY CHANGE WHAT. None of these three commands asked.
//
//   * Update found the row by Id alone. With the query filter now
//     admitting system rates, that would let any tenant admin edit the
//     standard GST every other tenant in India is priced from.
//   * Delete did the same, and its own comment said "you might want to
//     check ... For now, we'll allow deletion".
//   * Create took TenantId straight from the request body, so any
//     authenticated caller could create a rate for any workspace.
//
// Every command now carries ActingTenantId (NULL for a super admin) and
// IsSuperAdmin, both resolved from the TOKEN by the controller and never
// from the request. The rule is one sentence: a system rate belongs to
// super admins, a tenant rate belongs to its own tenant.
//
// Also added: a duplicate guard on create, and a delete that refuses to
// remove the last default for a country.
// =====================================================================

namespace MerkaiTrial.Application.Commands.TaxRates
{
    /// <summary>
    /// 056. Who is asking, from the token. Shared by all three commands so
    /// the rule lives in one place rather than three.
    /// </summary>
    public static class TaxRateOwnership
    {
        /// <summary>
        /// Throws unless the caller may write this row.
        /// <paramref name="rowTenantId"/> NULL = a system rate.
        /// </summary>
        public static void AssertCanWrite(Guid? rowTenantId, Guid? actingTenantId, bool isSuperAdmin)
        {
            if (rowTenantId is null)
            {
                // A system rate. Super admins only — it is shared by every
                // tenant in that country, so one workspace editing it changes
                // what every other workspace charges.
                if (!isSuperAdmin)
                    throw new UnauthorizedAccessException(
                        "This is a system tax rate, shared by every workspace in that country. " +
                        "Only a platform administrator can change it. Create your own rate instead " +
                        "and it will be used in place of this one.");
                return;
            }

            // A tenant rate: its own tenant, or a super admin.
            if (isSuperAdmin) return;

            if (actingTenantId is null || rowTenantId != actingTenantId)
                throw new KeyNotFoundException("Tax rate not found");
            //        ^ NOT UnauthorizedAccess. Telling a caller that a row
            //          exists but belongs to somebody else is itself a leak.
            //          "Not found" is what they would see if it did not
            //          exist, which is the honest answer to give a stranger.
        }
    }
    // =====================================================================
    // CREATE TAX RATE COMMAND
    // =====================================================================
    public class CreateTaxRateCommand : ICommandHandler
    {
        /// <summary>NULL = a SYSTEM rate. Set by the controller from the
        /// token, never from the request body.</summary>
        public Guid? TenantId { get; set; }

        /// <summary>056: from the token. NULL for a super admin.</summary>
        public Guid? ActingTenantId { get; set; }

        /// <summary>056: from the token.</summary>
        public bool IsSuperAdmin { get; set; }
        public string CountryCode { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string TaxType { get; set; } = string.Empty;
        public decimal Rate { get; set; }
        public bool IsDefault { get; set; }

        /// <summary>057. NULL = always in force / still in force.</summary>
        public DateTime? EffectiveFrom { get; set; }
        public DateTime? EffectiveTo { get; set; }

        public string CreatedBy { get; set; } = string.Empty;
    }

    public class CreateTaxRateHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;

        public CreateTaxRateHandler(FlowDbContext context)
        {
            _context = context;
        }

        public async Task<TaxRateDto> Handle(CreateTaxRateCommand command, CancellationToken cancellationToken = default)
        {
            // 056. May this caller create THIS kind of rate at all?
            TaxRateOwnership.AssertCanWrite(command.TenantId, command.ActingTenantId, command.IsSuperAdmin);

            // Verify country exists
            var countryExists = await _context.Countries
                .AnyAsync(c => c.Code == command.CountryCode, cancellationToken);

            if (!countryExists)
                throw new InvalidOperationException($"Country '{command.CountryCode}' does not exist");

            // 056. Two rates with the same name for the same country, in the
            // same scope, are indistinguishable on every screen that lists
            // them — and which one you opened to edit is a coin flip.
            var name = (command.Name ?? string.Empty).Trim();

            var duplicate = await _context.TaxRates
                .AnyAsync(t => t.TenantId == command.TenantId
                            && t.CountryCode == command.CountryCode
                            && t.Name == name
                            && !t.IsDeleted, cancellationToken);

            if (duplicate)
                throw new InvalidOperationException(
                    $"A tax rate called '{name}' already exists for {command.CountryCode}.");

            // 057. One validator, shared with the pages and the API, so all
            // three reject an inverted window with the same sentence.
            var windowProblem = TaxRateStatus.ValidateWindow(command.EffectiveFrom, command.EffectiveTo);
            if (windowProblem is not null)
                throw new InvalidOperationException(windowProblem);

            // If setting as default, unset other defaults for this country.
            //
            // 056: scoped to the SAME OWNER, which is what TenantId ==
            // command.TenantId now genuinely means. A tenant marking their own
            // rate as default must not clear the system default that every
            // other tenant in that country resolves against.
            if (command.IsDefault)
            {
                var existingDefaults = await _context.TaxRates
                    .Where(t => t.TenantId == command.TenantId
                             && t.CountryCode == command.CountryCode
                             && t.IsDefault
                             && !t.IsDeleted)
                    .ToListAsync(cancellationToken);

                foreach (var existing in existingDefaults)
                {
                    existing.IsDefault = false;
                    existing.UpdatedAtUtc = DateTime.UtcNow;
                }
            }

            var taxRate = new TaxRate
            {
                Id = Guid.NewGuid(),
                TenantId = command.TenantId,
                CountryCode = command.CountryCode.ToUpper(),
                Name = name,
                TaxType = command.TaxType,
                Rate = command.Rate,
                IsDefault = command.IsDefault,
                EffectiveFrom = command.EffectiveFrom,   // 057
                EffectiveTo = command.EffectiveTo,       // 057
                // A new rate is active; IsActive is the manual RETIRE switch
                // and belongs on Edit, not here. A rate created switched off
                // would be a rate created for no reason.
                IsActive = true,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = command.CreatedBy,
                IsDeleted = false
            };

            _context.TaxRates.Add(taxRate);
            await _context.SaveChangesAsync(cancellationToken);

            // Get country name for DTO
            var country = await _context.Countries
                .FirstOrDefaultAsync(c => c.Code == command.CountryCode, cancellationToken);

            return new TaxRateDto
            {
                Id = taxRate.Id,
                TenantId = taxRate.TenantId,
                CountryCode = taxRate.CountryCode,
                CountryName = country?.Name ?? taxRate.CountryCode,
                Name = taxRate.Name,
                TaxType = taxRate.TaxType,
                Rate = taxRate.Rate,
                IsDefault = taxRate.IsDefault,
                EffectiveFrom = taxRate.EffectiveFrom,   // 057
                EffectiveTo = taxRate.EffectiveTo,       // 057
                IsActive = taxRate.IsActive
            };
        }
    }

    // =====================================================================
    // UPDATE TAX RATE COMMAND
    // =====================================================================
    public class UpdateTaxRateCommand : ICommandHandler
    {
        public Guid Id { get; set; }

        /// <summary>056: from the token. NULL for a super admin.</summary>
        public Guid? ActingTenantId { get; set; }

        /// <summary>056: from the token.</summary>
        public bool IsSuperAdmin { get; set; }
        public string Name { get; set; } = string.Empty;
        public decimal Rate { get; set; }
        public bool IsDefault { get; set; }

        /// <summary>057.</summary>
        public DateTime? EffectiveFrom { get; set; }
        public DateTime? EffectiveTo { get; set; }

        /// <summary>057: the manual retire switch.</summary>
        public bool IsActive { get; set; } = true;

        public string UpdatedBy { get; set; } = string.Empty;
    }

    public class UpdateTaxRateHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;

        public UpdateTaxRateHandler(FlowDbContext context)
        {
            _context = context;
        }

        public async Task Handle(UpdateTaxRateCommand command, CancellationToken cancellationToken = default)
        {
            var taxRate = await _context.TaxRates
                .FirstOrDefaultAsync(t => t.Id == command.Id && !t.IsDeleted, cancellationToken);

            if (taxRate == null)
                throw new KeyNotFoundException($"Tax rate with ID '{command.Id}' not found");

            // 056. THE CHECK THAT WAS NOT HERE. Without it, and with the query
            // filter now admitting system rows, any tenant admin could edit
            // the standard rate every other tenant in that country is priced
            // from.
            TaxRateOwnership.AssertCanWrite(taxRate.TenantId, command.ActingTenantId, command.IsSuperAdmin);

            // If setting as default, unset other defaults for this country
            if (command.IsDefault && !taxRate.IsDefault)
            {
                var existingDefaults = await _context.TaxRates
                    .Where(t => t.TenantId == taxRate.TenantId
                             && t.CountryCode == taxRate.CountryCode
                             && t.IsDefault
                             && t.Id != command.Id
                             && !t.IsDeleted)
                    .ToListAsync(cancellationToken);

                foreach (var existing in existingDefaults)
                {
                    existing.IsDefault = false;
                    existing.UpdatedAtUtc = DateTime.UtcNow;
                }
            }

            // 057. Same validator as create.
            var windowProblem = TaxRateStatus.ValidateWindow(command.EffectiveFrom, command.EffectiveTo);
            if (windowProblem is not null)
                throw new InvalidOperationException(windowProblem);

            // 057. Switching the DEFAULT rate off would leave the country
            // with a default that is not in force — the resolver skips it and
            // falls through to Country.DefaultTaxRate with nothing on any
            // screen to say why. Same shape as the delete guard below it:
            // hand the job to another rate first.
            if (!command.IsActive && taxRate.IsActive && taxRate.IsDefault)
            {
                var anotherDefaultExists = await _context.TaxRates
                    .AnyAsync(t => t.TenantId == taxRate.TenantId
                                && t.CountryCode == taxRate.CountryCode
                                && t.IsDefault
                                && t.IsActive
                                && t.Id != taxRate.Id
                                && !t.IsDeleted, cancellationToken);

                if (!anotherDefaultExists)
                    throw new InvalidOperationException(
                        $"'{taxRate.Name}' is the active default for {taxRate.CountryCode}. " +
                        "Make another rate the default first — switching this one off would leave " +
                        "new quotes there with no rate to use.");
            }

            taxRate.Name = command.Name;
            taxRate.Rate = command.Rate;
            taxRate.IsDefault = command.IsDefault;
            taxRate.EffectiveFrom = command.EffectiveFrom;   // 057
            taxRate.EffectiveTo = command.EffectiveTo;       // 057
            taxRate.IsActive = command.IsActive;             // 057
            taxRate.UpdatedAtUtc = DateTime.UtcNow;
            taxRate.UpdatedBy = command.UpdatedBy;

            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    // =====================================================================
    // DELETE TAX RATE COMMAND
    // =====================================================================
    public class DeleteTaxRateCommand
    {
        public Guid Id { get; set; }

        /// <summary>056: from the token. NULL for a super admin.</summary>
        public Guid? ActingTenantId { get; set; }

        /// <summary>056: from the token.</summary>
        public bool IsSuperAdmin { get; set; }
    }

    public class DeleteTaxRateHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;

        public DeleteTaxRateHandler(FlowDbContext context)
        {
            _context = context;
        }

        public async Task Handle(DeleteTaxRateCommand command, CancellationToken cancellationToken = default)
        {
            var taxRate = await _context.TaxRates
                .FirstOrDefaultAsync(t => t.Id == command.Id && !t.IsDeleted, cancellationToken);

            if (taxRate == null)
                throw new KeyNotFoundException($"Tax rate with ID '{command.Id}' not found");

            // 056. Same rule as update.
            TaxRateOwnership.AssertCanWrite(taxRate.TenantId, command.ActingTenantId, command.IsSuperAdmin);

            // 056. Was: "Check if tax rate is in use ... For now, we'll allow
            // deletion but you can add validation here."
            //
            // Nothing stores a TaxRateId — a quote line snapshots the RATE
            // itself, not a reference to this row — so deleting one cannot
            // orphan anything already written. What it CAN do is remove the
            // row that every NEW quote in that country resolves against,
            // leaving the resolver to fall through to Country.DefaultTaxRate
            // silently.
            //
            // So the guard is about the DEFAULT, not about usage: refuse to
            // remove the last default for a country in this scope. Make
            // another rate the default first and this is allowed.
            if (taxRate.IsDefault)
            {
                var anotherDefaultExists = await _context.TaxRates
                    .AnyAsync(t => t.TenantId == taxRate.TenantId
                                && t.CountryCode == taxRate.CountryCode
                                && t.IsDefault
                                && t.Id != taxRate.Id
                                && !t.IsDeleted, cancellationToken);

                if (!anotherDefaultExists)
                    throw new InvalidOperationException(
                        $"'{taxRate.Name}' is the default rate for {taxRate.CountryCode}. " +
                        "Make another rate the default first — deleting this one would leave new " +
                        "quotes there with no rate to use.");
            }

            // Soft delete
            taxRate.IsDeleted = true;
            taxRate.UpdatedAtUtc = DateTime.UtcNow;

            await _context.SaveChangesAsync(cancellationToken);
        }
    }
}
