using System.Data.Common;
using Akka.Actor;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetRatel.API.Gateway;
using NetRatel.API.Services.AgentDirectory;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts;

namespace NetRatel.API.Endpoints.Client;

/// <summary>
/// Exposes persisted agent directory entries enriched with the process-local
/// Akka gateway presence projection. Live presence never owns the directory.
/// </summary>
public static class ClientPresenceReadEndpoints
{
    private const int DefaultLimit = 100;
    private const int MaximumLimit = 100;

    public static IEndpointRouteBuilder MapClientPresenceReadEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/client-presence")
            .WithTags("Client Presence")
            .RequireAuthorization("InstanceAdministrator");

        group.MapGet("/", async (
            [FromQuery] int? tenantId,
            [FromQuery] string? search,
            [FromQuery] bool? online,
            [FromQuery] int? limit,
            [FromServices] IClientPresenceReadModel readModel,
            [FromServices] IAgentTerminalSessionRegistry terminals,
            [FromServices] IAgentFileGatewaySessionRegistry files,
            [FromServices] OrchestratorDbContext db,
            [FromServices] ILoggerFactory loggerFactory,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var boundedLimit = limit ?? DefaultLimit;
            if (boundedLimit is < 1 or > MaximumLimit)
            {
                return Results.BadRequest(new { code = "invalid_limit", message = $"limit must be between 1 and {MaximumLimit}." });
            }

            try
            {
                var projection = await readModel.GetSnapshotAsync(ct).ConfigureAwait(false);
                var snapshots = projection.Items.ToDictionary(snapshot => snapshot.Client);

                var agents = from agent in db.Agents.AsNoTracking()
                             join tenant in db.Tenants.AsNoTracking()
                                 on agent.TenantId equals tenant.Id
                             select new
                             {
                                 agent.TenantId,
                                 agent.Id,
                                 agent.Name,
                                 agent.IsEnabled,
                                 agent.DeviceInfoJson,
                                 TenantName = tenant.Name
                             };
                if (tenantId.HasValue)
                {
                    agents = agents.Where(agent => agent.TenantId == tenantId.Value);
                }

                if (!string.IsNullOrWhiteSpace(search))
                {
                    var term = search.Trim();
                    var pattern = $"%{EscapeLikePattern(term)}%";
                    // DeviceInfoJson is parsed and unescaped after paging; raw SQL text matching can omit a real host/OS match.
                    agents = agents.Where(agent =>
                        (agent.Name != null && EF.Functions.ILike(agent.Name, pattern, "\\")) ||
                        agent.DeviceInfoJson != null ||
                        EF.Functions.ILike("Agent-" + agent.Id.ToString().Substring(0, 8), pattern, "\\") ||
                        EF.Functions.ILike(agent.Id.ToString(), pattern, "\\"));
                }

                const int batchSize = 100;
                var matches = new List<ClientPresenceDto>(boundedLimit);
                var offset = 0;
                while (matches.Count < boundedLimit)
                {
                    var page = await agents
                        .OrderBy(agent => agent.TenantId)
                        .ThenBy(agent => agent.Name)
                        .ThenBy(agent => agent.Id)
                        .Skip(offset)
                        .Take(batchSize)
                        .ToListAsync(ct)
                        .ConfigureAwait(false);
                    if (page.Count == 0)
                    {
                        break;
                    }

                    offset += page.Count;
                    foreach (var row in page)
                    {
                        var agent = AgentDirectoryPresentation.Create(
                            row.TenantId,
                            row.Id,
                            row.Name,
                            row.IsEnabled,
                            row.DeviceInfoJson,
                            row.TenantName);
                        var item = Map(agent, snapshots, terminals, files, projection.Revision);
                        if ((!online.HasValue || item.Online == online.Value) && Matches(item, search))
                        {
                            matches.Add(item);
                            if (matches.Count == boundedLimit)
                            {
                                break;
                            }
                        }
                    }

                    if (page.Count < batchSize)
                    {
                        break;
                    }
                }

                return Results.Ok(new ClientPresenceListDto(
                    "akka",
                    projection.Revision,
                    matches));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsDependencyFailure(exception))
            {
                var logger = loggerFactory.CreateLogger("NetRatel.API.Endpoints.Client.ClientPresenceReadEndpoints");
                logger.LogWarning(
                    "Client presence directory dependency failed for trace {TraceId} with {FailureType}.",
                    httpContext.TraceIdentifier,
                    exception.GetType().Name);
                return Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Client directory is temporarily unavailable.",
                    extensions: new Dictionary<string, object?>
                    {
                        ["code"] = "client_directory_unavailable",
                        ["traceId"] = httpContext.TraceIdentifier
                    });
            }
        })
        .WithName("ClientPresence_List")
        .Produces<ClientPresenceListDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    private static ClientPresenceDto Map(
        AgentDirectoryPresentation agent,
        IReadOnlyDictionary<ClientKey, ClientPresenceSnapshot> snapshots,
        IAgentTerminalSessionRegistry terminals,
        IAgentFileGatewaySessionRegistry files,
        long revision)
    {
        var key = new ClientKey(agent.TenantId, agent.AgentId);
        snapshots.TryGetValue(key, out var snapshot);
        var terminal = MapTerminal(snapshot, terminals.GetAvailability(key));
        var file = MapFile(snapshot, files.GetAvailability(key));

        return new ClientPresenceDto(
            PresenceId: $"gateway:{agent.TenantId}:{agent.AgentId:N}",
            TenantId: agent.TenantId,
            AgentId: agent.AgentId,
            DisplayName: agent.DisplayName,
            HostName: agent.HostName,
            OperatingSystem: agent.OperatingSystem,
            Architecture: agent.Architecture,
            Online: snapshot?.Status == ClientPresenceStatus.Online,
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
            File: file,
            LatencyMilliseconds: snapshot?.LatencyMilliseconds,
            LatencyMeasuredAtUtc: snapshot?.LatencyMeasuredAtUtc,
            LatencyExpiresAtUtc: snapshot?.LatencyExpiresAtUtc);
    }

    internal static GatewayTerminalCapabilityDto MapTerminal(
        ClientPresenceSnapshot? presence,
        GatewayTerminalAvailability? availability)
    {
        var supported = presence?.Capabilities.Contains("terminal-gateway", StringComparer.OrdinalIgnoreCase) == true;
        var ready = supported && availability is not null &&
            MatchesAuthoritativePresence(presence, availability.ConnectionId, availability.ConnectionEpoch);
        var reason = presence?.Status != ClientPresenceStatus.Online
            ? "terminal_presence_offline"
            : presence?.IsAuthoritative != true
                ? "terminal_presence_not_authoritative"
                : !supported
                    ? "terminal_not_supported"
                    : availability is null
                        ? "terminal_transport_not_admitted"
                        : !ready ? "terminal_transport_fenced" : null;
        return new GatewayTerminalCapabilityDto(
            supported,
            availability?.AvailableShells ?? Array.Empty<string>(),
            ready,
            reason,
            availability?.RegisteredAtUtc);
    }

    private static bool MatchesAuthoritativePresence(ClientPresenceSnapshot? presence, Guid connectionId, ulong epoch) =>
        connectionId != Guid.Empty && epoch != 0 &&
        presence is { Status: ClientPresenceStatus.Online, IsAuthoritative: true, ConnectionEpoch: > 0 } &&
        presence.ConnectionId == connectionId && (ulong)presence.ConnectionEpoch.Value == epoch;

    internal static GatewayFileCapabilityDto MapFile(
        ClientPresenceSnapshot? presence,
        GatewayFileGatewayAvailability? availability)
    {
        // The authenticated hello advertises file-gateway when the agent
        // implementation supports it; active sessions are reported separately.
        var advertised = presence?.Capabilities.Contains("file-gateway", StringComparer.OrdinalIgnoreCase) == true;
        var sessionActive = availability is not null;
        var fenceMatchesPresence = availability is not null &&
            MatchesAuthoritativePresence(presence, availability.ConnectionId, availability.ConnectionEpoch);
        var readinessReason = presence?.Status != ClientPresenceStatus.Online
            ? "file_gateway_presence_offline"
            : presence?.IsAuthoritative != true
                ? "file_gateway_presence_not_authoritative"
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

    private static string EscapeLikePattern(string term) =>
        term.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static bool Contains(string? value, string term) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains(term, StringComparison.OrdinalIgnoreCase);

    private static bool IsDependencyFailure(Exception exception) => exception switch
    {
        DbException or DbUpdateException or TimeoutException or AskTimeoutException => true,
        OperationCanceledException => true,
        _ => exception.InnerException is not null && IsDependencyFailure(exception.InnerException)
    };

}
