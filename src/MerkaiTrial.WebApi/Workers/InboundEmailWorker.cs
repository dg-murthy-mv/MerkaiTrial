// =====================================================================
// InboundEmailWorker.cs
// Location: MerkaiTrial.WebApi/Workers/InboundEmailWorker.cs
//
// NEW FILE (083). Finishes the emails the webhook filed: downloads them,
// files them on the right record, keeps their attachments, tells the
// owner. The work itself is InboundEmailProcessor; this class only
// decides WHEN, and makes sure it happens exactly once.
//
// THE SAME THREE RULES AS OutboundMessageWorker, for the same reasons:
//
//   1. A hosted service is a singleton. Every email gets its own scope.
//   2. There is no signed-in user, so the global tenant filter sees
//      Guid.Empty. The CLAIM (raw SQL) needs no filter; everything after
//      it runs on a FlowDbContext built for the email's own workspace —
//      see 4.
//   3. Claiming is one atomic UPDATE … OUTPUT under ROWLOCK + READPAST,
//      so two instances never process the same email. A crashed claim
//      expires (LockedUntilUtc) and the email is picked up again; the
//      processor is written so that is harmless.
//
//   4. NEW HERE — A WORKSPACE-SCOPED CONTEXT PER EMAIL. The processor
//      raises notifications through NotificationDispatcher, which reads
//      each person's preferences and the workspace's locked defaults
//      through the normal tenant filter. With Guid.Empty in context it
//      would find none and send everyone the built-in defaults, silently
//      ignoring "Required" locks and personal choices. So each email is
//      processed on `new FlowDbContext(options, new WorkerTenantProvider(
//      tenantId))`, with its own dispatcher on that same context, and the
//      notification rules behave exactly as they do for a click in the app.
//
// WHEN EMAIL IS NOT CONFIGURED (no Email__ApiKey) nothing is claimed and
// no attempt is used up. Configure the key and the backlog is processed.
// =====================================================================

using System.Data;
using System.Data.Common;
using MerkaiTrial.Application.Services.Inbound;
using MerkaiTrial.Application.Services.Notifications;
using MerkaiTrial.Application.Services.Storage;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using MerkaiTrial.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace MerkaiTrial.WebApi.Workers;

public sealed class InboundEmailWorkerOptions
{
    public const string SectionName = "InboundWorker";

    /// <summary>How often to look for work when there is none.</summary>
    public int IdleSecondsBetweenPolls { get; set; } = 10;

    /// <summary>Emails claimed per tick.</summary>
    public int BatchSize { get; set; } = 10;

    /// <summary>How long a claim holds — long enough for a slow attachment download.</summary>
    public int ClaimMinutes { get; set; } = 10;
}

/// <summary>
/// The workspace an email belongs to, for the length of one email's
/// processing. See rule 4 in the header.
/// </summary>
public sealed class WorkerTenantProvider : ITenantProvider
{
    
    public WorkerTenantProvider(Guid tenantId) => TenantId = tenantId;


    public Guid TenantId { get; }

    public bool HasTenant => TenantId != Guid.Empty;
}

public sealed class InboundEmailWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<InboundEmailWorker> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly InboundEmailWorkerOptions _options;

    public InboundEmailWorker(
        IServiceScopeFactory scopes,
        ILogger<InboundEmailWorker> logger,
        ILoggerFactory loggerFactory,
        IOptions<InboundEmailWorkerOptions> options)
    {
        _scopes = scopes;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Inbound email worker started (batch {Batch}, poll {Poll}s)",
            _options.BatchSize, _options.IdleSecondsBetweenPolls);

        try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var handled = 0;

            try
            {
                handled = await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Inbound email worker tick failed");
            }

            if (handled >= _options.BatchSize) continue;   // probably more waiting

            try { await Task.Delay(TimeSpan.FromSeconds(_options.IdleSecondsBetweenPolls), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("Inbound email worker stopped");
    }

    private async Task<int> RunOnceAsync(CancellationToken ct)
    {
        List<(Guid Id, Guid TenantId)> claimed;

        using (var scope = _scopes.CreateScope())
        {
            var email = scope.ServiceProvider.GetRequiredService<IOptions<EmailOptions>>().Value;

            // Nothing to fetch with — leave every row exactly as it is.
            if (!email.IsConfigured) return 0;

            var db = scope.ServiceProvider.GetRequiredService<FlowDbContext>();
            claimed = await ClaimAsync(db, ct);
        }

        foreach (var (id, tenantId) in claimed)
        {
            if (ct.IsCancellationRequested) break;
            await ProcessOneAsync(id, tenantId, ct);
        }

        return claimed.Count;
    }

    private async Task ProcessOneAsync(Guid id, Guid tenantId, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var sp = scope.ServiceProvider;

        try
        {
            var dbOptions = sp.GetRequiredService<DbContextOptions<FlowDbContext>>();
            var emailOptions = sp.GetRequiredService<IOptions<EmailOptions>>();

            // Rule 4: this email's workspace, for everything that follows.
            await using var db = new FlowDbContext(dbOptions, new WorkerTenantProvider(tenantId));

            var dispatcher = new NotificationDispatcher(
                db, _loggerFactory.CreateLogger<NotificationDispatcher>(), emailOptions);

            var processor = new InboundEmailProcessor(
                db,
                sp.GetRequiredService<IResendInboundClient>(),
                sp.GetRequiredService<IFileStorageService>(),
                dispatcher,
                emailOptions.Value,
                _loggerFactory.CreateLogger<InboundEmailProcessor>());

            await processor.ProcessAsync(id, tenantId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down. The claim expires and the email is picked up
            // again on the next start.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Inbound email {Id} could not be processed", id);
            await ReleaseAfterErrorAsync(sp, id, ex.Message);
        }
    }

    /// <summary>
    /// An unexpected error: count the attempt, schedule another in five
    /// minutes, give up (Failed) after the limit — and release the claim
    /// either way, so the email is not stuck "Processing" for ten minutes.
    /// </summary>
    private async Task ReleaseAfterErrorAsync(IServiceProvider sp, Guid id, string message)
    {
        try
        {
            var db = sp.GetRequiredService<FlowDbContext>();
            var error = message.Length <= 1000 ? message : message[..1000];
            var next = DateTime.UtcNow.AddMinutes(5);

            // IgnoreQueryFilters — rule 2. The Id names exactly one row.
            await db.InboundEmails.IgnoreQueryFilters()
                .Where(e => e.Id == id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.ProcessingAttempts, e => e.ProcessingAttempts + 1)
                    .SetProperty(e => e.ProcessingState, e => e.ProcessingAttempts + 1 >= InboundEmailProcessor.MaxAttempts
                        ? InboundProcessingState.Failed
                        : InboundProcessingState.Retry)
                    .SetProperty(e => e.NextAttemptAtUtc, next)
                    .SetProperty(e => e.LockedUntilUtc, (DateTime?)null)
                    .SetProperty(e => e.ProcessingError, error), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not release inbound email {Id} after an error", id);
        }
    }

    /// <summary>
    /// Pending (0) or Retry (3) and due — or Processing (1) whose claim has
    /// expired, i.e. an instance crashed holding it. One statement.
    /// </summary>
    private async Task<List<(Guid, Guid)>> ClaimAsync(FlowDbContext db, CancellationToken ct)
    {
        const string sql = @"
UPDATE TOP (@batch) dbo.InboundEmails WITH (ROWLOCK, READPAST)
SET    ProcessingState = 1,
       LockedUntilUtc  = @lockUntil
OUTPUT inserted.Id, inserted.TenantId
WHERE  IsDeleted = 0
  AND (
        (ProcessingState IN (0, 3) AND (NextAttemptAtUtc IS NULL OR NextAttemptAtUtc <= @now))
     OR (ProcessingState = 1 AND LockedUntilUtc IS NOT NULL AND LockedUntilUtc <= @now)
      );";

        var now = DateTime.UtcNow;
        var result = new List<(Guid, Guid)>();

        var connection = db.Database.GetDbConnection();
        var opened = false;

        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
            opened = true;
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;

            if (db.Database.CurrentTransaction is not null)
                command.Transaction = db.Database.CurrentTransaction.GetDbTransaction();

            command.Parameters.Add(Param(command, "@batch", _options.BatchSize));
            command.Parameters.Add(Param(command, "@now", now));
            command.Parameters.Add(Param(command, "@lockUntil", now.AddMinutes(_options.ClaimMinutes)));

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result.Add((reader.GetGuid(0), reader.GetGuid(1)));
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }

        if (result.Count > 0)
            _logger.LogInformation("Processing {Count} received email(s)", result.Count);

        return result;
    }

    private static DbParameter Param(DbCommand command, string name, object value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        return p;
    }
}
