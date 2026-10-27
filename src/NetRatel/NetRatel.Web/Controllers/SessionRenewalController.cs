using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NetRatel.Web.Services.Authentication;

namespace NetRatel.Web.Controllers;

[AllowAnonymous]
[Route("auth/session")]
public sealed class SessionRenewalController(ITokenService tokens, BrowserSessionRenewalBroker broker) : Controller
{
    [HttpPost("renew")]
    public async Task<IActionResult> Renew(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        var id = Request.Headers["X-NetRatel-Session-Renewal"].ToString();
        if (id.Length != 64 || id.Any(character => !Uri.IsHexDigit(character)))
        {
            return BadRequest();
        }

        // Authenticate explicitly to return a bounded 401 instead of following
        // an OIDC challenge inside fetch. Cookie validation persists rotation
        // before this response is sent; no tokens are returned to browser JS.
        var authentication = await HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (!authentication.Succeeded || authentication.Principal?.Identity?.IsAuthenticated != true)
        {
            return Unauthorized();
        }

        try
        {
            var accessToken = await tokens.GetValidAccessTokenAsync(cancellationToken);
            var expiresAt = OperatorApiCredentialState.ReadBearerExpiration(accessToken);
            return expiresAt is { } expiration
                   && broker.Complete(id, authentication.Principal, accessToken, expiration)
                ? NoContent()
                : Unauthorized();
        }
        catch (ReauthRequiredException)
        {
            return Unauthorized();
        }
    }
}
