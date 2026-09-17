using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using NetRatel.API.Realtime.Shadow;
using NetRatel.Application.Fanout;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class SignalRShadowBridgeConcurrencyTests
{
    [Theory]
    [InlineData(2_047, true, 2_048)]
    [InlineData(2_048, false, 2_048)]
    public void Bridge_EnforcesExactDistinctKeyBoundary(
        int admittedBeforeProbe,
        bool expectedProbeResult,
        int expectedDepth)
    {
        var bridge = new SignalRShadowFanoutBridge(new ControlledHubContext(), TimeProvider.System);
        for (var index = 0; index < admittedBeforeProbe; index++)
        {
            bridge.TryEnqueue(Command($"command-{index}")).Should().BeTrue();
        }

        bridge.TryEnqueue(Command("probe")).Should().Be(expectedProbeResult);
        bridge.GetStatus().CurrentDepth.Should().Be(expectedDepth);
    }

    [Fact]
    public void Bridge_EnforcesTelemetryBoundaryAndAllowsCoalescingAtCapacity()
    {
        var bridge = new SignalRShadowFanoutBridge(new ControlledHubContext(), TimeProvider.System);
        for (var index = 0; index < SignalRShadowFanoutBridge.TelemetryKeyCapacity; index++)
        {
            bridge.TryEnqueue(Telemetry($"client-{index}", sequence: 1)).Should().BeTrue();
        }

        bridge.TryEnqueue(Telemetry("overflow", sequence: 1)).Should().BeFalse();
        bridge.TryEnqueue(Telemetry("client-0", sequence: 2)).Should().BeTrue();

        var status = bridge.GetStatus();
        status.CurrentTelemetryKeys.Should().Be(SignalRShadowFanoutBridge.TelemetryKeyCapacity);
        status.CurrentDepth.Should().Be(SignalRShadowFanoutBridge.TelemetryKeyCapacity);
        status.Coalesced.Should().Be(1);
        status.Dropped.Should().Be(1);
    }

    [Fact]
    public void Bridge_ConcurrentSameKeyUpdatesRemainOneBoundedPendingKey()
    {
        var bridge = new SignalRShadowFanoutBridge(new ControlledHubContext(), TimeProvider.System);

        Parallel.For(0, 512, index =>
            bridge.TryEnqueue(Command("same-command", (ulong)index)).Should().BeTrue());

        var status = bridge.GetStatus();
        status.CurrentDepth.Should().Be(1);
        status.Enqueued.Should().Be(1);
        status.Coalesced.Should().Be(511);
    }

    [Fact]
    public void Bridge_ConcurrentDistinctKeyAdmissionNeverExceedsCapacity()
    {
        var bridge = new SignalRShadowFanoutBridge(new ControlledHubContext(), TimeProvider.System);
        var admitted = 0;

        Parallel.For(0, SignalRShadowFanoutBridge.Capacity * 2, index =>
        {
            if (bridge.TryEnqueue(Command($"command-{index}")))
            {
                Interlocked.Increment(ref admitted);
            }
        });

        Volatile.Read(ref admitted).Should().Be(SignalRShadowFanoutBridge.Capacity);
        bridge.GetStatus().Should().Match<SignalRShadowFanoutStatus>(status =>
            status.CurrentDepth == SignalRShadowFanoutBridge.Capacity &&
            status.HighWaterMark == SignalRShadowFanoutBridge.Capacity);
    }

    [Fact]
    public async Task Bridge_OneSlowGroupDoesNotBlockAnotherGroupOrRetry()
    {
        var hub = new ControlledHubContext();
        var slowTarget = CommandTarget("slow");
        SignalRShadowGroupName.TryCreate(slowTarget, out var slowGroup).Should().BeTrue();
        var slowStarted = NewCompletionSource();
        var slowCancelled = NewCompletionSource();
        var slowCompletion = NewCompletionSource();
        var healthyPublished = NewCompletionSource();
        var slowAttempts = 0;

        hub.SendAsync = (publication, cancellationToken) =>
        {
            if (publication.Group == slowGroup)
            {
                Interlocked.Increment(ref slowAttempts);
                slowStarted.TrySetResult();
                cancellationToken.Register(() =>
                {
                    slowCancelled.TrySetResult();
                    slowCompletion.TrySetCanceled(cancellationToken);
                });
                return slowCompletion.Task;
            }

            healthyPublished.TrySetResult();
            return Task.CompletedTask;
        };

        var bridge = new SignalRShadowFanoutBridge(
            hub,
            TimeProvider.System,
            TimeSpan.FromMinutes(1),
            maximumConcurrentPublishes: 2);
        await bridge.StartAsync(CancellationToken.None);
        try
        {
            bridge.TryEnqueue(Command("slow")).Should().BeTrue();
            await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

            bridge.TryEnqueue(Command("healthy")).Should().BeTrue();
            await healthyPublished.Task.WaitAsync(TimeSpan.FromSeconds(3));

            bridge.GetStatus().InFlightHighWaterMark.Should().BeLessThanOrEqualTo(2);
            Volatile.Read(ref slowAttempts).Should().Be(1);
        }
        finally
        {
            await bridge.StopAsync(CancellationToken.None);
        }

        await slowCancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Volatile.Read(ref slowAttempts).Should().Be(1);
    }

    [Fact]
    public async Task Bridge_TimeoutIsWallClockBoundedAndLateCompletionCannotBecomeSuccess()
    {
        var time = new ManualTimeProvider();
        var hub = new ControlledHubContext();
        var sendStarted = NewCompletionSource();
        var sendCancelled = NewCompletionSource();
        var lateCompletion = NewCompletionSource();
        var attempts = 0;
        hub.SendAsync = (_, cancellationToken) =>
        {
            Interlocked.Increment(ref attempts);
            sendStarted.TrySetResult();
            cancellationToken.Register(() => sendCancelled.TrySetResult());
            return lateCompletion.Task;
        };

        var bridge = new SignalRShadowFanoutBridge(
            hub,
            time,
            TimeSpan.FromSeconds(2),
            maximumConcurrentPublishes: 1);
        await bridge.StartAsync(CancellationToken.None);
        try
        {
            bridge.TryEnqueue(Command("late")).Should().BeTrue();
            await sendStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await time.WaitForTimerAsync().WaitAsync(TimeSpan.FromSeconds(3));

            time.Advance(TimeSpan.FromSeconds(2));
            await sendCancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await WaitForAsync(() => bridge.GetStatus().PublishTimeouts == 1);

            bridge.GetStatus().Should().Match<SignalRShadowFanoutStatus>(status =>
                status.PublishTimeouts == 1 && status.Published == 0);

            lateCompletion.TrySetResult();
            bridge.GetStatus().Published.Should().Be(0);
            Volatile.Read(ref attempts).Should().Be(1);
        }
        finally
        {
            await bridge.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bridge_ThrownOrClientCancelledSendFailsOnceWithoutRetry(bool cancelled)
    {
        var hub = new ControlledHubContext();
        SignalRShadowGroupName.TryCreate(CommandTarget("failing"), out var failingGroup).Should().BeTrue();
        var recoveryPublished = NewCompletionSource();
        var attempts = 0;
        hub.SendAsync = (publication, _) =>
        {
            if (publication.Group == failingGroup)
            {
                Interlocked.Increment(ref attempts);
                return cancelled
                    ? Task.FromCanceled(new CancellationToken(canceled: true))
                    : Task.FromException(new InvalidOperationException("controlled send failure"));
            }

            recoveryPublished.TrySetResult();
            return Task.CompletedTask;
        };

        var bridge = new SignalRShadowFanoutBridge(
            hub,
            TimeProvider.System,
            TimeSpan.FromMinutes(1),
            maximumConcurrentPublishes: 1);
        await bridge.StartAsync(CancellationToken.None);
        try
        {
            bridge.TryEnqueue(Command("failing")).Should().BeTrue();
            bridge.TryEnqueue(Command("recovery")).Should().BeTrue();
            await recoveryPublished.Task.WaitAsync(TimeSpan.FromSeconds(3));

            bridge.GetStatus().Should().Match<SignalRShadowFanoutStatus>(status =>
                status.PublishFailures == 1 && status.PublishTimeouts == 0 && status.Published == 1);
            Volatile.Read(ref attempts).Should().Be(1);
        }
        finally
        {
            await bridge.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Bridge_ResyncIsGroupScopedAndSurvivesCoalescing()
    {
        var hub = new ControlledHubContext();
        var failedTarget = CommandTarget("failed");
        var blockerTarget = CommandTarget("blocker");
        SignalRShadowGroupName.TryCreate(failedTarget, out var failedGroup).Should().BeTrue();
        SignalRShadowGroupName.TryCreate(blockerTarget, out var blockerGroup).Should().BeTrue();
        var failedOnce = NewCompletionSource();
        var blockerStarted = NewCompletionSource();
        var releaseBlocker = NewCompletionSource();
        var expectedPublications = new ConcurrentDictionary<string, TaskCompletionSource<Publication>>(
            StringComparer.Ordinal);
        var failedAttempts = 0;

        hub.SendAsync = (publication, _) =>
        {
            if (publication.Group == failedGroup && Interlocked.Increment(ref failedAttempts) == 1)
            {
                failedOnce.TrySetResult();
                throw new InvalidOperationException("controlled send failure");
            }

            if (publication.Group == blockerGroup)
            {
                blockerStarted.TrySetResult();
                return releaseBlocker.Task;
            }

            expectedPublications.GetOrAdd(
                publication.Group,
                static _ => new(TaskCreationOptions.RunContinuationsAsynchronously))
                .TrySetResult(publication);
            return Task.CompletedTask;
        };

        var bridge = new SignalRShadowFanoutBridge(
            hub,
            TimeProvider.System,
            TimeSpan.FromMinutes(1),
            maximumConcurrentPublishes: 1);
        await bridge.StartAsync(CancellationToken.None);
        try
        {
            bridge.TryEnqueue(Command("failed", sequence: 1)).Should().BeTrue();
            await failedOnce.Task.WaitAsync(TimeSpan.FromSeconds(3));

            bridge.TryEnqueue(Command("blocker")).Should().BeTrue();
            await blockerStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

            bridge.TryEnqueue(Command("other", sequence: 1)).Should().BeTrue();
            bridge.TryEnqueue(Command("failed", sequence: 2)).Should().BeTrue();
            bridge.TryEnqueue(Command("failed", sequence: 3)).Should().BeTrue();
            releaseBlocker.TrySetResult();

            SignalRShadowGroupName.TryCreate(CommandTarget("other"), out var otherGroup).Should().BeTrue();
            var other = await PublicationFor(expectedPublications, otherGroup);
            var recovered = await PublicationFor(expectedPublications, failedGroup);

            EnvelopeFrom(other).ResyncRequired.Should().BeFalse();
            EnvelopeFrom(recovered).Should().Match<ShadowFanoutEnvelope>(envelope =>
                envelope.Sequence == 3 && envelope.ResyncRequired);
            Volatile.Read(ref failedAttempts).Should().Be(2);
        }
        finally
        {
            await bridge.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void Bridge_SnapshotIsBoundedLatestArrivalStateAndExplicitlyMarked()
    {
        var bridge = new SignalRShadowFanoutBridge(new ControlledHubContext(), TimeProvider.System);
        var target = CommandTarget("snapshot");

        bridge.TryEnqueue(Command("snapshot", sequence: 9)).Should().BeTrue();
        bridge.TryEnqueue(Command("snapshot", sequence: 4)).Should().BeTrue();

        bridge.GetSnapshots(target).Should().ContainSingle()
            .Which.Should().Match<ShadowFanoutEnvelope>(envelope =>
                envelope.EventType == ShadowFanoutEventType.Snapshot &&
                envelope.Sequence == 4 &&
                !envelope.IsAuthoritative);
    }

    private static async Task<Publication> PublicationFor(
        ConcurrentDictionary<string, TaskCompletionSource<Publication>> publications,
        string group)
    {
        var completion = publications.GetOrAdd(
            group,
            static _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
        return await completion.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static ShadowFanoutEnvelope EnvelopeFrom(Publication publication) =>
        publication.Arguments.Should().ContainSingle()
            .Which.Should().BeOfType<ShadowFanoutEnvelope>().Subject;

    private static ShadowFanoutEnvelope Command(string commandId, ulong? sequence = null) =>
        new(
            ShadowFanoutEnvelope.CurrentSchemaVersion,
            ShadowFanoutCategory.Command,
            CommandTarget(commandId),
            ShadowFanoutEventType.Updated,
            ShadowFanoutStatus.Active,
            DateTimeOffset.Parse("2026-08-07T10:00:00Z"),
            sequence);

    private static ShadowFanoutEnvelope Telemetry(string clientId, ulong? sequence) =>
        new(
            ShadowFanoutEnvelope.CurrentSchemaVersion,
            ShadowFanoutCategory.Telemetry,
            new ShadowFanoutTarget(7, ShadowFanoutTargetScope.Client, ClientId: clientId),
            ShadowFanoutEventType.Updated,
            ShadowFanoutStatus.Active,
            DateTimeOffset.Parse("2026-08-07T10:00:00Z"),
            sequence);

    private static ShadowFanoutTarget CommandTarget(string commandId) =>
        new(7, ShadowFanoutTargetScope.Command, CommandId: commandId);

    private static TaskCompletionSource NewCompletionSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record Publication(string Group, string Method, object?[] Arguments);

    private sealed class ControlledHubContext : IHubContext<AkkaShadowHub>
    {
        public Func<Publication, CancellationToken, Task> SendAsync { get; set; } =
            static (_, _) => Task.CompletedTask;

        public IHubClients Clients => new ControlledHubClients(this);

        public IGroupManager Groups { get; } = new NoOpGroupManager();

        private sealed class ControlledHubClients(ControlledHubContext owner) : IHubClients
        {
            public IClientProxy All => Group("all");

            public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Group("all-except");

            public IClientProxy Client(string connectionId) => Group("client");

            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Group("clients");

            public IClientProxy Group(string groupName) => new ControlledClientProxy(owner, groupName);

            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) =>
                Group(groupName);

            public IClientProxy Groups(IReadOnlyList<string> groupNames) => Group("groups");

            public IClientProxy User(string userId) => Group("user");

            public IClientProxy Users(IReadOnlyList<string> userIds) => Group("users");
        }

        private sealed class ControlledClientProxy(ControlledHubContext owner, string group) : IClientProxy
        {
            public Task SendCoreAsync(
                string method,
                object?[] args,
                CancellationToken cancellationToken = default) =>
                owner.SendAsync(new Publication(group, method, args), cancellationToken);
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

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Yield();
        }

        condition().Should().BeTrue("the asynchronous bridge operation should complete");
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private readonly TaskCompletionSource<bool> _timerCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private DateTimeOffset _utcNow = DateTimeOffset.Parse("2026-08-07T10:00:00Z");

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _utcNow;
            }
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime, period);
            lock (_gate)
            {
                _timers.Add(timer);
            }

            _timerCreated.TrySetResult(true);

            return timer;
        }

        public Task WaitForTimerAsync() => _timerCreated.Task;

        public void Advance(TimeSpan amount)
        {
            List<(TimerCallback Callback, object? State)> callbacks = [];
            lock (_gate)
            {
                _utcNow += amount;
                foreach (var timer in _timers.ToArray())
                {
                    if (timer.TryFire(_utcNow, out var callback))
                    {
                        callbacks.Add(callback);
                    }
                }
            }

            foreach (var (callback, state) in callbacks)
            {
                callback(state);
            }
        }

        private void Remove(ManualTimer timer)
        {
            lock (_gate)
            {
                _timers.Remove(timer);
            }
        }

        private sealed class ManualTimer : ITimer
        {
            private readonly ManualTimeProvider _owner;
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private DateTimeOffset _next;
            private TimeSpan _period;
            private bool _disposed;

            public ManualTimer(
                ManualTimeProvider owner,
                TimerCallback callback,
                object? state,
                TimeSpan dueTime,
                TimeSpan period)
            {
                _owner = owner;
                _callback = callback;
                _state = state;
                _period = period;
                _next = owner.GetUtcNow() + dueTime;
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed)
                {
                    return false;
                }

                _period = period;
                _next = _owner.GetUtcNow() + dueTime;
                return true;
            }

            public void Dispose()
            {
                _disposed = true;
                _owner.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public bool TryFire(
                DateTimeOffset now,
                out (TimerCallback Callback, object? State) callback)
            {
                if (_disposed || now < _next)
                {
                    callback = default;
                    return false;
                }

                callback = (_callback, _state);
                if (_period == Timeout.InfiniteTimeSpan)
                {
                    _disposed = true;
                }
                else
                {
                    _next = now + _period;
                }

                return true;
            }
        }
    }
}
