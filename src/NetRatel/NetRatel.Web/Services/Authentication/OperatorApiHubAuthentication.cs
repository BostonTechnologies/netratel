using System.Net;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;

namespace NetRatel.Web.Services.Authentication;

/// <summary>Supplies a fresh circuit-owned credential for each SignalR connect or reconnect.</summary>
public static class OperatorApiHubAuthentication
{
    public static void Configure(
        HttpConnectionOptions options,
        Uri endpoint,
        OperatorApiCredentialProvider credentials)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(credentials);

        var cookies = new CookieContainer();
        options.Cookies = cookies;
        options.AccessTokenProvider = async () =>
        {
            var credential = await credentials.GetCurrentCredentialAsync().ConfigureAwait(false);
            ClearCookies(cookies, endpoint);
            if (credential?.Kind == OperatorApiCredentialKind.LocalSessionCookie)
            {
                var cookieName = string.IsNullOrWhiteSpace(credential.CookieName) ? "NetRatel.Local" : credential.CookieName;
                cookies.Add(endpoint, new Cookie(cookieName, credential.Value, "/")
                {
                    Secure = string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                });
                return null;
            }

            return credential?.Kind == OperatorApiCredentialKind.Bearer ? credential.Value : null;
        };
    }

    private static void ClearCookies(CookieContainer cookies, Uri endpoint)
    {
        foreach (Cookie cookie in cookies.GetCookies(endpoint))
        {
            cookies.Add(endpoint, new Cookie(cookie.Name, string.Empty, cookie.Path)
            {
                Expires = DateTime.UtcNow.AddDays(-1)
            });
        }
    }
}
