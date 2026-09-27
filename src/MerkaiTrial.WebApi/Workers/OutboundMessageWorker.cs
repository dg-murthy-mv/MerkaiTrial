// =====================================================================
// OutboundMessageWorker.cs
// Location: MerkaiTrial.WebApi/Workers/OutboundMessageWorker.cs
//
// NEW FILE (038). Drains the OutboundMessages queue, and once a day
// trims old rows.
//
// ─────────────────────────────────────────────────────────────────────
// THREE THINGS IN HERE ARE NOT OPTIONAL. Each one is a bug that would
// otherwise be silent.
// ─────────────────────────────────────────────────────────────────────
//
// 1. A HOSTED SERVICE IS A SINGLETON, SO IT CANNOT INJECT FlowDbContext.
//    Program.cs sets ValidateScopes = true and ValidateOnBuild = true, so
//    an attempt to do so refuses to start the app — correctly. Every tick
//    creates its own scope through IServiceScopeFactory and disposes it.
//    That also means one DbContext per tick rather than one living for the
//    lifetime of the process, which is what you want anyway: a long-lived
//    context accumulates tracked entities for ever.
//
// 2. IgnoreQueryFilters() ON EVERY QUERY AGAINST OutboundMessages.
//    A worker has no HttpContext, so ITenantProvider yields Guid.Empty and
//    FlowDbContext's global filter is `TenantId == Guid.Empty`. Without
//    IgnoreQueryFilters every query here returns ZERO ROWS, the queue fills
//    up, nothing is ever sent, and NOTHING IS LOGGED because from the
//    worker's point of view there is simply no work. That is the single
//    easiest way to ship a broken queue.
//
//    This is the only place in the codebase allowed to bypass that filter
//    for this table. It is safe because the worker never returns a row to
//    anyone: it reads a message and sends it to the address already stored
//    on it.
//
// 3. CLAIMING IS AN ATOMIC UPDATE ... OUTPUT.
//    Read-then-update would let two instances pick the same message and
//    send it twice. The claim marks rows Sending and returns their ids in
//    one statement, under ROWLOCK and READPAST so a second worker skips
//    what the first has taken rather than blocking on it.
//
//    App Service scales out. Today there is one instance; the day there
//    are two, this is the difference between working and emailing every
//    customer twice.
//
// WHY THE PROVIDER IDEMPOTENCY KEY STILL MATTERS
//   A crash between "Resend accepted it" and "we saved Sent" leaves the
//   row locked until LockedUntilUtc passes, after which it is retried. The
//   Idempotency-Key on the send is what stops that retry becoming a second
//   email. Claiming prevents concurrency; idempotency prevents crash
//   duplicates. You need both.
// =====================================================================

using System.Data;
using System.Data.Common;
using MerkaiTrial.Application.Services.Notifications;
using MerkaiTrial.Domain.Entities;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;   // GetDbTransaction()
using Microsoft.Extensions.Options;

namespace MerkaiTrial.WebApi.Workers;

public sealed class OutboundMessageWorkerOptions
{
    public const string SectionName = "OutboundWorker";

    /// <summary>How often to look for work when the queue is empty.</summary>
    public int IdleSecondsBetweenPolls { get; set; } = 15;

    /// <summary>Messages claimed per tick.</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>
    /// Pause between individual sends. Resend's default limit is 2 requests
    /// a second; 600ms leaves headroom and this queue is never in a hurry.
    /// </summary>
    public int MillisecondsBetweenSends { get; set; } = 600;

    /// <summary>
    /// How long a claim holds. Long enough to cover a slow send, short
    /// enough that a crashed instance's work is picked up again promptly.
    /// </summary>
    public int ClaimMinutes { get; set; } = 5;

    /// <summary>Attempts before a message is marked Dead and waits for a person.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Delete sent/cancelled messages older than this. 0 disables.</summary>
    public int DeleteSentAfterDays { get; set; } = 90;

    /// <summary>Delete read and dismissed notifications older than this. 0 disables.</summary>
    public int DeleteNotificationsAfterDays { get; set; } = 180;
}

public sealed class OutboundMessageWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;   // see note 1
    private readonly ILogger<OutboundMessageWorker> _logger;
    private readonly OutboundMessageWorkerOptions _options;

    private DateTime _lastCleanupUtc = DateTime.MinValue;

    public OutboundMessageWorker(
        IServiceScopeFactory scopes,
        ILogger<OutboundMessageWorker> logger,
        IOptions<OutboundMessageWorkerOptions> options)
    {
        _scopes = scopes;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Outbound message worker started (batch {Batch}, poll {Poll}s)",
            _options.BatchSize, _options.IdleSecondsBetweenPolls);

        // Let the app finish starting before touching the database.
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var sent = 0;

            try
            {
                sent = await RunOnceAsync(stoppingToken);
                await MaybeCleanUpAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;   // shutting down, not a failure
            }
            catch (Exception ex)
            {
                // Never let one bad tick kill the worker for the lifetime of
                // the process. Log it and try again after the idle delay.
                _logger.LogError(ex, "Outbound worker tick failed");
            }

            // A full batch means there is probably more waiting, so go
            // straight round again rather than sleeping on a backlog.
            if (sent >= _options.BatchSize) continue;

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(_options.IdleSecondsBetweenPolls), stoppingToken);
            }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("Outbound message worker stopped");
    }

    // =================================================================
    // ONE TICK
    // =================================================================

    private async Task<int> RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();       // note 1

        var db = scope.ServiceProvider.GetRequiredService<FlowDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();

        if (!sender.IsEnabled)
        {
            // Nothing is claimed, so nothing is marked failed and no attempt
            // is burned. Configure a key later and the backlog goes out.
            _logger.LogDebug("Email sender is not configured — leaving the queue alone");
            return 0;
        }

        var claimedIds = await ClaimAsync(db, ct);
        if (claimedIds.Count == 0) return 0;

        // IgnoreQueryFilters — note 2. Without it this returns nothing.
        var messages = await db.OutboundMessages
            .IgnoreQueryFilters()
            .Where(m => claimedIds.Contains(m.Id))
            .ToListAsync(ct);

        _logger.LogInformation("Sending {Count} queued message(s)", messages.Count);

        var processed = 0;

        foreach (var message in messages)
        {
            if (ct.IsCancellationRequested) break;

            await SendOneAsync(db, sender, message, ct);
            processed++;

            // Pace for the provider's rate limit.
            if (processed < messages.Count)
            {
                try
                {
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(_options.MillisecondsBetweenSends), ct);
                }
                catch (OperationCanceledException) { break; }
            }
        }

        // One save for the whole batch. Each message's outcome is already on
        // its tracked entity.
        await db.SaveChangesAsync(CancellationToken.None);

        return processed;
    }

    private async Task SendOneAsync(
        FlowDbContext db, IEmailSender sender, OutboundMessage message, CancellationToken ct)
    {
        message.AttemptCount++;
        message.UpdatedAtUtc = DateTime.UtcNow;

        var result = await sender.SendAsync(new EmailMessage(
            IdempotencyKey: message.Id,          // see the header
            ToAddress: message.ToAddress,
            ToName: message.ToName,
            Subject: message.Subject,
            BodyHtml: message.BodyHtml,
            BodyText: message.BodyText,
            ReplyToAddress: message.ReplyToAddress), ct);

        if (result.Success)
        {
            message.Status = OutboundStatus.Sent;
            message.SentAtUtc = DateTime.UtcNow;
            message.ProviderMessageId = result.ProviderMessageId;
            message.LastError = null;
            message.LockedUntilUtc = null;
            message.NextAttemptAtUtc = null;
            return;
        }

        message.LastError = Truncate(result.Error, 2000);
        message.LockedUntilUtc = null;

        // A PERMANENT failure is dead immediately, whatever the attempt
        // count. Retrying "that address does not exist" five times over six
        // hours only delays someone noticing.
        if (!result.Retryable || message.AttemptCount >= _options.MaxAttempts)
        {
            message.Status = OutboundStatus.Dead;
            message.NextAttemptAtUtc = null;

            _logger.LogError(
                "Message {Id} to {To} gave up after {Attempts} attempt(s): {Error}",
                message.Id, message.ToAddress, message.AttemptCount, message.LastError);
            return;
        }

        message.Status = OutboundStatus.Failed;
        message.NextAttemptAtUtc = DateTime.UtcNow.Add(BackoffFor(message.AttemptCount));

        _logger.LogWarning(
            "Message {Id} to {To} failed (attempt {Attempts}), next try {Next}: {Error}",
            message.Id, message.ToAddress, message.AttemptCount,
            message.NextAttemptAtUtc, message.LastError);
    }

    /// <summary>
    /// 1m, 5m, 15m, 1h, then 6h. Wide enough that a provider outage is
    /// ridden out rather than hammered, short enough that a blip costs
    /// minutes.
    /// </summary>
    private static TimeSpan BackoffFor(int attemptCount) => attemptCount switch
    {
        1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(5),
        3 => TimeSpan.FromMinutes(15),
        4 => TimeSpan.FromHours(1),
        _ => TimeSpan.FromHours(6)
    };

    // =================================================================
    // CLAIMING — note 3
    // =================================================================

    /// <summary>
    /// Marks up to BatchSize due messages as Sending and returns their ids,
    /// in ONE statement.
    ///
    /// Raw ADO rather than EF: this is an UPDATE ... OUTPUT, which is not
    /// something EF's query pipeline should be asked to compose, and the
    /// locking hints are the entire point.
    ///
    ///   ROWLOCK   take row locks, not a page or table lock
    ///   READPAST  skip rows another worker has already locked instead of
    ///             waiting for them
    ///
    /// Status 0 = Pending, 3 = Failed, 1 = Sending.
    /// </summary>
    private async Task<List<Guid>> ClaimAsync(FlowDbContext db, CancellationToken ct)
    {
        const string sql = @"
UPDATE TOP (@batch) dbo.OutboundMessages WITH (ROWLOCK, READPAST)
SET    Status = 1,
       LockedUntilUtc = @lockUntil
OUTPUT inserted.Id
WHERE  Status IN (0, 3)
  AND  (NextAttemptAtUtc IS NULL OR NextAttemptAtUtc <= @now)
  AND  (LockedUntilUtc  IS NULL OR LockedUntilUtc  <= @now);";

        var now = DateTime.UtcNow;
        var ids = new List<Guid>();

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

            // The context may already be in a transaction in some hosting
            // arrangements; enlisting is required if so.
            if (db.Database.CurrentTransaction is not null)
                command.Transaction = db.Database.CurrentTransaction.GetDbTransaction();

            command.Parameters.Add(Param(command, "@batch", _options.BatchSize));
            command.Parameters.Add(Param(command, "@now", now));
            command.Parameters.Add(
                Param(command, "@lockUntil", now.AddMinutes(_options.ClaimMinutes)));

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                ids.Add(reader.GetGuid(0));
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }

        return ids;
    }

    private static DbParameter Param(DbCommand command, string name, object value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        return p;
    }

    // =================================================================
    // RETENTION — once a day
    // =================================================================

    private async Task MaybeCleanUpAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _lastCleanupUtc < TimeSpan.FromHours(24)) return;
        _lastCleanupUtc = DateTime.UtcNow;

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowDbContext>();

        // Old messages that are finished with. Dead ones are KEPT — they are
        // the ones somebody still has to look at.
        if (_options.DeleteSentAfterDays > 0)
        {
            var cutoff = DateTime.UtcNow.AddDays(-_options.DeleteSentAfterDays);

            var removed = await DeleteInBatchesAsync(
                () => db.OutboundMessages
                        .IgnoreQueryFilters()                      // note 2
                        .Where(m => m.CreatedAtUtc < cutoff
                                 && (m.Status == OutboundStatus.Sent
                                  || m.Status == OutboundStatus.Cancelled))
                        // OrderBy before Take — see the note on
                        // DeleteInBatchesAsync. Oldest first is also the
                        // right order to retire rows in.
                        .OrderBy(m => m.CreatedAtUtc),
                ct);

            if (removed > 0)
                _logger.LogInformation("Retention: removed {Count} old outbound message(s)", removed);
        }

        // Old notifications that have been read AND dismissed. Anything
        // still unread survives however old it is — deleting something
        // nobody has looked at is how people miss an approval request.
        if (_options.DeleteNotificationsAfterDays > 0)
        {
            var cutoff = DateTime.UtcNow.AddDays(-_options.DeleteNotificationsAfterDays);

            var removed = await DeleteInBatchesAsync(
                () => db.Notifications
                        .IgnoreQueryFilters()
                        .Where(n => n.CreatedAtUtc < cutoff
                                 && n.ReadAtUtc != null
                                 && n.DismissedAtUtc != null)
                        .OrderBy(n => n.CreatedAtUtc),
                ct);

            if (removed > 0)
                _logger.LogInformation("Retention: removed {Count} old notification(s)", removed);
        }
    }

    /// <summary>
    /// Batched on purpose. A single DELETE of a large set escalates to a
    /// table lock and holds it — on a table the worker and every page are
    /// using, that is an outage rather than a tidy-up.
    ///
    /// THE QUERY MUST BE ORDERED BEFORE IT GETS HERE. Take() without an
    /// OrderBy makes EF log
    ///
    ///     "The query uses a row limiting operator ('Skip'/'Take') without
    ///      an 'OrderBy' operator. This may lead to unpredictable results."
    ///
    /// on every cleanup pass. For a batched delete the order genuinely does
    /// not matter — each pass removes some N rows matching the predicate and
    /// loops until there are none — but a warning that fires daily and never
    /// means anything is exactly the kind of noise that hides a real one
    /// later. Both callers order by CreatedAtUtc.
    /// </summary>
    private static async Task<int> DeleteInBatchesAsync<T>(
        Func<IQueryable<T>> query, CancellationToken ct) where T : class
    {
        const int batch = 2000;
        const int maxBatches = 25;      // ~50k a day is plenty; the rest waits

        var total = 0;

        for (var i = 0; i < maxBatches; i++)
        {
            var removed = await query().Take(batch).ExecuteDeleteAsync(ct);
            total += removed;

            if (removed < batch) break;

            await Task.Delay(TimeSpan.FromMilliseconds(200), ct);   // let others through
        }

        return total;
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return value.Length <= max ? value : value[..max];
    }
}
