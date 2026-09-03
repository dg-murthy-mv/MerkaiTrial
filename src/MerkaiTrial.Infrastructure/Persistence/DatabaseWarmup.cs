// =====================================================================
// FILE: MerkaiTrial.Infrastructure/Persistence/DatabaseWarmup.cs
//
// SESSION 8 — startup warm-up
//
// THE PROBLEM THIS SOLVES:
// EF Core builds its model lazily, on the first resolution of a DbContext
// that actually executes a query. That build costs 4-10 seconds and holds
// a lock: every concurrent request queues behind it.
//
// Measured 2026-08-04, first ViewAs -> Dashboard after a cold start:
//     GET /Dashboard                    12,320ms
//       api/leads/stats                 10,470ms
//       api/deals                       11,244ms
//       api/quotes                      11,161ms
//       api/quotes/statistics           10,469ms
//       api/invoices/statistics         10,469ms
//
// All five API calls were dispatched between .503 and .671, and all five
// returned at 15:25:14.32 — within 10ms of each other. That is not five
// slow calls. That is five calls waiting on one model build.
//
// The same page, warm: 440ms, with the API calls at 176-258ms.
//
// WHAT THIS DOES:
// Runs a handful of trivial queries at startup, before the host begins
// serving, so the model is already built when the first real user arrives.
// The queries chosen are exactly the four that DemoAuthenticationHandler
// issues on every request (Users, UserRoles, Roles, Tenants) — warming
// those warms both the model AND the query-plan cache for the hottest path
// in the application.
//
// FAILURE POLICY: this must NEVER prevent the app from starting. Every step
// is individually guarded. If the database is unreachable at startup, we log
// a warning and continue — the app then behaves exactly as it did before this
// file existed (slow first request, but running).
//
// PLACEMENT: this lives in Infrastructure because both hosts already
// reference Infrastructure and both need it. It deliberately takes an
// IServiceProvider rather than an IHost/WebApplication so that it needs no
// dependency on Microsoft.Extensions.Hosting or ASP.NET Core.
// =====================================================================

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace MerkaiTrial.Infrastructure.Persistence
{
    public static class DatabaseWarmup
    {
        /// <summary>
        /// Forces EF Core to build its model and open a pooled connection before
        /// the host starts serving requests. Call once, immediately after
        /// builder.Build() and before app.Run().
        /// </summary>
        /// <param name="services">The built application's root service provider (app.Services).</param>
        /// <param name="hostName">Name used in the log line, e.g. "Admin.Web" or "WebApi".</param>
        public static async Task WarmUpAsync(
            IServiceProvider services,
            string hostName,
            CancellationToken cancellationToken = default)
        {
            var loggerFactory = services.GetService<ILoggerFactory>();
            var logger = loggerFactory?.CreateLogger("DatabaseWarmup");

            var sw = Stopwatch.StartNew();

            try
            {
                using var scope = services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FlowDbContext>();

                // 1. Open a connection. This alone triggers model building, and
                //    also primes the ADO.NET connection pool so the first real
                //    request does not pay TCP + TLS + login handshake either.
                if (!await db.Database.CanConnectAsync(cancellationToken))
                {
                    logger?.LogWarning(
                        "DatabaseWarmup [{Host}]: database not reachable at startup — skipping warm-up. " +
                        "The first request will pay the EF model-build cost instead.", hostName);
                    return;
                }

                // 2. Warm the four queries DemoAuthenticationHandler runs on
                //    EVERY request. Each is guarded separately so that a renamed
                //    DbSet degrades the warm-up rather than breaking startup.
                await SafeAsync(logger, hostName, "Tenants",
                    () => db.Tenants.AsNoTracking().Select(t => t.Id).FirstOrDefaultAsync(cancellationToken));

                await SafeAsync(logger, hostName, "Users",
                    () => db.Users.AsNoTracking().Select(u => u.Id).FirstOrDefaultAsync(cancellationToken));

                await SafeAsync(logger, hostName, "UserRoles",
                    () => db.UserRoles.AsNoTracking().Select(ur => ur.UserId).FirstOrDefaultAsync(cancellationToken));

                await SafeAsync(logger, hostName, "Roles",
                    () => db.Roles.AsNoTracking().Select(r => r.Id).FirstOrDefaultAsync(cancellationToken));

                sw.Stop();

                // Deliberately Information, not Debug: this fires ONCE per process,
                // and the number it prints is the cost a user would otherwise have
                // paid. If this line ever reads under ~200ms, something changed and
                // the warm-up is no longer doing its job.
                logger?.LogInformation(
                    "DatabaseWarmup [{Host}]: EF model built and connection pool primed in {ElapsedMs}ms. " +
                    "First user request will not pay this cost.",
                    hostName, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                sw.Stop();
                logger?.LogWarning(ex,
                    "DatabaseWarmup [{Host}]: warm-up failed after {ElapsedMs}ms — continuing startup. " +
                    "The first request will pay the EF model-build cost instead.",
                    hostName, sw.ElapsedMilliseconds);
            }
        }

        private static async Task SafeAsync<T>(
            ILogger? logger, string hostName, string name, Func<Task<T>> query)
        {
            try
            {
                await query();
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex,
                    "DatabaseWarmup [{Host}]: could not warm {Query} — skipping it.", hostName, name);
            }
        }
    }
}
