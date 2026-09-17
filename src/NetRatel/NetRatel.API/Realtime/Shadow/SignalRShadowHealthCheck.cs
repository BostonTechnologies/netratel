using Microsoft.Extensions.Diagnostics.HealthChecks;
using NetRatel.Akka.Configuration;

namespace NetRatel.API.Realtime.Shadow;

public sealed class SignalRShadowHealthCheck(
    IShadowFanoutSink fanout,
    SignalRShadowSubscriptionRegistry subscriptions,
    NetRatelAkkaMigrationOptions options,
    IHostEnvironment environment,
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
            ["enabled"] = options.SignalRShadowEnabled,
            ["localCanaryEnabled"] = options.SignalRShadowLocalCanaryEnabled,
            ["developmentEnvironment"] = environment.IsDevelopment(),
            ["workerRunning"] = fanoutStatus.WorkerRunning,
            ["queueDepth"] = fanoutStatus.CurrentDepth,
            ["queueCapacity"] = SignalRShadowFanoutBridge.Capacity,
            ["highWaterMark"] = fanoutStatus.HighWaterMark,
            ["telemetryKeys"] = fanoutStatus.CurrentTelemetryKeys,
            ["telemetryCapacity"] = SignalRShadowFanoutBridge.TelemetryKeyCapacity,
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
            ["unauthorizedSubscriptions"] = subscriptionStatus.UnauthorizedSubscriptions,
            ["authority"] = options.IsSignalRAuthorityActive ? "akka" : "unavailable",
            ["uiSource"] = options.IsSignalRAuthorityActive ? "akka-authority-projections" : "existing-production-sources"
        };

        if (fanoutStatus.LastPublishedAtUtc is { } publishedAt)
        {
            data["lastPublishedAtUtc"] = publishedAt;
        }

        var configurationValid = options.Enabled
            ? options.SignalRShadowEnabled == options.SignalRShadowLocalCanaryEnabled
            : !options.SignalRShadowEnabled && !options.SignalRShadowLocalCanaryEnabled;
        if (!configurationValid)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "SignalR shadow fanout configuration is partial or contradictory.",
                data: data));
        }

        if (!options.Enabled || !options.SignalRShadowEnabled || !environment.IsDevelopment())
        {
            return Task.FromResult(HealthCheckResult.Healthy(
                "SignalR shadow fanout is disabled for this environment.",
                data));
        }

        if (!fanoutStatus.WorkerRunning)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "SignalR shadow fanout worker is not running.",
                data: data));
        }

        var now = timeProvider.GetUtcNow();
        var recentlyFailed = fanoutStatus.LastFailureAtUtc is { } failureAt &&
            now - failureAt <= RecentFailureWindow;
        var recentlyDropped = fanoutStatus.LastDroppedAtUtc is { } droppedAt &&
            now - droppedAt <= RecentFailureWindow;
        var queuePressure = fanoutStatus.CurrentDepth >= SignalRShadowFanoutBridge.Capacity * 3 / 4;
        var telemetryPressure =
            fanoutStatus.CurrentTelemetryKeys >= SignalRShadowFanoutBridge.TelemetryKeyCapacity * 4 / 5;
        var stalePending = fanoutStatus.OldestPendingAge > MaximumHealthyPendingAge;
        var staleInFlight = fanoutStatus.OldestInFlightAge > SignalRShadowFanoutBridge.PublishTimeout;
        var subscriptionPressure =
            subscriptionStatus.ActiveConnections >= SignalRShadowSubscriptionRegistry.MaximumConnections * 4 / 5 ||
            subscriptionStatus.ActiveGroups >= SignalRShadowSubscriptionRegistry.MaximumGroups * 4 / 5 ||
            subscriptionStatus.ActiveMemberships >= SignalRShadowSubscriptionRegistry.MaximumMemberships * 4 / 5;

        if (recentlyFailed || recentlyDropped || queuePressure || telemetryPressure ||
            stalePending || staleInFlight || subscriptionPressure)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                "SignalR shadow fanout is running with recent delivery loss or bounded-capacity pressure.",
                data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            options.IsSignalRAuthorityActive
                ? "SignalR authority fanout is healthy for the DEV Akka canary."
                : "SignalR shadow fanout local canary is healthy and non-authoritative.",
            data));
    }
}
