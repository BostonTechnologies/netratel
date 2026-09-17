using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;

namespace NetRatel.Web.Services.Authentication;

public interface ITokenService
{
    /// <summary>
    /// Returns a valid access token for the current signed-in user or throws a ReauthRequiredException.
    /// </summary>
    Task<string> GetValidAccessTokenAsync();

    /// <summary>
    /// Validates the current authentication ticket and renews its OIDC token state when required.
    /// </summary>
    Task<bool> TryRefreshSessionAsync(
        HttpContext context,
        AuthenticateResult authentication,
        CancellationToken cancellationToken = default) => Task.FromResult(false);
}
