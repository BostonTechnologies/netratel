using System.Threading.Channels;
using System.Security.Claims;
using FluentAssertions;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.API.Gateway;
using NetRatel.Akka.Configuration;
using NetRatel.Application.Presence;
using NetRatel.Application.Agents;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class GatewayAuthenticationLifetimeTests
{
    [Fact]
    public async Task CrossReplicaActorRenewal_ExtendsExactExpiry_AndAQueuedOldExpiryCannotRetireIt()
    {
        using var fixture = new Fixture();
        var originalExpiry = fixture.Router.Snapshot.AuthenticationExpiresAtUtc!.Value;
        await using var owner = await fixture.AttachAsync();
        fixture.Leases.Find(fixture.Client, fixture.Connection, 8).Should().BeNull("this API replica has no local presence RPC");
        var staleExpiryCallback = fixture.Clock.CallbackAt(originalExpiry);
        var renewedExpiry = fixture.Clock.GetUtcNow().AddMinutes(1);
        fixture.Router.Snapshot = fixture.Router.Snapshot with { AuthenticationExpiresAtUtc = renewedExpiry };
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        await fixture.Clock.WaitForDueAsync(renewedExpiry);
        fixture.Clock.Advance(TimeSpan.FromSeconds(15));
        staleExpiryCallback();
        owner.Token.IsCancellationRequested.Should().BeFalse("actor-confirmed renewal supersedes the old queued timer");
        fixture.Clock.Advance(TimeSpan.FromSeconds(40));
        owner.Token.IsCancellationRequested.Should().BeTrue("authority ends at the exact renewed credential expiry");
    }

    [Theory]
    [InlineData("connection")]
    [InlineData("epoch")]
    [InlineData("offline")]
    [InlineData("nonauthoritative")]
    [InlineData("expired")]
    [InlineData("failure")]
    [InlineData("stall")]
    public async Task LostOrUnverifiableActorAuthority_CancelsCrossReplicaCapability(string failure)
    {
        using var fixture = new Fixture();
        await using var owner = await fixture.AttachAsync();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var observer = owner.Token.Register(() => cancelled.TrySetResult());
        var stalled = new TaskCompletionSource<ClientPresenceSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Router.Read = failure switch
        {
            "failure" => _ => Task.FromException<ClientPresenceSnapshot>(new InvalidOperationException("actor unavailable")),
            "stall" => _ => stalled.Task,
            _ => null
        };
        fixture.Router.Snapshot = failure switch
        {
            "connection" => fixture.Router.Snapshot with { ConnectionId = Guid.NewGuid() },
            "epoch" => fixture.Router.Snapshot with { ConnectionEpoch = 9 },
            "offline" => fixture.Router.Snapshot with { Status = ClientPresenceStatus.Offline },
            "nonauthoritative" => fixture.Router.Snapshot with { IsAuthoritative = false },
            "expired" => fixture.Router.Snapshot with { AuthenticationExpiresAtUtc = fixture.Clock.GetUtcNow() },
            _ => fixture.Router.Snapshot
        };
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        await fixture.Router.WaitForReadsAsync(2);
        if (failure == "stall")
        {
            await fixture.Clock.WaitForDueAsync(fixture.Clock.GetUtcNow().AddSeconds(3));
            fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        }
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        owner.Token.IsCancellationRequested.Should().BeTrue();
        if (failure == "stall") stalled.TrySetException(new InvalidOperationException("late actor failure"));
    }

    [Fact]
    public async Task InitialActorQueryBlackHole_IsBoundedEvenWithoutALocalLease()
    {
        using var fixture = new Fixture();
        var stalled = new TaskCompletionSource<ClientPresenceSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Router.Read = _ => stalled.Task;
        var attaching = fixture.AttachAsync();
        await fixture.Router.WaitForReadsAsync(1);
        await fixture.Clock.WaitForDueAsync(fixture.Clock.GetUtcNow().AddSeconds(3));
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        var outcome = () => attaching.WaitAsync(TimeSpan.FromSeconds(5));
        await outcome.Should().ThrowAsync<TimeoutException>();
        stalled.SetException(new InvalidOperationException("late initial actor failure"));
        fixture.Clock.TimerCount.Should().Be(0);
    }

    [Fact]
    public async Task DisposeDuringStalledActorQuery_StopsMonitorAndTimers_BeforeLateCompletion()
    {
        using var fixture = new Fixture();
        var owner = await fixture.AttachAsync();
        var token = owner.Token;
        var stalled = new TaskCompletionSource<ClientPresenceSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Router.Read = _ => stalled.Task;
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        await fixture.Router.WaitForReadsAsync(2);
        await owner.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        token.IsCancellationRequested.Should().BeTrue();
        fixture.Clock.TimerCount.Should().Be(0);
        var readsAtDispose = fixture.Router.ReadCount;
        stalled.SetResult(fixture.Router.Snapshot);
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        fixture.Router.ReadCount.Should().Be(readsAtDispose);
        fixture.Clock.TimerCount.Should().Be(0);
    }

    [Fact]
    public async Task ExpiredCapabilityCredential_CannotAttachToAnActorExtendedByFreshPresenceCredentials()
    {
        using var fixture = new Fixture();
        fixture.Clock.Advance(TimeSpan.FromSeconds(21));
        fixture.Router.Snapshot = fixture.Router.Snapshot with
        {
            AuthenticationExpiresAtUtc = fixture.Clock.GetUtcNow().AddMinutes(5)
        };
        var attaching = () => fixture.AttachAsync();
        (await attaching.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Unauthenticated);
        fixture.Clock.TimerCount.Should().Be(0);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider _services;
        private readonly ServerCallContext _context;
        private readonly AgentGatewayRenewalTestCredentials _credentials = new();
        public ClientKey Client { get; } = new(71, Guid.NewGuid());
        public Guid Connection { get; } = Guid.NewGuid();
        public MonitorClock Clock { get; } = new();
        public PresenceRouter Router { get; }
        public AgentGatewayAuthenticationLeaseRegistry Leases { get; }
        public Fixture()
        {
            Router = new(new ClientPresenceSnapshot(Client, ClientPresenceStatus.Online, 8, Connection, 0,
                Clock.GetUtcNow(), "test", ["terminal-gateway", "file-gateway"], null, "akka", true,
                AuthenticationExpiresAtUtc: Clock.GetUtcNow().AddSeconds(20)));
            Leases = new(Clock);
            var identity = new AuthenticatedAgentIdentity(Client.TenantId, Client.AgentId);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAuthentication().AddJwtBearer("Agent", _credentials.Configure);
            services.AddSingleton<TimeProvider>(Clock).AddSingleton(Leases).AddSingleton<IClientPresenceRouter>(Router)
                .AddSingleton<IAgentManagementService>(new RenewalAgentManagementService(identity))
                .AddSingleton<AgentGatewayRenewalAuthenticator>()
                .AddSingleton(new NetRatelAkkaOptions { AskTimeoutSeconds = 3 });
            _services = services.BuildServiceProvider();
            var http = new DefaultHttpContext
            {
                RequestServices = _services,
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", identity.ClientId), new Claim("agent_id", identity.ClientId),
                    new Claim("tenant_id", identity.TenantId.ToString()), new Claim("role", "agent"),
                    new Claim("scope", "netratel:connect")], "Agent"))
            };
            http.Request.Headers.Authorization = "Bearer " + _credentials.CreateToken(identity, Router.Snapshot.AuthenticationExpiresAtUtc!.Value);
            _context = new CallContext(http);
        }
        public Task<AgentGatewayAuthenticationLifetime> AttachAsync() =>
            AgentGatewayAuthenticationLifetime.AttachAsync(_context, Client, Connection, 8);
        public void Dispose() { _services.Dispose(); _credentials.Dispose(); }
    }

    private sealed class PresenceRouter(ClientPresenceSnapshot snapshot) : IClientPresenceRouter
    {
        private readonly Channel<int> _reads = Channel.CreateUnbounded<int>();
        private int _readCount;
        public ClientPresenceSnapshot Snapshot { get; set; } = snapshot;
        public Func<CancellationToken, Task<ClientPresenceSnapshot>>? Read { get; set; }
        public int ReadCount => Volatile.Read(ref _readCount);
        public Task<ClientPresenceSnapshot> GetSnapshotAsync(ClientKey client, CancellationToken cancellationToken)
        {
            var result = Read?.Invoke(cancellationToken) ?? Task.FromResult(Snapshot);
            _reads.Writer.TryWrite(Interlocked.Increment(ref _readCount));
            return result;
        }
        public async Task WaitForReadsAsync(int expected)
        {
            while (await _reads.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)) < expected) { }
        }
        public Task<GatewayPresenceSessionStarted> StartSessionAsync(StartGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> RecordHeartbeatAsync(RecordGatewayHeartbeat message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> EndSessionAsync(EndGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientPresenceRouteStatus> ProbeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class MonitorClock : TimeProvider
    {
        private readonly object _sync = new();
        private readonly List<Timer> _timers = [];
        private readonly Channel<DateTimeOffset> _changed = Channel.CreateUnbounded<DateTimeOffset>();
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-10-04T12:00:00Z");
        public int TimerCount { get { lock (_sync) return _timers.Count; } }
        public override DateTimeOffset GetUtcNow() { lock (_sync) return _now; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_sync)
            {
                var timer = new Timer(this, callback, state);
                _timers.Add(timer);
                timer.Change(dueTime, period);
                return timer;
            }
        }
        public Action CallbackAt(DateTimeOffset expiry)
        {
            lock (_sync) return _timers.Single(timer => timer.Due == expiry).Callback;
        }
        public async Task WaitForDueAsync(DateTimeOffset due)
        {
            while (await _changed.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)) != due) { }
        }
        public void Advance(TimeSpan duration)
        {
            Action[] callbacks;
            lock (_sync)
            {
                _now += duration;
                var due = _timers.Where(timer => timer.Due <= _now).ToArray();
                callbacks = due.Select(timer => timer.Callback).ToArray();
                foreach (var timer in due) timer.Due = DateTimeOffset.MaxValue;
            }
            foreach (var callback in callbacks) callback();
        }
        private sealed class Timer(MonitorClock owner, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset Due { get; set; } = DateTimeOffset.MaxValue;
            public Action Callback => () => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._sync)
                {
                    if (!owner._timers.Contains(this)) return false;
                    Due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : owner._now + dueTime;
                    owner._changed.Writer.TryWrite(Due);
                    return true;
                }
            }
            public void Dispose() { lock (owner._sync) owner._timers.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class CallContext : ServerCallContext
    {
        public CallContext(HttpContext httpContext) => UserState["__HttpContext"] = httpContext;
        protected override string MethodCore => "/gateway";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "ipv4:127.0.0.1:0";
        protected override DateTime DeadlineCore => DateTime.MaxValue;
        protected override Metadata RequestHeadersCore { get; } = new();
        protected override CancellationToken CancellationTokenCore => CancellationToken.None;
        protected override Metadata ResponseTrailersCore { get; } = new();
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => new(null, new Dictionary<string, List<AuthProperty>>());
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => throw new NotSupportedException();
    }
}
