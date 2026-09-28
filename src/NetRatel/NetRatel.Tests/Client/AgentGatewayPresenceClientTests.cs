using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
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
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class AgentGatewayPresenceClientTests
{
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

    [Fact]
    public async Task RunAsync_InvalidatesHeartbeatReadinessWhenSessionDisconnectsBeforeReconnect()
    {
        var gateway = new RefreshGatewayService(disconnectAfterFirstHeartbeat: true);
        using var host = await BuildHostAsync(gateway);
        using var stopping = new CancellationTokenSource();
        var stages = new ConcurrentQueue<string>();
        var extensionCleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var extensionCleanupRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnectedDuringExtensionCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondHeartbeatReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeatReadyCount = 0;
        var sessionToken = CancellationToken.None;
        var agent = new AgentGatewayPresenceClient(
            new GatewayClientOptions { Endpoint = "https://gateway.test" },
            new ExpiringTokenService(),
            tenantId: 7,
            agentId: Guid.NewGuid(),
            agentVersion: "0.5.6-test",
            terminalShells: [],
            log: _ => { },
            runForPresenceSession: (_, _, token) =>
            {
                sessionToken = token;
                return HoldExtensionUntilReleasedAsync(token, extensionCleanupStarted, extensionCleanupRelease);
            },
            createChannel: _ => GrpcChannel.ForAddress("http://localhost",
                new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() }),
            extensionShutdownTimeout: TimeSpan.FromSeconds(10),
            reportReadiness: (stage, _, _, _, _) =>
            {
                stages.Enqueue(stage);
                if (stage == "heartbeat_ready" && Interlocked.Increment(ref heartbeatReadyCount) == 2)
                    secondHeartbeatReady.TrySetResult();
                if (stage == "disconnected" && !sessionToken.IsCancellationRequested)
                    disconnectedDuringExtensionCleanup.TrySetResult();
            });

        var run = agent.RunAsync(stopping.Token);
        try
        {
            await disconnectedDuringExtensionCleanup.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await extensionCleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            sessionToken.IsCancellationRequested.Should().BeTrue();
            run.IsCompleted.Should().BeFalse("extension cleanup remains in progress after readiness was invalidated");
            extensionCleanupRelease.TrySetResult();
            await gateway.SecondHeartbeatAcknowledged.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await secondHeartbeatReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stages.ToArray().Should().ContainInOrder("admitted", "heartbeat_ready", "disconnected", "admitted", "heartbeat_ready");
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
        var gateway = new RefreshGatewayService();
        using var host = await BuildHostAsync(gateway);
        using var stopping = new CancellationTokenSource();
        var tokenService = new DisabledDuringRefreshTokenService();
        var logs = new ConcurrentQueue<string>();
        var agent = new AgentGatewayPresenceClient(
            new GatewayClientOptions { Endpoint = "https://gateway.test" },
            tokenService, 7, Guid.NewGuid(), "0.5.6-test", [], logs.Enqueue,
            createChannel: _ => GrpcChannel.ForAddress("http://localhost",
                new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() }));
        var run = agent.RunAsync(stopping.Token);
        try
        {
            await tokenService.DisabledResponse.Task.WaitAsync(TimeSpan.FromSeconds(5));
            run.IsCompleted.Should().BeFalse();
            tokenService.Enable();
            await gateway.SecondAdmission.Task.WaitAsync(TimeSpan.FromSeconds(10));
            run.IsCompleted.Should().BeFalse();
            logs.Should().Contain(message => message.Contains("Waiting to retry authentication", StringComparison.Ordinal));
        }
        finally
        {
            stopping.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class DisabledDuringRefreshTokenService : IAgentTokenService
    {
        private int _attempts;
        private int _enabled;
        public TaskCompletionSource DisabledResponse { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Enable() => Volatile.Write(ref _enabled, 1);
        public Task<(string AccessToken, DateTimeOffset ExpiresAtUtc)> GetAccessTokenAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _attempts) == 1)
                return Task.FromResult(("initial-token", DateTimeOffset.UtcNow));
            if (Volatile.Read(ref _enabled) == 0)
            {
                DisabledResponse.TrySetResult();
                throw new AgentClientAuthException("Agent disabled.", 403, code: "agent_disabled");
            }
            return Task.FromResult(("enabled-token", DateTimeOffset.UtcNow.AddHours(1)));
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
            return Task.FromResult(("test-token", DateTimeOffset.UtcNow));
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

    private sealed class RefreshGatewayService : global::NetRatel.AgentGateway.Contracts.V1.AgentGateway.AgentGatewayBase
    {
        private readonly string _presenceAuthority;
        private readonly bool _disconnectAfterFirstHeartbeat;
        private int _connectionEpoch;
        private int _disconnectIssued;

        public RefreshGatewayService(string presenceAuthority = "akka", bool disconnectAfterFirstHeartbeat = false)
        {
            _presenceAuthority = presenceAuthority;
            _disconnectAfterFirstHeartbeat = disconnectAfterFirstHeartbeat;
        }

        public TaskCompletionSource SecondAdmission { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondHeartbeatAcknowledged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task Connect(
            IAsyncStreamReader<AgentFrame> requestStream,
            IServerStreamWriter<GatewayFrame> responseStream,
            ServerCallContext context)
        {
            if (!await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false)) return;

            var hello = requestStream.Current;
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

                    await responseStream.WriteAsync(new GatewayFrame
                    {
                        ProtocolVersion = frame.ProtocolVersion,
                        TenantId = frame.TenantId,
                        ClientId = frame.ClientId,
                        ConnectionEpoch = connectionEpoch,
                        ConnectionId = connectionId.ToString("D"),
                        OperationId = frame.OperationId,
                        Sequence = frame.Sequence,
                        HeartbeatAccepted = new HeartbeatAccepted
                        {
                            PresenceAuthority = _presenceAuthority
                        }
                    }).ConfigureAwait(false);

                    if (connectionEpoch == 1 && _disconnectAfterFirstHeartbeat &&
                        Interlocked.Exchange(ref _disconnectIssued, 1) == 0)
                    {
                        return;
                    }

                    if (connectionEpoch >= 2) SecondHeartbeatAcknowledged.TrySetResult();
                }
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
