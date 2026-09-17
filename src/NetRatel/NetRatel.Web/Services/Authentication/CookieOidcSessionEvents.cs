using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace NetRatel.Web.Services.Authentication;

/// <summary>
/// Renews an OIDC-backed cookie before a request reaches protected application code.
/// This keeps token rotation inside the encrypted authentication ticket instead of a
/// Blazor circuit-local cache.
/// </summary>
public sealed class CookieOidcSessionEvents(
    ITokenService tokenService,
    ILogger<CookieOidcSessionEvents> logger) : CookieAuthenticationEvents
{
    private readonly ITokenService _tokenService = tokenService;
    private readonly ILogger<CookieOidcSessionEvents> _logger = logger;

    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        if (context.Principal?.Identity?.IsAuthenticated != true)
        {
            return;
        }

        var authentication = AuthenticateResult.Success(new AuthenticationTicket(
            context.Principal,
            context.Properties,
            context.Scheme.Name));

        if (await _tokenService.TryRefreshSessionAsync(
                context.HttpContext,
                authentication,
                context.HttpContext.RequestAborted).ConfigureAwait(false))
        {
            if (context.HttpContext.Items.ContainsKey(TokenService.SessionRefreshedItemKey))
            {
                context.ShouldRenew = true;
            }

            return;
        }

        _logger.LogWarning(
            "Rejecting expired or invalid OIDC session for {Path}. CookieExpiresUtc={CookieExpiresUtc}",
            context.HttpContext.Request.Path,
            context.Properties.ExpiresUtc);
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
    }

    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    }

    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        context.Response.Redirect("/login");
        return Task.CompletedTask;
    }
}
