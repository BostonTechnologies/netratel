using System.Text.Json.Nodes;
using NetRatel.AgentClient;
using NetRatel.Mcp.Core;

namespace NetRatel.Mcp.Http;

/// <summary>
/// Adapts the isolated HTTP-host app-token client to the shared operational
/// handler contract without exposing inbound OAuth credentials to the API.
/// </summary>
internal sealed class NetRatelMcpHttpApiClient(INetRatelMcpOutboundClient client) : INetRatelMcpApiClient
{
    public Task<JsonNode?> GetAsync(string path, CancellationToken cancellationToken = default)
        => InvokeAsync(() => client.GetAsync(path, cancellationToken));

    public Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body = null, CancellationToken cancellationToken = default)
        => InvokeAsync(() => client.SendAsync(method, path, body, cancellationToken));

    private static async Task<JsonNode?> InvokeAsync(Func<Task<JsonNode?>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (AgentClientRemoteException exception)
        {
            throw new NetRatelMcpApiException(
                exception.Code,
                exception.Message,
                exception.StatusCode,
                exception.StatusCode >= StatusCodes.Status500InternalServerError,
                exception.RemoteCode);
        }
        catch (AgentClientValidationException exception)
        {
            throw new NetRatelMcpApiValidationException(exception.Message);
        }
    }
}
