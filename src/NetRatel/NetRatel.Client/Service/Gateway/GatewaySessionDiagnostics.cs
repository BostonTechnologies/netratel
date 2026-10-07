using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;

namespace NetRatel.Client.Service.Gateway;

/// <summary>Observes headers at the existing gRPC HTTP boundary; never reads a response body.</summary>
internal sealed class GatewayHttpDiagnosticsHandler(GatewaySessionDiagnostics diagnostics, HttpMessageHandler inner)
    : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        diagnostics.Observe(response);
        return response;
    }
}

internal sealed class GatewaySessionDiagnostics(string endpoint, TimeProvider? timeProvider = null)
{
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly string _origin = SanitizedOrigin(endpoint);
    private long? _lastAcknowledgedHeartbeat;
    private long? _failed;
    private HttpResponseMessage? _response;
    private Guid? _serverConnectionId;
    private string? _protocolFailure;
    private string? _failureReason;

    // Generated locally, contains no device identity or credential, and follows one attempt only.
    internal Guid CorrelationId { get; } = Guid.NewGuid();
    internal TimeSpan? RetryAfter { get; private set; }
    internal void Observe(HttpResponseMessage response)
    {
        _response = response;
        if (response.StatusCode is not (System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.ServiceUnavailable))
            return;
        var hint = response.Headers.RetryAfter;
        var delay = hint?.Delta ?? (hint?.Date is { } date ? date - (timeProvider ?? TimeProvider.System).GetUtcNow() : null);
        if (delay > TimeSpan.Zero) RetryAfter = delay;
    }
    internal void Admitted(Guid serverConnectionId) => _serverConnectionId = serverConnectionId;
    internal void AcknowledgeHeartbeat() => _lastAcknowledgedHeartbeat = Stopwatch.GetTimestamp();
    internal void SessionFailed() => _failed ??= Stopwatch.GetTimestamp();
    internal void ProtocolFailure(string reason) => _protocolFailure = reason;
    internal void FailureReason(string reason) => _failureReason = reason;
    internal string AdmissionSummary => $"utc={DateTimeOffset.UtcNow:O}, correlation={CorrelationId:D}, serverConnection={_serverConnectionId?.ToString("D") ?? "unknown"}, origin={_origin}, rpc=presence/connect";

    internal (string Key, string Message) Failure(Exception exception, TimeSpan retryDelay)
    {
        var rpc = exception as RpcException;
        var response = _response;
        var mediaType = AllowlistedMediaType(response?.Content?.Headers.ContentType?.MediaType);
        var grpcResponse = response is not null && response.StatusCode == System.Net.HttpStatusCode.OK &&
            response.Version.Major >= 2 && IsGrpcMediaType(mediaType);
        var reportedGrpcStatus = ReadGrpcStatus(response);
        var genuineDenial = grpcResponse && rpc?.StatusCode is StatusCode.PermissionDenied or StatusCode.Unauthenticated &&
            reportedGrpcStatus == (int)rpc.StatusCode;
        var category = response is not null && !grpcResponse
            ? "http-proxy-origin-rejection"
            : genuineDenial ? "grpc-rejection"
            : _protocolFailure is not null ? "gateway-protocol"
            : rpc?.StatusCode is StatusCode.PermissionDenied or StatusCode.Unauthenticated ? "grpc-status-unverified"
            : "transport-or-stream";
        var edge = IsCloudflareResponse(response) ? "cloudflare" : "unknown";
        var httpStatus = response is null ? "unknown" : ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
        var protocol = response is null ? "unknown" : $"HTTP/{response.Version}";
        var grpcStatus = rpc?.StatusCode.ToString() ?? "unknown";
        var transport = TransportCode(exception);
        var observedAt = _failed ?? Stopwatch.GetTimestamp();
        var lastAckAge = _lastAcknowledgedHeartbeat is { } acknowledged
            ? Stopwatch.GetElapsedTime(acknowledged, observedAt).TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "s"
            : "unknown";
        var reason = _protocolFailure ?? _failureReason;
        var key = $"{category}/{httpStatus}/{mediaType}/{grpcStatus}/{transport}/{edge}/{reason}";
        return (key, $"Gateway session failed: category={category}, {AdmissionSummary}, httpStatus={httpStatus}, " +
            $"contentType={mediaType}, protocol={protocol}, grpcStatus={grpcStatus}, transport={transport}, edge={edge}, " +
            $"sessionLifetime={Stopwatch.GetElapsedTime(_started, observedAt).TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}s, " +
            $"lastHeartbeatAckAge={lastAckAge}, resetDirection=unknown, reason={reason ?? "unknown"}. Retrying in {retryDelay.TotalSeconds:0}s.");
    }

    private static int? ReadGrpcStatus(HttpResponseMessage? response)
    {
        if (response is null) return null;
        var headers = response.Headers.TryGetValues("grpc-status", out var values) ? values :
            response.TrailingHeaders.TryGetValues("grpc-status", out values) ? values : null;
        var observedValues = headers?.Take(2).ToArray();
        var value = observedValues is { Length: 1 } ? observedValues[0] : null;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var status) && status is >= 0 and <= 16
            ? status : null;
    }

    private static bool IsCloudflareResponse(HttpResponseMessage? response)
        => response?.Headers.Server.Any(value => string.Equals(value.Product?.Name, "cloudflare", StringComparison.OrdinalIgnoreCase)) == true;

    private static string AllowlistedMediaType(string? value)
        => value is { Length: > 0 and <= 64 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '/' or '-' or '.' or '+')
            ? value.ToLowerInvariant() : "unknown";

    private static bool IsGrpcMediaType(string value)
        => value == "application/grpc" || value.StartsWith("application/grpc+", StringComparison.Ordinal);

    private static string SanitizedOrigin(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return "unknown";
        var origin = new UriBuilder(uri.Scheme, uri.IdnHost, uri.IsDefaultPort ? -1 : uri.Port).Uri.GetLeftPart(UriPartial.Authority);
        return origin.Length <= 320 && origin.All(character => char.IsAsciiLetterOrDigit(character) || character is ':' or '/' or '-' or '.' or '[' or ']')
            ? origin : "unknown";
    }

    private static string TransportCode(Exception exception)
    {
        // DebugException carries the typed transport cause. Never emit its message, data, headers or stack.
        Exception? current = exception is RpcException rpc ? rpc.Status.DebugException ?? exception : exception;
        var fallback = "unknown";
        for (var depth = 0; current is not null && depth < 8; depth++, current = current.InnerException)
        {
            if (current is HttpProtocolException protocol) return $"http2=0x{protocol.ErrorCode:x}";
            if (current is SocketException socket) return $"socket={socket.SocketErrorCode}";
            if (current is AuthenticationException) return "tls";
            if (current is HttpRequestException http && http.HttpRequestError != HttpRequestError.Unknown)
            {
                // Prefer a more specific typed reset/TLS cause when available.
                if (http.InnerException is not null)
                {
                    fallback = $"http={http.HttpRequestError}";
                    continue;
                }
                return $"http={http.HttpRequestError}";
            }
            if (current is OperationCanceledException) return "canceled";
        }
        return exception is OperationCanceledException ? "canceled" : fallback;
    }
}
