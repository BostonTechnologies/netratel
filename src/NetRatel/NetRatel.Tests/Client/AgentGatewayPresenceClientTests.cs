using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Application.ClientAuth;
using NetRatel.Client;
using NetRatel.Client.Service.Gateway;
using NetRatel.Client.Service.Updates;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class AgentGatewayPresenceClientTests
{
    [Fact]
    public async Task RunAsync_TokenRenewalCannotBeSuppressedByAStalledFirstAcknowledgement()
    {
        var gateway = new RefreshGatewayService(holdFirstHeartbeatAcknowledgement: true);
        using var host = await BuildHostAsync(gateway);
        using var stopping = new CancellationTokenSource();
        var agent = new AgentGatewayPresenceClient(
            new GatewayClientOptions { Endpoint = "https://gateway.test" },
            new ExpiringTokenService(), 7, Guid.NewGuid(), "test", [], _ => { },
            createHttpHandler: _ => host.GetTestServer().CreateHandler());

        var run = agent.RunAsync(stopping.Token);
        try
        {
            await gateway.FirstHeartbeatReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await gateway.SecondAdmission.Task.WaitAsync(TimeSpan.FromSeconds(5));
            gateway.Hellos.Should().HaveCount(2, "renewal must retire a black-holed owner rather than wait indefinitely for its ACK");
        }
        finally
        {
            stopping.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task RunAsync_ReportsOnlyPreviousValidatedHeartbeatRoundTrip_InSamePresenceSession()
    {
        var gateway = new RefreshGatewayService();
        using var host = await BuildHostAsync(gateway);
        using var stopping = new CancellationTokenSource();
        var logs = new ConcurrentQueue<string>();
        var agentId = Guid.NewGuid();
        var agent = new AgentGatewayPresenceClient(
            new GatewayClientOptions { Endpoint = "https://gateway.test" },
            new StableTokenService(), 7, agentId, "test", [], logs.Enqueue,
            createHttpHandler: _ => host.GetTestServer().CreateHandler());

        var run = agent.RunAsync(stopping.Token);
        try
        {
            await gateway.SecondHeartbeatReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var frames = gateway.Heartbeats.ToArray();
            frames.Should().HaveCount(2);
            frames[0].Heartbeat.HasAcknowledgedHeartbeatRoundTripMs.Should().BeFalse();
            frames[0].Heartbeat.AcknowledgedHeartbeatSequence.Should().Be(0);
            frames[1].Heartbeat.HasAcknowledgedHeartbeatRoundTripMs.Should().BeTrue();
            frames[1].Heartbeat.AcknowledgedHeartbeatSequence.Should().Be(frames[0].Sequence);
            frames[1].Heartbeat.AcknowledgedHeartbeatRoundTripMs.Should().BeInRange(0, 10000);
            frames.Should().OnlyContain(frame => frame.ClientId == agentId.ToString("D") && frame.TenantId == 7);
            frames[0].ConnectionEpoch.Should().Be(frames[1].ConnectionEpoch);
            gateway.Hellos.Should().ContainSingle().Which.Hello.Capabilities.Should().Contain("heartbeat-latency");
            logs.Should().NotContain(message => message.StartsWith("Gateway session failed", StringComparison.Ordinal));
        }
        finally
        {
            stopping.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task RunAsync_CancellationBeforeHeartbeatAcknowledgement_CannotReportLatency()
    {
        var gateway = new RefreshGatewayService(holdFirstHeartbeatAcknowledgement: true);
        using var host = await BuildHostAsync(gateway);
        using var stopping = new CancellationTokenSource();
        var agent = new AgentGatewayPresenceClient(
            new GatewayClientOptions { Endpoint = "https://gateway.test" },
            new StableTokenService(), 7, Guid.NewGuid(), "test", [], _ => { },
            createHttpHandler: _ => host.GetTestServer().CreateHandler());

        var run = agent.RunAsync(stopping.Token);
        await gateway.FirstHeartbeatReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stopping.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        gateway.Heartbeats.Should().ContainSingle().Which.Heartbeat.HasAcknowledgedHeartbeatRoundTripMs.Should().BeFalse();
        gateway.SecondHeartbeatReceived.Task.IsCompleted.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_LegacyGatewaySelectorsCannotDisableAcceptedExtensions(bool? retiredBooleanValue)
    {
        var root = Path.Combine(Path.GetTempPath(), $"netratel-presence-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var retiredSettings = new[]
            {
                "TelemetryShadowEnabled", "TelemetryAuthorityEnabled", "CommandAuthorityEnabled", "JobAuthorityEnabled",
                "TerminalAuthorityEnabled", "ControlAuthorityEnabled", "FileAuthorityEnabled", "LogAuthorityEnabled",
                "RemoteSupportAuthorityEnabled", "RemoteSupportV1Enabled", "ControlGatewayEnabled", "FileGatewayEnabled",
                "LogGatewayEnabled", "RemoteSupportGatewayEnabled", "TerminalGatewayEnabled",
                "RemoteSupportV2InventoryEnabled", "RemoteSupportV2MediaEnabled"
            };
            var booleanProperties = retiredBooleanValue is null
                ? string.Empty
                : "," + string.Join(",", retiredSettings.Select(name => $"\"{name}\":{retiredBooleanValue.Value.ToString().ToLowerInvariant()}"));
            await File.WriteAllTextAsync(Path.Combine(root, "clientsettings.json"), $$"""
                {
                  "Client": { "ApiBaseUrl": "https://gateway.test" },
                  "Gateway": {
                    "ProtocolVersion": "1.0",
                    "TelemetryFastIntervalSeconds": 19,
                    "RequiredPresenceAuthority": "legacy-wire-label"{{booleanProperties}}
                  },
                  "Transport": { "Mode": "unsupported-old-mode" }
                }
                """);

            var packagedDefaults = ClientConfigurationLoader.BuildPackagedDefaults(
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../NetRatel.Client")), null);
            var effectiveConfiguration = ClientConfigurationLoader.BuildEffectiveConfiguration(
                packagedDefaults, new ConfigurationBuilder().Build(), root, []);
            var options = ClientConfigurationLoader.LoadGatewayOptions(
                effectiveConfiguration,
                "https://gateway.test",
                packagedDefaults,
                new ConfigurationBuilder().Build(),
                root,
                []);
            options.TelemetryFastIntervalSeconds.Should().Be(19);

            var gateway = new RefreshGatewayService();
            using var host = await BuildHostAsync(gateway);
            using var stopping = new CancellationTokenSource();
            var extensionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var agent = new AgentGatewayPresenceClient(
                options,
                new StableTokenService(),
                tenantId: 7,
                agentId: Guid.NewGuid(),
                agentVersion: "0.5.6-test",
                terminalShells: [],
                log: _ => { },
                runForPresenceSession: (_, _, _) =>
                {
                    extensionStarted.TrySetResult();
                    return Task.CompletedTask;
                },
                createChannel: _ => GrpcChannel.ForAddress("http://localhost",
                    new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() }));

            var run = agent.RunAsync(stopping.Token);
            try
            {
                await extensionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                options.Endpoint.Should().Be("https://gateway.test");
                run.IsCompleted.Should().BeFalse();
            }
            finally
            {
                stopping.Cancel();
                await run.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_ReAdmitsAfterTokenRefresh_WhenAnExtensionDoesNotStop()
    {
        var gateway = new RefreshGatewayService();
        using var host = await BuildHostAsync(gateway);
        using var stopping = new CancellationTokenSource();
        var tokenService = new ExpiringTokenService();
        var logs = new ConcurrentQueue<string>();
        var neverCompletingExtension = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new GatewayClientOptions
        {
            Endpoint = "https://gateway.test"
        };
        var agent = new AgentGatewayPresenceClient(
            options,
            tokenService,
            tenantId: 7,
            agentId: Guid.NewGuid(),
            agentVersion: "0.5.1-test",
            terminalShells: [],
            log: logs.Enqueue,
            runForPresenceSession: (_, _, _) => neverCompletingExtension.Task,
            createChannel: _ => GrpcChannel.ForAddress(
                "http://localhost",
                new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() }),
            extensionShutdownTimeout: TimeSpan.FromMilliseconds(50));

        var runTask = agent.RunAsync(stopping.Token);
        await gateway.SecondAdmission.Task.WaitAsync(TimeSpan.FromSeconds(5));

        tokenService.RequestCount.Should().BeGreaterThanOrEqualTo(2);
        logs.Should().Contain(message => message.Contains("did not stop within", StringComparison.Ordinal));

        stopping.Cancel();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("legacy-wire-label")]
    [InlineData("Akka")]
    [InlineData("")]
    public async Task RunAsync_RejectsUnexpectedPresenceAuthorityWireValue(string authority)
    {
        var gateway = new RefreshGatewayService(authority);
        using var host = await BuildHostAsync(gateway);
        using var stopping = new CancellationTokenSource();
        var logs = new ConcurrentQueue<string>();
        var rejectedFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var extensionStarts = 0;

        var agent = new AgentGatewayPresenceClient(
            new GatewayClientOptions { Endpoint = "https://gateway.test" },
            new ExpiringTokenService(),
            tenantId: 7,
            agentId: Guid.NewGuid(),
            agentVersion: "0.5.6-test",
            terminalShells: [],
            log: message =>
            {
                logs.Enqueue(message);
                if (message.Contains("unsupported presence authority token", StringComparison.Ordinal))
                    rejectedFrame.TrySetResult();
            },
            runForPresenceSession: (_, _, _) =>
            {
                Interlocked.Increment(ref extensionStarts);
                return Task.CompletedTask;
            },
            createChannel: _ => GrpcChannel.ForAddress(
                "http://localhost",
                new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() }));

        var run = agent.RunAsync(stopping.Token);
        try
        {
            await rejectedFrame.Task.WaitAsync(TimeSpan.FromSeconds(5));
            logs.Should().Contain(message => message.Contains("Gateway session failed", StringComparison.Ordinal));
            Volatile.Read(ref extensionStarts).Should().Be(0);
        }
        finally
        {
            stopping.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData("protocol")]
    [InlineData("tenant")]
    [InlineData("agent")]
    [InlineData("epoch")]
    [InlineData("connection")]
    [InlineData("operation")]
    [InlineData("sequence")]
    [InlineData("authority")]
    public async Task RunAsync_DoesNotAcceptActivationHeartbeatForMismatchedGatewayAcknowledgement(string mismatch)
    {
        var gateway = new RefreshGatewayService(heartbeatMismatch: mismatch);
        using var host = await BuildHostAsync(gateway);
        using var stopping = new CancellationTokenSource();
        var invalidAcknowledgement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updateHandler = new RecordingUpdateHandler();
        var extensionStarts = 0;
        var agent = new AgentGatewayPresenceClient(
            new GatewayClientOptions { Endpoint = "https://gateway.test" },
            new StableTokenService(),
            tenantId: 7,
            agentId: Guid.NewGuid(),
            agentVersion: "0.5.6-test",
            terminalShells: [],
            log: message =>
            {
                if (message.Contains("invalid heartbeat acknowledgement", StringComparison.Ordinal) ||
                    message.Contains("unsupported heartbeat authority token", StringComparison.Ordinal))
                    invalidAcknowledgement.TrySetResult();
            },
            runForPresenceSession: (_, _, _) =>
            {
                Interlocked.Increment(ref extensionStarts);
                return Task.CompletedTask;
            },
            updateHandler: updateHandler,
            createChannel: _ => GrpcChannel.ForAddress(
                "http://localhost",
                new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() }));

        var run = agent.RunAsync(stopping.Token);
        try
        {
            await gateway.FirstHeartbeatReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await invalidAcknowledgement.Task.WaitAsync(TimeSpan.FromSeconds(5));

            updateHandler.Events.Should().Contain(entry => entry.Event == "sent" && entry.Epoch == 1);
            updateHandler.Events.Should().NotContain(entry => entry.Event == "accepted");
            gateway.Heartbeats.Should().ContainSingle().Which.Heartbeat.HasAcknowledgedHeartbeatRoundTripMs.Should().BeFalse();
            gateway.SecondHeartbeatReceived.Task.IsCompleted.Should().BeFalse("an invalid ACK cannot produce a latency report");
            Volatile.Read(ref extensionStarts).Should().Be(0);
        }
        finally
        {
            stopping.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task RunAsync_CancelsExtensionsBeforeReconnectingAfterTransportDisconnect()
    {
        var gateway = new RefreshGatewayService(disconnectAfterFirstHeartbeat: true);
        using var host = await BuildHostAsync(gateway);
        using var stopping = new CancellationTokenSource();
        var extensionCleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var extensionCleanupRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondConnectionSecondHeartbeatAccepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessions = new ConcurrentQueue<GatewayPresenceSession>();
        var secondConnectionAcceptedCount = 0;
        var updateHandler = new RecordingUpdateHandler(epoch =>
        {
            if (epoch == 2 && Interlocked.Increment(ref secondConnectionAcceptedCount) == 2)
                secondConnectionSecondHeartbeatAccepted.TrySetResult();
        });
        var sessionToken = CancellationToken.None;
        var agent = new AgentGatewayPresenceClient(
            new GatewayClientOptions { Endpoint = "https://gateway.test" },
            new StableTokenService(),
            tenantId: 7,
            agentId: Guid.NewGuid(),
            agentVersion: "0.5.6-test",
            terminalShells: [],
            log: _ => { },
            runForPresenceSession: (session, _, token) =>
            {
                updateHandler.Events.Should().Contain(entry => entry.Event == "accepted" && entry.Epoch == session.ConnectionEpoch,
                    "updater activation heartbeat must be accepted before optional extensions start");
                sessions.Enqueue(session);
                sessionToken = token;
                return HoldExtensionUntilReleasedAsync(token, extensionCleanupStarted, extensionCleanupRelease);
            },
            updateHandler: updateHandler,
            createChannel: _ => GrpcChannel.ForAddress("http://localhost",
                new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() }),
            extensionShutdownTimeout: TimeSpan.FromSeconds(10));

        var run = agent.RunAsync(stopping.Token);
        try
        {
            await extensionCleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            sessionToken.IsCancellationRequested.Should().BeTrue();
            run.IsCompleted.Should().BeFalse("presence waits for bounded extension cleanup before reconnecting");
            gateway.SecondAdmission.Task.IsCompleted.Should().BeFalse();
            extensionCleanupRelease.TrySetResult();
            await secondConnectionSecondHeartbeatAccepted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            updateHandler.Events.Where(entry => entry.Event == "accepted").Select(entry => entry.Epoch).Should().Equal(1UL, 2UL, 2UL);
            var admittedSessions = sessions.ToArray();
            admittedSessions.Should().HaveCount(2);
            admittedSessions[0].ConnectionEpoch.Should().Be(1);
            admittedSessions[1].ConnectionEpoch.Should().Be(2);
            admittedSessions[0].ConnectionId.Should().NotBe(admittedSessions[1].ConnectionId);
            gateway.Heartbeats.Where(frame => frame.Sequence == 1).Should().HaveCount(2)
                .And.OnlyContain(frame => !frame.Heartbeat.HasAcknowledgedHeartbeatRoundTripMs,
                    "a reconnect resets the previous connection's latency sample");
        }
        finally
        {
            stopping.Cancel();
            extensionCleanupRelease.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task HoldExtensionUntilReleasedAsync(
        CancellationToken stoppingToken,
        TaskCompletionSource cleanupStarted,
        TaskCompletionSource cleanupRelease)
    {
        using var cancellationRegistration = stoppingToken.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(), cleanupStarted);
        await cleanupStarted.Task.ConfigureAwait(false);
        await cleanupRelease.Task.ConfigureAwait(false);
    }

    [Fact]
    public async Task RunAsync_RejectsH2cGatewayEndpoints()
    {
        var agent = new AgentGatewayPresenceClient(
            new GatewayClientOptions { Endpoint = "h2c://api:9223" },
            new ExpiringTokenService(),
            tenantId: 7,
            agentId: Guid.NewGuid(),
            agentVersion: "0.5.6-test",
            terminalShells: [],
            log: _ => { });

        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RunAsync_DisabledDuringRefresh_ReAdmitsAfterEnableWithoutRestart()
    {
        var clock = new GatewayPresenceTestClock();
        var gateway = new RefreshGatewayService();
        using var host = await BuildHostAsync(gateway);
        using var stopping = new CancellationTokenSource();
        var tokenService = new DisabledDuringRefreshTokenService(clock);
        var logs = new ConcurrentQueue<string>();
        var firstOnline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new AgentGatewayPresenceClient(
            new GatewayClientOptions { Endpoint = "https://gateway.test" },
            tokenService, 7, Guid.NewGuid(), "0.5.6-test", [], message =>
            {
                logs.Enqueue(message);
                if (message.StartsWith("Operational state=Online", StringComparison.Ordinal))
                    firstOnline.TrySetResult();
            },
            createChannel: _ => GrpcChannel.ForAddress("http://localhost",
                new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() }),
            timeProvider: clock, nextRandom: () => 0);
        var run = agent.RunAsync(stopping.Token);
        try
        {
            // Renewal is scheduled before the first heartbeat ACK. Advancing its
            // timer before readiness can instead exercise blocked admission I/O.
            await firstOnline.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => clock.HasTimer(TimeSpan.FromSeconds(1)));
            clock.Advance(TimeSpan.FromSeconds(1));
            // Match the retired session's recovery wait, rather than another
            // one-second heartbeat/renewal timer that has not been retired yet.
            await WaitUntilAsync(() => logs.Any(message => message.Contains("state=WaitingForBackend", StringComparison.Ordinal)) &&
                clock.HasTimer(TimeSpan.FromSeconds(1)));
            clock.Advance(TimeSpan.FromSeconds(1));
            await tokenService.DisabledResponse.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => clock.HasTimer(TimeSpan.FromSeconds(300)));
            run.IsCompleted.Should().BeFalse();
            tokenService.Enable();
            clock.Advance(TimeSpan.FromSeconds(300));
            await gateway.SecondAdmission.Task.WaitAsync(TimeSpan.FromSeconds(10));
            run.IsCompleted.Should().BeFalse();
            logs.Should().Contain(message => message.Contains("state=AuthenticationAttention", StringComparison.Ordinal));
        }
        finally
        {
            stopping.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(1, timeout.Token);
    }

    private sealed class DisabledDuringRefreshTokenService(TimeProvider clock) : IAgentTokenService
    {
        private int _attempts;
        private int _enabled;
        public TaskCompletionSource DisabledResponse { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Enable() => Volatile.Write(ref _enabled, 1);
        public Task<(string AccessToken, DateTimeOffset ExpiresAtUtc)> GetAccessTokenAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _attempts) == 1)
                return Task.FromResult(("initial-token", clock.GetUtcNow().AddSeconds(3)));
            if (Volatile.Read(ref _enabled) == 0)
            {
                DisabledResponse.TrySetResult();
                throw new AgentClientAuthException("Agent disabled.", 403, code: "agent_disabled");
            }
            return Task.FromResult(("enabled-token", clock.GetUtcNow().AddHours(1)));
        }
    }

    private static async Task<IHost> BuildHostAsync(RefreshGatewayService gateway)
    {
        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddGrpc();
                services.AddSingleton(gateway);
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(endpoints => endpoints.MapGrpcService<RefreshGatewayService>());
            });
        });
        return await builder.StartAsync();
    }

    private sealed class ExpiringTokenService : IAgentTokenService
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        public Task<(string AccessToken, DateTimeOffset ExpiresAtUtc)> GetAccessTokenAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _requestCount);
            return Task.FromResult(("test-token", DateTimeOffset.UtcNow.AddSeconds(3)));
        }
    }

    private sealed class StableTokenService : IAgentTokenService
    {
        public Task<(string AccessToken, DateTimeOffset ExpiresAtUtc)> GetAccessTokenAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(("test-token", DateTimeOffset.UtcNow.AddHours(1)));
        }
    }

    private sealed class RecordingUpdateHandler(Action<ulong>? onAccepted = null) : IAgentGatewayUpdateHandler
    {
        public ConcurrentQueue<(string Event, ulong Epoch)> Events { get; } = new();

        public void PopulateHello(ConnectHello hello) { }
        public void OnAcknowledgement(ClientUpdateOffer? offer, ClientUpdatePolicy? policy, UpdateActivationConfirmation? confirmation) { }
        public void OnPresenceConnected(ulong connectionEpoch) => Events.Enqueue(("connected", connectionEpoch));
        public void OnActivationHeartbeatSent(ulong connectionEpoch) => Events.Enqueue(("sent", connectionEpoch));
        public void OnActivationHeartbeatAccepted(ulong connectionEpoch)
        {
            Events.Enqueue(("accepted", connectionEpoch));
            onAccepted?.Invoke(connectionEpoch);
        }
        public void RecordAcknowledgementFailure(Exception exception) { }
    }

    private sealed class RefreshGatewayService : global::NetRatel.AgentGateway.Contracts.V1.AgentGateway.AgentGatewayBase
    {
        private readonly string _presenceAuthority;
        private readonly bool _disconnectAfterFirstHeartbeat;
        private readonly string? _heartbeatMismatch;
        private readonly bool _holdFirstHeartbeatAcknowledgement;
        private int _connectionEpoch;
        private int _disconnectIssued;

        public RefreshGatewayService(
            string presenceAuthority = "akka",
            bool disconnectAfterFirstHeartbeat = false,
            string? heartbeatMismatch = null,
            bool holdFirstHeartbeatAcknowledgement = false)
        {
            _presenceAuthority = presenceAuthority;
            _disconnectAfterFirstHeartbeat = disconnectAfterFirstHeartbeat;
            _heartbeatMismatch = heartbeatMismatch;
            _holdFirstHeartbeatAcknowledgement = holdFirstHeartbeatAcknowledgement;
        }

        public TaskCompletionSource SecondAdmission { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstHeartbeatReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondHeartbeatReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<AgentFrame> Heartbeats { get; } = new();
        public ConcurrentQueue<AgentFrame> Hellos { get; } = new();

        public override async Task Connect(
            IAsyncStreamReader<AgentFrame> requestStream,
            IServerStreamWriter<GatewayFrame> responseStream,
            ServerCallContext context)
        {
            if (!await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false)) return;

            var hello = requestStream.Current;
            Hellos.Enqueue(hello);
            var connectionEpoch = checked((ulong)Interlocked.Increment(ref _connectionEpoch));
            var connectionId = Guid.NewGuid();
            await responseStream.WriteAsync(new GatewayFrame
            {
                ProtocolVersion = hello.ProtocolVersion,
                TenantId = hello.TenantId,
                ClientId = hello.ClientId,
                ConnectionEpoch = connectionEpoch,
                ConnectionId = connectionId.ToString("D"),
                OperationId = hello.OperationId,
                Sequence = 0,
                Connected = new ConnectAccepted
                {
                    HeartbeatIntervalSeconds = 1,
                    HeartbeatTimeoutSeconds = 10,
                    PresenceAuthority = _presenceAuthority
                }
            }).ConfigureAwait(false);

            if (connectionEpoch >= 2) SecondAdmission.TrySetResult();

            try
            {
                while (await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
                {
                    var frame = requestStream.Current;
                    if (frame.PayloadCase is not AgentFrame.PayloadOneofCase.Heartbeat) continue;
                    Heartbeats.Enqueue(frame.Clone());
                    FirstHeartbeatReceived.TrySetResult();
                    if (Heartbeats.Count >= 2) SecondHeartbeatReceived.TrySetResult();
                    if (_holdFirstHeartbeatAcknowledgement)
                        await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken).ConfigureAwait(false);

                    await responseStream.WriteAsync(new GatewayFrame
                    {
                        ProtocolVersion = _heartbeatMismatch == "protocol" ? "unsupported" : frame.ProtocolVersion,
                        TenantId = _heartbeatMismatch == "tenant" ? frame.TenantId + 1 : frame.TenantId,
                        ClientId = _heartbeatMismatch == "agent" ? Guid.NewGuid().ToString("D") : frame.ClientId,
                        ConnectionEpoch = _heartbeatMismatch == "epoch" ? connectionEpoch + 1 : connectionEpoch,
                        ConnectionId = _heartbeatMismatch == "connection" ? Guid.NewGuid().ToString("D") : connectionId.ToString("D"),
                        OperationId = _heartbeatMismatch == "operation" ? Guid.NewGuid().ToString("D") : frame.OperationId,
                        Sequence = _heartbeatMismatch == "sequence" ? frame.Sequence + 1 : frame.Sequence,
                        HeartbeatAccepted = new HeartbeatAccepted
                        {
                            PresenceAuthority = _heartbeatMismatch == "authority" ? "unsupported" : _presenceAuthority
                        }
                    }).ConfigureAwait(false);

                    if (connectionEpoch == 1 && _disconnectAfterFirstHeartbeat &&
                        Interlocked.Exchange(ref _disconnectIssued, 1) == 0)
                    {
                        return;
                    }

                }
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
