using System.Security.Claims;
using System.Threading.Channels;
using FluentAssertions;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Akka.Configuration;
using NetRatel.Akka.RemoteSupport;
using NetRatel.API.Gateway;
using NetRatel.Application.Agents;
using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts.RemoteSupport;
using Xunit;

namespace NetRatel.Tests.API;

/// <summary>
/// Exercises the real authenticated gRPC services and their registries as an
/// agent would. Endpoint tests cover HTTP routing separately; these tests
/// prove that the agent stream drives the live file and signalling transports.
/// </summary>
public sealed class AgentCapabilityGatewayEndToEndTests
{
    [Fact]
    public async Task FileGateway_StreamsFencedListReadAndWriteOperationsWithCredits()
    {
        const int tenantId = 91;
        var agentId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        const ulong epoch = 3;
        using var host = await BuildHostAsync(tenantId, agentId, connectionId, epoch);
        using var channel = CreateChannel(host);
        var client = new AgentFileGateway.AgentFileGatewayClient(channel);
        using var call = client.Connect();

        await call.RequestStream.WriteAsync(FileFrame(tenantId, agentId, connectionId, epoch, 0,
            frame => frame.Hello = new AgentFileHello { Capabilities = { "file-list", "file-read", "file-write", "file-stat-v1", "file-create-directory-v1", "file-delete-v1", "file-copy-v1", "file-move-v1", "file-policy-roots-v1", "chunk-sha256" } }));
        (await call.ResponseStream.MoveNext(CancellationToken.None)).Should().BeTrue();
        call.ResponseStream.Current.Accepted.FileAuthority.Should().Be("akka");

        var registry = host.Services.GetRequiredService<IAgentFileGatewaySessionRegistry>();
        var clientKey = new ClientKey(tenantId, agentId);

        var listing = registry.ListAsync(clientKey, "/tmp", 32, CancellationToken.None);
        var listDispatch = await NextFileServerFrameAsync(call);
        listDispatch.Dispatch.Operation.Should().Be("list");
        await call.RequestStream.WriteAsync(FileFrame(tenantId, agentId, connectionId, epoch, 1,
            frame => frame.RequestAccepted = Accepted(listDispatch.Dispatch)));
        await call.RequestStream.WriteAsync(FileFrame(tenantId, agentId, connectionId, epoch, 2,
            frame => frame.ListPage = new FileListPage
            {
                RequestId = listDispatch.Dispatch.RequestId,
                AttemptId = listDispatch.Dispatch.AttemptId,
                PageIndex = 0,
                IsLastPage = true,
                Entries = { new FileEntry { Name = "gateway.txt", FullPath = "/tmp/gateway.txt", SizeBytes = 9 } }
            }));
        await call.RequestStream.WriteAsync(FileFrame(tenantId, agentId, connectionId, epoch, 3,
            frame => frame.Completed = Completed(listDispatch.Dispatch)));
        (await listing).Should().ContainSingle(entry => entry.FullPath == "/tmp/gateway.txt");

        var statTask = registry.StatAsync(clientKey, "/tmp/gateway.txt", CancellationToken.None);
        var statDispatch = await NextFileServerFrameAsync(call);
        statDispatch.Dispatch.Operation.Should().Be("stat");
        await call.RequestStream.WriteAsync(FileFrame(tenantId, agentId, connectionId, epoch, 4,
            frame => frame.RequestAccepted = Accepted(statDispatch.Dispatch)));
        await call.RequestStream.WriteAsync(FileFrame(tenantId, agentId, connectionId, epoch, 5,
            frame => frame.Metadata = new FileMetadata
            {
                RequestId = statDispatch.Dispatch.RequestId,
                AttemptId = statDispatch.Dispatch.AttemptId,
                FullPath = "/tmp/gateway.txt",
                SizeBytes = 9,
                LastModifiedUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc)),
                MimeType = "text/plain; charset=utf-8"
            }));
        await call.RequestStream.WriteAsync(FileFrame(tenantId, agentId, connectionId, epoch, 6,
            frame => frame.Completed = Completed(statDispatch.Dispatch)));
        var stat = await statTask;
        stat.Should().BeEquivalentTo(new GatewayFileMetadata(
            "/tmp/gateway.txt",
            false,
            9,
            new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero),
            "text/plain; charset=utf-8"));

        var createDirectory = registry.CreateDirectoryAsync(clientKey, "/tmp/gateway-export", CancellationToken.None);
        var createDirectoryDispatch = await NextFileServerFrameAsync(call);
        createDirectoryDispatch.Dispatch.Operation.Should().Be("create_directory");
        await call.RequestStream.WriteAsync(FileFrame(tenantId, agentId, connectionId, epoch, 7,
            frame => frame.RequestAccepted = Accepted(createDirectoryDispatch.Dispatch)));
        await call.RequestStream.WriteAsync(FileFrame(tenantId, agentId, connectionId, epoch, 8,
            frame => frame.Completed = Completed(createDirectoryDispatch.Dispatch)));
        await createDirectory;

        var delete = registry.DeleteAsync(clientKey, "/tmp/gateway-export", CancellationToken.None);
        var deleteDispatch = await NextFileServerFrameAsync(call);
        deleteDispatch.Dispatch.Operation.Should().Be("delete");
        await call.RequestStream.WriteAsync(FileFrame(tenantId, agentId, connectionId, epoch, 9,
            frame => frame.RequestAccepted = Accepted(deleteDispatch.Dispatch)));
        await call.RequestStream.WriteAsync(FileFrame(tenantId, agentId, connectionId, epoch, 10,
            frame => frame.Completed = Completed(deleteDispatch.Dispatch)));
        await delete;

        var accessPolicy = new GatewayFileMoveCopyAccessPolicy(["/tmp"], ["/tmp"]);
        var copy = registry.CopyAsync(clientKey, "/tmp/gateway.txt", "/tmp/gateway-copy.txt", accessPolicy, CancellationToken.None);
        var copyDispatch = await NextFileServerFrameAsync(call);
        copyDispatch.Dispatch.Operation.Should().Be("copy");
        copyDispatch.Dispatch.Path.Should().Be("/tmp/gateway.txt");
        copyDispatch.Dispatch.DestinationPath.Should().Be("/tmp/gateway-copy.txt");
        copyDispatch.Dispatch.SourceAllowedRoots.Should().Equal("/tmp");
        copyDispatch.Dispatch.DestinationAllowedRoots.Should().Equal("/tmp");
        await call.RequestStream.WriteAsync(FileFrame(tenantId, agentId, connectionId, epoch, 11,
            frame => frame.RequestAccepted = Accepted(copyDispatch.Dispatch)));
        await call.RequestStream.WriteAsync(FileFrame(tenantId, agentId, connectionId, epoch, 12,
            frame => frame.Completed = Completed(copyDispatch.Dispatch)));
        await copy;

        var move = registry.MoveAsync(clientKey, "/tmp/gateway-copy.txt", "/tmp/gateway-moved.txt", accessPolicy, CancellationToken.None);
        var moveDispatch = await NextFileServerFrameAsync(call);
        moveDispatch.Dispatch.Operation.Should().Be("move");
        moveDispatch.Dispatch.Path.Should().Be("/tmp/gateway-copy.txt");
        moveDispatch.Dispatch.DestinationPath.Should().Be("/tmp/gateway-moved.txt");
        await call.RequestStream.WriteAsync(FileFrame(tenantId, agentId, connectionId, epoch, 13,
            frame => frame.RequestAccepted = Accepted(moveDispatch.Dispatch)));
        await call.RequestStream.WriteAsync(FileFrame(tenantId, agentId, connectionId, epoch, 14,
            frame => frame.Completed = Completed(moveDispatch.Dispatch)));
        await move;

        var readTask = registry.ReadAsync(clientKey, "/tmp/gateway.txt", CancellationToken.None);
        var readDispatch = await NextFileServerFrameAsync(call);
        readDispatch.Dispatch.Operation.Should().Be("read");
        await call.RequestStream.WriteAsync(FileFrame(tenantId, agentId, connectionId, epoch, 15,
            frame => frame.RequestAccepted = Accepted(readDispatch.Dispatch)));
        var initialCredit = await NextFileServerFrameAsync(call);
        initialCredit.Credit.AvailableBytes.Should().Be(FileTransferFrameBudget.PayloadBytes);
        var read = await readTask;
        var content = ByteString.CopyFromUtf8("read-through-gateway");
        await call.RequestStream.WriteAsync(FileFrame(tenantId, agentId, connectionId, epoch, 16,
            frame => frame.TransferChunk = TransferChunk(readDispatch.Dispatch, 0, content)));
        var returnedCredit = await NextFileServerFrameAsync(call);
        returnedCredit.Credit.AvailableBytes.Should().Be((uint)content.Length);
        await call.RequestStream.WriteAsync(FileFrame(tenantId, agentId, connectionId, epoch, 17,
            frame => frame.Completed = Completed(readDispatch.Dispatch)));
        (await read.Chunks.ReadAsync()).ToArray().Should().Equal(content.ToByteArray());
        await read.Completion;

        await using var upload = new MemoryStream(ByteString.CopyFromUtf8("write-through-gateway").ToByteArray());
        var write = registry.WriteAsync(clientKey, "/tmp/upload.txt", upload, CancellationToken.None);
        var writeDispatch = await NextFileServerFrameAsync(call);
        writeDispatch.Dispatch.Operation.Should().Be("write");
        await call.RequestStream.WriteAsync(FileFrame(tenantId, agentId, connectionId, epoch, 18,
            frame => frame.RequestAccepted = Accepted(writeDispatch.Dispatch)));
        var writeChunk = await NextFileServerFrameAsync(call);
        writeChunk.TransferChunk.Content.ToStringUtf8().Should().Be("write-through-gateway");
        writeChunk.TransferChunk.IsLastChunk.Should().BeFalse();
        var finalWriteChunk = await NextFileServerFrameAsync(call);
        finalWriteChunk.TransferChunk.IsLastChunk.Should().BeTrue();
        await call.RequestStream.WriteAsync(FileFrame(tenantId, agentId, connectionId, epoch, 19,
            frame => frame.Completed = Completed(writeDispatch.Dispatch)));
        await write;
    }

    [Fact]
    public async Task RemoteSupportGateway_RelaysFencedOrderedSignalsInBothDirections()
    {
        const int tenantId = 92;
        var agentId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        const ulong epoch = 4;
        using var host = await BuildHostAsync(tenantId, agentId, connectionId, epoch);
        using var channel = CreateChannel(host);
        var client = new AgentRemoteSupportGateway.AgentRemoteSupportGatewayClient(channel);
        using var call = client.Connect();

        await call.RequestStream.WriteAsync(RemoteFrame(tenantId, agentId, connectionId, epoch, 0,
            frame => frame.Hello = new AgentRemoteSupportHello { Capabilities = { "webrtc-signalling", "ice" } }));
        (await call.ResponseStream.MoveNext(CancellationToken.None)).Should().BeTrue();
        call.ResponseStream.Current.Accepted.SupportAuthority.Should().Be("akka");

        var registry = host.Services.GetRequiredService<IGatewayRemoteSupportSessionRegistry>();
        var clientKey = new ClientKey(tenantId, agentId);
        var opened = await registry.OpenAsync(clientKey, new OpenRemoteSupportRequest(), CancellationToken.None);
        var open = await NextRemoteServerFrameAsync(call);
        open.Signal.SessionId.Should().Be(opened.SessionId);
        open.Signal.SignalType.Should().Be("open");

        using var subscription = registry.Subscribe(opened.SessionId);
        await call.RequestStream.WriteAsync(RemoteFrame(tenantId, agentId, connectionId, epoch, 1,
            frame => frame.Signal = new RemoteSupportSignal
            {
                SessionId = opened.SessionId,
                MessageId = Guid.NewGuid().ToString("N"),
                SignalType = RemoteSupportSignalTypes.Ready,
                SessionSequence = 1,
                Payload = ByteString.CopyFromUtf8("{\"agent\":\"ready\"}")
            }));
        var agentReady = await subscription.Reader.ReadAsync();
        agentReady.SignalType.Should().Be(RemoteSupportSignalTypes.Ready);
        agentReady.Direction.Should().Be("agent");

        await registry.SendBrowserSignalAsync(opened.SessionId,
            new RemoteSupportSignalRequest(RemoteSupportSignalTypes.Offer, "{\"type\":\"offer\"}"), CancellationToken.None);
        var offer = await NextRemoteServerFrameAsync(call);
        offer.Signal.SessionId.Should().Be(opened.SessionId);
        offer.Signal.SignalType.Should().Be(RemoteSupportSignalTypes.Offer);
        // The opening request is the first browser-to-agent envelope, so the
        // subsequent offer is monotonically sequenced as two.
        offer.Signal.SessionSequence.Should().Be(2);

        await registry.CloseAsync(opened.SessionId, "operator_finished", CancellationToken.None);
        var close = await NextRemoteServerFrameAsync(call);
        close.Closed.SessionId.Should().Be(opened.SessionId);
        close.Closed.Reason.Should().Be("operator_finished");
    }

    [Fact]
    public async Task RemoteSupportV2Gateway_RegistersTheLiveEdgeAndWritesOnlyTheTransientRouteEnvelope()
    {
        const int tenantId = 93;
        var agentId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        const ulong epoch = 5;
        using var host = await BuildHostAsync(tenantId, agentId, connectionId, epoch, replicaSafe: true);
        using var channel = CreateChannel(host);
        var client = new AgentRemoteSupportGateway.AgentRemoteSupportGatewayClient(channel);
        using var call = client.Connect();
        var supportSession = Guid.NewGuid();

        await call.RequestStream.WriteAsync(RemoteFrame(tenantId, agentId, connectionId, epoch, 0,
            frame => frame.Hello = new AgentRemoteSupportHello { Capabilities = { "webrtc-signalling" } }));
        (await call.ResponseStream.MoveNext(CancellationToken.None)).Should().BeTrue();
        call.ResponseStream.Current.Accepted.SupportAuthority.Should().Be("akka");

        await call.RequestStream.WriteAsync(RemoteFrame(tenantId, agentId, connectionId, epoch, 1,
            frame => frame.V2EdgeRegistration = new RemoteSupportV2AgentEdgeRegistration
            {
                Session = new RemoteSupportV2SessionKey
                {
                    TenantId = tenantId,
                    AgentId = agentId.ToString("D"),
                    RemoteSupportSessionId = supportSession.ToString("D")
                },
                RouteGeneration = 1
            }));

        var edges = host.Services.GetRequiredService<TestV2EdgeRegistry>();
        await edges.WaitForRegistrationAsync();
        await edges.SendAsync(new RemoteSupportAgentRouteEnvelope(
            new RemoteSupportSessionKey(tenantId, agentId, supportSession),
            edges.EdgeRouteId,
            1,
            "renegotiation_required",
            []));

        var envelope = await NextRemoteServerFrameAsync(call);
        envelope.PayloadCase.Should().Be(GatewayRemoteSupportFrame.PayloadOneofCase.V2Envelope);
        envelope.V2Envelope.Kind.Should().Be("renegotiation_required");
        envelope.V2Envelope.Session.RemoteSupportSessionId.Should().Be(supportSession.ToString("D"));
        envelope.V2Envelope.Payload.Should().BeEmpty();

        await call.RequestStream.CompleteAsync();
    }

    private static GrpcChannel CreateChannel(IHost host) => GrpcChannel.ForAddress(
        "http://localhost",
        new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });

    private static async Task<GatewayFileFrame> NextFileServerFrameAsync(AsyncDuplexStreamingCall<AgentFileFrame, GatewayFileFrame> call)
    {
        (await call.ResponseStream.MoveNext(CancellationToken.None)).Should().BeTrue();
        return call.ResponseStream.Current;
    }

    private static async Task<GatewayRemoteSupportFrame> NextRemoteServerFrameAsync(AsyncDuplexStreamingCall<AgentRemoteSupportFrame, GatewayRemoteSupportFrame> call)
    {
        (await call.ResponseStream.MoveNext(CancellationToken.None)).Should().BeTrue();
        return call.ResponseStream.Current;
    }

    private static AgentFileFrame FileFrame(int tenantId, Guid agentId, Guid connectionId, ulong epoch, ulong sequence, Action<AgentFileFrame> populate)
    {
        var frame = new AgentFileFrame
        {
            ProtocolVersion = "1.0",
            TenantId = tenantId,
            ClientId = agentId.ToString("D"),
            ConnectionId = connectionId.ToString("D"),
            ConnectionEpoch = epoch,
            Sequence = sequence
        };
        populate(frame);
        return frame;
    }

    private static AgentRemoteSupportFrame RemoteFrame(int tenantId, Guid agentId, Guid connectionId, ulong epoch, ulong sequence, Action<AgentRemoteSupportFrame> populate)
    {
        var frame = new AgentRemoteSupportFrame
        {
            ProtocolVersion = "1.0",
            TenantId = tenantId,
            ClientId = agentId.ToString("D"),
            ConnectionId = connectionId.ToString("D"),
            ConnectionEpoch = epoch,
            Sequence = sequence
        };
        populate(frame);
        return frame;
    }

    private static FileRequestAccepted Accepted(FileRequestDispatch dispatch) => new()
    {
        RequestId = dispatch.RequestId,
        AttemptId = dispatch.AttemptId
    };

    private static FileRequestCompleted Completed(FileRequestDispatch dispatch) => new()
    {
        RequestId = dispatch.RequestId,
        AttemptId = dispatch.AttemptId
    };

    private static FileTransferChunk TransferChunk(FileRequestDispatch dispatch, ulong chunkIndex, ByteString content) => new()
    {
        RequestId = dispatch.RequestId,
        AttemptId = dispatch.AttemptId,
        ChunkIndex = chunkIndex,
        Content = content,
        Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content.Span)).ToLowerInvariant()
    };

    private static async Task<IHost> BuildHostAsync(int tenantId, Guid agentId, Guid connectionId, ulong epoch, bool replicaSafe = false)
    {
        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddAuthorization(options => options.AddPolicy("AgentGatewayAccess", policy =>
                    policy.RequireAssertion(context => AgentGatewayIdentityResolver.TryResolve(context.User, out _, out _))));
                services.AddGrpc();
                services.AddSingleton<IClientPresenceRouter>(new CurrentPresenceRouter(tenantId, agentId, connectionId, epoch));
                services.AddSingleton<IAgentManagementService>(new ActiveAgentManagementService(tenantId, agentId));
                services.AddSingleton<IAgentFileGatewaySessionRegistry, AgentFileGatewaySessionRegistry>();
                if (replicaSafe)
                {
                    services.AddSingleton<TestV2EdgeRegistry>();
                    services.AddSingleton<IRemoteSupportV2AgentEdgeRegistry>(serviceProvider =>
                        serviceProvider.GetRequiredService<TestV2EdgeRegistry>());
                }
                else
                {
                    services.AddSingleton<IGatewayRemoteSupportSessionRegistry, GatewayRemoteSupportSessionRegistry>();
                }
                services.AddSingleton(TimeProvider.System);
                services.AddSingleton(new NetRatelAkkaMigrationOptions
                {
                    Enabled = true,
                    PresenceEnabled = true,
                    GatewayEnabled = true,
                    PresenceAuthorityEnabled = true,
                    FileGatewayEnabled = true,
                    FileBrowseAuthorityEnabled = true,
                    RemoteSupportGatewayEnabled = true,
                    RemoteSupportAuthorityEnabled = true,
                    RemoteSupportV2LifecycleAuthorityEnabled = replicaSafe,
                    RemoteSupportV2ReplicaSafeEdgeEnabled = replicaSafe
                });
            });
            web.Configure(app =>
            {
                app.Use(async (context, next) =>
                {
                    var id = agentId.ToString("D");
                    context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim("role", "agent"),
                        new Claim("sub", id),
                        new Claim("agent_id", id),
                        new Claim("tenant_id", tenantId.ToString()),
                        new Claim("scope", "netratel:connect")
                    ], "GatewayCapabilityE2e"));
                    await next(context);
                });
                app.UseRouting();
                app.UseAuthorization();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapGrpcService<AgentFileGatewayService>();
                    endpoints.MapGrpcService<AgentRemoteSupportGatewayService>();
                });
            });
        });
        return await builder.StartAsync();
    }

    private sealed class CurrentPresenceRouter(int tenantId, Guid agentId, Guid connectionId, ulong epoch) : IClientPresenceRouter
    {
        public Task<GatewayPresenceSessionStarted> StartSessionAsync(StartGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> RecordHeartbeatAsync(RecordGatewayHeartbeat message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> EndSessionAsync(EndGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientPresenceRouteStatus> ProbeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ClientPresenceSnapshot> GetSnapshotAsync(ClientKey client, CancellationToken cancellationToken) =>
            Task.FromResult(new ClientPresenceSnapshot(
                client,
                client == new ClientKey(tenantId, agentId) ? ShadowPresenceStatus.Online : ShadowPresenceStatus.Offline,
                checked((long)epoch), connectionId, 0, DateTimeOffset.UtcNow, "e2e-test", Array.Empty<string>(), null,
                "akka", true));
    }

    private sealed class TestV2EdgeRegistry : IRemoteSupportV2AgentEdgeRegistry
    {
        private readonly Channel<RemoteSupportAgentRouteEnvelope> _outbound = Channel.CreateBounded<RemoteSupportAgentRouteEnvelope>(32);
        private readonly TaskCompletionSource _registered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid EdgeRouteId { get; } = Guid.NewGuid();

        public RemoteSupportV2AgentEdgeConnection Register(ClientKey client, Guid connectionId, ulong connectionEpoch) => new(
            _outbound.Reader,
            (session, generation, _) =>
            {
                _registered.TrySetResult();
                return Task.FromResult(session.TenantId == client.TenantId && session.AgentId == client.AgentId && generation == 1);
            },
            _ => Task.CompletedTask,
            () => _outbound.Writer.TryComplete());

        public Task WaitForRegistrationAsync() => _registered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public ValueTask SendAsync(RemoteSupportAgentRouteEnvelope envelope) => _outbound.Writer.WriteAsync(envelope);
    }

    private sealed class ActiveAgentManagementService(int tenantId, Guid agentId) : IAgentManagementService
    {
        public Task<AgentDetailDto?> GetAsync(int requestedTenantId, Guid requestedAgentId, CancellationToken cancellationToken) =>
            Task.FromResult<AgentDetailDto?>(requestedTenantId == tenantId && requestedAgentId == agentId
                ? new AgentDetailDto(tenantId, agentId, "gateway-e2e", true, null, DateTimeOffset.UtcNow, "test", null, null, null)
                : null);

        public Task<AgentListResponse> ListAsync(int tenantId, AgentListQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DisableAsync(int tenantId, Guid agentId, string reason, string actor, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task EnableAsync(int tenantId, Guid agentId, string actor, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteAsync(int tenantId, Guid agentId, string reason, string actor, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
