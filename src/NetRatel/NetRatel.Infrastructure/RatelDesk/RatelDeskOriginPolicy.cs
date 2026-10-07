using Microsoft.Extensions.Configuration;
using NetRatel.Application.RatelDesk;

namespace NetRatel.Infrastructure.RatelDesk;

/// <summary>Exact operator-configured HTTPS origins. Unknown origins, path prefixes and URL credentials are denied.</summary>
public sealed class RatelDeskOriginPolicy(IConfiguration configuration) : IRatelDeskOriginPolicy
{
    public bool TryValidate(string origin, out Uri? normalizedOrigin)
    {
        normalizedOrigin = Normalize(origin);
        if (normalizedOrigin is null) return false;
        var candidate = normalizedOrigin;
        return configuration.GetSection("RatelDesk:AllowedOrigins").GetChildren()
            .Select(item => Normalize(item.Value)).Any(allowed => allowed == candidate);
    }

    private static Uri? Normalize(string? origin)
    {
        if (origin is null || origin.Length > 2_048 || !Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.AbsolutePath != "/" || uri.Host.Length == 0 || uri.HostNameType == UriHostNameType.Unknown)
            return null;
        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
    }
}
