namespace NetRatel.Application.ClientAuth;

public sealed class AgentClientAuthException : Exception
{
    public int? StatusCode { get; }
    public string? Code { get; }
    public bool ShouldClearCredentials { get; }
    public AgentAuthEndpointRole EndpointRole { get; }
    public AgentAuthFailureKind FailureKind { get; }
    public TimeSpan? RetryAfter { get; }
    public bool RetryAfterWasCapped { get; }

    public bool IsRecoverable => FailureKind is AgentAuthFailureKind.Transport or AgentAuthFailureKind.Timeout or AgentAuthFailureKind.LocalContention ||
        FailureKind == AgentAuthFailureKind.Http && (StatusCode is 408 or 429 || StatusCode >= 500);

    public AgentClientAuthException(string message, int? statusCode = null, bool shouldClearCredentials = false, string? code = null,
        AgentAuthEndpointRole endpointRole = AgentAuthEndpointRole.Unknown,
        AgentAuthFailureKind failureKind = AgentAuthFailureKind.Http, TimeSpan? retryAfter = null,
        bool retryAfterWasCapped = false, Exception? innerException = null) : base(message, innerException)
    {
        StatusCode = statusCode;
        ShouldClearCredentials = shouldClearCredentials;
        Code = code;
        EndpointRole = endpointRole;
        FailureKind = failureKind;
        RetryAfter = retryAfter;
        RetryAfterWasCapped = retryAfterWasCapped;
    }
}

public enum AgentAuthEndpointRole { Unknown, Token, Capabilities, Enrollment }
public enum AgentAuthFailureKind { Http, Transport, Timeout, Protocol, Trust, LocalConfiguration, LocalContention }
