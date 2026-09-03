// =====================================================================
// MetaQueries.cs
// Location: MerkaiTrial.Application/Commands/Meta/MetaQueries.cs
//
// Follows exact same pattern as LeadQueries / GetLeadChannelsHandler etc.
// Controllers dispatch to handlers — handlers own FlowDbContext.
// =====================================================================

using MerkaiTrial.Application.Services;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Commands.Meta;

// ── DTOs ─────────────────────────────────────────────────────────────

public record CountryMetaDto(
    Guid    Id,
    string  Code,
    string  Name,
    string  CurrencyCode,
    string  CurrencySymbol,
    int     CurrencyDecimals,
    string? DialCode,
    string? DateFormat,
    string? Timezone
);

public record VerticalDto(Guid Id, string Name, string? Description);

// ── Queries ───────────────────────────────────────────────────────────

public record GetCountriesMetaQuery();
public record GetVerticalsQuery();

// ── Handlers ─────────────────────────────────────────────────────────

public class GetCountriesMetaHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly ILogger<GetCountriesMetaHandler> _logger;

    public GetCountriesMetaHandler(FlowDbContext db, ILogger<GetCountriesMetaHandler> logger)
    {
        _db     = db;
        _logger = logger;
    }

    public async Task<List<CountryMetaDto>> Handle(
        GetCountriesMetaQuery query, CancellationToken ct = default)
    {
        try
        {
            return await _db.Countries
                .AsNoTracking()
                .Where(c => c.IsActive && !c.IsDeleted)
                .OrderBy(c => c.Name)
                .Select(c => new CountryMetaDto(
                    c.Id,
                    c.Code,
                    c.Name,
                    c.CurrencyCode,
                    c.CurrencySymbol,
                    c.CurrencyDecimals,
                    c.DialCode,
                    c.DateFormat,
                    c.Timezone
                ))
                .ToListAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load countries meta");
            throw;
        }
    }
}

public class GetVerticalsHandler : ICommandHandler
{
    private readonly FlowDbContext _db;
    private readonly ILogger<GetVerticalsHandler> _logger;

    public GetVerticalsHandler(FlowDbContext db, ILogger<GetVerticalsHandler> logger)
    {
        _db     = db;
        _logger = logger;
    }

    public async Task<List<VerticalDto>> Handle(
        GetVerticalsQuery query, CancellationToken ct = default)
    {
        try
        {
            return await _db.CompanyVerticals
                .AsNoTracking()
                .Where(v => v.IsActive && !v.IsDeleted)
                .OrderBy(v => v.Name)
                .Select(v => new VerticalDto(v.Id, v.Name, v.Description))
                .ToListAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load verticals");
            throw;
        }
    }
}
