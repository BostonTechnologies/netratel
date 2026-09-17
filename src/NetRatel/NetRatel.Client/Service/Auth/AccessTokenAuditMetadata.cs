using System;
using System.Linq;
using System.IdentityModel.Tokens.Jwt;

namespace NetRatel.Client.Service.Auth;

internal static class AccessTokenAuditMetadata
{
    private const string Unavailable = "unavailable";

    public static string Describe(string? accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return Unavailable;
        }

        try
        {
            var token = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
            var audiences = string.Join(",", token.Audiences
                .Where(static audience => !string.IsNullOrWhiteSpace(audience))
                .OrderBy(static audience => audience, StringComparer.Ordinal));
            var keyId = token.Header.TryGetValue("kid", out var value) ? value?.ToString() : null;

            return $"issuer={Sanitize(token.Issuer)}, audience={Sanitize(audiences)}, keyId={Sanitize(keyId)}";
        }
        catch (ArgumentException)
        {
            return Unavailable;
        }
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Unavailable;
        }

        return value.Trim().Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal);
    }
}
