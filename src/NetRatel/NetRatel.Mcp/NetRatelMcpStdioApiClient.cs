using System.Text.Json.Nodes;
using NetRatel.AgentClient;
using NetRatel.Mcp.Core;

namespace NetRatel.Mcp;

/// <summary>
/// Adapts the existing stdio AgentClient to the transport-neutral Core
/// outbound boundary. The stdio host keeps its established configuration
/// precedence while Core handlers receive the same safe exception contract as
/// the HTTP host.
/// </summary>
internal sealed class NetRatelMcpStdioApiClient(INetRatelAgentClient client) : INetRatelMcpApiClient
{
    public Task<JsonNode?> GetAsync(string path, CancellationToken cancellationToken = default)
        => InvokeAsync(() => client.GetAsync(path, authenticated: true, cancellationToken));

    public Task<JsonNode?> SendAsync(
        HttpMethod method,
        string path,
        JsonNode? body = null,
        CancellationToken cancellationToken = default)
        => InvokeAsync(() => client.SendAsync(method, path, body, authenticated: true, cancellationToken));

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
                exception.StatusCode >= 500,
                exception.RemoteCode);
        }
        catch (AgentClientValidationException exception)
        {
            throw new NetRatelMcpApiValidationException(exception.Message);
        }
    }
}
