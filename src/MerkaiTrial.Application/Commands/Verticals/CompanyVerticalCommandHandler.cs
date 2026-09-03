using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using MerkaiTrial.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MerkaiTrial.Application.Commands.Verticals
{
    // =====================================================================
    // CREATE COMPANY VERTICAL COMMAND
    // =====================================================================
    public class CreateCompanyVerticalCommand : ICommandHandler
    {
        public Guid TenantId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? Icon { get; set; }
        public string? Color { get; set; }
        public string CreatedBy { get; set; } = string.Empty;
    }

    public class CreateCompanyVerticalHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;

        public CreateCompanyVerticalHandler(FlowDbContext context)
        {
            _context = context;
        }

        public async Task<CompanyVerticalDto> Handle(CreateCompanyVerticalCommand command, CancellationToken cancellationToken = default)
        {
            // Check for duplicate name within tenant
            var exists = await _context.CompanyVerticals
                .AnyAsync(v => v.TenantId == command.TenantId
                            && v.Name == command.Name
                            && !v.IsDeleted, cancellationToken);

            if (exists)
                throw new InvalidOperationException($"A vertical with name '{command.Name}' already exists");

            var vertical = new CompanyVertical
            {
                Id = Guid.NewGuid(),
                TenantId = command.TenantId,
                Name = command.Name,
                Description = command.Description,
                Icon = command.Icon,
                Color = command.Color,
                IsActive = true,
                IsSystem = false,  // Custom verticals are not system
                DisplayOrder = 999,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = command.CreatedBy,
                IsDeleted = false
            };

            _context.CompanyVerticals.Add(vertical);
            await _context.SaveChangesAsync(cancellationToken);

            return new CompanyVerticalDto
            {
                Id = vertical.Id,
                TenantId = vertical.TenantId,
                Name = vertical.Name,
                Description = vertical.Description,
                Icon = vertical.Icon,
                Color = vertical.Color,
                IsActive = vertical.IsActive,
                IsSystem = vertical.IsSystem,
                DisplayOrder = vertical.DisplayOrder
            };
        }
    }

    // =====================================================================
    // UPDATE COMPANY VERTICAL COMMAND
    // =====================================================================
    public class UpdateCompanyVerticalCommand : ICommandHandler
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? Icon { get; set; }
        public string? Color { get; set; }
        public string UpdatedBy { get; set; } = string.Empty;
    }

    public class UpdateCompanyVerticalHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;

        public UpdateCompanyVerticalHandler(FlowDbContext context)
        {
            _context = context;
        }

        public async Task Handle(UpdateCompanyVerticalCommand command, CancellationToken cancellationToken = default)
        {
            var vertical = await _context.CompanyVerticals
                .FirstOrDefaultAsync(v => v.Id == command.Id && !v.IsDeleted, cancellationToken);

            if (vertical == null)
                throw new KeyNotFoundException($"Vertical with ID '{command.Id}' not found");

            // System verticals have limited editability (only description, icon, color)
            if (vertical.IsSystem)
            {
                vertical.Description = command.Description;
                vertical.Icon = command.Icon;
                vertical.Color = command.Color;
            }
            else
            {
                // Custom verticals can change name too
                // Check for duplicate name
                var duplicateExists = await _context.CompanyVerticals
                    .AnyAsync(v => v.TenantId == vertical.TenantId
                                && v.Name == command.Name
                                && v.Id != command.Id
                                && !v.IsDeleted, cancellationToken);

                if (duplicateExists)
                    throw new InvalidOperationException($"A vertical with name '{command.Name}' already exists");

                vertical.Name = command.Name;
                vertical.Description = command.Description;
                vertical.Icon = command.Icon;
                vertical.Color = command.Color;
            }

            vertical.UpdatedAtUtc = DateTime.UtcNow;
            vertical.UpdatedBy = command.UpdatedBy;

            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    // =====================================================================
    // DELETE COMPANY VERTICAL COMMAND
    // =====================================================================
    public class DeleteCompanyVerticalCommand
    {
        public Guid Id { get; set; }
    }

    public class DeleteCompanyVerticalHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;

        public DeleteCompanyVerticalHandler(FlowDbContext context)
        {
            _context = context;
        }

        public async Task Handle(DeleteCompanyVerticalCommand command, CancellationToken cancellationToken = default)
        {
            var vertical = await _context.CompanyVerticals
                .FirstOrDefaultAsync(v => v.Id == command.Id && !v.IsDeleted, cancellationToken);

            if (vertical == null)
                throw new KeyNotFoundException($"Vertical with ID '{command.Id}' not found");

            // Cannot delete system verticals
            if (vertical.IsSystem)
                throw new InvalidOperationException("System verticals cannot be deleted");

            // Check if vertical is in use
            var inUse = await _context.Companies
                .AnyAsync(c => c.Vertical == vertical.Name && !c.IsDeleted, cancellationToken);

            if (inUse)
                throw new InvalidOperationException($"Cannot delete vertical '{vertical.Name}' because it is being used by companies");

            // Soft delete
            vertical.IsDeleted = true;
            vertical.UpdatedAtUtc = DateTime.UtcNow;

            await _context.SaveChangesAsync(cancellationToken);
        }
    }
}
