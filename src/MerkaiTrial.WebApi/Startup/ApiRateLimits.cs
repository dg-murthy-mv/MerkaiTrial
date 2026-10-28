// =====================================================================
// FILE: MerkaiTrial.WebApi/Startup/ApiRateLimits.cs
//
// NEW FILE (105 — VUL-18). How fast one person may call the API.
//
//   1,200 requests a minute per signed-in person (agreed with you in 105).
//
// That is far more than anyone clicking through the website makes — a
// dashboard is about ten calls — and it stops a script from walking every
// list and every id as fast as the server will answer.
//
// HOW IT COUNTS
//   A sliding minute in six 10-second steps: whatever the person used in
//   the last 60 seconds counts, so a burst cannot dodge the limit by
//   straddling the turn of a minute. Counted per person (the UserId in
//   the token), so one person's script never slows anyone else down.
//   Calls with no sign-in are not counted here: the API's anonymous
//   endpoints are the customer quote/invoice links (reached through
//   Admin.Web, so they all share Admin.Web's address) and the Resend
//   webhook (signed). /health is never limited.
//
// OVER THE LIMIT
//   429 Too Many Requests, Retry-After in seconds, and a sentence the
//   website can show. One warning line per person per minute in the log,
//   not one per refused call.
//
// CONFIG
//   RateLimits:ApiRequestsPerPersonPerMinute   (default 1200; 0 = off)
//
// The counters are in memory, per server. With more than one server each
// keeps its own count — still a limit, just per server.
// =====================================================================

using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using MerkaiTrial.Application.Security;
using Microsoft.AspNetCore.RateLimiting;

namespace MerkaiTrial.WebApi.Startup;

public static class ApiRateLimits
{
    public const string Setting = "RateLimits:ApiRequestsPerPersonPerMinute";
    public const int DefaultPerMinute = 1200;

    private static readonly ConcurrentDictionary<string, long> LastWarnedMinute = new();

    public static IServiceCollection AddApiRateLimits(this IServiceCollection services, IConfiguration config)
    {
        var perMinute = config.GetValue<int?>(Setting) ?? DefaultPerMinute;

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
            {
                var userId = ctx.User?.Identity?.IsAuthenticated == true
                    ? ctx.User.FindFirst(SignInService.ClaimUserId)?.Value
                    : null;

                if (perMinute <= 0 || string.IsNullOrEmpty(userId))
                    return RateLimitPartition.GetNoLimiter("none");

                return RateLimitPartition.GetSlidingWindowLimiter("u:" + userId, _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit          = perMinute,
                    Window               = TimeSpan.FromMinutes(1),
                    SegmentsPerWindow    = 6,
                    QueueLimit           = 0,
                    AutoReplenishment    = true,
                });
            });

            options.OnRejected = async (context, ct) =>
            {
                var http = context.HttpContext;
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait)
                    ? Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds))
                    : 10;

                http.Response.Headers.RetryAfter = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);

                var userId = http.User.FindFirst(SignInService.ClaimUserId)?.Value ?? "?";
                var minute = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute;
                if (LastWarnedMinute.TryGetValue(userId, out var last) is false || last != minute)
                {
                    LastWarnedMinute[userId] = minute;
                    http.RequestServices.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("RateLimit")
                        .LogWarning("API calls from user {UserId} paused: over {PerMinute} a minute", userId, perMinute);
                }

                await http.Response.WriteAsJsonAsync(new
                {
                    error = "Too many requests in a short time. Please wait a moment and try again.",
                    retryAfterSeconds = retryAfter,
                }, ct);
            };
        });

        return services;
    }
}
