namespace NetRatel.Application.ClientAuth;

public sealed class AgentClientAuthException : Exception
{
    public int? StatusCode { get; }
    public string? Code { get; }
    public bool ShouldClearCredentials { get; }

    public AgentClientAuthException(string message, int? statusCode = null, bool shouldClearCredentials = false, string? code = null) : base(message)
    {
        StatusCode = statusCode;
        ShouldClearCredentials = shouldClearCredentials;
        Code = code;
    }
}
