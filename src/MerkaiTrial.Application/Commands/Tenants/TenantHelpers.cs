// =====================================================================
// COUNTRIES & TIMEZONE HELPERS
// Location: MerkaiTrial.Application/Commands/Tenants/TenantHelpers.cs
// =====================================================================

using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Application.Commands.Tenants
{
    // ==================== GET COUNTRIES FOR DROPDOWN ====================
    public class GetCountriesForDropdownHandler : ICommandHandler
    {
        private readonly FlowDbContext _context;
        private readonly ILogger<GetCountriesForDropdownHandler> _logger;

        public GetCountriesForDropdownHandler(FlowDbContext context, ILogger<GetCountriesForDropdownHandler> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<List<CountryDropdownDto>> Handle(CancellationToken cancellationToken = default)
        {
            try
            {
                return await _context.Countries
                        .AsNoTracking()
                        .Where(c => c.IsActive && !c.IsDeleted)
                        .OrderBy(c => c.Name)
                        .Select(c => new CountryDropdownDto(
                            c.Id,
                            c.Name,
                            c.Code,
                            c.CurrencyCode
                        ))
                        .ToListAsync(cancellationToken);

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting countries for dropdown");
                throw;
            }
        }
    }

    // ==================== TIMEZONE CONSTANTS ====================
    public static class TimezoneHelper
    {
        public static List<TimezoneDto> GetAllTimezones()
        {
            return new List<TimezoneDto>
            {
                // UTC
                new TimezoneDto("UTC", "UTC (Coordinated Universal Time)"),

                // Americas
                new TimezoneDto("America/New_York", "America/New York (EST/EDT)"),
                new TimezoneDto("America/Chicago", "America/Chicago (CST/CDT)"),
                new TimezoneDto("America/Denver", "America/Denver (MST/MDT)"),
                new TimezoneDto("America/Los_Angeles", "America/Los Angeles (PST/PDT)"),
                new TimezoneDto("America/Phoenix", "America/Phoenix (MST)"),
                new TimezoneDto("America/Toronto", "America/Toronto (EST/EDT)"),
                new TimezoneDto("America/Vancouver", "America/Vancouver (PST/PDT)"),
                new TimezoneDto("America/Sao_Paulo", "America/Sao Paulo (BRT)"),
                new TimezoneDto("America/Argentina/Buenos_Aires", "America/Buenos Aires (ART)"),
                new TimezoneDto("America/Mexico_City", "America/Mexico City (CST/CDT)"),

                // Europe
                new TimezoneDto("Europe/London", "Europe/London (GMT/BST)"),
                new TimezoneDto("Europe/Paris", "Europe/Paris (CET/CEST)"),
                new TimezoneDto("Europe/Berlin", "Europe/Berlin (CET/CEST)"),
                new TimezoneDto("Europe/Rome", "Europe/Rome (CET/CEST)"),
                new TimezoneDto("Europe/Madrid", "Europe/Madrid (CET/CEST)"),
                new TimezoneDto("Europe/Amsterdam", "Europe/Amsterdam (CET/CEST)"),
                new TimezoneDto("Europe/Brussels", "Europe/Brussels (CET/CEST)"),
                new TimezoneDto("Europe/Vienna", "Europe/Vienna (CET/CEST)"),
                new TimezoneDto("Europe/Stockholm", "Europe/Stockholm (CET/CEST)"),
                new TimezoneDto("Europe/Warsaw", "Europe/Warsaw (CET/CEST)"),
                new TimezoneDto("Europe/Athens", "Europe/Athens (EET/EEST)"),
                new TimezoneDto("Europe/Istanbul", "Europe/Istanbul (TRT)"),
                new TimezoneDto("Europe/Moscow", "Europe/Moscow (MSK)"),

                // Asia
                new TimezoneDto("Asia/Dubai", "Asia/Dubai (GST)"),
                new TimezoneDto("Asia/Karachi", "Asia/Karachi (PKT)"),
                new TimezoneDto("Asia/Kolkata", "Asia/Kolkata (IST)"),
                new TimezoneDto("Asia/Dhaka", "Asia/Dhaka (BST)"),
                new TimezoneDto("Asia/Bangkok", "Asia/Bangkok (ICT)"),
                new TimezoneDto("Asia/Singapore", "Asia/Singapore (SGT)"),
                new TimezoneDto("Asia/Hong_Kong", "Asia/Hong Kong (HKT)"),
                new TimezoneDto("Asia/Shanghai", "Asia/Shanghai (CST)"),
                new TimezoneDto("Asia/Tokyo", "Asia/Tokyo (JST)"),
                new TimezoneDto("Asia/Seoul", "Asia/Seoul (KST)"),
                new TimezoneDto("Asia/Manila", "Asia/Manila (PHT)"),
                new TimezoneDto("Asia/Jakarta", "Asia/Jakarta (WIB)"),

                // Australia & Pacific
                new TimezoneDto("Australia/Sydney", "Australia/Sydney (AEDT/AEST)"),
                new TimezoneDto("Australia/Melbourne", "Australia/Melbourne (AEDT/AEST)"),
                new TimezoneDto("Australia/Brisbane", "Australia/Brisbane (AEST)"),
                new TimezoneDto("Australia/Perth", "Australia/Perth (AWST)"),
                new TimezoneDto("Pacific/Auckland", "Pacific/Auckland (NZDT/NZST)"),
                new TimezoneDto("Pacific/Fiji", "Pacific/Fiji (FJT)"),

                // Africa
                new TimezoneDto("Africa/Cairo", "Africa/Cairo (EET)"),
                new TimezoneDto("Africa/Johannesburg", "Africa/Johannesburg (SAST)"),
                new TimezoneDto("Africa/Lagos", "Africa/Lagos (WAT)"),
                new TimezoneDto("Africa/Nairobi", "Africa/Nairobi (EAT)"),

                // Middle East
                new TimezoneDto("Asia/Riyadh", "Asia/Riyadh (AST)"),
                new TimezoneDto("Asia/Jerusalem", "Asia/Jerusalem (IST/IDT)"),
                new TimezoneDto("Asia/Tehran", "Asia/Tehran (IRST)")
            };
        }

        public static List<TimezoneDto> GetCommonTimezones()
        {
            return new List<TimezoneDto>
            {
                new TimezoneDto("UTC", "UTC"),
                new TimezoneDto("America/New_York", "Eastern Time (US)"),
                new TimezoneDto("America/Chicago", "Central Time (US)"),
                new TimezoneDto("America/Los_Angeles", "Pacific Time (US)"),
                new TimezoneDto("Europe/London", "London (UK)"),
                new TimezoneDto("Europe/Paris", "Paris (France)"),
                new TimezoneDto("Asia/Dubai", "Dubai (UAE)"),
                new TimezoneDto("Asia/Kolkata", "Kolkata (India)"),
                new TimezoneDto("Asia/Bangkok", "Bangkok (Thailand)"),
                new TimezoneDto("Asia/Singapore", "Singapore"),
                new TimezoneDto("Asia/Tokyo", "Tokyo (Japan)"),
                new TimezoneDto("Australia/Sydney", "Sydney (Australia)")
            };
        }
    }

    // ==================== PLAN HELPER ====================
    public static class PlanHelper
    {
        public static List<string> GetAllPlans()
        {
            return new List<string> { "Starter", "Professional", "Enterprise" };
        }

        public static string GetPlanDisplayName(string plan)
        {
            return plan switch
            {
                "Starter" => "Starter (5 users, 100 leads, 50 deals, 5 GB)",
                "Professional" => "Professional (10 users, 200 leads, 100 deals, 10 GB)",
                "Enterprise" => "Enterprise (20 users, 300 leads, 200 deals, 20 GB)",
                _ => plan
            };
        }
    }
}
