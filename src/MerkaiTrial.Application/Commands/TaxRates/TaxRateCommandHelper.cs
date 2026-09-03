using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MerkaiTrial.Application.Commands.TaxRates
{
    // =====================================================================
    // CREATE TAX RATE COMMAND
    // =====================================================================
    public class CreateTaxRateCommand : ICommandHandler
    {
        public Guid TenantId { get; set; }
        public string CountryCode { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string TaxType { get; set; } = string.Empty;
        public decimal Rate { get; set; }
        public bool IsDefault { get; set; }
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
            // Verify country exists
            var countryExists = await _context.Countries
                .AnyAsync(c => c.Code == command.CountryCode, cancellationToken);

            if (!countryExists)
                throw new InvalidOperationException($"Country '{command.CountryCode}' does not exist");

            // If setting as default, unset other defaults for this country
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
                Name = command.Name,
                TaxType = command.TaxType,
                Rate = command.Rate,
                IsDefault = command.IsDefault,
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
        public string Name { get; set; } = string.Empty;
        public decimal Rate { get; set; }
        public bool IsDefault { get; set; }
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

            taxRate.Name = command.Name;
            taxRate.Rate = command.Rate;
            taxRate.IsDefault = command.IsDefault;
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

            // Check if tax rate is in use (you might want to check Invoices, Quotes, etc.)
            // For now, we'll allow deletion but you can add validation here

            // Soft delete
            taxRate.IsDeleted = true;
            taxRate.UpdatedAtUtc = DateTime.UtcNow;

            await _context.SaveChangesAsync(cancellationToken);
        }
    }
}
