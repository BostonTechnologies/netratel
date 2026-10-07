using System;
using System.IO;
using System.Net.Http;
using System.Security.Authentication;
using Grpc.Core;
using NetRatel.Application.ClientAuth;

namespace NetRatel.Client.Service.Auth;

/// <summary>Expected availability/authorization failures keep the owner live; programming faults escape.</summary>
internal static class OperationalRecoveryFailure
{
    internal static bool IsExpected(Exception exception) => exception is AgentClientAuthException or RpcException or
        HttpRequestException or IOException or OperationCanceledException or TimeoutException or AuthenticationException;

    internal static bool RequiresAttention(Exception exception) => HasTrustFailure(exception) || (exception switch
    {
        AgentClientAuthException auth => !auth.IsRecoverable,
        AuthenticationException => true,
        HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => true,
        RpcException rpc => rpc.StatusCode is not (StatusCode.Unavailable or StatusCode.Internal or
            StatusCode.DeadlineExceeded or StatusCode.ResourceExhausted or StatusCode.Aborted or
            StatusCode.Cancelled or StatusCode.Unauthenticated),
        _ => false
    });

    private static bool HasTrustFailure(Exception exception)
    {
        Exception? current = exception is RpcException rpc ? rpc.Status.DebugException ?? exception : exception;
        for (var depth = 0; current is not null && depth < 8; depth++, current = current.InnerException)
            if (current is AuthenticationException or HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError })
                return true;
        return false;
    }
}
