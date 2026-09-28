using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace NetRatel.API.Realtime;

public sealed class SignalRFanoutHealthCheck(
    IRealtimeFanoutSink fanout,
    RealtimeSubscriptionRegistry subscriptions,
    TimeProvider timeProvider) : IHealthCheck
{
    private static readonly TimeSpan RecentFailureWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaximumHealthyPendingAge = TimeSpan.FromSeconds(2);

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var fanoutStatus = fanout.GetStatus();
        var subscriptionStatus = subscriptions.GetStatus();
        var data = new Dictionary<string, object>
        {
            ["workerRunning"] = fanoutStatus.WorkerRunning,
            ["queueDepth"] = fanoutStatus.CurrentDepth,
            ["queueCapacity"] = RealtimeFanoutBridge.Capacity,
            ["highWaterMark"] = fanoutStatus.HighWaterMark,
            ["telemetryKeys"] = fanoutStatus.CurrentTelemetryKeys,
            ["telemetryCapacity"] = RealtimeFanoutBridge.TelemetryKeyCapacity,
            ["inFlightPublishes"] = fanoutStatus.InFlightPublishes,
            ["inFlightHighWaterMark"] = fanoutStatus.InFlightHighWaterMark,
            ["published"] = fanoutStatus.Published,
            ["dropped"] = fanoutStatus.Dropped,
            ["coalesced"] = fanoutStatus.Coalesced,
            ["rejectedEvents"] = fanoutStatus.Rejected,
            ["publishFailures"] = fanoutStatus.PublishFailures,
            ["publishTimeouts"] = fanoutStatus.PublishTimeouts,
            ["activeConnections"] = subscriptionStatus.ActiveConnections,
            ["activeGroups"] = subscriptionStatus.ActiveGroups,
            ["activeMemberships"] = subscriptionStatus.ActiveMemberships,
            ["rejectedSubscriptions"] = subscriptionStatus.RejectedSubscriptions,
            ["unauthorizedSubscriptions"] = subscriptionStatus.UnauthorizedSubscriptions
        };

        if (fanoutStatus.LastPublishedAtUtc is { } publishedAt)
        {
            data["lastPublishedAtUtc"] = publishedAt;
        }

        if (!fanoutStatus.WorkerRunning)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "SignalR fanout worker is not running.",
                data: data));
        }

        var now = timeProvider.GetUtcNow();
        var recentlyFailed = fanoutStatus.LastFailureAtUtc is { } failureAt &&
            now - failureAt <= RecentFailureWindow;
        var recentlyDropped = fanoutStatus.LastDroppedAtUtc is { } droppedAt &&
            now - droppedAt <= RecentFailureWindow;
        var queuePressure = fanoutStatus.CurrentDepth >= RealtimeFanoutBridge.Capacity * 3 / 4;
        var telemetryPressure =
            fanoutStatus.CurrentTelemetryKeys >= RealtimeFanoutBridge.TelemetryKeyCapacity * 4 / 5;
        var stalePending = fanoutStatus.OldestPendingAge > MaximumHealthyPendingAge;
        var staleInFlight = fanoutStatus.OldestInFlightAge > RealtimeFanoutBridge.PublishTimeout;
        var subscriptionPressure =
            subscriptionStatus.ActiveConnections >= RealtimeSubscriptionRegistry.MaximumConnections * 4 / 5 ||
            subscriptionStatus.ActiveGroups >= RealtimeSubscriptionRegistry.MaximumGroups * 4 / 5 ||
            subscriptionStatus.ActiveMemberships >= RealtimeSubscriptionRegistry.MaximumMemberships * 4 / 5;

        if (recentlyFailed || recentlyDropped || queuePressure || telemetryPressure ||
            stalePending || staleInFlight || subscriptionPressure)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                "SignalR fanout is running with recent delivery loss or bounded-capacity pressure.",
                data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy("SignalR fanout is healthy.", data));
    }
}
