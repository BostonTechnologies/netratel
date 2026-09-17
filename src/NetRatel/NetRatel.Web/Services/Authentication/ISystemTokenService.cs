namespace NetRatel.Web.Services.Authentication;

public interface ISystemTokenService
{
    /// <summary>
    /// Returns the cached system token (fetches/renews when needed).
    /// </summary>
    Task<string> GetTokenAsync();
}
