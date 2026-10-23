using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetRatel.Application.Events;
using NetRatel.Application.Notifications;
using NetRatel.Application.Observability;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Monitoring;

namespace NetRatel.Infrastructure.Events;

public sealed class OutboxProcessor(IServiceProvider services, ILogger<OutboxProcessor> logger) : BackgroundService
{
    private readonly IServiceProvider _services = services;
    private readonly ILogger<OutboxProcessor> _logger = logger;
    private readonly string _lockOwner = $"{Environment.MachineName}:{Guid.NewGuid():N}";
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(2);
    private readonly TimeSpan _lockDuration = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Outbox loop error");
            }

            await Task.Delay(_pollInterval, stoppingToken);
        }
    }

    internal async Task ProcessBatchAsync(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();

        var now = DateTimeOffset.UtcNow;

        var candidates = await db.OutboxMessages.AsNoTracking()
            .Where(x => !x.Type.StartsWith(MonitoringLimits.NotificationEventPrefix) && x.Status == OutboxStatuses.Pending
                        && (x.NextAttemptUtc == null || x.NextAttemptUtc <= now)
                        && (x.LockedUntilUtc == null || x.LockedUntilUtc <= now))
            .OrderBy(x => x.OccurredUtc)
            .Take(50)
            .Select(x => x.Id)
            .ToListAsync(ct);

        foreach (var id in candidates)
        {
            now = DateTimeOffset.UtcNow;
            var leaseOwner = $"{_lockOwner}:{Guid.NewGuid():N}";
            var lockedUntil = now.Add(_lockDuration);
            var claimed = await db.OutboxMessages
                .Where(x => x.Id == id && x.Status == OutboxStatuses.Pending
                            && (x.NextAttemptUtc == null || x.NextAttemptUtc <= now)
                            && (x.LockedUntilUtc == null || x.LockedUntilUtc <= now))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.LockOwner, leaseOwner)
                    .SetProperty(x => x.LockedUntilUtc, lockedUntil), ct);
            if (claimed == 0) continue;

            // A candidate may have been disabled or claimed elsewhere since selection.
            // Recheck immediately before admitting publication; an already admitted
            // publication may finish after disable, but cannot overwrite that state.
            var dispatchTime = DateTimeOffset.UtcNow;
            var message = await db.OutboxMessages.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == id && x.Status == OutboxStatuses.Pending
                                          && x.LockOwner == leaseOwner && x.LockedUntilUtc > dispatchTime, ct);
            if (message is null) continue;

            var owned = db.OutboxMessages.Where(x => x.Id == id && x.Status == OutboxStatuses.Pending && x.LockOwner == leaseOwner);
            var envelope = MapEnvelope(message);
            try
            {
                await publisher.PublishAsync(envelope, ct);
                await owned.ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, OutboxStatuses.Published)
                    .SetProperty(x => x.LastError, (string?)null)
                    .SetProperty(x => x.LockOwner, (string?)null)
                    .SetProperty(x => x.LockedUntilUtc, (DateTimeOffset?)null), ct);
                NetRatelTelemetry.RecordOutboxEventPublished(envelope);
                _logger.LogInformation(
                    "Published NetRatel outbox event {EventType} from {Source} with severity {Severity}",
                    envelope.Type,
                    envelope.Source,
                    envelope.Severity ?? "Info");
            }
            catch (Exception ex)
            {
                var attempts = message.Attempts + 1;
                var error = ex.ToString();
                var delaySeconds = Math.Min(300, Math.Pow(2, Math.Min(10, attempts)));
                var nextAttempt = DateTimeOffset.UtcNow.AddSeconds(delaySeconds);
                var status = attempts >= 20 ? OutboxStatuses.Failed : OutboxStatuses.Pending;
                var recorded = await owned.ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Attempts, attempts)
                    .SetProperty(x => x.LastError, error)
                    .SetProperty(x => x.NextAttemptUtc, nextAttempt)
                    .SetProperty(x => x.Status, status)
                    .SetProperty(x => x.LockOwner, (string?)null)
                    .SetProperty(x => x.LockedUntilUtc, (DateTimeOffset?)null), ct);
                if (recorded == 0) continue;

                NetRatelTelemetry.RecordOutboxEventFailed(envelope, status);
                _logger.LogWarning(
                    ex,
                    "Failed publishing NetRatel outbox event {EventType} from {Source} on attempt {Attempts}; next status {Status}",
                    envelope.Type,
                    envelope.Source,
                    attempts,
                    status);
            }
        }
    }

    private static OutboxEnvelope MapEnvelope(OutboxMessage message)
        => new(
            message.Id,
            message.OccurredUtc,
            message.Type,
            message.PayloadJson,
            message.Source,
            message.CorrelationId,
            message.TenantId,
            message.EntityId,
            message.Severity,
            message.Message,
            message.Status,
            message.Attempts,
            message.NextAttemptUtc,
            message.LockedUntilUtc,
            message.LockOwner,
            message.LastError);
}
