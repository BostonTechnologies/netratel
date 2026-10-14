using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Yarp.ReverseProxy.Model;

namespace NetRatel.Web.Configuration;

public static class ApiRouteGuard
{
    public static IApplicationBuilder UseApiRouteGuard(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path.Value;
            if (string.Equals(path, "/api", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, "/api/", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.Redirect("/api/docs/");
                return;
            }

            // The effective proxy endpoints are the allowlist. Using their metadata also
            // respects custom routes, removals and file reloads without a second path list.
            var guarded = ctx.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) ||
                ctx.Request.Path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase) ||
                ctx.Request.Path.StartsWithSegments("/.well-known/oauth-protected-resource/mcp", StringComparison.OrdinalIgnoreCase);
            if (guarded && ctx.GetEndpoint()?.Metadata.GetMetadata<RouteModel>() is null &&
                !ctx.Request.Path.StartsWithSegments("/api/docs", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await next();
        });
}
