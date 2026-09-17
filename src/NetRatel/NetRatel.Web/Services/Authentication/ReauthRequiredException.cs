namespace NetRatel.Web.Services.Authentication;

public sealed class ReauthRequiredException : Exception
{
    public ReauthRequiredException(string message) : base(message)
    {
    }
}