using System.Security.Claims;
using FluentAssertions;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Akka.Configuration;
using NetRatel.API.Gateway;
using NetRatel.API.Services;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Application.Agents;
using NetRatel.Application.Presence;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AgentGatewayServiceTests
{
    [Theory]
    [InlineData(false, "unavailable")]
    [InlineData(true, "akka")]
    public async Task Connect_AdmitsAuthenticatedAgentAndReportsConfiguredPresenceAuthority(
        bool presenceAuthorityEnabled,
        string expectedAuthority)
    {
        const int tenantId = 73;
        var agentId = Guid.NewGuid();
        var helloOperationId = Guid.NewGuid();
        var heartbeatOperationId = Guid.NewGuid();
        var router = new RecordingPresenceRouter();
        var legacySpacetimeIdentity = new string('a', 64);

        using var host = await BuildHostAsync(
            tenantId,
            agentId,
            router,
            presenceAuthorityEnabled);
        using var channel = GrpcChannel.ForAddress(
            "http://localhost",
            new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
        var client = new global::NetRatel.AgentGateway.Contracts.V1.AgentGateway.AgentGatewayClient(channel);
        using var call = client.Connect();

        await call.RequestStream.WriteAsync(new AgentFrame
        {
            ProtocolVersion = "1.0",
            TenantId = tenantId,
            ClientId = agentId.ToString("D"),
            ConnectionId = Guid.NewGuid().ToString("D"),
            OperationId = helloOperationId.ToString("D"),
            Hello = new ConnectHello
            {
                AgentVersion = "phase1-test",
                LegacySpacetimeIdentity = legacySpacetimeIdentity
            }
        });

        (await call.ResponseStream.MoveNext(CancellationToken.None)).Should().BeTrue();
        var connected = call.ResponseStream.Current;
        connected.Connected.PresenceAuthority.Should().Be(expectedAuthority);
        connected.ConnectionEpoch.Should().Be(1);
        Guid.Parse(connected.ConnectionId).Should().NotBeEmpty();

        await call.RequestStream.WriteAsync(new AgentFrame
        {
            ProtocolVersion = "1.0",
            TenantId = tenantId,
            ClientId = agentId.ToString("D"),
            ConnectionEpoch = connected.ConnectionEpoch,
            ConnectionId = connected.ConnectionId,
            OperationId = heartbeatOperationId.ToString("D"),
            Sequence = 1,
            Heartbeat = new PresenceHeartbeat()
        });

        (await call.ResponseStream.MoveNext(CancellationToken.None)).Should().BeTrue();
        var heartbeat = call.ResponseStream.Current;
        heartbeat.Sequence.Should().Be(1);
        heartbeat.HeartbeatAccepted.Duplicate.Should().BeFalse();
        heartbeat.HeartbeatAccepted.PresenceAuthority.Should().Be(expectedAuthority);

        await call.RequestStream.CompleteAsync();
        (await call.ResponseStream.MoveNext(CancellationToken.None)).Should().BeFalse();

        router.Started.Should().NotBeNull();
        router.Started!.Client.Should().Be(new ClientKey(tenantId, agentId));
        router.Started.OperationId.Should().Be(helloOperationId);
        router.Started.LegacySpacetimeIdentity.Should().Be(legacySpacetimeIdentity);
        router.Heartbeat.Should().NotBeNull();
        router.Heartbeat!.OperationId.Should().Be(heartbeatOperationId);
        router.Ended.Should().NotBeNull();
        router.Ended!.Reason.Should().Be("stream_closed");
    }

    private static async Task<IHost> BuildHostAsync(
        int tenantId,
        Guid agentId,
        RecordingPresenceRouter router,
        bool presenceAuthorityEnabled)
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
                services.AddSingleton<IClientPresenceRouter>(router);
                services.AddSingleton<IAgentManagementService>(
                    new ActiveAgentManagementService(tenantId, agentId));
                services.AddSingleton(new NetRatelAkkaMigrationOptions
                {
                    Enabled = true,
                    PresenceEnabled = true,
                    GatewayEnabled = true,
                    PresenceAuthorityEnabled = presenceAuthorityEnabled
                });
                services.AddSingleton(TimeProvider.System);
                services.AddDbContext<OrchestratorDbContext>(options => options.UseInMemoryDatabase($"gateway-{Guid.NewGuid():N}"));
                services.AddSingleton<IClientUpdateCatalog, EmptyClientUpdateCatalog>();
                services.AddScoped<ClientUpdateAuthorityService>();
                services.AddSingleton(NullLogger<AgentGatewayService>.Instance);
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
                    ], "Phase1Test"));
                    await next(context);
                });
                app.UseRouting();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapGrpcService<AgentGatewayService>());
            });
        });

        return await builder.StartAsync();
    }

    private sealed class EmptyClientUpdateCatalog : IClientUpdateCatalog
    {
        public long Revision => 0;
        public DateTimeOffset RefreshedAtUtc => DateTimeOffset.UtcNow;
        public ClientUpdateOfferSnapshot? GetOffer(int tenantId, Guid agentId, string runtimeId, string currentVersion, string channel) => null;
        public ClientUpdatePolicySnapshot GetPolicy(int tenantId, Guid agentId, long clientPolicyRevision) => new(0, false, false, false, null);
        public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingPresenceRouter : IClientPresenceRouter
    {
        public StartGatewayPresenceSession? Started { get; private set; }

        public RecordGatewayHeartbeat? Heartbeat { get; private set; }

        public EndGatewayPresenceSession? Ended { get; private set; }

        public Task<GatewayPresenceSessionStarted> StartSessionAsync(
            StartGatewayPresenceSession message,
            CancellationToken cancellationToken)
        {
            Started = message;
            return Task.FromResult(new GatewayPresenceSessionStarted(
                message.Client,
                message.ConnectionId,
                ConnectionEpoch: 1,
                PresenceMessageDisposition.Accepted,
                message.ReceivedAtUtc));
        }

        public Task<PresenceMessageResult> RecordHeartbeatAsync(
            RecordGatewayHeartbeat message,
            CancellationToken cancellationToken)
        {
            Heartbeat = message;
            return Task.FromResult(new PresenceMessageResult(
                message.Client,
                message.ConnectionEpoch,
                PresenceMessageDisposition.Accepted,
                message.Sequence));
        }

        public Task<PresenceMessageResult> EndSessionAsync(
            EndGatewayPresenceSession message,
            CancellationToken cancellationToken)
        {
            Ended = message;
            return Task.FromResult(new PresenceMessageResult(
                message.Client,
                message.ConnectionEpoch,
                PresenceMessageDisposition.Accepted,
                Heartbeat?.Sequence ?? 0));
        }

        public Task<ClientPresenceSnapshot> GetSnapshotAsync(
            ClientKey client,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ClientPresenceRouteStatus> ProbeAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ActiveAgentManagementService(int tenantId, Guid agentId) : IAgentManagementService
    {
        public Task<AgentDetailDto?> GetAsync(int requestedTenantId, Guid requestedAgentId, CancellationToken ct)
        {
            if (requestedTenantId != tenantId || requestedAgentId != agentId)
            {
                return Task.FromResult<AgentDetailDto?>(null);
            }

            return Task.FromResult<AgentDetailDto?>(new AgentDetailDto(
                tenantId,
                agentId,
                "Phase 1 test agent",
                IsEnabled: true,
                DisabledReason: null,
                DateTimeOffset.UtcNow,
                CreatedBy: "test",
                LastSeenAtUtc: null,
                LastTokenIssuedAtUtc: null,
                RevokedAtUtc: null));
        }

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
