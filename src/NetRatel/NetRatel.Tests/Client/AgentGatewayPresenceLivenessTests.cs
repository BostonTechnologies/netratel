using System.Threading.Channels;
using Google.Protobuf.WellKnownTypes;
using AwesomeAssertions;
using Grpc.Core;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Application.ClientAuth;
using NetRatel.Client.Service.Auth;
using NetRatel.Client.Service.Gateway;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class AgentGatewayPresenceLivenessTests
{
    [Fact]
    public async Task OfflineTokenStartup_RestoresAuthoritativePresenceWithTheSameOwnerAndIdentity()
    {
        var clock = new GatewayPresenceTestClock();
        var transport = new ScriptedTransport("healthy");
        var agentId = Guid.NewGuid();
        var requests = 0;
        var tokens = new DelegateTokenService(_ => Interlocked.Increment(ref requests) == 1
            ? Task.FromException<(string, DateTimeOffset)>(new AgentClientAuthException("Upstream unavailable", 502))
            : Task.FromResult(("restored-token", clock.GetUtcNow().AddHours(1))));
        using var stopping = new CancellationTokenSource();
        var ready = new TaskCompletionSource<GatewayPresenceSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new AgentGatewayPresenceClient(new GatewayClientOptions { Endpoint = "https://gateway.test" },
            tokens, 7, agentId, "test", [], _ => { },
            runForPresenceSession: (session, _, token) =>
            {
                ready.TrySetResult(session);
                return Task.Delay(Timeout.InfiniteTimeSpan, token);
            }, timeProvider: clock, nextRandom: () => 0.5, createCall: transport.Open);
        var run = agent.RunAsync(stopping.Token);
        try
        {
            await WaitUntilAsync(() => clock.HasTimer(TimeSpan.FromSeconds(1.5)));
            run.IsCompleted.Should().BeFalse();
            transport.Attempts.Should().Be(0);
            Volatile.Read(ref requests).Should().Be(1);
            clock.Advance(TimeSpan.FromSeconds(1.5));
            var session = await ready.Task.WaitAsync(TimeSpan.FromSeconds(2));
            session.AgentId.Should().Be(agentId);
            session.TenantId.Should().Be(7);
            transport.Attempts.Should().Be(1);
            Volatile.Read(ref requests).Should().Be(2);
        }
        finally { stopping.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    [Fact]
    public async Task BlackholedAuthentication_CancelsActualWorkAtWholeAttemptBudget()
    {
        var clock = new GatewayPresenceTestClock();
        var active = 0;
        var tokens = new DelegateTokenService(async token =>
        {
            Interlocked.Increment(ref active);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return ("unreachable", clock.GetUtcNow()); }
            finally { Interlocked.Decrement(ref active); }
        });
        using var stopping = new CancellationTokenSource();
        var agent = new AgentGatewayPresenceClient(new GatewayClientOptions { Endpoint = "https://gateway.test" },
            tokens, 7, Guid.NewGuid(), "test", [], _ => { }, timeProvider: clock, nextRandom: () => 0.5);
        var run = agent.RunAsync(stopping.Token);
        try
        {
            await WaitUntilAsync(() => Volatile.Read(ref active) == 1 && clock.HasTimer(TimeSpan.FromSeconds(30)));
            clock.Advance(TimeSpan.FromSeconds(30));
            await WaitUntilAsync(() => clock.HasTimer(TimeSpan.FromSeconds(1.5)));
            Volatile.Read(ref active).Should().Be(0, "the request token, rather than only its waiting wrapper, is cancelled");
            run.IsCompleted.Should().BeFalse();
        }
        finally { stopping.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    [Fact]
    public async Task ProactiveTransientRenewalFailure_PreservesValidAuthorityAndHeartbeatProgress()
    {
        var clock = new GatewayPresenceTestClock();
        var transport = new ScriptedTransport("healthy", supportsRenewal: true, clock);
        var requests = 0;
        var tokens = new DelegateTokenService(_ => Interlocked.Increment(ref requests) == 1
            ? Task.FromResult(("initial-token", clock.GetUtcNow().AddSeconds(65)))
            : Task.FromException<(string, DateTimeOffset)>(new AgentClientAuthException("Upstream unavailable", 503)));
        using var stopping = new CancellationTokenSource();
        GatewayPresenceSession? session = null;
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new AgentGatewayPresenceClient(new GatewayClientOptions { Endpoint = "https://gateway.test" },
            tokens, 7, Guid.NewGuid(), "test", [], message =>
            {
                if (message.StartsWith("Proactive authentication renewal", StringComparison.Ordinal)) waiting.TrySetResult();
            }, runForPresenceSession: (current, _, token) =>
            {
                session = current;
                return Task.Delay(Timeout.InfiniteTimeSpan, token);
            }, timeProvider: clock, nextRandom: () => 0.5, createCall: transport.Open);
        var run = agent.RunAsync(stopping.Token);
        try
        {
            await WaitUntilAsync(() => session is not null && clock.HasTimer(TimeSpan.FromSeconds(5)));
            clock.Advance(TimeSpan.FromSeconds(5));
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => clock.HasTimer(TimeSpan.FromSeconds(5)));
            clock.Advance(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => transport.First!.HeartbeatWrites >= 2);
            session!.GetAccessToken("fallback").Should().Be("initial-token");
            transport.Attempts.Should().Be(1);
            transport.First!.Disposed.Should().BeFalse();
        }
        finally { stopping.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    [Fact]
    public async Task RenewalAcknowledgementsFor120Seconds_DoNotResetInheritedOutageHistory()
    {
        var clock = new GatewayPresenceTestClock();
        var transport = new ScriptedTransport("healthy", supportsRenewal: true, clock,
            heartbeatInterval: 300, heartbeatTimeout: 3060);
        var directory = Path.Combine(Path.GetTempPath(), $"netratel-renewal-{Guid.NewGuid():N}");
        var store = new OperationalRecoveryStateStore(Path.Combine(directory, "recovery.json"));
        store.Write(new OperationalRecoveryState(1, clock.GetUtcNow(), true, 1800, 5, 100, null, null, null)).Should().BeTrue();
        using var stopping = new CancellationTokenSource();
        var renewals = 0;
        var agent = new AgentGatewayPresenceClient(new GatewayClientOptions { Endpoint = "https://gateway.test" },
            new ClockTokenService(clock, TimeSpan.FromSeconds(65)), 7, Guid.NewGuid(), "test", [], message =>
            {
                if (message.StartsWith("Presence authentication renewed", StringComparison.Ordinal)) Interlocked.Increment(ref renewals);
            }, timeProvider: clock, nextRandom: () => 0.5, createCall: transport.Open, recoveryStateStore: store);
        var run = agent.RunAsync(stopping.Token);
        try
        {
            for (var expected = 1; expected <= 25; expected++)
            {
                await WaitUntilAsync(() => clock.HasTimer(TimeSpan.FromSeconds(5)));
                clock.Advance(TimeSpan.FromSeconds(5));
                await WaitUntilAsync(() => Volatile.Read(ref renewals) == expected);
            }
            transport.First!.HeartbeatWrites.Should().Be(1);
            store.Read()!.HasOutage.Should().BeTrue();
        }
        finally
        {
            stopping.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(2));
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task UnexpectedTokenProgrammingFault_RemainsVisibleToTheNativeHost()
    {
        var tokens = new DelegateTokenService(_ => throw new InvalidOperationException("unexpected worker defect"));
        var agent = new AgentGatewayPresenceClient(new GatewayClientOptions { Endpoint = "https://gateway.test" },
            tokens, 7, Guid.NewGuid(), "test", [], _ => { });
        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("renewal-rejected")]
    [InlineData("renewed-heartbeat-rejected")]
    public async Task RejectedRenewalAuthority_InvalidatesExactFreshTokenAndReauthenticatesOnce(string failure)
    {
        var clock = new GatewayPresenceTestClock();
        var tokens = new ClockTokenService(clock, TimeSpan.FromSeconds(65));
        var transport = new ScriptedTransport(failure, supportsRenewal: true, clock);
        using var stopping = new CancellationTokenSource();
        var agent = new AgentGatewayPresenceClient(new GatewayClientOptions { Endpoint = "https://gateway.test" },
            tokens, 7, Guid.NewGuid(), "test", [], _ => { }, timeProvider: clock, nextRandom: () => 0.5,
            createCall: transport.Open);
        var run = agent.RunAsync(stopping.Token);
        try
        {
            await WaitUntilAsync(() => clock.HasTimer(TimeSpan.FromSeconds(5)));
            clock.Advance(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => clock.HasTimer(TimeSpan.FromSeconds(1.5)));
            tokens.Invalidations.Should().Equal("token-2");
            tokens.Requests.Should().Be(2);
            transport.First!.Disposed.Should().BeTrue();
            clock.Advance(TimeSpan.FromSeconds(1.5));
            await transport.Replaced.Task.WaitAsync(TimeSpan.FromSeconds(2));
            tokens.Requests.Should().Be(3, "the single owner makes one coalesced fresh-auth attempt after rejection");
            transport.Attempts.Should().Be(2);
        }
        finally { stopping.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    [Fact]
    public async Task RenewalAuthenticationAndAck_ShareOneThirtySecondFiniteBudget()
    {
        var clock = new GatewayPresenceTestClock();
        var requests = 0;
        var tokens = new DelegateTokenService(async token =>
        {
            var request = Interlocked.Increment(ref requests);
            if (request > 1) await Task.Delay(TimeSpan.FromSeconds(20), clock, token);
            return ($"token-{request}", clock.GetUtcNow().AddSeconds(65));
        });
        var transport = new ScriptedTransport("renewal-ack", supportsRenewal: true, clock,
            heartbeatInterval: 300, heartbeatTimeout: 3060);
        using var stopping = new CancellationTokenSource();
        var agent = new AgentGatewayPresenceClient(new GatewayClientOptions { Endpoint = "https://gateway.test" },
            tokens, 7, Guid.NewGuid(), "test", [], _ => { }, timeProvider: clock, nextRandom: () => 0.5,
            createCall: transport.Open);
        var run = agent.RunAsync(stopping.Token);
        try
        {
            await WaitUntilAsync(() => clock.HasTimer(TimeSpan.FromSeconds(5)));
            clock.Advance(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => clock.HasTimer(TimeSpan.FromSeconds(20)));
            clock.Advance(TimeSpan.FromSeconds(20));
            await transport.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => clock.HasTimer(TimeSpan.FromSeconds(10)));
            clock.Advance(TimeSpan.FromSeconds(10));
            await WaitUntilAsync(() => transport.First!.Disposed && clock.HasTimer(TimeSpan.FromSeconds(1.5)));
            transport.First!.ActiveReads.Should().Be(0);
            transport.First.ActiveWrites.Should().Be(0);
            transport.Attempts.Should().Be(1);
            Volatile.Read(ref requests).Should().Be(2);
            run.IsCompleted.Should().BeFalse();
        }
        finally { stopping.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    [Theory]
    [InlineData(120u, 365u)]
    [InlineData(300u, 3060u)]
    public async Task EstablishedServerHeartbeatPolicyRanges_RemainAdmissible(uint interval, uint timeout)
    {
        var clock = new GatewayPresenceTestClock();
        var transport = new ScriptedTransport("healthy", heartbeatInterval: interval, heartbeatTimeout: timeout);
        using var stopping = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new AgentGatewayPresenceClient(new GatewayClientOptions { Endpoint = "https://gateway.test" },
            new ClockTokenService(clock, TimeSpan.FromHours(1)), 7, Guid.NewGuid(), "test", [], _ => { },
            runForPresenceSession: (_, _, token) =>
            {
                ready.TrySetResult();
                return Task.Delay(Timeout.InfiniteTimeSpan, token);
            }, timeProvider: clock, nextRandom: () => 0.5, createCall: transport.Open);
        var run = agent.RunAsync(stopping.Token);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(2));
            transport.First!.HeartbeatWrites.Should().Be(1);
            transport.Attempts.Should().Be(1);
        }
        finally
        {
            stopping.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Theory]
    [InlineData(0u, 10u)]
    [InlineData(301u, 3060u)]
    [InlineData(300u, 3061u)]
    [InlineData(120u, 119u)]
    public async Task MalformedHeartbeatPolicy_IsRejectedBeforeOptionalChildren(uint interval, uint timeout)
    {
        var clock = new GatewayPresenceTestClock();
        var transport = new ScriptedTransport("healthy", heartbeatInterval: interval, heartbeatTimeout: timeout);
        using var stopping = new CancellationTokenSource();
        var rejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = CreateAgent(clock, transport, message =>
        {
            if (message.Contains("invalid heartbeat policy", StringComparison.Ordinal)) rejected.TrySetResult();
        });
        var run = agent.RunAsync(stopping.Token);
        try
        {
            await rejected.Task.WaitAsync(TimeSpan.FromSeconds(2));
            transport.First!.HeartbeatWrites.Should().Be(0);
            transport.First.Disposed.Should().BeTrue();
            transport.Attempts.Should().Be(1);
        }
        finally
        {
            stopping.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Theory]
    [InlineData("hello-write")]
    [InlineData("admission")]
    [InlineData("heartbeat-write")]
    [InlineData("first-ack")]
    [InlineData("later-ack")]
    public async Task BlackHoledIo_RetiresOnlyItsOwnedCallBeforeOneReplacement(string blockedState)
    {
        var clock = new GatewayPresenceTestClock();
        var transport = new ScriptedTransport(blockedState);
        using var stopping = new CancellationTokenSource();
        var failure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = CreateAgent(clock, transport, message =>
        {
            if (message.StartsWith("Gateway session failed", StringComparison.Ordinal)) failure.TrySetResult();
        });
        var run = agent.RunAsync(stopping.Token);
        try
        {
            if (blockedState == "later-ack")
            {
                await WaitUntilAsync(() => clock.HasTimer(TimeSpan.FromSeconds(5)));
                clock.Advance(TimeSpan.FromSeconds(5));
            }
            await transport.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var budget = blockedState is "hello-write" or "admission" ? 3 : 10;
            await WaitUntilAsync(() => clock.HasTimer(TimeSpan.FromSeconds(budget)));
            clock.Advance(TimeSpan.FromSeconds(budget));
            await failure.Task.WaitAsync(TimeSpan.FromSeconds(2));
            transport.Attempts.Should().Be(1);
            transport.First!.Disposed.Should().BeTrue();
            transport.First.ActiveReads.Should().Be(0);
            await WaitUntilAsync(() => clock.HasTimer(TimeSpan.FromSeconds(1.5)));
            clock.Advance(TimeSpan.FromSeconds(1.5));
            await transport.Replaced.Task.WaitAsync(TimeSpan.FromSeconds(2));
            transport.Attempts.Should().Be(2);
            transport.ReplacementSawRetiredOwner.Should().BeTrue();
        }
        finally
        {
            stopping.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task RenewalDeadline_RetiresStalledAckBeforeItsWatchdogWithoutACompetingReader()
    {
        var clock = new GatewayPresenceTestClock();
        var transport = new ScriptedTransport("first-ack");
        using var stopping = new CancellationTokenSource();
        var renewalBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = CreateAgent(clock, transport, message =>
        {
            if (message.StartsWith("Token renewal became due", StringComparison.Ordinal)) renewalBlocked.TrySetResult();
        }, lifetime: TimeSpan.FromSeconds(62));
        var run = agent.RunAsync(stopping.Token);
        try
        {
            await transport.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => clock.HasTimer(TimeSpan.FromSeconds(2)));
            clock.Advance(TimeSpan.FromSeconds(2));
            await renewalBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => clock.HasTimer(TimeSpan.FromSeconds(5)));
            clock.Advance(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => transport.First!.Disposed);
            transport.First!.MaximumActiveReads.Should().Be(1);
            transport.First.HeartbeatWrites.Should().Be(1);
            transport.Attempts.Should().Be(1, "only the owning recovery loop may start the replacement after its delay");
        }
        finally
        {
            stopping.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Theory]
    [InlineData("hello-write")]
    [InlineData("admission")]
    [InlineData("heartbeat-write")]
    [InlineData("first-ack")]
    public async Task ShutdownDuringBlockedIo_AbortsAndObservesWithoutStartingReplacement(string blockedState)
    {
        var clock = new GatewayPresenceTestClock();
        var transport = new ScriptedTransport(blockedState);
        using var stopping = new CancellationTokenSource();
        var logs = new List<string>();
        var run = CreateAgent(clock, transport, logs.Add).RunAsync(stopping.Token);
        await transport.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        stopping.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        transport.Attempts.Should().Be(1);
        transport.First!.Disposed.Should().BeTrue();
        transport.First.ActiveReads.Should().Be(0);
        transport.First.ActiveWrites.Should().Be(0);
        logs.Should().NotContain(message => message.StartsWith("Gateway session failed", StringComparison.Ordinal),
            "shutdown cancellation winning the renewal race is not an unplanned disconnect");
    }

    [Fact]
    public async Task ShutdownDuringRetryDelay_CancelsTheSingleRecoveryOwner()
    {
        var clock = new GatewayPresenceTestClock();
        var transport = new ScriptedTransport("admission");
        using var stopping = new CancellationTokenSource();
        var run = CreateAgent(clock, transport, _ => { }).RunAsync(stopping.Token);
        await transport.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => clock.HasTimer(TimeSpan.FromSeconds(3)));
        clock.Advance(TimeSpan.FromSeconds(3));
        await WaitUntilAsync(() => clock.HasTimer(TimeSpan.FromSeconds(1.5)));
        stopping.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromMinutes(1));
        transport.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task HealthyAckArrivingAfterRenewalTimer_PreservesOwnerAndRenewsWithoutCompetingReader()
    {
        var clock = new GatewayPresenceTestClock();
        var transport = new ScriptedTransport("later-ack", supportsRenewal: true, clock);
        using var stopping = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var due = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logs = new List<string>();
        var agent = new AgentGatewayPresenceClient(new GatewayClientOptions { Endpoint = "https://gateway.test" },
            new ClockTokenService(clock, TimeSpan.FromSeconds(67)), 7, Guid.NewGuid(), "test", [], message =>
            {
                logs.Add(message);
                if (message.StartsWith("Token renewal became due", StringComparison.Ordinal)) due.TrySetResult();
                if (message.StartsWith("Presence authentication renewed", StringComparison.Ordinal)) renewed.TrySetResult();
            }, runForPresenceSession: (_, _, token) =>
            {
                ready.TrySetResult();
                return Task.Delay(Timeout.InfiniteTimeSpan, token);
            }, timeProvider: clock, nextRandom: () => 0.5, createCall: transport.Open);
        var run = agent.RunAsync(stopping.Token);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => clock.HasTimer(TimeSpan.FromSeconds(5)));
            clock.Advance(TimeSpan.FromSeconds(5));
            await transport.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
            clock.Advance(TimeSpan.FromSeconds(2));
            await due.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => clock.HasTimer(TimeSpan.FromSeconds(5)));
            transport.First!.ReleaseHeartbeatAck();
            await renewed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            transport.First.Disposed.Should().BeFalse();
            transport.First.MaximumActiveReads.Should().Be(1);
            transport.Attempts.Should().Be(1);
            logs.Should().NotContain(message => message.StartsWith("Gateway session failed", StringComparison.Ordinal));
        }
        finally
        {
            stopping.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(TimeSpan.TicksPerSecond)]
    public async Task EarlyRenewalTimer_RechecksAbsoluteDueTimeWithoutRequestingCachedAuthority(long setbackTicks)
    {
        var clock = new GatewayPresenceTestClock();
        var transport = new ScriptedTransport("healthy", supportsRenewal: true, clock);
        var tokens = new ClockTokenService(clock, TimeSpan.FromSeconds(65));
        using var stopping = new CancellationTokenSource();
        GatewayPresenceSession? session = null;
        var renewed = 0;
        var agent = new AgentGatewayPresenceClient(new GatewayClientOptions { Endpoint = "https://gateway.test" },
            tokens, 7, Guid.NewGuid(), "test", [], message =>
            {
                if (message.StartsWith("Presence authentication renewed", StringComparison.Ordinal))
                    Interlocked.Increment(ref renewed);
            }, runForPresenceSession: (owner, _, token) =>
            {
                session = owner;
                return Task.Delay(Timeout.InfiniteTimeSpan, token);
            }, timeProvider: clock, nextRandom: () => 0.5, createCall: transport.Open);
        var run = agent.RunAsync(stopping.Token);
        try
        {
            await WaitUntilAsync(() => session is not null && clock.HasTimer(TimeSpan.FromSeconds(5)));
            clock.AdjustUtc(TimeSpan.FromTicks(-setbackTicks));
            clock.Advance(TimeSpan.FromSeconds(5));
            var remaining = TimeSpan.FromMilliseconds(Math.Ceiling(TimeSpan.FromTicks(setbackTicks).TotalMilliseconds));
            await WaitUntilAsync(() => clock.HasTimer(remaining));
            tokens.Requests.Should().Be(1, "a due timer cannot override the credential's absolute refresh boundary");
            Volatile.Read(ref renewed).Should().Be(0);
            session!.GetAccessToken("fallback").Should().Be("token-1");
            transport.First!.Disposed.Should().BeFalse();
            clock.Advance(remaining);
            await WaitUntilAsync(() => Volatile.Read(ref renewed) == 1);
            tokens.Requests.Should().Be(2);
            transport.Attempts.Should().Be(1);
            session.GetAccessToken("fallback").Should().Be("token-2");
        }
        finally
        {
            stopping.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task NegotiatedRenewal_TwicePreservesOwnerAndPublishesOnlyAcknowledgedCredentials()
    {
        var clock = new GatewayPresenceTestClock();
        var transport = new ScriptedTransport("healthy", supportsRenewal: true, clock);
        using var stopping = new CancellationTokenSource();
        GatewayPresenceSession? session = null;
        var starts = 0;
        var renewed = 0;
        var agent = new AgentGatewayPresenceClient(
            new GatewayClientOptions { Endpoint = "https://gateway.test" },
            new ClockTokenService(clock, TimeSpan.FromSeconds(65)), 7, Guid.NewGuid(), "test", [],
            message => { if (message.StartsWith("Presence authentication renewed", StringComparison.Ordinal)) Interlocked.Increment(ref renewed); },
            runForPresenceSession: (current, _, token) =>
            {
                session = current;
                Interlocked.Increment(ref starts);
                return Task.Delay(Timeout.InfiniteTimeSpan, token);
            }, timeProvider: clock, nextRandom: () => 0.5, createCall: transport.Open);
        var run = agent.RunAsync(stopping.Token);
        try
        {
            await WaitUntilAsync(() => session is not null && clock.HasTimer(TimeSpan.FromSeconds(5)));
            session!.GetAccessToken("fallback").Should().Be("token-1");
            clock.Advance(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => Volatile.Read(ref renewed) == 1 && clock.HasTimer(TimeSpan.FromSeconds(5)));
            session.GetAccessToken("fallback").Should().Be("token-2");
            clock.Advance(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => Volatile.Read(ref renewed) == 2);
            session.GetAccessToken("fallback").Should().Be("token-3");
            Volatile.Read(ref starts).Should().Be(1);
            transport.Attempts.Should().Be(1);
            transport.First!.Disposed.Should().BeFalse();
            transport.First.MaximumActiveReads.Should().Be(1);
        }
        finally
        {
            stopping.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task StalledRenewalAcknowledgement_DoesNotPublishUnconfirmedCredential()
    {
        var clock = new GatewayPresenceTestClock();
        var transport = new ScriptedTransport("renewal-ack", supportsRenewal: true, clock);
        using var stopping = new CancellationTokenSource();
        GatewayPresenceSession? session = null;
        var ownerToken = CancellationToken.None;
        var agent = new AgentGatewayPresenceClient(
            new GatewayClientOptions { Endpoint = "https://gateway.test" },
            new ClockTokenService(clock, TimeSpan.FromSeconds(65)), 7, Guid.NewGuid(), "test", [], _ => { },
            runForPresenceSession: (current, _, token) =>
            {
                session = current;
                ownerToken = token;
                return Task.Delay(Timeout.InfiniteTimeSpan, token);
            }, timeProvider: clock, nextRandom: () => 0.5, createCall: transport.Open);
        var run = agent.RunAsync(stopping.Token);
        try
        {
            await WaitUntilAsync(() => session is not null && clock.HasTimer(TimeSpan.FromSeconds(5)));
            clock.Advance(TimeSpan.FromSeconds(5));
            await transport.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
            session!.GetAccessToken("fallback").Should().Be("token-1");
            await WaitUntilAsync(() => clock.HasTimer(TimeSpan.FromSeconds(10)));
            clock.Advance(TimeSpan.FromSeconds(10));
            await WaitUntilAsync(() => transport.First!.Disposed && ownerToken.IsCancellationRequested);
            session.GetAccessToken("fallback").Should().Be("token-1");
            transport.First!.MaximumActiveReads.Should().Be(1);
            transport.Attempts.Should().Be(1);
        }
        finally
        {
            stopping.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private static AgentGatewayPresenceClient CreateAgent(GatewayPresenceTestClock clock, ScriptedTransport transport,
        Action<string> log, TimeSpan? lifetime = null) => new(
        new GatewayClientOptions { Endpoint = "https://gateway.test", PresenceBootstrapTimeoutSeconds = 3 },
        new ClockTokenService(clock, lifetime ?? TimeSpan.FromHours(1)), 7, Guid.NewGuid(), "test", [], log,
        timeProvider: clock, nextRandom: () => 0.5, createCall: transport.Open);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(1, timeout.Token);
    }

    private sealed class ClockTokenService(TimeProvider clock, TimeSpan lifetime) : IAgentTokenService
    {
        private int _requests;
        private string? _latestToken;
        internal List<string> Invalidations { get; } = [];
        internal int Requests => Volatile.Read(ref _requests);
        public Task<(string AccessToken, DateTimeOffset ExpiresAtUtc)> GetAccessTokenAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var token = $"token-{Interlocked.Increment(ref _requests)}";
            Volatile.Write(ref _latestToken, token);
            return Task.FromResult((token, clock.GetUtcNow() + lifetime));
        }
        public bool InvalidateAccessToken(string rejectedAccessToken)
        {
            if (Interlocked.CompareExchange(ref _latestToken, null, rejectedAccessToken) != rejectedAccessToken) return false;
            Invalidations.Add(rejectedAccessToken);
            return true;
        }
    }

    private sealed class DelegateTokenService(Func<CancellationToken, Task<(string, DateTimeOffset)>> acquire) : IAgentTokenService
    {
        public Task<(string AccessToken, DateTimeOffset ExpiresAtUtc)> GetAccessTokenAsync(CancellationToken ct) => acquire(ct);
    }

    private sealed class ScriptedTransport(string blockedState, bool supportsRenewal = false, TimeProvider? clock = null,
        uint heartbeatInterval = 5, uint heartbeatTimeout = 10)
    {
        private int _attempts;
        internal int Attempts => Volatile.Read(ref _attempts);
        internal TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Replaced { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ScriptedCall? First { get; private set; }
        internal bool ReplacementSawRetiredOwner { get; private set; }
        internal AsyncDuplexStreamingCall<AgentFrame, GatewayFrame> Open(Metadata _, CancellationToken token)
        {
            var attempt = Interlocked.Increment(ref _attempts);
            var scripted = new ScriptedCall(attempt == 1 ? blockedState : "healthy", token, Blocked, supportsRenewal, clock, heartbeatInterval, heartbeatTimeout);
            if (attempt == 1) First = scripted;
            else
            {
                ReplacementSawRetiredOwner = First!.Disposed && First.ActiveReads == 0 && First.ActiveWrites == 0;
                Replaced.TrySetResult();
            }
            return new AsyncDuplexStreamingCall<AgentFrame, GatewayFrame>(scripted, scripted,
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), scripted.Dispose);
        }
    }

    private sealed class ScriptedCall : IClientStreamWriter<AgentFrame>, IAsyncStreamReader<GatewayFrame>
    {
        private readonly string _blockedState;
        private readonly TaskCompletionSource _blocked;
        private readonly CancellationTokenSource _stopping;
        private readonly Channel<GatewayFrame> _responses = Channel.CreateUnbounded<GatewayFrame>();
        private readonly Guid _connectionId = Guid.NewGuid();
        private readonly bool _supportsRenewal;
        private readonly TimeProvider _clock;
        private readonly uint _heartbeatInterval;
        private readonly uint _heartbeatTimeout;
        private int _activeReads;
        private int _activeWrites;
        private int _heartbeatWrites;
        private GatewayFrame? _pendingHeartbeatAck;
        internal void ReleaseHeartbeatAck() => _responses.Writer.TryWrite(_pendingHeartbeatAck!);
        internal int ActiveReads => Volatile.Read(ref _activeReads);
        internal int ActiveWrites => Volatile.Read(ref _activeWrites);
        internal int HeartbeatWrites => Volatile.Read(ref _heartbeatWrites);
        internal int MaximumActiveReads { get; private set; }
        internal bool Disposed { get; private set; }
        public WriteOptions? WriteOptions { get; set; }
        public GatewayFrame Current { get; private set; } = new();
        internal ScriptedCall(string blockedState, CancellationToken token, TaskCompletionSource blocked, bool supportsRenewal, TimeProvider? clock,
            uint heartbeatInterval, uint heartbeatTimeout)
        {
            _blockedState = blockedState;
            _blocked = blocked;
            _supportsRenewal = supportsRenewal;
            _clock = clock ?? TimeProvider.System;
            _heartbeatInterval = heartbeatInterval;
            _heartbeatTimeout = heartbeatTimeout;
            _stopping = CancellationTokenSource.CreateLinkedTokenSource(token);
        }

        public Task WriteAsync(AgentFrame message) => WriteAsync(message, _stopping.Token);
        public async Task WriteAsync(AgentFrame frame, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _activeWrites);
            try
            {
                using var writeStopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
                if ((_blockedState == "hello-write" && frame.Hello is not null) ||
                    (_blockedState == "heartbeat-write" && frame.Heartbeat is not null))
                {
                    _blocked.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, writeStopping.Token);
                    return;
                }
                if (frame.Hello is not null && _blockedState == "admission")
                {
                    _blocked.TrySetResult();
                    return;
                }
                var response = new GatewayFrame
                {
                    ProtocolVersion = frame.ProtocolVersion, TenantId = frame.TenantId, ClientId = frame.ClientId,
                    ConnectionId = _connectionId.ToString("D"), ConnectionEpoch = 1,
                    OperationId = frame.OperationId, Sequence = frame.Sequence
                };
                if (frame.Hello is not null)
                    response.Connected = new ConnectAccepted { HeartbeatIntervalSeconds = _heartbeatInterval, HeartbeatTimeoutSeconds = _heartbeatTimeout, PresenceAuthority = "akka", SupportsAuthenticatedRenewal = _supportsRenewal };
                else if (frame.Renew is not null)
                {
                    if (_blockedState == "renewal-rejected")
                        throw new RpcException(new Status(StatusCode.Unauthenticated, "Rejected proposed authority."));
                    if (_blockedState == "renewal-ack")
                    {
                        _blocked.TrySetResult();
                        return;
                    }
                    response.Renewed = new PresenceAuthRenewed { PresenceAuthority = "akka", ExpiresAtUtc = Timestamp.FromDateTimeOffset(_clock.GetUtcNow().AddSeconds(65)) };
                }
                else
                {
                    var count = Interlocked.Increment(ref _heartbeatWrites);
                    if (_blockedState == "renewed-heartbeat-rejected" && count > 1)
                        throw new RpcException(new Status(StatusCode.Unauthenticated, "Rejected confirmed authority."));
                    if (_blockedState == "first-ack" || (_blockedState == "later-ack" && count > 1))
                    {
                        response.HeartbeatAccepted = new HeartbeatAccepted { PresenceAuthority = "akka" };
                        _pendingHeartbeatAck = response;
                        _blocked.TrySetResult();
                        return;
                    }
                    response.HeartbeatAccepted = new HeartbeatAccepted { PresenceAuthority = "akka" };
                }
                await _responses.Writer.WriteAsync(response, writeStopping.Token);
            }
            finally { Interlocked.Decrement(ref _activeWrites); }
        }
        public Task CompleteAsync() => Task.CompletedTask;
        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            var readers = Interlocked.Increment(ref _activeReads);
            MaximumActiveReads = Math.Max(MaximumActiveReads, readers);
            try
            {
                using var readStopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
                Current = await _responses.Reader.ReadAsync(readStopping.Token);
                return true;
            }
            finally { Interlocked.Decrement(ref _activeReads); }
        }
        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            _stopping.Cancel();
            _stopping.Dispose();
        }
    }
}
