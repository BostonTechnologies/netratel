using System.Text.Json.Nodes;

namespace NetRatel.Mcp.Core;

/// <summary>
/// Transport-neutral outbound boundary used by MCP operation handlers. Hosts
/// provide an adapter that owns their authentication and target isolation.
/// </summary>
public interface INetRatelMcpApiClient
{
    Task<JsonNode?> GetAsync(string path, CancellationToken cancellationToken = default);

    Task<JsonNode?> SendAsync(
        HttpMethod method,
        string path,
        JsonNode? body = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Safe API failure metadata for transport-neutral MCP responses.</summary>
public sealed class NetRatelMcpApiException(string code, string message, int statusCode, bool retryable, string? remoteCode = null) : Exception(message)
{
    public string Code { get; } = code;

    public int StatusCode { get; } = statusCode;

    public bool Retryable { get; } = retryable;

    public string? RemoteCode { get; } = remoteCode;
}

/// <summary>Raised by a host adapter when local operation input is invalid.</summary>
public sealed class NetRatelMcpApiValidationException(string message) : Exception(message);
