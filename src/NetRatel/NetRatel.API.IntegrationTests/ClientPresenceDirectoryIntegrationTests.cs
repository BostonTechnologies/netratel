using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.API.Gateway;
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
        tenantId = await db.Tenants.Select(tenant => tenant.Id).SingleAsync();
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
            tenantId = await db.Tenants.Select(tenant => tenant.Id).SingleAsync();
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
            foreach (var agent in onlineAgents)
            {
                await presence.StartSessionAsync(new StartGatewayPresenceSession(
                    new ClientKey(tenantId, agent.Id),
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    "1.0",
                    "integration-agent",
                    [],
                    null,
                    DateTimeOffset.UtcNow),
                    CancellationToken.None);
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
}
