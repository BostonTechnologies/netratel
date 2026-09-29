using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace NetRatel.Web.Services.Authentication;

/// <summary>Resolves the caller credential from the current request or its server-side circuit scope.</summary>
public sealed class OperatorApiCredentialProvider(
    IHttpContextAccessor httpContextAccessor,
    ITokenService tokenService,
    OperatorApiCredentialState circuitState,
    IConfiguration configuration)
{
    private readonly string _localCookieName = configuration["Authentication:Local:CookieName"]?.Trim() is { Length: > 0 } cookieName
        ? cookieName
        : "NetRatel.Local";

    public async ValueTask<OperatorApiCredential?> GetCurrentCredentialAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = httpContextAccessor.HttpContext;
        if (context is { User.Identity.IsAuthenticated: true })
        {
            return await CaptureAuthenticatedRequestAsync(context, cancellationToken).ConfigureAwait(false);
        }

        var principal = circuitState.GetBoundPrincipal(out _);
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var cached = circuitState.GetCredential(principal, out var expectedGeneration);
        if (cached is not null || OperatorApiCredentialState.IsLocal(principal))
        {
            return cached;
        }

        try
        {
            // This TokenService is resolved from the caller's request/circuit
            // scope, never from the pooled HttpClient handler scope.
            var accessToken = await tokenService.GetValidAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var expiration = OperatorApiCredentialState.ReadBearerExpiration(accessToken);
            if (expiration is null)
            {
                circuitState.ClearCredential(principal, expectedGeneration);
                return null;
            }

            var credential = new OperatorApiCredential(OperatorApiCredentialKind.Bearer, accessToken, expiration);
            return circuitState.SetCredential(principal, credential, expectedGeneration)
                ? circuitState.GetCredential(principal, out _)
                : null;
        }
        catch (ReauthRequiredException)
        {
            circuitState.ClearCredential(principal, expectedGeneration);
            return null;
        }
    }

    public async Task CaptureForCircuitAsync(
        ClaimsPrincipal circuitPrincipal,
        long expectedGeneration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(circuitPrincipal);
        if (!circuitState.IsBoundTo(circuitPrincipal, expectedGeneration))
        {
            return;
        }

        var context = httpContextAccessor.HttpContext;
        if (context?.User.Identity?.IsAuthenticated != true
            || !OperatorApiCredentialState.SamePrincipal(circuitPrincipal, context.User))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        _ = await CaptureAuthenticatedRequestAsync(context, cancellationToken, expectedGeneration).ConfigureAwait(false);
    }

    private async Task<OperatorApiCredential?> CaptureAuthenticatedRequestAsync(
        HttpContext context,
        CancellationToken cancellationToken,
        long? requiredGeneration = null)
    {
        var principal = context.User;
        if (principal.Identity?.IsAuthenticated != true
            || !circuitState.BindPrincipalForRequest(principal, out var expectedGeneration)
            || requiredGeneration is { } required && required != expectedGeneration)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        OperatorApiCredential? credential;
        if (OperatorApiCredentialState.IsLocal(principal))
        {
            var cookie = context.Request.Cookies[_localCookieName];
            credential = string.IsNullOrWhiteSpace(cookie)
                ? null
                : new OperatorApiCredential(OperatorApiCredentialKind.LocalSessionCookie, cookie, CookieName: _localCookieName);
        }
        else
        {
            try
            {
                var accessToken = await tokenService.GetValidAccessTokenAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var expiration = OperatorApiCredentialState.ReadBearerExpiration(accessToken);
                credential = expiration is null
                    ? null
                    : new OperatorApiCredential(OperatorApiCredentialKind.Bearer, accessToken, expiration);
            }
            catch (ReauthRequiredException)
            {
                credential = null;
            }
        }

        if (credential is null)
        {
            circuitState.ClearCredential(principal, expectedGeneration);
            return null;
        }

        return circuitState.SetCredential(principal, credential, expectedGeneration)
            ? circuitState.GetCredential(principal, out _)
            : null;
    }
}
