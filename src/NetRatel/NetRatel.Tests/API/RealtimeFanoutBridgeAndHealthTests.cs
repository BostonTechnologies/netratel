using AwesomeAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NetRatel.API.Realtime;
using NetRatel.Application.Fanout;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class RealtimeFanoutBridgeAndHealthTests
{
    [Fact]
    public void Bridge_CoalescesLatestStateForTheSameCategoryAndGroup()
    {
        var bridge = new RealtimeFanoutBridge(new RecordingHubContext(), TimeProvider.System);

        bridge.TryEnqueue(Envelope(sequence: 1)).Should().BeTrue();
        bridge.TryEnqueue(Envelope(sequence: 2)).Should().BeTrue();

        var status = bridge.GetStatus();
        status.Enqueued.Should().Be(1);
        status.Coalesced.Should().Be(1);
        status.CurrentDepth.Should().Be(1);
        status.Dropped.Should().Be(0);
    }

    [Fact]
    public void Bridge_DropsNewestDistinctKeyAtTheFixedCapacity()
    {
        var bridge = new RealtimeFanoutBridge(new RecordingHubContext(), TimeProvider.System);
        for (var index = 0; index < RealtimeFanoutBridge.Capacity; index++)
        {
            bridge.TryEnqueue(Envelope(commandId: $"command-{index}"))
                .Should().BeTrue();
        }

        bridge.TryEnqueue(Envelope(commandId: "overflow")).Should().BeFalse();

        var status = bridge.GetStatus();
        status.CurrentDepth.Should().Be(RealtimeFanoutBridge.Capacity);
        status.HighWaterMark.Should().Be(RealtimeFanoutBridge.Capacity);
        status.Dropped.Should().Be(1);
    }

    [Fact]
    public async Task Bridge_PublishesOneSafeEnvelopeToOneDerivedGroup()
    {
        var hub = new RecordingHubContext();
        var bridge = new RealtimeFanoutBridge(hub, TimeProvider.System);
        await bridge.StartAsync(CancellationToken.None);
        try
        {
            bridge.TryEnqueue(Envelope(sequence: 9)).Should().BeTrue();
            var publication = await hub.Proxy.Publication.Task.WaitAsync(TimeSpan.FromSeconds(3));

            publication.Method.Should().Be(RealtimeFanoutBridge.ClientMethod);
            publication.Group.Should().StartWith("akka-shadow:v1:tenant:");
            publication.Group.Should().Contain(":command:");
            publication.Group.Should().NotContain(":7:");
            publication.Group.Should().NotContain("command-a");
            publication.Arguments.Should().ContainSingle()
                .Which.Should().BeOfType<RealtimeFanoutEnvelope>()
                .Which.Should().Match<RealtimeFanoutEnvelope>(envelope =>
                    envelope.Sequence == 9 && envelope.IsValid && !envelope.IsAuthoritative);
            for (var attempt = 0; attempt < 300 && bridge.GetStatus().Published == 0; attempt++)
            {
                await Task.Delay(10);
            }
            bridge.GetStatus().Published.Should().Be(1);
        }
        finally
        {
            await bridge.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task HealthCheck_ReportsOnlyBoundedScalarOperationalDiagnostics()
    {
        var fanout = new StubFanoutSink(new RealtimeFanoutHealthStatus(
            WorkerRunning: true,
            Enqueued: 12,
            Coalesced: 3,
            Dropped: 0,
            Rejected: 1,
            Published: 8,
            PublishFailures: 0,
            PublishTimeouts: 0,
            CurrentDepth: 1,
            HighWaterMark: 4,
            OldestPendingAge: TimeSpan.FromMilliseconds(20),
            LastEnqueuedAtUtc: DateTimeOffset.UtcNow,
            LastPublishedAtUtc: DateTimeOffset.UtcNow,
            LastDroppedAtUtc: null,
            LastFailureAtUtc: null,
            CurrentTelemetryKeys: 0,
            InFlightPublishes: 0,
            InFlightHighWaterMark: 1,
            OldestInFlightAge: null));
        var registry = new RealtimeSubscriptionRegistry(TimeProvider.System);
        var health = new SignalRFanoutHealthCheck(
            fanout,
            registry,
            TimeProvider.System);

        var result = await health.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["published"].Should().Be(8UL);
        result.Data.Keys.Should().NotContain(key =>
            key.Contains("payload", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("content", StringComparison.OrdinalIgnoreCase) ||
            key.EndsWith("Id", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HealthCheck_ReportsAnUnstartedFanoutWorkerAsUnhealthy()
    {
        var health = CreateHealth(Status(workerRunning: false));

        var result = await health.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("not running");
    }

    [Fact]
    public async Task HealthCheck_DegradesForTelemetryPressureAndStuckPublication()
    {
        var telemetryPressure = CreateHealth(
            Status(
                workerRunning: true,
                currentTelemetryKeys: RealtimeFanoutBridge.TelemetryKeyCapacity * 4 / 5));
        (await telemetryPressure.CheckHealthAsync(new HealthCheckContext())).Status
            .Should().Be(HealthStatus.Degraded);

        var stuck = CreateHealth(
            Status(
                workerRunning: true,
                inFlightPublishes: 1,
                oldestInFlightAge: RealtimeFanoutBridge.PublishTimeout + TimeSpan.FromMilliseconds(1)));
        (await stuck.CheckHealthAsync(new HealthCheckContext())).Status
            .Should().Be(HealthStatus.Degraded);
    }

    private static SignalRFanoutHealthCheck CreateHealth(RealtimeFanoutHealthStatus status) =>
        new(
            new StubFanoutSink(status),
            new RealtimeSubscriptionRegistry(TimeProvider.System),
            TimeProvider.System);

    private static RealtimeFanoutHealthStatus Status(
        bool workerRunning,
        int currentTelemetryKeys = 0,
        int inFlightPublishes = 0,
        TimeSpan? oldestInFlightAge = null) =>
        new(
            WorkerRunning: workerRunning,
            Enqueued: 0,
            Coalesced: 0,
            Dropped: 0,
            Rejected: 0,
            Published: 0,
            PublishFailures: 0,
            PublishTimeouts: 0,
            CurrentDepth: inFlightPublishes,
            HighWaterMark: inFlightPublishes,
            OldestPendingAge: null,
            LastEnqueuedAtUtc: null,
            LastPublishedAtUtc: null,
            LastDroppedAtUtc: null,
            LastFailureAtUtc: null,
            CurrentTelemetryKeys: currentTelemetryKeys,
            InFlightPublishes: inFlightPublishes,
            InFlightHighWaterMark: inFlightPublishes,
            OldestInFlightAge: oldestInFlightAge);

    private static RealtimeFanoutEnvelope Envelope(string commandId = "command-a", ulong? sequence = null) =>
        new(
            RealtimeFanoutEnvelope.CurrentSchemaVersion,
            RealtimeFanoutCategory.Command,
            new RealtimeFanoutTarget(7, RealtimeFanoutTargetScope.Command, CommandId: commandId),
            RealtimeFanoutEventType.Updated,
            RealtimeFanoutStatus.Active,
            DateTimeOffset.UtcNow,
            sequence);

    private sealed record Publication(string Group, string Method, object?[] Arguments);

    private sealed class RecordingHubContext : IHubContext<RealtimeHub>
    {
        public RecordingHubClients Proxy { get; } = new();

        public IHubClients Clients => Proxy;

        public IGroupManager Groups { get; } = new NoOpGroupManager();
    }

    private sealed class RecordingHubClients : IHubClients
    {
        public TaskCompletionSource<Publication> Publication { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IClientProxy All => Group("all");

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Group("all-except");

        public IClientProxy Client(string connectionId) => Group("client");

        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Group("clients");

        public IClientProxy Group(string groupName) => new RecordingClientProxy(groupName, Publication);

        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) =>
            Group(groupName);

        public IClientProxy Groups(IReadOnlyList<string> groupNames) => Group("groups");

        public IClientProxy User(string userId) => Group("user");

        public IClientProxy Users(IReadOnlyList<string> userIds) => Group("users");
    }

    private sealed class RecordingClientProxy(
        string group,
        TaskCompletionSource<Publication> publication) : IClientProxy
    {
        public Task SendCoreAsync(
            string method,
            object?[] args,
            CancellationToken cancellationToken = default)
        {
            publication.TrySetResult(new Publication(group, method, args));
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(
            string connectionId,
            string groupName,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveFromGroupAsync(
            string connectionId,
            string groupName,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubFanoutSink(RealtimeFanoutHealthStatus status) : IRealtimeFanoutSink
    {
        public bool TryEnqueue(RealtimeFanoutEnvelope envelope) => false;

        public RealtimeFanoutHealthStatus GetStatus() => status;
    }

}
