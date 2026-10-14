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

    internal static bool RequiresAttention(Exception exception) => HasAuthenticationEvidence(exception) || (exception switch
    {
        AgentClientAuthException auth => !auth.IsRecoverable,
        AuthenticationException => true,
        HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => true,
        // Unknown does not identify an authentication failure. Only typed causes
        // can justify the attention floor when a transport supplies that status.
        RpcException rpc => rpc.StatusCode is not (StatusCode.Unknown or StatusCode.Unavailable or StatusCode.Internal or
            StatusCode.DeadlineExceeded or StatusCode.ResourceExhausted or StatusCode.Aborted or
            StatusCode.Cancelled or StatusCode.Unauthenticated or StatusCode.DataLoss),
        _ => false
    });

    private static bool HasAuthenticationEvidence(Exception exception)
    {
        Exception? current = exception;
        for (var depth = 0; current is not null && depth < 8; depth++,
             current = current is RpcException rpc ? rpc.Status.DebugException ?? current.InnerException : current.InnerException)
            if (current is AuthenticationException or HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } or
                AgentClientAuthException { IsRecoverable: false } or RpcException { StatusCode: StatusCode.PermissionDenied })
                return true;
        return false;
    }
}
