using System.Threading.Channels;
using FluentAssertions;
using Grpc.Core;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Application.ClientAuth;
using NetRatel.Client.Service.Gateway;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class GatewayPresenceReconnectResourceTests
{
    [Fact]
    public async Task RepeatedHeartbeatResets_JoinEveryRetiredOwnerAndBoundLiveResources()
    {
        const int reconnects = 30;
        var clock = new GatewayPresenceTestClock();
        var transport = new ResettingTransport();
        using var stopping = new CancellationTokenSource();
        var agent = new AgentGatewayPresenceClient(
            new GatewayClientOptions { Endpoint = "https://gateway.test", PresenceBootstrapTimeoutSeconds = 3 },
            new ClockTokenService(clock), 7, Guid.NewGuid(), "test", [], _ => { },
            runForPresenceSession: transport.RunChildOwnerAsync,
            timeProvider: clock, nextRandom: () => 0.5, createCall: transport.Open);
        var run = agent.RunAsync(stopping.Token);
        var peakTimers = 0;
        try
        {
            for (var iteration = 0; iteration <= reconnects; iteration++)
            {
                var current = await transport.Owners.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                // Sample after the validated initial heartbeat and completed I/O,
                // with only the interval/renewal wait belonging to this live call.
                await WaitUntilAsync(() => current.AllIoCompleted && clock.HasTimer(TimeSpan.FromSeconds(5)));
                peakTimers = Math.Max(peakTimers, clock.ActiveTimerCount);
                clock.ActiveTimerCount.Should().BeLessThanOrEqualTo(4,
                    "timer handles must stay bounded across repeated reconnects");
                transport.LiveCalls.Should().Be(1);
                transport.LiveChildren.Should().Be(1);
                transport.AllSuccessorsSawQuiescentPredecessors.Should().BeTrue();
                current.HeartbeatWrites.Should().Be(1);
                current.ChildToken.IsCancellationRequested.Should().BeFalse();
                transport.Calls.Take(iteration).Should().OnlyContain(call => call.IsRetired,
                    "no prior physical read, write or child task may survive replacement");
                if (iteration == reconnects) break;

                // The second heartbeat receives a physical stream reset. It
                // exercises a pending reader, the owner's teardown and one retry.
                clock.Advance(TimeSpan.FromSeconds(5));
                await WaitUntilAsync(() => current.HeartbeatWrites == 2 && current.ActiveReads == 1);
                peakTimers = Math.Max(peakTimers, clock.ActiveTimerCount);
                clock.ActiveTimerCount.Should().BeLessThanOrEqualTo(4);
                current.ResetPendingRead();
                var retryDelay = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, Math.Min(iteration, 5)), 30));
                await WaitUntilAsync(() => current.Disposed && current.AllIoCompleted &&
                    current.ChildCancellationObserved && clock.HasTimer(TimeSpan.FromSeconds(5)));
                current.ActiveReads.Should().Be(0);
                current.ActiveWrites.Should().Be(0);
                current.ChildToken.IsCancellationRequested.Should().BeTrue();
                current.ChildTask!.IsCompleted.Should().BeFalse(
                    "cancelling the child owner must still await its asynchronous cleanup");
                transport.Calls.Should().HaveCount(iteration + 1,
                    "a successor cannot start while its predecessor's child cleanup is pending");
                transport.LiveCalls.Should().Be(0);
                transport.LiveChildren.Should().Be(1);
                clock.ActiveTimerCount.Should().Be(1, "only the bounded child join may remain");
                clock.HasTimer(retryDelay).Should().BeFalse(
                    "recovery cannot begin before the retired child has joined");
                current.FinishChildCleanup();
                await WaitUntilAsync(() => current.IsRetired && clock.HasTimer(retryDelay));
                clock.ActiveTimerCount.Should().Be(1,
                    "only the recovery delay may remain after its old owner has joined");
                transport.LiveCalls.Should().Be(0);
                transport.LiveChildren.Should().Be(0);
                current.PhysicalToken.IsCancellationRequested.Should().BeTrue();
                current.ChildToken.IsCancellationRequested.Should().BeTrue();
                current.ActiveReads.Should().Be(0);
                current.ActiveWrites.Should().Be(0);
                current.ChildTask!.IsCompleted.Should().BeTrue();
                clock.Advance(retryDelay);
            }
        }
        finally
        {
            stopping.Cancel();
            foreach (var call in transport.Calls) call.FinishChildCleanup();
            await run.WaitAsync(TimeSpan.FromSeconds(3));
        }

        await WaitUntilAsync(() => transport.Calls.All(call => call.IsRetired));
        transport.Calls.Should().HaveCount(reconnects + 1);
        transport.Calls.Select(call => call.ConnectionId).Should().OnlyHaveUniqueItems();
        transport.AllSuccessorsSawQuiescentPredecessors.Should().BeTrue();
        transport.Calls.Should().OnlyContain(call => call.PhysicalToken.IsCancellationRequested &&
            call.ChildToken.IsCancellationRequested && call.AllIoCompleted && call.ChildTask!.IsCompleted);
        transport.LiveCalls.Should().Be(0);
        transport.LiveChildren.Should().Be(0);
        peakTimers.Should().BeGreaterThan(0);
        clock.ActiveTimerCount.Should().Be(0,
            "all interval, renewal, I/O watchdog and recovery timers must be disposed on shutdown");
        clock.Advance(TimeSpan.FromHours(2));
        transport.Calls.Should().HaveCount(reconnects + 1, "shutdown must leave no delayed reconnect owner");
        clock.ActiveTimerCount.Should().Be(0);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(1, timeout.Token);
    }

    private sealed class ClockTokenService(TimeProvider clock) : IAgentTokenService
    {
        public Task<(string AccessToken, DateTimeOffset ExpiresAtUtc)> GetAccessTokenAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(("test-token", clock.GetUtcNow().AddHours(1)));
        }
    }

    private sealed class ResettingTransport
    {
        private readonly object _gate = new();
        private readonly List<ResettingCall> _calls = [];
        internal Channel<ResettingCall> Owners { get; } = Channel.CreateUnbounded<ResettingCall>();
        internal bool AllSuccessorsSawQuiescentPredecessors { get; private set; } = true;
        internal ResettingCall[] Calls { get { lock (_gate) return _calls.ToArray(); } }
        internal int LiveCalls => Calls.Count(call => !call.Disposed);
        internal int LiveChildren => Calls.Count(call => call.ChildTask is { IsCompleted: false });

        internal AsyncDuplexStreamingCall<AgentFrame, GatewayFrame> Open(Metadata _, CancellationToken token)
        {
            ResettingCall call;
            lock (_gate)
            {
                AllSuccessorsSawQuiescentPredecessors &= _calls.All(previous => previous.IsRetired);
                call = new ResettingCall(token, checked((ulong)_calls.Count + 1));
                _calls.Add(call);
            }
            return new AsyncDuplexStreamingCall<AgentFrame, GatewayFrame>(call, call,
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), call.Dispose);
        }

        internal Task RunChildOwnerAsync(GatewayPresenceSession session, string _, CancellationToken token)
        {
            var call = Calls.Single(candidate => candidate.ConnectionId == session.ConnectionId);
            call.ChildToken = token;
            call.ChildTask = call.RunChildAsync(token);
            Owners.Writer.TryWrite(call).Should().BeTrue();
            return call.ChildTask;
        }
    }

    private sealed class ResettingCall : IClientStreamWriter<AgentFrame>, IAsyncStreamReader<GatewayFrame>
    {
        private readonly object _tasksGate = new();
        private readonly List<Task> _ioTasks = [];
        private readonly Channel<GatewayFrame> _responses = Channel.CreateUnbounded<GatewayFrame>();
        private readonly TaskCompletionSource _childCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _childCleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _physicalOwner;
        private readonly ulong _epoch;
        private int _disposed;
        private int _activeReads;
        private int _activeWrites;
        private int _heartbeatWrites;
        internal ResettingCall(CancellationToken token, ulong epoch)
        {
            _physicalOwner = CancellationTokenSource.CreateLinkedTokenSource(token);
            PhysicalToken = _physicalOwner.Token;
            _epoch = epoch;
        }
        internal Guid ConnectionId { get; } = Guid.NewGuid();
        internal CancellationToken PhysicalToken { get; }
        internal CancellationToken ChildToken { get; set; }
        internal Task? ChildTask { get; set; }
        internal bool ChildCancellationObserved => _childCancelled.Task.IsCompleted;
        internal bool Disposed => Volatile.Read(ref _disposed) != 0;
        internal int ActiveReads => Volatile.Read(ref _activeReads);
        internal int ActiveWrites => Volatile.Read(ref _activeWrites);
        internal int HeartbeatWrites => Volatile.Read(ref _heartbeatWrites);
        internal bool AllIoCompleted { get { lock (_tasksGate) return _ioTasks.All(task => task.IsCompleted); } }
        internal bool IsRetired => Disposed && ActiveReads == 0 && ActiveWrites == 0 && AllIoCompleted &&
            ChildToken.IsCancellationRequested && ChildTask is { IsCompleted: true };
        public WriteOptions? WriteOptions { get; set; }
        public GatewayFrame Current { get; private set; } = new();
        internal void ResetPendingRead() => _responses.Writer.TryComplete(
            new RpcException(new Status(StatusCode.Unavailable, "Test physical stream reset.")));
        internal void FinishChildCleanup() => _childCleanup.TrySetResult();
        internal async Task RunChildAsync(CancellationToken token)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally
            {
                _childCancelled.TrySetResult();
                await _childCleanup.Task;
            }
        }

        public Task WriteAsync(AgentFrame frame) => WriteAsync(frame, PhysicalToken);
        public Task WriteAsync(AgentFrame frame, CancellationToken token) => Track(WriteCoreAsync(frame, token));
        private async Task WriteCoreAsync(AgentFrame frame, CancellationToken token)
        {
            Interlocked.Increment(ref _activeWrites);
            try
            {
                using var stopping = CancellationTokenSource.CreateLinkedTokenSource(token, PhysicalToken);
                await Task.Yield();
                stopping.Token.ThrowIfCancellationRequested();
                if (frame.Heartbeat is not null && Interlocked.Increment(ref _heartbeatWrites) > 1)
                {
                    return;
                }
                var response = new GatewayFrame
                {
                    ProtocolVersion = frame.ProtocolVersion, TenantId = frame.TenantId, ClientId = frame.ClientId,
                    ConnectionId = ConnectionId.ToString("D"), ConnectionEpoch = _epoch,
                    OperationId = frame.OperationId, Sequence = frame.Sequence
                };
                if (frame.Hello is not null)
                    response.Connected = new ConnectAccepted
                    {
                        HeartbeatIntervalSeconds = 5, HeartbeatTimeoutSeconds = 10, PresenceAuthority = "akka"
                    };
                else
                    response.HeartbeatAccepted = new HeartbeatAccepted { PresenceAuthority = "akka" };
                await _responses.Writer.WriteAsync(response, stopping.Token);
            }
            finally { Interlocked.Decrement(ref _activeWrites); }
        }

        public Task<bool> MoveNext(CancellationToken token) => Track(ReadCoreAsync(token));
        private async Task<bool> ReadCoreAsync(CancellationToken token)
        {
            Interlocked.Increment(ref _activeReads);
            try
            {
                using var stopping = CancellationTokenSource.CreateLinkedTokenSource(token, PhysicalToken);
                if (!await _responses.Reader.WaitToReadAsync(stopping.Token)) return false;
                stopping.Token.ThrowIfCancellationRequested();
                if (!_responses.Reader.TryRead(out var frame)) throw new InvalidOperationException("The test has one reader.");
                Current = frame;
                return true;
            }
            finally { Interlocked.Decrement(ref _activeReads); }
        }
        private T Track<T>(T task) where T : Task
        {
            lock (_tasksGate) _ioTasks.Add(task);
            return task;
        }
        public Task CompleteAsync() => Task.CompletedTask;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _physicalOwner.Cancel();
            _physicalOwner.Dispose();
        }
    }
}
