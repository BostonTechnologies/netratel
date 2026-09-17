namespace NetRatel.Application.Agents;

public sealed class AgentAuthException : Exception
{
    public int StatusCode { get; }
    public string? Code { get; }

    public AgentAuthException(int statusCode, string message, string? code = null) : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }
}
