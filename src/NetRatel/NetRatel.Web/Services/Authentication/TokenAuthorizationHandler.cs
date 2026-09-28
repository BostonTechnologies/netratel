using System.Net;
using System.Net.Http.Headers;

namespace NetRatel.Web.Services.Authentication;

/// <summary>Projects the request-scoped credential onto an API request for API-side validation.</summary>
public sealed class TokenAuthorizationHandler(ILogger<TokenAuthorizationHandler> logger) : DelegatingHandler
{
    private static readonly TimeSpan TokenRefreshBuffer = TimeSpan.FromMinutes(5);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            if (!request.Options.TryGetValue(OperatorApiCredential.RequestOptionsKey, out var credential))
            {
                throw new ReauthRequiredException("No caller credential is available for this API request.");
            }

            if (credential.Kind == OperatorApiCredentialKind.LocalSessionCookie)
            {
                if (string.IsNullOrWhiteSpace(credential.Value))
                {
                    throw new ReauthRequiredException("The local operator session is not available for this API request.");
                }

                // The API validates the protected LocalSession ticket and current account state on every call.
                request.Headers.Authorization = null;
                request.Headers.Remove("Cookie");
                request.Headers.Remove("X-NetRatel-Account-Request");
                var cookieName = string.IsNullOrWhiteSpace(credential.CookieName) ? "NetRatel.Local" : credential.CookieName;
                request.Headers.TryAddWithoutValidation("Cookie", $"{cookieName}={credential.Value}");
                request.Headers.TryAddWithoutValidation("X-NetRatel-Account-Request", "1");
            }
            else if (credential.Kind == OperatorApiCredentialKind.Bearer
                     && credential.ExpiresAtUtc is { } expiresAt
                     && expiresAt > DateTimeOffset.UtcNow.Add(TokenRefreshBuffer)
                     && !string.IsNullOrWhiteSpace(credential.Value))
            {
                request.Headers.Remove("Cookie");
                request.Headers.Remove("X-NetRatel-Account-Request");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Value);
            }
            else
            {
                throw new ReauthRequiredException("The operator API credential is missing or no longer valid.");
            }

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException exception)
        {
            logger.LogWarning(exception, "Timed out calling operator API method {Method}.", request.Method);
            return new HttpResponseMessage(HttpStatusCode.GatewayTimeout)
            {
                RequestMessage = request,
                ReasonPhrase = "Upstream API timeout"
            };
        }
    }
}
