using Microsoft.AspNetCore.Http;

namespace NetRatel.Web.OpenApi;

public static class ScalarApiReferenceOptions
{
    public static string BuildPublicServerUrl(HttpRequest request)
    {
        var pathBase = request.PathBase.HasValue ? request.PathBase.Value : string.Empty;
        return $"{request.Scheme}://{request.Host}{pathBase}".TrimEnd('/');
    }
}
