using System.Security.Claims;
using FluentAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Akka.Configuration;
using NetRatel.API.Gateway;
using NetRatel.Application.Agents;
using NetRatel.Application.Presence;
using NetRatel.Tests.Client;
using Xunit;
using PresenceGrpcGateway = NetRatel.AgentGateway.Contracts.V1.AgentGateway;

namespace NetRatel.Tests.API;

public sealed class AgentGatewayLifetimeTests
{
    [Fact]
    public async Task InitialHelloStall_IsRetiredByTheBootstrapBudget()
    {
        var fixture = new Fixture();
        using var host = await BuildHostAsync(fixture);
        using var channel = CreateChannel(host);
        using var call = new PresenceGrpcGateway.AgentGatewayClient(channel).Connect();
        await fixture.ManagementStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Clock.Advance(TimeSpan.FromSeconds(31));
        await fixture.HandlerFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Router.Started.Should().BeNull();
        fixture.Router.Ended.Should().BeNull();
    }

    [Fact]
    public async Task MissingHeartbeat_RetiresTheMatchingPhysicalPresenceOwner()
    {
        var fixture = new Fixture();
        using var host = await BuildHostAsync(fixture);
        using var channel = CreateChannel(host);
        using var call = new PresenceGrpcGateway.AgentGatewayClient(channel).Connect();
        await AdmitAsync(call, fixture);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await fixture.HandlerFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Router.Ended.Should().NotBeNull();
        fixture.Router.Ended!.ConnectionId.Should().Be(fixture.Router.Started!.ConnectionId);
        fixture.Router.Ended.ConnectionEpoch.Should().Be(1);
        fixture.Router.Ended.Reason.Should().Be("heartbeat_expired");
    }

    [Fact]
    public async Task DuplicateHeartbeat_DoesNotExtendTheInactivityDeadline()
    {
        var fixture = new Fixture();
        using var host = await BuildHostAsync(fixture);
        using var channel = CreateChannel(host);
        using var call = new PresenceGrpcGateway.AgentGatewayClient(channel).Connect();
        var connected = await AdmitAsync(call, fixture);
        await HeartbeatAsync(call, fixture, connected, 1);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1.5));
        var duplicate = await HeartbeatAsync(call, fixture, connected, 1);
        duplicate.HeartbeatAccepted.Duplicate.Should().BeTrue();
        fixture.Clock.Advance(TimeSpan.FromSeconds(.5));
        await fixture.HandlerFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Router.Ended.Should().NotBeNull();
    }

    [Fact]
    public async Task AcceptedHeartbeats_RenewThePolicyWithoutAnOverallStreamDeadline()
    {
        var fixture = new Fixture();
        using var host = await BuildHostAsync(fixture);
        using var channel = CreateChannel(host);
        using var call = new PresenceGrpcGateway.AgentGatewayClient(channel).Connect();
        var connected = await AdmitAsync(call, fixture);
        for (ulong sequence = 1; sequence <= 4; sequence++)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(1.5));
            var accepted = await HeartbeatAsync(call, fixture, connected, sequence);
            accepted.ConnectionId.Should().Be(connected.ConnectionId);
            accepted.ConnectionEpoch.Should().Be(connected.ConnectionEpoch);
            fixture.HandlerFinished.Task.IsCompleted.Should().BeFalse();
        }
        await call.RequestStream.CompleteAsync();
        await fixture.HandlerFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Router.Ended!.Reason.Should().Be("stream_closed");
    }

    [Fact]
    public async Task BlockedAcknowledgement_CancelsActualIoAndRetiresTheOwner()
    {
        var fixture = new Fixture { BlockAcknowledgement = true };
        using var host = await BuildHostAsync(fixture);
        using var channel = CreateChannel(host);
        using var call = new PresenceGrpcGateway.AgentGatewayClient(channel).Connect();
        try
        {
            var connected = await AdmitAsync(call, fixture);
            await call.RequestStream.WriteAsync(Heartbeat(fixture, connected, 1));
            await fixture.WriteBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Clock.Advance(TimeSpan.FromSeconds(2));
            await fixture.HandlerFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
            fixture.WriteCancellation.IsCancellationRequested.Should().BeTrue();
            fixture.Router.Ended.Should().NotBeNull();
            fixture.WritesCompleted.Should().Be(1,"the admission is the only completed response");
        }
        finally { fixture.ReleaseWrite.TrySetResult(); }
    }

    private static GrpcChannel CreateChannel(IHost host) => GrpcChannel.ForAddress("http://localhost",
        new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });

    private static async Task<GatewayFrame> AdmitAsync(AsyncDuplexStreamingCall<AgentFrame, GatewayFrame> call, Fixture fixture)
    {
        await call.RequestStream.WriteAsync(new AgentFrame
        {
            ProtocolVersion = "1.0", TenantId = fixture.Client.TenantId,
            ClientId = fixture.Client.AgentId.ToString("D"), ConnectionId = Guid.NewGuid().ToString("D"),
            OperationId = Guid.NewGuid().ToString("D"), Hello = new ConnectHello { AgentVersion = "test" }
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        (await call.ResponseStream.MoveNext(deadline.Token)).Should().BeTrue();
        call.ResponseStream.Current.PayloadCase.Should().Be(GatewayFrame.PayloadOneofCase.Connected);
        return call.ResponseStream.Current;
    }

    private static AgentFrame Heartbeat(Fixture fixture, GatewayFrame connected, ulong sequence) => new()
    {
        ProtocolVersion = "1.0", TenantId = fixture.Client.TenantId, ClientId = fixture.Client.AgentId.ToString("D"),
        ConnectionId = connected.ConnectionId, ConnectionEpoch = connected.ConnectionEpoch,
        OperationId = Guid.NewGuid().ToString("D"), Sequence = sequence, Heartbeat = new PresenceHeartbeat()
    };

    private static async Task<GatewayFrame> HeartbeatAsync(AsyncDuplexStreamingCall<AgentFrame, GatewayFrame> call,
        Fixture fixture, GatewayFrame connected, ulong sequence)
    {
        await call.RequestStream.WriteAsync(Heartbeat(fixture, connected, sequence));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        (await call.ResponseStream.MoveNext(deadline.Token)).Should().BeTrue();
        return call.ResponseStream.Current;
    }

    private static async Task<IHost> BuildHostAsync(Fixture fixture)
    {
        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddGrpc();
                services.AddSingleton(fixture);
                services.AddSingleton(sp => new AgentGatewayService(fixture.Router, new ActiveAgentManagement(fixture),
                    null!, null!, new NetRatelAkkaOptions
                    {
                        HeartbeatIntervalSeconds = 1, MissedHeartbeatLimit = 2, HeartbeatGraceSeconds = 0
                    }, fixture.Clock, sp.GetRequiredService<IHostEnvironment>(), sp.GetRequiredService<ILogger<AgentGatewayService>>()));
            });
            web.Configure(app =>
            {
                app.Use(async (context, next) =>
                {
                    var id = fixture.Client.AgentId.ToString("D");
                    context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("role", "agent"), new Claim("sub", id), new Claim("agent_id", id),
                        new Claim("tenant_id", fixture.Client.TenantId.ToString()), new Claim("scope", "netratel:connect")],
                        "PresenceLifetimeTest"));
                    await next(context);
                });
                app.UseRouting();
                app.UseEndpoints(endpoints => endpoints.MapGrpcService<PresenceLifetimeGateway>());
            });
        });
        return await builder.StartAsync();
    }

    private sealed class Fixture
    {
        public ClientKey Client { get; } = new(73, Guid.NewGuid());
        public GatewayPresenceTestClock Clock { get; } = new();
        public RecordingPresenceRouter Router { get; } = new();
        public TaskCompletionSource ManagementStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HandlerFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WriteBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool BlockAcknowledgement { get; init; }
        public CancellationToken WriteCancellation { get; set; }
        public int WritesCompleted { get; set; }
    }

    private sealed class PresenceLifetimeGateway(AgentGatewayService gateway, Fixture fixture) : PresenceGrpcGateway.AgentGatewayBase
    {
        public override async Task Connect(IAsyncStreamReader<AgentFrame> requests,
            IServerStreamWriter<GatewayFrame> responses, ServerCallContext context)
        {
            try { await gateway.Connect(requests, new ControlledWriter(responses, fixture), context); }
            finally { fixture.HandlerFinished.TrySetResult(); }
        }
    }

    private sealed class ControlledWriter(IServerStreamWriter<GatewayFrame> inner, Fixture fixture) : IServerStreamWriter<GatewayFrame>
    {
        public WriteOptions? WriteOptions { get => inner.WriteOptions; set => inner.WriteOptions = value; }
        public Task WriteAsync(GatewayFrame frame) => WriteAsync(frame, CancellationToken.None);
        public async Task WriteAsync(GatewayFrame frame, CancellationToken cancellationToken)
        {
            if (fixture.BlockAcknowledgement && frame.PayloadCase == GatewayFrame.PayloadOneofCase.HeartbeatAccepted)
            {
                fixture.WriteCancellation = cancellationToken;
                fixture.WriteBlocked.TrySetResult();
                await fixture.ReleaseWrite.Task.WaitAsync(cancellationToken);
            }
            await inner.WriteAsync(frame, cancellationToken);
            fixture.WritesCompleted++;
        }
    }

    private sealed class RecordingPresenceRouter : IClientPresenceRouter
    {
        private ulong _lastSequence;
        public StartGatewayPresenceSession? Started { get; private set; }
        public EndGatewayPresenceSession? Ended { get; private set; }
        public Task<GatewayPresenceSessionStarted> StartSessionAsync(StartGatewayPresenceSession message, CancellationToken cancellationToken)
        {
            Started = message;
            return Task.FromResult(new GatewayPresenceSessionStarted(message.Client, message.ConnectionId, 1,
                PresenceMessageDisposition.Accepted, message.ReceivedAtUtc));
        }
        public Task<PresenceMessageResult> RecordHeartbeatAsync(RecordGatewayHeartbeat message, CancellationToken cancellationToken)
        {
            var duplicate = message.Sequence == _lastSequence;
            _lastSequence = message.Sequence;
            return Task.FromResult(new PresenceMessageResult(message.Client, 1,
                duplicate ? PresenceMessageDisposition.Duplicate : PresenceMessageDisposition.Accepted, _lastSequence));
        }
        public Task<PresenceMessageResult> EndSessionAsync(EndGatewayPresenceSession message, CancellationToken cancellationToken)
        {
            Ended = message;
            return Task.FromResult(new PresenceMessageResult(message.Client, 1, PresenceMessageDisposition.Accepted, _lastSequence));
        }
        public Task<ClientPresenceSnapshot> GetSnapshotAsync(ClientKey client, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientPresenceRouteStatus> ProbeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ActiveAgentManagement(Fixture fixture) : IAgentManagementService
    {
        public Task<AgentDetailDto?> GetAsync(int tenantId, Guid agentId, CancellationToken ct)
        {
            fixture.ManagementStarted.TrySetResult();
            return Task.FromResult<AgentDetailDto?>(new AgentDetailDto(tenantId, agentId, "test", true, null,
                DateTimeOffset.UtcNow, "test", null, null, null));
        }
        public Task<AgentListResponse> ListAsync(int tenantId, AgentListQuery query, CancellationToken ct) => throw new NotSupportedException();
        public Task DisableAsync(int tenantId, Guid agentId, string reason, string actor, CancellationToken ct) => throw new NotSupportedException();
        public Task EnableAsync(int tenantId, Guid agentId, string actor, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(int tenantId, Guid agentId, string reason, string actor, CancellationToken ct) => throw new NotSupportedException();
    }
}
