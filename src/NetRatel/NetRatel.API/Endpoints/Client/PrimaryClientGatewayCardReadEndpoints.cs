using Microsoft.AspNetCore.Mvc;
using NetRatel.Akka.Configuration;
using NetRatel.API.Gateway;
using NetRatel.Application.Agents;
using NetRatel.Application.Presence;
using NetRatel.Application.Telemetry;
using NetRatel.Shared.Contracts;

namespace NetRatel.API.Endpoints.Client;

/// <summary>
/// Additive, mapping-backed read surface for the primary client-card canary.
/// It never changes the V1 card endpoint: callers must opt in through the
/// primary-card read flag and receive an explicit source for every field.
/// </summary>
public static class PrimaryClientGatewayCardReadEndpoints
{
    public static IEndpointRouteBuilder MapPrimaryClientGatewayCardReadEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v2/tenants/{tenantId:int}/primary-client-cards/gateway", ListAsync)
            .WithName("PrimaryClientGatewayCard_List")
            .WithTags("Primary Client Gateway Cards")
            .RequireAuthorization("Operator")
            .Produces<PrimaryClientGatewayCardListResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    /// <summary>
    /// Registers the active gateway-control route only when a caller has
    /// composed the Dev operational authority. The normal API bootstrap keeps
    /// it unavailable until target eligibility and audit requirements exist.
    /// </summary>
    public static IEndpointRouteBuilder MapPrimaryClientGatewayCardActionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v2/tenants/{tenantId:int}/primary-client-cards/{primaryClientIdentity}/gateway/ping", PingAsync)
            .WithName("PrimaryClientGatewayCard_Ping")
            .WithTags("Primary Client Gateway Cards")
            .RequireAuthorization("Operator")
            .Produces<AgentControlPingResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status504GatewayTimeout);

        return app;
    }

    private static async Task<IResult> PingAsync(
        int tenantId,
        string primaryClientIdentity,
        [FromServices] NetRatelAkkaMigrationOptions options,
        IPrimaryClientAgentBindingService bindings,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (!options.IsPrimaryCardGatewayActionActive || !options.IsPingAuthorityActive)
        {
            return Results.NotFound();
        }

        var binding = await bindings.GetByPrimaryClientIdentityAsync(tenantId, primaryClientIdentity, cancellationToken)
            .ConfigureAwait(false);
        if (binding is not { Status: PrimaryClientAgentBindingStatus.Bound, AgentId: { } agentId })
        {
            return Results.NotFound();
        }

        var controlSessions = services.GetRequiredService<IAgentControlSessionRegistry>();
        try
        {
            var ping = await controlSessions.RequestPingAsync(
                new ClientKey(tenantId, agentId),
                TimeSpan.FromSeconds(5),
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(new AgentControlPingResponse(
                ping.Client.TenantId,
                ping.Client.AgentId,
                ping.RequestId,
                ping.SentAtUtc,
                ping.ReceivedAtUtc,
                ping.AgentRespondedAtUtc,
                Math.Max(0, ping.RoundTripTime.TotalMilliseconds),
                "akka"));
        }
        catch (AgentControlSessionUnavailableException)
        {
            return Results.Conflict(new { code = "agent_control_session_unavailable" });
        }
        catch (TimeoutException)
        {
            return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
    }

    private static async Task<IResult> ListAsync(
        int tenantId,
        [FromServices] NetRatelAkkaMigrationOptions options,
        IPrimaryClientAgentBindingService bindings,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (!options.IsPrimaryCardGatewayReadActive)
        {
            return Results.NotFound();
        }

        var presenceReadModel = services.GetRequiredService<IClientPresenceReadModel>();
        var presence = await presenceReadModel.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var presenceByClient = presence.Items.ToDictionary(snapshot => snapshot.Client);

        IReadOnlyDictionary<ClientKey, TelemetrySnapshot> telemetryByClient = new Dictionary<ClientKey, TelemetrySnapshot>();
        if (options.IsTelemetryAuthorityActive)
        {
            var telemetryRouter = services.GetRequiredService<IClientTelemetryRouter>();
            telemetryByClient = (await telemetryRouter.GetReadModelAsync(cancellationToken).ConfigureAwait(false)).Snapshots
                .ToDictionary(snapshot => snapshot.Client);
        }

        var controlSessions = services.GetService<IAgentControlSessionRegistry>();
        var terminals = services.GetService<IAgentTerminalSessionRegistry>();
        var mapped = await bindings.ListBoundAsync(tenantId, cancellationToken).ConfigureAwait(false);
        var cards = mapped.Select(binding => Map(binding, presenceByClient, telemetryByClient, controlSessions, terminals, options, presence.Revision)).ToArray();

        return Results.Ok(new PrimaryClientGatewayCardListResponse(
            tenantId,
            presence.Revision,
            "gateway-mapped-canary",
            cards));
    }

    private static PrimaryClientGatewayCardResponse Map(
        PrimaryClientAgentBindingDto binding,
        IReadOnlyDictionary<ClientKey, ClientPresenceSnapshot> presenceByClient,
        IReadOnlyDictionary<ClientKey, TelemetrySnapshot> telemetryByClient,
        IAgentControlSessionRegistry? controlSessions,
        IAgentTerminalSessionRegistry? terminals,
        NetRatelAkkaMigrationOptions options,
        long revision)
    {
        var client = new ClientKey(binding.TenantId, binding.AgentId!.Value);
        presenceByClient.TryGetValue(client, out var presence);
        telemetryByClient.TryGetValue(client, out var telemetry);
        var terminalSupported = presence?.Capabilities.Contains("terminal-gateway", StringComparer.OrdinalIgnoreCase) == true;
        var terminalAvailability = terminals?.GetAvailability(client);

        return new(
            binding.PrimaryClientIdentity,
            binding.TenantId,
            binding.AgentId.Value,
            binding.BindingId,
            presence?.Status == ShadowPresenceStatus.Online,
            presence?.LastReceivedAtUtc,
            presence?.AgentVersion,
            presence?.Capabilities ?? Array.Empty<string>(),
            FieldSource(presence?.Source, presence?.IsAuthoritative ?? false, "unobserved"),
            telemetry is null
                ? null
                : new PrimaryClientGatewayTelemetrySummary(
                    telemetry.ObservedAtUtc,
                    telemetry.ReceivedAtUtc,
                    telemetry.Cpu?.UsagePercent,
                    telemetry.Memory?.UsagePercent,
                    telemetry.Source,
                    telemetry.IsAuthoritative),
            MapLatency(client, controlSessions, options),
            revision,
            new GatewayTerminalCapabilityDto(
                terminalSupported,
                terminalAvailability?.AvailableShells ?? Array.Empty<string>(),
                terminalAvailability is not null,
                terminalSupported ? terminalAvailability is null ? "terminal_transport_not_admitted" : null : "terminal_not_supported",
                terminalAvailability?.RegisteredAtUtc));
    }

    private static PrimaryClientGatewayLatencySummary MapLatency(
        ClientKey client,
        IAgentControlSessionRegistry? controlSessions,
        NetRatelAkkaMigrationOptions options)
    {
        if (options.IsPingAuthorityActive && controlSessions is not null && controlSessions.TryGetLatestPing(client, out var ping))
        {
            return new(
                Math.Max(0, ping.RoundTripTime.TotalMilliseconds),
                ping.ReceivedAtUtc,
                "akka",
                true);
        }

        return new(null, null, "unavailable", false);
    }

    private static PrimaryClientGatewayFieldSource FieldSource(string? authority, bool authoritative, string fallback) =>
        new(authority ?? fallback, authoritative);
}

public sealed record PrimaryClientGatewayCardListResponse(
    int TenantId,
    long Revision,
    string Source,
    IReadOnlyList<PrimaryClientGatewayCardResponse> Items);

public sealed record PrimaryClientGatewayCardResponse(
    string PrimaryClientIdentity,
    int TenantId,
    Guid AgentId,
    Guid BindingId,
    bool Online,
    DateTimeOffset? LastHeartbeatAtUtc,
    string? AgentVersion,
    IReadOnlyList<string> Capabilities,
    PrimaryClientGatewayFieldSource Presence,
    PrimaryClientGatewayTelemetrySummary? Telemetry,
    PrimaryClientGatewayLatencySummary Latency,
    long Revision,
    GatewayTerminalCapabilityDto? Terminal = null);

public sealed record PrimaryClientGatewayFieldSource(string Authority, bool IsAuthoritative);

public sealed record PrimaryClientGatewayTelemetrySummary(
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset ReceivedAtUtc,
    double? CpuUsagePercent,
    double? MemoryUsagePercent,
    string Authority,
    bool IsAuthoritative);

public sealed record PrimaryClientGatewayLatencySummary(
    double? RoundTripMilliseconds,
    DateTimeOffset? MeasuredAtUtc,
    string Authority,
    bool IsAuthoritative);
