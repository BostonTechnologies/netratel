using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetRatel.Application.Events;
using NetRatel.Application.Notifications;
using NetRatel.Application.Observability;
using NetRatel.Infrastructure.Persistence;

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

    private async Task ProcessBatchAsync(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();

        var now = DateTimeOffset.UtcNow;

        var batch = await db.OutboxMessages
            .Where(x => x.Status == OutboxStatuses.Pending
                        && (x.NextAttemptUtc == null || x.NextAttemptUtc <= now)
                        && (x.LockedUntilUtc == null || x.LockedUntilUtc <= now))
            .OrderBy(x => x.OccurredUtc)
            .Take(50)
            .ToListAsync(ct);

        foreach (var message in batch)
        {
            message.LockOwner = _lockOwner;
            message.LockedUntilUtc = now.Add(_lockDuration);
        }

        if (batch.Count > 0)
            await db.SaveChangesAsync(ct);

        foreach (var message in batch)
        {
            var envelope = MapEnvelope(message);
            try
            {
                await publisher.PublishAsync(envelope, ct);
                message.Status = OutboxStatuses.Published;
                message.LastError = null;
                message.LockOwner = null;
                message.LockedUntilUtc = null;
                NetRatelTelemetry.RecordOutboxEventPublished(envelope);
                _logger.LogInformation(
                    "Published NetRatel outbox event {EventType} from {Source} with severity {Severity}",
                    envelope.Type,
                    envelope.Source,
                    envelope.Severity ?? "Info");
            }
            catch (Exception ex)
            {
                message.Attempts += 1;
                message.LastError = ex.ToString();
                message.LockOwner = null;
                message.LockedUntilUtc = null;
                var delaySeconds = Math.Min(300, Math.Pow(2, Math.Min(10, message.Attempts)));
                message.NextAttemptUtc = DateTimeOffset.UtcNow.AddSeconds(delaySeconds);
                if (message.Attempts >= 20)
                    message.Status = OutboxStatuses.Failed;

                NetRatelTelemetry.RecordOutboxEventFailed(envelope, message.Status);
                _logger.LogWarning(
                    ex,
                    "Failed publishing NetRatel outbox event {EventType} from {Source} on attempt {Attempts}; next status {Status}",
                    envelope.Type,
                    envelope.Source,
                    message.Attempts,
                    message.Status);
            }
        }

        if (batch.Count > 0)
            await db.SaveChangesAsync(ct);
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
