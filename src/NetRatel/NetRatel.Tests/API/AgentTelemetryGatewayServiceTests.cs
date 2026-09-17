using System.Security.Claims;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Akka.Configuration;
using NetRatel.API.Gateway;
using NetRatel.API.Realtime;
using NetRatel.Application.Agents;
using NetRatel.Application.Presence;
using NetRatel.Application.Telemetry;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AgentTelemetryGatewayServiceTests
{
    [Fact]
    public async Task PublishTelemetry_AcceptsAuthenticatedCurrentPresenceSession()
    {
        const int tenantId = 81;
        var agentId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        const long connectionEpoch = 4;
        var telemetry = new RecordingTelemetryRouter();
        var presence = new CurrentPresenceRouter(tenantId, agentId, connectionId, connectionEpoch);

        using var host = await BuildHostAsync(
            tenantId,
            agentId,
            presence,
            telemetry,
            telemetryEnabled: true);
        using var channel = GrpcChannel.ForAddress(
            "http://localhost",
            new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
        var client = new global::NetRatel.AgentGateway.Contracts.V1.AgentTelemetryGateway.AgentTelemetryGatewayClient(channel);
        using var call = client.PublishTelemetry();

        await call.RequestStream.WriteAsync(CreateFrame(
            tenantId,
            agentId,
            connectionId,
            connectionEpoch,
            sequence: 1,
            cpuUsage: 21));
        await call.RequestStream.WriteAsync(CreateFrame(
            tenantId,
            agentId,
            connectionId,
            connectionEpoch,
            sequence: 1,
            cpuUsage: 99));
        await call.RequestStream.CompleteAsync();

        var summary = await call.ResponseAsync;
        summary.AcceptedCount.Should().Be(1);
        summary.RejectedCount.Should().Be(1);
        summary.LastAcceptedSequence.Should().Be(1);
        summary.TelemetryAuthority.Should().Be("unavailable");
        telemetry.Latest.Should().NotBeNull();
        telemetry.Latest!.Snapshot.Client.Should().Be(new ClientKey(tenantId, agentId));
        telemetry.Latest.Snapshot.Cpu!.UsagePercent.Should().Be(21);
        telemetry.Latest.Snapshot.Memory!.TotalMb.Should().Be(8192);
        telemetry.Latest.Snapshot.Disks.Should().ContainSingle(disk => disk.Scope == "/");
        telemetry.Latest.Snapshot.Networks.Should().ContainSingle(network => network.Scope == "eth0");
        telemetry.Latest.Snapshot.TransportHealth!.AgentVersion.Should().Be("phase2-test");
        telemetry.Latest.Snapshot.IsAuthoritative.Should().BeFalse();
    }

    [Fact]
    public async Task PublishTelemetry_IsRejectedWhenFeatureFlagIsOff()
    {
        const int tenantId = 82;
        var agentId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var telemetry = new RecordingTelemetryRouter();
        var presence = new CurrentPresenceRouter(tenantId, agentId, connectionId, connectionEpoch: 1);

        using var host = await BuildHostAsync(
            tenantId,
            agentId,
            presence,
            telemetry,
            telemetryEnabled: false);
        using var channel = GrpcChannel.ForAddress(
            "http://localhost",
            new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
        var client = new global::NetRatel.AgentGateway.Contracts.V1.AgentTelemetryGateway.AgentTelemetryGatewayClient(channel);
        using var call = client.PublishTelemetry();

        var action = async () =>
        {
            await call.RequestStream.WriteAsync(CreateFrame(
                tenantId,
                agentId,
                connectionId,
                connectionEpoch: 1,
                sequence: 1,
                cpuUsage: 21));
            await call.RequestStream.CompleteAsync();
            await call.ResponseAsync;
        };

        var exception = await action.Should().ThrowAsync<RpcException>();
        exception.Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);
        telemetry.Latest.Should().BeNull();
    }

    [Fact]
    public async Task ConnectV2_AdmitsFencedAgentAndAcknowledgesAuthoritativeSnapshot()
    {
        const int tenantId = 83;
        var agentId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        const long connectionEpoch = 6;
        var telemetry = new RecordingTelemetryRouter();
        var buffered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var presence = new CurrentPresenceRouter(tenantId, agentId, connectionId, connectionEpoch)
        {
            ReadSnapshot = async (_, snapshot) =>
            {
                await buffered.Task;
                return snapshot;
            }
        };

        using var host = await BuildHostAsync(
            tenantId,
            agentId,
            presence,
            telemetry,
            telemetryEnabled: true,
            telemetryAuthorityEnabled: true);
        using var channel = GrpcChannel.ForAddress(
            "http://localhost",
            new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
        var client = new global::NetRatel.AgentGateway.Contracts.V1.AgentTelemetryGatewayV2.AgentTelemetryGatewayV2Client(channel);
        using var call = client.Connect();

        await call.RequestStream.WriteAsync(new AgentTelemetryFrame
        {
            ProtocolVersion = "1.0",
            TenantId = tenantId,
            ClientId = agentId.ToString("D"),
            ConnectionId = connectionId.ToString("D"),
            ConnectionEpoch = connectionEpoch,
            Sequence = 0,
            Hello = new AgentTelemetryHello { AgentVersion = "v2-test" }
        });

        await call.RequestStream.WriteAsync(new AgentTelemetryFrame
        {
            ProtocolVersion = "1.0",
            TenantId = tenantId,
            ClientId = agentId.ToString("D"),
            ConnectionId = connectionId.ToString("D"),
            ConnectionEpoch = connectionEpoch,
            Sequence = 1,
            Snapshot = CreateFrame(tenantId, agentId, connectionId, connectionEpoch, sequence: 1, cpuUsage: 37)
        });

        buffered.TrySetResult();

        (await call.ResponseStream.MoveNext(CancellationToken.None)).Should().BeTrue();
        call.ResponseStream.Current.Accepted.TelemetryAuthority.Should().Be("akka");
        call.ResponseStream.Current.Accepted.MaximumInFlightFrames.Should().Be(1);

        (await call.ResponseStream.MoveNext(CancellationToken.None)).Should().BeTrue();
        var acknowledgement = call.ResponseStream.Current;
        acknowledgement.SnapshotAccepted.AcceptedSequence.Should().Be(1);
        acknowledgement.SnapshotAccepted.AvailableCredits.Should().Be(1);
        telemetry.Latest.Should().NotBeNull();
        telemetry.Latest!.Snapshot.IsAuthoritative.Should().BeTrue();
        telemetry.Latest.Snapshot.Cpu!.UsagePercent.Should().Be(37);
    }

    [Fact]
    public async Task ConnectV2_CapableAgentReceivesExistingInteractivePolicyAfterAdmission()
    {
        const int tenantId = 84;
        var agentId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        const long connectionEpoch = 7;
        var telemetry = new RecordingTelemetryRouter();
        var presence = new CurrentPresenceRouter(tenantId, agentId, connectionId, connectionEpoch);

        using var host = await BuildHostAsync(
            tenantId,
            agentId,
            presence,
            telemetry,
            telemetryEnabled: true,
            telemetryAuthorityEnabled: true);
        var demand = host.Services.GetRequiredService<ITelemetryInteractiveDemandRegistry>();
        var lease = demand.Acquire(new ClientKey(tenantId, agentId), 1000);
        try
        {
            using var channel = GrpcChannel.ForAddress(
                "http://localhost",
                new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
            var client = new global::NetRatel.AgentGateway.Contracts.V1.AgentTelemetryGatewayV2.AgentTelemetryGatewayV2Client(channel);
            using var call = client.Connect();

            await call.RequestStream.WriteAsync(new AgentTelemetryFrame
            {
                ProtocolVersion = "1.0",
                TenantId = tenantId,
                ClientId = agentId.ToString("D"),
                ConnectionId = connectionId.ToString("D"),
                ConnectionEpoch = connectionEpoch,
                Sequence = 0,
                Hello = new AgentTelemetryHello
                {
                    AgentVersion = "v2-capable-test",
                    Capabilities = { "telemetry-rate-control-v1" }
                }
            });

            (await call.ResponseStream.MoveNext(CancellationToken.None)).Should().BeTrue();
            call.ResponseStream.Current.PayloadCase.Should().Be(GatewayTelemetryFrame.PayloadOneofCase.Accepted);
            (await call.ResponseStream.MoveNext(CancellationToken.None)).Should().BeTrue();
            call.ResponseStream.Current.PayloadCase.Should().Be(GatewayTelemetryFrame.PayloadOneofCase.TelemetrySamplingPolicy);
            call.ResponseStream.Current.TelemetrySamplingPolicy.FastIntervalMilliseconds.Should().Be(1000);
            call.ResponseStream.Current.TelemetrySamplingPolicy.Interactive.Should().BeTrue();
        }
        finally
        {
            demand.Release(lease);
        }
    }

    [Fact]
    public async Task ConnectV2_InFlightSnapshotCannotPublishAfterExactReconnect()
    {
        const int tenantId = 85;
        var agentId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var telemetry = new RecordingTelemetryRouter
        {
            BeforeRecord = async () =>
            {
                entered.TrySetResult();
                await release.Task;
            }
        };
        using var host = await BuildHostAsync(tenantId, agentId,
            new CurrentPresenceRouter(tenantId, agentId, connectionId, 5), telemetry, true, true);
        using var channel = GrpcChannel.ForAddress("http://localhost",
            new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
        var gateway = new global::NetRatel.AgentGateway.Contracts.V1.AgentTelemetryGatewayV2.AgentTelemetryGatewayV2Client(channel);
        var client = new ClientKey(tenantId, agentId);
        var live = host.Services.GetRequiredService<IGatewayTelemetryLiveRegistry>();
        await using var subscription = live.Subscribe(client);
        using var oldCall = gateway.Connect();
        await oldCall.RequestStream.WriteAsync(TelemetryHello(client, connectionId, 5));
        (await oldCall.ResponseStream.MoveNext(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        await oldCall.RequestStream.WriteAsync(new AgentTelemetryFrame
        {
            ProtocolVersion = "1.0",
            TenantId = tenantId,
            ClientId = agentId.ToString("D"),
            ConnectionId = connectionId.ToString("D"),
            ConnectionEpoch = 5,
            Sequence = 1,
            Snapshot = CreateFrame(tenantId, agentId, connectionId, 5, 1, 99)
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var replacement = gateway.Connect();
        try
        {
            await replacement.RequestStream.WriteAsync(TelemetryHello(client, connectionId, 5));
            (await replacement.ResponseStream.MoveNext(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        }
        finally
        {
            release.TrySetResult();
        }
        var oldResponse = async () => await oldCall.ResponseStream.MoveNext(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        (await oldResponse.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Aborted);
        host.Services.GetRequiredService<IAgentTelemetryCompatibilityRegistry>().GetSnapshot(agentId.ToString("D")).Should().BeNull();
        live.GetLatest(client).Should().BeNull();
        host.Services.GetRequiredService<IAgentTelemetryGatewaySessionRegistry>().GetStatus(client).Connected.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChildAdmission_RevalidatesPresenceBeforeBecomingVisibleOrAccepted(bool file)
    {
        var client = new ClientKey(86, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var presence = new CurrentPresenceRouter(client.TenantId, client.AgentId, connectionId, 5)
        {
            ReadSnapshot = async (read, snapshot) =>
            {
                if (read == 2)
                {
                    entered.TrySetResult();
                    await release.Task;
                    return snapshot with { ConnectionEpoch = 6, ConnectionId = Guid.NewGuid() };
                }
                return snapshot;
            }
        };
        using var host = await BuildHostAsync(client.TenantId, client.AgentId, presence, new RecordingTelemetryRouter(), true, true);
        using var channel = GrpcChannel.ForAddress("http://localhost",
            new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
        using var fileCall = new AgentFileGateway.AgentFileGatewayClient(channel).Connect();
        using var telemetryCall = new global::NetRatel.AgentGateway.Contracts.V1.AgentTelemetryGatewayV2.AgentTelemetryGatewayV2Client(channel).Connect();
        if (file) await fileCall.RequestStream.WriteAsync(FileHello(client, connectionId, 5));
        else await telemetryCall.RequestStream.WriteAsync(TelemetryHello(client, connectionId, 5));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            host.Services.GetRequiredService<IAgentFileGatewaySessionRegistry>().GetAvailability(client).Should().BeNull();
            host.Services.GetRequiredService<IAgentTelemetryGatewaySessionRegistry>().GetStatus(client).Connected.Should().BeFalse();
        }
        finally
        {
            release.TrySetResult();
        }
        var response = async () => await (file
            ? fileCall.ResponseStream.MoveNext(CancellationToken.None)
            : telemetryCall.ResponseStream.MoveNext(CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(5));
        (await response.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Aborted);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ChildRegistration_MapsStaleAndAmbiguousFenceToAborted(bool file, bool ambiguous)
    {
        var client = new ClientKey(87, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var epoch = ambiguous ? 5UL : 4UL;
        using var host = await BuildHostAsync(client.TenantId, client.AgentId,
            new CurrentPresenceRouter(client.TenantId, client.AgentId, connectionId, (long)epoch), new RecordingTelemetryRouter(), true, true);
        using var channel = GrpcChannel.ForAddress("http://localhost",
            new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
        using var currentFile = host.Services.GetRequiredService<IAgentFileGatewaySessionRegistry>().Register(client, Guid.NewGuid(), 5);
        await using var currentTelemetry = host.Services.GetRequiredService<IAgentTelemetryGatewaySessionRegistry>().Register(client, Guid.NewGuid(), 5, false, "current");
        using var fileCall = new AgentFileGateway.AgentFileGatewayClient(channel).Connect();
        using var telemetryCall = new global::NetRatel.AgentGateway.Contracts.V1.AgentTelemetryGatewayV2.AgentTelemetryGatewayV2Client(channel).Connect();
        if (file) await fileCall.RequestStream.WriteAsync(FileHello(client, connectionId, epoch));
        else await telemetryCall.RequestStream.WriteAsync(TelemetryHello(client, connectionId, epoch));
        var response = async () => await (file
            ? fileCall.ResponseStream.MoveNext(CancellationToken.None)
            : telemetryCall.ResponseStream.MoveNext(CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(5));
        var error = (await response.Should().ThrowAsync<RpcException>()).Which;
        error.StatusCode.Should().Be(StatusCode.Aborted);
        error.Status.Detail.Should().NotContain(client.AgentId.ToString("D")).And.NotContain(connectionId.ToString("D"));
        currentFile.IsCurrent.Should().BeTrue();
        currentTelemetry.IsCurrent.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactReconnect_TerminatesOldRpcWaitingForInboundFrames(bool file)
    {
        var client = new ClientKey(88, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        using var host = await BuildHostAsync(client.TenantId, client.AgentId,
            new CurrentPresenceRouter(client.TenantId, client.AgentId, connectionId, 5), new RecordingTelemetryRouter(), true, true);
        using var channel = GrpcChannel.ForAddress("http://localhost",
            new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
        if (file)
        {
            var gateway = new AgentFileGateway.AgentFileGatewayClient(channel);
            using var oldCall = gateway.Connect();
            await oldCall.RequestStream.WriteAsync(FileHello(client, connectionId, 5));
            (await oldCall.ResponseStream.MoveNext(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
            using var replacement = gateway.Connect();
            await replacement.RequestStream.WriteAsync(FileHello(client, connectionId, 5));
            (await replacement.ResponseStream.MoveNext(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
            (await oldCall.ResponseStream.MoveNext(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5))).Should().BeFalse();
        }
        else
        {
            var gateway = new global::NetRatel.AgentGateway.Contracts.V1.AgentTelemetryGatewayV2.AgentTelemetryGatewayV2Client(channel);
            using var oldCall = gateway.Connect();
            await oldCall.RequestStream.WriteAsync(TelemetryHello(client, connectionId, 5));
            (await oldCall.ResponseStream.MoveNext(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
            using var replacement = gateway.Connect();
            await replacement.RequestStream.WriteAsync(TelemetryHello(client, connectionId, 5));
            (await replacement.ResponseStream.MoveNext(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
            (await oldCall.ResponseStream.MoveNext(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5))).Should().BeFalse();
        }
    }

    private static AgentFileFrame FileHello(ClientKey client, Guid connectionId, ulong epoch) => new()
    {
        ProtocolVersion = "1.0",
        TenantId = client.TenantId,
        ClientId = client.AgentId.ToString("D"),
        ConnectionId = connectionId.ToString("D"),
        ConnectionEpoch = epoch,
        Hello = new AgentFileHello()
    };

    private static AgentTelemetryFrame TelemetryHello(ClientKey client, Guid connectionId, ulong epoch) => new()
    {
        ProtocolVersion = "1.0",
        TenantId = client.TenantId,
        ClientId = client.AgentId.ToString("D"),
        ConnectionId = connectionId.ToString("D"),
        ConnectionEpoch = epoch,
        Hello = new AgentTelemetryHello { AgentVersion = "test" }
    };

    private static TelemetryFrame CreateFrame(
        int tenantId,
        Guid agentId,
        Guid connectionId,
        long connectionEpoch,
        ulong sequence,
        double cpuUsage) =>
        new()
        {
            ProtocolVersion = "1.0",
            TenantId = tenantId,
            ClientId = agentId.ToString("D"),
            ConnectionId = connectionId.ToString("D"),
            ConnectionEpoch = checked((ulong)connectionEpoch),
            Sequence = sequence,
            ObservedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Cpu = new global::NetRatel.AgentGateway.Contracts.V1.TelemetryCpu
            {
                UsagePercent = cpuUsage,
                LoadAverage = 0.25,
                ProcessCount = 50
            },
            Memory = new global::NetRatel.AgentGateway.Contracts.V1.TelemetryMemory
            {
                TotalMb = 8192,
                UsedMb = 4096,
                AvailableMb = 4096,
                UsagePercent = 50
            },
            TransportHealth = new global::NetRatel.AgentGateway.Contracts.V1.TelemetryTransportHealth
            {
                UptimeSeconds = 120,
                AgentVersion = "phase2-test",
                OsVersion = "linux",
                LastHeartbeat = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
            },
            Disks =
            {
                new global::NetRatel.AgentGateway.Contracts.V1.TelemetryDisk
                {
                    Scope = "/",
                    TotalGb = 100,
                    UsedGb = 40,
                    FreeGb = 60,
                    UsagePercent = 40
                }
            },
            Networks =
            {
                new global::NetRatel.AgentGateway.Contracts.V1.TelemetryNetwork
                {
                    Scope = "eth0",
                    RxBytesPerSec = 1000,
                    TxBytesPerSec = 500
                }
            }
        };

    private static async Task<IHost> BuildHostAsync(
        int tenantId,
        Guid agentId,
        IClientPresenceRouter presence,
        IClientTelemetryRouter telemetry,
        bool telemetryEnabled,
        bool telemetryAuthorityEnabled = false)
    {
        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddAuthorization(options =>
                {
                    options.AddPolicy("AgentGatewayAccess", policy =>
                        policy.RequireAssertion(context =>
                            AgentGatewayIdentityResolver.TryResolve(context.User, out _, out _)));
                });
                services.AddGrpc();
                services.AddSingleton(presence);
                services.AddSingleton(telemetry);
                services.AddSingleton<IAgentTelemetryCompatibilityRegistry, GatewayTelemetryCompatibilityRegistry>();
                services.AddSingleton<IGatewayTelemetryLiveRegistry, GatewayTelemetryLiveRegistry>();
                services.AddSingleton<TelemetryInteractiveDemandRegistry>();
                services.AddSingleton<ITelemetryInteractiveDemandRegistry>(provider => provider.GetRequiredService<TelemetryInteractiveDemandRegistry>());
                services.AddSingleton<IAgentTelemetryGatewaySessionRegistry, AgentTelemetryGatewaySessionRegistry>();
                services.AddSingleton<IAgentFileGatewaySessionRegistry, AgentFileGatewaySessionRegistry>();
                services.AddSingleton<IAgentManagementService>(
                    new ActiveAgentManagementService(tenantId, agentId));
                services.AddSingleton(new NetRatelAkkaMigrationOptions
                {
                    Enabled = true,
                    PresenceEnabled = true,
                    GatewayEnabled = true,
                    TelemetryShadowEnabled = telemetryEnabled,
                    PresenceAuthorityEnabled = telemetryAuthorityEnabled,
                    TelemetryAuthorityEnabled = telemetryAuthorityEnabled,
                    FileGatewayEnabled = true,
                    FileBrowseAuthorityEnabled = true
                });
                services.AddSingleton(TimeProvider.System);
                services.AddSingleton(NullLogger<AgentTelemetryGatewayService>.Instance);
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
                    ], "Phase2Test"));
                    await next(context);
                });
                app.UseRouting();
                app.UseAuthorization();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapGrpcService<AgentTelemetryGatewayService>();
                    endpoints.MapGrpcService<AgentTelemetryGatewayV2Service>();
                    endpoints.MapGrpcService<AgentFileGatewayService>();
                });
            });
        });

        return await builder.StartAsync();
    }

    private sealed class RecordingTelemetryRouter : IClientTelemetryRouter
    {
        private ulong _sequence;

        public RecordTelemetrySnapshot? Latest { get; private set; }
        public Func<Task>? BeforeRecord { get; init; }

        public async Task<TelemetryMessageResult> RecordAsync(
            RecordTelemetrySnapshot message,
            CancellationToken cancellationToken)
        {
            if (BeforeRecord is not null) await BeforeRecord();
            var disposition = message.Snapshot.Sequence <= _sequence
                ? TelemetryMessageDisposition.Duplicate
                : TelemetryMessageDisposition.Accepted;
            if (disposition == TelemetryMessageDisposition.Accepted)
            {
                _sequence = message.Snapshot.Sequence;
                Latest = message;
            }

            return new TelemetryMessageResult(message.Client, disposition, _sequence);
        }

        public Task<ClientTelemetryState> GetSnapshotAsync(
            ClientKey client,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ClientTelemetryReadModelSnapshot> GetReadModelAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ClientTelemetryReadModelSnapshot(Array.Empty<TelemetrySnapshot>(), DateTimeOffset.UtcNow));

        public Task<ClientTelemetryRouteStatus> ProbeAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class CurrentPresenceRouter(
        int tenantId,
        Guid agentId,
        Guid connectionId,
        long connectionEpoch) : IClientPresenceRouter
    {
        private int _reads;
        public Func<int, ClientPresenceSnapshot, Task<ClientPresenceSnapshot>>? ReadSnapshot { get; init; }
        public Task<ClientPresenceSnapshot> GetSnapshotAsync(ClientKey client, CancellationToken cancellationToken)
        {
            var snapshot = new ClientPresenceSnapshot(new ClientKey(tenantId, agentId), ShadowPresenceStatus.Online,
                connectionEpoch, connectionId, 0, DateTimeOffset.UtcNow, "phase2-test", ["presence", "telemetry"],
                null, "akka-shadow", IsAuthoritative: false);
            var read = Interlocked.Increment(ref _reads);
            return ReadSnapshot?.Invoke(read, snapshot) ?? Task.FromResult(snapshot);
        }

        public Task<GatewayPresenceSessionStarted> StartSessionAsync(
            StartGatewayPresenceSession message,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PresenceMessageResult> RecordHeartbeatAsync(
            RecordGatewayHeartbeat message,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PresenceMessageResult> EndSessionAsync(
            EndGatewayPresenceSession message,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ClientPresenceRouteStatus> ProbeAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ActiveAgentManagementService(int tenantId, Guid agentId) : IAgentManagementService
    {
        public Task<AgentDetailDto?> GetAsync(int requestedTenantId, Guid requestedAgentId, CancellationToken ct) =>
            Task.FromResult<AgentDetailDto?>(
                requestedTenantId == tenantId && requestedAgentId == agentId
                    ? new AgentDetailDto(
                        tenantId,
                        agentId,
                        "Phase 2 test agent",
                        IsEnabled: true,
                        DisabledReason: null,
                        DateTimeOffset.UtcNow,
                        CreatedBy: "test",
                        LastSeenAtUtc: null,
                        LastTokenIssuedAtUtc: null,
                        RevokedAtUtc: null)
                    : null);

        public Task<AgentListResponse> ListAsync(int requestedTenantId, AgentListQuery query, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task DisableAsync(int requestedTenantId, Guid requestedAgentId, string reason, string actor, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task EnableAsync(int requestedTenantId, Guid requestedAgentId, string actor, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task DeleteAsync(int requestedTenantId, Guid requestedAgentId, string reason, string actor, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
