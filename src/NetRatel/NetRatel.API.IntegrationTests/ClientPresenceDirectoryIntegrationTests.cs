using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Akka.Actor;
using Akka.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.API.Gateway;
using NetRatel.Akka.Configuration;
using NetRatel.Akka.Hosting;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts;
using Xunit;

[Trait("category", "integration")]
[Collection(ApiIntegrationCollection.Name)]
public sealed class ClientPresenceDirectoryIntegrationTests
{
    private readonly ApiFactory _factory;

    public ClientPresenceDirectoryIntegrationTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task RetainedCapabilityRegistrations_DoNotMakeAnOfflineDirectoryEntryReady()
    {
        var agentId = Guid.NewGuid();
        int tenantId;
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        tenantId = await db.Tenants
            .Where(tenant => tenant.Name == "OpenAPI tenant")
            .Select(tenant => tenant.Id).SingleAsync();
        db.Agents.Add(new Agent
        {
            Id = agentId, TenantId = tenantId, Name = $"Capability readiness {agentId:N}",
            IsEnabled = true, CreatedAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var client = new ClientKey(tenantId, agentId);
        var connection = Guid.NewGuid();
        // Simulate transport teardown lagging behind offline presence. The
        // projection must not infer authority merely from a retained object.
        using var terminal = scope.ServiceProvider.GetRequiredService<IAgentTerminalSessionRegistry>()
            .Register(client, connection, 1, ["sh"]);
        using var file = scope.ServiceProvider.GetRequiredService<IAgentFileGatewaySessionRegistry>()
            .Register(client, connection, 1);
        using var administrator = await _factory.CreateLocalAdministratorClientAsync();
        var directory = await administrator.GetFromJsonAsync<ClientPresenceListDto>(
            $"/api/v2/client-presence/?tenantId={tenantId}&search={agentId:D}&limit=1");
        var entry = directory!.Items.Should().ContainSingle().Which;
        entry.Online.Should().BeFalse();
        entry.Terminal!.TransportReady.Should().BeFalse();
        entry.Terminal.ReadinessReason.Should().Be("terminal_presence_offline");
        entry.File!.SessionActive.Should().BeTrue();
        entry.File.FenceMatchesPresence.Should().BeFalse();
        entry.File.ReadinessReason.Should().Be("file_gateway_presence_offline");
    }

    [Fact]
    public async Task LocalCookieAuthorizationAndPersistedOfflineDirectoryUseTheNormalApiHost()
    {
        using var anonymous = _factory.CreateClient();
        using var anonymousResponse = await anonymous.GetAsync("/api/v2/client-presence/");
        anonymousResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var unprivileged = await _factory.CreateLocalUserClientAsync(
            $"reader-{Guid.NewGuid():N}@example.test",
            "A1! unprivileged passphrase");
        using var forbiddenResponse = await unprivileged.GetAsync("/api/v2/client-presence/");
        forbiddenResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var agentId = Guid.NewGuid();
        var directorySearchPrefix = $"DirectoryPagination-{Guid.NewGuid():N}";
        var onlineAgents = Enumerable.Range(0, 100)
            .Select(index => new Agent
            {
                Id = Guid.NewGuid(),
                Name = $"{directorySearchPrefix} online {index:D3}",
                IsEnabled = true,
                CreatedAtUtc = DateTimeOffset.UtcNow
            })
            .ToArray();
        var fallbackAgents = new[]
        {
            new Agent { Id = Guid.NewGuid(), Name = null, DeviceInfoJson = null, IsEnabled = true, CreatedAtUtc = DateTimeOffset.UtcNow },
            new Agent { Id = Guid.NewGuid(), Name = null, DeviceInfoJson = "{}", IsEnabled = true, CreatedAtUtc = DateTimeOffset.UtcNow },
            new Agent { Id = Guid.NewGuid(), Name = null, DeviceInfoJson = "{\"hostName\":\" \"}", IsEnabled = true, CreatedAtUtc = DateTimeOffset.UtcNow },
            new Agent { Id = Guid.NewGuid(), Name = null, DeviceInfoJson = "{\"hostName\":42}", IsEnabled = true, CreatedAtUtc = DateTimeOffset.UtcNow },
            new Agent { Id = Guid.NewGuid(), Name = null, DeviceInfoJson = "{invalid-json", IsEnabled = true, CreatedAtUtc = DateTimeOffset.UtcNow },
            new Agent { Id = Guid.NewGuid(), Name = null, DeviceInfoJson = "[]", IsEnabled = true, CreatedAtUtc = DateTimeOffset.UtcNow },
            new Agent { Id = Guid.NewGuid(), Name = null, DeviceInfoJson = "null", IsEnabled = true, CreatedAtUtc = DateTimeOffset.UtcNow },
            new Agent { Id = Guid.NewGuid(), Name = "\t", DeviceInfoJson = null, IsEnabled = true, CreatedAtUtc = DateTimeOffset.UtcNow }
        };
        var literalAgentId = Guid.NewGuid();
        var unicodeHostAgentId = Guid.NewGuid();
        int tenantId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            tenantId = await db.Tenants
                .Where(tenant => tenant.Name == "OpenAPI tenant")
                .Select(tenant => tenant.Id).SingleAsync();
            foreach (var agent in onlineAgents)
            {
                agent.TenantId = tenantId;
            }
            db.Agents.Add(new Agent
            {
                Id = agentId,
                TenantId = tenantId,
                Name = $"{directorySearchPrefix} z-persisted offline agent",
                IsEnabled = true,
                CreatedAtUtc = DateTimeOffset.UtcNow
            });
            db.Agents.AddRange(onlineAgents);
            await db.SaveChangesAsync();

            var presence = scope.ServiceProvider.GetRequiredService<IClientPresenceRouter>();
            var options = scope.ServiceProvider.GetRequiredService<NetRatelAkkaOptions>();
            var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
            using var presenceBudget = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            foreach (var agent in onlineAgents)
            {
                var client = new ClientKey(tenantId, agent.Id);
                var connection = Guid.NewGuid();
                var received = clock.GetUtcNow();
                // Use the bounded provisional admission supplied by the gateway;
                // a reservation alone cannot make this pagination fixture online.
                var started = await presence.StartSessionAsync(new StartGatewayPresenceSession(
                    client,
                    connection,
                    Guid.NewGuid(),
                    "1.0",
                    "integration-agent",
                    [],
                    null,
                    received,
                    AuthenticationExpiresAtUtc: received.AddMinutes(10),
                    AdmissionExpiresAtUtc: received.AddSeconds(options.GatewayAdmissionTimeoutSeconds),
                    ProvisionalAdmission: true),
                    presenceBudget.Token);
                started.Disposition.Should().Be(PresenceMessageDisposition.Accepted);
                started.ConnectionEpoch.Should().BeGreaterThan(0);
                var heartbeat = await presence.RecordHeartbeatAsync(new RecordGatewayHeartbeat(
                    client, connection, started.ConnectionEpoch, Guid.NewGuid(), 1, clock.GetUtcNow()),
                    presenceBudget.Token);
                heartbeat.Disposition.Should().Be(PresenceMessageDisposition.Accepted);
            }
        }

        using var administrator = await _factory.CreateLocalAdministratorClientAsync();
        using var response = await administrator.GetAsync(
            $"/api/v2/client-presence/?online=false&search={Uri.EscapeDataString(directorySearchPrefix)}&limit=1");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var directory = await response.Content.ReadFromJsonAsync<ClientPresenceListDto>();
        directory.Should().NotBeNull();
        directory!.Mode.Should().Be("akka");
        directory.Items.Should().ContainSingle(agent =>
            agent.AgentId == agentId &&
            agent.TenantId == tenantId &&
            agent.TenantName == "OpenAPI tenant" &&
            !agent.Online &&
            agent.DisplayName == $"{directorySearchPrefix} z-persisted offline agent");

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            foreach (var agent in fallbackAgents)
            {
                agent.TenantId = tenantId;
            }

            db.Agents.AddRange(fallbackAgents);
            db.Agents.AddRange(
                new Agent { Id = literalAgentId, TenantId = tenantId, Name = "Needle%_literal", IsEnabled = true, CreatedAtUtc = DateTimeOffset.UtcNow },
                new Agent
                {
                    Id = unicodeHostAgentId,
                    TenantId = tenantId,
                    Name = null,
                    DeviceInfoJson = "{\"hostName\":\"caf\\u00e9\"}",
                    IsEnabled = true,
                    CreatedAtUtc = DateTimeOffset.UtcNow
                });
            await db.SaveChangesAsync();
        }

        var fallback = await administrator.GetFromJsonAsync<ClientPresenceListDto>(
            "/api/v2/client-presence/?search=Agent-&limit=10");
        fallback.Should().NotBeNull();
        foreach (var candidate in fallbackAgents)
        {
            var expectedDisplayName = $"Agent-{candidate.Id:N}"[..14];
            fallback!.Items.Should().ContainSingle(agent =>
                agent.AgentId == candidate.Id && agent.DisplayName == expectedDisplayName);
        }

        var exactFallbackId = fallbackAgents[1].Id;
        var exactFallbackName = $"Agent-{exactFallbackId:N}"[..14];
        var exactFallback = await administrator.GetFromJsonAsync<ClientPresenceListDto>(
            $"/api/v2/client-presence/?search={Uri.EscapeDataString(exactFallbackName)}");
        exactFallback.Should().NotBeNull();
        exactFallback!.Items.Should().ContainSingle(agent => agent.AgentId == exactFallbackId);

        var decodedUnicode = await administrator.GetFromJsonAsync<ClientPresenceListDto>(
            $"/api/v2/client-presence/?search={Uri.EscapeDataString("café")}");
        decodedUnicode.Should().NotBeNull();
        decodedUnicode!.Items.Should().ContainSingle(agent =>
            agent.AgentId == unicodeHostAgentId && agent.HostName == "café");

        var literal = await administrator.GetFromJsonAsync<ClientPresenceListDto>(
            "/api/v2/client-presence/?search=Needle%25%5F&limit=1");
        literal.Should().NotBeNull();
        literal!.Items.Should().ContainSingle(agent => agent.AgentId == literalAgentId);
    }

    [Fact]
    public async Task CommittedOwnership_DrivesDirectoryAuthorityWithoutAnActiveLocalProjection()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = budget.Token;
        using var administrator = await _factory.CreateLocalAdministratorClientAsync();
        var agentId = Guid.NewGuid();
        int tenantId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            tenantId = await db.Tenants
                .Where(tenant => tenant.Name == "OpenAPI tenant")
                .Select(tenant => tenant.Id).SingleAsync(ct);
            db.Agents.Add(new Agent
            {
                Id = agentId, TenantId = tenantId, Name = $"Committed directory {agentId:N}",
                IsEnabled = true, CreatedAtUtc = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(ct);
        }

        var client = new ClientKey(tenantId, agentId);
        var store = _factory.Services.GetRequiredService<IClientConnectionEpochStore>();
        var clock = _factory.Services.GetRequiredService<TimeProvider>();
        var options = _factory.Services.GetRequiredService<NetRatelAkkaOptions>();
        var projection = _factory.Services.GetRequiredService<IClientPresenceReadModel>();
        (await projection.GetClientSnapshotAsync(client, ct)).Should().BeNull();

        async Task<ClientPresenceDto> ReadEntryAsync()
        {
            using var response = await administrator.GetAsync(
                $"/api/v2/client-presence/?tenantId={tenantId}&search={agentId:D}&limit=1", ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var directory = await response.Content.ReadFromJsonAsync<ClientPresenceListDto>(cancellationToken: ct);
            directory.Should().NotBeNull();
            directory!.Mode.Should().Be("akka");
            return directory.Items.Should().ContainSingle().Which;
        }

        var received = clock.GetUtcNow();
        var reserved = await store.ReserveAsync(new AdmissionRequest(client, Guid.NewGuid(), Guid.NewGuid(), 0,
            received, received.AddSeconds(options.GatewayAdmissionTimeoutSeconds), received.AddMinutes(10),
            new("directory-first-owner", ["presence"], null)), ct);
        reserved.Disposition.Should().Be(OwnershipDisposition.Accepted);
        reserved.Reservation.Should().NotBeNull();
        (await store.GetCurrentAsync(client, ct)).Should().BeNull();
        (await ReadEntryAsync()).Online.Should().BeFalse();

        var reservation = reserved.Reservation!;
        var committed = await store.CommitAsync(reservation, new HeartbeatRequest(
            reservation.Owner, 1, clock.GetUtcNow()), ct);
        committed.Disposition.Should().Be(OwnershipDisposition.Accepted);
        committed.Current.Should().NotBeNull();
        committed.Current!.AcceptanceGuardAtUtc.Should().NotBeNull();
        (await projection.GetClientSnapshotAsync(client, ct)).Should().BeNull();
        var first = await ReadEntryAsync();
        first.Online.Should().BeTrue();
        first.IsAuthoritative.Should().BeTrue();
        first.Source.Should().Be("gateway");
        first.Authority.Should().Be("akka");
        first.AgentVersion.Should().Be("directory-first-owner");

        // A real later committed owner replaces the first one. The physical
        // cache deliberately retains only the first owner's stale observation.
        received = clock.GetUtcNow();
        var replacement = await store.ReserveAsync(new AdmissionRequest(client, Guid.NewGuid(), Guid.NewGuid(), 0,
            received, received.AddSeconds(options.GatewayAdmissionTimeoutSeconds), received.AddMinutes(10),
            new("directory-current-owner", ["presence"], null)), ct);
        replacement.Disposition.Should().Be(OwnershipDisposition.Accepted);
        replacement.Reservation.Should().NotBeNull();
        replacement.Reservation!.Owner.Epoch.Should().BeGreaterThan(reservation.Owner.Epoch);
        var current = await store.CommitAsync(replacement.Reservation, new HeartbeatRequest(
            replacement.Reservation.Owner, 1, clock.GetUtcNow()), ct);
        current.Disposition.Should().Be(OwnershipDisposition.Accepted);
        current.Current.Should().NotBeNull();

        var readModel = await _factory.Services.GetRequiredService<IRequiredActor<ClientPresenceReadModelRegion>>().GetAsync(ct);
        readModel.Tell(new TrackClientPresenceSnapshot(new ClientPresenceSnapshot(
            client, ClientPresenceStatus.Online, reservation.Owner.Epoch, reservation.Owner.ConnectionId,
            1, committed.Current.LastReceivedAtUtc, "stale-physical-owner", ["stale-capability"], null,
            "akka", IsAuthoritative: false, LatencyMilliseconds: 7,
            LatencyMeasuredAtUtc: clock.GetUtcNow(), LatencyExpiresAtUtc: clock.GetUtcNow().AddMinutes(1))));
        var stale = await readModel.Ask<ClientPresenceReadModelPointSnapshot>(
            new GetClientPresenceReadModelByKey(client), options.AskTimeout, ct);
        stale.Snapshot.Should().NotBeNull();
        stale.Snapshot!.ConnectionId.Should().Be(reservation.Owner.ConnectionId);
        stale.Snapshot.IsAuthoritative.Should().BeFalse();

        var visible = await ReadEntryAsync();
        visible.Online.Should().BeTrue();
        visible.IsAuthoritative.Should().BeTrue();
        visible.Source.Should().Be("gateway");
        visible.Authority.Should().Be("akka");
        visible.AgentVersion.Should().Be("directory-current-owner");
        visible.Capabilities.Should().Equal("presence");
        visible.LatencyMilliseconds.Should().BeNull();

        var retired = await store.RetireAsync(replacement.Reservation.Owner,
            RetirementReason.ExplicitClose, null, ct);
        retired.Disposition.Should().Be(OwnershipDisposition.Accepted);
        (await ReadEntryAsync()).Online.Should().BeFalse();
        var online = await administrator.GetFromJsonAsync<ClientPresenceListDto>(
            $"/api/v2/client-presence/?tenantId={tenantId}&online=true&search={agentId:D}&limit=1", ct);
        online.Should().NotBeNull();
        online!.Items.Should().BeEmpty();
    }
}
