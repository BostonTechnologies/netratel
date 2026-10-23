using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;

namespace NetRatel.Infrastructure.Netclaw;

/// <summary>Classifies transport failures without returning exception messages, URLs or credentials.</summary>
internal static class NetclawConnectionFailure
{
    internal static string Classify(Exception exception)
    {
        // SignalR can aggregate several transport failures. Inspect a bounded set of
        // typed causes so a wrapping HTTP failure does not hide DNS/TLS/socket evidence.
        List<Exception> causes = [exception];
        string? fallback = null;
        for (var index = 0; index < causes.Count; index++)
        {
            var cause = causes[index];
            var code = cause switch
            {
                CryptographicException => "device-token-unavailable",
                HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => "daemon-authentication-rejected",
                HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed } => "daemon-session-endpoint-unavailable",
                HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } => "daemon-name-resolution-failed",
                HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } or AuthenticationException => "daemon-tls-verification-failed",
                SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain } => "daemon-name-resolution-failed",
                SocketException { SocketErrorCode: SocketError.ConnectionRefused } => "daemon-connection-refused",
                SocketException { SocketErrorCode: SocketError.TimedOut } or TimeoutException or OperationCanceledException => "session-test-timeout",
                SocketException { SocketErrorCode: SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.NetworkDown or SocketError.HostDown } => "daemon-network-unreachable",
                ArgumentException => "daemon-endpoint-rejected",
                HubException or InvalidDataException or JsonException => "daemon-session-protocol-rejected",
                _ => null
            };
            if (code is not null) return code;
            if (cause is HttpRequestException { StatusCode: not null }) fallback ??= "daemon-http-request-failed";
            else if (cause is HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError }) fallback ??= "daemon-network-unreachable";

            if (cause is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    if (causes.Count == 16) break;
                    causes.Add(inner);
                }
            }
            else if (cause.InnerException is { } inner && causes.Count < 16) causes.Add(inner);
        }
        return fallback ?? "authenticated-session-unavailable";
    }
}
