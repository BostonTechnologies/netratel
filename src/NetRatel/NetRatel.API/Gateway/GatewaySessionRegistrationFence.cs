namespace NetRatel.API.Gateway;

/// <summary>
/// Classifies a candidate child-gateway registration against the registration
/// that currently owns the same authenticated presence client. The connection
/// tuple is immutable: an exact reconnect is permitted, a newer presence
/// epoch supersedes the current session, and every other candidate is stale.
/// </summary>
internal static class GatewaySessionRegistrationFence
{
    public static bool CanReplace(
        Guid candidateConnectionId,
        ulong candidateConnectionEpoch,
        Guid currentConnectionId,
        ulong currentConnectionEpoch) =>
        candidateConnectionEpoch > currentConnectionEpoch ||
        (candidateConnectionEpoch == currentConnectionEpoch &&
         candidateConnectionId == currentConnectionId);
}

/// <summary>
/// Raised when a child gateway attempts to register behind the active
/// authenticated presence fence. It intentionally contains no connection or
/// tenant details so it is safe to return as a bounded gRPC failure.
/// </summary>
public sealed class AgentGatewayRegistrationFencedException()
    : InvalidOperationException("The gateway registration is fenced by the active presence connection.");
