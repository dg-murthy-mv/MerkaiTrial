using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MerkaiTrial.Application.Commands.Countries
{
    // =====================================================================
    // CREATE COUNTRY COMMAND
    // =====================================================================
    public class CreateCountryCommand : ICommandHandler
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? DialCode { get; set; }
        public string? CurrencyCode { get; set; }
        public string? TaxLabel { get; set; }
        public decimal? DefaultTaxRate { get; set; }
    }

    public class CreateCountryHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;

        public CreateCountryHandler(FlowDbContext context)
        {
            _context = context;
        }

        public async Task<CountryDto> Handle(CreateCountryCommand command, CancellationToken cancellationToken = default)
        {
            // Check if country code already exists
            var exists = await _context.Countries
                .AnyAsync(c => c.Code == command.Code, cancellationToken);

            if (exists)
                throw new InvalidOperationException($"Country with code '{command.Code}' already exists");

            var country = new Country
            {
                Id = Guid.NewGuid(),
                Code = command.Code.ToUpper(),
                Name = command.Name,
                DialCode = command.DialCode,
                CurrencyCode = command.CurrencyCode?.ToUpper(),
                TaxLabel = command.TaxLabel,
                DefaultTaxRate = command.DefaultTaxRate,
                IsActive = true,
                DisplayOrder = 999,
                CreatedAtUtc = DateTime.UtcNow
            };

            _context.Countries.Add(country);
            await _context.SaveChangesAsync(cancellationToken);

            return new CountryDto
            {
                Id = country.Id,
                Code = country.Code,
                Name = country.Name,
                DialCode = country.DialCode,
                CurrencyCode = country.CurrencyCode,
                TaxLabel = country.TaxLabel,
                DefaultTaxRate = country.DefaultTaxRate,
                IsActive = country.IsActive,
                DisplayOrder = country.DisplayOrder
            };
        }
    }

    // =====================================================================
    // UPDATE COUNTRY COMMAND
    // =====================================================================
    public class UpdateCountryCommand : ICommandHandler
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? DialCode { get; set; }
        public string? CurrencyCode { get; set; }
        public string? TaxLabel { get; set; }
        public decimal? DefaultTaxRate { get; set; }
        public bool IsActive { get; set; }
    }

    public class UpdateCountryHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;

        public UpdateCountryHandler(FlowDbContext context)
        {
            _context = context;
        }

        public async Task Handle(UpdateCountryCommand command, CancellationToken cancellationToken = default)
        {
            var country = await _context.Countries
                .FirstOrDefaultAsync(c => c.Code == command.Code, cancellationToken);

            if (country == null)
                throw new KeyNotFoundException($"Country with code '{command.Code}' not found");

            country.Name = command.Name;
            country.DialCode = command.DialCode;
            country.CurrencyCode = command.CurrencyCode?.ToUpper();
            country.TaxLabel = command.TaxLabel;
            country.DefaultTaxRate = command.DefaultTaxRate;
            country.IsActive = command.IsActive;
            country.UpdatedAtUtc = DateTime.UtcNow;

            await _context.SaveChangesAsync(cancellationToken);
        }
    }
}
