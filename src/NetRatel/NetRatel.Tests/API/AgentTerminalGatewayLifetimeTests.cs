using System.Security.Claims;
using System.Threading.Channels;
using FluentAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.API.Gateway;
using NetRatel.Application.Presence;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AgentTerminalGatewayLifetimeTests
{
    [Fact]
    public async Task Replacement_CancelsThePhysicalWriteAndJoinsTheHandler()
    {
        var fixture = new Fixture(respectWriteCancellation: true);
        using var host = await BuildHostAsync(fixture);
        using var channel = GrpcChannel.ForAddress("http://localhost",
            new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
        using var call = new AgentTerminalGateway.AgentTerminalGatewayClient(channel).Connect();
        try
        {
            await AdmitAndBlockWriteAsync(call, fixture);
            fixture.Registry.Replace();
            await fixture.HandlerFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
            fixture.WriteCancellation.IsCancellationRequested.Should().BeTrue("replacement must reach actual I/O");
            fixture.WriterFinished.Task.IsCompleted.Should().BeTrue();
            fixture.Registry.WrittenCallbacks.Should().Be(0);
            fixture.Registry.SuccessorCurrent.Should().BeTrue();
        }
        finally
        {
            fixture.ReleaseWrite.TrySetResult();
        }
    }

    [Fact]
    public async Task NonCooperativeWrite_IsAbortedWithinTheJoinBoundAndCannotReportLateDelivery()
    {
        var fixture = new Fixture(respectWriteCancellation: false);
        using var host = await BuildHostAsync(fixture);
        using var channel = GrpcChannel.ForAddress("http://localhost",
            new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
        using var call = new AgentTerminalGateway.AgentTerminalGatewayClient(channel).Connect();
        try
        {
            await AdmitAndBlockWriteAsync(call, fixture);
            fixture.Registry.Replace();
            await fixture.HandlerFinished.Task.WaitAsync(TimeSpan.FromSeconds(7));
            fixture.Failure.Should().BeOfType<RpcException>().Which.StatusCode.Should().Be(StatusCode.DeadlineExceeded);
            fixture.WriteCancellation.IsCancellationRequested.Should().BeTrue();
            fixture.Registry.SuccessorCurrent.Should().BeTrue();
            fixture.ReleaseWrite.TrySetResult();
            await fixture.WriterFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
            fixture.Registry.WrittenCallbacks.Should().Be(0,"a retired handler cannot acknowledge a late physical write");
        }
        finally
        {
            fixture.ReleaseWrite.TrySetResult();
        }
    }

    private static async Task AdmitAndBlockWriteAsync(
        AsyncDuplexStreamingCall<AgentTerminalFrame, GatewayTerminalFrame> call, Fixture fixture)
    {
        await call.RequestStream.WriteAsync(new AgentTerminalFrame
        {
            ProtocolVersion = "1.0", TenantId = fixture.Client.TenantId,
            ClientId = fixture.Client.AgentId.ToString("D"), ConnectionId = fixture.ConnectionId.ToString("D"),
            ConnectionEpoch = 1, Sequence = 0,
            Hello = new AgentTerminalHello
            {
                TerminalCapability = new TerminalCapability { Supported = true, AvailableShells = { "sh" } }
            }
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        (await call.ResponseStream.MoveNext(deadline.Token)).Should().BeTrue();
        call.ResponseStream.Current.PayloadCase.Should().Be(GatewayTerminalFrame.PayloadOneofCase.Accepted);
        fixture.Registry.Outbound.Writer.TryWrite(new GatewayTerminalFrame
        {
            Sequence = 1, Start = new TerminalSessionStart { SessionId = "bounded-writer", Generation = 1 }
        }).Should().BeTrue();
        await fixture.WriteBlocked.Task.WaitAsync(deadline.Token);
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
                services.AddSingleton<IClientPresenceRouter>(new CurrentPresenceRouter(fixture));
                services.AddSingleton<IAgentTerminalSessionRegistry>(fixture.Registry);
                services.AddSingleton<AgentTerminalGatewayService>();
            });
            web.Configure(app =>
            {
                app.Use(async (context, next) =>
                {
                    var agentId = fixture.Client.AgentId.ToString("D");
                    context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("role", "agent"), new Claim("sub", agentId), new Claim("agent_id", agentId),
                        new Claim("tenant_id", fixture.Client.TenantId.ToString()), new Claim("scope", "netratel:connect")],
                        "TerminalLifetimeTest"));
                    await next(context);
                });
                app.UseRouting();
                app.UseEndpoints(endpoints => endpoints.MapGrpcService<BlockingTerminalGateway>());
            });
        });
        return await builder.StartAsync();
    }

    private sealed class Fixture(bool respectWriteCancellation)
    {
        public ClientKey Client { get; } = new(73, Guid.NewGuid());
        public Guid ConnectionId { get; } = Guid.NewGuid();
        public RegistrationRegistry Registry { get; } = new();
        public bool RespectWriteCancellation { get; } = respectWriteCancellation;
        public TaskCompletionSource WriteBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WriterFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HandlerFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken WriteCancellation { get; set; }
        public Exception? Failure { get; set; }
    }

    private sealed class BlockingTerminalGateway(AgentTerminalGatewayService gateway, Fixture fixture)
        : AgentTerminalGateway.AgentTerminalGatewayBase
    {
        public override async Task Connect(IAsyncStreamReader<AgentTerminalFrame> requests,
            IServerStreamWriter<GatewayTerminalFrame> responses, ServerCallContext context)
        {
            try { await gateway.Connect(requests, new BlockingWriter(responses, fixture), context); }
            catch (Exception exception) { fixture.Failure = exception; throw; }
            finally { fixture.HandlerFinished.TrySetResult(); }
        }
    }

    private sealed class BlockingWriter(IServerStreamWriter<GatewayTerminalFrame> inner, Fixture fixture)
        : IServerStreamWriter<GatewayTerminalFrame>
    {
        public WriteOptions? WriteOptions { get => inner.WriteOptions; set => inner.WriteOptions = value; }
        public Task WriteAsync(GatewayTerminalFrame frame) => WriteAsync(frame, CancellationToken.None);
        public async Task WriteAsync(GatewayTerminalFrame frame, CancellationToken cancellationToken)
        {
            if (frame.PayloadCase == GatewayTerminalFrame.PayloadOneofCase.Accepted)
            {
                await inner.WriteAsync(frame, cancellationToken);
                return;
            }
            fixture.WriteCancellation = cancellationToken;
            fixture.WriteBlocked.TrySetResult();
            try
            {
                if (fixture.RespectWriteCancellation)
                    await fixture.ReleaseWrite.Task.WaitAsync(cancellationToken);
                else
                    await fixture.ReleaseWrite.Task;
            }
            finally { fixture.WriterFinished.TrySetResult(); }
        }
    }

    private sealed class RegistrationRegistry : IAgentTerminalSessionRegistry
    {
        private readonly CancellationTokenSource _completion = new();
        private bool _current = true;
        public Channel<GatewayTerminalFrame> Outbound { get; } = Channel.CreateBounded<GatewayTerminalFrame>(4);
        public int WrittenCallbacks { get; private set; }
        public bool SuccessorCurrent { get; private set; }
        public void Replace() { _current = false; SuccessorCurrent = true; _completion.Cancel(); }
        public AgentTerminalGatewayRegistration Register(ClientKey client, Guid connectionId, ulong connectionEpoch,
            IReadOnlyList<string> availableShells, IReadOnlyList<string>? capabilities = null) =>
            new(Outbound.Reader, () => { _current = false; _completion.Cancel(); },
                _ => WrittenCallbacks++, () => _current, _completion.Token, Guid.NewGuid());
        public GatewayTerminalAvailability? GetAvailability(ClientKey client) => throw new NotSupportedException();
        public Task<GatewayTerminalSession> OpenAsync(ClientKey client, string shellType, string? workingDirectory, int columns, int rows, CancellationToken ct) => throw new NotSupportedException();
        public Task SendInputAsync(string sessionId, ulong generation, ReadOnlyMemory<byte> content, CancellationToken ct) => throw new NotSupportedException();
        public Task ResizeAsync(string sessionId, ulong generation, int columns, int rows, CancellationToken ct) => throw new NotSupportedException();
        public Task CloseAsync(string sessionId, ulong generation, string reason, CancellationToken ct) => throw new NotSupportedException();
        public GatewayTerminalOutputSubscription Subscribe(string sessionId, ulong generation) => throw new NotSupportedException();
        public GatewayTerminalSession? Get(string sessionId) => throw new NotSupportedException();
        public bool TryReceiveOpened(ClientKey client, TerminalSessionOpened opened) => throw new NotSupportedException();
        public Task<bool> TryReceiveOutputAsync(ClientKey client, TerminalOutput output, CancellationToken ct) => throw new NotSupportedException();
        public bool TryReceiveResizeApplied(ClientKey client, TerminalResizeApplied resize) => throw new NotSupportedException();
        public bool TryReceiveClosed(ClientKey client, TerminalSessionClosed closed) => throw new NotSupportedException();
        public bool TryReceiveFailed(ClientKey client, TerminalSessionFailed failed) => throw new NotSupportedException();
    }

    private sealed class CurrentPresenceRouter(Fixture fixture) : IClientPresenceRouter
    {
        public Task<ClientPresenceSnapshot> GetSnapshotAsync(ClientKey client, CancellationToken cancellationToken) =>
            Task.FromResult(new ClientPresenceSnapshot(client, ClientPresenceStatus.Online, 1, fixture.ConnectionId,
                0, DateTimeOffset.UtcNow, "test", [], null, "akka", true));
        public Task<GatewayPresenceSessionStarted> StartSessionAsync(StartGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> RecordHeartbeatAsync(RecordGatewayHeartbeat message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> EndSessionAsync(EndGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientPresenceRouteStatus> ProbeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
