using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using NetRatel.Shared.Operations;

namespace NetRatel.Mcp.Http;

/// <summary>
/// Safe classification for the paired local-credential exchange. It carries
/// no upstream response content, credential value, or transport exception.
/// </summary>
internal sealed record McpLocalCredentialExchangeFailure(
    string Code,
    HttpStatusCode StatusCode,
    int? RetryAfterSeconds = null)
{
    public string? CorrelationId { get; init; }

    public object ToPayload(string? fallbackCorrelationId = null) => new
    {
        success = false,
        summary = "Local HTTP MCP delegation was not admitted.",
        code = Code,
        failure = McpDelegationFailure.ForCode(Code),
        retryAfterSeconds = RetryAfterSeconds,
        correlationId = CorrelationId ?? fallbackCorrelationId
    };

    public static async Task<McpLocalCredentialExchangeFailure> FromResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var fallback = FromResponse(response);
        // Only the authenticated peer's bounded code and correlation are read.
        // Messages, remediation, ownership and arbitrary response fields are never reflected.
        if (response.StatusCode is not (HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
            return fallback;
        var payload = await McpLocalCredentialExchangeResponse.ReadJsonAsync<SafeExchangeFailure>(response.Content, cancellationToken).ConfigureAwait(false);
        if (payload?.Code is not { } code || McpDelegationFailure.ForCode(code) is not { Retryable: false })
            return fallback;
        var correlation = payload.CorrelationId is { Length: > 0 and <= 128 } value &&
            value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':') ? value : null;
        return new(code, response.StatusCode) { CorrelationId = correlation };
    }

    private sealed record SafeExchangeFailure(string? Code, string? CorrelationId);

    public static McpLocalCredentialExchangeFailure FromResponse(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new("local_credential_invalid", HttpStatusCode.Unauthorized),
            HttpStatusCode.Forbidden => new("local_credential_forbidden", HttpStatusCode.Forbidden),
            HttpStatusCode.TooManyRequests => new("local_credential_throttled", HttpStatusCode.TooManyRequests, BoundedRetryAfterSeconds(response)),
            >= HttpStatusCode.InternalServerError => DependencyUnavailable,
            _ => ProtocolError
        };
    }

    public static McpLocalCredentialExchangeFailure DependencyUnavailable { get; } =
        new("local_credential_exchange_unavailable", HttpStatusCode.ServiceUnavailable);

    public static McpLocalCredentialExchangeFailure TimedOut { get; } =
        new("local_credential_exchange_timed_out", HttpStatusCode.GatewayTimeout);

    public static McpLocalCredentialExchangeFailure ProtocolError { get; } =
        new("local_credential_exchange_protocol_error", HttpStatusCode.BadGateway);

    private static int? BoundedRetryAfterSeconds(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        var delay = retryAfter?.Delta ?? (retryAfter?.Date - DateTimeOffset.UtcNow);
        return delay is { } value && value > TimeSpan.Zero
            ? (int)Math.Clamp(Math.Ceiling(value.TotalSeconds), 1, 60)
            : null;
    }
}

/// <summary>Non-secret control flow for a local exchange outcome that is not an invalid credential.</summary>
internal sealed class McpLocalCredentialExchangeException(McpLocalCredentialExchangeFailure failure)
    : Exception("Local HTTP MCP credential exchange did not complete.")
{
    public McpLocalCredentialExchangeFailure Failure { get; } = failure;
}

/// <summary>Reads a paired-exchange payload without allowing a peer to allocate an unbounded response body.</summary>
internal static class McpLocalCredentialExchangeResponse
{
    public const int MaximumResponseBytes = 16 * 1024;

    public static async Task<T?> ReadJsonAsync<T>(HttpContent content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Headers.ContentLength is > MaximumResponseBytes)
            return default;

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var buffer = new MemoryStream(content.Headers.ContentLength is { } contentLength
            ? checked((int)contentLength)
            : 0);
        var chunk = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (buffer.Length > MaximumResponseBytes - read)
                return default;
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return JsonSerializer.Deserialize<T>(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException)
        {
            return default;
        }
    }
}

/// <summary>
/// Converts a local authentication failure into its safe HTTP response after
/// authorization has selected the challenge path. It runs only in local
/// credential mode and never changes normal OAuth authentication responses.
/// </summary>
public static class McpLocalCredentialExchangeFailureResponses
{
    public static async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (McpLocalCredentialExchangeException exception) when (!context.Response.HasStarted)
        {
            var failure = exception.Failure;
            context.Response.Clear();
            context.Response.StatusCode = (int)failure.StatusCode;
            if (failure.RetryAfterSeconds is { } retryAfterSeconds)
                context.Response.Headers.RetryAfter = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await context.Response.WriteAsJsonAsync(failure.ToPayload(context.TraceIdentifier), context.RequestAborted).ConfigureAwait(false);
        }
    }
}
