using System.Security.Claims;
using AwesomeAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Akka.Configuration;
using NetRatel.API.Gateway;
using NetRatel.API.Realtime;
using NetRatel.Application.Agents;
using NetRatel.Application.Presence;
using NetRatel.Application.Telemetry;
using NetRatel.Application.Services;
using NetRatel.API.Services;
using NetRatel.Shared.Contracts.Services;
using Xunit;
using System.Collections.Immutable;
using NetRatel.Application.Monitoring;
using NetRatel.Shared.Contracts.Monitoring;

namespace NetRatel.Tests.API;

public sealed class AgentTelemetryGatewayServiceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SharedAcknowledgementWaitsForDurableMonitoringAndFailureClosesStream(bool serviceInput, bool persistenceFailure)
    {
        var key = new ClientKey(93, Guid.NewGuid());
        var connection = Guid.NewGuid();
        var monitoring = new RecordingMonitoringRuntime();
        var telemetry = new RecordingTelemetryRouter();
        using var host = await BuildHostAsync(key.TenantId, key.AgentId, new CurrentPresenceRouter(key.TenantId, key.AgentId, connection, 5),
            telemetry, serviceInput ? new RecordingServicesRouter() : null, monitoring);
        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
        using var call = new global::NetRatel.AgentGateway.Contracts.V1.AgentTelemetryGatewayV2.AgentTelemetryGatewayV2Client(channel).Connect();
        var hello = TelemetryHello(key, connection, 5);
        if (serviceInput) hello.Hello.Capabilities.Add(ClientServicesLimits.Capability);
        await call.RequestStream.WriteAsync(hello);
        (await call.ResponseStream.MoveNext(default).WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        monitoring.Begun.Should().ContainSingle();
        var registration = host.Services.GetRequiredService<IAgentTelemetryGatewaySessionRegistry>().GetStatus(key);
        monitoring.Begun[0].EvidenceStreamId.Should().Be(registration.RegistrationId!.Value);
        monitoring.Begun[0].ConnectionId.Should().Be(connection);
        if (serviceInput) (await call.ResponseStream.MoveNext(default).WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        var frame = serviceInput ? ServicesFrame(key, connection, 1) : new AgentTelemetryFrame
        {
            ProtocolVersion = "1.0", TenantId = key.TenantId, ClientId = key.AgentId.ToString("D"), ConnectionId = connection.ToString("D"),
            ConnectionEpoch = 5, Sequence = 1, Snapshot = CreateFrame(key.TenantId, key.AgentId, connection, 5, 1, 90)
        };
        if (!serviceInput) { frame.Snapshot.Disks[0].TotalBytes = 100; frame.Snapshot.Disks[0].FreeBytes = 40; }
        await call.RequestStream.WriteAsync(frame);
        var acknowledgement = call.ResponseStream.MoveNext(default);
        await monitoring.Observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        acknowledgement.IsCompleted.Should().BeFalse("the source projection alone cannot acknowledge monitoring evidence");
        var compatibility = host.Services.GetRequiredService<IAgentTelemetryCompatibilityRegistry>();
        compatibility.GetSnapshot(key.AgentId.ToString("D")).Should().BeNull();
        if (serviceInput) monitoring.Services!.Services.LastAcceptedSequence.Should().Be(1);
        else
        {
            monitoring.Telemetry!.Snapshot.IsAuthoritative.Should().BeTrue();
            monitoring.Telemetry.Snapshot.Disks[0].TotalBytes.Should().Be(100);
            monitoring.Telemetry.Snapshot.Disks[0].FreeBytes.Should().Be(40);
        }
        monitoring.Release.TrySetResult(new(persistenceFailure ? MonitoringInputDisposition.PersistenceUnavailable : MonitoringInputDisposition.Accepted, 1, 1));
        if (persistenceFailure)
        {
            Func<Task> read = async () => await acknowledgement.WaitAsync(TimeSpan.FromSeconds(5));
            (await read.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Unavailable);
            compatibility.GetSnapshot(key.AgentId.ToString("D")).Should().BeNull();
        }
        else
        {
            (await acknowledgement.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
            call.ResponseStream.Current.SnapshotAccepted.AcceptedSequence.Should().Be(1);
            if (!serviceInput) compatibility.GetSnapshot(key.AgentId.ToString("D")).Should().NotBeNull();
        }
    }

    [Fact]
    public async Task SamePresenceReconnectStartsDistinctServerEvidenceStreamsWithoutChangingEpoch()
    {
        var key = new ClientKey(94, Guid.NewGuid());
        var connection = Guid.NewGuid();
        var monitoring = new RecordingMonitoringRuntime();
        using var host = await BuildHostAsync(key.TenantId, key.AgentId, new CurrentPresenceRouter(key.TenantId, key.AgentId, connection, 5),
            new RecordingTelemetryRouter(), monitoring: monitoring);
        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
        var client = new global::NetRatel.AgentGateway.Contracts.V1.AgentTelemetryGatewayV2.AgentTelemetryGatewayV2Client(channel);
        using var first = client.Connect();
        await first.RequestStream.WriteAsync(TelemetryHello(key, connection, 5));
        (await first.ResponseStream.MoveNext(default).WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        using var second = client.Connect();
        await second.RequestStream.WriteAsync(TelemetryHello(key, connection, 5));
        (await second.ResponseStream.MoveNext(default).WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        monitoring.Begun.Should().HaveCount(2);
        monitoring.Begun[1].EvidenceStreamId.Should().NotBe(monitoring.Begun[0].EvidenceStreamId);
        monitoring.Begun[1].ConnectionId.Should().Be(monitoring.Begun[0].ConnectionId);
        monitoring.Begun[1].ConnectionEpoch.Should().Be(monitoring.Begun[0].ConnectionEpoch);
    }

    [Fact]
    public async Task ServicesCapabilityIsTwoSidedAndSharesSnapshotSequenceAndAcknowledgements()
    {
        var key = new ClientKey(89, Guid.NewGuid());
        var connection = Guid.NewGuid();
        var services = new RecordingServicesRouter();
        var telemetry = new RecordingTelemetryRouter();
        using var host = await BuildHostAsync(key.TenantId, key.AgentId,
            new CurrentPresenceRouter(key.TenantId, key.AgentId, connection, 5), telemetry, services);
        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
        using var call = new global::NetRatel.AgentGateway.Contracts.V1.AgentTelemetryGatewayV2.AgentTelemetryGatewayV2Client(channel).Connect();
        var hello = TelemetryHello(key, connection, 5);
        hello.Hello.Capabilities.Add(ClientServicesLimits.Capability);
        await call.RequestStream.WriteAsync(hello);
        (await call.ResponseStream.MoveNext(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        call.ResponseStream.Current.Accepted.AcceptedCapabilities.Should().Contain(ClientServicesLimits.Capability);
        (await call.ResponseStream.MoveNext(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        call.ResponseStream.Current.ServiceWatchPolicy.ServiceNames.Should().BeEmpty("inventory discovery does not imply monitoring");

        await call.RequestStream.WriteAsync(new AgentTelemetryFrame
        {
            ProtocolVersion = "1.0", TenantId = key.TenantId, ClientId = key.AgentId.ToString("D"),
            ConnectionId = connection.ToString("D"), ConnectionEpoch = 5, Sequence = 1,
            Snapshot = CreateFrame(key.TenantId, key.AgentId, connection, 5, 1, 12)
        });
        (await call.ResponseStream.MoveNext(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        call.ResponseStream.Current.SnapshotAccepted.AcceptedSequence.Should().Be(1);
        await call.RequestStream.WriteAsync(ServicesFrame(key, connection, 2));
        (await call.ResponseStream.MoveNext(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        call.ResponseStream.Current.SnapshotAccepted.AcceptedSequence.Should().Be(2);
        call.ResponseStream.Current.SnapshotAccepted.AvailableCredits.Should().Be(1);
        services.Chunks.Should().ContainSingle().Which.Client.Should().Be(key);
        services.Chunks.Single().Sequence.Should().Be(2);
        services.Chunks.Single().Services.Single().State.Should().Be(ClientServiceState.Stopped);
        telemetry.Latest!.Snapshot.Sequence.Should().Be(1);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ServicesFramesRequireBothEndpointsToSupportCapability(bool clientCapable, bool serverCapable)
    {
        var key = new ClientKey(90, Guid.NewGuid());
        var connection = Guid.NewGuid();
        var services = new RecordingServicesRouter();
        using var host = await BuildHostAsync(key.TenantId, key.AgentId,
            new CurrentPresenceRouter(key.TenantId, key.AgentId, connection, 5), new RecordingTelemetryRouter(), serverCapable ? services : null);
        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
        using var call = new global::NetRatel.AgentGateway.Contracts.V1.AgentTelemetryGatewayV2.AgentTelemetryGatewayV2Client(channel).Connect();
        var hello = TelemetryHello(key, connection, 5);
        if (clientCapable) hello.Hello.Capabilities.Add(ClientServicesLimits.Capability);
        await call.RequestStream.WriteAsync(hello);
        (await call.ResponseStream.MoveNext(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        call.ResponseStream.Current.Accepted.AcceptedCapabilities.Should().BeEmpty();
        await call.RequestStream.WriteAsync(ServicesFrame(key, connection, 1));
        var read = async () => await call.ResponseStream.MoveNext(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        (await read.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);
        services.Chunks.Should().BeEmpty();
    }

    [Theory]
    [InlineData("cross-tenant", StatusCode.InvalidArgument)]
    [InlineData("wrong-connection", StatusCode.InvalidArgument)]
    [InlineData("oversized", StatusCode.ResourceExhausted)]
    [InlineData("missing-unconfirmed", StatusCode.InvalidArgument)]
    public async Task ServicesValidationRejectsUnsafeFramesBeforeProjection(string defect, StatusCode expected)
    {
        var key = new ClientKey(91, Guid.NewGuid());
        var connection = Guid.NewGuid();
        var services = new RecordingServicesRouter();
        using var host = await BuildHostAsync(key.TenantId, key.AgentId,
            new CurrentPresenceRouter(key.TenantId, key.AgentId, connection, 5), new RecordingTelemetryRouter(), services);
        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
        using var call = new global::NetRatel.AgentGateway.Contracts.V1.AgentTelemetryGatewayV2.AgentTelemetryGatewayV2Client(channel).Connect();
        var hello = TelemetryHello(key, connection, 5);
        hello.Hello.Capabilities.Add(ClientServicesLimits.Capability);
        await call.RequestStream.WriteAsync(hello);
        (await call.ResponseStream.MoveNext(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        (await call.ResponseStream.MoveNext(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        var frame = ServicesFrame(key, connection, 1);
        if (defect == "cross-tenant") frame.TenantId++;
        if (defect == "wrong-connection") frame.ConnectionId = Guid.NewGuid().ToString("D");
        if (defect == "oversized")
            for (var index = 0; index < 30; index++)
                frame.ServicesChunk.Services.Add(new ServiceObservation
                {
                    Name = "service-" + index, DisplayName = new string('d', 512), Platform = ServicePlatform.Windows,
                    State = ServiceState.Stopped, ObservedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
                });
        if (defect == "missing-unconfirmed") frame.ServicesChunk.Services[0].State = ServiceState.Missing;
        await call.RequestStream.WriteAsync(frame);
        var read = async () => await call.ResponseStream.MoveNext(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        (await read.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(expected);
        services.Chunks.Should().BeEmpty();
    }

    private static AgentTelemetryFrame ServicesFrame(ClientKey client, Guid connection, ulong sequence) => new()
    {
        ProtocolVersion = "1.0", TenantId = client.TenantId, ClientId = client.AgentId.ToString("D"),
        ConnectionId = connection.ToString("D"), ConnectionEpoch = 5, Sequence = sequence,
        ServicesChunk = new ServiceSnapshotChunk
        {
            CollectionId = Guid.NewGuid().ToString("D"), Kind = ServiceSnapshotType.Inventory,
            Status = ServiceCollectionCompleteness.Complete, IsFinal = true,
            ObservedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Services = { new ServiceObservation
            {
                Name = "synthetic-stopped", DisplayName = "Synthetic stopped service", Platform = ServicePlatform.Windows,
                State = ServiceState.Stopped, RawState = "Stopped", StartMode = "Automatic",
                ObservedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
            } }
        }
    };

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
            telemetry);
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
            telemetry);
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
            new CurrentPresenceRouter(tenantId, agentId, connectionId, 5), telemetry);
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
        using var host = await BuildHostAsync(client.TenantId, client.AgentId, presence, new RecordingTelemetryRouter());
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
            new CurrentPresenceRouter(client.TenantId, client.AgentId, connectionId, (long)epoch), new RecordingTelemetryRouter());
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
            new CurrentPresenceRouter(client.TenantId, client.AgentId, connectionId, 5), new RecordingTelemetryRouter());
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
        IClientServicesRouter? servicesRouter = null,
        IMonitoringRuntime? monitoring = null)
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
                if (monitoring is not null) services.AddSingleton(monitoring);
                if (servicesRouter is not null)
                {
                    services.AddSingleton(servicesRouter);
                    services.AddSingleton<IClientServiceWatchPolicySource, EmptyClientServiceWatchPolicySource>();
                }
                services.AddSingleton<IAgentTelemetryCompatibilityRegistry, GatewayTelemetryCompatibilityRegistry>();
                services.AddSingleton<IGatewayTelemetryLiveRegistry, GatewayTelemetryLiveRegistry>();
                services.AddSingleton<TelemetryInteractiveDemandRegistry>();
                services.AddSingleton<ITelemetryInteractiveDemandRegistry>(provider => provider.GetRequiredService<TelemetryInteractiveDemandRegistry>());
                services.AddSingleton<IAgentTelemetryGatewaySessionRegistry, AgentTelemetryGatewaySessionRegistry>();
                services.AddSingleton<IAgentFileGatewaySessionRegistry, AgentFileGatewaySessionRegistry>();
                services.AddSingleton<IAgentManagementService>(
                    new ActiveAgentManagementService(tenantId, agentId));
                services.AddSingleton(new NetRatelAkkaOptions());
                services.AddSingleton(TimeProvider.System);
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
            var snapshot = new ClientPresenceSnapshot(new ClientKey(tenantId, agentId), ClientPresenceStatus.Online,
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

    private sealed class RecordingServicesRouter : IClientServicesRouter
    {
        public List<ClientServicesChunk> Chunks { get; } = [];
        public Task<ClientServicesMessageResult> RecordAsync(RecordClientServicesChunk message, CancellationToken cancellationToken)
        {
            Chunks.Add(message.Chunk);
            var chunk = message.Chunk;
            var state = ClientServicesState.Empty(message.Client) with
            {
                ConnectionEpoch = chunk.ConnectionEpoch, ConnectionId = chunk.ConnectionId, LastAcceptedSequence = chunk.Sequence,
                LatestAttempt = new(chunk.CollectionId, chunk.Kind, chunk.Status, chunk.ConnectionEpoch, chunk.Sequence,
                    chunk.ObservedAtUtc, chunk.ReceivedAtUtc, chunk.WatchPolicyRevision, chunk.ErrorCode)
            };
            return Task.FromResult(new ClientServicesMessageResult(message.Client, ClientServicesMessageDisposition.Accepted, message.Chunk.Sequence, state));
        }
        public Task<ClientServicesState> GetSnapshotAsync(ClientKey client, CancellationToken cancellationToken) => Task.FromResult(ClientServicesState.Empty(client));
        public Task<ClientServicesState> UpdateWatchPolicyAsync(ClientServiceWatchPolicy policy, CancellationToken cancellationToken) => Task.FromResult(ClientServicesState.Empty(policy.Client));
    }

    private sealed class RecordingMonitoringRuntime : IMonitoringRuntime
    {
        public List<MonitoringEvidenceFence> Begun { get; } = [];
        public TaskCompletionSource Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<MonitoringInputResult> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public MonitoringTelemetryInput? Telemetry { get; private set; }
        public MonitoringServicesInput? Services { get; private set; }
        public Task<MonitoringEvidenceFence?> ReserveEvidenceRegistrationAsync(ClientKey client, Guid connectionId, long epoch, Guid registrationId, CancellationToken ct)
            => Task.FromResult<MonitoringEvidenceFence?>(new(client, connectionId, epoch, registrationId, 1));
        public Task<MonitoringInputResult> BeginEvidenceStreamAsync(MonitoringEvidenceFence fence, CancellationToken ct)
        { Begun.Add(fence); return Task.FromResult(new MonitoringInputResult(MonitoringInputDisposition.Accepted, 0, 0)); }
        public Task<MonitoringInputResult> EndEvidenceStreamAsync(MonitoringEvidenceFence fence, CancellationToken ct) => Task.FromResult(new MonitoringInputResult(MonitoringInputDisposition.Accepted, 0, 0));
        public Task<MonitoringInputResult> RecordTelemetryAsync(MonitoringTelemetryInput input, CancellationToken ct) { Telemetry = input; Observed.TrySetResult(); return Release.Task.WaitAsync(ct); }
        public Task<MonitoringInputResult> RecordServicesAsync(MonitoringServicesInput input, CancellationToken ct) { Services = input; Observed.TrySetResult(); return Release.Task.WaitAsync(ct); }
        public Task<ImmutableArray<MonitoringSeriesState>> GetClientAsync(ClientKey client, CancellationToken ct) => throw new NotSupportedException();
        public Task<MonitoringSeriesPageDto> ReadTenantAsync(int tenant, int max, string? cursor, CancellationToken ct) => throw new NotSupportedException();
        public Task<MonitoringEventPageDto> ReadTenantEventsAsync(int tenant, int max, string? cursor, CancellationToken ct) => throw new NotSupportedException();
        public Task<MonitoringSummaryDto> ReadTenantSummaryAsync(int tenant, CancellationToken ct) => throw new NotSupportedException();
        public Task<MonitoringStoreWriteResult> AcknowledgeAsync(MonitoringOperatorCommand command, CancellationToken ct) => throw new NotSupportedException();
        public Task<MonitoringStoreWriteResult> ClearAsync(MonitoringOperatorCommand command, CancellationToken ct) => throw new NotSupportedException();
        public Task RefreshAsync(ClientKey client, CancellationToken ct) => Task.CompletedTask;
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
