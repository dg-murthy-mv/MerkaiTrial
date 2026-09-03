// File: MerkaiTrial.Application/Commands/Users/GetSalesTeamHandler.cs
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Application.Commands.Users
{
    public class GetSalesTeamHandler
    {
        private readonly FlowDbContext _db;

        public GetSalesTeamHandler(FlowDbContext db)
        {
            _db = db;
        }

        public async Task<List<SalesTeamMemberDto>> Handle(Guid tenantId)
        {
            return await _db.Users
                .Where(u =>
                    u.TenantId == tenantId &&
                    !u.IsDeleted &&
                    u.IsActive &&
                    u.Department == "Sales")
                .OrderBy(u => u.FirstName)
                .ThenBy(u => u.LastName)
                .Select(u => new SalesTeamMemberDto(
                    u.Id,
                    $"{u.FirstName} {u.LastName}".Trim(),
                    u.Email,
                    u.JobTitle
                ))
                .ToListAsync();
        }
    }
}