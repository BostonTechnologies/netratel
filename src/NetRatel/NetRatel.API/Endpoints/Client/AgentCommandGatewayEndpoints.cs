using Akka.Actor;
using Microsoft.AspNetCore.Mvc;
using NetRatel.API.Gateway;
using NetRatel.Akka.Observability;
using NetRatel.Application.Commands;
using NetRatel.Application.Presence;

namespace NetRatel.API.Endpoints.Client;

/// <summary>
/// Explicit Agent-ID keyed command surface. It returns a conflict when the
/// admitted command stream is unavailable instead of writing a legacy dispatch row.
/// </summary>
public static class AgentCommandGatewayEndpoints
{
    private const string Authority = "akka";
    private const string Feature = "commands";

    public static IEndpointRouteBuilder MapAgentCommandGatewayEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/agents/{tenantId:int}/{agentId:guid}/commands")
            .WithTags("Gateway Commands")
            .RequireAuthorization("CommandOperator");

        group.MapPost("", DispatchAsync)
            .Produces<GatewayCommandDispatchResponse>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status409Conflict);
        group.MapPost("/{commandId}/cancel", CancelAsync)
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status409Conflict);
        group.MapGet("/{commandId}", GetAsync)
            .Produces<CommandState>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return app;
    }

    private static async Task<IResult> DispatchAsync(
        int tenantId,
        Guid agentId,
        [FromBody] GatewayCommandDispatchRequest request,
        IHostEnvironment environment,
        [FromServices] IAgentCommandAuthorityDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.TaskType) || request.TaskType.Length > 128 || request.PayloadJson?.Length > 64 * 1024)
        {
            return Results.BadRequest("A bounded task_type and payload_json are required.");
        }

        var commandId = Guid.NewGuid().ToString("N");
        var correlationId = string.IsNullOrWhiteSpace(request.CorrelationId) ? commandId : request.CorrelationId.Trim();
        var requestedAt = DateTimeOffset.UtcNow;
        try
        {
            await dispatcher.DispatchAsync(new ClientKey(tenantId, agentId), commandId, correlationId, request.TaskType.Trim(), request.PayloadJson ?? string.Empty, request.Environment, cancellationToken).ConfigureAwait(false);
            return Results.Accepted($"/api/v2/agents/{tenantId}/{agentId:D}/commands/{commandId}", new GatewayCommandDispatchResponse(commandId, correlationId, Authority));
        }
        catch (AgentCommandGatewaySessionUnavailableException exception)
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, environment.EnvironmentName);
            return Results.Conflict(new { code = "agent_command_session_unavailable", detail = exception.Message });
        }
    }

    private static async Task<IResult> CancelAsync(
        int tenantId,
        Guid agentId,
        string commandId,
        [FromBody] GatewayCommandCancelRequest? request,
        IHostEnvironment environment,
        [FromServices] IAgentCommandGatewaySessionRegistry sessions,
        CancellationToken cancellationToken)
    {
        var client = new ClientKey(tenantId, agentId);
        if (!sessions.IsAvailable(client))
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, environment.EnvironmentName);
            return Results.Conflict(new { code = "agent_command_session_unavailable" });
        }

        var reason = string.IsNullOrWhiteSpace(request?.Reason) ? "operator_cancelled" : request.Reason.Trim();
        await sessions.CancelAsync(client, commandId, reason, cancellationToken).ConfigureAwait(false);
        NetRatelAkkaTelemetry.RecordAuthorityEvent(Feature, Authority, environment.EnvironmentName);
        return Results.Accepted();
    }

    private static async Task<IResult> GetAsync(
        int tenantId,
        Guid agentId,
        string commandId,
        [FromServices] IClientCommandRouter commandRouter,
        CancellationToken cancellationToken)
    {
        try
        {
            var state = await commandRouter.GetStateAsync(new CommandKey(tenantId, commandId), cancellationToken).ConfigureAwait(false);
            if (string.Equals(state.Source, "akka-persistence-unavailable", StringComparison.Ordinal))
            {
                return CommandStateUnavailable();
            }

            return state.Client == new ClientKey(tenantId, agentId)
                ? Results.Ok(state)
                : Results.NotFound();
        }
        catch (AskTimeoutException)
        {
            return CommandStateUnavailable();
        }
    }

    private static IResult CommandStateUnavailable() => Results.Problem(
        title: "Command state is temporarily unavailable.",
        detail: "The command state store is unavailable.",
        statusCode: StatusCodes.Status503ServiceUnavailable);

}

public sealed record GatewayCommandDispatchRequest(string TaskType, string? PayloadJson, int Environment, string? CorrelationId);
public sealed record GatewayCommandCancelRequest(string? Reason);
public sealed record GatewayCommandDispatchResponse(string CommandId, string CorrelationId, string Authority);
