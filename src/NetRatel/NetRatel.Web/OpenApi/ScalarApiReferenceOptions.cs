using Microsoft.AspNetCore.Http;

namespace NetRatel.Web.OpenApi;

public static class ScalarApiReferenceOptions
{
    public static string BuildPublicServerUrl(HttpRequest request, Uri? publicOrigin = null)
    {
        if (publicOrigin is not null) return publicOrigin.AbsoluteUri.TrimEnd('/');
        // Relative URLs keep the development/docs experience on the current browser origin
        // without publishing an origin derived from an untrusted Host or forwarded header.
        return request.PathBase.HasValue ? request.PathBase.Value!.TrimEnd('/') : "/";
    }
}
