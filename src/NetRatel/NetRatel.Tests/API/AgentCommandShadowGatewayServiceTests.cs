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
using NetRatel.Application.Agents;
using NetRatel.Application.Commands;
using NetRatel.Application.Presence;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AgentCommandShadowGatewayServiceTests
{
    [Fact]
    public async Task PublishCommandEvents_AcceptsAuthenticatedCurrentPresenceSession()
    {
        const int tenantId = 91;
        var agentId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        const long connectionEpoch = 7;
        var commands = new RecordingCommandRouter();
        var presence = new CurrentCommandPresenceRouter(tenantId, agentId, connectionId, connectionEpoch);

        using var host = await BuildHostAsync(
            tenantId,
            agentId,
            presence,
            commands,
            commandShadowEnabled: true);
        using var channel = GrpcChannel.ForAddress(
            "http://localhost",
            new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
        var client = new global::NetRatel.AgentGateway.Contracts.V1.AgentCommandShadowGateway.AgentCommandShadowGatewayClient(channel);
        using var call = client.PublishCommandEvents();
        var requestTimestamp = DateTimeOffset.UtcNow;

        await call.RequestStream.WriteAsync(CreateFrame(
            tenantId,
            agentId,
            connectionId,
            connectionEpoch,
            "opaque-command-id",
            requestTimestamp,
            CommandShadowStatus.Created,
            version: 1,
            sequence: 1));
        await call.RequestStream.WriteAsync(CreateFrame(
            tenantId,
            agentId,
            connectionId,
            connectionEpoch,
            "opaque-command-id",
            requestTimestamp,
            CommandShadowStatus.Dispatched,
            version: 2,
            sequence: 2));
        await call.RequestStream.CompleteAsync();

        var summary = await call.ResponseAsync;
        summary.AcceptedCount.Should().Be(2);
        summary.RejectedCount.Should().Be(0);
        summary.LastAcceptedSequence.Should().Be(2);
        summary.CommandAuthority.Should().Be("unavailable");
        commands.Events.Should().HaveCount(2);
        commands.Events[0].Event.Client.Should().Be(new ClientKey(tenantId, agentId));
        commands.Events[0].Event.CommandId.Should().Be("opaque-command-id");
        commands.Events[0].Event.CorrelationId.Should().Be("netratel-task-opaque-command-id");
        commands.Events[1].Event.Status.Should().Be(CommandLifecycleStatus.Dispatched);
        commands.Events.Should().OnlyContain(message => !message.Event.IsAuthoritative);
    }

    [Fact]
    public async Task PublishCommandEvents_IsRejectedWhenFeatureFlagIsOff()
    {
        const int tenantId = 92;
        var agentId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var commands = new RecordingCommandRouter();

        using var host = await BuildHostAsync(
            tenantId,
            agentId,
            new CurrentCommandPresenceRouter(tenantId, agentId, connectionId, connectionEpoch: 1),
            commands,
            commandShadowEnabled: false);
        using var channel = GrpcChannel.ForAddress(
            "http://localhost",
            new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
        var client = new global::NetRatel.AgentGateway.Contracts.V1.AgentCommandShadowGateway.AgentCommandShadowGatewayClient(channel);
        using var call = client.PublishCommandEvents();

        var action = async () =>
        {
            await call.RequestStream.WriteAsync(CreateFrame(
                tenantId,
                agentId,
                connectionId,
                connectionEpoch: 1,
                "disabled-command",
                DateTimeOffset.UtcNow,
                CommandShadowStatus.Created,
                version: 1,
                sequence: 1));
            await call.RequestStream.CompleteAsync();
            await call.ResponseAsync;
        };

        var exception = await action.Should().ThrowAsync<RpcException>();
        exception.Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);
        commands.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task PublishCommandEvents_RejectsAStalePresenceConnection()
    {
        const int tenantId = 93;
        var agentId = Guid.NewGuid();
        var activeConnectionId = Guid.NewGuid();
        var staleConnectionId = Guid.NewGuid();
        var commands = new RecordingCommandRouter();

        using var host = await BuildHostAsync(
            tenantId,
            agentId,
            new CurrentCommandPresenceRouter(tenantId, agentId, activeConnectionId, connectionEpoch: 3),
            commands,
            commandShadowEnabled: true);
        using var channel = GrpcChannel.ForAddress(
            "http://localhost",
            new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
        var client = new global::NetRatel.AgentGateway.Contracts.V1.AgentCommandShadowGateway.AgentCommandShadowGatewayClient(channel);
        using var call = client.PublishCommandEvents();

        var action = async () =>
        {
            await call.RequestStream.WriteAsync(CreateFrame(
                tenantId,
                agentId,
                staleConnectionId,
                connectionEpoch: 2,
                "stale-command",
                DateTimeOffset.UtcNow,
                CommandShadowStatus.Started,
                version: 4,
                sequence: 4));
            await call.RequestStream.CompleteAsync();
            await call.ResponseAsync;
        };

        var exception = await action.Should().ThrowAsync<RpcException>();
        exception.Which.StatusCode.Should().Be(StatusCode.Aborted);
        commands.Events.Should().BeEmpty();
    }

    private static CommandShadowFrame CreateFrame(
        int tenantId,
        Guid agentId,
        Guid connectionId,
        long connectionEpoch,
        string commandId,
        DateTimeOffset requestTimestamp,
        CommandShadowStatus status,
        ulong version,
        ulong sequence) =>
        new()
        {
            ProtocolVersion = "1.0",
            TenantId = tenantId,
            ClientId = agentId.ToString("D"),
            ConnectionId = connectionId.ToString("D"),
            ConnectionEpoch = checked((ulong)connectionEpoch),
            CommandId = commandId,
            CorrelationId = $"netratel-task-{commandId}",
            RequestTimestamp = Timestamp.FromDateTimeOffset(requestTimestamp),
            StatusTimestamp = Timestamp.FromDateTimeOffset(requestTimestamp.AddSeconds(checked((long)version))),
            Version = version,
            Sequence = sequence,
            Status = status
        };

    private static async Task<IHost> BuildHostAsync(
        int tenantId,
        Guid agentId,
        IClientPresenceRouter presence,
        IClientCommandRouter commands,
        bool commandShadowEnabled)
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
                services.AddSingleton(commands);
                services.AddSingleton<IAgentManagementService>(
                    new ActiveCommandAgentManagementService(tenantId, agentId));
                services.AddSingleton(new NetRatelAkkaMigrationOptions
                {
                    Enabled = true,
                    PresenceEnabled = true,
                    GatewayEnabled = true,
                    CommandShadowEnabled = commandShadowEnabled
                });
                services.AddSingleton(NullLogger<AgentCommandShadowGatewayService>.Instance);
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
                    ], "Phase3Test"));
                    await next(context);
                });
                app.UseRouting();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapGrpcService<AgentCommandShadowGatewayService>());
            });
        });

        return await builder.StartAsync();
    }

    private sealed class RecordingCommandRouter : IClientCommandRouter
    {
        public List<RecordCommandLifecycleEvent> Events { get; } = [];

        public Task<CommandMessageResult> RecordAsync(
            RecordCommandLifecycleEvent message,
            CancellationToken cancellationToken)
        {
            Events.Add(message);
            return Task.FromResult(new CommandMessageResult(
                message.Command,
                CommandMessageDisposition.Accepted,
                message.Event.Status,
                message.Event.Version,
                message.Event.Sequence));
        }

        public Task<CommandShadowState> GetStateAsync(
            CommandKey command,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ClientCommandRouteStatus> ProbeAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class CurrentCommandPresenceRouter(
        int tenantId,
        Guid agentId,
        Guid connectionId,
        long connectionEpoch) : IClientPresenceRouter
    {
        public Task<ClientPresenceSnapshot> GetSnapshotAsync(
            ClientKey client,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ClientPresenceSnapshot(
                new ClientKey(tenantId, agentId),
                ShadowPresenceStatus.Online,
                connectionEpoch,
                connectionId,
                0,
                DateTimeOffset.UtcNow,
                "phase3-test",
                ["presence", "commands"],
                null,
                "akka-shadow",
                IsAuthoritative: false));

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

    private sealed class ActiveCommandAgentManagementService(int tenantId, Guid agentId) : IAgentManagementService
    {
        public Task<AgentDetailDto?> GetAsync(
            int requestedTenantId,
            Guid requestedAgentId,
            CancellationToken ct) =>
            Task.FromResult<AgentDetailDto?>(
                requestedTenantId == tenantId && requestedAgentId == agentId
                    ? new AgentDetailDto(
                        tenantId,
                        agentId,
                        "Phase 3 test agent",
                        IsEnabled: true,
                        DisabledReason: null,
                        DateTimeOffset.UtcNow,
                        CreatedBy: "test",
                        LastSeenAtUtc: null,
                        LastTokenIssuedAtUtc: null,
                        RevokedAtUtc: null)
                    : null);

        public Task<AgentListResponse> ListAsync(
            int requestedTenantId,
            AgentListQuery query,
            CancellationToken ct) => throw new NotSupportedException();

        public Task DisableAsync(
            int requestedTenantId,
            Guid requestedAgentId,
            string reason,
            string actor,
            CancellationToken ct) => throw new NotSupportedException();

        public Task EnableAsync(
            int requestedTenantId,
            Guid requestedAgentId,
            string actor,
            CancellationToken ct) => throw new NotSupportedException();

        public Task DeleteAsync(
            int requestedTenantId,
            Guid requestedAgentId,
            string reason,
            string actor,
            CancellationToken ct) => throw new NotSupportedException();
    }
}
