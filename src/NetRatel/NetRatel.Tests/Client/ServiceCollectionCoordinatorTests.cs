using AwesomeAssertions;
using NetRatel.Client.Service.Services;
using NetRatel.Shared.Contracts.Services;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class ServiceCollectionCoordinatorTests
{
    [Fact]
    public async Task InventoryAndWatch_ShareOneOutstandingCollection_AndWatchUsesOnlyServerSelection()
    {
        var time = new ManualTimeProvider();
        var collector = new RecordingCollector(time);
        using var coordinator = new ServiceCollectionCoordinator(collector, time);
        coordinator.BeginStream();
        coordinator.ApplyPolicy(Policy(time, 1, ["chosen.service"])).Should().BeTrue();
        coordinator.StartDueCollection(CancellationToken.None);
        coordinator.StartDueCollection(CancellationToken.None);
        collector.InventoryCalls.Should().Be(1);
        collector.WatchCalls.Should().Be(0);
        collector.Complete(ServiceSnapshotKind.Inventory);
        await coordinator.PendingCollection!;
        coordinator.TryTakeCompleted(out var inventory).Should().BeTrue();
        inventory!.Kind.Should().Be(ServiceSnapshotKind.Inventory);

        coordinator.StartDueCollection(CancellationToken.None);
        collector.WatchCalls.Should().Be(1);
        collector.LastSelection.Should().Equal("chosen.service");
        collector.LastRevision.Should().Be(1);
        collector.Complete(ServiceSnapshotKind.Watch, 1);
        await coordinator.PendingCollection!;
        coordinator.TryTakeCompleted(out _);
        coordinator.StartDueCollection(CancellationToken.None);
        collector.WatchCalls.Should().Be(1);
        time.Advance(TimeSpan.FromSeconds(34));
        coordinator.StartDueCollection(CancellationToken.None);
        collector.WatchCalls.Should().Be(2);
    }

    [Fact]
    public async Task RefreshIsCoalesced_AndExpiresSelectionWithoutDisablingSlowInventory()
    {
        var time = new ManualTimeProvider();
        var collector = new RecordingCollector(time);
        using var coordinator = new ServiceCollectionCoordinator(collector, time);
        coordinator.BeginStream();
        coordinator.StartDueCollection(CancellationToken.None);
        collector.Complete(ServiceSnapshotKind.Inventory);
        await coordinator.PendingCollection!;
        coordinator.TryTakeCompleted(out _);
        coordinator.ApplyPolicy(Policy(time, 1, [], Guid.NewGuid())).Should().BeTrue();
        coordinator.StartDueCollection(CancellationToken.None);
        collector.InventoryCalls.Should().Be(1);
        time.Advance(TimeSpan.FromSeconds(15));
        coordinator.StartDueCollection(CancellationToken.None);
        collector.InventoryCalls.Should().Be(2);
        collector.Complete(ServiceSnapshotKind.Inventory);
        await coordinator.PendingCollection!;
        coordinator.TryTakeCompleted(out _);
        coordinator.ApplyPolicy(Policy(time, 2, ["selected.service"]) with { ExpiresAtUtc = time.GetUtcNow().AddSeconds(1) }).Should().BeTrue();
        time.Advance(TimeSpan.FromSeconds(2));
        coordinator.StartDueCollection(CancellationToken.None);
        collector.WatchCalls.Should().Be(0);
        time.Advance(TimeSpan.FromMinutes(16));
        coordinator.StartDueCollection(CancellationToken.None);
        collector.InventoryCalls.Should().Be(3);
    }

    [Fact]
    public async Task SameRevisionRefresh_DoesNotChangeSelection_AndRejectsConflictingOrReplayedPolicy()
    {
        var time = new ManualTimeProvider();
        var collector = new RecordingCollector(time);
        using var coordinator = new ServiceCollectionCoordinator(collector, time);
        coordinator.BeginStream();
        var initial = Policy(time, 4, []);
        coordinator.ApplyPolicy(initial).Should().BeTrue();
        coordinator.StartDueCollection(CancellationToken.None);
        collector.Complete(ServiceSnapshotKind.Inventory);
        await coordinator.PendingCollection!;
        coordinator.TryTakeCompleted(out _);
        var refresh = initial with { RefreshRequestId = Guid.NewGuid() };
        coordinator.ApplyPolicy(refresh).Should().BeTrue();
        coordinator.ApplyPolicy(refresh).Should().BeFalse();
        coordinator.ApplyPolicy(refresh with { ServiceNames = ["changed.service"], RefreshRequestId = Guid.NewGuid() }).Should().BeFalse();
        coordinator.ApplyPolicy(refresh with { WatchIntervalSeconds = 60, RefreshRequestId = Guid.NewGuid() }).Should().BeFalse();
        coordinator.ApplyPolicy(refresh with { Revision = 3, RefreshRequestId = Guid.NewGuid() }).Should().BeFalse();
        coordinator.ApplyPolicy(refresh with { ExpiresAtUtc = refresh.ExpiresAtUtc.AddMinutes(1) }).Should().BeTrue();
        time.Advance(TimeSpan.FromSeconds(15));
        coordinator.StartDueCollection(CancellationToken.None);
        collector.InventoryCalls.Should().Be(2);
    }

    [Fact]
    public async Task ReconnectCancelsCollection_AndFencesLateCompletedInventory()
    {
        var time = new ManualTimeProvider();
        var collector = new RecordingCollector(time);
        using var coordinator = new ServiceCollectionCoordinator(collector, time);
        coordinator.BeginStream();
        coordinator.StartDueCollection(CancellationToken.None);
        coordinator.BeginStream();
        collector.LastCancellation.IsCancellationRequested.Should().BeTrue();
        coordinator.StartDueCollection(CancellationToken.None);
        collector.InventoryCalls.Should().Be(1);
        collector.Complete(ServiceSnapshotKind.Inventory);
        await coordinator.PendingCollection!;
        coordinator.TryTakeCompleted(out var stale).Should().BeTrue();
        stale.Should().BeNull();
        coordinator.StartDueCollection(CancellationToken.None);
        collector.InventoryCalls.Should().Be(2);
    }

    [Fact]
    public async Task NewPolicyFencesLateWatch_AndRejectsOldOrInvalidSelection()
    {
        var time = new ManualTimeProvider();
        var collector = new RecordingCollector(time);
        using var coordinator = new ServiceCollectionCoordinator(collector, time);
        coordinator.BeginStream();
        coordinator.ApplyPolicy(Policy(time, 1, ["old.service"]));
        coordinator.StartDueCollection(CancellationToken.None);
        collector.Complete(ServiceSnapshotKind.Inventory);
        await coordinator.PendingCollection!;
        coordinator.TryTakeCompleted(out _);
        coordinator.StartDueCollection(CancellationToken.None);
        coordinator.ApplyPolicy(Policy(time, 2, ["new.service"])).Should().BeTrue();
        collector.Complete(ServiceSnapshotKind.Watch, 1);
        await coordinator.PendingCollection!;
        coordinator.TryTakeCompleted(out var stale);
        stale.Should().BeNull();
        coordinator.ApplyPolicy(Policy(time, 1, ["old.service"])).Should().BeFalse();
        coordinator.ApplyPolicy(Policy(time, 3, Enumerable.Repeat("invalid.service", 65).ToArray())).Should().BeFalse();
    }

    private static ClientServiceWatchPolicyDto Policy(TimeProvider time, ulong revision, IReadOnlyList<string> names, Guid? refresh = null) =>
        new(revision, names, 30, 900, time.GetUtcNow().AddMinutes(30), refresh);

    private sealed class RecordingCollector(TimeProvider time) : IServiceInventoryCollector
    {
        private TaskCompletionSource<ServiceCollectionResult>? _pending;
        public int InventoryCalls { get; private set; }
        public int WatchCalls { get; private set; }
        public IReadOnlyList<string> LastSelection { get; private set; } = [];
        public ulong LastRevision { get; private set; }
        public CancellationToken LastCancellation { get; private set; }
        public Task<ServiceCollectionResult> CollectInventoryAsync(CancellationToken cancellationToken)
        {
            InventoryCalls++;
            return Begin(cancellationToken);
        }
        public Task<ServiceCollectionResult> CollectWatchAsync(IReadOnlyList<string> names, ulong policyRevision, CancellationToken cancellationToken)
        {
            WatchCalls++;
            LastSelection = names;
            LastRevision = policyRevision;
            return Begin(cancellationToken);
        }
        private Task<ServiceCollectionResult> Begin(CancellationToken cancellationToken)
        {
            LastCancellation = cancellationToken;
            _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return _pending.Task;
        }
        public void Complete(ServiceSnapshotKind kind, ulong revision = 0) =>
            _pending!.TrySetResult(new(Guid.NewGuid(), kind, ServiceCollectionStatus.Complete, time.GetUtcNow(), [], null, revision));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }
}
