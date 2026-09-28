using NetRatel.Akka.Observability;
using NetRatel.Application.Commands;
using NetRatel.Application.Presence;

namespace NetRatel.API.Gateway;

/// <summary>
/// Single server-side create/dispatch authority for an admitted agent command
/// session. It records the two server lifecycle transitions before exposing
/// work to the client; failure to find a session remains explicit.
/// </summary>
public interface IAgentCommandAuthorityDispatcher
{
    bool IsAvailable(ClientKey client);

    Task DispatchAsync(
        ClientKey client,
        string commandId,
        string correlationId,
        string taskType,
        string payloadJson,
        int environment,
        CancellationToken cancellationToken);
}

public sealed class AgentCommandAuthorityDispatcher(
    IClientCommandRouter commandRouter,
    IAgentCommandGatewaySessionRegistry sessions,
    IHostEnvironment environment) : IAgentCommandAuthorityDispatcher
{
    private const string Authority = "akka";
    private const string Feature = "commands";

    public bool IsAvailable(ClientKey client) => sessions.IsAvailable(client);

    public async Task DispatchAsync(
        ClientKey client,
        string commandId,
        string correlationId,
        string taskType,
        string payloadJson,
        int environmentValue,
        CancellationToken cancellationToken)
    {
        if (!sessions.IsAvailable(client))
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, environment.EnvironmentName);
            throw new AgentCommandGatewaySessionUnavailableException(client);
        }

        var requestedAt = DateTimeOffset.UtcNow;
        NetRatelAkkaTelemetry.RecordAuthorityRequest(Feature, Authority, environment.EnvironmentName);
        using var activity = NetRatelAkkaTelemetry.StartAuthorityActivity(Feature, Authority, "dispatch", environment.EnvironmentName);
        await RecordRequiredAsync(new CommandLifecycleEvent(client, commandId, correlationId, requestedAt, requestedAt, 1, 1, CommandLifecycleStatus.Created, Authority, true), cancellationToken).ConfigureAwait(false);
        await RecordRequiredAsync(new CommandLifecycleEvent(client, commandId, correlationId, requestedAt, requestedAt, 2, 2, CommandLifecycleStatus.Dispatched, Authority, true), cancellationToken).ConfigureAwait(false);
        await sessions.DispatchAsync(client, new CommandGatewayDispatch(commandId, correlationId, requestedAt, 3, 3, taskType, payloadJson, environmentValue, client.TenantId), cancellationToken).ConfigureAwait(false);
        NetRatelAkkaTelemetry.RecordAuthorityEvent(Feature, Authority, environment.EnvironmentName);
    }

    private async Task RecordRequiredAsync(CommandLifecycleEvent lifecycle, CancellationToken cancellationToken)
    {
        var result = await commandRouter.RecordAsync(new RecordCommandLifecycleEvent(lifecycle), cancellationToken).ConfigureAwait(false);
        if (result.Disposition != CommandMessageDisposition.Accepted)
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, environment.EnvironmentName);
            throw new InvalidOperationException($"Command authority transition '{lifecycle.Status}' was rejected: {result.Disposition}.");
        }
    }
}
