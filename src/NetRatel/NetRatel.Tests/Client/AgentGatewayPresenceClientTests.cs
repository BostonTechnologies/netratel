using System.Collections.Concurrent;
using FluentAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Application.ClientAuth;
using NetRatel.Client.Service.Gateway;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class AgentGatewayPresenceClientTests
{
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
            Endpoint = "https://gateway.test",
            RequiredPresenceAuthority = GatewayAuthority.Akka
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

    [Fact]
    public async Task RunAsync_DisabledDuringRefresh_ReAdmitsAfterEnableWithoutRestart()
    {
        var gateway = new RefreshGatewayService();
        using var host = await BuildHostAsync(gateway);
        using var stopping = new CancellationTokenSource();
        var tokenService = new DisabledDuringRefreshTokenService();
        var logs = new ConcurrentQueue<string>();
        var agent = new AgentGatewayPresenceClient(
            new GatewayClientOptions { Endpoint = "https://gateway.test", RequiredPresenceAuthority = GatewayAuthority.Akka },
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

    private sealed class RefreshGatewayService : global::NetRatel.AgentGateway.Contracts.V1.AgentGateway.AgentGatewayBase
    {
        private int _connectionEpoch;

        public TaskCompletionSource SecondAdmission { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
                    PresenceAuthority = GatewayAuthority.Akka
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
                            PresenceAuthority = GatewayAuthority.Akka
                        }
                    }).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
