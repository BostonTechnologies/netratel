using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Grpc.AspNetCore.Server;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using NetRatel.Akka.Configuration;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.API.Bootstrap;
using NetRatel.Application.Commands;
using NetRatel.Application.Jobs;
using NetRatel.Application.Presence;
using NetRatel.Application.Telemetry;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts;
using Xunit;

[Trait("category", "integration")]
[Collection(ApiIntegrationCollection.Name)]
public sealed class RuntimeInventoryHostIntegrationTests
{
    private static readonly string[] RetiredBooleanKeys =
    [
        "NetRatelAkkaMigration:Enabled",
        "NetRatelAkkaMigration:PresenceEnabled",
        "NetRatelAkkaMigration:GatewayEnabled",
        "NetRatelAkkaMigration:ClientUpdatesEnabled",
        "NetRatelAkkaMigration:ControlGatewayEnabled",
        "NetRatelAkkaMigration:FileGatewayEnabled",
        "NetRatelAkkaMigration:LogGatewayEnabled",
        "NetRatelAkkaMigration:RemoteSupportGatewayEnabled",
        "NetRatelAkkaMigration:RemoteSupportV2InventoryEnabled",
        "NetRatelAkkaMigration:RemoteSupportV2LifecycleAuthorityEnabled",
        "NetRatelAkkaMigration:RemoteSupportV2ReplicaSafeEdgeEnabled",
        "NetRatelAkkaMigration:RemoteSupportV2MediaEnabled",
        "NetRatelAkkaMigration:RemoteSupportLegacyGatewayRollbackEnabled",
        "NetRatelAkkaMigration:PrimaryCardGatewayReadsEnabled",
        "NetRatelAkkaMigration:PrimaryCardGatewayActionsEnabled",
        "NetRatelAkkaMigration:TerminalGatewayEnabled",
        "NetRatelAkkaMigration:TerminalGatewayPrimaryCardEnabled",
        "NetRatelAkkaMigration:PresenceReadModelEnabled",
        "NetRatelAkkaMigration:TelemetryShadowEnabled",
        "NetRatelAkkaMigration:CommandShadowEnabled",
        "NetRatelAkkaMigration:CommandPersistenceEnabled",
        "NetRatelAkkaMigration:JobShadowEnabled",
        "NetRatelAkkaMigration:TerminalShadowEnabled",
        "NetRatelAkkaMigration:SignalRShadowEnabled",
        "NetRatelAkkaMigration:SignalRShadowLocalCanaryEnabled",
        "NetRatelAkkaMigration:PresenceAuthorityEnabled",
        "NetRatelAkkaMigration:PingAuthorityEnabled",
        "NetRatelAkkaMigration:TelemetryAuthorityEnabled",
        "NetRatelAkkaMigration:FileBrowseAuthorityEnabled",
        "NetRatelAkkaMigration:LogAuthorityEnabled",
        "NetRatelAkkaMigration:RemoteSupportAuthorityEnabled",
        "NetRatelAkkaMigration:CommandAuthorityEnabled",
        "NetRatelAkkaMigration:JobAuthorityEnabled",
        "NetRatelAkkaMigration:TerminalAuthorityEnabled",
        "NetRatelAkkaMigration:SignalRAuthorityEnabled",
        "NetRatelAkkaMigration:RemoteSupportShadowEnabled",
        "LegacyQueueWorker:Enabled"
    ];

    private static readonly string[] ExpectedGatewayMethods =
    [
        "AgentGateway/Connect",
        "AgentTelemetryGatewayV2/Connect",
        "AgentCommandGateway/Connect",
        "AgentJobGateway/Connect",
        "AgentControlGateway/Connect",
        "AgentFileGateway/Connect",
        "AgentLogGateway/Connect",
        "AgentRemoteSupportGateway/Connect",
        "AgentRemoteSupportPreparationGateway/Connect",
        "AgentTerminalGateway/Connect"
    ];

    private readonly ApiFactory _factory;

    public RuntimeInventoryHostIntegrationTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task InitializedLocalHostsIgnoreRetiredSelectorsAndRunTheSameAkkaGateway()
    {
        var variants = new[]
        {
            (Name: "absent", OldValue: (string?)null, AuthorityMode: (string?)null),
            (Name: "true", OldValue: "true", AuthorityMode: "Shadow"),
            (Name: "false", OldValue: "false", AuthorityMode: "Authority")
        };
        var signatures = new List<RuntimeSignature>(variants.Length);

        for (var index = 0; index < variants.Length; index++)
        {
            var variant = variants[index];
            var overrides = CreateRuntimeSettings($"NetRatelRuntimeInventory{index}");
            if (variant.OldValue is not null)
            {
                foreach (var key in RetiredBooleanKeys)
                {
                    overrides[key] = variant.OldValue;
                }

                overrides["NetRatelAkkaMigration:AuthorityMode"] = variant.AuthorityMode;
            }

            using var host = _factory.CreateRuntimeSibling(overrides);
            using var anonymous = host.CreateClient();

            var bootstrap = await anonymous.GetFromJsonAsync<BootstrapStatus>("/api/v2/setup/status");
            bootstrap.Should().NotBeNull();
            bootstrap!.State.Should().Be(BootstrapState.Ready, "each sibling uses the real completed Local bootstrap state");
            bootstrap.IsReady.Should().BeTrue();

            var runtimeOptions = host.Services.GetRequiredService<IOptions<NetRatelAkkaOptions>>().Value;
            runtimeOptions.ActorSystemName.Should().Be($"NetRatelRuntimeInventory{index}");
            runtimeOptions.GatewayGrpcPort.Should().Be(19320);
            runtimeOptions.HeartbeatIntervalSeconds.Should().Be(23);
            runtimeOptions.MissedHeartbeatLimit.Should().Be(2);
            runtimeOptions.HeartbeatGraceSeconds.Should().Be(4);
            runtimeOptions.HeartbeatTimeoutSeconds.Should().Be(50);
            runtimeOptions.AskTimeout.Should().Be(TimeSpan.FromSeconds(7));
            runtimeOptions.MaxInboundMessageBytes.Should().Be(32 * 1024);
            runtimeOptions.MaxOutboundMessageBytes.Should().Be(48 * 1024);
            runtimeOptions.MaxTelemetryScopesPerFrame.Should().Be(31);
            runtimeOptions.RemoteSupportAgentEdgeRenewalInterval.Should().Be(TimeSpan.FromSeconds(17));

            var grpcOptions = host.Services.GetRequiredService<IOptions<GrpcServiceOptions>>().Value;
            grpcOptions.MaxReceiveMessageSize.Should().Be(32 * 1024);
            grpcOptions.MaxSendMessageSize.Should().Be(48 * 1024);

            var presence = await host.Services.GetRequiredService<IClientPresenceRouter>()
                .ProbeAsync(CancellationToken.None);
            var telemetry = await host.Services.GetRequiredService<IClientTelemetryRouter>()
                .ProbeAsync(CancellationToken.None);
            var commands = await host.Services.GetRequiredService<IClientCommandRouter>()
                .ProbeAsync(CancellationToken.None);
            var jobs = await host.Services.GetRequiredService<IJobRuntimeRouter>()
                .ProbeAsync(CancellationToken.None);
            presence.Mode.Should().Be("akka");
            telemetry.Mode.Should().Be("akka");
            telemetry.Authority.Should().Be("akka");
            commands.Mode.Should().Be("akka");
            commands.Authority.Should().Be("akka");
            jobs.Mode.Should().Be("akka");
            jobs.Authority.Should().Be("akka");

            var health = await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync();
            health.Status.Should().Be(HealthStatus.Healthy,
                string.Join("; ", health.Entries.Select(entry => $"{entry.Key}: {entry.Value.Status} {entry.Value.Description}")));
            health.Entries.Keys.Should().Contain([
                "akka-presence",
                "agent-auth-signing",
                "akka-telemetry",
                "akka-command",
                "akka-command-persistence",
                "akka-job",
                "akka-signalr"
            ]);

            var routePatterns = host.Services.GetRequiredService<EndpointDataSource>().Endpoints
                .OfType<RouteEndpoint>()
                .Select(endpoint => endpoint.RoutePattern.RawText)
                .Where(pattern => pattern is not null && pattern.Contains("netratel.gateway.v1.", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToArray();
            foreach (var expectedMethod in ExpectedGatewayMethods)
            {
                routePatterns.Should().Contain(pattern => pattern!.EndsWith(expectedMethod, StringComparison.Ordinal),
                    $"the normal host should register {expectedMethod}");
            }

            int tenantId;
            var activeAgentId = Guid.NewGuid();
            var offlineAgentId = Guid.NewGuid();
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var database = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                tenantId = await database.Tenants
                    .Where(tenant => tenant.Name == "OpenAPI tenant")
                    .Select(tenant => tenant.Id)
                    .SingleAsync();
                database.Agents.AddRange(
                    new Agent
                    {
                        Id = activeAgentId,
                        TenantId = tenantId,
                        Name = $"Active runtime {variant.Name}",
                        Status = AgentStatus.Active,
                        IsEnabled = true,
                        CreatedAtUtc = DateTimeOffset.UtcNow
                    },
                    new Agent
                    {
                        Id = offlineAgentId,
                        TenantId = tenantId,
                        Name = $"Offline runtime {variant.Name}",
                        Status = AgentStatus.Active,
                        IsEnabled = true,
                        CreatedAtUtc = DateTimeOffset.UtcNow
                    });
                await database.SaveChangesAsync();
            }

            using (var administrator = await host.CreateLocalAdministratorClientAsync())
            using (var directoryResponse = await administrator.GetAsync(
                       $"/api/v2/client-presence/?online=false&search={Uri.EscapeDataString($"Offline runtime {variant.Name}")}&limit=100"))
            {
                directoryResponse.StatusCode.Should().Be(HttpStatusCode.OK);
                var directory = await directoryResponse.Content.ReadFromJsonAsync<ClientPresenceListDto>();
                directory.Should().NotBeNull();
                directory!.Mode.Should().Be("akka");
                directory.Items.Should().ContainSingle(agent =>
                    agent.TenantId == tenantId &&
                    agent.AgentId == offlineAgentId &&
                    agent.DisplayName == $"Offline runtime {variant.Name}" &&
                    !agent.Online);
            }

            if (index == 0)
            {
                var rejectedTokens = new[]
                {
                    (Name: "signature", Token: host.CreateAgentBearerToken(tenantId, activeAgentId, invalidSignature: true), Status: StatusCode.Unauthenticated),
                    (Name: "issuer", Token: host.CreateAgentBearerToken(tenantId, activeAgentId, issuer: "https://wrong-issuer.example.test"), Status: StatusCode.Unauthenticated),
                    (Name: "audience", Token: host.CreateAgentBearerToken(tenantId, activeAgentId, audience: "wrong-audience"), Status: StatusCode.Unauthenticated),
                    (Name: "expiry", Token: host.CreateAgentBearerToken(tenantId, activeAgentId, expired: true), Status: StatusCode.Unauthenticated),
                    (Name: "role", Token: host.CreateAgentBearerToken(tenantId, activeAgentId, includeRole: false), Status: StatusCode.PermissionDenied),
                    (Name: "tenant", Token: host.CreateAgentBearerToken(tenantId, activeAgentId, includeTenant: false), Status: StatusCode.PermissionDenied)
                };

                foreach (var rejected in rejectedTokens)
                {
                    await AssertAgentConnectionRejectedAsync(host, rejected.Token, rejected.Status, rejected.Name);
                }

                var missingPresence = await host.Services.GetRequiredService<IClientPresenceRouter>()
                    .GetSnapshotAsync(new ClientKey(tenantId, activeAgentId), CancellationToken.None);
                missingPresence.Status.Should().Be(ClientPresenceStatus.Unknown,
                    "an invalid signature, issuer, audience, expiry, role, or tenant must not start a presence session");
                missingPresence.ConnectionId.Should().BeNull();

                using var administrator = await host.CreateLocalAdministratorClientAsync();
                var search = Uri.EscapeDataString($"Active runtime {variant.Name}");
                var activeDirectory = await administrator.GetFromJsonAsync<ClientPresenceListDto>(
                    $"/api/v2/client-presence/?online=true&search={search}");
                activeDirectory.Should().NotBeNull();
                activeDirectory!.Items.Should().BeEmpty("rejected credentials must not publish online presence");
            }

            var callResult = await ConnectAndHeartbeatOverTheActualApiHostAsync(host, tenantId, activeAgentId);
            callResult.PresenceAuthority.Should().Be("akka");
            callResult.HeartbeatAuthority.Should().Be("akka");
            callResult.HeartbeatIntervalSeconds.Should().Be(23);
            callResult.HeartbeatTimeoutSeconds.Should().Be(50);

            signatures.Add(new RuntimeSignature(
                routePatterns,
                health.Entries.OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .Select(entry => $"{entry.Key}:{entry.Value.Status}")
                    .ToArray(),
                runtimeOptions.GatewayGrpcPort,
                runtimeOptions.HeartbeatIntervalSeconds,
                runtimeOptions.HeartbeatTimeoutSeconds,
                runtimeOptions.AskTimeoutSeconds,
                runtimeOptions.MaxInboundMessageBytes,
                runtimeOptions.MaxOutboundMessageBytes,
                callResult.PresenceAuthority,
                callResult.HeartbeatAuthority));
        }

        signatures[1].Should().BeEquivalentTo(signatures[0], "retired true-valued selectors cannot alter an initialized host");
        signatures[2].Should().BeEquivalentTo(signatures[0], "retired false-valued selectors cannot alter an initialized host");
    }

    [Fact]
    public async Task DatabaseUnavailableDuringBootstrapKeepsTheOperationalRuntimeOutAndReadinessFalse()
    {
        var preReadySelectors = RetiredBooleanKeys.ToDictionary(
            static key => key,
            static _ => (string?)"true",
            StringComparer.Ordinal);
        preReadySelectors["NetRatelAkkaMigration:AuthorityMode"] = "Shadow";
        using var host = _factory.CreateUnavailableDatabaseSibling(preReadySelectors);
        using var client = host.CreateClient();

        var ready = await client.GetAsync("/health/ready");
        ready.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "the copied Ready descriptor must not make an unreachable configured database ready");

        var status = await client.GetFromJsonAsync<BootstrapStatus>("/api/v2/setup/status");
        status.Should().NotBeNull();
        status!.State.Should().Be(BootstrapState.RecoveryRequired);
        status.IsReady.Should().BeFalse();
        var descriptor = await host.Services.GetRequiredService<BootstrapStateStore>().LoadOrCreateAsync();
        descriptor.RecoveryReason.Should().Be("configured-storage-unavailable",
            "the isolated copy of Ready bootstrap evidence must fail specifically on the unreachable configured database");

        host.Services.GetService<IClientPresenceRouter>().Should().BeNull();
        host.Services.GetService<IClientTelemetryRouter>().Should().BeNull();
        host.Services.GetService<IClientCommandRouter>().Should().BeNull();
        host.Services.GetService<IJobRuntimeRouter>().Should().BeNull();
        var routes = host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToArray();
        routes.Where(route => route?.Contains("netratel.gateway.v1.", StringComparison.Ordinal) == true)
            .Should().BeEmpty();
    }

    [Fact]
    public async Task DatabaseFailureAfterReadyReturnsSanitizedUnavailableAndRecoversOnTheSameHost()
    {
        using var host = _factory.CreateRuntimeSibling(CreateRuntimeSettings("NetRatelRuntimeDependencyRecovery"));
        using var administrator = await host.CreateLocalAdministratorClientAsync(TimeSpan.FromSeconds(10));

        var search = $"dependency-recovery-{Guid.NewGuid():N}";
        var agentId = Guid.NewGuid();
        int tenantId;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            tenantId = await database.Tenants
                .Where(tenant => tenant.Name == "OpenAPI tenant")
                .Select(tenant => tenant.Id)
                .SingleAsync();
            database.Agents.Add(new Agent
            {
                Id = agentId,
                TenantId = tenantId,
                Name = $"{search} offline agent",
                Status = AgentStatus.Active,
                IsEnabled = true,
                CreatedAtUtc = DateTimeOffset.UtcNow
            });
            await database.SaveChangesAsync();
        }

        var route = $"/api/v2/client-presence/?online=false&search={Uri.EscapeDataString(search)}";
        using (var available = await administrator.GetAsync(route))
        {
            available.StatusCode.Should().Be(HttpStatusCode.OK);
            var directory = await available.Content.ReadFromJsonAsync<ClientPresenceListDto>();
            directory.Should().NotBeNull();
            directory!.Items.Should().ContainSingle(agent =>
                agent.AgentId == agentId && agent.TenantId == tenantId && !agent.Online);
        }

        var healthChecks = host.Services.GetRequiredService<HealthCheckService>();
        var healthy = await CheckHealthAsync(healthChecks);
        healthy.Status.Should().Be(HealthStatus.Healthy);

        var outage = await _factory.DenyApplicationDatabaseConnectionsAsync();
        try
        {
            using var unavailable = await administrator.GetAsync(route);
            unavailable.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            await using var content = await unavailable.Content.ReadAsStreamAsync();
            using var problem = await JsonDocument.ParseAsync(content);
            problem.RootElement.GetProperty("code").GetString().Should().Be("local_authentication_unavailable");
            problem.RootElement.GetProperty("title").GetString()
                .Should().Be("Local authentication is temporarily unavailable.");
            problem.RootElement.TryGetProperty("detail", out _).Should().BeFalse();
            unavailable.Headers.Contains("Set-Cookie").Should().BeFalse(
                "a database outage must not invalidate or replace the operator's existing cookie");

            var unhealthy = await CheckHealthAsync(healthChecks);
            unhealthy.Status.Should().NotBe(HealthStatus.Healthy,
                "the registered readiness checks must report durable-store unavailability instead of an operational runtime that appears healthy");
            unhealthy.Entries["akka-command-persistence"].Status.Should().Be(HealthStatus.Unhealthy);
            unhealthy.Entries["akka-job"].Status.Should().Be(HealthStatus.Unhealthy);
        }
        finally
        {
            await outage.DisposeAsync();
        }

        var recovered = await CheckHealthAsync(healthChecks);
        recovered.Status.Should().Be(HealthStatus.Healthy,
            string.Join("; ", recovered.Entries.Select(entry => $"{entry.Key}: {entry.Value.Status}")));
        using var restored = await administrator.GetAsync(route);
        restored.StatusCode.Should().Be(HttpStatusCode.OK);
        var restoredDirectory = await restored.Content.ReadFromJsonAsync<ClientPresenceListDto>();
        restoredDirectory.Should().NotBeNull();
        restoredDirectory!.Items.Should().ContainSingle(agent =>
            agent.AgentId == agentId && agent.TenantId == tenantId && !agent.Online);
    }

    [Fact]
    public async Task UnconfiguredLocalBootstrapDoesNotStartAkkaEvenWhenRetiredSelectorsAreTrue()
    {
        var selectors = RetiredBooleanKeys.ToDictionary(
            static key => key,
            static _ => (string?)"true",
            StringComparer.Ordinal);
        selectors["NetRatelAkkaMigration:AuthorityMode"] = "Shadow";
        using var host = _factory.CreateUnconfiguredSibling(selectors);
        using var client = host.CreateClient();

        var live = await client.GetAsync("/health/live");
        live.StatusCode.Should().Be(HttpStatusCode.OK);
        var ready = await client.GetAsync("/health/ready");
        ready.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        var status = await client.GetFromJsonAsync<BootstrapStatus>("/api/v2/setup/status");
        status.Should().NotBeNull();
        status!.State.Should().Be(BootstrapState.Unconfigured);
        status.SetupRequired.Should().BeTrue();

        host.Services.GetService<IClientPresenceRouter>().Should().BeNull();
        host.Services.GetService<IClientTelemetryRouter>().Should().BeNull();
        host.Services.GetService<IClientCommandRouter>().Should().BeNull();
        host.Services.GetService<IJobRuntimeRouter>().Should().BeNull();
        var routes = host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToArray();
        routes.Where(route => route?.Contains("netratel.gateway.v1.", StringComparison.Ordinal) == true)
            .Should().BeEmpty();
    }

    private static Dictionary<string, string?> CreateRuntimeSettings(string actorSystemName) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["NetRatelAkka:ActorSystemName"] = actorSystemName,
        ["NetRatelAkka:GatewayGrpcPort"] = "19320",
        ["NetRatelAkka:HeartbeatIntervalSeconds"] = "23",
        ["NetRatelAkka:MissedHeartbeatLimit"] = "2",
        ["NetRatelAkka:HeartbeatGraceSeconds"] = "4",
        ["NetRatelAkka:AskTimeoutSeconds"] = "7",
        ["NetRatelAkka:MaxInboundMessageBytes"] = (32 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["NetRatelAkka:MaxOutboundMessageBytes"] = (48 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["NetRatelAkka:MaxTelemetryScopesPerFrame"] = "31",
        ["NetRatelAkka:RemoteSupportAgentEdgeRenewalSeconds"] = "17"
    };

    private static async Task<HealthReport> CheckHealthAsync(HealthCheckService healthChecks)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await healthChecks.CheckHealthAsync(cancellationToken: timeout.Token);
    }

    private static async Task<GatewayCallResult> ConnectAndHeartbeatOverTheActualApiHostAsync(
        ApiFactory host,
        int tenantId,
        Guid agentId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var channel = GrpcChannel.ForAddress(
            "http://localhost",
            new GrpcChannelOptions { HttpHandler = host.Server.CreateHandler() });
        var headers = new Metadata
        {
            { "Authorization", $"Bearer {host.CreateAgentBearerToken(tenantId, agentId)}" }
        };
        using var call = new AgentGateway.AgentGatewayClient(channel)
            .Connect(headers, cancellationToken: timeout.Token);

        await call.RequestStream.WriteAsync(new AgentFrame
        {
            ProtocolVersion = NetRatelAkkaOptions.ProtocolVersion,
            TenantId = tenantId,
            ClientId = agentId.ToString("D"),
            ConnectionId = Guid.NewGuid().ToString("D"),
            OperationId = Guid.NewGuid().ToString("D"),
            Hello = new ConnectHello { AgentVersion = "runtime-inventory-integration" }
        });

        (await call.ResponseStream.MoveNext(timeout.Token)).Should().BeTrue();
        var connected = call.ResponseStream.Current;
        connected.Connected.PresenceAuthority.Should().Be("akka");

        await call.RequestStream.WriteAsync(new AgentFrame
        {
            ProtocolVersion = NetRatelAkkaOptions.ProtocolVersion,
            TenantId = tenantId,
            ClientId = agentId.ToString("D"),
            ConnectionEpoch = connected.ConnectionEpoch,
            ConnectionId = connected.ConnectionId,
            OperationId = Guid.NewGuid().ToString("D"),
            Sequence = 1,
            Heartbeat = new PresenceHeartbeat()
        });

        (await call.ResponseStream.MoveNext(timeout.Token)).Should().BeTrue();
        var heartbeat = call.ResponseStream.Current;
        heartbeat.Sequence.Should().Be(1);
        heartbeat.HeartbeatAccepted.Duplicate.Should().BeFalse();
        await call.RequestStream.CompleteAsync();
        (await call.ResponseStream.MoveNext(timeout.Token)).Should().BeFalse();

        return new GatewayCallResult(
            connected.Connected.PresenceAuthority,
            heartbeat.HeartbeatAccepted.PresenceAuthority,
            checked((int)connected.Connected.HeartbeatIntervalSeconds),
            checked((int)connected.Connected.HeartbeatTimeoutSeconds));
    }

    private static async Task AssertAgentConnectionRejectedAsync(
        ApiFactory host,
        string token,
        StatusCode expectedStatus,
        string reason)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var channel = GrpcChannel.ForAddress(
            "http://localhost",
            new GrpcChannelOptions { HttpHandler = host.Server.CreateHandler() });
        var headers = new Metadata { { "Authorization", $"Bearer {token}" } };
        RpcException? failure = null;
        try
        {
            using var call = new AgentGateway.AgentGatewayClient(channel)
                .Connect(headers, cancellationToken: timeout.Token);
            _ = await call.ResponseStream.MoveNext(timeout.Token);
        }
        catch (RpcException exception)
        {
            failure = exception;
        }

        failure.Should().NotBeNull($"the {reason} credential must be rejected by the normal API authentication path");
        failure!.StatusCode.Should().Be(expectedStatus, reason);
    }

    private sealed record RuntimeSignature(
        string?[] GatewayRoutes,
        string[] HealthStatuses,
        int GatewayGrpcPort,
        int HeartbeatIntervalSeconds,
        int HeartbeatTimeoutSeconds,
        int AskTimeoutSeconds,
        int MaxInboundMessageBytes,
        int MaxOutboundMessageBytes,
        string PresenceAuthority,
        string HeartbeatAuthority);

    private sealed record GatewayCallResult(
        string PresenceAuthority,
        string HeartbeatAuthority,
        int HeartbeatIntervalSeconds,
        int HeartbeatTimeoutSeconds);
}
