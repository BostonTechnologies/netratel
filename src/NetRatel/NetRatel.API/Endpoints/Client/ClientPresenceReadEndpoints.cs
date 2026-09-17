using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NetRatel.Akka.Configuration;
using NetRatel.API.Gateway;
using NetRatel.API.Services.AgentDirectory;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts;

namespace NetRatel.API.Endpoints.Client;

/// <summary>
/// Exposes the process-local Akka gateway presence projection. This is a
/// presence-only gateway API: it deliberately does not reuse legacy client
/// identity or expose remote-action endpoints.
/// </summary>
public static class ClientPresenceReadEndpoints
{
    private const int DefaultLimit = 100;
    private const int MaximumLimit = 100;

    public static IEndpointRouteBuilder MapClientPresenceReadEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/client-presence")
            .WithTags("Client Presence")
            .RequireAuthorization("Operator");

        group.MapGet("/", async (
            [FromQuery] int? tenantId,
            [FromQuery] string? search,
            [FromQuery] bool? online,
            [FromQuery] int? limit,
            [FromServices] NetRatelAkkaMigrationOptions options,
            IServiceProvider services,
            [FromServices] OrchestratorDbContext db,
            CancellationToken ct) =>
        {
            var boundedLimit = limit ?? DefaultLimit;
            if (boundedLimit is < 1 or > MaximumLimit)
            {
                return Results.BadRequest(new { code = "invalid_limit", message = $"limit must be between 1 and {MaximumLimit}." });
            }

            if (!options.IsGatewayPresenceReadModelEnabled)
            {
                return Results.NotFound();
            }

            var readModel = services.GetRequiredService<IClientPresenceReadModel>();
            var terminals = services.GetService<IAgentTerminalSessionRegistry>();
            var files = services.GetService<IAgentFileGatewaySessionRegistry>();
            var projection = await readModel.GetSnapshotAsync(ct).ConfigureAwait(false);
            var snapshots = projection.Items.ToDictionary(snapshot => snapshot.Client);

            var agents = db.Agents.AsNoTracking().AsQueryable();
            if (tenantId.HasValue)
            {
                agents = agents.Where(agent => agent.TenantId == tenantId.Value);
            }

            var rows = await agents
                .Join(
                    db.Tenants.AsNoTracking(),
                    agent => agent.TenantId,
                    tenant => tenant.Id,
                    (agent, tenant) => new { agent, tenant.Name })
                .OrderBy(agent => agent.agent.TenantId)
                .ThenBy(agent => agent.agent.Name)
                .ThenBy(agent => agent.agent.Id)
                .Select(agent => AgentDirectoryPresentation.Create(
                    agent.agent.TenantId,
                    agent.agent.Id,
                    agent.agent.Name,
                    agent.agent.IsEnabled,
                    agent.agent.DeviceInfoJson,
                    agent.Name))
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var items = rows
                .Select(agent => Map(agent, snapshots, terminals, files, projection.Revision))
                .Where(item => !online.HasValue || item.Online == online.Value)
                .Where(item => Matches(item, search))
                .Take(boundedLimit)
                .ToArray();

            return Results.Ok(new ClientPresenceListDto(
                "akka",
                projection.Revision,
                items));
        })
        .WithName("ClientPresence_List")
        .Produces<ClientPresenceListDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    private static ClientPresenceDto Map(
        AgentDirectoryPresentation agent,
        IReadOnlyDictionary<ClientKey, ClientPresenceSnapshot> snapshots,
        IAgentTerminalSessionRegistry? terminals,
        IAgentFileGatewaySessionRegistry? files,
        long revision)
    {
        var key = new ClientKey(agent.TenantId, agent.AgentId);
        snapshots.TryGetValue(key, out var snapshot);
        var supported = snapshot?.Capabilities.Contains("terminal-gateway", StringComparer.OrdinalIgnoreCase) == true;
        var availability = terminals?.GetAvailability(key);
        var terminal = new GatewayTerminalCapabilityDto(
            supported,
            availability?.AvailableShells ?? Array.Empty<string>(),
            availability is not null,
            supported ? availability is null ? "terminal_transport_not_admitted" : null : "terminal_not_supported",
            availability?.RegisteredAtUtc);
        var file = MapFile(snapshot, files?.GetAvailability(key));

        return new ClientPresenceDto(
            PresenceId: $"gateway:{agent.TenantId}:{agent.AgentId:N}",
            TenantId: agent.TenantId,
            AgentId: agent.AgentId,
            DisplayName: agent.DisplayName,
            HostName: agent.HostName,
            OperatingSystem: agent.OperatingSystem,
            Architecture: agent.Architecture,
            Online: snapshot?.Status == ShadowPresenceStatus.Online,
            IsEnabled: agent.IsEnabled,
            LastReceivedAtUtc: snapshot?.LastReceivedAtUtc,
            AgentVersion: snapshot?.AgentVersion,
            Capabilities: snapshot?.Capabilities ?? Array.Empty<string>(),
            Source: "gateway",
            Authority: snapshot?.Source ?? "unobserved",
            IsAuthoritative: snapshot?.IsAuthoritative ?? false,
            Revision: revision,
            Terminal: terminal,
            TenantName: agent.TenantName,
            File: file);
    }

    private static GatewayFileCapabilityDto MapFile(
        ClientPresenceSnapshot? presence,
        GatewayFileGatewayAvailability? availability)
    {
        // The authenticated hello advertises file-gateway only when the client
        // configuration enables it, so "configured" and "advertised" are
        // distinct fields with the same current source of truth.
        var advertised = presence?.Capabilities.Contains("file-gateway", StringComparer.OrdinalIgnoreCase) == true;
        var sessionActive = availability is not null;
        var fenceMatchesPresence = availability is not null &&
            presence is { Status: ShadowPresenceStatus.Online, ConnectionId: var connectionId, ConnectionEpoch: var epoch } &&
            connectionId == availability.ConnectionId && epoch == checked((long)availability.ConnectionEpoch);
        var readinessReason = presence?.Status != ShadowPresenceStatus.Online
            ? "file_gateway_presence_offline"
            : !advertised
                ? "file_gateway_not_advertised"
                : !sessionActive
                    ? "file_gateway_not_admitted"
                    : !fenceMatchesPresence
                        ? "file_gateway_fenced"
                        : null;

        return new GatewayFileCapabilityDto(
            Configured: advertised,
            Advertised: advertised,
            SessionActive: sessionActive,
            FenceMatchesPresence: fenceMatchesPresence,
            ClientVersion: presence?.AgentVersion,
            NegotiatedCapabilities: availability?.NegotiatedCapabilities ?? Array.Empty<string>(),
            PresenceObservedAtUtc: presence?.LastReceivedAtUtc,
            SessionRegisteredAtUtc: availability?.RegisteredAtUtc,
            ReadinessReason: readinessReason);
    }

    private static bool Matches(ClientPresenceDto item, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        var term = search.Trim();
        return Contains(item.DisplayName, term) ||
               Contains(item.HostName, term) ||
               Contains(item.OperatingSystem, term) ||
               item.AgentId.ToString("D").Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Contains(string? value, string term) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains(term, StringComparison.OrdinalIgnoreCase);

}
