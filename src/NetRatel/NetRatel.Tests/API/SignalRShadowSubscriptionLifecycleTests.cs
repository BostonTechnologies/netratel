using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using NetRatel.API.Realtime.Shadow;
using NetRatel.Application.Fanout;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class SignalRShadowSubscriptionLifecycleTests
{
    [Fact]
    public void Registry_DuplicateAndConcurrentSameGroupJoinsAreIdempotent()
    {
        var registry = new SignalRShadowSubscriptionRegistry(TimeProvider.System);
        registry.TryRegisterConnection("connection-a", Principal(7)).Should().BeTrue();
        var group = Group(CommandTarget(7, "command-a"));

        Parallel.For(0, 128, _ =>
            registry.TryAddSubscription("connection-a", group).Should().BeTrue());

        registry.TryAddSubscription("connection-a", Group(CommandTarget(7, "command-b")))
            .Should().BeTrue("idempotent joins must not exhaust the change-rate window");
        registry.GetStatus().Should().Match<SignalRShadowSubscriptionStatus>(status =>
            status.ActiveMemberships == 2 && status.ActiveGroups == 2);
    }

    [Fact]
    public void Registry_RejectsAnyGroupNameNotDerivedByTheServer()
    {
        var registry = new SignalRShadowSubscriptionRegistry(TimeProvider.System);
        registry.TryRegisterConnection("connection-a", Principal(7)).Should().BeTrue();

        registry.TryAddSubscription("connection-a", "akka-shadow:v1:tenant:7:command:raw")
            .Should().BeFalse();
        registry.GetStatus().Should().Match<SignalRShadowSubscriptionStatus>(status =>
            status.ActiveMemberships == 0 && status.RejectedSubscriptions == 1);
    }

    [Fact]
    public void Registry_ConcurrentDifferentGroupsStopsAtPerConnectionLimitWithoutLeaks()
    {
        var registry = new SignalRShadowSubscriptionRegistry(TimeProvider.System);
        registry.TryRegisterConnection("connection-a", Principal(7)).Should().BeTrue();

        Parallel.For(0, 64, index =>
            registry.TryAddSubscription(
                "connection-a",
                Group(CommandTarget(7, $"command-{index}"))));

        var status = registry.GetStatus();
        status.ActiveMemberships.Should().Be(SignalRShadowSubscriptionRegistry.MaximumGroupsPerConnection);
        status.ActiveGroups.Should().Be(SignalRShadowSubscriptionRegistry.MaximumGroupsPerConnection);
        status.ActiveMemberships.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void Registry_RateWindowResetsAtExactInjectedTimeBoundary()
    {
        var time = new AdjustableTimeProvider();
        var registry = new SignalRShadowSubscriptionRegistry(time);
        registry.TryRegisterConnection("connection-a", Principal(7)).Should().BeTrue();
        var group = Group(CommandTarget(7, "command-a"));

        for (var index = 0; index < SignalRShadowSubscriptionRegistry.MaximumSubscriptionChangesPerMinute / 2;
             index++)
        {
            registry.TryAddSubscription("connection-a", group).Should().BeTrue();
            registry.TryBeginRemoveSubscription("connection-a", group)
                .Should().Be(SubscriptionRemovalDisposition.Ready);
            registry.CommitRemoveSubscription("connection-a", group);
        }

        registry.TryAddSubscription("connection-a", group).Should().BeFalse();
        time.Advance(TimeSpan.FromMinutes(1));
        registry.TryAddSubscription("connection-a", group).Should().BeTrue();
    }

    [Fact]
    public void Registry_DisconnectCleanupIsIdempotentAndReconnectStartsClean()
    {
        var registry = new SignalRShadowSubscriptionRegistry(TimeProvider.System);
        registry.TryRegisterConnection("connection-a", Principal(7)).Should().BeTrue();
        registry.TryAddSubscription("connection-a", Group(CommandTarget(7, "command-a")))
            .Should().BeTrue();

        registry.RemoveConnection("connection-a");
        registry.RemoveConnection("connection-a");
        registry.GetStatus().Should().Match<SignalRShadowSubscriptionStatus>(status =>
            status.ActiveConnections == 0 && status.ActiveGroups == 0 && status.ActiveMemberships == 0);

        registry.TryRegisterConnection("connection-b", Principal(7)).Should().BeTrue();
        registry.GetStatus().ActiveConnections.Should().Be(1);
    }

    [Fact]
    public void Registry_DuplicateConnectionIdCannotChangeAuthenticatedOwner()
    {
        var registry = new SignalRShadowSubscriptionRegistry(TimeProvider.System);
        registry.TryRegisterConnection("connection-a", Principal(7, subject: "operator-a"))
            .Should().BeTrue();
        registry.TryRegisterConnection("connection-a", Principal(7, subject: "operator-b"))
            .Should().BeFalse();

        registry.GetStatus().Should().Match<SignalRShadowSubscriptionStatus>(status =>
            status.ActiveConnections == 1 && status.ActiveUsers == 1 && status.RejectedConnections == 1);
    }

    [Fact]
    public void Registry_EnforcesPerUserAndGlobalConnectionLimitsExactly()
    {
        var perUser = new SignalRShadowSubscriptionRegistry(TimeProvider.System);
        for (var index = 0; index < SignalRShadowSubscriptionRegistry.MaximumConnectionsPerUser; index++)
        {
            perUser.TryRegisterConnection($"same-user-{index}", Principal(7)).Should().BeTrue();
        }

        perUser.TryRegisterConnection("same-user-overflow", Principal(7)).Should().BeFalse();

        var global = new SignalRShadowSubscriptionRegistry(TimeProvider.System);
        for (var index = 0; index < SignalRShadowSubscriptionRegistry.MaximumConnections; index++)
        {
            global.TryRegisterConnection($"connection-{index}", Principal(7, subject: $"user-{index}"))
                .Should().BeTrue();
        }

        global.TryRegisterConnection("connection-overflow", Principal(7, subject: "overflow"))
            .Should().BeFalse();
        global.GetStatus().ActiveConnections.Should().Be(SignalRShadowSubscriptionRegistry.MaximumConnections);
    }

    [Fact]
    public void Registry_EnforcesGroupMemberLimitAndCleansEveryMembership()
    {
        var registry = new SignalRShadowSubscriptionRegistry(TimeProvider.System);
        var group = Group(CommandTarget(7, "shared-command"));
        for (var index = 0; index < SignalRShadowSubscriptionRegistry.MaximumMembersPerGroup; index++)
        {
            var connection = $"connection-{index}";
            registry.TryRegisterConnection(connection, Principal(7, subject: $"user-{index}"))
                .Should().BeTrue();
            registry.TryAddSubscription(connection, group).Should().BeTrue();
        }

        registry.GetStatus().Should().Match<SignalRShadowSubscriptionStatus>(status =>
            status.ActiveGroups == 1 &&
            status.ActiveMemberships == SignalRShadowSubscriptionRegistry.MaximumMembersPerGroup);

        for (var index = 0; index < SignalRShadowSubscriptionRegistry.MaximumMembersPerGroup; index++)
        {
            registry.RemoveConnection($"connection-{index}");
        }

        registry.GetStatus().Should().Match<SignalRShadowSubscriptionStatus>(status =>
            status.ActiveConnections == 0 && status.ActiveGroups == 0 && status.ActiveMemberships == 0);
    }

    [Fact]
    public async Task Hub_UnauthenticatedOrWrongTenantRequestsFailBeforeGroupMutation()
    {
        var registry = new SignalRShadowSubscriptionRegistry(TimeProvider.System);
        var groups = new TrackingGroupManager();
        var unauthenticated = CreateHub(registry, groups, Principal(null, authenticated: false));

        await FluentActions.Awaiting(unauthenticated.OnConnectedAsync)
            .Should().ThrowAsync<HubException>();
        unauthenticated.Context.ConnectionAborted.IsCancellationRequested.Should().BeTrue();

        var hub = CreateHub(registry, groups, Principal(7));
        await hub.OnConnectedAsync();
        await FluentActions.Awaiting(() => hub.SubscribeCommand(8, "other-tenant-command"))
            .Should().ThrowAsync<HubException>();
        await FluentActions.Awaiting(() => hub.SubscribeCommand(7, "contains:delimiter"))
            .Should().ThrowAsync<HubException>();

        groups.Added.Should().BeEmpty();
        registry.GetStatus().UnauthorizedSubscriptions.Should().Be(1);
        await hub.OnDisconnectedAsync(null);
    }

    [Fact]
    public async Task Hub_RemoveFailureKeepsMembershipTrackedAndRetryable()
    {
        var registry = new SignalRShadowSubscriptionRegistry(TimeProvider.System);
        var groups = new TrackingGroupManager();
        var hub = CreateHub(registry, groups, Principal(7));
        await hub.OnConnectedAsync();
        await hub.SubscribeCommand(7, "command-a");

        groups.ThrowOnRemove = true;
        await FluentActions.Awaiting(() => hub.UnsubscribeCommand(7, "command-a"))
            .Should().ThrowAsync<InvalidOperationException>();
        registry.GetStatus().ActiveMemberships.Should().Be(1);

        groups.ThrowOnRemove = false;
        await hub.UnsubscribeCommand(7, "command-a");
        await hub.UnsubscribeCommand(7, "command-a");
        registry.GetStatus().ActiveMemberships.Should().Be(0);

        await hub.OnDisconnectedAsync(null);
        registry.GetStatus().ActiveConnections.Should().Be(0);
    }

    [Fact]
    public async Task Hub_DuplicateJoinIsNoOpAndNewJoinFailureRollsBackOnlyNewMembership()
    {
        var registry = new SignalRShadowSubscriptionRegistry(TimeProvider.System);
        var groups = new TrackingGroupManager();
        var hub = CreateHub(registry, groups, Principal(7));
        await hub.OnConnectedAsync();
        await hub.SubscribeCommand(7, "command-a");

        groups.ThrowOnAdd = true;
        await hub.SubscribeCommand(7, "command-a");
        await FluentActions.Awaiting(() => hub.SubscribeCommand(7, "command-b"))
            .Should().ThrowAsync<InvalidOperationException>();

        registry.GetStatus().Should().Match<SignalRShadowSubscriptionStatus>(status =>
            status.ActiveMemberships == 1 && status.ActiveGroups == 1);
        await hub.OnDisconnectedAsync(null);
    }

    [Fact]
    public async Task Hub_SubscribeReturnsLatestBoundedSnapshotOnReconnect()
    {
        var registry = new SignalRShadowSubscriptionRegistry(TimeProvider.System);
        var target = CommandTarget(7, "command-a");
        var snapshot = new ShadowFanoutEnvelope(
            ShadowFanoutEnvelope.CurrentSchemaVersion,
            ShadowFanoutCategory.Command,
            target,
            ShadowFanoutEventType.Snapshot,
            ShadowFanoutStatus.Active,
            DateTimeOffset.Parse("2026-08-07T10:00:00Z"),
            Sequence: 3);
        var snapshots = new StubSnapshotSource(snapshot);

        var first = CreateHub(registry, new TrackingGroupManager(), Principal(7), snapshots, "connection-a");
        await first.OnConnectedAsync();
        (await first.SubscribeCommand(7, "command-a")).Should().ContainSingle().Which.Should().Be(snapshot);
        await first.OnDisconnectedAsync(null);

        var reconnect = CreateHub(registry, new TrackingGroupManager(), Principal(7), snapshots, "connection-b");
        await reconnect.OnConnectedAsync();
        (await reconnect.SubscribeCommand(7, "command-a")).Should().ContainSingle().Which.Should().Be(snapshot);
        await reconnect.OnDisconnectedAsync(null);
    }

    [Fact]
    public void Hub_HasNoArbitraryGroupMutationMethod()
    {
        typeof(AkkaShadowHub).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.DeclaringType == typeof(AkkaShadowHub))
            .Select(method => method.Name)
            .Should().NotContain(name =>
                name.Contains("Group", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Publish", StringComparison.OrdinalIgnoreCase));
    }

    private static AkkaShadowHub CreateHub(
        SignalRShadowSubscriptionRegistry registry,
        TrackingGroupManager groups,
        ClaimsPrincipal principal,
        IShadowFanoutSnapshotSource? snapshots = null,
        string connectionId = "connection-a")
    {
        var context = new TestHubCallerContext(connectionId, principal);
        return new AkkaShadowHub(
            registry,
            new ShadowTenantClaimAuthorizer(),
            snapshots ?? new StubSnapshotSource())
        {
            Context = context,
            Groups = groups,
            Clients = new NoOpCallerClients()
        };
    }

    private static ClaimsPrincipal Principal(
        int? tenantId,
        bool authenticated = true,
        string subject = "operator-a")
    {
        var claims = new List<Claim> { new("sub", subject) };
        if (tenantId is not null)
        {
            claims.Add(new Claim("tenant_id", tenantId.Value.ToString()));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticated ? "test" : null));
    }

    private static ShadowFanoutTarget CommandTarget(int tenantId, string commandId) =>
        new(tenantId, ShadowFanoutTargetScope.Command, CommandId: commandId);

    private static string Group(ShadowFanoutTarget target)
    {
        SignalRShadowGroupName.TryCreate(target, out var group).Should().BeTrue();
        return group;
    }

    private sealed class AdjustableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-08-07T10:00:00Z");

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }

    private sealed class StubSnapshotSource(params ShadowFanoutEnvelope[] snapshots)
        : IShadowFanoutSnapshotSource
    {
        public IReadOnlyList<ShadowFanoutEnvelope> GetSnapshots(ShadowFanoutTarget target) => snapshots;
    }

    private sealed class TrackingGroupManager : IGroupManager
    {
        public List<string> Added { get; } = [];

        public List<string> Removed { get; } = [];

        public bool ThrowOnRemove { get; set; }

        public bool ThrowOnAdd { get; set; }

        public Task AddToGroupAsync(
            string connectionId,
            string groupName,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnAdd)
            {
                throw new InvalidOperationException("controlled add failure");
            }

            Added.Add(groupName);
            return Task.CompletedTask;
        }

        public Task RemoveFromGroupAsync(
            string connectionId,
            string groupName,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnRemove)
            {
                throw new InvalidOperationException("controlled remove failure");
            }

            Removed.Add(groupName);
            return Task.CompletedTask;
        }
    }

    private sealed class TestHubCallerContext : HubCallerContext
    {
        private readonly CancellationTokenSource _aborted = new();

        public TestHubCallerContext(string connectionId, ClaimsPrincipal principal)
        {
            ConnectionId = connectionId;
            User = principal;
        }

        public override string ConnectionId { get; }

        public override string? UserIdentifier => User.FindFirst("sub")?.Value;

        public override ClaimsPrincipal User { get; }

        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

        public override IFeatureCollection Features { get; } = new FeatureCollection();

        public override CancellationToken ConnectionAborted => _aborted.Token;

        public override void Abort() => _aborted.Cancel();
    }

    private sealed class NoOpCallerClients : IHubCallerClients
    {
        private static readonly IClientProxy Proxy = new NoOpClientProxy();

        public IClientProxy All => Proxy;

        public IClientProxy Caller => Proxy;

        public IClientProxy Others => Proxy;

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy;

        public IClientProxy Client(string connectionId) => Proxy;

        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy;

        public IClientProxy Group(string groupName) => Proxy;

        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy;

        public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy;

        public IClientProxy OthersInGroup(string groupName) => Proxy;

        public IClientProxy User(string userId) => Proxy;

        public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy;
    }

    private sealed class NoOpClientProxy : IClientProxy
    {
        public Task SendCoreAsync(
            string method,
            object?[] args,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
