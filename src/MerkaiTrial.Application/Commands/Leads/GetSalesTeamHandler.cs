// =====================================================================
// SALES TEAM HANDLER - FINAL VERSION
// Location: MerkaiTrial.Application/Commands/Leads/GetSalesTeamHandler.cs
// =====================================================================

using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Commands.Leads
{
    // ==================== GET SALES TEAM (Users for Assignment) ====================
    public record GetSalesTeamQuery(Guid TenantId);

    public class GetSalesTeamHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<GetSalesTeamHandler> _logger;

        public GetSalesTeamHandler(FlowDbContext context, ILogger<GetSalesTeamHandler> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<List<SalesTeamMemberDto>> Handle(
            GetSalesTeamQuery query,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var users = await _context.Set<User>()
                    .AsNoTracking()
                    .Where(u => u.TenantId == query.TenantId && u.IsActive && !u.IsDeleted)
                    .OrderBy(u => u.FirstName)
                    .ThenBy(u => u.LastName)
                    .Select(u => new SalesTeamMemberDto(
                        u.Id,
                        u.FirstName + " " + u.LastName,
                        u.Email,
                        u.JobTitle
                    ))
                    .ToListAsync(cancellationToken);

                _logger.LogInformation("Loaded {Count} sales team members for tenant {TenantId}", users.Count, query.TenantId);

                return users;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting sales team for tenant {TenantId}", query.TenantId);
                throw;
            }
        }
    }

    // DTO
    
}