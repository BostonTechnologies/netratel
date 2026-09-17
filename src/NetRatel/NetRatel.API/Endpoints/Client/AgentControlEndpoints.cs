using Microsoft.AspNetCore.Mvc;
using NetRatel.Akka.Configuration;
using NetRatel.API.Gateway;
using NetRatel.Application.Presence;

namespace NetRatel.API.Endpoints.Client;

/// <summary>
/// V2 control endpoints are keyed by the authenticated agent directory ID, not
/// a Spacetime identity. They are intentionally separate from existing v1
/// client action routes while the inventory mapping migration is incomplete.
/// </summary>
public static class AgentControlEndpoints
{
    public static IEndpointRouteBuilder MapAgentControlEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v2/agents/{tenantId:int}/{agentId:guid}/ping", PingAsync)
            .WithName("AgentControl_Ping")
            .WithTags("Agent Control")
            .RequireAuthorization("Operator")
            .Produces<AgentControlPingResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .Produces(StatusCodes.Status504GatewayTimeout);

        return app;
    }

    private static async Task<IResult> PingAsync(
        int tenantId,
        Guid agentId,
        [FromServices] NetRatelAkkaMigrationOptions options,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (!options.IsPingAuthorityActive)
        {
            return Results.NotFound();
        }

        var client = new ClientKey(tenantId, agentId);
        var registry = services.GetRequiredService<IAgentControlSessionRegistry>();
        try
        {
            var result = await registry.RequestPingAsync(
                client,
                TimeSpan.FromSeconds(5),
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(new AgentControlPingResponse(
                result.Client.TenantId,
                result.Client.AgentId,
                result.RequestId,
                result.SentAtUtc,
                result.ReceivedAtUtc,
                result.AgentRespondedAtUtc,
                Math.Max(0, result.RoundTripTime.TotalMilliseconds),
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
}

public sealed record AgentControlPingResponse(
    int TenantId,
    Guid AgentId,
    Guid RequestId,
    DateTimeOffset SentAtUtc,
    DateTimeOffset ReceivedAtUtc,
    DateTimeOffset AgentRespondedAtUtc,
    double RoundTripMilliseconds,
    string Authority);
